using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Thalovant
{
    /// <summary>Options for <see cref="ThalovantControlPlane.LoginWithBrowserAsync(DeviceLoginOptions?, System.Threading.CancellationToken)"/>.</summary>
    public sealed class DeviceLoginOptions
    {
        /// <summary>Scopes to request for the issued API token. Omitted from the request when null.</summary>
        public IReadOnlyList<string>? Scopes { get; set; }

        /// <summary>Optional client name shown on the browser approval page.</summary>
        public string? ClientName { get; set; }

        /// <summary>
        /// The registered app to sign in as, such as
        /// <see cref="ThalovantHome.HomeAssistantClientId"/>; left out of the
        /// request when null. The approval page then shows the platform's own
        /// name for the app as verified, with <see cref="ClientName"/> as the
        /// device's label beside it, and approving the app again replaces the
        /// token it already holds. An id the API does not know is refused (400
        /// <c>unknown_client</c>).
        /// </summary>
        public string? ClientId { get; set; }

        /// <summary>
        /// Whether to open the system browser at <c>verification_uri_complete</c>.
        /// Opening is best-effort and never fatal; the prompt always shows the
        /// verification URI and user code regardless.
        /// </summary>
        public bool OpenBrowser { get; set; } = true;

        /// <summary>
        /// Callback presenting the authorization to the user. When null, the SDK
        /// writes the plain <c>verification_uri</c> and <c>user_code</c> to the console.
        /// </summary>
        public Action<DeviceAuthorization>? Prompt { get; set; }

        /// <summary>How long to keep polling for approval. Defaults to 15 minutes.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>Test hook replacing the process launch that opens the browser.</summary>
        internal Action<string>? BrowserLauncher { get; set; }
    }

    /// <summary>
    /// A pending device authorization from <c>POST /v1/auth/device/authorize</c>:
    /// where the user must go (<see cref="VerificationUri"/>) and the short
    /// <see cref="UserCode"/> to enter there.
    /// </summary>
    public sealed class DeviceAuthorization
    {
        public string DeviceCode { get; }
        public string UserCode { get; }
        public string VerificationUri { get; }

        /// <summary>Verification URI with the user code pre-filled, when the API provides one.</summary>
        public string? VerificationUriComplete { get; }

        /// <summary>Seconds until the device code expires, when the API provides it.</summary>
        public int? ExpiresIn { get; }

        /// <summary>Minimum polling interval requested by the server.</summary>
        public TimeSpan Interval { get; }

        /// <summary>The raw authorization payload as returned by the API.</summary>
        public JsonObject Raw { get; }

        public DeviceAuthorization(
            string deviceCode,
            string userCode,
            string verificationUri,
            string? verificationUriComplete,
            int? expiresIn,
            TimeSpan interval,
            JsonObject raw)
        {
            DeviceCode = deviceCode;
            UserCode = userCode;
            VerificationUri = verificationUri;
            VerificationUriComplete = verificationUriComplete;
            ExpiresIn = expiresIn;
            Interval = interval;
            Raw = raw;
        }
    }

    /// <summary>
    /// A pending device sign-in as the person approving it sees it, from
    /// <see cref="ThalovantControlPlane.DescribeDeviceLoginAsync"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ClientVerified"/> is true only when a registered app asked
    /// (it named its <see cref="ClientId"/>): <see cref="ClientName"/> is then
    /// the platform's own name for that app, and <see cref="DeviceName"/>
    /// whatever the device called itself, which nothing checks. Otherwise
    /// <see cref="ClientName"/> is the device's own claim.
    /// </remarks>
    public sealed class DeviceLoginRequest
    {
        /// <summary>The scopes the token would carry.</summary>
        public IReadOnlyList<string> Scopes { get; }

        /// <summary>The app's name: the platform's when <see cref="ClientVerified"/>, otherwise the device's claim.</summary>
        public string? ClientName { get; }

        /// <summary>When the code stops being approvable, as the API wrote it (ISO 8601), when it said.</summary>
        public string? ExpiresAt { get; }

        /// <summary>The registered app that asked, when one did.</summary>
        public string? ClientId { get; }

        /// <summary>Whether the platform vouches for <see cref="ClientName"/>: true only with a <see cref="ClientId"/>.</summary>
        public bool ClientVerified { get; }

        /// <summary>What the device called itself, when a registered app asked.</summary>
        public string? DeviceName { get; }

        /// <summary>The raw payload as returned by the API.</summary>
        public JsonObject Raw { get; }

        public DeviceLoginRequest(
            IReadOnlyList<string> scopes,
            string? clientName,
            string? expiresAt,
            string? clientId,
            bool clientVerified,
            string? deviceName,
            JsonObject raw)
        {
            Scopes = scopes;
            ClientName = clientName;
            ExpiresAt = expiresAt;
            ClientId = clientId;
            ClientVerified = clientVerified;
            DeviceName = deviceName;
            Raw = raw;
        }

        /// <summary>Reads <c>GET /v1/auth/device/codes/{user_code}</c>; absent or empty fields are null.</summary>
        internal static DeviceLoginRequest FromPayload(JsonObject payload)
        {
            var scopes = new List<string>();
            if (payload["scopes"] is JsonArray raw)
            {
                foreach (var entry in raw)
                {
                    if (JsonUtil.GetString(entry) is string scope)
                    {
                        scopes.Add(scope);
                    }
                }
            }
            string? Text(string key) => JsonUtil.GetString(payload[key]) is string value && value.Length > 0 ? value : null;
            var clientId = Text("client_id");
            // Verified only as the API says it, and only with the app named: a
            // true without an id says nothing about who asked.
            var verified = payload["client_verified"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value && clientId != null;
            return new DeviceLoginRequest(scopes, Text("client_name"), Text("expires_at"), clientId, verified, Text("device_name"), payload);
        }
    }

    /// <summary>
    /// The approved device sign-in: a durable scoped API token, already stored on
    /// <see cref="ThalovantControlPlane.AccessToken"/>. <see cref="Scopes"/> echoes
    /// the granted scopes (server-side normalization may expand the requested set).
    /// </summary>
    public sealed class DeviceLoginResult
    {
        public string AccessToken { get; }
        public string? TokenType { get; }
        public IReadOnlyList<string> Scopes { get; }
        public string? ExpiresAt { get; }
        public string? TokenId { get; }

        /// <summary>The raw token payload as returned by the API.</summary>
        public JsonObject Raw { get; }

        public DeviceLoginResult(
            string accessToken,
            string? tokenType,
            IReadOnlyList<string> scopes,
            string? expiresAt,
            string? tokenId,
            JsonObject raw)
        {
            AccessToken = accessToken;
            TokenType = tokenType;
            Scopes = scopes;
            ExpiresAt = expiresAt;
            TokenId = tokenId;
            Raw = raw;
        }

        internal static DeviceLoginResult FromToken(JsonObject token, string accessToken)
        {
            var scopes = new List<string>();
            if (token["scopes"] is JsonArray rawScopes)
            {
                foreach (var entry in rawScopes)
                {
                    if (JsonUtil.GetString(entry) is string scope)
                    {
                        scopes.Add(scope);
                    }
                }
            }
            return new DeviceLoginResult(
                accessToken,
                JsonUtil.GetString(token["token_type"]),
                scopes,
                JsonUtil.GetString(token["expires_at"]),
                JsonUtil.GetString(token["token_id"]),
                token);
        }
    }
}
