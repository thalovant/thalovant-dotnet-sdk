using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Thalovant.Sdk
{
    /// <summary>
    /// The authorization-code grant with PKCE (RFC 7636), for a client that can
    /// open a browser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ThalovantControlPlane.LoginWithBrowserAsync"/> is the device
    /// grant, and it exists for something that <em>cannot</em> open one:
    /// somebody reads a code off one screen and types it into another. A
    /// desktop or mobile app is not in that position -- it can open the browser
    /// itself and be handed the answer back -- and asking its user to copy a
    /// code between two windows on the same device is a worse experience than
    /// the one every other app there offers.
    /// </para>
    /// <para>
    /// <see cref="Verifier"/> never leaves the process and never enters the
    /// browser. That is what PKCE is for: a code intercepted by whatever else
    /// claimed the redirect is useless without it.
    /// </para>
    /// </remarks>
    public sealed class NativeSignIn
    {
        /// <summary>Where a person approves the request.</summary>
        public const string DefaultDashboardUrl = "https://dash.thalovant.com";

        /// <summary>The three a phone needs; also the three a free plan may mint.</summary>
        public static readonly IReadOnlyList<string> DefaultScopes =
            new[] { "hubs:read", "clients:read", "clients:write" };

        private NativeSignIn(string authorizationUrl, string state, string verifier)
        {
            AuthorizationUrl = authorizationUrl;
            State = state;
            Verifier = verifier;
        }

        /// <summary>Open this in a browser.</summary>
        public string AuthorizationUrl { get; }

        /// <summary>Proves the redirect answers <em>this</em> attempt, not a replayed one.</summary>
        public string State { get; }

        /// <summary>Never send this to the browser. Exchanged with the code, once.</summary>
        public string Verifier { get; }

        /// <summary>
        /// Start a sign-in. Returns the URL to open and the secrets to keep.
        /// </summary>
        /// <remarks>
        /// <paramref name="redirectUri"/> must be one the API has registered for
        /// <paramref name="clientId"/>; the authorization endpoint matches it
        /// exactly and refuses anything else, so it cannot be turned into an
        /// open redirect.
        /// </remarks>
        public static NativeSignIn Begin(
            string clientId,
            string redirectUri,
            IEnumerable<string>? scopes = null,
            string dashboardUrl = DefaultDashboardUrl)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new ArgumentException("clientId is required to start a sign-in.", nameof(clientId));
            }
            if (string.IsNullOrWhiteSpace(redirectUri))
            {
                throw new ArgumentException("redirectUri is required to start a sign-in.", nameof(redirectUri));
            }

            var verifier = NewVerifier();
            var state = RandomUrlSafe(24);
            var requested = scopes is null ? DefaultScopes : new List<string>(scopes);
            var query = new StringBuilder();
            void Add(string name, string value)
            {
                if (query.Length > 0) query.Append('&');
                query.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            }
            Add("client_id", clientId.Trim());
            Add("redirect_uri", redirectUri.Trim());
            Add("response_type", "code");
            Add("code_challenge", ChallengeFor(verifier));
            // S256 only. `plain` is refused by the API, and offering it here
            // would only give a caller a way to ask for the weaker one.
            Add("code_challenge_method", "S256");
            Add("scope", string.Join(" ", requested));
            Add("state", state);

            var dashboard = dashboardUrl.TrimEnd('/');
            return new NativeSignIn($"{dashboard}/authorize?{query}", state, verifier);
        }

        /// <summary>A PKCE verifier: 64 random bytes, base64url, no padding.</summary>
        public static string NewVerifier() => RandomUrlSafe(64);

        /// <summary>The S256 challenge for a verifier.</summary>
        public static string ChallengeFor(string verifier)
        {
            using var sha256 = SHA256.Create();
            return Base64Url(sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        /// <summary>
        /// Whether a URL belongs to Thalovant, for a caller that wants to show
        /// where it is about to send somebody. Scheme and host only: a display
        /// check, not an authorization one.
        /// </summary>
        public static bool IsThalovantUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
            if (!string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return false;
            var host = parsed.Host.ToLowerInvariant();
            return host == "thalovant.com" || host.EndsWith(".thalovant.com", StringComparison.Ordinal);
        }

        /// <summary>
        /// The authorization code out of the redirect the browser came back
        /// with, or <c>null</c> when it is not an answer to this attempt.
        /// </summary>
        /// <remarks>
        /// <c>null</c> rather than an exception on a state mismatch, a missing
        /// code, or an <c>error=</c> response: all three mean "do not
        /// continue", and a caller that handles them alike cannot accidentally
        /// treat one of them as success.
        /// </remarks>
        public string? CodeFrom(string redirect)
        {
            if (string.IsNullOrEmpty(redirect)) return null;
            var mark = redirect.IndexOf('?');
            if (mark < 0 || mark == redirect.Length - 1) return null;
            string? state = null;
            string? code = null;
            foreach (var pair in redirect.Substring(mark + 1).Split('&'))
            {
                var split = pair.IndexOf('=');
                if (split <= 0) continue;
                var name = Uri.UnescapeDataString(pair.Substring(0, split));
                var value = Uri.UnescapeDataString(pair.Substring(split + 1));
                if (name == "state") state = value;
                else if (name == "code") code = value;
            }
            if (state != State) return null;
            return string.IsNullOrEmpty(code) ? null : code;
        }

        private static string RandomUrlSafe(int byteCount)
        {
            var raw = new byte[byteCount];
#if NET8_0_OR_GREATER
            RandomNumberGenerator.Fill(raw);
#else
            using var generator = RandomNumberGenerator.Create();
            generator.GetBytes(raw);
#endif
            return Base64Url(raw);
        }

        private static string Base64Url(byte[] raw) =>
            Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
