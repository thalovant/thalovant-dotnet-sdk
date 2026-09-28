using System;
using System.Linq;
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

        /// <summary>
        /// The kind of connection to make, sent as <c>spec.connection_type</c>:
        /// one of <see cref="ThalovantConnectionTypes"/>, such as
        /// <see cref="ThalovantConnectionTypes.HomeAssistant"/>. The kind decides
        /// what the connection may send and receive, so the API must say the
        /// connection is of that kind; see
        /// <see cref="ThalovantControlPlane.CreateClientIdentityAsync(JsonObject, CreateClientIdentityOptions, CancellationToken)"/>.
        /// Null leaves the kind to <see cref="Spec"/> and the API's default.
        /// </summary>
        public string? ConnectionType { get; set; }

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

        /// <summary>The new connection's id, when the API returned one.</summary>
        public string? ClientId => JsonUtil.GetString(Client["id"]);

        /// <summary>The connection type the API recorded (<c>spec.connection_type</c>), or null.</summary>
        public string? ConnectionType => JsonUtil.GetString((Client["spec"] as JsonObject)?["connection_type"]);

        /// <summary>
        /// The operation that carries the new connection to its hub, when the API
        /// returned one: the hub admits it about ninety seconds after it is
        /// created. <see cref="ThalovantControlPlane.WaitForAdmissionAsync(BootstrapIdentityResult, TimeSpan?, TimeSpan?, CancellationToken)"/>
        /// waits for it.
        /// </summary>
        public OperationResource? Operation { get; }

        public BootstrapIdentityResult(ThalovantIdentity identity, JsonObject hub, JsonObject client, SelectedHubEndpoint? endpoint)
        {
            Identity = identity;
            Hub = hub;
            Client = client;
            Endpoint = endpoint;
            Operation = ThalovantControlPlane.OperationOrNull(client["operation"]);
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
    public sealed partial class ThalovantControlPlane
    {
        public string ApiUrl { get; }
        public string? AccessToken { get; set; }
        public string UserAgent { get; }

        private readonly HttpClient _http;
        private readonly bool _uncontrolledHttpClient;
        private readonly HttpMessageHandler? _handler;

        public ThalovantControlPlane(
            string apiUrl = ThalovantDefaults.ControlApiUrl,
            string? accessToken = null,
            string? userAgent = null,
            HttpClient? httpClient = null)
            : this(apiUrl, accessToken, userAgent, httpClient, null) { }

        /// <summary>Owns a client using the supplied handler with SDK-controlled redirect policy.</summary>
        public ThalovantControlPlane(
            HttpMessageHandler httpMessageHandler,
            string apiUrl = ThalovantDefaults.ControlApiUrl,
            string? accessToken = null,
            string? userAgent = null)
            : this(apiUrl, accessToken, userAgent, null, httpMessageHandler ?? throw new ArgumentNullException(nameof(httpMessageHandler))) { }

        private ThalovantControlPlane(string apiUrl, string? accessToken, string? userAgent,
            HttpClient? httpClient, HttpMessageHandler? httpMessageHandler)
        {
            ApiUrl = NormalizeControlApiUrl(apiUrl);
            AccessToken = accessToken;
            // Resolved here rather than as a parameter default so that the
            // version is never inlined into a caller's assembly at their
            // compile time.
            UserAgent = userAgent ?? ThalovantDefaults.UserAgent;
            if (httpClient != null && httpMessageHandler != null) throw new ArgumentException("Supply either httpClient or httpMessageHandler, not both.");
            _uncontrolledHttpClient = httpClient != null;
            if (httpClient != null) _http = httpClient;
            else {
                var handler = httpMessageHandler ?? new HttpClientHandler();
                DisableAutomaticRedirects(handler);
                _handler = handler;
                _http = new HttpClient(handler);
            }
        }

        private static void DisableAutomaticRedirects(HttpMessageHandler handler)
        {
            if (handler is HttpClientHandler http) http.AllowAutoRedirect = false;
#if NET8_0_OR_GREATER
            else if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
#else
            else if (handler.GetType().FullName == "System.Net.Http.SocketsHttpHandler") {
                var property = handler.GetType().GetProperty("AllowAutoRedirect");
                if (property == null || !property.CanWrite) throw new ArgumentException("This handler cannot disable automatic redirects.");
                property.SetValue(handler, false);
            }
#endif
            else if (handler is DelegatingHandler delegating && delegating.InnerHandler != null) DisableAutomaticRedirects(delegating.InnerHandler);
        }

        private static bool HasAmbientCredentials(HttpMessageHandler? handler, Uri url)
        {
            if (handler is DelegatingHandler delegating) return HasAmbientCredentials(delegating.InnerHandler, url);
            if (handler is HttpClientHandler http) return http.UseDefaultCredentials || http.Credentials != null ||
                (http.UseCookies && http.CookieContainer.GetCookieHeader(url).Length != 0);
            if (handler?.GetType().FullName == "System.Net.Http.SocketsHttpHandler") {
                var type = handler.GetType();
                if (type.GetProperty("Credentials")?.GetValue(handler) != null) return true;
                if (type.GetProperty("UseCookies")?.GetValue(handler) is bool enabled && enabled &&
                    type.GetProperty("CookieContainer")?.GetValue(handler) is System.Net.CookieContainer cookies)
                    return cookies.GetCookieHeader(url).Length != 0;
            }
            return false;
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
        /// Exchange an authorization code for a scoped access token and store it.
        /// </summary>
        /// <remarks>
        /// The other half of <see cref="NativeSignIn.Begin"/>. The verifier is
        /// sent here and nowhere else; it never entered the browser, which is
        /// what makes an intercepted code useless to whoever intercepted it.
        /// A code presented twice revokes the token the first exchange minted
        /// (RFC 9700), so retrying a failed exchange with the same code
        /// destroys the token it is trying to obtain.
        /// </remarks>
        public async Task<JsonObject> CompleteNativeSignInAsync(
            string code,
            string verifier,
            string clientId,
            string redirectUri,
            CancellationToken cancellationToken = default)
        {
            NativeSignIn.RequireSecureTokenExchange(ApiUrl);
            var body = new JsonObject
            {
                ["code"] = code,
                ["code_verifier"] = verifier,
                // Trimmed the way NativeSignIn.Begin trims them, so a caller
                // passing the same strings to both does not get a redirect_uri
                // mismatch the API cannot explain.
                ["client_id"] = clientId?.Trim(),
                ["redirect_uri"] = redirectUri?.Trim(),
            };
            var token = await RequestObjectAsync("POST", "/v1/auth/native/token", body, auth: false, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var accessToken = JsonUtil.GetString(token["access_token"]);
            if (string.IsNullOrEmpty(accessToken))
            {
                throw new ThalovantApiException("Thalovant API token response did not include access_token.");
            }
            // The id goes with the token it names: one left over from an
            // earlier device login would have RevokeApiTokenAsync() revoke
            // that token and then forget this one.
            KeepToken(accessToken!, TokenIdOf(token));
            return token;
        }

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
            // The id goes with the token it names: one left over from an
            // earlier device login would have RevokeApiTokenAsync() revoke
            // that token and then forget this one.
            KeepToken(accessToken!, TokenIdOf(token));
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
            var authorization = await AuthorizeDeviceAsync(options.Scopes, options.ClientName, cancellationToken)
                .ConfigureAwait(false);

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
                authorization.Interval,
                options.Timeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return AcceptDeviceToken(token);
        }

        /// <summary>
        /// The id of the API token this client signed in with through a device
        /// login, which <see cref="RevokeApiTokenAsync"/> revokes by default; null
        /// otherwise.
        /// </summary>
        public string? TokenId { get; set; }

        /// <summary>
        /// The wait before the next poll, per device code: a <c>slow_down</c>
        /// lengthens it for good (RFC 8628 §3.5), across however many calls the
        /// caller's own loop makes.
        /// </summary>
        private readonly Dictionary<string, TimeSpan> _deviceIntervals = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        /// <summary>
        /// Starts a device sign-in, one step at a time, for a caller that runs its
        /// own loop -- a Home Assistant config flow shows the code, then polls on
        /// its own schedule. <c>POST /v1/auth/device/authorize</c>.
        /// </summary>
        /// <remarks>
        /// Show the person <see cref="DeviceAuthorization.VerificationUri"/> and
        /// <see cref="DeviceAuthorization.UserCode"/> (or
        /// <see cref="DeviceAuthorization.VerificationUriComplete"/>, which
        /// carries the code), then call <see cref="PollDeviceLoginAsync"/> every
        /// <see cref="DeviceAuthorization.Interval"/>. <paramref name="scopes"/>
        /// are what the token will carry; the API defaults to <c>hubs:read</c> and
        /// <c>clients:write</c>. A Free plan can approve only
        /// <see cref="ThalovantHome.HomeAssistantScopes"/>. A verification URL
        /// that is not http(s), has no host, or carries credentials is refused
        /// with <see cref="ThalovantApiException"/>: it is about to be opened in a
        /// browser.
        /// </remarks>
        public async Task<DeviceAuthorization> BeginDeviceLoginAsync(
            IEnumerable<string>? scopes = null,
            string? clientName = null,
            CancellationToken cancellationToken = default)
        {
            var authorization = await AuthorizeDeviceAsync(scopes, clientName, cancellationToken).ConfigureAwait(false);
            RememberDeviceInterval(authorization.DeviceCode, authorization.Interval, replace: true);
            return authorization;
        }

        /// <summary>
        /// Asks once whether a device sign-in was approved: one
        /// <c>POST /v1/auth/device/token</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Approved returns the token and keeps it on this client
        /// (<see cref="AccessToken"/> and <see cref="TokenId"/>). Otherwise it
        /// throws <see cref="ThalovantDeviceLoginPendingException"/> -- poll again
        /// after its <see cref="ThalovantDeviceLoginPendingException.Interval"/>,
        /// which a <c>slow_down</c> has already lengthened --
        /// <see cref="ThalovantDeviceCodeExpiredException"/> or
        /// <see cref="ThalovantDeviceAccessDeniedException"/>. All three are
        /// <see cref="ThalovantApiException"/>; so is any other failure, which
        /// carries what the API said.
        /// </para>
        /// <para>
        /// Neither the device code nor the token ever appears in an exception
        /// message.
        /// </para>
        /// </remarks>
        public async Task<DeviceLoginResult> PollDeviceLoginAsync(
            DeviceAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            if (authorization is null) throw new ArgumentNullException(nameof(authorization));
            var deviceCode = authorization.DeviceCode;
            var interval = RememberDeviceInterval(deviceCode, authorization.Interval, replace: false);
            JsonObject token;
            try
            {
                token = await DeviceTokenOnceAsync(deviceCode, interval, cancellationToken).ConfigureAwait(false);
            }
            catch (ThalovantDeviceLoginPendingException pending)
            {
                RememberDeviceInterval(deviceCode, pending.Interval, replace: true);
                throw;
            }
            catch (Exception error) when (error is ThalovantDeviceAccessDeniedException || error is ThalovantDeviceCodeExpiredException)
            {
                // This code is finished. Anything else -- a 503, a lost
                // response -- leaves it, and its lengthened wait, as it was.
                ForgetDeviceInterval(deviceCode);
                throw;
            }
            ForgetDeviceInterval(deviceCode);
            return AcceptDeviceToken(token);
        }

        /// <summary>
        /// Revokes an API token; by default the one this client signed in with
        /// (<see cref="TokenId"/>). <c>DELETE /v1/auth/api-tokens/{token_id}</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A token may always revoke itself, whatever its scopes. Revoking the
        /// token in use forgets it here too, so a later call fails locally rather
        /// than with a 401.
        /// </para>
        /// <para>
        /// Revoking the token in use is idempotent. A token already revoked, or
        /// expired, cannot authenticate its own revoke, so the API answers 401;
        /// the token is dead either way, so that counts as revoked and the token
        /// is forgotten, and revoking again sends nothing until the next sign-in.
        /// Revoking another token by id is not: the API's answer -- 404 for one it
        /// does not know -- is thrown as usual.
        /// </para>
        /// </remarks>
        public async Task RevokeApiTokenAsync(string? tokenId = null, CancellationToken cancellationToken = default)
        {
            string? target;
            bool own;
            lock (_credentials)
            {
                target = string.IsNullOrEmpty(tokenId) ? TokenId : tokenId;
                if (string.IsNullOrEmpty(target))
                {
                    if (_revokedOwn && AccessToken is null)
                    {
                        return; // Already revoked and forgotten: revoking again changes nothing.
                    }
                    throw new ThalovantApiException(
                        "No API token id to revoke: pass tokenId, or sign in with a device login first.");
                }
                own = target == TokenId;
            }
            try
            {
                await RequestDataAsync("DELETE", "/v1/auth/api-tokens/" + Uri.EscapeDataString(target!), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ThalovantAuthenticationException error) when (own && error.StatusCode == 401)
            {
                // It cannot authenticate its own revoke: it is dead already.
            }
            // Forget the token only if it is still the one revoked: a sign-in that
            // finished while the revoke was on its way installed another, and that
            // one is alive. Checked and cleared under the lock every sign-in takes
            // to install a token and its id, so a sign-in on another thread can
            // never be caught half done.
            if (own)
            {
                lock (_credentials)
                {
                    if (TokenId == target)
                    {
                        AccessToken = null;
                        TokenId = null;
                        _revokedOwn = true;
                    }
                }
            }
        }

        /// <summary>Whether the token this client signed in with was revoked and forgotten, so revoking again is a no-op.</summary>
        private bool _revokedOwn;

        /// <summary>
        /// Held while a sign-in installs a token and its id, and while a revoke
        /// checks and clears them -- never across a request.
        /// </summary>
        private readonly object _credentials = new object();

        /// <summary>Installs a sign-in's token and the id it came with (or none), as one step.</summary>
        private void KeepToken(string accessToken, string? tokenId)
        {
            lock (_credentials)
            {
                AccessToken = accessToken;
                TokenId = tokenId;
                _revokedOwn = false;
            }
        }

        /// <summary><c>POST /v1/auth/device/authorize</c>, and the grant read out of it.</summary>
        private async Task<DeviceAuthorization> AuthorizeDeviceAsync(
            IEnumerable<string>? scopes,
            string? clientName,
            CancellationToken cancellationToken)
        {
            var payload = new JsonObject();
            var list = new JsonArray();
            foreach (var scope in scopes ?? Array.Empty<string>())
            {
                list.Add(scope);
            }
            // An empty list is left out exactly as none is: the API requires at
            // least one scope and answers [] with a 422, and a missing field asks
            // for its default.
            if (list.Count > 0)
            {
                payload["scopes"] = list;
            }
            if (!string.IsNullOrEmpty(clientName))
            {
                payload["client_name"] = clientName;
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
            var completeUri = JsonUtil.GetString(grant["verification_uri_complete"]);
            if (DeviceVerificationUri(verificationUri!) == null ||
                (grant["verification_uri_complete"] != null && (completeUri == null || DeviceVerificationUri(completeUri) == null)))
                throw new ThalovantApiException("Thalovant API device authorization returned an invalid verification URI.");
            var rawInterval = JsonUtil.GetInt(grant["interval"]);
            var interval = rawInterval is int seconds && seconds >= 0
                ? TimeSpan.FromSeconds(seconds)
                : DefaultDevicePollInterval;
            return new DeviceAuthorization(
                deviceCode!,
                userCode!,
                verificationUri!,
                completeUri,
                JsonUtil.GetInt(grant["expires_in"]),
                interval,
                grant);
        }

        /// <summary>The wait remembered for a device code, setting it first when there is none (or always, with <paramref name="replace"/>).</summary>
        private TimeSpan RememberDeviceInterval(string deviceCode, TimeSpan interval, bool replace)
        {
            lock (_deviceIntervals)
            {
                if (!replace && _deviceIntervals.TryGetValue(deviceCode, out var known))
                {
                    return known;
                }
                // A caller that abandons codes mid-flight must not grow this for ever.
                if (!_deviceIntervals.ContainsKey(deviceCode) && _deviceIntervals.Count >= 64)
                {
                    _deviceIntervals.Clear();
                }
                _deviceIntervals[deviceCode] = interval;
                return interval;
            }
        }

        private void ForgetDeviceInterval(string deviceCode)
        {
            lock (_deviceIntervals)
            {
                _deviceIntervals.Remove(deviceCode);
            }
        }

        /// <summary>Keeps an approved device token, and its id, on this client.</summary>
        private DeviceLoginResult AcceptDeviceToken(JsonObject token)
        {
            var accessToken = JsonUtil.GetString(token["access_token"]);
            if (string.IsNullOrEmpty(accessToken))
            {
                throw new ThalovantApiException("Thalovant API token response did not include access_token.");
            }
            KeepToken(accessToken!, TokenIdOf(token));
            return DeviceLoginResult.FromToken(token, accessToken!);
        }

        private static string? TokenIdOf(JsonObject token) =>
            JsonUtil.GetString(token["token_id"]) is string id && id.Length > 0 ? id : null;

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
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return await DeviceTokenOnceAsync(deviceCode, wait, cancellationToken).ConfigureAwait(false);
                }
                catch (ThalovantDeviceLoginPendingException pending)
                {
                    wait = pending.Interval;
                }
                var remaining = deadline - clock();
                if (remaining <= TimeSpan.Zero)
                {
                    throw new ThalovantTimeoutException("Timed out waiting for the device sign-in to be approved.");
                }
                await delay(wait < remaining ? wait : remaining, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// One <c>POST /v1/auth/device/token</c>: the token response, or the
        /// exception that says why there is none yet. <paramref name="interval"/>
        /// is this code's wait so far; a pending answer carries it, five seconds
        /// longer after a <c>slow_down</c>.
        /// </summary>
        private async Task<JsonObject> DeviceTokenOnceAsync(string deviceCode, TimeSpan interval, CancellationToken cancellationToken)
        {
            var body = new JsonObject { ["device_code"] = deviceCode };
            var (statusCode, text) = await SendRawAsync("POST", "/v1/auth/device/token", body, auth: false, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var parsed = ThalovantApiException.ParseProblem(text);
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
                    throw new ThalovantDeviceLoginPendingException(
                        "The device sign-in has not been approved yet.", interval, statusCode, text, parsed);
                case "slow_down":
                    throw new ThalovantDeviceLoginPendingException(
                        "The device sign-in has not been approved yet.", interval + DevicePollSlowDownIncrement, statusCode, text, parsed);
                case "access_denied":
                    throw new ThalovantDeviceAccessDeniedException(
                        "The device sign-in request was denied in the browser.", statusCode, text, parsed);
                case "expired_token":
                    throw new ThalovantDeviceCodeExpiredException(
                        "The device sign-in code expired before it was approved. "
                        + "Start the sign-in again to request a new code.", statusCode, text, parsed);
                default:
                    throw ApiError(statusCode, text, parsed);
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
        internal static Uri? DeviceVerificationUri(string url)
        {
            if (url.Any(ch => char.IsControl(ch) || char.IsWhiteSpace(ch)) || !Uri.TryCreate(url, UriKind.Absolute, out var target) ||
                (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrEmpty(target.Host) || !string.IsNullOrEmpty(target.UserInfo) ||
                System.Text.RegularExpressions.Regex.IsMatch(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*://[^/?#]*@")) return null;
            return target;
        }

        internal static void TryOpenBrowser(string url, Action<ProcessStartInfo>? launch = null)
        {
            var target = DeviceVerificationUri(url);
            if (target == null) return;
            try
            {
                ProcessStartInfo info;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    info = new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true };
                else {
                    info = new ProcessStartInfo(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open") { UseShellExecute = false };
                    info.ArgumentList.Add(target.AbsoluteUri);
                }
                if (launch != null) launch(info);
                else { using var process = Process.Start(info); }
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

        /// <summary>Deep merge with a revision precondition. Retry only 412, at most three attempts.
        /// Requires hubs:read and paid hubs:write; older servers fail before any write.</summary>
        public async Task<JsonObject> UpdateRuntimeGroupConfigAsync(string runtimeGroupId, JsonObject config,
            JsonObject? personas = null, CancellationToken cancellationToken = default) {
            var delta = JsonUtil.CloneObject(config);
            var stablePersonas = personas == null ? null : JsonUtil.CloneObject(personas);
            for (int attempt = 0; ; attempt++) {
                var snapshot = await GetRuntimeGroupConfigAsync(runtimeGroupId, cancellationToken).ConfigureAwait(false);
                var revision = JsonUtil.GetString(snapshot["revision"]);
                if (revision == null || !System.Text.RegularExpressions.Regex.IsMatch(revision, "\\A[0-9a-f]{64}\\z") || snapshot["config"] is not JsonObject stored)
                    throw new ThalovantApiException("Safe configuration merge requires a valid config and revision from the API.");
                var body = new JsonObject { ["config"] = MergeRuntimeConfig(stored, delta), ["expected_revision"] = revision };
                if (stablePersonas != null) body["personas"] = JsonUtil.CloneObject(stablePersonas);
                try { return await RequestObjectAsync("PUT", "/v1/runtime-groups/" + Uri.EscapeDataString(runtimeGroupId) + "/config", body, cancellationToken: cancellationToken).ConfigureAwait(false); }
                catch (ThalovantApiException error) when (error.StatusCode == 412 && attempt < 2) { }
            }
        }
        private static JsonObject MergeRuntimeConfig(JsonObject stored, JsonObject delta) {
            var result = JsonUtil.CloneObject(stored);
            foreach (var pair in delta) result[pair.Key] = result[pair.Key] is JsonObject old && pair.Value is JsonObject next
                ? MergeRuntimeConfig(old, next) : pair.Value?.DeepClone();
            return result;
        }

        /// <summary>Explicit unconditional configuration replacement via PATCH.</summary>
        public Task<JsonObject> ReplaceRuntimeGroupConfigAsync(
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
        /// (<c>title</c>, <c>category</c>, <c>tags</c>, <c>verified</c>, <c>access_tier</c>).
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

        /// <summary>
        /// Provisions a client identity on <paramref name="hub"/> (a hub resource
        /// already read): <c>POST /v1/clients</c> with an <c>Idempotency-Key</c>
        /// header, parsing the returned <c>initial_identify</c> credentials.
        /// </summary>
        /// <remarks>
        /// <para>
        /// With <see cref="CreateClientIdentityOptions.ConnectionType"/> set, the
        /// kind is sent as <c>spec.connection_type</c> and the API must say the
        /// connection is of that kind. A 422 about the field, or a created
        /// connection whose type came back different, throws
        /// <see cref="ThalovantUnsupportedConnectionTypeException"/> -- after
        /// deleting that connection, which would otherwise be an ordinary
        /// satellite nobody asked for. A plan that does not allow it throws
        /// <see cref="ThalovantPlanException"/>; a hub that already holds its one
        /// link of that kind, <see cref="ThalovantAlreadyLinkedException"/>; a
        /// token that cannot do it, <see cref="ThalovantAuthenticationException"/>.
        /// </para>
        /// <para>
        /// The result's <see cref="BootstrapIdentityResult.Operation"/> tracks the
        /// hub admitting the connection, about ninety seconds;
        /// <see cref="WaitForAdmissionAsync(BootstrapIdentityResult, TimeSpan?, TimeSpan?, CancellationToken)"/>
        /// waits for it.
        /// </para>
        /// </remarks>
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
            var connectionType = options.ConnectionType;
            if (connectionType is not null)
            {
                spec["connection_type"] = connectionType;
            }
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

            JsonObject client;
            try
            {
                client = await CreateClientAsync(payload, options.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            }
            catch (ThalovantApiException error) when (connectionType is not null && RefusesConnectionType(error))
            {
                throw new ThalovantUnsupportedConnectionTypeException(
                    $"The Thalovant API cannot create a '{connectionType}' connection yet.", error);
            }
            if (connectionType is not null)
            {
                await RequireConnectionTypeAsync(client, connectionType).ConfigureAwait(false);
            }
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

        /// <summary>A 422 whose problem is about <c>connection_type</c>.</summary>
        /// <remarks>
        /// Only the parts of the problem that name what is wrong are read: its
        /// <c>detail</c> and <c>code</c>, and the <c>loc</c> and <c>msg</c> of each
        /// validation error, under <c>errors</c> or under a <c>detail</c> that is a
        /// list. Never an entry's <c>input</c>: that echoes what was sent, and a
        /// spec that failed validation for any other reason still carries the
        /// <c>connection_type</c> the SDK put in it.
        /// </remarks>
        private static bool RefusesConnectionType(ThalovantApiException error)
        {
            if (error.StatusCode != 422)
            {
                return false;
            }
            if (NamesConnectionType(error.Detail) || NamesConnectionType(error.ErrorCode))
            {
                return true;
            }
            var problem = error.Problem;
            foreach (var key in new[] { "errors", "detail" })
            {
                if (problem?[key] is not JsonArray entries)
                {
                    continue;
                }
                foreach (var entry in entries)
                {
                    if (entry is JsonObject item
                        && (NamesConnectionType(JsonUtil.GetString(item["msg"])) || NamesConnectionType(Location(item["loc"]))))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>A validation error's <c>loc</c> as one dotted path, such as <c>body.spec.connection_type</c>.</summary>
        private static string? Location(JsonNode? loc) => loc switch
        {
            JsonArray parts => string.Join(".", parts.Select(part => part is JsonValue value && value.TryGetValue<string>(out var name) ? name : part?.ToJsonString() ?? "None")),
            JsonValue value when value.TryGetValue<string>(out var path) => path,
            _ => null,
        };

        private static bool NamesConnectionType(string? text) =>
            text is not null
            && (text.IndexOf("connection_type", StringComparison.Ordinal) >= 0
                || text.IndexOf("connectionType", StringComparison.Ordinal) >= 0);

        /// <summary>Deletes, then refuses, a connection the API did not make of the kind asked.</summary>
        private async Task RequireConnectionTypeAsync(JsonObject client, string connectionType)
        {
            var echoed = JsonUtil.GetString((client["spec"] as JsonObject)?["connection_type"]);
            if (echoed == connectionType)
            {
                return;
            }
            var clientId = JsonUtil.GetString(client["id"]);
            var note = "";
            if (!string.IsNullOrEmpty(clientId))
            {
                try
                {
                    // Not the caller's token: this undoes what the call just
                    // did, and a connection left behind has grants nobody
                    // asked for.
                    await DeleteClientAsync(clientId!, JsonUtil.GetString(client["etag"]), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (ThalovantApiException)
                {
                    note = $" Deleting the connection it made instead ({clientId}) failed; remove it in the dashboard.";
                }
            }
            throw new ThalovantUnsupportedConnectionTypeException(
                $"The Thalovant API did not make a '{connectionType}' connection (it answered '{echoed ?? "no type"}').{note}");
        }

        /// <summary>
        /// One client (a hub connection), with the <c>etag</c> a change needs.
        /// <c>GET /v1/clients/{client_id}</c>.
        /// </summary>
        public Task<JsonObject> GetClientAsync(string clientId, CancellationToken cancellationToken = default)
        {
            return RequestObjectAsync("GET", "/v1/clients/" + Uri.EscapeDataString(clientId), cancellationToken: cancellationToken);
        }

        /// <summary>
        /// Deletes a client (a hub connection). <c>DELETE /v1/clients/{client_id}</c>
        /// with <c>If-Match</c>.
        /// </summary>
        /// <remarks>
        /// Without <paramref name="etag"/> this reads the client's current one
        /// first. If another writer changed the client in between (HTTP 412) it
        /// reads the etag once more and retries once. A client that is already
        /// gone (HTTP 404, on either request) counts as deleted.
        /// </remarks>
        public async Task DeleteClientAsync(string clientId, string? etag = null, CancellationToken cancellationToken = default)
        {
            var path = "/v1/clients/" + Uri.EscapeDataString(clientId);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (etag is null)
                    {
                        var current = await GetClientAsync(clientId, cancellationToken).ConfigureAwait(false);
                        etag = JsonUtil.GetString(current["etag"]);
                        if (string.IsNullOrEmpty(etag))
                        {
                            throw new ThalovantApiException("Thalovant API client resource did not include an etag.");
                        }
                    }
                    var headers = new Dictionary<string, string> { ["If-Match"] = etag! };
                    await RequestDataAsync("DELETE", path, headers: headers, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (ThalovantApiException error) when (error.StatusCode == 404)
                {
                    return;
                }
                catch (ThalovantApiException error) when (error.StatusCode == 412 && attempt == 0)
                {
                    etag = null;
                }
            }
        }

        /// <summary>Default wait for <see cref="WaitForAdmissionAsync(OperationResource?, TimeSpan?, TimeSpan?, CancellationToken)"/>: a hub admits a new connection in about ninety seconds.</summary>
        public static readonly TimeSpan DefaultAdmissionTimeout = TimeSpan.FromSeconds(180);

        /// <summary>Default time between two reads of an operation being waited on.</summary>
        public static readonly TimeSpan DefaultOperationPollInterval = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Waits until the hub has admitted a connection
        /// <see cref="CreateClientIdentityAsync(JsonObject, CreateClientIdentityOptions, CancellationToken)"/>
        /// just created, by following its <see cref="BootstrapIdentityResult.Operation"/>.
        /// </summary>
        /// <inheritdoc cref="WaitForAdmissionAsync(OperationResource?, TimeSpan?, TimeSpan?, CancellationToken)"/>
        public Task WaitForAdmissionAsync(
            BootstrapIdentityResult connection,
            TimeSpan? timeout = null,
            TimeSpan? pollInterval = null,
            CancellationToken cancellationToken = default)
        {
            if (connection is null) throw new ArgumentNullException(nameof(connection));
            return WaitForAdmissionAsync(connection.Operation, timeout, pollInterval, cancellationToken);
        }

        /// <summary>
        /// Waits until the hub has admitted a new connection, polling
        /// <c>GET /v1/operations/{id}</c> (the operation's <c>links.self</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Returns at once when there is nothing to wait on: no operation, or one
        /// the API no longer tracks (HTTP 404). <c>ready</c> is admitted;
        /// <c>requested</c>, <c>committed</c> and <c>applied</c> keep polling, and
        /// a 5xx is ridden out. So is a 429, the token's rate limit, which this
        /// wait shares with every other call: the next poll waits
        /// <see cref="ThalovantApiException.RetryAfter"/> when that is longer than
        /// <paramref name="pollInterval"/>, and when it is longer than the time
        /// left the wait ends at once as a timeout. No read runs past
        /// <paramref name="timeout"/> (<see cref="DefaultAdmissionTimeout"/>).
        /// </para>
        /// <para>
        /// Throws <see cref="ThalovantAdmissionFailedException"/> when the
        /// operation failed or timed out on the platform
        /// (<see cref="ThalovantAdmissionFailedException.ErrorCode"/>) or the API
        /// refused the wait itself (<see cref="ThalovantAdmissionFailedException.ApiError"/>),
        /// and <see cref="ThalovantAdmissionTimeoutException"/> -- a
        /// <see cref="ThalovantConnectionException"/> that is also an
        /// <see cref="IThalovantTimeout"/> -- when the time runs out; the
        /// connection may still be admitted after that. A 401 or 403 is thrown as
        /// the API's own error (<see cref="ThalovantAuthenticationException"/> and
        /// its kin), and an API out of reach as
        /// <see cref="ThalovantApiUnreachableException"/>: neither says anything
        /// about the connection.
        /// </para>
        /// <para>
        /// A <c>links.self</c> on another origin than the API's -- scheme, host and
        /// port, the default port spelled out -- is never fetched, because the
        /// token goes nowhere else: that throws <see cref="ThalovantApiException"/>.
        /// </para>
        /// </remarks>
        public async Task WaitForAdmissionAsync(
            OperationResource? operation,
            TimeSpan? timeout = null,
            TimeSpan? pollInterval = null,
            CancellationToken cancellationToken = default)
        {
            var budget = timeout ?? DefaultAdmissionTimeout;
            var every = pollInterval ?? DefaultOperationPollInterval;
            if (budget < TimeSpan.Zero || every <= TimeSpan.Zero || every.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(pollInterval), "Admission waits must be positive, and the poll interval must fit Task.Delay.");
            if (operation is null)
            {
                return;
            }
            var link = operation.Links.TryGetValue("self", out var self) ? self : null;
            if (link is not null && link.IndexOf("://", StringComparison.Ordinal) >= 0 && !SameOrigin(link, ApiUrl))
            {
                // The token goes to the API's own origin -- scheme, host and
                // port -- and nowhere else.
                throw new ThalovantApiException("The admission operation points outside the Thalovant API.");
            }
            var operationId = OperationId(operation);
            var clock = Stopwatch.StartNew();
            ThalovantAdmissionTimeoutException TimedOut(string? why = null) => new ThalovantAdmissionTimeoutException(
                $"The hub did not admit the connection within {budget.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s"
                + (why is null ? "" : $" ({why})") + "; it may still admit it later.",
                budget);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OperationResource? current = null;
                var pause = every;
                var rateLimited = false;
                // Every read is bounded by what is left of the wait: a read the
                // API is slow to answer must not carry the wait past it.
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var left = budget - clock.Elapsed;
                    read.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
                    try
                    {
                        current = await GetOperationAsync(operationId, read.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw TimedOut();
                    }
                    catch (ThalovantApiUnreachableException)
                    {
                        // Out of reach says nothing about the hub: the connection
                        // may be admitted already. Not a failed admission.
                        throw;
                    }
                    catch (ThalovantApiException error) when (error.StatusCode == 404)
                    {
                        // The API no longer tracks it: nothing is left to wait for.
                        return;
                    }
                    catch (ThalovantApiException error) when (error.StatusCode == 401 || error.StatusCode == 403)
                    {
                        // The token, not the connection: signing in again fixes it.
                        throw;
                    }
                    catch (ThalovantApiException error) when (error.StatusCode >= 500)
                    {
                        // Ridden out: the platform is busy, not the operation failed.
                    }
                    catch (ThalovantApiException error) when (error.StatusCode == 429)
                    {
                        // The token's rate limit, which this wait shares with the
                        // caller's other calls: the connection is still on its way.
                        rateLimited = true;
                        if (error.RetryAfter is TimeSpan asked && asked > pause) pause = asked;
                    }
                    catch (ThalovantApiException error)
                    {
                        throw new ThalovantAdmissionFailedException(
                            $"The hub could not admit the connection: {error.Message}", null, error);
                    }
                }
                if (current is not null)
                {
                    if (current.Status == OperationStatus.Ready)
                    {
                        return;
                    }
                    if (current.Status == OperationStatus.Failed || current.Status == OperationStatus.TimedOut)
                    {
                        throw new ThalovantAdmissionFailedException(
                            $"The hub could not admit the connection: operation {current.Id} ended with status "
                            + $"{OperationStatusConverter.WireName(current.Status)} ({current.ErrorCode ?? "no code"}).",
                            current.ErrorCode);
                    }
                }
                var remaining = budget - clock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw TimedOut();
                }
                if (rateLimited && pause > remaining)
                {
                    // Asked to wait longer than is left: waiting it out would
                    // only end in the same timeout, later.
                    throw TimedOut("the API asked to slow down");
                }
                await Monotonic.DelayAtLeastAsync(pause < remaining ? pause : remaining, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Whether two URLs share an origin: scheme, host and port, the scheme's default port spelled out.</summary>
        internal static bool SameOrigin(string url, string other)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var a) || !Uri.TryCreate(other, UriKind.Absolute, out var b))
            {
                return false;
            }
            // Uri.Port is the scheme's default when the URL names none: 443 for
            // https and 80 for http, so https://h and https://h:443 are one.
            return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
                && a.Port == b.Port;
        }

        /// <summary>The id to poll: the operation's own, else the tail of its <c>links.self</c>.</summary>
        private static string OperationId(OperationResource operation)
        {
            if (!string.IsNullOrEmpty(operation.Id))
            {
                return operation.Id;
            }
            var text = (operation.Links.TryGetValue("self", out var self) ? self : null)?.Trim() ?? "";
            var marker = text.LastIndexOf("/v1/operations/", StringComparison.Ordinal);
            if (marker >= 0)
            {
                text = text.Substring(marker + "/v1/operations/".Length);
                var query = text.IndexOf('?');
                if (query >= 0) text = text.Substring(0, query);
                text = text.Trim('/');
            }
            if (text.Length == 0)
            {
                throw new ThalovantApiException("An operation needs an id to wait on.");
            }
            return Uri.UnescapeDataString(text);
        }

        /// <summary>The operation a create answered with, or null when there is none or it is unreadable.</summary>
        internal static OperationResource? OperationOrNull(JsonNode? node)
        {
            if (node is not JsonObject value)
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<OperationResource>(value.ToJsonString());
            }
            catch (JsonException)
            {
                return null;
            }
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
                throw new ThalovantApiException("Invalid Thalovant API URL.");
            }
            if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) throw new ThalovantApiException("Thalovant API URLs require HTTP or HTTPS.");
            if (!string.IsNullOrEmpty(url.UserInfo) || System.Text.RegularExpressions.Regex.IsMatch(ApiUrl, @"^[a-zA-Z][a-zA-Z0-9+.-]*://[^/?#]*@")) throw new ThalovantApiException("Thalovant API URLs must not contain userinfo credentials.");
            var secretHeaders = new[] { "Authorization", "Proxy-Authorization", "Cookie" };
            var carriesCredentials = auth || body != null || HasAmbientCredentials(_handler, url) ||
                secretHeaders.Any(name => _http.DefaultRequestHeaders.Contains(name)) ||
                (headers != null && headers.Keys.Any(key => secretHeaders.Any(name => key.Equals(name, StringComparison.OrdinalIgnoreCase))));
            var explicitLoopback = System.Text.RegularExpressions.Regex.IsMatch(ApiUrl, @"^http://(?:localhost|127\.0\.0\.1|\[::1\])(?::[0-9]+)?(?:/|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (carriesCredentials && url.Scheme != Uri.UriSchemeHttps && !explicitLoopback)
                throw new ThalovantApiException("Credential-bearing Thalovant API requests require HTTPS except explicit loopback development endpoints.");
            if (carriesCredentials && _uncontrolledHttpClient)
                throw new ThalovantApiException("Credential-bearing requests cannot use an injected HttpClient because redirects cannot be controlled. Supply httpMessageHandler instead.");
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
            var (statusCode, text, _) = await SendAsync(method, path, body, headers, auth, cancellationToken).ConfigureAwait(false);
            return (statusCode, text);
        }

        /// <summary>
        /// Sends a request and returns its status, its body, and the wait its
        /// <c>Retry-After</c> or <c>RateLimit-Reset</c> header names. A request the
        /// API never answered -- DNS, the connection, TLS, a proxy -- throws
        /// <see cref="ThalovantApiUnreachableException"/>.
        /// </summary>
        private async Task<(int StatusCode, string Body, TimeSpan? RetryAfter)> SendAsync(
            string method,
            string path,
            JsonObject? body,
            IReadOnlyDictionary<string, string>? headers,
            bool auth,
            CancellationToken cancellationToken)
        {
            using var request = BuildRequest(method, path, body, headers, auth);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new ThalovantApiUnreachableException($"Thalovant API request failed: {exception.Message}", exception);
            }
            using (response)
            {
                var bytes = response.Content is null
                    ? Array.Empty<byte>()
                    : await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                return ((int)response.StatusCode, DecodeBody(bytes), RetryAfterHeader(response));
            }
        }

        /// <summary>
        /// <c>Retry-After</c> in whole seconds, else <c>RateLimit-Reset</c>: the
        /// API's own rate limiter answers a 429 in plain text with only the
        /// latter. An HTTP-date <c>Retry-After</c> is not read.
        /// </summary>
        private static TimeSpan? RetryAfterHeader(HttpResponseMessage response)
        {
            foreach (var name in new[] { "Retry-After", "RateLimit-Reset" })
            {
                if (response.Headers.TryGetValues(name, out var values)
                    && values.FirstOrDefault()?.Trim() is string text
                    && text.Length > 0 && text.Length <= 9 && text.All(ch => ch >= '0' && ch <= '9'))
                {
                    return TimeSpan.FromSeconds(int.Parse(text, CultureInfo.InvariantCulture));
                }
            }
            return null;
        }

        /// <summary>
        /// A response body as text: UTF-8 whatever the Content-Type says, without
        /// a byte-order mark. JSON is UTF-8 (RFC 8259), and the API labels its
        /// error bodies <c>application/problem+json</c> with no charset;
        /// <c>ReadAsStringAsync</c> would instead decode the bytes as whatever
        /// charset a response names, so a proxy's label could turn an accented
        /// sentence into mojibake.
        /// </summary>
        internal static string DecodeBody(byte[] bytes)
        {
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
        }

        internal async Task<string> RequestDataAsync(
            string method,
            string path,
            JsonObject? body = null,
            IReadOnlyDictionary<string, string>? headers = null,
            bool auth = true,
            CancellationToken cancellationToken = default)
        {
            var (statusCode, text, retryAfter) = await SendAsync(method, path, body, headers, auth, cancellationToken)
                .ConfigureAwait(false);
            if (statusCode < 200 || statusCode >= 300)
            {
                throw ApiError(statusCode, text).WithRetryAfterHeader(retryAfter);
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
        /// The error for a response the API answered with a failure status: the
        /// one place a non-2xx control-plane response becomes an exception.
        /// </summary>
        /// <remarks>
        /// The body is parsed once. When it is a JSON object it rides on the
        /// error whole, as <see cref="ThalovantApiException.Problem"/>, with its
        /// <see cref="ThalovantApiException.ErrorCode"/> and its unshortened
        /// <see cref="ThalovantApiException.Detail"/> read out of it; the
        /// message stays the bounded line <see cref="FormatRequestFailed"/>
        /// always built.
        /// </remarks>
        internal static ThalovantApiException ApiError(int statusCode, string body) =>
            ApiError(statusCode, body, ThalovantApiException.ParseProblem(body));

        private static ThalovantApiException ApiError(int statusCode, string body, JsonObject? problem) =>
            ThalovantApiException.FromResponse(FailureMessage(statusCode, problem), statusCode, body, problem);

        /// <summary>
        /// Builds the message for a failed control-plane request: the HTTP status
        /// plus, only when present, a known human-readable field of a JSON error
        /// envelope. Arbitrary response-body text is never echoed — so a 4xx that
        /// reflects the request (for example a validation error carrying the
        /// POST /v1/clients <c>apiKey</c>, <c>password</c>, or <c>cryptoKey</c> the
        /// SDK generated) cannot launder those secrets into the message. The full
        /// body stays available on <see cref="ThalovantApiException.Body"/> and
        /// <see cref="ThalovantApiException.Problem"/>, and still feeds
        /// <see cref="ThalovantApiException.ErrorCode"/> and
        /// <see cref="ThalovantApiException.Detail"/>.
        /// </summary>
        internal static string FormatRequestFailed(int statusCode, string? body) =>
            FailureMessage(statusCode, ThalovantApiException.ParseProblem(body));

        private static string FailureMessage(int statusCode, JsonObject? problem)
        {
            var detail = SummarizeProblem(problem);
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
        internal static string SummarizeServerDetail(string? body) =>
            SummarizeProblem(ThalovantApiException.ParseProblem(body));

        private static string SummarizeProblem(JsonObject? envelope)
        {
            var detail = envelope is null ? null : ExtractKnownDetail(envelope);
            return detail is null ? "" : CollapseWhitespace(detail, MaxServerDetailLength);
        }

        private static string? ExtractKnownDetail(JsonObject envelope)
        {
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
