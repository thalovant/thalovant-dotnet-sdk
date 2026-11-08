using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Thalovant
{
    /// <summary>How <see cref="ThalovantControlPlane.InteractiveLoginAsync"/> signs a person in.</summary>
    public enum LoginMethod
    {
        /// <summary>
        /// Through the browser when this machine can show one
        /// (<see cref="BrowserSignIn.CanOpenBrowser"/>) and the API offers it;
        /// with a device login otherwise.
        /// </summary>
        Auto = 0,

        /// <summary>Through the browser, whatever the machine. A configuration failure is thrown, never turned into a device login.</summary>
        Browser = 1,

        /// <summary>With a device login; the browser sign-in configuration is never requested.</summary>
        Device = 2,
    }

    /// <summary>Options for <see cref="ThalovantControlPlane.BrowserLoginAsync"/>.</summary>
    public sealed class BrowserLoginOptions
    {
        /// <summary>
        /// Scopes the token carries. Empty strings are dropped; none (or only
        /// empty ones) leaves <c>scope</c> out, and the API grants its default,
        /// <c>hubs:read clients:write</c>. Admin and unknown scopes are refused
        /// when the person approves.
        /// </summary>
        public IReadOnlyList<string>? Scopes { get; set; }

        /// <summary>
        /// What the consent page and the token show, at most
        /// <see cref="BrowserSignIn.MaxDeviceNameLength"/> code points. Null or
        /// empty is <see cref="BrowserSignIn.DefaultDeviceName()"/>
        /// (<c>thalovant-dotnet-sdk on &lt;hostname&gt;</c>).
        /// </summary>
        public string? DeviceName { get; set; }

        /// <summary>
        /// Whether to open the system browser at the consent page. The URL is
        /// printed as well (or handed to <see cref="Prompt"/>), because opening a
        /// browser can fail without a word. False only shows it.
        /// </summary>
        public bool OpenBrowser { get; set; } = true;

        /// <summary>How long to wait for the browser to come back. Defaults to <see cref="BrowserSignIn.DefaultTimeout"/> (300 seconds).</summary>
        public TimeSpan Timeout { get; set; } = BrowserSignIn.DefaultTimeout;

        /// <summary>
        /// Receives the consent page's URL and takes the place of the printed
        /// prompt. It does not stop the browser from opening.
        /// </summary>
        public Action<string>? Prompt { get; set; }

        /// <summary>
        /// Opens a URL in a browser, in place of the platform's default
        /// (<c>ShellExecute</c> on Windows, <c>open</c> on macOS, <c>xdg-open</c>
        /// elsewhere). It runs on a background thread, and what it throws is
        /// ignored: the URL has already been shown.
        /// </summary>
        public Action<string>? Opener { get; set; }

        /// <summary>Where the prompt is printed; the console when null. A test hook.</summary>
        internal TextWriter? Output { get; set; }
    }

    /// <summary>Options for <see cref="ThalovantControlPlane.InteractiveLoginAsync"/>.</summary>
    public sealed class InteractiveLoginOptions
    {
        /// <summary>How to sign in; <see cref="LoginMethod.Auto"/> by default.</summary>
        public LoginMethod Method { get; set; } = LoginMethod.Auto;

        /// <summary>Scopes the token carries; none asks for the API's default.</summary>
        public IReadOnlyList<string>? Scopes { get; set; }

        /// <summary>
        /// Names this machine on the consent page (a device login's
        /// <c>client_name</c>). Null or empty is
        /// <see cref="BrowserSignIn.DefaultDeviceName()"/>.
        /// </summary>
        public string? DeviceName { get; set; }

        /// <summary>Whether to open the system browser, for either method. The URL or the code is printed as well.</summary>
        public bool OpenBrowser { get; set; } = true;

        /// <summary>
        /// How long to wait. Null is each method's own default: 300 seconds for
        /// the browser, 15 minutes for a device login.
        /// </summary>
        public TimeSpan? Timeout { get; set; }

        /// <summary>
        /// Opens a URL in a browser, in place of the platform's default. A
        /// device login hands it its verification page too.
        /// </summary>
        public Action<string>? Opener { get; set; }

        /// <summary>The environment the browser rule reads, in place of this process's. A test hook.</summary>
        internal IReadOnlyDictionary<string, string>? Environment { get; set; }

        /// <summary>The platform the browser rule reads, in place of this machine's. A test hook.</summary>
        internal string? Platform { get; set; }

        /// <summary>Where the browser prompt is printed; the console when null. A test hook.</summary>
        internal TextWriter? Output { get; set; }
    }

    /// <summary>
    /// Where a program signs in through the browser:
    /// <c>GET /v1/auth/native/config</c>, read by
    /// <see cref="ThalovantControlPlane.BrowserLoginAsync"/>.
    /// </summary>
    /// <remarks>
    /// The API reads it from its own settings, so a staging or self-hosted API
    /// sends the browser to its own dashboard. The answer's
    /// <c>loopback_redirect_uri</c> is never used: the redirect is fixed
    /// (<see cref="BrowserSignIn.LoopbackRedirectUri"/>).
    /// </remarks>
    public sealed class NativeSignInConfig
    {
        private NativeSignInConfig(string authorizationEndpoint, string tokenEndpoint, string sdkClientId, string? deviceAuthorizationEndpoint)
        {
            AuthorizationEndpoint = authorizationEndpoint;
            TokenEndpoint = tokenEndpoint;
            SdkClientId = sdkClientId;
            DeviceAuthorizationEndpoint = deviceAuthorizationEndpoint;
        }

        /// <summary>The dashboard page the browser opens.</summary>
        public string AuthorizationEndpoint { get; }

        /// <summary>The path on the API the code is exchanged at.</summary>
        public string TokenEndpoint { get; }

        /// <summary>The registered app the program signs in as (<see cref="BrowserSignIn.SdkClientId"/> on the hosted API).</summary>
        public string SdkClientId { get; }

        /// <summary>The device login's path, when the answer names one. Informational.</summary>
        public string? DeviceAuthorizationEndpoint { get; }

        /// <summary>
        /// Reads the configuration, refusing one a browser sign-in cannot safely
        /// use with <see cref="FormatException"/>.
        /// </summary>
        /// <remarks>
        /// Unusable: a payload that is not an object; an
        /// <c>authorization_endpoint</c> that is not https (or http on
        /// <c>localhost</c>, <c>127.0.0.1</c> or <c>::1</c>), has no host, carries
        /// credentials, a query (or a bare <c>?</c>), a fragment, white space,
        /// control characters or a port no browser can open; a
        /// <c>token_endpoint</c> that is not a path on the API (one leading
        /// <c>/</c>, not <c>//</c>, no <c>?</c>, <c>#</c>, <c>\</c> or white
        /// space); an <c>sdk_client_id</c> that is not 1-64 characters of
        /// <c>[A-Za-z0-9._-]</c>; or <c>code_challenge_methods_supported</c>
        /// without <c>S256</c>. Other members are ignored.
        /// </remarks>
        public static NativeSignInConfig FromPayload(JsonNode? payload)
        {
            if (!(payload is JsonObject body))
            {
                throw new FormatException("The browser sign-in configuration is not an object.");
            }
            var authorization = Text(body["authorization_endpoint"]);
            var token = Text(body["token_endpoint"]);
            var clientId = Text(body["sdk_client_id"]);
            if (authorization is null || !UsableAuthorizationEndpoint(authorization))
            {
                throw new FormatException("The browser sign-in configuration has no usable authorization_endpoint.");
            }
            if (token is null || !UsableTokenEndpoint(token))
            {
                throw new FormatException("The browser sign-in configuration has no usable token_endpoint.");
            }
            if (clientId is null || !UsableClientId(clientId))
            {
                throw new FormatException("The browser sign-in configuration has no usable sdk_client_id.");
            }
            if (!(body["code_challenge_methods_supported"] is JsonArray methods) || !methods.Any(method => Text(method) == "S256"))
            {
                throw new FormatException("The browser sign-in configuration does not offer S256.");
            }
            var device = Text(body["device_authorization_endpoint"]);
            return new NativeSignInConfig(authorization, token, clientId, string.IsNullOrEmpty(device) ? null : device);
        }

        private static string? Text(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static bool Unsafe(char character) =>
            char.IsWhiteSpace(character) || character < 32 || (character >= 127 && character <= 159);

        /// <summary>
        /// https, or http on a loopback host while developing; a host; no
        /// credentials, query, fragment, white space or control characters; a
        /// port from 0 to 65535 if any. Split as Python's <c>urlsplit</c> splits it.
        /// </summary>
        private static bool UsableAuthorizationEndpoint(string value)
        {
            if (value.Length == 0 || value.Any(Unsafe) || value.IndexOf('?') >= 0 || value.IndexOf('#') >= 0)
            {
                return false;
            }
            var colon = value.IndexOf(':');
            if (colon <= 0)
            {
                return false;
            }
            var scheme = value.Substring(0, colon).ToLowerInvariant();
            if (scheme != "https" && scheme != "http")
            {
                return false;
            }
            var rest = value.Substring(colon + 1);
            if (!rest.StartsWith("//", StringComparison.Ordinal))
            {
                return false; // No authority, so no host.
            }
            var authority = rest.Substring(2);
            var slash = authority.IndexOf('/');
            var netloc = slash < 0 ? authority : authority.Substring(0, slash);
            if (netloc.IndexOf('@') >= 0)
            {
                return false; // https://evil.example@dash.thalovant.com reads as one host and goes to another.
            }
            var open = netloc.IndexOf('[');
            var close = netloc.IndexOf(']');
            if ((open < 0) != (close < 0))
            {
                return false;
            }
            string host;
            string port;
            if (open >= 0)
            {
                if (open > 0)
                {
                    return false; // Nothing may come before a bracket.
                }
                var bracketed = netloc.Substring(1);
                var end = bracketed.IndexOf(']');
                if (end < 0)
                {
                    return false;
                }
                host = bracketed.Substring(0, end);
                var after = bracketed.Substring(end + 1);
                if (after.Length > 0 && after[0] != ':')
                {
                    return false;
                }
                port = after.Length > 0 ? after.Substring(1) : "";
                if (!BracketedHost(host))
                {
                    return false;
                }
            }
            else
            {
                var portColon = netloc.IndexOf(':');
                host = portColon < 0 ? netloc : netloc.Substring(0, portColon);
                port = portColon < 0 ? "" : netloc.Substring(portColon + 1);
            }
            if (host.Length == 0 || !UsablePort(port))
            {
                return false;
            }
            if (scheme == "https")
            {
                return true;
            }
            var lowered = host.ToLowerInvariant();
            return lowered == "localhost" || lowered == "127.0.0.1" || lowered == "::1";
        }

        /// <summary>An IPv6 literal, or an IPvFuture one (<c>v1.x</c>), as the brackets of a URL may hold.</summary>
        private static bool BracketedHost(string host)
        {
            if (host.StartsWith("v", StringComparison.Ordinal))
            {
                var dot = host.IndexOf('.');
                return dot > 1 && dot < host.Length - 1 && host.Substring(1, dot - 1).All(IsHex);
            }
            return IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
        }

        private static bool IsHex(char character) =>
            (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f') || (character >= 'A' && character <= 'F');

        /// <summary>None, or ASCII digits naming 0 to 65535.</summary>
        private static bool UsablePort(string port)
        {
            if (port.Length == 0)
            {
                return true;
            }
            if (!port.All(character => character >= '0' && character <= '9'))
            {
                return false;
            }
            var digits = port.TrimStart('0');
            return digits.Length <= 5 && (digits.Length == 0 || int.Parse(digits, CultureInfo.InvariantCulture) <= 65535);
        }

        /// <summary>A path on the API: one leading slash, no query, fragment, backslash or white space.</summary>
        private static bool UsableTokenEndpoint(string value) =>
            value.StartsWith("/", StringComparison.Ordinal)
            && !value.StartsWith("//", StringComparison.Ordinal)
            && !value.Any(character => character == '?' || character == '#' || character == '\\' || Unsafe(character));

        private static bool UsableClientId(string value) =>
            value.Length >= 1 && value.Length <= 64
            && value.All(character => (character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9') || character == '.' || character == '_' || character == '-');
    }

    /// <summary>
    /// The pieces browser sign-in is made of -- the authorization code grant
    /// with PKCE (RFC 7636, S256 only) on a loopback redirect (RFC 8252
    /// section 7.3) -- each of which the shared vectors check on its own.
    /// </summary>
    /// <remarks>
    /// <see cref="ThalovantControlPlane.BrowserLoginAsync"/> puts them
    /// together. The browser that approves hands the code to a listener on its
    /// own computer, so a sign-in link forwarded to somebody else gives the
    /// sender nothing unless that person also sends back the address their
    /// browser was redirected to. A device code, read off one screen and typed
    /// into another, needs no such second step. PKCE binds the code to the
    /// verifier this process holds; it does not stop a forwarded callback
    /// address.
    /// </remarks>
    public static class BrowserSignIn
    {
        /// <summary>The SDKs' own registered app; <c>GET /v1/auth/native/config</c> names it as <c>sdk_client_id</c>.</summary>
        public const string SdkClientId = "thalovant-sdk";

        /// <summary>The one path the loopback listener answers.</summary>
        public const string LoopbackCallbackPath = "/callback";

        /// <summary>The longest <c>device_name</c>, in Unicode code points: the API refuses a longer one.</summary>
        public const int MaxDeviceNameLength = 120;

        /// <summary>The name this SDK gives itself in <see cref="DefaultDeviceName()"/>.</summary>
        public const string SdkDeviceLabel = "thalovant-dotnet-sdk";

        /// <summary>How long a browser sign-in waits for the browser to come back: 300 seconds.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(300);

        /// <summary>What is printed when the browser is opened; <c>{url}</c> is the consent page.</summary>
        internal const string OpeningPrompt = "Opening your browser to sign in to Thalovant. If it does not open, visit:\n{url}";

        /// <summary>What is printed when it is not.</summary>
        internal const string NotOpeningPrompt = "To sign in to Thalovant, visit:\n{url}";

        private const string HexDigits = "0123456789ABCDEF";

        /// <summary>A PKCE verifier: 64 random bytes in base64url without padding (86 characters).</summary>
        public static string NewVerifier() => NativeSignIn.NewVerifier();

        /// <summary>The S256 challenge for a verifier: <c>base64url(sha256(ascii(verifier)))</c> without padding.</summary>
        public static string ChallengeFor(string verifier) => NativeSignIn.ChallengeFor(verifier);

        /// <summary>
        /// An OAuth <c>state</c>: 24 random bytes (192 bits) in base64url without
        /// padding. It ties the answer the browser brings back to the request
        /// that sent it; the listener compares it in constant time.
        /// </summary>
        public static string NewState()
        {
            var raw = new byte[24];
            using (var generator = RandomNumberGenerator.Create())
            {
                generator.GetBytes(raw);
            }
            return Convert.ToBase64String(raw).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        /// <summary>
        /// <c>http://127.0.0.1:&lt;port&gt;/callback</c>, the one redirect a
        /// program is answered on: the IP literal, never <c>localhost</c>; that
        /// path; no query, no trailing slash. The API matches it exactly and only
        /// for an unprivileged port, which a port bound as port 0 always is; any
        /// other port throws <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        public static string LoopbackRedirectUri(int port)
        {
            if (port < 1024 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), port, "A loopback redirect needs a port from 1024 to 65535.");
            }
            return "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + LoopbackCallbackPath;
        }

        /// <summary>
        /// The address a browser sign-in opens:
        /// <c>{authorizationEndpoint}?response_type=code&amp;client_id=…&amp;redirect_uri=…&amp;code_challenge=…&amp;code_challenge_method=S256&amp;state=…&amp;scope=…&amp;device_name=…</c>,
        /// in that order.
        /// </summary>
        /// <remarks>
        /// Every value is percent-encoded as RFC 3986 has it: <c>A-Z a-z 0-9 - . _ ~</c>
        /// stay, every other byte of the UTF-8 form is <c>%XX</c> in upper-case hex,
        /// a space is <c>%20</c>. Empty (and null) strings among
        /// <paramref name="scopes"/> are dropped and the rest joined by one space;
        /// <c>scope</c> is left out when nothing is left. <paramref name="deviceName"/>
        /// is cut to <see cref="MaxDeviceNameLength"/> code points, never inside
        /// one, and left out when empty.
        /// </remarks>
        public static string AuthorizationUrl(
            string authorizationEndpoint,
            string redirectUri,
            string codeChallenge,
            string state,
            IEnumerable<string?>? scopes = null,
            string? deviceName = null,
            string clientId = SdkClientId)
        {
            var parameters = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("response_type", "code"),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("redirect_uri", redirectUri),
                new KeyValuePair<string, string>("code_challenge", codeChallenge),
                // S256 only: `plain` puts the verifier in the browser.
                new KeyValuePair<string, string>("code_challenge_method", "S256"),
                new KeyValuePair<string, string>("state", state),
            };
            var scope = string.Join(" ", (scopes ?? Enumerable.Empty<string?>()).Where(value => !string.IsNullOrEmpty(value)));
            if (scope.Length > 0)
            {
                parameters.Add(new KeyValuePair<string, string>("scope", scope));
            }
            if (!string.IsNullOrEmpty(deviceName))
            {
                parameters.Add(new KeyValuePair<string, string>("device_name", CutToCodePoints(deviceName!, MaxDeviceNameLength)));
            }
            return authorizationEndpoint + "?" + string.Join("&", parameters.Select(pair => pair.Key + "=" + PercentEncode(pair.Value)));
        }

        /// <summary><c>thalovant-dotnet-sdk on &lt;hostname&gt;</c>: what the consent page and the token show by default.</summary>
        public static string DefaultDeviceName() => DefaultDeviceName(Dns.GetHostName);

        /// <summary>The default device name, with the host name read from <paramref name="hostName"/>. A test seam.</summary>
        internal static string DefaultDeviceName(Func<string?> hostName)
        {
            string host;
            try
            {
                host = (hostName() ?? "").Trim();
            }
            catch (Exception)
            {
                host = "";
            }
            var name = host.Length > 0 ? SdkDeviceLabel + " on " + host : SdkDeviceLabel;
            return CutToCodePoints(name, MaxDeviceNameLength);
        }

        /// <summary>
        /// Whether this machine can show a browser to the person signing in.
        /// </summary>
        /// <remarks>
        /// Never in CI (<c>CI</c> set) or over SSH (<c>SSH_CONNECTION</c> or
        /// <c>SSH_TTY</c> set): the browser would open on another machine, or
        /// nowhere. Otherwise yes on <c>macos</c> and <c>windows</c>; anywhere
        /// else -- Linux, the BSDs, Android, iOS -- only with a display
        /// (<c>DISPLAY</c> or <c>WAYLAND_DISPLAY</c>). A variable counts as set
        /// when it has a non-empty value: <c>CI=false</c> is set, an empty
        /// <c>DISPLAY</c> is not. <paramref name="environment"/> and
        /// <paramref name="platform"/> default to this process's.
        /// </remarks>
        public static bool CanOpenBrowser(IReadOnlyDictionary<string, string>? environment = null, string? platform = null)
        {
            bool Present(string name)
            {
                string? value;
                if (environment is null)
                {
                    value = System.Environment.GetEnvironmentVariable(name);
                }
                else
                {
                    environment.TryGetValue(name, out value);
                }
                return !string.IsNullOrEmpty(value);
            }

            if (Present("CI") || Present("SSH_CONNECTION") || Present("SSH_TTY"))
            {
                return false;
            }
            var system = platform ?? PlatformName();
            if (system == "macos" || system == "windows")
            {
                return true;
            }
            return Present("DISPLAY") || Present("WAYLAND_DISPLAY");
        }

        /// <summary>
        /// This machine as the browser rule names it: <c>windows</c>,
        /// <c>macos</c>, <c>linux</c>, <c>android</c>, <c>ios</c>, <c>freebsd</c>
        /// or <c>unix</c>.
        /// </summary>
        /// <remarks>
        /// Android and iOS are named before the desktop they resemble. Neither is
        /// <c>macos</c> or <c>windows</c>, so the browser rule asks them for a
        /// display variable, as the porting brief has it, and a phone sets none:
        /// <see cref="LoginMethod.Auto"/> picks a device login there, and an app
        /// on either signs in through its own flow. A Darwin without
        /// <c>/usr/bin/open</c> is not a Mac (iOS under Unity reports OSX).
        /// </remarks>
        internal static string PlatformName()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID")))
            {
                return "android";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS")) || RuntimeInformation.IsOSPlatform(OSPlatform.Create("TVOS"))
                || RuntimeInformation.IsOSPlatform(OSPlatform.Create("WATCHOS")) || RuntimeInformation.IsOSPlatform(OSPlatform.Create("MACCATALYST")))
            {
                return "ios";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "windows";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return File.Exists("/usr/bin/open") ? "macos" : "ios";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "linux";
            }
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("FREEBSD")))
            {
                return "freebsd";
            }
            return "unix";
        }

        /// <summary>What is printed in place of a prompt callback, line breaks as <c>\n</c>.</summary>
        internal static string PromptText(string url, bool openBrowser) =>
            (openBrowser ? OpeningPrompt : NotOpeningPrompt).Replace("{url}", url);

        /// <summary>
        /// Opens <paramref name="url"/> on a background thread: a console browser
        /// can hold the call until it exits, and the listener has to be answering
        /// by then. Whatever it throws is ignored; the URL was already shown.
        /// </summary>
        internal static void OpenInBackground(string url, Action<string>? opener)
        {
            var open = opener ?? OpenWithSystemBrowser;
            var thread = new Thread(() =>
            {
                try
                {
                    open(url);
                }
                catch (Exception)
                {
                    // Browser availability is best-effort; the URL was shown.
                }
            })
            {
                IsBackground = true,
                Name = "thalovant-browser",
            };
            thread.Start();
        }

        /// <summary>
        /// The platform's default browser: <c>ShellExecute</c> on Windows (never
        /// <c>cmd /c start</c>, whose command line the <c>&amp;</c> in the query
        /// would split), <c>open</c> on macOS, <c>xdg-open</c> elsewhere. The URL
        /// is handed over exactly as built.
        /// </summary>
        internal static void OpenWithSystemBrowser(string url)
        {
            using (Process.Start(SystemBrowserStart(url)))
            {
            }
        }

        /// <summary>
        /// How <see cref="OpenWithSystemBrowser"/> starts the browser: the URL as
        /// the file to shell-execute on Windows, else as the one argument of
        /// <c>open</c> or <c>xdg-open</c>, with no shell in between.
        /// </summary>
        internal static ProcessStartInfo SystemBrowserStart(string url)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new ProcessStartInfo(url) { UseShellExecute = true };
            }
            var info = new ProcessStartInfo(RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "open" : "xdg-open") { UseShellExecute = false };
            info.ArgumentList.Add(url);
            return info;
        }

        /// <summary>RFC 3986 percent-encoding of the UTF-8 form, upper-case hex, a space as <c>%20</c>.</summary>
        internal static string PercentEncode(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var encoded = new StringBuilder(bytes.Length * 3);
            foreach (var octet in bytes)
            {
                if ((octet >= 'A' && octet <= 'Z') || (octet >= 'a' && octet <= 'z') || (octet >= '0' && octet <= '9')
                    || octet == '-' || octet == '.' || octet == '_' || octet == '~')
                {
                    encoded.Append((char)octet);
                }
                else
                {
                    encoded.Append('%').Append(HexDigits[octet >> 4]).Append(HexDigits[octet & 0x0F]);
                }
            }
            return encoded.ToString();
        }

        /// <summary>The first <paramref name="limit"/> Unicode code points: a surrogate pair is one, and is never split.</summary>
        internal static string CutToCodePoints(string value, int limit)
        {
            var index = 0;
            for (var count = 0; count < limit && index < value.Length; count++)
            {
                index += char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]) ? 2 : 1;
            }
            return index >= value.Length ? value : value.Substring(0, index);
        }
    }

    public sealed partial class ThalovantControlPlane
    {
        /// <summary>Where the API says how to sign a program in through the browser.</summary>
        internal const string NativeSignInConfigPath = "/v1/auth/native/config";

        /// <summary>
        /// Signs in through the browser and stores the API token: the
        /// authorization code grant with PKCE on a loopback redirect (RFC 8252).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reads <c>GET /v1/auth/native/config</c> (no auth), listens on
        /// <c>127.0.0.1</c> on a port the system picks, opens the browser at the
        /// dashboard's consent page, and waits for the browser to come back to
        /// <c>http://127.0.0.1:&lt;port&gt;/callback</c>. The browser that
        /// approves hands the code to its own computer, so a sign-in link sent to
        /// somebody else gives the sender nothing unless that person also sends
        /// back the address their browser was redirected to.
        /// </para>
        /// <para>
        /// The URL is printed as well as opened (<c>Opening your browser to sign
        /// in to Thalovant. If it does not open, visit:</c> and the URL), because
        /// opening a browser can fail without a word. Pass
        /// <see cref="BrowserLoginOptions.Prompt"/> to present it yourself: it then
        /// receives the URL in place of the print, and showing it is up to it.
        /// <see cref="BrowserLoginOptions.OpenBrowser"/> false only shows it.
        /// A tab that answers with another sign-in's <c>state</c> is turned away
        /// and the wait goes on.
        /// </para>
        /// <para>
        /// Returns what <see cref="LoginWithBrowserAsync"/> returns, and keeps the
        /// token exactly as a device login does (<see cref="AccessToken"/> and
        /// <see cref="TokenId"/>), so <see cref="RevokeApiTokenAsync"/> revokes it
        /// the same way. Denied in the browser throws
        /// <see cref="ThalovantDeviceAccessDeniedException"/>; no answer within
        /// the timeout throws <see cref="ThalovantDeviceCodeExpiredException"/>,
        /// both with no status; a code the API will not exchange throws
        /// <see cref="ThalovantApiException"/> with its status and detail
        /// (<c>invalid_grant</c>). An API without browser sign-in answers the
        /// configuration with a 404, thrown as a
        /// <see cref="ThalovantApiException"/>: <see cref="InteractiveLoginAsync"/>
        /// falls back to a device login for it. An http API that is not loopback
        /// is refused before anything is asked or opened. Neither the code, the
        /// verifier, the state nor the token appears in an exception.
        /// Cancelling closes the port.
        /// </para>
        /// </remarks>
        public async Task<DeviceLoginResult> BrowserLoginAsync(
            BrowserLoginOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new BrowserLoginOptions();
            // Before anything is asked or opened: a code and its verifier never
            // cross the network in cleartext.
            NativeSignIn.RequireSecureTokenExchange(ApiUrl);
            var config = await NativeSignInConfigAsync(cancellationToken).ConfigureAwait(false);
            return await RunBrowserLoginAsync(
                config, options.Scopes, options.DeviceName, options.OpenBrowser, options.Timeout,
                options.Prompt, options.Opener, options.Output, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Signs a person in the best way this machine allows, and stores the API
        /// token: through the browser (<see cref="BrowserLoginAsync"/>) where it
        /// can show one, with a device login (<see cref="LoginWithBrowserAsync"/>)
        /// otherwise.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="LoginMethod.Auto"/> signs in through the browser when this
        /// machine can show one (<see cref="BrowserSignIn.CanOpenBrowser"/>:
        /// never over SSH or in CI; on Linux only with a display) and the API
        /// answers <c>GET /v1/auth/native/config</c> with a usable configuration.
        /// A 404, any other failure, an unusable answer or a cleartext API URL
        /// means a device login instead, whose code can be approved from any
        /// browser; nothing has been opened yet, so that is not a downgrade.
        /// <see cref="LoginMethod.Browser"/> and <see cref="LoginMethod.Device"/>
        /// force one. Once the browser has been sent to the consent page, the
        /// browser sign-in's own outcome stands: it never turns into a device
        /// login.
        /// </para>
        /// <para>
        /// Returns the same <see cref="DeviceLoginResult"/> from both paths. A
        /// device login's own timeout still throws
        /// <see cref="ThalovantTimeoutException"/>; only the browser throws
        /// <see cref="ThalovantDeviceCodeExpiredException"/> on one.
        /// </para>
        /// </remarks>
        public async Task<DeviceLoginResult> InteractiveLoginAsync(
            InteractiveLoginOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options ??= new InteractiveLoginOptions();
            var method = options.Method;
            if (method != LoginMethod.Auto && method != LoginMethod.Browser && method != LoginMethod.Device)
            {
                throw new ArgumentOutOfRangeException(nameof(options), method, "The login method must be Auto, Browser or Device.");
            }
            var name = string.IsNullOrEmpty(options.DeviceName) ? BrowserSignIn.DefaultDeviceName() : options.DeviceName!;
            NativeSignInConfig? config = null;
            if (method == LoginMethod.Browser)
            {
                NativeSignIn.RequireSecureTokenExchange(ApiUrl);
                config = await NativeSignInConfigAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (method == LoginMethod.Auto && BrowserSignIn.CanOpenBrowser(options.Environment, options.Platform))
            {
                try
                {
                    NativeSignIn.RequireSecureTokenExchange(ApiUrl);
                    config = await NativeSignInConfigAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ThalovantApiException)
                {
                    // An older API (404), or one that cannot say: the browser has
                    // not been opened, so a device login is not a downgrade.
                    config = null;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    config = null; // The configuration request timed out.
                }
            }
            if (config is null)
            {
                var device = new DeviceLoginOptions
                {
                    Scopes = options.Scopes,
                    ClientName = name,
                    OpenBrowser = options.OpenBrowser,
                    BrowserLauncher = options.Opener,
                };
                if (options.Timeout is TimeSpan deviceTimeout)
                {
                    device.Timeout = deviceTimeout;
                }
                return await LoginWithBrowserAsync(device, cancellationToken).ConfigureAwait(false);
            }
            return await RunBrowserLoginAsync(
                config, options.Scopes, name, options.OpenBrowser, options.Timeout ?? BrowserSignIn.DefaultTimeout,
                prompt: null, options.Opener, options.Output, cancellationToken).ConfigureAwait(false);
        }

        /// <summary><c>GET /v1/auth/native/config</c>: where to send the browser, where to exchange the code.</summary>
        private async Task<NativeSignInConfig> NativeSignInConfigAsync(CancellationToken cancellationToken)
        {
            var (statusCode, text, retryAfter) = await SendAsync("GET", NativeSignInConfigPath, null, null, auth: false, cancellationToken)
                .ConfigureAwait(false);
            if (statusCode == 404)
            {
                throw ThalovantApiException.FromResponse(
                    "This Thalovant API does not offer browser sign-in; sign in with a device login instead.",
                    statusCode, text, ThalovantApiException.ParseProblem(text));
            }
            if (statusCode < 200 || statusCode >= 300)
            {
                throw ApiError(statusCode, text).WithRetryAfterHeader(retryAfter);
            }
            try
            {
                // A 2xx the SDK cannot use is an error with no status, as a token
                // answer's is: the API did not refuse.
                return NativeSignInConfig.FromPayload(ThalovantApiException.ParseProblem(text));
            }
            catch (FormatException error)
            {
                throw new ThalovantApiException($"Thalovant API answered an unusable browser sign-in configuration. {error.Message}");
            }
        }

        /// <summary>One browser sign-in against <paramref name="config"/>: listen, open, wait, exchange, answer the tab.</summary>
        private async Task<DeviceLoginResult> RunBrowserLoginAsync(
            NativeSignInConfig config,
            IReadOnlyList<string>? scopes,
            string? deviceName,
            bool openBrowser,
            TimeSpan timeout,
            Action<string>? prompt,
            Action<string>? opener,
            TextWriter? output,
            CancellationToken cancellationToken)
        {
            NativeSignIn.RequireSecureTokenExchange(ApiUrl);
            var verifier = BrowserSignIn.NewVerifier();
            var state = BrowserSignIn.NewState();
            var listener = new LoopbackListener(state);
            // Bound before the URL is built: the redirect names the port.
            var port = listener.Start();
            try
            {
                var redirectUri = BrowserSignIn.LoopbackRedirectUri(port);
                var url = BrowserSignIn.AuthorizationUrl(
                    config.AuthorizationEndpoint,
                    redirectUri,
                    BrowserSignIn.ChallengeFor(verifier),
                    state,
                    scopes,
                    string.IsNullOrEmpty(deviceName) ? BrowserSignIn.DefaultDeviceName() : deviceName,
                    config.SdkClientId);
                PresentBrowserLogin(url, prompt, openBrowser, opener, output);
                var callback = await listener.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                if (callback is null)
                {
                    throw new ThalovantDeviceCodeExpiredException(
                        $"The browser sign-in was not completed within {timeout.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture)} seconds. "
                        + "Call BrowserLoginAsync() again to start a new one.");
                }
                if (callback.Outcome == LoopbackOutcome.Denied)
                {
                    throw new ThalovantDeviceAccessDeniedException("The sign-in request was denied in the browser.");
                }
                if (callback.Code is null)
                {
                    // The page's error is whatever the tab sent with the right
                    // state; it must not carry that state, or the verifier, out.
                    var code = ProblemCode(callback.Error);
                    if (code != null && (code.IndexOf(state, StringComparison.Ordinal) >= 0 || code.IndexOf(verifier, StringComparison.Ordinal) >= 0))
                    {
                        code = null;
                    }
                    throw new ThalovantApiException(
                        "The browser came back without a sign-in code" + (code is null ? "." : $" ({code})."),
                        errorCode: code);
                }
                DeviceLoginResult result;
                try
                {
                    result = await ExchangeBrowserCodeAsync(config, callback.Code, verifier, redirectUri, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ThalovantApiException error)
                {
                    // A code is single-use: retrying the exchange would only
                    // revoke the token a first exchange minted (RFC 9700). The
                    // copy thrown carries no inner exception, so the original --
                    // whose body may echo the code and the verifier -- does not
                    // ride along. A token the failed answer carries anyway is
                    // taken out with them.
                    var hidden = new List<string?> { callback.Code, verifier, state };
                    hidden.AddRange(TokensIn(error.Body));
                    throw WithoutSecrets(error, hidden.ToArray());
                }
                listener.Finish(true);
                return result;
            }
            finally
            {
                await listener.CloseAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// <c>POST {token_endpoint}</c> with exactly <c>code</c>,
        /// <c>code_verifier</c>, <c>client_id</c> and <c>redirect_uri</c>, and the
        /// token kept as a device login keeps it.
        /// </summary>
        /// <remarks>
        /// Read as a device-login token answer is: a 2xx whose body is not a JSON
        /// object, or has no <c>access_token</c>, is an error with no status --
        /// the API did not refuse, its answer could not be used -- and any other
        /// failure carries the api-errors fields (<c>invalid_grant</c> for every
        /// way an exchange fails).
        /// </remarks>
        private async Task<DeviceLoginResult> ExchangeBrowserCodeAsync(
            NativeSignInConfig config,
            string code,
            string verifier,
            string redirectUri,
            CancellationToken cancellationToken)
        {
            NativeSignIn.RequireSecureTokenExchange(ApiUrl);
            var body = new JsonObject
            {
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["client_id"] = config.SdkClientId,
                ["redirect_uri"] = redirectUri,
            };
            var (statusCode, text, retryAfter) = await SendAsync("POST", config.TokenEndpoint, body, null, auth: false, cancellationToken)
                .ConfigureAwait(false);
            if (statusCode < 200 || statusCode >= 300)
            {
                throw ApiError(statusCode, text).WithRetryAfterHeader(retryAfter);
            }
            var token = ThalovantApiException.ParseProblem(text);
            if (token is null)
            {
                throw new ThalovantApiException("Thalovant API returned an unexpected response shape.");
            }
            try
            {
                // Everything the result reads, read before the token is kept: a
                // value that cannot be read (a lone surrogate in a scope) must not
                // leave this client holding a token its caller was told failed.
                _ = DeviceLoginResult.FromToken(token, JsonUtil.GetString(token["access_token"]) ?? "");
            }
            catch (Exception error) when (error is InvalidOperationException || error is FormatException)
            {
                throw new ThalovantApiException("Thalovant API returned an unexpected response shape.");
            }
            return AcceptDeviceToken(token);
        }

        /// <summary>
        /// The token values a failed exchange's body carries anyway, under
        /// <c>access_token</c>, <c>refresh_token</c>, <c>id_token</c> or
        /// <c>token</c> at any depth, so that they can be taken out of the error.
        /// </summary>
        internal static IEnumerable<string> TokensIn(string? body)
        {
            var found = new List<string>();
            if (string.IsNullOrWhiteSpace(body))
            {
                return found;
            }
            void Visit(JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject())
                        {
                            if (property.Value.ValueKind == JsonValueKind.String
                                && (property.Name == "access_token" || property.Name == "refresh_token"
                                    || property.Name == "id_token" || property.Name == "token"))
                            {
                                var value = property.Value.GetString();
                                if (!string.IsNullOrEmpty(value)) found.Add(value!);
                            }
                            Visit(property.Value);
                        }
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in element.EnumerateArray())
                        {
                            Visit(item);
                        }
                        break;
                }
            }
            try
            {
                using var document = JsonDocument.Parse(body!);
                Visit(document.RootElement);
            }
            catch (Exception error) when (error is JsonException || error is InvalidOperationException)
            {
                // Not JSON, or a string that cannot be read: nothing to name.
            }
            return found;
        }

        /// <summary>Shows the sign-in URL (opening a browser can fail without a word), then opens it.</summary>
        private static void PresentBrowserLogin(string url, Action<string>? prompt, bool openBrowser, Action<string>? opener, TextWriter? output)
        {
            if (prompt is not null)
            {
                prompt(url);
            }
            else
            {
                var writer = output ?? Console.Out;
                writer.WriteLine(BrowserSignIn.PromptText(url, openBrowser).Replace("\n", writer.NewLine));
            }
            if (openBrowser)
            {
                BrowserSignIn.OpenInBackground(url, opener);
            }
        }

        /// <summary>The consent page's <c>error</c>, when it reads as a machine code (<c>[A-Za-z0-9_.:-]{1,80}</c>).</summary>
        private static string? ProblemCode(string? error)
        {
            if (string.IsNullOrEmpty(error) || error!.Length > 80)
            {
                return null;
            }
            return error.All(character => (character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9') || character == '_' || character == '.' || character == ':' || character == '-')
                ? error
                : null;
        }

        /// <summary>
        /// <paramref name="error"/>, with every one of <paramref name="hidden"/>
        /// taken out of its message, its body and its problem, keys included.
        /// </summary>
        /// <remarks>
        /// An API that echoes a request back (a FastAPI 422 lists each field's
        /// <c>input</c>) would otherwise hand the code and the verifier to
        /// whoever logs the error. A body that is JSON of any shape -- an object,
        /// an array, a bare string -- is read and written back from its clean
        /// values, so a secret spelled with JSON escapes cannot survive in the
        /// raw text either. The same error comes back when there is nothing to
        /// take out; otherwise a copy of the same kind, with no inner exception.
        /// </remarks>
        internal static ThalovantApiException WithoutSecrets(ThalovantApiException error, params string?[] hidden)
        {
            var values = hidden.Where(value => !string.IsNullOrEmpty(value)).Select(value => value!).Distinct(StringComparer.Ordinal)
                .OrderByDescending(value => value.Length).ToArray();
            // One pass over every secret at once, the longest first: replacing
            // them one after another would find a short one (a one-letter code)
            // inside the "[redacted]" an earlier one left.
            var secrets = values.Length == 0
                ? null
                : new System.Text.RegularExpressions.Regex(
                    string.Join("|", values.Select(System.Text.RegularExpressions.Regex.Escape)),
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(2));
            var changed = false;
            string Scrub(string text)
            {
                var clean = secrets is null ? text : secrets.Replace(text, "[redacted]");
                if (clean != text)
                {
                    changed = true;
                }
                return clean;
            }
            void Write(JsonElement element, Utf8JsonWriter writer)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Object:
                        writer.WriteStartObject();
                        foreach (var property in element.EnumerateObject())
                        {
                            writer.WritePropertyName(Scrub(property.Name));
                            Write(property.Value, writer);
                        }
                        writer.WriteEndObject();
                        break;
                    case JsonValueKind.Array:
                        writer.WriteStartArray();
                        foreach (var item in element.EnumerateArray())
                        {
                            Write(item, writer);
                        }
                        writer.WriteEndArray();
                        break;
                    case JsonValueKind.String:
                        writer.WriteStringValue(Scrub(element.GetString()!));
                        break;
                    default:
                        element.WriteTo(writer);
                        break;
                }
            }
            string ScrubBody(string text)
            {
                try
                {
                    using var document = JsonDocument.Parse(text);
                    var before = changed;
                    changed = false;
                    using var buffer = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        Write(document.RootElement, writer);
                    }
                    var rewritten = changed;
                    changed = before || rewritten;
                    // Written back only when a value was taken out: otherwise the
                    // body stays exactly as the API sent it.
                    return rewritten ? Encoding.UTF8.GetString(buffer.ToArray()) : Scrub(text);
                }
                catch (Exception failure) when (failure is JsonException || failure is InvalidOperationException)
                {
                    return Scrub(text); // Not JSON (or unreadable as it): plain text.
                }
            }

            var message = Scrub(error.Message);
            var source = error.Body ?? error.Problem?.ToJsonString();
            var body = source is null ? null : ScrubBody(source);
            if (!changed)
            {
                return error;
            }
            if (error.StatusCode is int statusCode)
            {
                var clean = body ?? "";
                return ThalovantApiException.FromResponse(message, statusCode, clean, ThalovantApiException.ParseProblem(clean))
                    .WithRetryAfterHeader(error.RetryAfter);
            }
            return new ThalovantApiException(message);
        }
    }
}
