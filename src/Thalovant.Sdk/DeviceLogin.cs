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
