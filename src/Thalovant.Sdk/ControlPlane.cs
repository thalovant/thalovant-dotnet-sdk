using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Thalovant
{
    public static class ThalovantDefaults
    {
        public const string ControlApiUrl = "https://api.thalovant.com";

        /// <summary>
        /// The default user agent, <c>ThalovantDotNetSDK/&lt;version&gt;</c>.
        /// </summary>
        /// <remarks>
        /// Derived from <see cref="ThalovantSdkVersion.UserAgent"/> rather than
        /// hard-coded, so it can never drift from the csproj
        /// <c>&lt;Version&gt;</c>. It is therefore <c>static readonly</c> and no
        /// longer a compile-time constant: it cannot be used in <c>const</c>
        /// expressions, <c>switch</c> case labels, or attribute arguments.
        /// </remarks>
        public static readonly string UserAgent = ThalovantSdkVersion.UserAgent;
    }

    /// <summary>
    /// Filters for <c>GET /v1/analytics/overview</c>, the workspace analytics
    /// rollup any authenticated caller can read.
    /// </summary>
    public sealed class AnalyticsOverviewOptions
    {
        public string? Range { get; set; }
        public string? Bucket { get; set; }
        public string? HubId { get; set; }
        public string? ClientId { get; set; }
        public string? Country { get; set; }
        public string? Message { get; set; }
        public string? Utterance { get; set; }
        public string? Intent { get; set; }
        public string? TimeStart { get; set; }
        public string? TimeEnd { get; set; }
        public int? Weekday { get; set; }
        public int? Hour { get; set; }
    }

    /// <summary>Options for provisioning a client identity on a hub.</summary>
    public sealed class CreateClientIdentityOptions
    {
        public string Name { get; }
        public string? SiteId { get; set; }
        public JsonObject? Spec { get; set; }
        public string? OwnerId { get; set; }
        public bool Active { get; set; } = true;
        public IReadOnlyList<HubProtocol>? PreferredProtocols { get; set; }
        public string? IdempotencyKey { get; set; }

        public CreateClientIdentityOptions(string name)
        {
            Name = name;
        }
    }

    /// <summary>
    /// Result of <see cref="ThalovantControlPlane.CreateClientIdentityAsync(string, CreateClientIdentityOptions, CancellationToken)"/>:
    /// the provisioned identity plus the hub and client resources it was derived from.
    /// </summary>
    /// <remarks>
    /// Intentionally a plain <c>sealed class</c> with no <c>ToString()</c> override:
    /// it holds secret-bearing data (the identity credentials plus the raw
    /// <c>client</c> resource with the POST /v1/clients secrets), so its
    /// human-readable form must stay the default type name and never render its
    /// members. Do not convert it to a <c>record</c> — the synthesized
    /// <c>ToString()</c> would print <see cref="Hub"/>/<see cref="Client"/>
    /// (whose <c>JsonObject.ToString()</c> emits the raw JSON) and leak those
    /// secrets.
    /// </remarks>
    public sealed class BootstrapIdentityResult
    {
        public ThalovantIdentity Identity { get; }
        public JsonObject Hub { get; }
        public JsonObject Client { get; }
        public SelectedHubEndpoint? Endpoint { get; }

        public HubProtocol? SelectedProtocol => Endpoint?.Protocol;

        public BootstrapIdentityResult(ThalovantIdentity identity, JsonObject hub, JsonObject client, SelectedHubEndpoint? endpoint)
        {
            Identity = identity;
            Hub = hub;
            Client = client;
            Endpoint = endpoint;
        }

        /// <summary>
        /// Serializes the result. Secrets are gated behind
        /// <paramref name="includeSecrets"/>: the default (<c>false</c>) form
        /// redacts the identity <b>and</b> the secret subkeys — plus any embedded
        /// URL credentials — of the passed-through <c>hub</c>/<c>client</c>
        /// resources (see <see cref="JsonUtil.RedactSecretsInPlace(JsonNode)"/>),
        /// so it is safe to log or persist for display. Only
        /// <c>includeSecrets: true</c> returns the raw credentials; never log that
        /// form.
        /// </summary>
        public JsonObject ToJsonObject(bool includeSecrets = false)
        {
            var hub = JsonUtil.CloneObject(Hub);
            var client = JsonUtil.CloneObject(Client);
            if (!includeSecrets)
            {
                // Redaction affects only this display/serialization copy; the raw
                // Hub/Client properties and the includeSecrets path are untouched.
                JsonUtil.RedactSecretsInPlace(hub);
                JsonUtil.RedactSecretsInPlace(client);
            }
            var data = new JsonObject
            {
                ["identity"] = Identity.ToJsonObject(includeSecrets),
                ["hub"] = hub,
                ["client"] = client,
            };
            if (Endpoint is not null)
            {
                data["selectedProtocol"] = Endpoint.Protocol.WireName();
                data["selectedEndpoint"] = Endpoint.Endpoint;
            }
            return data;
        }
    }

    /// <summary>Client for the Thalovant control API (<c>https://api.thalovant.com</c>).</summary>
    public sealed class ThalovantControlPlane
    {
        public string ApiUrl { get; }
        public string? AccessToken { get; set; }
        public string UserAgent { get; }

        private readonly HttpClient _http;

        public ThalovantControlPlane(
            string apiUrl = ThalovantDefaults.ControlApiUrl,
            string? accessToken = null,
            string? userAgent = null,
            HttpClient? httpClient = null)
        {
            ApiUrl = NormalizeControlApiUrl(apiUrl);
            AccessToken = accessToken;
            // Resolved here rather than as a parameter default so that the
            // version is never inlined into a caller's assembly at their
            // compile time.
            UserAgent = userAgent ?? ThalovantDefaults.UserAgent;
            _http = httpClient ?? new HttpClient();
        }

        /// <summary>
        /// Normalizes the control API base URL: trims trailing slashes and a
        /// trailing <c>/v1</c> path segment, and appends exactly one trailing <c>/</c>.
        /// </summary>
        public static string NormalizeControlApiUrl(string apiUrl)
        {
            var raw = apiUrl.Trim();
            var normalized = HubEndpoints.TrimTrailingSlashes(raw.Length == 0 ? ThalovantDefaults.ControlApiUrl : raw);
            if (normalized.EndsWith("/v1", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(0, normalized.Length - 3);
            }
            return HubEndpoints.TrimTrailingSlashes(normalized) + "/";
        }

        // -- Auth ------------------------------------------------------------

        /// <summary>
        /// <c>POST /v1/auth/token</c>. <paramref name="otpCode"/>/<paramref name="recoveryCode"/>
        /// are sent as <c>otp_code</c>/<c>recovery_code</c> only when provided; MFA-enabled
        /// accounts receive HTTP 401 with code <c>mfa_required</c> without one (surfaced
        /// via <see cref="ThalovantApiException.ErrorCode"/>).
        /// </summary>
        public async Task<JsonObject> LoginAsync(
            string email,
            string password,
            string? scope = null,
            string? otpCode = null,
            string? recoveryCode = null,
            CancellationToken cancellationToken = default)
        {
            var body = new JsonObject
            {
                ["email"] = email,
                ["password"] = password,
            };
            if (!string.IsNullOrEmpty(scope))
            {
                body["scope"] = scope;
            }
            if (otpCode is not null)
            {
                body["otp_code"] = otpCode;
            }
            if (recoveryCode is not null)
            {
                body["recovery_code"] = recoveryCode;
            }
            var token = await RequestObjectAsync("POST", "/v1/auth/token", body, auth: false, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var accessToken = JsonUtil.GetString(token["access_token"]);
            if (string.IsNullOrEmpty(accessToken))
            {
                throw new ThalovantApiException("Thalovant API token response did not include access_token.");
            }
            AccessToken = accessToken;
            return token;
        }

        /// <summary>Default device-flow polling interval when the API does not send one.</summary>
        internal static readonly TimeSpan DefaultDevicePollInterval = TimeSpan.FromSeconds(5);

        /// <summary>Extra back-off added each time the API answers <c>slow_down</c>.</summary>
        internal static readonly TimeSpan DevicePollSlowDownIncrement = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Signs in through the browser device flow and stores the API token. This is
        /// the sign-in path for accounts without a password (for example Google
        /// sign-in). It requests a device authorization
        /// (<c>POST /v1/auth/device/authorize</c>), tells the user to visit
        /// <c>verification_uri</c> and enter the short <c>user_code</c> (pass
        /// <see cref="DeviceLoginOptions.Prompt"/> to present it yourself), optionally
        /// opens the browser at <c>verification_uri_complete</c>, and polls
        /// <c>POST /v1/auth/device/token</c> until the request is approved, denied,
        /// expired, or <see cref="DeviceLoginOptions.Timeout"/> elapses.
        ///
        /// On approval the returned <c>access_token</c> is a durable scoped API token
        /// and is stored on <see cref="AccessToken"/> exactly like
        /// <see cref="LoginAsync(string, string, string?, string?, string?, CancellationToken)"/>.
        /// Denial throws <see cref="ThalovantDeviceAccessDeniedException"/>, an expired
        /// code throws <see cref="ThalovantDeviceCodeExpiredException"/>, and running
        /// past the timeout throws <see cref="ThalovantTimeoutException"/>.
        /// </summary>
        public async Task<DeviceLoginResult> LoginWithBrowserAsync(
            DeviceLoginOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new DeviceLoginOptions();
            var payload = new JsonObject();
            if (options.Scopes is not null)
            {
                var scopes = new JsonArray();
                foreach (var scope in options.Scopes)
                {
                    scopes.Add(scope);
                }
                payload["scopes"] = scopes;
            }
            if (!string.IsNullOrEmpty(options.ClientName))
            {
                payload["client_name"] = options.ClientName;
            }
            var grant = await RequestObjectAsync("POST", "/v1/auth/device/authorize", payload, auth: false, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var deviceCode = JsonUtil.GetString(grant["device_code"]);
            var userCode = JsonUtil.GetString(grant["user_code"]);
            var verificationUri = JsonUtil.GetString(grant["verification_uri"]);
            if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(verificationUri))
            {
                throw new ThalovantApiException("Thalovant API device authorization response was incomplete.");
            }
            var rawInterval = JsonUtil.GetInt(grant["interval"]);
            var interval = rawInterval is int seconds && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : DefaultDevicePollInterval;
            var authorization = new DeviceAuthorization(
                deviceCode!,
                userCode!,
                verificationUri!,
                JsonUtil.GetString(grant["verification_uri_complete"]),
                JsonUtil.GetInt(grant["expires_in"]),
                interval,
                grant);

            if (options.Prompt is not null)
            {
                options.Prompt(authorization);
            }
            else
            {
                Console.WriteLine($"To sign in, visit {authorization.VerificationUri} and enter the code {authorization.UserCode}");
            }
            if (options.OpenBrowser && !string.IsNullOrEmpty(authorization.VerificationUriComplete))
            {
                if (options.BrowserLauncher is not null)
                {
                    options.BrowserLauncher(authorization.VerificationUriComplete!);
                }
                else
                {
                    TryOpenBrowser(authorization.VerificationUriComplete!);
                }
            }

            var token = await PollDeviceTokenAsync(
                authorization.DeviceCode,
                interval,
                options.Timeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var accessToken = JsonUtil.GetString(token["access_token"]);
            if (string.IsNullOrEmpty(accessToken))
            {
                throw new ThalovantApiException("Thalovant API token response did not include access_token.");
            }
            AccessToken = accessToken;
            return DeviceLoginResult.FromToken(token, accessToken!);
        }

        /// <summary>
        /// Polls <c>POST /v1/auth/device/token</c> until approval or a terminal state.
        /// HTTP 400 <c>authorization_pending</c> keeps polling, <c>slow_down</c> also
        /// adds <see cref="DevicePollSlowDownIncrement"/> to the wait; any other error
        /// is terminal. <paramref name="delay"/> and <paramref name="clock"/> are
        /// injectable so tests can drive the loop without real waiting.
        /// </summary>
        internal async Task<JsonObject> PollDeviceTokenAsync(
            string deviceCode,
            TimeSpan interval,
            TimeSpan timeout,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            Func<TimeSpan>? clock = null,
            CancellationToken cancellationToken = default)
        {
            delay ??= (wait, token) => Task.Delay(wait, token);
            clock ??= MonotonicClock;
            var deadline = clock() + timeout;
            var wait = interval;
            var body = new JsonObject { ["device_code"] = deviceCode };
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (statusCode, text) = await SendRawAsync("POST", "/v1/auth/device/token", body, auth: false, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                JsonObject? parsed;
                try
                {
                    parsed = string.IsNullOrWhiteSpace(text) ? null : JsonUtil.ParseObject(text);
                }
                catch (Exception)
                {
                    parsed = null;
                }
                if (statusCode >= 200 && statusCode < 300)
                {
                    if (parsed is null)
                    {
                        throw new ThalovantApiException("Thalovant API returned an unexpected response shape.");
                    }
                    return parsed;
                }
                var error = statusCode == 400 && parsed is not null ? JsonUtil.GetString(parsed["error"]) : null;
                switch (error)
                {
                    case "authorization_pending":
                        break;
                    case "slow_down":
                        wait += DevicePollSlowDownIncrement;
                        break;
                    case "access_denied":
                        throw new ThalovantDeviceAccessDeniedException(
                            "The device sign-in request was denied in the browser.");
                    case "expired_token":
                        throw new ThalovantDeviceCodeExpiredException(
                            "The device sign-in code expired before it was approved. "
                            + "Call LoginWithBrowserAsync() again to request a new code.");
                    default:
                        throw new ThalovantApiException(
                            FormatRequestFailed(statusCode, text),
                            statusCode,
                            text);
                }
                var remaining = deadline - clock();
                if (remaining <= TimeSpan.Zero)
                {
                    throw new ThalovantTimeoutException("Timed out waiting for the device sign-in to be approved.");
                }
                await delay(wait < remaining ? wait : remaining, cancellationToken).ConfigureAwait(false);
            }
        }

        internal static TimeSpan MonotonicClock()
        {
            return TimeSpan.FromSeconds(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        }

        /// <summary>
        /// Best-effort system browser launch: <c>Process.Start</c> with
        /// <c>UseShellExecute</c> on Windows, <c>open</c> on macOS, and
        /// <c>xdg-open</c> elsewhere. Never throws — the prompt has already shown
        /// the verification URI and user code.
        /// </summary>
        internal static void TryOpenBrowser(string url)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }))
                    {
                    }
                }
                else
                {
                    var opener = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open";
                    using (Process.Start(opener, url))
                    {
                    }
                }
            }
            catch (Exception)
            {
                // Browser availability is best-effort.
            }
        }

        // -- Hubs ------------------------------------------------------------

        public Task<JsonObject> ListHubsAsync(int limit = 100, string? cursor = null, string? ownerId = null, CancellationToken cancellationToken = default)
        {
            var parameters = new List<(string, string)> { ("limit", limit.ToString(CultureInfo.InvariantCulture)) };
            if (!string.IsNullOrEmpty(cursor))
            {
                parameters.Add(("cursor", cursor!));
            }
            if (!string.IsNullOrEmpty(ownerId))
            {
                parameters.Add(("owner_id", ownerId!));
            }
            return RequestObjectAsync("GET", PathWithQuery("/v1/hubs", parameters), cancellationToken: cancellationToken);
        }

        public Task<JsonObject> GetHubAsync(string hubId, CancellationToken cancellationToken = default)
        {
            return RequestObjectAsync("GET", "/v1/hubs/" + Uri.EscapeDataString(hubId), cancellationToken: cancellationToken);
        }

        public Task<JsonObject> ListPublicHubsAsync(int limit = 24, string? cursor = null, CancellationToken cancellationToken = default)
        {
            var parameters = new List<(string, string)> { ("limit", limit.ToString(CultureInfo.InvariantCulture)) };
            if (!string.IsNullOrEmpty(cursor))
            {
                parameters.Add(("cursor", cursor!));
            }
            return RequestObjectAsync("GET", PathWithQuery("/v1/public/hubs", parameters), auth: false, cancellationToken: cancellationToken);
        }

        public Task<JsonObject> GetPublicHubAsync(string hubRef, CancellationToken cancellationToken = default)
        {
            return RequestObjectAsync("GET", "/v1/public/hubs/" + Uri.EscapeDataString(hubRef), auth: false, cancellationToken: cancellationToken);
        }

        // -- Hub provisioning ------------------------------------------------

        /// <summary>
        /// <c>POST /v1/hubs</c>. Creates a hub.
        /// <para>
        /// The request is idempotent: an <c>Idempotency-Key</c> header is generated
        /// unless <see cref="CreateHubOptions.IdempotencyKey"/> supplies one, so a
        /// retried create after a timeout returns the hub the first attempt made
        /// instead of making a second one.
        /// </para>
        /// <para>
        /// Requires a paid plan and a token with the <c>hubs:write</c> scope; a
        /// free-plan token fails with HTTP 402 and a token without the scope with
        /// HTTP 403.
        /// </para>
        /// </summary>
        public Task<JsonObject> CreateHubAsync(CreateHubOptions options, CancellationToken cancellationToken = default)
        {
            var headers = new Dictionary<string, string>
            {
                ["Idempotency-Key"] = options.IdempotencyKey ?? NewIdempotencyKey(),
            };
            return RequestObjectAsync("POST", "/v1/hubs", options.ToJsonObject(), headers, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>PATCH /v1/hubs/{hub_id}</c>. Partially updates a hub.
        /// <para>
        /// The API enforces optimistic locking here, so <paramref name="etag"/> is
        /// required rather than optional: pass the <c>etag</c> from the hub resource
        /// you read and it is sent as <c>If-Match</c>. A stale or missing value fails
        /// with HTTP 412 and changes nothing; re-read the hub with
        /// <see cref="GetHubAsync(string, CancellationToken)"/> and retry with the new
        /// <c>etag</c>.
        /// </para>
        /// <para>
        /// The API treats <c>name</c>, <c>namespace</c>, and <c>domain</c> as
        /// immutable and answers HTTP 400 when one of them is changed, and
        /// <see cref="UpdateHubOptions.IsLocked"/> is admin-only (HTTP 403 otherwise).
        /// </para>
        /// <para>Requires a paid plan and a token with the <c>hubs:write</c> scope.</para>
        /// </summary>
        public Task<JsonObject> UpdateHubAsync(
            string hubId,
            UpdateHubOptions options,
            string etag,
            CancellationToken cancellationToken = default)
        {
            var headers = new Dictionary<string, string> { ["If-Match"] = etag };
            return RequestObjectAsync(
                "PATCH",
                "/v1/hubs/" + Uri.EscapeDataString(hubId),
                options.ToJsonObject(),
                headers,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>DELETE /v1/hubs/{hub_id}</c>. Deletes a hub along with its dependent
        /// clients and ACLs.
        /// <para>
        /// Like <see cref="UpdateHubAsync(string, UpdateHubOptions, string, CancellationToken)"/>
        /// this route requires the hub's current <paramref name="etag"/>, sent as
        /// <c>If-Match</c>; a stale or missing value fails with HTTP 412.
        /// </para>
        /// <para>Requires a paid plan and a token with the <c>hubs:write</c> scope.</para>
        /// </summary>
        public Task DeleteHubAsync(string hubId, string etag, CancellationToken cancellationToken = default)
        {
            var headers = new Dictionary<string, string> { ["If-Match"] = etag };
            return RequestDataAsync(
                "DELETE",
                "/v1/hubs/" + Uri.EscapeDataString(hubId),
                headers: headers,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>POST /v1/hubs/{hub_id}/release</c>. Applies a hub release policy and
        /// returns the updated hub. Every option is optional; omitted fields fall back
        /// to the workspace release policy.
        /// <para>Requires a paid plan and a token with the <c>hubs:write</c> scope.</para>
        /// </summary>
        public Task<JsonObject> ReleaseHubAsync(
            string hubId,
            ReleaseOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new ReleaseOptions();
            return RequestObjectAsync(
                "POST",
                "/v1/hubs/" + Uri.EscapeDataString(hubId) + "/release",
                options.ToJsonObject(),
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>PUT /v1/hubs/{hub_id}/rating</c>. Rates a public hub from 1 to 5 and
        /// returns the updated hub. Only public hubs can be rated, and owners cannot
        /// rate their own hubs.
        /// <para>
        /// Requires a token with the <c>hubs:write</c> scope. Unlike the provisioning
        /// routes, rating is <b>not</b> paid-gated.
        /// </para>
        /// </summary>
        public Task<JsonObject> SetHubRatingAsync(string hubId, int rating, CancellationToken cancellationToken = default)
        {
            var body = new JsonObject { ["rating"] = rating };
            return RequestObjectAsync(
                "PUT",
                "/v1/hubs/" + Uri.EscapeDataString(hubId) + "/rating",
                body,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>DELETE /v1/hubs/{hub_id}/rating</c>. Removes the caller's rating from a
        /// public hub and returns the hub.
        /// <para>
        /// Requires a token with the <c>hubs:write</c> scope; like
        /// <see cref="SetHubRatingAsync(string, int, CancellationToken)"/> it is not
        /// paid-gated.
        /// </para>
        /// </summary>
        public Task<JsonObject> ClearHubRatingAsync(string hubId, CancellationToken cancellationToken = default)
        {
            return RequestObjectAsync(
                "DELETE",
                "/v1/hubs/" + Uri.EscapeDataString(hubId) + "/rating",
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>GET /v1/hubs/{hub_id}/runtime-capabilities</c>. Reads the live skill and
        /// intent inventory a hub runtime exposes.
        /// <para>
        /// Requires a token with the <c>hubs:inspect</c> scope. This is the one
        /// discovery read that fails when nothing is reporting: the API answers HTTP
        /// 409 when the hub has no connected client that can report inventory, where
        /// <see cref="ListRuntimeGroupInventoryAsync(string, bool, CancellationToken)"/>
        /// returns an empty list with a pending source instead.
        /// </para>
        /// </summary>
        public Task<JsonObject> GetHubRuntimeCapabilitiesAsync(string hubId, CancellationToken cancellationToken = default)
        {
            return RequestObjectAsync(
                "GET",
                "/v1/hubs/" + Uri.EscapeDataString(hubId) + "/runtime-capabilities",
                cancellationToken: cancellationToken);
        }

        // -- Runtime groups --------------------------------------------------

        /// <summary>
        /// <c>GET /v1/runtime-groups</c>. Lists the runtime groups visible to the
        /// authenticated user. <paramref name="ownerId"/> is admin-only and is sent
        /// only when non-blank. Requires a token with the <c>hubs:read</c> scope.
        /// </summary>
        public Task<JsonObject> ListRuntimeGroupsAsync(string? ownerId = null, CancellationToken cancellationToken = default)
        {
            var parameters = new List<(string, string)>();
            AppendParameter(parameters, "owner_id", ownerId);
            return RequestObjectAsync("GET", PathWithQuery("/v1/runtime-groups", parameters), cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>GET /v1/runtime-groups/{runtime_group_id}</c>. Requires a token with the
        /// <c>hubs:read</c> scope.
        /// </summary>
        public Task<JsonObject> GetRuntimeGroupAsync(string runtimeGroupId, CancellationToken cancellationToken = default)
        {
            return RequestObjectAsync(
                "GET",
                "/v1/runtime-groups/" + Uri.EscapeDataString(runtimeGroupId),
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>POST /v1/runtime-groups</c>. Creates a runtime group. This route reads no
        /// <c>Idempotency-Key</c>, so no key is sent.
        /// <para>Requires a paid plan and a token with the <c>hubs:write</c> scope.</para>
        /// </summary>
        public Task<JsonObject> CreateRuntimeGroupAsync(CreateRuntimeGroupOptions options, CancellationToken cancellationToken = default)
        {
            return RequestObjectAsync("POST", "/v1/runtime-groups", options.ToJsonObject(), cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>PATCH /v1/runtime-groups/{runtime_group_id}</c>. Updates a runtime group's
        /// name, description, or spec. Unlike the hub update route this one reads no
        /// <c>If-Match</c>, so there is no <c>etag</c> parameter.
        /// <para>Requires a paid plan and a token with the <c>hubs:write</c> scope.</para>
        /// </summary>
        public Task<JsonObject> UpdateRuntimeGroupAsync(
            string runtimeGroupId,
            UpdateRuntimeGroupOptions options,
            CancellationToken cancellationToken = default)
        {
            return RequestObjectAsync(
                "PATCH",
                "/v1/runtime-groups/" + Uri.EscapeDataString(runtimeGroupId),
                options.ToJsonObject(),
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>GET /v1/runtime-groups/{runtime_group_id}/config</c>. Reads a runtime
        /// group's runtime configuration and personas. Requires a token with the
        /// <c>hubs:read</c> scope.
        /// </summary>
        public Task<JsonObject> GetRuntimeGroupConfigAsync(string runtimeGroupId, CancellationToken cancellationToken = default)
        {
            return RequestObjectAsync(
                "GET",
                "/v1/runtime-groups/" + Uri.EscapeDataString(runtimeGroupId) + "/config",
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>PATCH /v1/runtime-groups/{runtime_group_id}/config</c>. Merges runtime
        /// configuration into a runtime group.
        /// <para>
        /// The API merges <paramref name="config"/> into the stored configuration
        /// rather than replacing it, and marks the group pending so the runtime
        /// operator reconciles the change. <paramref name="personas"/> is replaced,
        /// and only when provided.
        /// </para>
        /// <para>Requires a paid plan and a token with the <c>hubs:write</c> scope.</para>
        /// </summary>
        public Task<JsonObject> UpdateRuntimeGroupConfigAsync(
            string runtimeGroupId,
            JsonObject config,
            JsonObject? personas = null,
            CancellationToken cancellationToken = default)
        {
            var body = new JsonObject { ["config"] = JsonUtil.CloneObject(config) };
            if (personas is not null)
            {
                body["personas"] = JsonUtil.CloneObject(personas);
            }
            return RequestObjectAsync(
                "PATCH",
                "/v1/runtime-groups/" + Uri.EscapeDataString(runtimeGroupId) + "/config",
                body,
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>POST /v1/runtime-groups/{runtime_group_id}/release</c>. Applies a runtime
        /// image policy and returns the updated runtime group. Options behave exactly
        /// like <see cref="ReleaseHubAsync(string, ReleaseOptions?, CancellationToken)"/>.
        /// <para>Requires a paid plan and a token with the <c>hubs:write</c> scope.</para>
        /// </summary>
        public Task<JsonObject> ReleaseRuntimeGroupAsync(
            string runtimeGroupId,
            ReleaseOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new ReleaseOptions();
            return RequestObjectAsync(
                "POST",
                "/v1/runtime-groups/" + Uri.EscapeDataString(runtimeGroupId) + "/release",
                options.ToJsonObject(),
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>DELETE /v1/runtime-groups/{runtime_group_id}</c>. The API answers HTTP 409
        /// for the workspace default group and for a group that still has hubs
        /// attached.
        /// <para>Requires a paid plan and a token with the <c>hubs:write</c> scope.</para>
        /// </summary>
        public Task DeleteRuntimeGroupAsync(string runtimeGroupId, CancellationToken cancellationToken = default)
        {
            return RequestDataAsync(
                "DELETE",
                "/v1/runtime-groups/" + Uri.EscapeDataString(runtimeGroupId),
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>POST /v1/runtime-groups/{runtime_group_id}/skills</c>. Installs (or
        /// re-installs) a skill in a runtime group; installing a skill that is already
        /// present updates the existing entry.
        /// <para>
        /// Requires a paid plan and a token with the <c>hubs:write</c> scope. Paid
        /// marketplace skills additionally need marketplace access on the tenant plan.
        /// </para>
        /// </summary>
        public Task<JsonObject> InstallRuntimeGroupSkillAsync(
            string runtimeGroupId,
            InstallRuntimeGroupSkillOptions options,
            CancellationToken cancellationToken = default)
        {
            return RequestObjectAsync(
                "POST",
                "/v1/runtime-groups/" + Uri.EscapeDataString(runtimeGroupId) + "/skills",
                options.ToJsonObject(),
                cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>DELETE /v1/runtime-groups/{runtime_group_id}/skills/{skill_id}</c>.
        /// Removes a skill from a runtime group.
        /// <para>Requires a paid plan and a token with the <c>hubs:write</c> scope.</para>
        /// </summary>
        public Task UninstallRuntimeGroupSkillAsync(
            string runtimeGroupId,
            string skillId,
            CancellationToken cancellationToken = default)
        {
            return RequestDataAsync(
                "DELETE",
                "/v1/runtime-groups/" + Uri.EscapeDataString(runtimeGroupId)
                    + "/skills/" + Uri.EscapeDataString(skillId),
                cancellationToken: cancellationToken);
        }

        // -- Skill discovery -------------------------------------------------

        /// <summary>
        /// <c>GET /v1/marketplace/skills</c>. Lists the marketplace skill catalog
        /// visible to the authenticated user, as <c>{"data": [...]}</c>. Each entry
        /// carries the catalog fields an install needs (<c>skill_id</c>,
        /// <c>source_type</c>, <c>source_ref</c>, <c>config_schema</c>,
        /// <c>secret_schema</c>) alongside presentation and access fields
        /// (<c>category</c>, <c>tags</c>, <c>verified</c>, <c>access_tier</c>).
        /// <para>
        /// Requires a token with the <c>hubs:read</c> scope. Unlike the provisioning
        /// routes this catalog is <b>not</b> paid-gated, so free-tier callers can
        /// browse before upgrading; only the install itself needs a paid plan.
        /// </para>
        /// </summary>
        public Task<JsonObject> ListMarketplaceSkillsAsync(
            MarketplaceSkillListOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new MarketplaceSkillListOptions();
            var parameters = new List<(string, string)>();
            AppendParameter(parameters, "owner_id", options.OwnerId);
            if (options.IncludeInactive)
            {
                parameters.Add(("include_inactive", "true"));
            }
            if (options.ForceRefresh)
            {
                parameters.Add(("force_refresh", "true"));
            }
            return RequestObjectAsync("GET", PathWithQuery("/v1/marketplace/skills", parameters), cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>GET /v1/runtime-groups/{runtime_group_id}/marketplace</c>. Lists the
        /// marketplace catalog resolved against one runtime group — the discovery view
        /// to use before installing, since every entry folds in whether the skill is
        /// desired, whether it was observed running, and the access verdict for the
        /// tenant plan (<c>purchase_required</c>, <c>installable</c>).
        /// <para>
        /// <paramref name="refreshInventory"/> forces a live read from the runtime
        /// operator instead of answering from the cached snapshot. It also decides the
        /// envelope's <c>source</c> when nothing is reporting: the default cached read
        /// answers <c>runtime-group-cache-empty</c>, and only a refreshing read
        /// answers <c>ovos-runtime-operator-pending</c>. Either way <c>data</c> still
        /// carries the catalog entries — this route lists what could be installed, so
        /// it is never empty just because the operator is quiet.
        /// </para>
        /// <para>
        /// Requires a token with the <c>hubs:inspect</c> scope and is not paid-gated.
        /// The API answers HTTP 404 for an unknown group and HTTP 403 when the caller
        /// does not own it, but does not 409 when no client is connected.
        /// </para>
        /// </summary>
        public Task<JsonObject> ListRuntimeGroupMarketplaceAsync(
            string runtimeGroupId,
            bool refreshInventory = false,
            CancellationToken cancellationToken = default)
        {
            var parameters = new List<(string, string)>();
            if (refreshInventory)
            {
                parameters.Add(("refresh_inventory", "true"));
            }
            var path = "/v1/runtime-groups/" + Uri.EscapeDataString(runtimeGroupId) + "/marketplace";
            return RequestObjectAsync("GET", PathWithQuery(path, parameters), cancellationToken: cancellationToken);
        }

        /// <summary>
        /// <c>GET /v1/runtime-groups/{runtime_group_id}/inventory</c>. Lists the skills
        /// a runtime group is actually observed running. The envelope reports
        /// <c>source</c> — one of <c>ovos-runtime-operator</c>,
        /// <c>runtime-group-cache</c>, or <c>ovos-runtime-operator-pending</c> — plus
        /// <c>operator_phase</c> and <c>operator_message</c>.
        /// <para>
        /// <paramref name="refresh"/> forces a live operator read; the API also
        /// refreshes on its own when it holds no cached snapshot. Unlike
        /// <see cref="GetHubRuntimeCapabilitiesAsync(string, CancellationToken)"/> this
        /// route does not answer HTTP 409 when nothing is reporting — it returns an
        /// empty <c>data</c> list with a pending <c>source</c> instead.
        /// </para>
        /// <para>
        /// Requires a token with the <c>hubs:inspect</c> scope; no paid plan is needed.
        /// </para>
        /// </summary>
        public Task<JsonObject> ListRuntimeGroupInventoryAsync(
            string runtimeGroupId,
            bool refresh = false,
            CancellationToken cancellationToken = default)
        {
            var parameters = new List<(string, string)>();
            if (refresh)
            {
                parameters.Add(("refresh", "true"));
            }
            var path = "/v1/runtime-groups/" + Uri.EscapeDataString(runtimeGroupId) + "/inventory";
            return RequestObjectAsync("GET", PathWithQuery(path, parameters), cancellationToken: cancellationToken);
        }

        // -- Operations ------------------------------------------------------

        public async Task<OperationResource> GetOperationAsync(string operationId, CancellationToken cancellationToken = default)
        {
            var body = await RequestDataAsync("GET", "/v1/operations/" + Uri.EscapeDataString(operationId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return DecodeResource<OperationResource>(body);
        }

        // -- Memory ----------------------------------------------------------

        public async Task<MemoryListResponse> ListMemoryItemsAsync(MemoryListOptions? options = null, CancellationToken cancellationToken = default)
        {
            options ??= new MemoryListOptions();
            var parameters = new List<(string, string)>();
            if (options.Scope is MemoryScope scope)
            {
                parameters.Add(("scope", MemoryScopeConverter.WireName(scope)));
            }
            if (options.Kind is MemoryKind kind)
            {
                parameters.Add(("kind", MemoryKindConverter.WireName(kind)));
            }
            AppendParameter(parameters, "owner_id", options.OwnerId);
            AppendParameter(parameters, "hub_id", options.HubId);
            AppendParameter(parameters, "q", options.Query);
            if (options.IncludeDeleted)
            {
                parameters.Add(("include_deleted", "true"));
            }
            if (options.IncludeExpired)
            {
                parameters.Add(("include_expired", "true"));
            }
            if (options.Limit is int limit)
            {
                parameters.Add(("limit", limit.ToString(CultureInfo.InvariantCulture)));
            }
            if (options.Offset is int offset)
            {
                parameters.Add(("offset", offset.ToString(CultureInfo.InvariantCulture)));
            }
            var body = await RequestDataAsync("GET", PathWithQuery("/v1/memory", parameters), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return DecodeResource<MemoryListResponse>(body);
        }

        public async Task<MemorySummaryResponse> GetMemorySummaryAsync(string? ownerId = null, CancellationToken cancellationToken = default)
        {
            var parameters = new List<(string, string)>();
            AppendParameter(parameters, "owner_id", ownerId);
            var body = await RequestDataAsync("GET", PathWithQuery("/v1/memory/summary", parameters), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return DecodeResource<MemorySummaryResponse>(body);
        }

        public async Task<MemoryItemResource> CreateMemoryItemAsync(MemoryCreatePayload payload, CancellationToken cancellationToken = default)
        {
            var body = await RequestDataAsync("POST", "/v1/memory", payload.ToJsonObject(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return DecodeResource<MemoryItemResource>(body);
        }

        public async Task<MemoryItemResource> GetMemoryItemAsync(string memoryId, CancellationToken cancellationToken = default)
        {
            var body = await RequestDataAsync("GET", "/v1/memory/" + Uri.EscapeDataString(memoryId), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return DecodeResource<MemoryItemResource>(body);
        }

        public async Task<MemoryItemResource> UpdateMemoryItemAsync(string memoryId, MemoryUpdatePayload payload, CancellationToken cancellationToken = default)
        {
            var body = await RequestDataAsync("PATCH", "/v1/memory/" + Uri.EscapeDataString(memoryId), payload.ToJsonObject(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return DecodeResource<MemoryItemResource>(body);
        }

        public Task DeleteMemoryItemAsync(string memoryId, CancellationToken cancellationToken = default)
        {
            return RequestDataAsync("DELETE", "/v1/memory/" + Uri.EscapeDataString(memoryId), cancellationToken: cancellationToken);
        }

        // -- Analytics -------------------------------------------------------

        public Task<JsonObject> AnalyticsOverviewAsync(AnalyticsOverviewOptions? options = null, CancellationToken cancellationToken = default)
        {
            options ??= new AnalyticsOverviewOptions();
            var parameters = new List<(string, string)>();
            AppendParameter(parameters, "range", options.Range);
            AppendParameter(parameters, "bucket", options.Bucket);
            AppendParameter(parameters, "hub_id", options.HubId);
            AppendParameter(parameters, "client_id", options.ClientId);
            AppendParameter(parameters, "country", options.Country);
            AppendParameter(parameters, "message", options.Message);
            AppendParameter(parameters, "utterance", options.Utterance);
            AppendParameter(parameters, "intent", options.Intent);
            AppendParameter(parameters, "time_start", options.TimeStart);
            AppendParameter(parameters, "time_end", options.TimeEnd);
            if (options.Weekday is int weekday)
            {
                parameters.Add(("weekday", weekday.ToString(CultureInfo.InvariantCulture)));
            }
            if (options.Hour is int hour)
            {
                parameters.Add(("hour", hour.ToString(CultureInfo.InvariantCulture)));
            }
            return RequestObjectAsync("GET", PathWithQuery("/v1/analytics/overview", parameters), cancellationToken: cancellationToken);
        }

        // -- Clients ---------------------------------------------------------

        public Task<JsonObject> CreateClientAsync(JsonObject payload, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            var headers = new Dictionary<string, string>
            {
                ["Idempotency-Key"] = idempotencyKey ?? NewIdempotencyKey(),
            };
            return RequestObjectAsync("POST", "/v1/clients", payload, headers, cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Provisions a client identity: <c>GET /v1/hubs/{id}</c> followed by
        /// <c>POST /v1/clients</c> with an <c>Idempotency-Key</c> header, parsing the
        /// returned <c>initial_identify</c> credentials.
        /// </summary>
        public async Task<BootstrapIdentityResult> CreateClientIdentityAsync(string hubId, CreateClientIdentityOptions options, CancellationToken cancellationToken = default)
        {
            var hub = await GetHubAsync(hubId, cancellationToken).ConfigureAwait(false);
            return await CreateClientIdentityAsync(hub, options, cancellationToken).ConfigureAwait(false);
        }

        public async Task<BootstrapIdentityResult> CreateClientIdentityAsync(JsonObject hub, CreateClientIdentityOptions options, CancellationToken cancellationToken = default)
        {
            var hubId = JsonUtil.GetString(hub["id"]);
            if (string.IsNullOrEmpty(hubId))
            {
                throw new ThalovantApiException("Hub resource is missing id.");
            }
            var siteId = CleanSiteId(options.SiteId ?? options.Name);
            var apiKey = NewSecret();
            var password = NewSecret();
            var cryptoKey = NewSecret();
            var spec = JsonUtil.CloneObject(options.Spec);
            spec["version"] = JsonUtil.OptionalString(spec["version"]) ?? "1";
            spec["apiKey"] = apiKey;
            spec["password"] = password;
            spec["cryptoKey"] = cryptoKey;
            spec["siteId"] = siteId;
            var payload = new JsonObject
            {
                ["hub_id"] = hubId,
                ["name"] = options.Name,
                ["spec"] = spec,
                ["active"] = options.Active,
            };
            if (options.OwnerId is not null)
            {
                payload["owner_id"] = options.OwnerId;
            }

            var client = await CreateClientAsync(payload, options.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            var protocols = HubProtocolSettings.From(hub);
            var endpoints = HubDataPlaneEndpoints.FromHub(hub);
            var endpoint = HubEndpoints.SelectDataPlaneEndpoint(
                endpoints,
                protocols,
                options.PreferredProtocols ?? HubEndpoints.DefaultProtocolPreference);
            JsonObject identityJson;
            if (JsonUtil.AsObject(client["initial_identify"]) is JsonObject initialIdentify)
            {
                identityJson = JsonUtil.CloneObject(initialIdentify);
            }
            else
            {
                identityJson = new JsonObject
                {
                    ["access_key"] = apiKey,
                    ["password"] = password,
                    ["crypto_key"] = cryptoKey,
                    ["site_id"] = siteId,
                    ["default_master"] = DefaultMaster(hub, endpoints, endpoint),
                    ["default_port"] = 443,
                };
            }
            identityJson["data_plane_endpoints"] = endpoints.ToJsonObject();
            identityJson["protocols"] = protocols.ToJsonObject();
            var identity = new ThalovantIdentity(identityJson);
            return new BootstrapIdentityResult(identity, hub, client, endpoint);
        }

        /// <summary>
        /// Resolves the endpoint the runtime should use, or throws when the hub
        /// does not expose the requested protocol.
        /// </summary>
        public SelectedHubEndpoint RequireRuntimeProtocol(BootstrapIdentityResult result, HubProtocol? hubProtocol = null)
        {
            var selected = hubProtocol ?? result.SelectedProtocol ?? HubEndpoints.DefaultProtocolPreference[0];
            if (selected == HubProtocol.Mqtt && result.Identity.Mqtt is null)
            {
                throw new ThalovantUnsupportedProtocolException(
                    "MQTT is enabled, but the API did not return client-scoped MQTT broker credentials.");
            }
            var endpoint = result.Identity.EndpointFor(selected);
            if (endpoint is null)
            {
                throw new ThalovantUnsupportedProtocolException(
                    $"This hub does not expose a {selected.WireName().ToUpperInvariant()} endpoint for the SDK runtime.");
            }
            return new SelectedHubEndpoint(selected, endpoint);
        }

        // -- Request plumbing ------------------------------------------------

        internal HttpRequestMessage BuildRequest(
            string method,
            string path,
            JsonObject? body = null,
            IReadOnlyDictionary<string, string>? headers = null,
            bool auth = true)
        {
            var trimmedPath = path.TrimStart('/');
            if (!Uri.TryCreate(ApiUrl + trimmedPath, UriKind.Absolute, out var url))
            {
                throw new ThalovantApiException($"Invalid Thalovant API URL: {ApiUrl + trimmedPath}");
            }
            var request = new HttpRequestMessage(new HttpMethod(method), url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }
            if (auth)
            {
                if (string.IsNullOrEmpty(AccessToken))
                {
                    throw new ThalovantApiException("Missing Thalovant API access token.");
                }
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
            }
            if (headers is not null)
            {
                foreach (var header in headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            return request;
        }

        /// <summary>
        /// Sends a request and returns the raw status code and body without
        /// treating non-2xx statuses as errors (the device-token poll decodes
        /// its expected HTTP 400 payloads itself).
        /// </summary>
        internal async Task<(int StatusCode, string Body)> SendRawAsync(
            string method,
            string path,
            JsonObject? body = null,
            IReadOnlyDictionary<string, string>? headers = null,
            bool auth = true,
            CancellationToken cancellationToken = default)
        {
            using var request = BuildRequest(method, path, body, headers, auth);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new ThalovantApiException($"Thalovant API request failed: {exception.Message}");
            }
            using (response)
            {
                var text = response.Content is null
                    ? ""
                    : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ((int)response.StatusCode, text);
            }
        }

        internal async Task<string> RequestDataAsync(
            string method,
            string path,
            JsonObject? body = null,
            IReadOnlyDictionary<string, string>? headers = null,
            bool auth = true,
            CancellationToken cancellationToken = default)
        {
            var (statusCode, text) = await SendRawAsync(method, path, body, headers, auth, cancellationToken)
                .ConfigureAwait(false);
            if (statusCode < 200 || statusCode >= 300)
            {
                throw new ThalovantApiException(
                    FormatRequestFailed(statusCode, text),
                    statusCode,
                    text);
            }
            return text;
        }

        internal async Task<JsonObject> RequestObjectAsync(
            string method,
            string path,
            JsonObject? body = null,
            IReadOnlyDictionary<string, string>? headers = null,
            bool auth = true,
            CancellationToken cancellationToken = default)
        {
            var text = await RequestDataAsync(method, path, body, headers, auth, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new JsonObject();
            }
            try
            {
                return JsonUtil.ParseObject(text);
            }
            catch (Exception)
            {
                throw new ThalovantApiException("Thalovant API returned an unexpected response shape.");
            }
        }

        private static T DecodeResource<T>(string body)
        {
            try
            {
                var decoded = JsonSerializer.Deserialize<T>(body);
                if (decoded is null)
                {
                    throw new JsonException("null resource");
                }
                return decoded;
            }
            catch (JsonException exception)
            {
                throw new ThalovantApiException(
                    $"Thalovant API returned an unexpected response shape: {exception.Message}",
                    body: body);
            }
        }

        // -- Helpers ---------------------------------------------------------

        /// <summary>Maximum length of the server detail echoed into an exception message.</summary>
        internal const int MaxServerDetailLength = 200;

        /// <summary>
        /// Builds the message for a failed control-plane request: the HTTP status
        /// plus, only when present, a known human-readable field of a JSON error
        /// envelope. Arbitrary response-body text is never echoed — so a 4xx that
        /// reflects the request (for example a validation error carrying the
        /// POST /v1/clients <c>apiKey</c>, <c>password</c>, or <c>cryptoKey</c> the
        /// SDK generated) cannot launder those secrets into the message. The full
        /// body stays available on <see cref="ThalovantApiException.Body"/> and
        /// still feeds <see cref="ThalovantApiException.ErrorCode"/>.
        /// </summary>
        internal static string FormatRequestFailed(int statusCode, string? body)
        {
            var detail = SummarizeServerDetail(body);
            return detail.Length == 0
                ? $"Thalovant API request failed with HTTP {statusCode}."
                : $"Thalovant API request failed with HTTP {statusCode}: {detail}";
        }

        private static readonly string[] KnownDetailFields =
        {
            "message", "error_description", "error", "title", "code",
        };

        /// <summary>
        /// Extracts a short, safe server detail for an exception message: the first
        /// known scalar field of a JSON error envelope (<c>detail</c> string,
        /// <c>detail.message</c>/<c>detail.code</c>, then <c>message</c>,
        /// <c>error_description</c>, <c>error</c>, <c>title</c>, <c>code</c>),
        /// whitespace-collapsed and capped at <see cref="MaxServerDetailLength"/>.
        /// A <c>detail</c> that is an array or any other object (FastAPI validation
        /// errors echo the offending input there) is skipped, and a body that is
        /// not a JSON object — or carries no known field — yields an empty string,
        /// so no raw or reflected body text ever reaches the message.
        /// </summary>
        internal static string SummarizeServerDetail(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return "";
            }
            var detail = ExtractKnownDetail(body!);
            return detail is null ? "" : CollapseWhitespace(detail, MaxServerDetailLength);
        }

        private static string? ExtractKnownDetail(string body)
        {
            JsonObject envelope;
            try
            {
                envelope = JsonUtil.ParseObject(body);
            }
            catch (Exception)
            {
                return null;
            }

            // `detail` is the primary FastAPI error field. Its shape varies:
            var detail = envelope["detail"];

            // A plain string is a safe server message.
            if (JsonUtil.GetString(detail) is string detailText && detailText.Trim().Length > 0)
            {
                return detailText;
            }

            // An array is a FastAPI 422 validation error: each entry's `input`
            // echoes the SUBMITTED request (including the apiKey/password/cryptoKey
            // the SDK generated), so surface ONLY each entry's `msg` string and
            // never `input`/`loc` or a stringified entry.
            if (detail is JsonArray detailArray)
            {
                var messages = new List<string>();
                foreach (var entry in detailArray)
                {
                    if (entry is JsonObject entryObject
                        && JsonUtil.GetString(entryObject["msg"]) is string msg
                        && msg.Trim().Length > 0)
                    {
                        messages.Add(msg.Trim());
                    }
                }
                return messages.Count == 0 ? null : string.Join("; ", messages);
            }

            // An object: surface only its known scalar message/code.
            if (detail is JsonObject detailObject
                && FirstKnownField(detailObject, "message", "code") is string nested)
            {
                return nested;
            }

            return FirstKnownField(envelope, KnownDetailFields);
        }

        private static string? FirstKnownField(JsonObject source, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (JsonUtil.GetString(source[key]) is string value && value.Trim().Length > 0)
                {
                    return value;
                }
            }
            return null;
        }

        /// <summary>
        /// Collapses runs of whitespace and control characters to a single space
        /// and caps the result at <paramref name="maxLength"/> characters, with no
        /// leading or trailing space.
        /// </summary>
        private static string CollapseWhitespace(string text, int maxLength)
        {
            var builder = new StringBuilder(Math.Min(text.Length, maxLength));
            var pendingSpace = false;
            foreach (var character in text)
            {
                if (char.IsControl(character) || char.IsWhiteSpace(character))
                {
                    pendingSpace = builder.Length > 0;
                    continue;
                }
                var needed = (pendingSpace ? 1 : 0) + 1;
                if (builder.Length + needed > maxLength)
                {
                    break;
                }
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }
                builder.Append(character);
            }
            return builder.ToString();
        }

        internal static string PathWithQuery(string path, List<(string Name, string Value)> parameters)
        {
            if (parameters.Count == 0)
            {
                return path;
            }
            var builder = new StringBuilder(path);
            builder.Append('?');
            for (var index = 0; index < parameters.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append('&');
                }
                builder.Append(Uri.EscapeDataString(parameters[index].Name));
                builder.Append('=');
                builder.Append(Uri.EscapeDataString(parameters[index].Value));
            }
            return builder.ToString();
        }

        internal static void AppendParameter(List<(string, string)> parameters, string name, string? value)
        {
            if (value is not null && value.Trim().Length > 0)
            {
                parameters.Add((name, value));
            }
        }

        internal static string NewIdempotencyKey()
        {
            return Guid.NewGuid().ToString("D").ToLowerInvariant();
        }

        internal static string NewSecret()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return Base64UrlEncode(bytes);
        }

        internal static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .Replace("=", "");
        }

        /// <summary>
        /// Site ids collapse runs of underscores and whitespace to single dashes; a
        /// blank input gets a generated <c>thalovant-client-&lt;hex&gt;</c> id.
        /// </summary>
        internal static string CleanSiteId(string value)
        {
            var trimmed = value.Trim();
            var dashed = ReplaceRuns(trimmed, character => character == '_');
            var cleaned = ReplaceRuns(dashed, char.IsWhiteSpace);
            if (cleaned.Length == 0)
            {
                var suffixBytes = new byte[4];
                using (var random = RandomNumberGenerator.Create())
                {
                    random.GetBytes(suffixBytes);
                }
                return "thalovant-client-" + ThalovantCrypto.HexEncode(suffixBytes);
            }
            return cleaned;
        }

        /// <summary>Replaces each run of matching characters with a single dash.</summary>
        private static string ReplaceRuns(string value, Func<char, bool> matches)
        {
            var result = new StringBuilder(value.Length);
            var inRun = false;
            foreach (var character in value)
            {
                if (matches(character))
                {
                    if (!inRun)
                    {
                        result.Append('-');
                        inRun = true;
                    }
                }
                else
                {
                    result.Append(character);
                    inRun = false;
                }
            }
            return result.ToString();
        }

        internal static string DefaultMaster(JsonObject hub, HubDataPlaneEndpoints endpoints, SelectedHubEndpoint? selected)
        {
            if (endpoints.Https is not null)
            {
                return StripEndpointPath(endpoints.Https);
            }
            if (JsonUtil.OptionalString(hub["domain"]) is string domain)
            {
                return HubEndpoints.EndpointFromDomain(domain, HubProtocol.Https);
            }
            if (selected is not null)
            {
                return StripEndpointPath(selected.Endpoint);
            }
            throw new ThalovantApiException("Hub resource does not expose a usable data-plane endpoint.");
        }

        internal static string StripEndpointPath(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            {
                return HubEndpoints.TrimTrailingSlashes(endpoint);
            }
            var builder = new UriBuilder(uri) { Path = "", Query = "", Fragment = "" };
            return HubEndpoints.TrimTrailingSlashes(builder.Uri.ToString());
        }
    }
}
