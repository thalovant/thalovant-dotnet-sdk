using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
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
        public const string UserAgent = "ThalovantDotNetSDK/0.1.0";
    }

    /// <summary>
    /// Filters for <c>GET /v1/analytics/overview</c> (and, with <see cref="Admin"/>,
    /// <c>GET /v1/admin/analytics/overview</c>). <see cref="OwnerId"/> is admin-only.
    /// </summary>
    public sealed class AnalyticsOverviewOptions
    {
        public bool Admin { get; set; }
        public string? Range { get; set; }
        public string? Bucket { get; set; }
        public string? OwnerId { get; set; }
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

        public JsonObject ToJsonObject(bool includeSecrets = false)
        {
            var data = new JsonObject
            {
                ["identity"] = Identity.ToJsonObject(includeSecrets),
                ["hub"] = JsonUtil.CloneObject(Hub),
                ["client"] = JsonUtil.CloneObject(Client),
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
            string userAgent = ThalovantDefaults.UserAgent,
            HttpClient? httpClient = null)
        {
            ApiUrl = NormalizeControlApiUrl(apiUrl);
            AccessToken = accessToken;
            UserAgent = userAgent;
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
            var endpoint = options.Admin ? "/v1/admin/analytics/overview" : "/v1/analytics/overview";
            var parameters = new List<(string, string)>();
            AppendParameter(parameters, "range", options.Range);
            AppendParameter(parameters, "bucket", options.Bucket);
            if (options.Admin)
            {
                AppendParameter(parameters, "owner_id", options.OwnerId);
            }
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
            return RequestObjectAsync("GET", PathWithQuery(endpoint, parameters), cancellationToken: cancellationToken);
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

        internal async Task<string> RequestDataAsync(
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
                var statusCode = (int)response.StatusCode;
                if (statusCode < 200 || statusCode >= 300)
                {
                    throw new ThalovantApiException(
                        $"Thalovant API request failed with HTTP {statusCode}: {text}",
                        statusCode,
                        text);
                }
                return text;
            }
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
