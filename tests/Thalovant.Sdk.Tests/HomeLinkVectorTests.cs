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

    /// <summary>A control plane pointed at the loopback API, over the SDK's own HttpClient.</summary>
    private static ThalovantControlPlane Plane(LoopbackApi api, string? accessToken = null) =>
        new ThalovantControlPlane(apiUrl: api.Url, accessToken: accessToken);

    // -- device login ---------------------------------------------------------

    [Theory]
    [MemberData(nameof(DeviceCases))]
    public async Task DeviceLoginAnswersEachStepAsItsVectorSays(string name)
    {
        var vector = Case(Device, name);
        var call = vector["call"]!.AsObject();
        using var api = new LoopbackApi(vector["exchanges"]!.AsArray());
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
                // Idempotent: revoking again sends nothing and succeeds.
                await plane.RevokeApiTokenAsync();
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

    [Fact]
    public async Task RevokingAnotherTokenByIdStillThrowsWhatTheApiSaid()
    {
        using var api = new LoopbackApi(new JsonArray(new JsonObject
        {
            ["request"] = new JsonObject { ["method"] = "DELETE", ["path"] = "/v1/auth/api-tokens/someone-else" },
            ["response"] = new JsonObject { ["status"] = 401, ["content_type"] = "application/problem+json", ["body"] = """{"detail":"Could not validate credentials"}""" },
        }));
        var plane = Plane(api, "synthetic-token");
        plane.TokenId = "mine";
        var error = await Assert.ThrowsAsync<ThalovantAuthenticationException>(() => plane.RevokeApiTokenAsync("someone-else"));
        Assert.Equal(401, error.StatusCode);
        // Not this client's token: nothing here is forgotten.
        Assert.Equal("synthetic-token", plane.AccessToken);
        Assert.Equal("mine", plane.TokenId);
        Assert.Empty(api.Mismatches);
    }

    [Fact]
    public async Task APasswordSignInTakesTheDeviceTokensPlaceAndItsId()
    {
        var approved = Case(Device, "approved, with the token's scopes and id")["exchanges"]!.AsArray()[0]!.DeepClone();
        using var api = new LoopbackApi(new JsonArray(approved, new JsonObject
        {
            ["request"] = new JsonObject { ["method"] = "POST", ["path"] = "/v1/auth/token" },
            ["response"] = new JsonObject { ["status"] = 200, ["content_type"] = "application/json", ["body"] = """{"access_token":"session-token","token_type":"bearer"}""" },
        }));
        var plane = Plane(api);
        await plane.PollDeviceLoginAsync(new DeviceAuthorization("dc-5f0c2d4e8a614c1f9d2b3e7a9c0b1f24", "", "https://x", null, 900, TimeSpan.FromSeconds(5), new JsonObject()));
        Assert.Equal("7b0e1c52-9a0d-4f64-9d0e-3f1a2b4c5d6e", plane.TokenId);
        await plane.LoginAsync("dev@example.com", "secret");
        Assert.Equal("session-token", plane.AccessToken);
        // The device token's id went with it: a default revoke has nothing to
        // reach for, rather than revoking a token this client no longer holds.
        Assert.Null(plane.TokenId);
        await Assert.ThrowsAsync<ThalovantApiException>(() => plane.RevokeApiTokenAsync());
        Assert.Equal(2, api.Sent.Count);
        Assert.Empty(api.Mismatches);
    }

    // -- connection kinds -----------------------------------------------------

    [Theory]
    [MemberData(nameof(KindCases))]
    public async Task ConnectionKindsAnswerAsTheirVectorSays(string name)
    {
        var vector = Case(Kinds, name);
        var call = vector["call"]!.AsObject();
        using var api = new LoopbackApi(vector["exchanges"]!.AsArray());
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

    /// <summary>One exchange for a scripted API: what is sent, and what comes back.</summary>
    private static JsonObject Exchange(string method, string path, int status, string body) => new JsonObject
    {
        ["request"] = new JsonObject { ["method"] = method, ["path"] = path },
        ["response"] = new JsonObject { ["status"] = status, ["content_type"] = "application/json", ["body"] = body },
    };

    private static readonly JsonObject KitchenHub = new JsonObject
    {
        ["id"] = "4a1b2c3d-0000-4000-8000-000000000001",
        ["domain"] = "kitchen.thalovant.io",
        ["wss_enabled"] = true,
    };

    [Theory]
    // A model validator on the spec: its input echoes the whole spec, connection_type included.
    [InlineData("""{"detail":[{"type":"value_error","loc":["body","spec"],"msg":"Value error, siteId must be lowercase","input":{"version":"1","connection_type":"home_assistant","siteId":"X"}}]}""", false)]
    [InlineData("""{"detail":[{"type":"literal_error","loc":["body","spec","connection_type"],"msg":"Input should be 'voice_satellite' or 'web_chat'","input":"home_assistant"}]}""", true)]
    [InlineData("""{"detail":"Schema validation failed: 'home_assistant' is not one of ['voice_satellite'] at spec.connection_type","code":"schema_validation_failed"}""", true)]
    public async Task OnlyA422ThatNamesTheKindMakesItUnsupported(string body, bool unsupported)
    {
        using var api = new LoopbackApi(new JsonArray(Exchange("POST", "/v1/clients", 422, body)));
        var plane = Plane(api, "synthetic-token");
        var options = new CreateClientIdentityOptions("Home Assistant") { ConnectionType = ThalovantConnectionTypes.HomeAssistant };
        var error = await Assert.ThrowsAnyAsync<ThalovantApiException>(() => plane.CreateClientIdentityAsync((JsonObject)KitchenHub.DeepClone(), options));
        Assert.Equal(unsupported, error is ThalovantUnsupportedConnectionTypeException);
        Assert.Equal(422, error.StatusCode);
    }

    [Fact]
    public async Task ARateLimitWhileWaitingForAdmissionIsRiddenOut()
    {
        const string Ready = """{"id":"op-1","kind":"client.sync","aggregate_type":"client","status":"ready","details":{},"created_at":"2026-09-27T10:00:00Z","updated_at":"2026-09-27T10:00:00Z","links":{"self":"/v1/operations/op-1"}}""";
        using var api = new LoopbackApi(new JsonArray(
            Exchange("GET", "/v1/operations/op-1", 429, """{"detail":"Too many requests","code":"token_rate_limited","retry_after_seconds":0.2}"""),
            Exchange("GET", "/v1/operations/op-1", 200, Ready)));
        var plane = Plane(api, "synthetic-token");
        var operation = new OperationResource { Id = "op-1", Links = new Dictionary<string, string?> { ["self"] = "/v1/operations/op-1" } };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await plane.WaitForAdmissionAsync(operation, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(10));
        // Admitted after waiting what the refusal asked for, not the poll interval.
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(150), $"waited {clock.Elapsed}");
        Assert.Equal(2, api.Sent.Count);
        Assert.Empty(api.Mismatches);
    }

    // -- admission -----------------------------------------------------------

    [Theory]
    [MemberData(nameof(AdmissionCases))]
    public async Task AdmissionEndsAsItsVectorSays(string name)
    {
        var vector = Case(Admission, name);
        var call = vector["call"]!.AsObject();
        using var api = new LoopbackApi(vector["exchanges"]!.AsArray());
        // An API out of reach: a port nothing answers on.
        var (unreachable, keeping) = (string?)call["api"] == "unreachable" ? LoopbackApi.Unreachable() : (null, null);
        using var kept = keeping;
        var plane = unreachable is not null
            ? new ThalovantControlPlane(apiUrl: unreachable, accessToken: "synthetic-token")
            : Plane(api, "synthetic-token");
        var operation = call["operation"] is JsonObject raw
            ? JsonSerializer.Deserialize<OperationResource>(Placed(raw, api).ToJsonString())
            : null;
        var expect = vector["expect"]!.AsObject();
        JsonObject produced;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await plane.WaitForAdmissionAsync(
                operation,
                TimeSpan.FromMilliseconds(call["timeout_ms"]!.GetValue<long>()),
                TimeSpan.FromMilliseconds(call["poll_interval_ms"]!.GetValue<long>()));
            produced = new JsonObject { ["outcome"] = "admitted", ["polls"] = api.Sent.Count };
        }
        catch (ThalovantAdmissionTimeoutException error)
        {
            // A connection error and a timeout at once: it may still be admitted.
            Assert.IsAssignableFrom<ThalovantConnectionException>(error);
            Assert.IsAssignableFrom<IThalovantTimeout>(error);
            Assert.EndsWith("it may still admit it later.", error.Message, StringComparison.Ordinal);
            produced = new JsonObject { ["outcome"] = "timeout" };
            if (expect.ContainsKey("polls")) produced["polls"] = api.Sent.Count;
        }
        catch (ThalovantAdmissionFailedException error)
        {
            produced = new JsonObject { ["outcome"] = "failed", ["error_code"] = error.ErrorCode, ["status"] = error.StatusCode };
            if (error.ApiError is ThalovantApiException refusal)
            {
                produced["code"] = refusal.ErrorCode;
                produced["detail"] = refusal.Detail;
            }
            produced["polls"] = api.Sent.Count;
        }
        catch (ThalovantApiUnreachableException)
        {
            produced = new JsonObject { ["outcome"] = "unreachable", ["polls"] = api.Sent.Count };
        }
        catch (ThalovantAuthenticationException error)
        {
            produced = new JsonObject { ["outcome"] = "auth", ["status"] = error.StatusCode, ["polls"] = api.Sent.Count };
        }
        catch (ThalovantApiException)
        {
            produced = new JsonObject { ["outcome"] = "error", ["polls"] = api.Sent.Count };
        }
        if (expect["waited_at_least_ms"] is JsonNode least)
        {
            // Recorded as the bound it met, so every SDK records the same value.
            var bound = least.GetValue<long>();
            var waited = (long)clock.Elapsed.TotalMilliseconds;
            produced["waited_at_least_ms"] = waited >= bound ? bound : waited;
        }
        ConformanceRecord.Record("connection-admission-vectors.json", name, produced.DeepClone());
        Assert.Empty(api.Mismatches);
        Same(vector["expect"]!, produced, name);
    }

    /// <summary>The case's operation with <c>{api_host}</c> and <c>{api_port}</c> filled in with the loopback API's.</summary>
    private static JsonNode Placed(JsonNode node, LoopbackApi api) => node switch
    {
        JsonObject map => new JsonObject(map.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value is null ? null : Placed(pair.Value, api)))),
        JsonArray list => new JsonArray(list.Select(item => item is null ? null : Placed(item, api)).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(
            text.Replace("{api_host}", "127.0.0.1", StringComparison.Ordinal)
                .Replace("{api_port}", api.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))!,
        _ => node.DeepClone(),
    };

    // -- the home link -------------------------------------------------------

    private static HomeRequestHandler Handler(JsonObject spec) => async (request, cancellationToken) =>
    {
        if (spec["raises"]?.GetValue<bool>() == true) throw new InvalidOperationException("the conversation agent is gone");
        if (spec["sleep_ms"] is JsonNode sleep) await Task.Delay(TimeSpan.FromMilliseconds(sleep.GetValue<long>()), cancellationToken);
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
        var kind = (string)vector["kind"]!;
        if (kind == "speech")
        {
            produced = JsonValue.Create(ThalovantHome.PlainSpeech((string)vector["text"]!))!;
        }
        else if (kind == "deadline")
        {
            produced = await Deadline(vector);
        }
        else if (kind == "reply_context")
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
            var timeout = vector["timeout_ms"] is JsonNode milliseconds ? TimeSpan.FromMilliseconds(milliseconds.GetValue<long>()) : (TimeSpan?)null;
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

    /// <summary>
    /// One request answered inside the hub's bound, through a transport that
    /// takes the case's <c>send_ms</c> to put a reply on the wire: whether the
    /// reply went out, and what it said.
    /// </summary>
    private static async Task<JsonObject> Deadline(JsonObject vector)
    {
        var hub = TimeSpan.FromMilliseconds(vector["hub_timeout_ms"]!.GetValue<long>());
        var send = TimeSpan.FromMilliseconds(vector["send_ms"]!.GetValue<long>());
        var request = HomeRequest.FromEvent(new ThalovantEvent(ThalovantHome.RequestEvent, vector["request"]!.DeepClone().AsObject(),
            new JsonObject { ["source"] = "skill" }));
        var wire = new List<JsonObject>();
        async Task Reply(JsonObject payload, CancellationToken cancellationToken)
        {
            await Task.Delay(send, cancellationToken);
            lock (wire) wire.Add(payload);
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sent = await ThalovantHome.AnswerAsync(request, Handler(vector["handler"]!.AsObject()), Reply,
            TimeSpan.FromMilliseconds(vector["timeout_ms"]!.GetValue<long>()), hub);
        // Never past the hub's bound, whatever the handler or the transport did.
        Assert.True(clock.Elapsed <= hub + TimeSpan.FromMilliseconds(100), $"answered after {clock.Elapsed}");
        var produced = new JsonObject { ["replied"] = sent is not null };
        if (sent is not null)
        {
            lock (wire) Assert.True(JsonNode.DeepEquals(sent, Assert.Single(wire)));
            produced["response"] = sent.DeepClone();
        }
        else
        {
            await Task.Delay(send);
            lock (wire) Assert.Empty(wire); // a reply the hub gave up on is withdrawn, not sent late
        }
        return produced;
    }

    [Fact]
    public async Task AHandlerThatIgnoresCancellationDoesNotHoldTheAnswerBack()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sent = await ThalovantHome.AnswerAsync(
            new HomeRequest("s1", "x"),
            (request, cancellationToken) =>
            {
                Thread.Sleep(2000); // blocks, and never looks at its token
                return new ValueTask<HomeAnswer>(HomeAnswer.ActionDone("Too late."));
            },
            (payload, cancellationToken) => Task.CompletedTask,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(1));
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(900), $"took {clock.Elapsed}");
        Assert.Equal("timeout", (string?)sent!["error_code"]);
    }

    [Fact]
    public void TheContractListsMatchTheSdk()
    {
        Assert.Equal(Home["response_types"]!.AsArray().Select(item => (string)item!), HomeResponseTypes.All);
        Assert.Equal(Home["error_codes"]!.AsArray().Select(item => (string)item!), HomeErrorCodes.All);
        Assert.Equal(ThalovantHome.RequestEvent, (string)Home["request_type"]!);
        Assert.Equal(ThalovantHome.ResponseEvent, (string)Home["response_type"]!);
        Assert.Equal(TimeSpan.FromMilliseconds(Home["reply_timeout_ms"]!.GetValue<long>()), ThalovantHome.HubTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(9000), ThalovantHome.DefaultHandlerTimeout);
        Assert.Equal(Device["home_assistant_scopes"]!.AsArray().Select(item => (string)item!), ThalovantHome.HomeAssistantScopes);
    }
}
