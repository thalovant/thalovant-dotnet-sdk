using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests;

/// <summary>
/// Browser sign-in against <c>browser-login-vectors.json</c>, vendored byte for
/// byte from the Python reference's contracts/conformance.
/// </summary>
/// <remarks>
/// <para>
/// The pure sections (PKCE, the redirect, the authorization URL, the
/// configuration, the browser rule, what a callback means) run against the
/// functions that decide them; the callbacks run through a real listener too.
/// <c>sign_ins</c> and <c>chooser</c> run for real: a loopback API serves each
/// case's exchanges in order and checks every request the SDK sends over its
/// own HttpClient, and a raw HTTP client plays the browser against the port
/// the SDK bound.
/// </para>
/// <para>
/// What the SDK produced is recorded before it is compared, so the
/// conformance record is this SDK's output rather than a restatement of the
/// vectors. This SDK has one form (async), so that is the one recorded.
/// </para>
/// </remarks>
[Collection("Runtime deadlines")]
public sealed class BrowserLoginVectorTests
{
    internal static readonly JsonObject Browser =
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "browser-login-vectors.json")))!.AsObject();

    private const string SyntheticState = "Xx0-_yLq7m3Vw9ZtR2cKp4HdN8sFjB6a";

    private static TheoryData<string> Names(string section)
    {
        var names = new TheoryData<string>();
        foreach (var item in Browser[section]!.AsArray()) names.Add((string)item!["name"]!);
        return names;
    }

    public static TheoryData<string> PkceCases => Names("pkce");
    public static TheoryData<string> RedirectCases => Names("redirect_uris");
    public static TheoryData<string> UrlCases => Names("authorization_urls");
    public static TheoryData<string> ConfigCases => Names("config");
    public static TheoryData<string> EnvironmentCases => Names("environment");
    public static TheoryData<string> CallbackCases => Names("callbacks");
    public static TheoryData<string> SignInCases => Names("sign_ins");
    public static TheoryData<string> ChooserCases => Names("chooser");

    private static JsonObject Case(string section, string name) =>
        Browser[section]!.AsArray().Single(item => (string)item!["name"]! == name)!.AsObject();

    private static void Record(string section, string name, JsonNode produced) =>
        ConformanceRecord.Record("browser-login-vectors.json", $"{section}/{name}", produced.DeepClone());

    private static void Same(JsonNode expect, JsonNode produced, string name)
    {
        Assert.True(JsonNode.DeepEquals(expect, produced), $"{name}: produced {produced.ToJsonString()}, expected {expect.ToJsonString()}");
        Assert.Equal(ConformanceRecord.CanonicalDigest(expect), ConformanceRecord.CanonicalDigest(produced));
    }

    internal static Dictionary<string, string> Env(JsonNode? env) =>
        (env as JsonObject ?? new JsonObject()).ToDictionary(pair => pair.Key, pair => (string)pair.Value!, StringComparer.Ordinal);

    // -- the pure sections ----------------------------------------------------

    [Theory]
    [MemberData(nameof(PkceCases))]
    public void ThePkceChallengeIsTheVectors(string name)
    {
        var vector = Case("pkce", name);
        var produced = new JsonObject { ["challenge"] = BrowserSignIn.ChallengeFor((string)vector["verifier"]!) };
        Record("pkce", name, produced);
        Same(vector["expect"]!, produced, name);
    }

    [Theory]
    [MemberData(nameof(RedirectCases))]
    public void TheRedirectIsTheVectors(string name)
    {
        var vector = Case("redirect_uris", name);
        JsonObject produced;
        try
        {
            produced = new JsonObject { ["redirect_uri"] = BrowserSignIn.LoopbackRedirectUri(vector["port"]!.GetValue<int>()) };
        }
        catch (ArgumentOutOfRangeException)
        {
            produced = new JsonObject { ["error"] = "invalid_port" };
        }
        Record("redirect_uris", name, produced);
        Same(vector["expect"]!, produced, name);
    }

    [Theory]
    [MemberData(nameof(UrlCases))]
    public void TheAuthorizationUrlIsTheVectors(string name)
    {
        var vector = Case("authorization_urls", name);
        var given = vector["input"]!.AsObject();
        var scopes = given["scopes"] is JsonArray list ? list.Select(item => (string?)item).ToList() : null;
        var produced = new JsonObject
        {
            ["url"] = BrowserSignIn.AuthorizationUrl(
                (string)given["authorization_endpoint"]!,
                BrowserSignIn.LoopbackRedirectUri(given["port"]!.GetValue<int>()),
                (string)given["code_challenge"]!,
                (string)given["state"]!,
                scopes,
                (string?)given["device_name"],
                (string)given["client_id"]!),
        };
        Record("authorization_urls", name, produced);
        Same(vector["expect"]!, produced, name);
    }

    [Theory]
    [MemberData(nameof(ConfigCases))]
    public void TheConfigurationIsReadAsTheVectorsSay(string name)
    {
        var vector = Case("config", name);
        JsonObject produced;
        try
        {
            var config = NativeSignInConfig.FromPayload(vector["body"]);
            produced = new JsonObject
            {
                ["usable"] = true,
                ["authorization_endpoint"] = config.AuthorizationEndpoint,
                ["token_endpoint"] = config.TokenEndpoint,
                ["sdk_client_id"] = config.SdkClientId,
            };
        }
        catch (FormatException)
        {
            produced = new JsonObject { ["usable"] = false };
        }
        Record("config", name, produced);
        Same(vector["expect"]!, produced, name);
    }

    [Theory]
    [MemberData(nameof(EnvironmentCases))]
    public void TheBrowserRuleIsTheVectors(string name)
    {
        var vector = Case("environment", name);
        var produced = new JsonObject { ["browser"] = BrowserSignIn.CanOpenBrowser(Env(vector["env"]), (string)vector["platform"]!) };
        Record("environment", name, produced);
        Same(vector["expect"]!, produced, name);
    }

    private static JsonObject CallbackOutcome(int status, LoopbackCallback? callback) => callback switch
    {
        null => new JsonObject { ["status"] = status, ["outcome"] = "ignored" },
        { Outcome: LoopbackOutcome.Code } => new JsonObject { ["status"] = status, ["outcome"] = "code", ["code"] = callback.Code },
        { Outcome: LoopbackOutcome.Denied } => new JsonObject { ["status"] = status, ["outcome"] = "denied" },
        _ => new JsonObject { ["status"] = status, ["outcome"] = "failed", ["error"] = callback.Error },
    };

    [Theory]
    [MemberData(nameof(CallbackCases))]
    public void ACallbackMeansWhatTheVectorsSay(string name)
    {
        var vector = Case("callbacks", name);
        var request = vector["request"]!.AsObject();
        var (status, callback) = LoopbackListener.Classify(
            (string)request["method"]!, ((string)request["target"]!).Replace("{state}", SyntheticState), SyntheticState);
        var produced = CallbackOutcome(status, callback);
        Record("callbacks", name, produced);
        Same(vector["expect"]!, produced, name);
    }

    [Theory]
    [MemberData(nameof(CallbackCases))]
    public async Task TheListenerAnswersEachCallbackAsTheVectorsSay(string name)
    {
        // The same cases through a real socket: the listener answers as the rule says.
        var vector = Case("callbacks", name);
        var request = vector["request"]!.AsObject();
        var listener = new LoopbackListener(SyntheticState);
        var port = listener.Start();
        try
        {
            var answer = await BrowserTab.SendAsync(
                port, (string)request["method"]!, ((string)request["target"]!).Replace("{state}", SyntheticState),
                beforeReading: () => listener.Finish(false)).WaitAsync(TimeSpan.FromSeconds(10));
            CheckPageHeaders(answer);
            var accepted = await listener.WaitAsync(TimeSpan.Zero, CancellationToken.None);
            Same(vector["expect"]!, CallbackOutcome(answer.Status, accepted), name);
        }
        finally
        {
            await listener.CloseAsync();
        }
    }

    internal static void CheckPageHeaders(BrowserAnswer answer)
    {
        foreach (var header in Browser["page_headers"]!.AsObject())
        {
            Assert.True(answer.Headers.TryGetValue(header.Key, out var value), $"no {header.Key} header");
            Assert.Equal((string)header.Value!, value);
        }
        // Self-contained: nothing for the page to load.
        Assert.DoesNotContain("src=", answer.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("href=", answer.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("http", answer.Body, StringComparison.Ordinal);
    }

    // -- sign-ins and the chooser, for real -----------------------------------

    private static TimeSpan? Timeout(JsonObject call) =>
        call["timeout_ms"] is JsonNode milliseconds ? TimeSpan.FromMilliseconds(milliseconds.GetValue<int>()) : null;

    [Theory]
    [MemberData(nameof(SignInCases))]
    public async Task ABrowserSignInRunsAsTheVectorsSay(string name)
    {
        var vector = Case("sign_ins", name);
        var call = vector["call"]!.AsObject();
        var (produced, api) = await SignInAsync(vector, (plane, handOver) =>
        {
            var options = new BrowserLoginOptions
            {
                Scopes = call["scopes"] is JsonArray list ? list.Select(item => (string)item!).ToList() : null,
                DeviceName = (string?)call["device_name"],
                OpenBrowser = false,
                Prompt = handOver,
                Opener = _ => throw new InvalidOperationException("the browser was opened"),
            };
            if (Timeout(call) is TimeSpan timeout) options.Timeout = timeout;
            return plane.BrowserLoginAsync(options);
        });
        Record("sign_ins", name, produced);
        Assert.Empty(api.Mismatches);
        Assert.True(api.AllUsed, $"not every exchange was used: {string.Join(", ", api.Sent)}");
        Same(vector["expect"]!, produced, name);
    }

    [Theory]
    [MemberData(nameof(ChooserCases))]
    public async Task TheChooserPicksAsTheVectorsSay(string name)
    {
        var vector = Case("chooser", name);
        var call = vector["call"]!.AsObject();
        var (produced, api) = await SignInAsync(vector, (plane, handOver) => plane.InteractiveLoginAsync(new InteractiveLoginOptions
        {
            Method = (string)call["method"]! switch
            {
                "browser" => LoginMethod.Browser,
                "device" => LoginMethod.Device,
                _ => LoginMethod.Auto,
            },
            DeviceName = (string?)call["device_name"],
            Timeout = Timeout(call),
            // The case's machine, exactly: its platform and only its variables.
            Environment = Env(vector["env"]),
            Platform = (string)vector["platform"]!,
            // The browser the SDK opens. A device login opens its verification
            // page too; only the consent page is this test's to answer.
            Opener = url =>
            {
                if (url.Contains("response_type=code", StringComparison.Ordinal)) handOver(url);
            },
            Output = TextWriter.Null,
        }));
        var result = new JsonObject
        {
            ["method"] = api.Sent.Contains("POST /v1/auth/device/authorize") ? "device" : "browser",
            ["result"] = produced["result"]!.DeepClone(),
        };
        Record("chooser", name, result);
        Assert.Empty(api.Mismatches);
        Assert.True(api.AllUsed, $"not every exchange was used: {string.Join(", ", api.Sent)}");
        Same(vector["expect"]!, result, name);
        if ((string)result["method"]! == "browser" && produced.ContainsKey("browser"))
        {
            Assert.True((bool)produced["closed"]!, "the port still listens after the sign-in ended");
        }
    }

    /// <summary>
    /// One sign-in against a case: serve its exchanges, read the URL the SDK
    /// built, play the browser, and record what happened.
    /// </summary>
    private static async Task<(JsonObject Produced, ScriptedBrowserApi Api)> SignInAsync(
        JsonObject vector, Func<ThalovantControlPlane, Action<string>, Task<DeviceLoginResult>> start)
    {
        using var api = new ScriptedBrowserApi(vector["exchanges"]!.AsArray());
        var plane = new ThalovantControlPlane(apiUrl: api.Url);
        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = start(plane, url => opened.TrySetResult(url));
        var produced = new JsonObject();
        var first = await Task.WhenAny(opened.Task, task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(first == opened.Task || first == task, "the sign-in neither opened a URL nor ended");
        if (opened.Task.IsCompleted)
        {
            var signing = new BrowserSigning(opened.Task.Result);
            api.Signing = signing;
            produced["authorization_url"] = signing.Record();
            var browser = new JsonArray();
            foreach (var item in vector["requests"]!.AsArray())
            {
                var request = item!.AsObject();
                var answer = await BrowserTab.SendAsync(
                    signing.Port, (string)request["method"]!, ((string)request["target"]!).Replace("{state}", signing.State))
                    .WaitAsync(TimeSpan.FromSeconds(10));
                CheckPageHeaders(answer);
                var step = new JsonObject { ["status"] = answer.Status };
                if (answer.Status == 200) step["page"] = answer.Page;
                browser.Add(step);
            }
            produced["browser"] = browser;
        }
        object outcome;
        try
        {
            outcome = await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (ThalovantApiException error)
        {
            NoSecrets(error, api);
            outcome = error;
        }
        if (api.Signing is BrowserSigning signed) produced["closed"] = await BrowserTab.RefusedAsync(signed.Port);
        if (api.Exchange is JsonObject exchange) produced["exchange"] = exchange.DeepClone();
        produced["result"] = Result(plane, outcome);
        if ((string?)vector["call"]?["then"] == "revoke" && (string?)produced["result"]!["outcome"] == "approved")
        {
            await plane.RevokeApiTokenAsync();
            Assert.Null(plane.AccessToken);
            Assert.Null(plane.TokenId);
            await plane.RevokeApiTokenAsync(); // Idempotent: sends nothing.
            produced["result"] = new JsonObject { ["outcome"] = "revoked" };
        }
        return (produced, api);
    }

    private static JsonObject Result(ThalovantControlPlane plane, object outcome)
    {
        switch (outcome)
        {
            case ThalovantDeviceAccessDeniedException denied:
                return new JsonObject { ["outcome"] = "denied", ["status"] = denied.StatusCode };
            case ThalovantDeviceCodeExpiredException expired:
                return new JsonObject { ["outcome"] = "expired", ["status"] = expired.StatusCode };
            case ThalovantApiException error:
                var produced = new JsonObject { ["outcome"] = "error", ["status"] = error.StatusCode, ["code"] = error.ErrorCode };
                if (error.StatusCode is not null) produced["detail"] = error.Detail;
                return produced;
            case DeviceLoginResult token:
                // Kept exactly as a device login keeps it.
                Assert.Equal(token.AccessToken, plane.AccessToken);
                Assert.Equal(token.TokenId, plane.TokenId);
                return new JsonObject
                {
                    ["outcome"] = "approved",
                    ["token_type"] = token.TokenType,
                    ["scopes"] = new JsonArray(token.Scopes.Select(scope => (JsonNode?)JsonValue.Create(scope)).ToArray()),
                    ["expires_at"] = token.ExpiresAt,
                    ["token_id"] = token.TokenId,
                };
            default:
                throw new InvalidOperationException($"unexpected outcome {outcome}");
        }
    }

    /// <summary>No synthetic secret, and neither the verifier nor the state the SDK generated, reaches an error.</summary>
    private static void NoSecrets(ThalovantApiException error, ScriptedBrowserApi api)
    {
        var hidden = Browser["message_excludes"]!.AsArray().Select(item => (string)item!).ToList();
        if (api.Verifier is string verifier) hidden.Add(verifier);
        if (api.Signing?.State is string state) hidden.Add(state);
        var shown = new[] { error.Message, error.ToString(), error.Body ?? "", error.Problem?.ToJsonString() ?? "", error.Detail ?? "" };
        foreach (var secret in hidden)
        {
            Assert.False(shown.Any(text => text.Contains(secret, StringComparison.Ordinal)), "a secret reached the error");
        }
    }

    // -- the rest -------------------------------------------------------------

    [Fact]
    public void EverySectionIsRun()
    {
        // A section nobody runs is behaviour written down and never checked.
        var sections = new SortedSet<string>(StringComparer.Ordinal)
        {
            "pkce", "redirect_uris", "authorization_urls", "config", "environment", "callbacks", "sign_ins", "chooser",
        };
        var named = new SortedSet<string>(
            Browser.Where(pair => pair.Value is JsonArray list && list.Count > 0 && list[0] is JsonObject first && first.ContainsKey("name"))
                .Select(pair => pair.Key),
            StringComparer.Ordinal);
        Assert.Equal(sections, named);
        Assert.Equal(98, sections.Sum(section => Browser[section]!.AsArray().Count));
    }

    [Fact]
    public void TheRfc7636VectorIsTheFirstPkceCase()
    {
        var first = Browser["pkce"]!.AsArray()[0]!;
        Assert.Equal("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", (string)first["verifier"]!);
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", (string)first["expect"]!["challenge"]!);
    }

    [Fact]
    public void TheConstantsAreTheVectors()
    {
        Assert.Equal(BrowserSignIn.SdkClientId, (string)Browser["client_id"]!);
        Assert.Equal(((string)Browser["redirect_uri"]!).Replace("{port}", "53682"), BrowserSignIn.LoopbackRedirectUri(53682));
        Assert.Equal(Browser["default_timeout_ms"]!.GetValue<int>(), (int)BrowserSignIn.DefaultTimeout.TotalMilliseconds);
        Assert.Equal(BrowserSignIn.DefaultTimeout, new BrowserLoginOptions().Timeout);
        Assert.Equal(BrowserSignIn.MaxDeviceNameLength, Browser["device_name_max_code_points"]!.GetValue<int>());
        Assert.Equal(LoopbackListener.SignedInText, (string)Browser["pages"]!["signed_in"]!);
        Assert.Equal(LoopbackListener.NotCompletedText, (string)Browser["pages"]!["not_completed"]!);
        Assert.Equal(
            Browser["page_headers"]!.AsObject().Select(pair => $"{pair.Key}: {(string)pair.Value!}"),
            LoopbackListener.PageHeaders.Select(pair => $"{pair.Key}: {pair.Value}"));
        Assert.Equal(BrowserSignIn.OpeningPrompt, (string)Browser["prompt"]!["opening"]!);
        Assert.Equal(BrowserSignIn.NotOpeningPrompt, (string)Browser["prompt"]!["not_opening"]!);
    }

    [Fact]
    public async Task TheListenerBindsLoopbackOnly()
    {
        var listener = new LoopbackListener(new string('s', 32));
        var port = listener.Start();
        try
        {
            Assert.Equal(IPAddress.Parse("127.0.0.1"), listener.LocalEndpoint!.Address);
            Assert.InRange(port, 1024, 65535);
            // Exclusive on Windows, where another socket could otherwise share the port.
            Assert.Equal(OperatingSystem.IsWindows(), listener.ExclusiveAddressUse);
        }
        finally
        {
            await listener.CloseAsync();
        }
    }
}

/// <summary>What one tab got back: the status, the headers, and the page.</summary>
internal sealed class BrowserAnswer
{
    internal BrowserAnswer(int status, Dictionary<string, string> headers, string body)
    {
        Status = status;
        Headers = headers;
        Body = body;
    }

    internal int Status { get; }
    internal Dictionary<string, string> Headers { get; }
    internal string Body { get; }

    /// <summary>Which of the vectors' pages the body is, or null.</summary>
    internal string? Page =>
        BrowserLoginVectorTests.Browser["pages"]!.AsObject().FirstOrDefault(pair => Body.Contains((string)pair.Value!, StringComparison.Ordinal)).Key;
}

/// <summary>A browser tab, as raw HTTP/1.1: one request on its own connection.</summary>
internal static class BrowserTab
{
    internal static async Task<BrowserAnswer> SendAsync(int port, string method, string target, Action? beforeReading = null, byte[]? raw = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var request = raw ?? Encoding.UTF8.GetBytes($"{method} {target} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        await stream.FlushAsync();
        if (beforeReading is not null)
        {
            await Task.Delay(50);
            beforeReading();
        }
        var data = new MemoryStream();
        await stream.CopyToAsync(data);
        return Parse(data.ToArray());
    }

    internal static BrowserAnswer Parse(byte[] raw)
    {
        var end = IndexOf(raw, "\r\n\r\n"u8.ToArray());
        Assert.True(end >= 0, "no answer head");
        var lines = Encoding.Latin1.GetString(raw, 0, end).Split("\r\n");
        var status = int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }
        return new BrowserAnswer(status, headers, Encoding.UTF8.GetString(raw, end + 4, raw.Length - end - 4));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var index = 0; index + needle.Length <= haystack.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle)) return index;
        }
        return -1;
    }

    /// <summary>Whether a new connection to the port is refused. Windows retries a refused loopback connect for about two seconds.</summary>
    internal static async Task<bool> RefusedAsync(int port)
    {
        using var client = new TcpClient();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, limit.Token);
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>What a test learnt from the URL the SDK built.</summary>
internal sealed class BrowserSigning
{
    private static readonly Regex StateAlphabet = new(@"\A[A-Za-z0-9_-]{22,}\z");
    private static readonly Regex Challenge = new(@"\A[A-Za-z0-9_-]{43}\z");

    internal BrowserSigning(string url)
    {
        var mark = url.IndexOf('?');
        Assert.True(mark > 0, "the URL has no query");
        Endpoint = url.Substring(0, mark);
        Pairs = url.Substring(mark + 1).Split('&').Select(pair =>
        {
            var split = pair.IndexOf('=');
            return (Uri.UnescapeDataString(pair.Substring(0, split)), Uri.UnescapeDataString(pair.Substring(split + 1)));
        }).ToList();
        var values = Pairs.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
        var redirect = new Uri(values["redirect_uri"]);
        Port = redirect.Port;
        State = values["state"];
        CodeChallenge = values["code_challenge"];
        Assert.Matches(StateAlphabet, State);
        Assert.Matches(Challenge, CodeChallenge);
        Assert.Equal($"http://127.0.0.1:{Port}/callback", values["redirect_uri"]);
        Assert.InRange(Port, 1024, 65535);
    }

    internal string Endpoint { get; }
    internal List<(string Name, string Value)> Pairs { get; }
    internal int Port { get; }
    internal string State { get; }
    internal string CodeChallenge { get; }

    /// <summary>The generated values put back as the vectors' placeholders.</summary>
    internal string Normalized(string text) =>
        text.Replace($"http://127.0.0.1:{Port}/callback", "http://127.0.0.1:{port}/callback", StringComparison.Ordinal)
            .Replace(State, "{state}", StringComparison.Ordinal)
            .Replace(CodeChallenge, "{challenge}", StringComparison.Ordinal);

    internal JsonObject Record() => new JsonObject
    {
        ["endpoint"] = Endpoint,
        ["query"] = new JsonArray(Pairs.Select(pair => (JsonNode?)new JsonArray(pair.Name, Normalized(pair.Value))).ToArray()),
    };
}

/// <summary>One request a <see cref="TinyHttpServer"/> received.</summary>
internal sealed record TinyRequest(string Method, string Path, Dictionary<string, string> Headers, string Body);

/// <summary>What a <see cref="TinyHttpServer"/> answers; no Content-Type with an empty body.</summary>
internal sealed record TinyResponse(int Status, string ContentType, string Body);

/// <summary>
/// An API on a loopback port: plain HTTP/1.1 over a <see cref="TcpListener"/>,
/// one request per connection, so the SDK's own HttpClient path is what runs.
/// </summary>
internal sealed class TinyHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<TinyRequest, TinyResponse> _respond;

    internal TinyHttpServer(Func<TinyRequest, TinyResponse> respond)
    {
        _respond = respond;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    internal int Port { get; }
    internal string Url => $"http://127.0.0.1:{Port}";

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadAsync(stream);
                var answer = _respond(request);
                var body = Encoding.UTF8.GetBytes(answer.Body);
                var head = new StringBuilder();
                head.Append("HTTP/1.1 ").Append(answer.Status.ToString(CultureInfo.InvariantCulture)).Append(" Scripted\r\n");
                if (body.Length > 0) head.Append("Content-Type: ").Append(answer.ContentType).Append("\r\n");
                head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                head.Append("Connection: close\r\n\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()).Concat(body).ToArray(), _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch (Exception)
            {
                // A client that went away, or the server stopping: nothing to answer.
            }
        }
    }

    private async Task<TinyRequest> ReadAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (!(buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n'))
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0) throw new EndOfStreamException();
            buffer.Add(one[0]);
            if (buffer.Count > 65536) throw new InvalidDataException("request head too large");
        }
        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
        var start = lines[0].Split(' ');
        var target = start[1];
        var query = target.IndexOf('?');
        var path = query >= 0 ? target.Substring(0, query) : target;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':');
            headers[line.Substring(0, colon).Trim().ToLowerInvariant()] = line.Substring(colon + 1).Trim();
        }
        var length = headers.TryGetValue("content-length", out var declared) ? int.Parse(declared, CultureInfo.InvariantCulture) : 0;
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var got = await stream.ReadAsync(body.AsMemory(read, length - read), _stop.Token);
            if (got == 0) throw new EndOfStreamException();
            read += got;
        }
        return new TinyRequest(start[0], path, headers, Encoding.UTF8.GetString(body));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

/// <summary>
/// Serves a case's exchanges in order and checks each request against its
/// own: method, path, <c>json</c> and <c>authorization</c>. A request the case
/// does not list is a mismatch, which is how a device-login fallback is caught.
/// </summary>
internal sealed class ScriptedBrowserApi : IDisposable
{
    private static readonly Regex Unreserved = new(@"\A[A-Za-z0-9._~-]{43,128}\z");
    private readonly JsonArray _exchanges;
    private readonly object _gate = new();
    private readonly TinyHttpServer _server;
    private BrowserSigning? _signing;
    private int _index;

    internal ScriptedBrowserApi(JsonArray exchanges)
    {
        _exchanges = exchanges;
        _server = new TinyHttpServer(Respond);
    }

    internal string Url => _server.Url;
    internal List<string> Sent { get; } = new();
    internal List<string> Mismatches { get; } = new();
    internal JsonObject? Exchange { get; private set; }
    internal string? Verifier { get; private set; }

    internal BrowserSigning? Signing
    {
        get { lock (_gate) return _signing; }
        set { lock (_gate) _signing = value; }
    }

    internal bool AllUsed
    {
        get { lock (_gate) return _index == _exchanges.Count; }
    }

    private TinyResponse Respond(TinyRequest request)
    {
        lock (_gate)
        {
            Sent.Add($"{request.Method} {request.Path}");
            if (_index >= _exchanges.Count)
            {
                Mismatches.Add($"unexpected {request.Method} {request.Path}");
                return new TinyResponse(599, "application/json", "{}");
            }
            var exchange = _exchanges[_index++]!.AsObject();
            var expected = exchange["request"]!.AsObject();
            if (request.Method != (string)expected["method"]! || request.Path != (string)expected["path"]!)
                Mismatches.Add($"{request.Method} {request.Path} != {expected["method"]} {expected["path"]}");
            var body = request.Body.Length == 0 ? null : JsonNode.Parse(request.Body);
            if ((request.Path == "/v1/auth/native/token" && (string)expected["path"]! == request.Path)
                || (body is JsonObject sent && sent.ContainsKey("code_verifier")))
                body = TokenBody(body);
            if (expected.TryGetPropertyValue("json", out var json) && !JsonNode.DeepEquals(body, json))
                Mismatches.Add($"body {body?.ToJsonString()} != {json?.ToJsonString()}");
            if (expected.TryGetPropertyValue("authorization", out var authorization)
                && (request.Headers.TryGetValue("authorization", out var header) ? header : null) != (string?)authorization)
                Mismatches.Add("wrong Authorization header");
            var response = exchange["response"]!.AsObject();
            return new TinyResponse(response["status"]!.GetValue<int>(), (string)response["content_type"]!, (string)response["body"]!);
        }
    }

    /// <summary>The exchange body, with the generated values put back as placeholders once proven.</summary>
    private JsonNode? TokenBody(JsonNode? body)
    {
        if (body is not JsonObject fields || _signing is null)
        {
            return body;
        }
        var keys = new SortedSet<string>(fields.Select(pair => pair.Key), StringComparer.Ordinal);
        if (!keys.SetEquals(new[] { "code", "code_verifier", "client_id", "redirect_uri" }))
        {
            Mismatches.Add($"the exchange must carry exactly four fields, not {string.Join(",", keys)}");
        }
        var clean = new JsonObject();
        foreach (var pair in fields)
        {
            clean[pair.Key] = pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
                ? JsonValue.Create(_signing.Normalized(text))
                : pair.Value?.DeepClone();
        }
        var verifier = (string?)fields["code_verifier"];
        if (verifier is not null && Unreserved.IsMatch(verifier) && S256(verifier) == _signing.CodeChallenge)
        {
            clean["code_verifier"] = "{verifier}";
            Verifier = verifier;
        }
        else
        {
            Mismatches.Add("the code_verifier is not the one the challenge was made from");
        }
        Exchange = clean;
        return clean;
    }

    private static string S256(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public void Dispose() => _server.Dispose();
}
