using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests;

/// <summary>
/// The Home Assistant link against the four vector files every SDK shares,
/// vendored byte for byte from the Python reference's contracts/conformance.
/// </summary>
/// <remarks>
/// <para>
/// <c>device-login</c>, <c>connection-kinds</c> and <c>connection-admission</c>
/// are HTTP exchanges: each case's answers are served, in order, by the handler
/// the control plane sends through, and every request the SDK makes is checked
/// against the one the case names -- method, path, body or body subset,
/// <c>If-Match</c>, <c>Authorization</c>. <c>home-link</c> runs its answers
/// through a real <see cref="HiveMindWssTransport"/> to an in-memory hub that
/// completes the Noise handshake, so the reply recorded is the one on the wire.
/// </para>
/// <para>
/// What the SDK produced is recorded before it is compared, so the conformance
/// record is this SDK's output rather than a restatement of the vectors.
/// </para>
/// </remarks>
[Collection("Runtime deadlines")]
public sealed class HomeLinkVectorTests : IClassFixture<HomeLinkVectorTests.ConnectedHub>
{
    /// <summary>
    /// One hub, connected once for every home-link case: a Noise handshake costs
    /// an Argon2 derivation, and nothing about a case depends on a fresh one.
    /// </summary>
    public sealed class ConnectedHub : IAsyncLifetime
    {
        internal HubPeer Peer { get; } = new HubPeer();
        internal ThalovantClient Client { get; }

        public ConnectedHub() => Client = HubPeer.ClientFor(Peer);

        public Task InitializeAsync() => Client.ConnectAsync(TimeSpan.FromSeconds(10));

        public Task DisposeAsync() => Client.CloseAsync();
    }

    private readonly ConnectedHub _hub;

    public HomeLinkVectorTests(ConnectedHub hub) => _hub = hub;

    private static JsonObject Vectors(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!.AsObject();

    private static readonly JsonObject Device = Vectors("device-login-vectors.json");
    private static readonly JsonObject Kinds = Vectors("connection-kinds-vectors.json");
    private static readonly JsonObject Admission = Vectors("connection-admission-vectors.json");
    private static readonly JsonObject Home = Vectors("home-link-vectors.json");

    private static TheoryData<string> Names(JsonObject vectors)
    {
        var names = new TheoryData<string>();
        foreach (var item in vectors["cases"]!.AsArray()) names.Add((string)item!["name"]!);
        return names;
    }

    public static TheoryData<string> DeviceCases => Names(Device);
    public static TheoryData<string> KindCases => Names(Kinds);
    public static TheoryData<string> AdmissionCases => Names(Admission);
    public static TheoryData<string> HomeCases => Names(Home);

    private static JsonObject Case(JsonObject vectors, string name) =>
        vectors["cases"]!.AsArray().Single(item => (string)item!["name"]! == name)!.AsObject();

    private static void Same(JsonNode expect, JsonNode produced, string name)
    {
        Assert.True(JsonNode.DeepEquals(expect, produced), $"{name}: produced {produced.ToJsonString()}, expected {expect.ToJsonString()}");
        Assert.Equal(ConformanceRecord.CanonicalDigest(expect), ConformanceRecord.CanonicalDigest(produced));
    }

    /// <summary>A whole number of seconds as JSON, or a refusal to pretend one is.</summary>
    private static JsonNode Seconds(TimeSpan value)
    {
        var seconds = value.TotalSeconds;
        Assert.Equal(Math.Truncate(seconds), seconds);
        return JsonValue.Create((long)seconds);
    }

    /// <summary>Neither the device code nor the token, nor a connection's secrets, ever reaches a message.</summary>
    private static void Excluded(Exception error, JsonObject vectors)
    {
        foreach (var item in vectors["message_excludes"]?.AsArray() ?? new JsonArray())
        {
            var secret = (string)item!;
            Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        }
    }

    /// <summary>Serves a case's exchanges in order and checks each request against its own.</summary>
    private sealed class ScriptedApi : HttpMessageHandler
    {
        private readonly JsonArray _exchanges;
        private int _index;

        internal List<string> Sent { get; } = new List<string>();
        internal List<string> Mismatches { get; } = new List<string>();
        internal bool AllUsed => _index == _exchanges.Count;

        internal ScriptedApi(JsonArray exchanges) => _exchanges = exchanges;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var ifMatch = request.Headers.TryGetValues("If-Match", out var values) ? string.Join(",", values) : null;
            lock (Sent) Sent.Add($"{request.Method.Method} {path}" + (ifMatch is null ? "" : $" If-Match={ifMatch}"));
            if (_index >= _exchanges.Count)
            {
                Mismatches.Add($"unexpected {request.Method.Method} {path}");
                return new HttpResponseMessage((HttpStatusCode)599) { Content = new StringContent("{}"), RequestMessage = request };
            }
            var exchange = _exchanges[_index]!.AsObject();
            if (exchange["repeat"]?.GetValue<bool>() != true) _index++;
            var expected = exchange["request"]!.AsObject();
            if (request.Method.Method != (string)expected["method"]! || path != (string)expected["path"]!)
                Mismatches.Add($"{request.Method.Method} {path} != {expected["method"]} {expected["path"]}");
            var raw = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var body = raw.Length == 0 ? null : JsonNode.Parse(raw);
            if (expected.TryGetPropertyValue("json", out var json) && !JsonNode.DeepEquals(body, json))
                Mismatches.Add($"body {body?.ToJsonString()} != {json?.ToJsonString()}");
            if (expected.TryGetPropertyValue("json_subset", out var subset) && !Contains(body, subset))
                Mismatches.Add($"body lacks {subset?.ToJsonString()}");
            if (expected.TryGetPropertyValue("if_match", out var match) && ifMatch != (string?)match)
                Mismatches.Add($"If-Match {ifMatch} != {match}");
            if (expected.TryGetPropertyValue("authorization", out var authorization)
                && request.Headers.Authorization?.ToString() != (string?)authorization)
                Mismatches.Add("wrong Authorization header");
            var response = exchange["response"]!.AsObject();
            var text = (string)response["body"]!;
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
            if (text.Length > 0) content.Headers.TryAddWithoutValidation("Content-Type", (string)response["content_type"]!);
            return new HttpResponseMessage((HttpStatusCode)response["status"]!.GetValue<int>()) { Content = content, RequestMessage = request };
        }

        private static bool Contains(JsonNode? value, JsonNode? subset)
        {
            if (subset is JsonObject wanted)
            {
                return value is JsonObject actual && wanted.All(pair => actual.ContainsKey(pair.Key) && Contains(actual[pair.Key], pair.Value));
            }
            return JsonNode.DeepEquals(value, subset);
        }
    }

    private static ThalovantControlPlane Plane(ScriptedApi api, string? accessToken = null) =>
        new ThalovantControlPlane(api, apiUrl: "https://api.example.test", accessToken: accessToken);

    // -- device login ---------------------------------------------------------

    [Theory]
    [MemberData(nameof(DeviceCases))]
    public async Task DeviceLoginAnswersEachStepAsItsVectorSays(string name)
    {
        var vector = Case(Device, name);
        var call = vector["call"]!.AsObject();
        using var api = new ScriptedApi(vector["exchanges"]!.AsArray());
        var plane = Plane(api);
        var produced = new JsonArray();
        if ((string)call["op"]! == "begin")
        {
            try
            {
                var grant = await plane.BeginDeviceLoginAsync(
                    call["scopes"]?.AsArray().Select(scope => (string)scope!).ToArray(),
                    (string?)call["client_name"]);
                produced.Add(new JsonObject
                {
                    ["outcome"] = "started",
                    ["user_code"] = grant.UserCode,
                    ["verification_uri"] = grant.VerificationUri,
                    ["verification_uri_complete"] = grant.VerificationUriComplete,
                    ["interval"] = Seconds(grant.Interval),
                    ["expires_in"] = grant.ExpiresIn,
                });
            }
            catch (ThalovantApiException error)
            {
                Excluded(error, Device);
                produced.Add(new JsonObject { ["outcome"] = "error", ["status"] = error.StatusCode });
            }
        }
        else
        {
            var authorization = new DeviceAuthorization(
                (string)call["authorization"]!["device_code"]!, "", "https://x", null, 900,
                TimeSpan.FromSeconds(call["authorization"]!["interval"]!.GetValue<double>()), new JsonObject());
            var times = call["times"]?.GetValue<int>() ?? 1;
            for (var poll = 0; poll < times; poll++) produced.Add(await PollOnce(plane, authorization));
            if ((string)call["op"]! == "revoke")
            {
                await plane.RevokeApiTokenAsync();
                Assert.Null(plane.AccessToken);
                Assert.Null(plane.TokenId);
                produced = new JsonArray { new JsonObject { ["outcome"] = "revoked" } };
            }
        }
        ConformanceRecord.Record("device-login-vectors.json", name, produced.DeepClone());
        Assert.Empty(api.Mismatches);
        Assert.True(api.AllUsed, "not every exchange was used");
        Same(vector["expect"]!, produced, name);
    }

    private static async Task<JsonObject> PollOnce(ThalovantControlPlane plane, DeviceAuthorization authorization)
    {
        try
        {
            var token = await plane.PollDeviceLoginAsync(authorization);
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
        }
        catch (ThalovantDeviceLoginPendingException pending)
        {
            return new JsonObject { ["outcome"] = "pending", ["interval"] = Seconds(pending.Interval) };
        }
        catch (ThalovantDeviceCodeExpiredException error)
        {
            Excluded(error, Device);
            return new JsonObject { ["outcome"] = "expired", ["status"] = error.StatusCode };
        }
        catch (ThalovantDeviceAccessDeniedException error)
        {
            Excluded(error, Device);
            return new JsonObject { ["outcome"] = "denied", ["status"] = error.StatusCode };
        }
        catch (ThalovantApiException error)
        {
            Excluded(error, Device);
            var produced = new JsonObject { ["outcome"] = "error", ["status"] = error.StatusCode };
            if (error.StatusCode is not null)
            {
                produced["code"] = error.ErrorCode;
                produced["detail"] = error.Detail;
            }
            return produced;
        }
    }

    // -- connection kinds -----------------------------------------------------

    [Theory]
    [MemberData(nameof(KindCases))]
    public async Task ConnectionKindsAnswerAsTheirVectorSays(string name)
    {
        var vector = Case(Kinds, name);
        var call = vector["call"]!.AsObject();
        using var api = new ScriptedApi(vector["exchanges"]!.AsArray());
        var plane = Plane(api, "synthetic-token");
        JsonObject produced;
        if ((string)call["op"]! == "create")
        {
            try
            {
                var result = await plane.CreateClientIdentityAsync(
                    call["hub"]!.DeepClone().AsObject(),
                    new CreateClientIdentityOptions((string)call["name"]!) { ConnectionType = (string)call["connection_type"]! });
                produced = new JsonObject
                {
                    ["outcome"] = "created",
                    ["client_id"] = result.ClientId,
                    ["connection_type"] = result.ConnectionType,
                    ["operation_id"] = result.Operation?.Id,
                };
            }
            catch (ThalovantUnsupportedConnectionTypeException error)
            {
                Excluded(error, Kinds);
                produced = new JsonObject { ["outcome"] = "unsupported" };
                if (error.StatusCode is not null) ApiFields(produced, error);
                else produced["deleted"] = api.Sent.Any(line => line.StartsWith("DELETE ", StringComparison.Ordinal));
            }
            catch (ThalovantApiException error)
            {
                Excluded(error, Kinds);
                produced = new JsonObject { ["outcome"] = Outcome(error) };
                ApiFields(produced, error);
                if (error is ThalovantAlreadyLinkedException linked) produced["client_id"] = linked.ClientId;
            }
        }
        else
        {
            try
            {
                await plane.DeleteClientAsync((string)call["client_id"]!, (string?)call["etag"]);
                produced = new JsonObject { ["outcome"] = "deleted" };
            }
            catch (ThalovantApiException error)
            {
                produced = new JsonObject { ["outcome"] = Outcome(error) };
                ApiFields(produced, error);
            }
        }
        produced["requests"] = new JsonArray(api.Sent.Select(line => (JsonNode?)JsonValue.Create(line)).ToArray());
        ConformanceRecord.Record("connection-kinds-vectors.json", name, produced.DeepClone());
        Assert.Empty(api.Mismatches);
        Same(vector["expect"]!, produced, name);
    }

    private static void ApiFields(JsonObject produced, ThalovantApiException error)
    {
        produced["status"] = error.StatusCode;
        produced["code"] = error.ErrorCode;
        produced["detail"] = error.Detail;
    }

    private static string Outcome(ThalovantApiException error) => error switch
    {
        ThalovantPlanException => "plan",
        ThalovantAlreadyLinkedException => "already_linked",
        ThalovantAuthenticationException => "auth",
        _ => "error",
    };

    // -- admission -----------------------------------------------------------

    [Theory]
    [MemberData(nameof(AdmissionCases))]
    public async Task AdmissionEndsAsItsVectorSays(string name)
    {
        var vector = Case(Admission, name);
        var call = vector["call"]!.AsObject();
        using var api = new ScriptedApi(vector["exchanges"]!.AsArray());
        var plane = Plane(api, "synthetic-token");
        var operation = call["operation"] is JsonObject raw ? JsonSerializer.Deserialize<OperationResource>(raw.ToJsonString()) : null;
        JsonObject produced;
        try
        {
            await plane.WaitForAdmissionAsync(
                operation,
                TimeSpan.FromSeconds(call["timeout_seconds"]!.GetValue<double>()),
                TimeSpan.FromSeconds(call["poll_interval_seconds"]!.GetValue<double>()));
            produced = new JsonObject { ["outcome"] = "admitted", ["polls"] = api.Sent.Count };
        }
        catch (ThalovantAdmissionTimeoutException error)
        {
            // A connection error and a timeout at once: it may still be admitted.
            Assert.IsAssignableFrom<ThalovantConnectionException>(error);
            Assert.IsAssignableFrom<IThalovantTimeout>(error);
            produced = new JsonObject { ["outcome"] = "timeout" };
        }
        catch (ThalovantAdmissionFailedException error)
        {
            produced = new JsonObject { ["outcome"] = "failed", ["error_code"] = error.ErrorCode, ["polls"] = api.Sent.Count };
        }
        catch (ThalovantApiException)
        {
            produced = new JsonObject { ["outcome"] = "error", ["polls"] = api.Sent.Count };
        }
        ConformanceRecord.Record("connection-admission-vectors.json", name, produced.DeepClone());
        Assert.Empty(api.Mismatches);
        Same(vector["expect"]!, produced, name);
    }

    // -- the home link -------------------------------------------------------

    private static HomeRequestHandler Handler(JsonObject spec) => async (request, cancellationToken) =>
    {
        if (spec["raises"]?.GetValue<bool>() == true) throw new InvalidOperationException("the conversation agent is gone");
        if (spec["sleep_seconds"] is JsonNode sleep) await Task.Delay(TimeSpan.FromSeconds(sleep.GetValue<double>()), cancellationToken);
        return new HomeAnswer(
            (string?)spec["speech"] ?? "",
            (string?)spec["response_type"] ?? HomeResponseTypes.ActionDone,
            (string?)spec["error_code"],
            spec["continue_conversation"]?.GetValue<bool>() ?? false);
    };

    [Theory]
    [MemberData(nameof(HomeCases))]
    public async Task HomeRequestsAreAnsweredAsTheirVectorSays(string name)
    {
        var vector = Case(Home, name);
        var peer = _hub.Peer;
        var client = _hub.Client;
        Assert.True(client.LinkUp);
        JsonNode produced;
        if ((string)vector["kind"]! == "reply_context")
        {
            var context = vector["context"]!.AsObject();
            produced = ThalovantContext.ReplyContext(context);
            // And the same routing on the wire, through ReplyAsync.
            var arrived = new TaskCompletionSource<ThalovantEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var listening = client.On("test.ask", arrived.SetResult);
            peer.SendBus("test.ask", new JsonObject(), (JsonObject)context.DeepClone());
            await client.ReplyAsync(await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10)), "test.answer");
            var reply = await peer.NextAsync(TimeSpan.FromSeconds(10));
            Same(produced, reply["context"]!, name);
        }
        else
        {
            var timeout = vector["timeout_seconds"] is JsonNode seconds ? TimeSpan.FromSeconds(seconds.GetValue<double>()) : (TimeSpan?)null;
            using var answering = client.AnswerHomeRequests(Handler(vector["handler"]!.AsObject()), timeout);
            peer.SendBus(ThalovantHome.RequestEvent, vector["request"]!.DeepClone().AsObject(), new JsonObject
            {
                ["source"] = "thalovant-skill-home",
                ["destination"] = new JsonArray("ha-peer"),
                ["session"] = new JsonObject { ["session_id"] = "kitchen" },
            });
            var sent = await peer.NextAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ThalovantHome.ResponseEvent, (string?)sent["type"]);
            // A reply: back to the skill that asked, from the peer it asked, in its session.
            Assert.Equal("thalovant-skill-home", (string?)sent["context"]!["destination"]);
            Assert.Equal("ha-peer", (string?)sent["context"]!["source"]);
            Assert.Equal("kitchen", (string?)sent["context"]!["session"]!["session_id"]);
            produced = sent["data"]!;
        }
        ConformanceRecord.Record("home-link-vectors.json", name, produced.DeepClone());
        Same(vector["expect"]!, produced, name);
    }

    [Fact]
    public void TheContractListsMatchTheSdk()
    {
        Assert.Equal(Home["response_types"]!.AsArray().Select(item => (string)item!), HomeResponseTypes.All);
        Assert.Equal(Home["error_codes"]!.AsArray().Select(item => (string)item!), HomeErrorCodes.All);
        Assert.Equal(ThalovantHome.RequestEvent, (string)Home["request_type"]!);
        Assert.Equal(ThalovantHome.ResponseEvent, (string)Home["response_type"]!);
        Assert.Equal(TimeSpan.FromSeconds(Home["reply_timeout_seconds"]!.GetValue<double>()), ThalovantHome.HubTimeout);
        Assert.True(ThalovantHome.DefaultHandlerTimeout < ThalovantHome.HubTimeout);
        Assert.Equal(Device["home_assistant_scopes"]!.AsArray().Select(item => (string)item!), ThalovantHome.HomeAssistantScopes);
    }
}
