using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests;

/// <summary>
/// Browser sign-in beyond the shared vectors: what is printed and opened, the
/// generated secrets, the listener's own edges, and what never reaches an
/// exception.
/// </summary>
/// <remarks>
/// <see cref="BrowserLoginVectorTests"/> runs the contract every SDK shares;
/// these are the parts that are this SDK's own -- its threads, its console,
/// its cancellation -- or that are about what must <em>not</em> happen. No
/// test opens a real browser: each passes an opener, or none is asked for.
/// </remarks>
[Collection("Runtime deadlines")]
public sealed class BrowserLoginTests
{
    private const string ConfigBody =
        """{"authorization_endpoint":"https://dash.thalovant.com/authorize","token_endpoint":"/v1/auth/native/token","device_authorization_endpoint":"/v1/auth/device/authorize","sdk_client_id":"thalovant-sdk","loopback_redirect_uri":"http://127.0.0.1:{port}/callback","code_challenge_methods_supported":["S256"]}""";

    private const string TokenBody =
        """{"access_token":"tvpat_unit_token","token_type":"bearer","scopes":["hubs:read"],"expires_at":"2027-11-08T12:00:00Z","token_id":"tok-unit","ai_disclosure":"AI"}""";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>The configuration and token routes; the token route answers with the given function.</summary>
    private sealed class Api : IDisposable
    {
        private readonly TinyHttpServer _server;
        private readonly Func<TinyRequest, TinyResponse?>? _override;
        private readonly Func<JsonObject, TinyResponse> _token;

        internal Api(Func<JsonObject, TinyResponse>? token = null, Func<TinyRequest, TinyResponse?>? overrides = null)
        {
            _token = token ?? (_ => new TinyResponse(200, "application/json", TokenBody));
            _override = overrides;
            _server = new TinyHttpServer(Respond);
        }

        internal string Url => _server.Url;
        internal List<string> Requests { get; } = new();

        private TinyResponse Respond(TinyRequest request)
        {
            lock (Requests) Requests.Add($"{request.Method} {request.Path}");
            if (_override?.Invoke(request) is TinyResponse answer) return answer;
            if (request.Path == "/v1/auth/native/config") return new TinyResponse(200, "application/json", ConfigBody);
            if (request.Path == "/v1/auth/native/token") return _token(JsonNode.Parse(request.Body)!.AsObject());
            return new TinyResponse(599, "application/json", "{}");
        }

        public void Dispose() => _server.Dispose();
    }

    private static (int Port, string State, Dictionary<string, string> Query) Parts(string url)
    {
        var query = url.Substring(url.IndexOf('?') + 1).Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
        return (new Uri(query["redirect_uri"]).Port, query["state"], query);
    }

    private static (Task<string> Url, Action<string> HandOver) Opened()
    {
        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        return (opened.Task, url => opened.TrySetResult(url));
    }

    private static string Lines(string text, TextWriter writer) => text.Replace("\n", writer.NewLine) + writer.NewLine;

    // -- what the person sees -------------------------------------------------

    [Fact]
    public async Task TheUrlIsPrintedAndTheBrowserOpenedOffTheCallersThread()
    {
        using var api = new Api();
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var output = new StringWriter();
        var (opened, handOver) = Opened();
        string? openedOn = null;
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions
        {
            DeviceName = "unit",
            Output = output,
            Opener = url =>
            {
                openedOn = Thread.CurrentThread.Name;
                handOver(url);
            },
        });
        var url = await opened.WaitAsync(Patience);
        var (port, state, _) = Parts(url);
        Assert.Equal(200, (await BrowserTab.SendAsync(port, "GET", $"/callback?code=c&state={state}")).Status);
        var token = await task.WaitAsync(Patience);
        Assert.Equal("tvpat_unit_token", token.AccessToken);
        Assert.Equal("thalovant-browser", openedOn);
        Assert.Equal(Lines("Opening your browser to sign in to Thalovant. If it does not open, visit:\n" + url, output), output.ToString());
    }

    [Fact]
    public async Task WithoutOpenBrowserTheUrlIsOnlyShown()
    {
        using var api = new Api();
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var output = new StringWriter();
        var opened = 0;
        // Nobody comes back to the port, so the sign-in expires.
        await Assert.ThrowsAsync<ThalovantDeviceCodeExpiredException>(() => plane.BrowserLoginAsync(new BrowserLoginOptions
        {
            OpenBrowser = false,
            Timeout = TimeSpan.FromMilliseconds(200),
            Output = output,
            Opener = _ => Interlocked.Increment(ref opened),
        }));
        Assert.StartsWith(
            "To sign in to Thalovant, visit:" + output.NewLine + "https://dash.thalovant.com/authorize?response_type=code&",
            output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task ABrowserThatCannotOpenChangesNothing()
    {
        using var api = new Api();
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions
        {
            Prompt = handOver,
            Opener = _ => throw new InvalidOperationException("no runnable browser"),
        });
        var (port, state, _) = Parts(await opened.WaitAsync(Patience));
        await BrowserTab.SendAsync(port, "GET", $"/callback?code=c&state={state}");
        Assert.Equal("tok-unit", (await task.WaitAsync(Patience)).TokenId);
    }

    [Fact]
    public void TheDeviceNameDefaultsToThisSdkOnThisHost()
    {
        Assert.Equal("thalovant-dotnet-sdk on workstation", BrowserSignIn.DefaultDeviceName(() => "workstation"));
        Assert.Equal("thalovant-dotnet-sdk on workstation", BrowserSignIn.DefaultDeviceName(() => "  workstation\n"));
        Assert.Equal(120, BrowserSignIn.DefaultDeviceName(() => new string('h', 300)).Length);
        Assert.Equal("thalovant-dotnet-sdk", BrowserSignIn.DefaultDeviceName(() => ""));
        Assert.Equal("thalovant-dotnet-sdk", BrowserSignIn.DefaultDeviceName(() => null));
        Assert.Equal("thalovant-dotnet-sdk", BrowserSignIn.DefaultDeviceName(() => throw new SocketException()));
        // Cut by code points, never inside a surrogate pair: 24 + 95 + 1 of 3 emoji.
        var cut = BrowserSignIn.DefaultDeviceName(() => new string('x', 95) + "🎙🎙🎙");
        Assert.EndsWith("x🎙", cut, StringComparison.Ordinal);
        Assert.Equal(121, cut.Length);
        Assert.StartsWith("thalovant-dotnet-sdk on ", BrowserSignIn.DefaultDeviceName(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDefaultDeviceNameReachesTheConsentPage()
    {
        using var api = new Api();
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions
        {
            OpenBrowser = false,
            Prompt = handOver,
            Timeout = TimeSpan.FromMilliseconds(200),
        });
        var (_, _, query) = Parts(await opened.WaitAsync(Patience));
        var error = await Assert.ThrowsAsync<ThalovantDeviceCodeExpiredException>(() => task);
        Assert.Contains("within 0.2 seconds", error.Message, StringComparison.Ordinal);
        Assert.Null(error.StatusCode);
        Assert.Equal(BrowserSignIn.DefaultDeviceName(), query["device_name"]);
        Assert.False(query.ContainsKey("scope"));
    }

    // -- the secrets ----------------------------------------------------------

    [Fact]
    public void EveryVerifierAndStateIsFreshAndWellFormed()
    {
        var verifiers = Enumerable.Range(0, 50).Select(_ => BrowserSignIn.NewVerifier()).ToHashSet();
        var states = Enumerable.Range(0, 50).Select(_ => BrowserSignIn.NewState()).ToHashSet();
        Assert.Equal(50, verifiers.Count);
        Assert.Equal(50, states.Count);
        Assert.All(verifiers, value => Assert.Matches(new Regex(@"\A[A-Za-z0-9._~-]{43,128}\z"), value));
        // 24 random bytes: 192 bits, well over the 128 the contract asks for.
        Assert.All(states, value => Assert.Matches(new Regex(@"\A[A-Za-z0-9_-]{32}\z"), value));
    }

    [Fact]
    public async Task AnEchoedCodeAndVerifierNeverReachTheError()
    {
        JsonObject? sent = null;
        using var api = new Api(body =>
        {
            // A validation error lists each field's input, as FastAPI's 422 does,
            // and this one puts a secret in a key too.
            sent = body;
            var detail = new JsonArray(body.Select(pair => (JsonNode?)new JsonObject
            {
                ["loc"] = new JsonArray("body", pair.Key),
                ["msg"] = "bad",
                ["input"] = pair.Value!.DeepClone(),
            }).ToArray());
            var problem = new JsonObject { ["detail"] = detail, [$"seen:{(string)body["code"]!}"] = "x" };
            return new TinyResponse(422, "application/json", problem.ToJsonString());
        });
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver });
        var (port, state, _) = Parts(await opened.WaitAsync(Patience));
        var answer = await BrowserTab.SendAsync(port, "GET", $"/callback?code=secret-code-value&state={state}");
        Assert.Equal(200, answer.Status);
        Assert.Contains("Sign-in was not completed.", answer.Body, StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<ThalovantApiException>(() => task);
        Assert.Equal(typeof(ThalovantApiException), error.GetType());
        Assert.Equal(422, error.StatusCode);
        var shown = string.Join("\n", error.Message, error.ToString(), error.Body, error.Problem?.ToJsonString(), error.Detail);
        foreach (var secret in new[] { (string)sent!["code"]!, (string)sent["code_verifier"]!, state })
        {
            Assert.DoesNotContain(secret, shown, StringComparison.Ordinal);
        }
        Assert.Contains("[redacted]", error.Problem!.ToJsonString(), StringComparison.Ordinal);
        Assert.True(error.Problem!.ContainsKey("seen:[redacted]"));
        // Nothing rides along: no inner exception holding the unredacted original.
        Assert.Null(error.InnerException);
        Assert.Null(plane.AccessToken);
    }

    [Fact]
    public void ARedactedErrorIsAlwaysANewOneWithNothingRidingAlong()
    {
        // Even with nothing to take out: the original can carry the transport's
        // exception, and that can keep the request.
        var error = ThalovantControlPlane.ApiError(400, """{"detail":"invalid_grant"}""");
        var clean = ThalovantControlPlane.WithoutSecrets(error, "nc_code", "verifier", null, "");
        Assert.NotSame(error, clean);
        Assert.Equal(400, clean.StatusCode);
        Assert.Equal("invalid_grant", clean.Detail);
        Assert.Null(clean.InnerException);
        var unreachable = new ThalovantApiUnreachableException("Thalovant API request failed: reset", new System.Net.Http.HttpRequestException("body: nc_code"));
        var bare = ThalovantControlPlane.WithoutSecrets(unreachable, "nc_code");
        Assert.IsType<ThalovantApiUnreachableException>(bare);
        Assert.Null(bare.InnerException);
        Assert.Null(bare.StatusCode);
    }

    [Fact]
    public void ScrubbingIsOnePassLongestFirstAndLeavesItsMarkerAlone()
    {
        var redactor = new ThalovantControlPlane.Redactor(new[] { "abc", "abcdef", "c" });
        Assert.Equal("xx-[redacted]-[redacted]-[redacted]", redactor.Scrub("xx-abc-abcdef-[redacted]"));
        var once = redactor.ScrubBody("""{"seen:abcdef":["c",{"deep":"abc"}]}""");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"seen:[redacted]":["[redacted]",{"deep":"[redacted]"}]}"""), JsonNode.Parse(once)));
        Assert.Equal(once, redactor.ScrubBody(once));
        Assert.Equal("nothing hidden", new ThalovantControlPlane.Redactor(Array.Empty<string>()).Scrub("nothing hidden"));
        // A body with nothing to take out is kept exactly as sent.
        Assert.Equal("{ \"detail\" : \"invalid_grant\" }", redactor.ScrubBody("{ \"detail\" : \"invalid_grant\" }"));
    }

    [Fact]
    public void EverySecretIsWrittenTheSameWayPercentEncoded()
    {
        // The verifier and the state are base64url and the API mints codes
        // URL-safe: nothing in one needs encoding, so a URL or a form repeats
        // it as it is, and the redaction finds it.
        for (var round = 0; round < 200; round++)
        {
            foreach (var secret in new[] { BrowserSignIn.NewVerifier(), BrowserSignIn.NewState() })
            {
                Assert.Equal(secret, BrowserSignIn.PercentEncode(secret));
                Assert.Equal(secret, Uri.EscapeDataString(secret));
                var echoed = $"echo: {BrowserSignIn.PercentEncode(secret)} and {System.Net.WebUtility.UrlEncode(secret)}";
                Assert.DoesNotContain(secret, new ThalovantControlPlane.Redactor(new[] { secret }).Scrub(echoed), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ARedactedErrorKeepsItsKindAndItsFields()
    {
        var error = ThalovantControlPlane.ApiError(401, """{"detail":"bad nc_code","code":"x","retry_after_seconds":3}""");
        var clean = ThalovantControlPlane.WithoutSecrets(error, "nc_code");
        Assert.IsType<ThalovantAuthenticationException>(clean);
        Assert.Equal(401, clean.StatusCode);
        Assert.Equal("x", clean.ErrorCode);
        Assert.Equal("bad [redacted]", clean.Detail);
        Assert.Equal(TimeSpan.FromSeconds(3), clean.RetryAfter);
        Assert.DoesNotContain("nc_code", clean.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("nc_code", clean.Message, StringComparison.Ordinal);
        var bare = ThalovantControlPlane.WithoutSecrets(new ThalovantApiException("no nc_code here"), "nc_code");
        Assert.Null(bare.StatusCode);
        Assert.Equal("no [redacted] here", bare.Message);
    }

    [Fact]
    public void AShortSecretNeverCutsIntoAnotherOnesMarker()
    {
        // One pass, the longest first: a one-letter code must not be found
        // again inside the "[redacted]" a longer secret left.
        var clean = ThalovantControlPlane.WithoutSecrets(new ThalovantApiException("tvpat_secret then c"), "c", "tvpat_secret");
        Assert.Equal("[redacted] then [redacted]", clean.Message);
    }

    [Fact]
    public async Task ACleartextApiIsRefusedBeforeAnythingIsAskedOrOpened()
    {
        var opened = 0;
        var plane = new ThalovantControlPlane("http://control.example.test");
        var browser = await Assert.ThrowsAsync<ThalovantApiException>(() => plane.BrowserLoginAsync(new BrowserLoginOptions
        {
            OpenBrowser = false,
            Opener = _ => Interlocked.Increment(ref opened),
        }));
        Assert.Contains("cleartext", browser.Message, StringComparison.Ordinal);
        var forced = await Assert.ThrowsAsync<ThalovantApiException>(() => plane.InteractiveLoginAsync(new InteractiveLoginOptions
        {
            Method = LoginMethod.Browser,
            Opener = _ => Interlocked.Increment(ref opened),
        }));
        Assert.Contains("cleartext", forced.Message, StringComparison.Ordinal);
        // Auto counts a cleartext API as "no browser sign-in" and turns to a
        // device login, which refuses to send its request over http on its own.
        var auto = await Assert.ThrowsAsync<ThalovantApiException>(() => plane.InteractiveLoginAsync(new InteractiveLoginOptions
        {
            Environment = new Dictionary<string, string> { ["DISPLAY"] = ":0" },
            Platform = "linux",
            Opener = _ => Interlocked.Increment(ref opened),
            Output = TextWriter.Null,
        }));
        Assert.DoesNotContain("cleartext", auto.Message, StringComparison.Ordinal);
        Assert.Contains("HTTPS", auto.Message, StringComparison.Ordinal);
        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task InteractiveLoginRefusesAMethodItDoesNotKnow()
    {
        var plane = new ThalovantControlPlane(apiUrl: $"http://127.0.0.1:{LoopbackApi.ClosedPort()}");
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            plane.InteractiveLoginAsync(new InteractiveLoginOptions { Method = (LoginMethod)42 }));
        Assert.Contains("Auto", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultsMatchTheReference()
    {
        var browser = new BrowserLoginOptions();
        Assert.True(browser.OpenBrowser);
        Assert.Equal(TimeSpan.FromSeconds(300), browser.Timeout);
        Assert.Null(browser.Scopes);
        Assert.Null(browser.DeviceName);
        Assert.Null(browser.Prompt);
        var interactive = new InteractiveLoginOptions();
        Assert.Equal(LoginMethod.Auto, interactive.Method);
        Assert.True(interactive.OpenBrowser);
        // Each method's own default: 300 s for the browser, 15 minutes for a device login.
        Assert.Null(interactive.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(15), new DeviceLoginOptions().Timeout);
    }

    [Fact]
    public async Task AnErrorTheTabSendsNeverCarriesTheStateOut()
    {
        using var api = new Api();
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver });
        var (port, state, _) = Parts(await opened.WaitAsync(Patience));
        // A tab with the right state that sends that state back as its error.
        var answer = await BrowserTab.SendAsync(port, "GET", $"/callback?state={state}&error={state}");
        Assert.Contains("Sign-in was not completed.", answer.Body, StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<ThalovantApiException>(() => task.WaitAsync(Patience));
        Assert.Null(error.StatusCode);
        Assert.Null(error.ErrorCode);
        Assert.DoesNotContain(state, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(new[] { "GET /v1/auth/native/config" }, api.Requests);
    }

    [Fact]
    public async Task ATokenAFailedExchangeCarriesNeverReachesTheError()
    {
        using var api = new Api(_ => new TinyResponse(400, "application/json",
            """{"detail":"invalid_grant, issued tvpat_leaked_token","access_token":"tvpat_leaked_token","nested":{"refresh_token":"tvprt_leaked"}}"""));
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver });
        var (port, state, _) = Parts(await opened.WaitAsync(Patience));
        await BrowserTab.SendAsync(port, "GET", $"/callback?code=c&state={state}");
        var error = await Assert.ThrowsAsync<ThalovantApiException>(() => task.WaitAsync(Patience));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid_grant, issued [redacted]", error.Detail);
        var shown = string.Join("\n", error.Message, error.ToString(), error.Body, error.Problem?.ToJsonString(), error.Detail);
        Assert.DoesNotContain("tvpat_leaked_token", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("tvprt_leaked", shown, StringComparison.Ordinal);
        Assert.Null(plane.AccessToken);
    }

    [Fact]
    public async Task AnEchoThatIsNotAnObjectIsCleanedToo()
    {
        // Every character spelled as a JSON escape, so a plain text search finds nothing.
        static string Escaped(string text) => string.Concat(text.Select(character => $"\\u{(int)character:x4}"));
        string? verifier = null;
        using var api = new Api(body =>
        {
            verifier = (string)body["code_verifier"]!;
            return new TinyResponse(422, "application/json", $"[\"{Escaped((string)body["code"]!)}\",\"{Escaped(verifier)}\"]");
        });
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver });
        var (port, state, _) = Parts(await opened.WaitAsync(Patience));
        await BrowserTab.SendAsync(port, "GET", $"/callback?code=secret-code-value&state={state}");
        var error = await Assert.ThrowsAsync<ThalovantApiException>(() => task.WaitAsync(Patience));
        Assert.Equal(422, error.StatusCode);
        Assert.DoesNotContain(Escaped("secret-code-value"), error.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(verifier!, error.Body!, StringComparison.Ordinal);
        Assert.Equal(new[] { "[redacted]", "[redacted]" }, JsonNode.Parse(error.Body!)!.AsArray().Select(item => (string)item!).ToArray());
    }

    [Fact]
    public async Task AnAnswerThatCannotBeReadKeepsNoToken()
    {
        // A lone surrogate in a scope cannot be read as a string.
        using var api = new Api(_ => new TinyResponse(200, "application/json",
            """{"access_token":"tvpat_surrogate","token_type":"bearer","scopes":["\uD800"],"token_id":"tok-surrogate"}"""));
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver });
        var (port, state, _) = Parts(await opened.WaitAsync(Patience));
        var answer = await BrowserTab.SendAsync(port, "GET", $"/callback?code=c&state={state}");
        Assert.Contains("Sign-in was not completed.", answer.Body, StringComparison.Ordinal);
        var error = await Assert.ThrowsAsync<ThalovantApiException>(() => task.WaitAsync(Patience));
        Assert.Null(error.StatusCode);
        // The caller was told it failed, so the client must not hold the token either.
        Assert.Null(plane.AccessToken);
        Assert.Null(plane.TokenId);
    }

    [Fact]
    public async Task TheTokenPathTheConfigurationNamesIsTheOneUsed()
    {
        var config = JsonNode.Parse(ConfigBody)!.AsObject();
        config["token_endpoint"] = "/v2/sign-in/exchange";
        using var api = new Api(overrides: request => request.Path switch
        {
            "/v1/auth/native/config" => new TinyResponse(200, "application/json", config.ToJsonString()),
            "/v2/sign-in/exchange" => new TinyResponse(200, "application/json", TokenBody),
            _ => null,
        });
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver });
        var (port, state, _) = Parts(await opened.WaitAsync(Patience));
        await BrowserTab.SendAsync(port, "GET", $"/callback?code=c&state={state}");
        Assert.Equal("tok-unit", (await task.WaitAsync(Patience)).TokenId);
        Assert.Equal(new[] { "GET /v1/auth/native/config", "POST /v2/sign-in/exchange" }, api.Requests);
    }

    [Fact]
    public async Task AMessageCutToLengthKeepsNoPartOfASecret()
    {
        string? verifier = null;
        // The verifier starts ten characters before the message's 200-character cut.
        using var api = new Api(body =>
        {
            verifier = (string)body["code_verifier"]!;
            return new TinyResponse(400, "application/json", new JsonObject { ["detail"] = new string('a', 190) + verifier }.ToJsonString());
        });
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver });
        var (port, state, _) = Parts(await opened.WaitAsync(Patience));
        await BrowserTab.SendAsync(port, "GET", $"/callback?code=c&state={state}");
        var error = await Assert.ThrowsAsync<ThalovantApiException>(() => task.WaitAsync(Patience));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal(new string('a', 190) + "[redacted]", error.Detail);
        var prefix = verifier!.Substring(0, 10);
        foreach (var shown in new[] { error.Message, error.ToString(), error.Detail!, error.Problem!.ToJsonString(), error.Body! })
        {
            Assert.DoesNotContain(prefix, shown, StringComparison.Ordinal);
        }
    }

    /// <summary>A transport the test scripts: the configuration, then whatever <paramref name="exchange"/> does.</summary>
    private sealed class ScriptedHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly Func<System.Net.Http.HttpRequestMessage, Task<System.Net.Http.HttpResponseMessage>> _exchange;

        internal ScriptedHandler(Func<System.Net.Http.HttpRequestMessage, Task<System.Net.Http.HttpResponseMessage>> exchange) => _exchange = exchange;

        internal List<string> Requests { get; } = new();

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.RequestUri!.AbsolutePath == "/v1/auth/native/config")
            {
                return Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent(ConfigBody, Encoding.UTF8, "application/json"),
                });
            }
            return _exchange(request);
        }
    }

    [Fact]
    public async Task ATransportFailureDuringTheExchangeCarriesNothingOfTheRequest()
    {
        // A transport whose own exception repeats the request body.
        string? sent = null;
        var handler = new ScriptedHandler(async request =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            throw new System.Net.Http.HttpRequestException("connection reset after " + sent);
        });
        var plane = new ThalovantControlPlane(handler, apiUrl: "https://api.example.test");
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver });
        var (port, state, _) = Parts(await opened.WaitAsync(Patience));
        await BrowserTab.SendAsync(port, "GET", $"/callback?code=transport-code&state={state}");
        var error = await Assert.ThrowsAsync<ThalovantApiUnreachableException>(() => task.WaitAsync(Patience));
        Assert.Null(error.StatusCode);
        Assert.Null(error.InnerException);
        var verifier = (string)JsonNode.Parse(sent!)!["code_verifier"]!;
        foreach (var secret in new[] { "transport-code", verifier, state })
        {
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        }
        Assert.Contains("[redacted]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheExchangeIsWrittenOnceEvenIfThePipelineResendsIt()
    {
        // A pipeline that writes the request body twice, as a retry on a failed
        // reused connection would: presenting a code twice revokes the token the
        // first presentation minted, so the second write must fail instead.
        var written = 0;
        var handler = new ScriptedHandler(async request =>
        {
            await request.Content!.CopyToAsync(new MemoryStream());
            Interlocked.Increment(ref written);
            await request.Content.CopyToAsync(new MemoryStream());
            Interlocked.Increment(ref written);
            return new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(TokenBody) };
        });
        var plane = new ThalovantControlPlane(handler, apiUrl: "https://api.example.test");
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver });
        var (port, state, _) = Parts(await opened.WaitAsync(Patience));
        await BrowserTab.SendAsync(port, "GET", $"/callback?code=nc_resend_code&state={state}");
        var error = await Assert.ThrowsAsync<ThalovantApiException>(() => task.WaitAsync(Patience));
        Assert.Null(error.StatusCode);
        Assert.Contains("sent once", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, written);
        Assert.Null(plane.AccessToken);
    }

    [Theory]
    [InlineData("/https://elsewhere.example/v1/x")]
    [InlineData("/elsewhere.example:443/v1/x")]
    [InlineData("https://elsewhere.example/v1/x")]
    [InlineData("//elsewhere.example/v1/x")]
    [InlineData("/{collector}/v1/x")]
    public void NoRequestLeavesTheApiOrigin(string path)
    {
        // The path is appended to the API's address, so none of these can name
        // another authority here; whatever comes out is on the API's origin.
        using var collector = new TinyHttpServer(_ => new TinyResponse(599, "application/json", "{}"));
        var plane = new ThalovantControlPlane(apiUrl: "https://api.example.test", accessToken: "tvpat_must_stay_home");
        using var request = plane.BuildRequest("POST", path.Replace("{collector}", collector.Url), new JsonObject { ["code"] = "c" });
        Assert.True(ThalovantControlPlane.SameOrigin(request.RequestUri!.AbsoluteUri, plane.ApiUrl), request.RequestUri.AbsoluteUri);
        Assert.Equal("api.example.test", request.RequestUri.Host);
    }

    [Fact]
    public void ARequestOffTheApiOriginIsRefusedWithNoStatus()
    {
        // The guard every request passes through, BuildRequest included.
        foreach (var url in new[] { "https://elsewhere.example/v1/x", "http://api.example.test/v1/x", "https://api.example.test:8443/v1/x" })
        {
            var error = Assert.Throws<ThalovantApiException>(() => ThalovantControlPlane.RequireApiOrigin(new Uri(url), "https://api.example.test/"));
            Assert.Null(error.StatusCode);
            Assert.Contains("origin", error.Message, StringComparison.Ordinal);
        }
        ThalovantControlPlane.RequireApiOrigin(new Uri("https://API.example.test:443/v1/x"), "https://api.example.test/");
    }

    [Fact]
    public async Task AnOrdinaryApiPathStillReachesTheApi()
    {
        using var api = new Api();
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var config = await plane.RequestObjectAsync("GET", "/v1/auth/native/config", auth: false);
        Assert.Equal("thalovant-sdk", (string)config["sdk_client_id"]!);
        Assert.Equal(new[] { "GET /v1/auth/native/config" }, api.Requests);
    }

    // -- the listener's own edges ---------------------------------------------

    [Fact]
    public async Task CancellingTheSignInClosesThePort()
    {
        using var api = new Api();
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        using var cancel = new CancellationTokenSource();
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver }, cancel.Token);
        var (port, _, _) = Parts(await opened.WaitAsync(Patience));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Patience));
        Assert.True(await BrowserTab.RefusedAsync(port));
        Assert.Null(plane.AccessToken);
    }

    [Fact]
    public async Task AConnectionThatSendsNothingDoesNotHoldTheSignIn()
    {
        // A browser opens connections it never uses; the sign-in must not wait on them.
        using var api = new Api();
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver });
        var (port, state, _) = Parts(await opened.WaitAsync(Patience));
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, port);
        var clock = Stopwatch.StartNew();
        Assert.Equal(200, (await BrowserTab.SendAsync(port, "GET", $"/callback?code=c&state={state}")).Status);
        await task.WaitAsync(Patience);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(0.9), $"the sign-in took {clock.Elapsed}");
    }

    [Fact]
    public async Task ARequestThatIsNotOneIs400AndTheWaitGoesOn()
    {
        using var api = new Api();
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver });
        var (port, state, _) = Parts(await opened.WaitAsync(Patience));
        Assert.Equal(400, (await BrowserTab.SendAsync(port, "", "", raw: Encoding.ASCII.GetBytes("hello\r\n\r\n"))).Status);
        var tooBig = Encoding.ASCII.GetBytes("GET /callback?code=" + new string('a', LoopbackListener.MaxRequestHead + 10) + " HTTP/1.1\r\n\r\n");
        Assert.Equal(400, (await BrowserTab.SendAsync(port, "", "", raw: tooBig)).Status);
        Assert.False(task.IsCompleted);
        Assert.Equal(200, (await BrowserTab.SendAsync(port, "GET", $"/callback?code=c&state={state}")).Status);
        Assert.Equal("tvpat_unit_token", (await task.WaitAsync(Patience)).AccessToken);
    }

    [Fact]
    public async Task ASecondAnswerAfterTheFirstIsTurnedAway()
    {
        var listener = new LoopbackListener("the-state-of-this-sign-in");
        var port = listener.Start();
        try
        {
            // Both connected before the first is accepted, so the second still
            // reaches the listener after the port has stopped listening.
            using var first = new TcpClient();
            using var second = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, port);
            await second.ConnectAsync(IPAddress.Loopback, port);
            await UntilAsync(() => listener.ConnectionCount == 2, "the listener never took both connections in");
            var request = Encoding.ASCII.GetBytes("GET /callback?code=c&state=the-state-of-this-sign-in HTTP/1.1\r\n\r\n");
            await first.GetStream().WriteAsync(request);
            var accepted = await listener.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.NotNull(accepted);
            Assert.Equal("c", accepted!.Code);
            Assert.True(await BrowserTab.RefusedAsync(port));
            await second.GetStream().WriteAsync(request);
            Assert.StartsWith("HTTP/1.1 400 ", await ReadToEndAsync(second), StringComparison.Ordinal);
            listener.Finish(true);
            Assert.Contains("Signed in to Thalovant.", await ReadToEndAsync(first), StringComparison.Ordinal);
        }
        finally
        {
            await listener.CloseAsync();
        }
    }

    /// <summary>Waits for <paramref name="condition"/>, failing after <see cref="Patience"/>.</summary>
    private static async Task UntilAsync(Func<bool> condition, string because)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Patience, because);
            await Task.Delay(10);
        }
    }

    private static async Task<string> ReadToEndAsync(TcpClient client)
    {
        var data = new MemoryStream();
        await client.GetStream().CopyToAsync(data).WaitAsync(Patience);
        return Encoding.UTF8.GetString(data.ToArray());
    }

    [Fact]
    public async Task AFloodOfSilentConnectionsCannotCrowdOutTheCallback()
    {
        var listener = new LoopbackListener("s");
        var port = listener.Start();
        var silent = new List<TcpClient>();
        try
        {
            for (var index = 0; index < LoopbackListener.MaxConnections + 8; index++)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                silent.Add(client);
            }
            // Never more than the bound: the oldest silent ones were closed to make room.
            await UntilAsync(() => listener.ConnectionCount == LoopbackListener.MaxConnections, "the listener did not settle at its bound");
            Assert.True(await ClosedByTheListenerAsync(silent[0]), "the oldest silent connection was not closed");
            var answer = BrowserTab.SendAsync(port, "GET", "/callback?code=c&state=s");
            Assert.NotNull(await listener.WaitAsync(Patience, CancellationToken.None));
            listener.Finish(true);
            Assert.Contains("Signed in to Thalovant.", (await answer.WaitAsync(Patience)).Body, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var client in silent) client.Dispose();
            await listener.CloseAsync();
        }
    }

    /// <summary>Whether the other end closed this connection: a read that ends, or one that is reset.</summary>
    private static async Task<bool> ClosedByTheListenerAsync(TcpClient client)
    {
        try
        {
            return await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(Patience) == 0;
        }
        catch (IOException)
        {
            return true;
        }
    }

    [Fact]
    public async Task TheWaitIsNeverCutShort()
    {
        var listener = new LoopbackListener("s");
        listener.Start();
        try
        {
            var clock = Stopwatch.StartNew();
            Assert.Null(await listener.WaitAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None));
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(250), $"woke after {clock.Elapsed}");
        }
        finally
        {
            await listener.CloseAsync();
        }
    }

    [Fact]
    public async Task WaitingOnAListenerThatWasNeverStartedIsAnError()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LoopbackListener("s").WaitAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task AConnectionThatClosesOrBreaksBeforeItsAnswerChangesNothing()
    {
        var listener = new LoopbackListener("the-state");
        var port = listener.Start();
        try
        {
            // Gone before sending a whole request head.
            using (var gone = new TcpClient())
            {
                await gone.ConnectAsync(IPAddress.Loopback, port);
                await gone.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /callback?code=c&st"));
            }
            // Reset half way through one.
            using (var broken = new TcpClient())
            {
                await broken.ConnectAsync(IPAddress.Loopback, port);
                await broken.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /callback?code=c&state=the-state HTTP/1.1\r\n"));
                broken.Client.LingerState = new LingerOption(true, 0);
            }
            Assert.Null(await listener.WaitAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None));
            // Neither was accepted, so this sign-in's own answer still is.
            var tab = BrowserTab.SendAsync(port, "GET", "/callback?code=c&state=the-state");
            Assert.NotNull(await listener.WaitAsync(Patience, CancellationToken.None));
            listener.Finish(true);
            var answer = await tab.WaitAsync(Patience);
            Assert.Equal(200, answer.Status);
            Assert.Contains("Signed in to Thalovant.", answer.Body, StringComparison.Ordinal);
        }
        finally
        {
            await listener.CloseAsync();
        }
    }

    [Fact]
    public async Task TheAcceptedTabWaitsAsLongAsTheSignInTakes()
    {
        // The page says how the sign-in ended, so it is written once it has,
        // however long the exchange took: no deadline of its own.
        var listener = new LoopbackListener("the-state");
        var port = listener.Start();
        try
        {
            var clock = Stopwatch.StartNew();
            var tab = BrowserTab.SendAsync(port, "GET", "/callback?code=c&state=the-state");
            Assert.NotNull(await listener.WaitAsync(Patience, CancellationToken.None));
            await Task.Delay(300);
            Assert.False(tab.IsCompleted);
            listener.Finish(true);
            Assert.Contains("Signed in to Thalovant. You can close this tab.", (await tab.WaitAsync(Patience)).Body, StringComparison.Ordinal);
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            await listener.CloseAsync();
        }
    }

    [Fact]
    public async Task ClosingTheListenerAnswersATabThatIsStillWaiting()
    {
        var listener = new LoopbackListener("the-state");
        var port = listener.Start();
        var tab = BrowserTab.SendAsync(port, "GET", "/callback?code=c&state=the-state");
        Assert.NotNull(await listener.WaitAsync(Patience, CancellationToken.None));
        await listener.CloseAsync();
        Assert.Contains("Sign-in was not completed.", (await tab.WaitAsync(Patience)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHeadRequestGetsTheHeadersAndNoBody()
    {
        var listener = new LoopbackListener("the-state");
        var port = listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HEAD /callback?code=c&state=the-state HTTP/1.1\r\n\r\n"));
            var raw = await ReadToEndAsync(client);
            var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var head = raw.Substring(0, split);
            Assert.StartsWith("HTTP/1.1 404 ", head, StringComparison.Ordinal);
            Assert.Equal("", raw.Substring(split + 4));
            Assert.Contains("Content-Type: text/html; charset=utf-8", head, StringComparison.Ordinal);
            // What a GET would have carried.
            var length = int.Parse(Regex.Match(head, @"Content-Length: (\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(LoopbackListener.ResponseBytes(404, LoopbackListener.NotFoundText).Length - LoopbackListener.ResponseBytes(404, LoopbackListener.NotFoundText, headOnly: true).Length, length);
            Assert.True(length > 0);
        }
        finally
        {
            await listener.CloseAsync();
        }
    }

    [Theory]
    [InlineData("oversized head")]
    [InlineData("unread body")]
    public async Task AnEarlyAnswerIsNotLostToAReset(string kind)
    {
        // Closing with bytes unread resets the connection, and macOS then drops
        // the answer the browser had not read; Linux shows the reset.
        var request = kind == "oversized head"
            ? Encoding.ASCII.GetBytes("GET /callback?code=" + new string('a', 600_000) + " HTTP/1.1\r\nHost: x\r\n\r\n")
            : Encoding.ASCII.GetBytes("POST /callback HTTP/1.1\r\nHost: x\r\nContent-Length: 600000\r\n\r\n" + new string('b', 600_000));
        var listener = new LoopbackListener("the-state");
        var port = listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            // Sent while the answer is read, as a browser does.
            var sending = Task.Run(async () =>
            {
                try { await stream.WriteAsync(request); } catch (IOException) { }
            });
            var data = new MemoryStream();
            Exception? ended = null;
            try
            {
                await stream.CopyToAsync(data).WaitAsync(Patience);
            }
            catch (IOException error)
            {
                ended = error;
            }
            await sending.WaitAsync(Patience);
            var line = Encoding.ASCII.GetString(data.ToArray()).Split("\r\n")[0];
            Assert.Contains(line, new[] { "HTTP/1.1 400 Bad Request", "HTTP/1.1 404 Not Found" });
            Assert.True(ended is null, $"the connection ended with {ended}");
        }
        finally
        {
            await listener.CloseAsync();
        }
    }

    [Fact]
    public async Task DrainingAConnectionIsBounded()
    {
        // A browser that never closes its side cannot keep a connection alive.
        var listener = new LoopbackListener("the-state") { DrainTimeout = TimeSpan.FromMilliseconds(200) };
        var port = listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /favicon.ico HTTP/1.1\r\n\r\n"));
            // The answer ends (the listener shut its side), and ours stays open.
            Assert.StartsWith("HTTP/1.1 404 ", await ReadToEndAsync(client), StringComparison.Ordinal);
            await Task.Delay(500);
            Assert.Equal(0, listener.ConnectionCount);
        }
        finally
        {
            await listener.CloseAsync();
        }
    }

    [Fact]
    public void TheDrainBoundsAreTheBriefs()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), LoopbackListener.DefaultDrainTimeout);
        Assert.Equal(1024 * 1024, LoopbackListener.DrainLimit);
        Assert.Equal(TimeSpan.FromSeconds(2), new LoopbackListener("s").DrainTimeout);
    }

    [Fact]
    public void AnAcceptedCallbackNeverShowsItsCode()
    {
        var (_, callback) = LoopbackListener.Classify("GET", "/callback?code=nc_secret_code&state=s", "s");
        Assert.Equal("nc_secret_code", callback!.Code);
        Assert.DoesNotContain("nc_secret_code", callback.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void QueryValuesAreDecodedOnceAsAFormDecodesThem()
    {
        // The edges Python's parse_qsl decides, which no vector pins.
        Assert.Equal(new[] { ("a", "1"), ("b", "2") }, LoopbackListener.ParseQuery("a=1&&b=2"));
        Assert.Equal(new[] { ("code", "") }, LoopbackListener.ParseQuery("code"));
        Assert.Equal(new[] { ("state", "s;code=1") }, LoopbackListener.ParseQuery("state=s;code=1"));
        Assert.Equal("%zz", LoopbackListener.FormDecode("%zz"));
        Assert.Equal(" x ", LoopbackListener.FormDecode("+x+"));
        Assert.Equal("é", LoopbackListener.FormDecode("%C3%A9"));
        Assert.Equal("�", LoopbackListener.FormDecode("%E9"));
        Assert.Equal("A%41", LoopbackListener.FormDecode("%41%2541"));
        Assert.Equal("éé", LoopbackListener.FormDecode("é%C3%A9"));
        // A + is a space, so a code with one must arrive as %2B.
        Assert.Equal("a b", LoopbackListener.Classify("GET", "/callback?state=s&code=a+b", "s").Callback!.Code);
        Assert.Equal(400, LoopbackListener.Classify("GET", "/callback?#state=s", "s").Status);
        Assert.Equal(404, LoopbackListener.Classify("GET", "/%63allback?state=s", "s").Status);
        Assert.Equal(404, LoopbackListener.Classify("GET", "http://127.0.0.1/callback?state=s", "s").Status);
        // Two errors are a failure; the first is kept only when it is an RFC 6749 code.
        var mixed = LoopbackListener.Classify("GET", "/callback?state=s&error=a&error=access_denied", "s").Callback!;
        Assert.Equal(LoopbackOutcome.Failed, mixed.Outcome);
        Assert.Null(mixed.Error);
        Assert.Equal("server_error", LoopbackListener.Classify("GET", "/callback?state=s&error=server_error&error=access_denied", "s").Callback!.Error);
        foreach (var code in LoopbackListener.OAuthErrors.Where(code => code != "access_denied"))
        {
            Assert.Equal(code, LoopbackListener.Classify("GET", $"/callback?state=s&error={code}", "s").Callback!.Error);
        }
        Assert.Equal(7, LoopbackListener.OAuthErrors.Count);
        Assert.Null(LoopbackListener.RequestLine(Encoding.ASCII.GetBytes("GET  /callback HTTP/1.1\r\n\r\n")));
        Assert.Null(LoopbackListener.RequestLine(Encoding.ASCII.GetBytes("GET /callback FTP/1\r\n\r\n")));
    }

    // -- the remaining edges --------------------------------------------------

    [Fact]
    public void ThisMachineIsNamedAsTheBrowserRuleNamesIt()
    {
        var expected = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : null;
        if (expected != null) Assert.Equal(expected, BrowserSignIn.PlatformName());
        // Neither Android nor iOS is macos or windows, so the rule asks for a
        // display variable (the porting brief maps Android to linux), and a
        // phone sets none: auto picks a device login there.
        Assert.False(BrowserSignIn.CanOpenBrowser(new Dictionary<string, string>(), "android"));
        Assert.False(BrowserSignIn.CanOpenBrowser(new Dictionary<string, string>(), "ios"));
    }

    [Theory]
    [InlineData("android")]
    [InlineData("ios")]
    public void APhoneFollowsTheRuleForEverywhereElse(string platform)
    {
        // As Linux and the BSDs do: only a display variable would make it a desktop.
        foreach (var display in new[] { "DISPLAY", "WAYLAND_DISPLAY" })
        {
            var desktop = new Dictionary<string, string> { [display] = ":0" };
            Assert.True(BrowserSignIn.CanOpenBrowser(desktop, platform));
            Assert.Equal(BrowserSignIn.CanOpenBrowser(desktop, "linux"), BrowserSignIn.CanOpenBrowser(desktop, platform));
        }
        Assert.False(BrowserSignIn.CanOpenBrowser(new Dictionary<string, string> { ["DISPLAY"] = "" }, platform));
    }

    [Fact]
    public void TheSystemBrowserGetsTheUrlAsBuiltWithNoShellInBetween()
    {
        // Built, never started: no test opens a real browser.
        var url = "https://dash.thalovant.com/authorize?response_type=code&client_id=thalovant-sdk&state=a%20b&device_name=x%26y";
        var info = BrowserSignIn.SystemBrowserStart(url);
        if (OperatingSystem.IsWindows())
        {
            // ShellExecute, never `cmd /c start`, whose command line the & would split.
            Assert.True(info.UseShellExecute);
            Assert.Equal(url, info.FileName);
            Assert.Empty(info.ArgumentList);
        }
        else
        {
            Assert.False(info.UseShellExecute);
            Assert.Equal(OperatingSystem.IsMacOS() ? "open" : "xdg-open", info.FileName);
            Assert.Equal(new[] { url }, info.ArgumentList);
            Assert.Equal("", info.Arguments);
        }
    }

    [Fact]
    public void TheBrowserRuleReadsThisProcessByDefault()
    {
        var here = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "CI", "SSH_CONNECTION", "SSH_TTY", "DISPLAY", "WAYLAND_DISPLAY" })
        {
            if (Environment.GetEnvironmentVariable(name) is string value) here[name] = value;
        }
        Assert.Equal(BrowserSignIn.CanOpenBrowser(here, BrowserSignIn.PlatformName()), BrowserSignIn.CanOpenBrowser());
    }

    [Theory]
    [InlineData("https://dash.thalovant.com/author\u0007ize")]
    [InlineData("https://dash.thalovant.com /authorize")]
    [InlineData("https://dash.thalovant.com/a ")]
    [InlineData("https://dash.thalovant.com:99999/authorize")]
    [InlineData("https://dash.thalovant.com:+1/authorize")]
    [InlineData("https://x:12:13/a")]
    [InlineData("https://[::1/authorize")]
    [InlineData("https://::1]/a")]
    [InlineData("https://[zz]/a")]
    [InlineData("https://a[::1]/a")]
    [InlineData("http://[::2]:5173/authorize")]
    [InlineData("https:dash.thalovant.com/a")]
    [InlineData("https:///a")]
    [InlineData("https://dash.thalovant.com/authorize?")]
    [InlineData("ftp://dash.thalovant.com/authorize")]
    [InlineData("")]
    // A host as written must be an address or a real hostname (each checked
    // against the reference's _usable_authorization_endpoint at 39e31d8).
    [InlineData("https://%/authorize")]
    [InlineData("https://dash_staging.thalovant.com/a")]
    [InlineData("https://DASH_X.com/a")]
    [InlineData("https://dash..thalovant.com/a")]
    [InlineData("https://dash.thalovant.com./a")]
    [InlineData("https://dash.thalovânt.com/a")]
    [InlineData("https://999.1.1.1/a")]
    [InlineData("https://127.1/a")]
    [InlineData("https://0x7f.0.0.1/a")]
    [InlineData("https://01.2.3.4/a")]
    [InlineData("https://1.2.3.4.5/a")]
    [InlineData("https://example.123/a")]
    [InlineData("https://[v1.x]/a")]
    [InlineData("https://[1.2.3.4]/a")]
    [InlineData("https://x\\y/a")]
    [InlineData("https://-dash.example/a")]
    [InlineData("https://dash-.example/a")]
    // Port 0, as written or padded: no browser can open it.
    [InlineData("https://dash.thalovant.com:0/a")]
    [InlineData("https://dash.thalovant.com:00/a")]
    public void AnAuthorizationEndpointNoBrowserCanOpenIsUnusable(string endpoint)
    {
        Assert.Throws<FormatException>(() => NativeSignInConfig.FromPayload(Config("authorization_endpoint", endpoint)));
    }

    [Theory]
    [InlineData("http://[::1]:5173/authorize")]
    [InlineData("http://LOCALHOST/x")]
    [InlineData("HTTPS://Dash.Thalovant.com/A")]
    [InlineData("https://dash.thalovant.com:/authorize")]
    [InlineData("https://dash.thalovant.com:0443/authorize")]
    [InlineData("https://dash.thalovant.com")]
    [InlineData("https://dash.thalovant.com/a@b")]
    [InlineData("https://xn--dsh-0na.example/a")]
    [InlineData("https://192.0.2.10/a")]
    [InlineData("https://[::ffff:1.2.3.4]/a")]
    [InlineData("https://123.example/a")]
    public void AnAuthorizationEndpointTheReferenceAcceptsIsUsable(string endpoint)
    {
        Assert.Equal(endpoint, NativeSignInConfig.FromPayload(Config("authorization_endpoint", endpoint)).AuthorizationEndpoint);
    }

    [Fact]
    public void AHostnameIsAtMost253CharactersOf63CharacterLabels()
    {
        string Endpoint(string host) => $"https://{host}/a";
        bool Usable(string host)
        {
            try
            {
                NativeSignInConfig.FromPayload(Config("authorization_endpoint", Endpoint(host)));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
        Assert.True(Usable(new string('a', 63) + ".example"));
        Assert.False(Usable(new string('a', 64) + ".example"));
        var label = new string('a', 63);
        Assert.True(Usable(string.Join(".", label, label, label, new string('a', 61))));   // 253
        Assert.False(Usable(string.Join(".", label, label, label, new string('a', 62))));  // 254
    }

    [Theory]
    [InlineData("/v1/auth/native/token#")]
    [InlineData("/v1/auth\\native/token")]
    [InlineData("/v1/auth/native/token ")]
    [InlineData("/v1/auth/native/\u0085token")]
    [InlineData("v1/auth/native/token")]
    // No colon anywhere, and every % followed by two hex digits.
    [InlineData("/v1:x")]
    [InlineData("/https://x/y")]
    [InlineData("/x:443/y")]
    [InlineData("/v1/%zz")]
    [InlineData("/v1/token%")]
    [InlineData("/v1/%7")]
    [InlineData("/v1/%G1")]
    public void ATokenEndpointThatIsNotAPathOnTheApiIsUnusable(string endpoint)
    {
        Assert.Throws<FormatException>(() => NativeSignInConfig.FromPayload(Config("token_endpoint", endpoint)));
    }

    [Theory]
    [InlineData("/v1/%74oken")]
    [InlineData("/v1/%2F")]
    public void ATokenEndpointWithWholePercentEscapesIsUsable(string endpoint)
    {
        Assert.Equal(endpoint, NativeSignInConfig.FromPayload(Config("token_endpoint", endpoint)).TokenEndpoint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("thalovant sdk")]
    [InlineData("thalovant/sdk")]
    public void AClientIdThatIsNotOneIsUnusable(string clientId)
    {
        Assert.Throws<FormatException>(() => NativeSignInConfig.FromPayload(Config("sdk_client_id", clientId)));
        Assert.Throws<FormatException>(() => NativeSignInConfig.FromPayload(Config("sdk_client_id", new string('a', 65))));
        Assert.Equal(new string('a', 64), NativeSignInConfig.FromPayload(Config("sdk_client_id", new string('a', 64))).SdkClientId);
    }

    [Fact]
    public void AConfigurationThatIsNotAnObjectIsUnusable()
    {
        Assert.Throws<FormatException>(() => NativeSignInConfig.FromPayload(new JsonArray("not", "an", "object")));
        Assert.Throws<FormatException>(() => NativeSignInConfig.FromPayload(null));
        var numbers = JsonNode.Parse(ConfigBody)!.AsObject();
        numbers["sdk_client_id"] = 42;
        Assert.Throws<FormatException>(() => NativeSignInConfig.FromPayload(numbers));
        var config = NativeSignInConfig.FromPayload(JsonNode.Parse(ConfigBody));
        Assert.Equal("/v1/auth/device/authorize", config.DeviceAuthorizationEndpoint);
    }

    private static JsonObject Config(string member, string value)
    {
        var config = JsonNode.Parse(ConfigBody)!.AsObject();
        config[member] = value;
        return config;
    }

    [Theory]
    [InlineData("config")]
    [InlineData("token")]
    public async Task AnAnswerThatIsNotJsonIsAnErrorWithNoStatus(string route)
    {
        using var api = new Api(overrides: request => request.Path == $"/v1/auth/native/{route}"
            ? new TinyResponse(200, "text/html", "<html>not json</html>")
            : null);
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var (opened, handOver) = Opened();
        var task = plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false, Prompt = handOver });
        if (route == "token")
        {
            var (port, state, _) = Parts(await opened.WaitAsync(Patience));
            Assert.Equal(200, (await BrowserTab.SendAsync(port, "GET", $"/callback?code=c&state={state}")).Status);
        }
        var error = await Assert.ThrowsAsync<ThalovantApiException>(() => task.WaitAsync(Patience));
        Assert.Null(error.StatusCode);
        Assert.Null(error.ErrorCode);
        Assert.Null(plane.AccessToken);
    }

    [Fact]
    public async Task AConfigurationFailureOtherThan404CarriesTheApiErrorFields()
    {
        using var api = new Api(overrides: request => new TinyResponse(503, "application/json", """{"detail":"Service Unavailable","code":"maintenance"}"""));
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var error = await Assert.ThrowsAsync<ThalovantApiException>(() => plane.BrowserLoginAsync(new BrowserLoginOptions { OpenBrowser = false }));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal("maintenance", error.ErrorCode);
        Assert.Equal("Service Unavailable", error.Detail);
        Assert.Equal(new[] { "GET /v1/auth/native/config" }, api.Requests);
    }
}
