using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Thalovant
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
        // ReadOnlyCollection, not an array behind an interface: a caller who
        // casts the interface back to string[] can otherwise rewrite the
        // defaults for every other caller in the process.
        public static readonly IReadOnlyList<string> DefaultScopes =
            new ReadOnlyCollection<string>(new[] { "hubs:read", "clients:read", "clients:write" });

        private NativeSignIn(string authorizationUrl, string state, string verifier, string redirectUri)
        {
            AuthorizationUrl = authorizationUrl;
            State = state;
            Verifier = verifier;
            RedirectUri = redirectUri;
        }

        /// <summary>Open this in a browser.</summary>
        public string AuthorizationUrl { get; }

        /// <summary>Proves the redirect answers <em>this</em> attempt, not a replayed one.</summary>
        public string State { get; }

        /// <summary>Never send this to the browser. Exchanged with the code, once.</summary>
        public string Verifier { get; }

        /// <summary>
        /// What this attempt asked the callback to arrive at. One that lands
        /// anywhere else is not this attempt's, however good its state looks.
        /// </summary>
        public string RedirectUri { get; }

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

            RequireSafeDashboard(dashboardUrl);
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
            return new NativeSignIn($"{dashboard}/authorize?{query}", state, verifier, redirectUri.Trim());
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
            // Reject embedded credentials: https://evil.test@dash.thalovant.com/
            // has a host that passes, and a URL somebody is about to be sent to
            // should not read as one host and resolve to another.
            if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;
            var host = parsed.Host.ToLowerInvariant();
            return host == "thalovant.com" || host.EndsWith(".thalovant.com", StringComparison.Ordinal);
        }

        /// <summary>
        /// The authorization code out of the redirect the browser came back
        /// with, or <c>null</c> when it is not an answer to this attempt.
        /// </summary>
        /// <remarks>
        /// <c>null</c> rather than an exception on a state mismatch, a missing
        /// code, or an <c>error=</c> response -- including one that also
        /// carries a code: all of those mean "do not
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
            var refused = false;
            foreach (var pair in redirect.Substring(mark + 1).Split('&'))
            {
                var split = pair.IndexOf('=');
                if (split <= 0) continue;
                var name = Uri.UnescapeDataString(pair.Substring(0, split));
                var value = Uri.UnescapeDataString(pair.Substring(split + 1));
                if (name == "state") state = value;
                else if (name == "code") code = value;
                // A refusal that also carries a code is still a refusal.
                // Checking only for a missing code accepted that pair and
                // would have started an exchange on a code the server had just
                // declined to issue.
                else if (name == "error") refused = true;
            }
            if (refused || state != State) return null;
            // The callback has to arrive where this attempt asked it to. State
            // proves the answer belongs to this request; the address proves it
            // came back to the app that made it.
            if (!SameTarget(redirect, RedirectUri)) return null;
            return string.IsNullOrEmpty(code) ? null : code;
        }

        /// <summary>
        /// Refuse to put an authorization code and its PKCE verifier on the
        /// wire in cleartext.
        /// </summary>
        /// <remarks>
        /// The control-plane URL accepts an <c>http</c> scheme -- a self-hosted
        /// or local deployment may legitimately be served that way -- and the
        /// request path hands whatever it is given to HttpClient without
        /// looking. Every other call that would leak over http leaks a bearer
        /// token the caller already holds; this one leaks the two secrets that
        /// are about to become one, and a code is exchangeable by whoever sees
        /// it first. Loopback is allowed: a request that never leaves the
        /// machine has no cleartext to observe.
        /// </remarks>
        public static void RequireSecureTokenExchange(string apiUrl)
        {
            if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var parsed))
            {
                throw new ThalovantApiException($"Thalovant API URL could not be read: {apiUrl}");
            }
            if (string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return;
            if (IsLoopback(parsed.Host)) return;
            throw new ThalovantApiException(
                $"Refusing to send an authorization code and PKCE verifier in cleartext to {parsed.Host}. " +
                "Use https, or a loopback address while developing.");
        }

        private static bool SameTarget(string got, string expected)
        {
            if (!Uri.TryCreate(got, UriKind.Absolute, out var a)) return false;
            if (!Uri.TryCreate(expected, UriKind.Absolute, out var b)) return false;
            return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
                && a.AbsolutePath.TrimEnd('/') == b.AbsolutePath.TrimEnd('/');
        }

        /// <summary>
        /// Refuse to hand the authorization request to a dashboard that cannot
        /// be trusted with it.
        /// </summary>
        /// <remarks>
        /// The request carries the challenge, the scopes and the state. A
        /// caller may point this at their own dashboard -- a self-hosted
        /// control plane is a real thing -- but not at a cleartext one, and not
        /// at one whose address reads as a different host than it resolves to.
        /// Loopback is allowed: it never leaves the machine.
        /// </remarks>
        public static void RequireSafeDashboard(string dashboardUrl)
        {
            if (!Uri.TryCreate(dashboardUrl, UriKind.Absolute, out var parsed))
            {
                throw new ThalovantApiException($"dashboardUrl is not a URL: {dashboardUrl}");
            }
            if (!string.IsNullOrEmpty(parsed.UserInfo))
            {
                throw new ThalovantApiException("dashboardUrl must not carry credentials.");
            }
            if (string.Equals(parsed.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(parsed.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                && IsLoopback(parsed.Host)) return;
            throw new ThalovantApiException(
                $"dashboardUrl must be https (or a loopback address while developing), not {dashboardUrl}");
        }

        /// <summary>Loopback, in both spellings a URI parser hands back for IPv6.</summary>
        internal static bool IsLoopback(string host)
        {
            var value = host.ToLowerInvariant();
            return value == "localhost" || value == "127.0.0.1" || value == "::1" || value == "[::1]";
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
