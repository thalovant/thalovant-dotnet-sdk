using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests;

/// <summary>
/// Keeping a hub link up, against <c>link-keeping-vectors.json</c>, vendored byte
/// for byte from the Python reference.
/// </summary>
/// <remarks>
/// <c>close</c> cases hold the transport's reading of a close to the vectors;
/// <c>handshake</c> cases run one real connect as a kept link makes it -- a
/// WebSocket upgrade, a Noise handshake, then the settle window -- against a
/// loopback hub in the case's situation and list the patterns the hub saw; <c>supervise</c> cases drive the supervisor
/// <see cref="HubSession.RunAsync"/> asks after every attempt.
/// </remarks>
[Collection("Runtime deadlines")]
public sealed class LinkKeepingVectorTests
{
    private static readonly JsonObject Vectors = JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "link-keeping-vectors.json")))!.AsObject();

    private static JsonObject Policy => Vectors["policy"]!.AsObject();

    private static TheoryData<string> Names(string kind)
    {
        var names = new TheoryData<string>();
        foreach (var item in Vectors["cases"]!.AsArray().Where(item => (string)item!["kind"]! == kind)) names.Add((string)item!["name"]!);
        return names;
    }

    public static TheoryData<string> CloseCases => Names("close");
    public static TheoryData<string> HandshakeCases => Names("handshake");
    public static TheoryData<string> SuperviseCases => Names("supervise");

    private static JsonObject Case(string name) =>
        Vectors["cases"]!.AsArray().Single(item => (string)item!["name"]! == name)!.AsObject();

    private static void Record(string name, JsonNode produced, JsonObject vector)
    {
        // Recorded before the assert: what this SDK produced.
        ConformanceRecord.Record("link-keeping-vectors.json", name, produced.DeepClone());
        Assert.True(JsonNode.DeepEquals(vector["expect"], produced), $"{name}: produced {produced.ToJsonString()}");
    }

    [Fact]
    public void ThePolicyIsTheSdks()
    {
        var defaults = new HubSessionPolicy();
        Assert.Equal(Policy["retry_ms"]!.GetValue<long>(), (long)(defaults.RetrySeconds * 1000));
        Assert.Equal(Policy["retry_ceiling_ms"]!.GetValue<long>(), (long)(defaults.RetryCeilingSeconds * 1000));
        Assert.Equal(Policy["probe_ms"]!.GetValue<long>(), (long)(defaults.ProbeSeconds * 1000));
        Assert.Equal(Policy["probe_down_ms"]!.GetValue<long>(), (long)(defaults.ProbeDownSeconds * 1000));
        Assert.Equal(Policy["refusal_grace_ms"]!.GetValue<long>(), (long)(defaults.RefusalGraceSeconds * 1000));
        Assert.Equal(HiveMindWssTransport.RefusalSettleMs, Policy["settle_ms"]!.GetValue<long>());
        Assert.Equal(HiveMindWssTransport.CloseCodeGraceMs, Policy["close_code_grace_ms"]!.GetValue<long>());
        Assert.Equal(Policy["refusal_close_codes"]!.AsArray().Select(code => code!.GetValue<int>()), HiveMindWssTransport.RefusalCloseCodes);
        var session = new HubSession(_ => throw new InvalidOperationException(), warm: false);
        Assert.Equal(Policy["settle_ms"]!.GetValue<long>(), (long)session.SettleWindow.TotalMilliseconds);
    }

    [Theory]
    [MemberData(nameof(CloseCases))]
    public void ACloseIsReadAsItsVectorSays(string name)
    {
        var vector = Case(name);
        var after = (string)vector["when"]! == "after_handshake" ? vector["after_ms"]!.GetValue<long>() : (long?)null;
        var refused = HiveMindWssTransport.CloseRefuses(
            vector["code"]?.GetValue<int>(), after, vector["code_late_ms"]?.GetValue<long>() ?? 0,
            vector["after_authenticated_frame"]?.GetValue<bool>() ?? false);
        Record(name, new JsonObject { ["outcome"] = refused ? "refused" : "dropped" }, vector);
    }

    private static string Outcome(ThalovantConnectionException? error) => error switch
    {
        null => "connected",
        ThalovantClientKeyRejectedException => "client_key_rejected",
        ThalovantHubRefusedException => "refused",
        ThalovantHubKeyChangedException => "key_changed",
        _ => "failed",
    };

    [Theory]
    [MemberData(nameof(HandshakeCases))]
    public async Task AHandshakeEndsAsItsVectorSays(string name)
    {
        var vector = Case(name);
        using var hub = new LoopbackHub();
        var store = new HubPeer.Store();
        var situation = (string)vector["situation"]!;
        if (situation is "pinned" or "password_changed_since_pinning" or "hub_key_changed"
            or "client_key_changed" or "client_key_changed_pinned_here")
        {
            Assert.Null(await hub.AttemptAsync(store)); // first contact pins both ways
        }
        var password = "test-password";
        switch (situation)
        {
            case "wrong_password":
                password = "a-wrong-password";
                break;
            case "password_changed_since_pinning":
                hub.Password = "the-password-now"; // the hub's side changed
                break;
            case "hub_key_changed":
                hub.StaticKey = Noise.RandomKey(); // the hub was replaced
                hub.OfferKK = vector["hub_offers_kk"]!.GetValue<bool>();
                break;
            case "upgrade_status":
                hub.UpgradeStatus = vector["status"]!.GetValue<int>();
                break;
            case "client_key_changed":
                store = new HubPeer.Store(); // another program: its own folder, its own key
                break;
            case "client_key_changed_pinned_here":
                store.ReplaceKey(); // a new key, the hub pins kept
                break;
            case "closed_after_first_frame":
                hub.CloseAfterHandshake = true;
                hub.CloseAfterHandshakeSpeaks = true;
                break;
        }
        var before = hub.Patterns.Count;
        var outcome = Outcome(await SettledAttemptAsync(hub, store, password));
        var patterns = hub.Patterns.Skip(before).Select(pattern => (JsonNode?)JsonValue.Create(pattern.Substring(0, 2))).ToArray();
        Record(name, new JsonObject { ["outcome"] = outcome, ["patterns"] = new JsonArray(patterns) }, vector);
    }

    /// <summary>
    /// One connect as a kept link makes it -- the handshake, then the settle
    /// window -- and what it came to: a close that lands just after the
    /// handshake would otherwise race the connect returning.
    /// </summary>
    private static async Task<ThalovantConnectionException?> SettledAttemptAsync(LoopbackHub hub, IHiveMindNoiseStore store, string password)
    {
        await using var session = new HubSession(async cancellationToken =>
        {
            var client = hub.Client(store, password);
            try
            {
                await client.ConnectAsync(TimeSpan.FromSeconds(15), cancellationToken);
                return client;
            }
            catch
            {
                await client.CloseAsync();
                throw;
            }
        }, warm: false);
        session.SettleWindow = TimeSpan.FromMilliseconds(Policy["settle_ms"]!.GetValue<long>());
        try
        {
            await session.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(30));
            return null;
        }
        catch (ThalovantConnectionException error)
        {
            return error;
        }
    }

    [Theory]
    [MemberData(nameof(SuperviseCases))]
    public void ASupervisorDecidesAsItsVectorSays(string name)
    {
        var vector = Case(name);
        var supervisor = new LinkSupervisor(new HubSessionPolicy(
            Policy["retry_ms"]!.GetValue<long>() / 1000.0,
            Policy["retry_ceiling_ms"]!.GetValue<long>() / 1000.0,
            Policy["probe_ms"]!.GetValue<long>() / 1000.0,
            Policy["probe_down_ms"]!.GetValue<long>() / 1000.0,
            Policy["refusal_grace_ms"]!.GetValue<long>() / 1000.0));
        var produced = new JsonArray();
        foreach (var item in vector["events"]!.AsArray())
        {
            var outcome = (string)item!["outcome"]! switch
            {
                "up" => LinkOutcome.Up,
                "dropped" => LinkOutcome.Dropped,
                "failed" => LinkOutcome.Failed,
                "refused" => LinkOutcome.Refused,
                "key_changed" => LinkOutcome.KeyChanged,
                "client_key_rejected" => LinkOutcome.ClientKeyRejected,
                var other => throw new InvalidDataException(other),
            };
            var decision = supervisor.After(outcome, item["at_ms"]!.GetValue<long>() / 1000.0);
            produced.Add(decision.Action switch
            {
                LinkAction.Retry => new JsonObject { ["action"] = "retry", ["wait_ms"] = (long)Math.Round(decision.Wait.TotalMilliseconds) },
                LinkAction.GiveUp => new JsonObject { ["action"] = "give_up", ["reason"] = decision.Reason },
                _ => new JsonObject { ["action"] = "hold" },
            });
        }
        Record(name, produced, vector);
    }

    private static HubSession Session(LoopbackHub hub, IHiveMindNoiseStore store, HubSessionPolicy? policy = null) =>
        new HubSession(async cancellationToken =>
        {
            var client = hub.Client(store);
            try
            {
                await client.ConnectAsync(TimeSpan.FromSeconds(15), cancellationToken);
                return client;
            }
            catch
            {
                await client.CloseAsync();
                throw;
            }
        }, policy, warm: false);

    [Theory]
    [InlineData(null, true)]
    [InlineData(WebSocketCloseStatus.NormalClosure, true)]
    [InlineData(WebSocketCloseStatus.PolicyViolation, true)]
    [InlineData(WebSocketCloseStatus.InternalServerError, false)]
    [InlineData(WebSocketCloseStatus.EndpointUnavailable, false)]
    public async Task ARealCloseRightAfterTheHandshake(WebSocketCloseStatus? status, bool refused)
    {
        using var hub = new LoopbackHub { CloseAfterHandshake = true, CloseAfterHandshakeWith = status };
        await using var session = Session(hub, new HubPeer.Store());
        session.SettleWindow = TimeSpan.FromMilliseconds(500);
        var error = await Assert.ThrowsAnyAsync<ThalovantConnectionException>(() => session.ConnectAsync());
        Assert.Equal(refused, error is ThalovantHubRefusedException);
    }

    [Fact]
    public async Task RunStopsAtOnceWhenTheHubKeyChanged()
    {
        using var hub = new LoopbackHub();
        var store = new HubPeer.Store();
        Assert.Null(await hub.AttemptAsync(store));
        hub.StaticKey = Noise.RandomKey();
        await using var session = Session(hub, store, new HubSessionPolicy(0.05, 0.1, 0.05, 0.05, refusalGraceSeconds: 30));
        session.SettleWindow = TimeSpan.FromMilliseconds(50);
        // KK against the old key fails, XX follows at once and meets the pin:
        // RunAsync ends there rather than retrying for ever.
        await Assert.ThrowsAsync<ThalovantHubKeyChangedException>(() => session.RunAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(new[] { "KKpsk0", "XXpsk2" }, hub.Patterns.TakeLast(2));
        Assert.Equal(3, hub.Attempts); // the pinning connect, then KK and XX
    }

    [Fact]
    public async Task AKeyTheHubDidNotPinNamesBothFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "thalovant-key-rejected-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var hub = new LoopbackHub();
            var first = Path.Combine(root, "satellite");
            var second = Path.Combine(root, "cli");
            Assert.Null(await hub.AttemptAsync(new HiveMindFileNoiseStore(first))); // the hub pins this key
            var error = await SettledAttemptAsync(hub, new HiveMindFileNoiseStore(second), "test-password");
            var rejected = Assert.IsType<ThalovantClientKeyRejectedException>(error);
            Assert.IsAssignableFrom<ThalovantHubRefusedException>(rejected); // caught where any refusal is
            Assert.Equal(Path.GetFullPath(second), rejected.KeyFolder);
            Assert.Equal(Path.GetFullPath(HiveMindFileNoiseStore.DefaultDirectory), rejected.OtherKeyFolder);
            Assert.Contains(Path.GetFullPath(second), rejected.Message, StringComparison.Ordinal);
            Assert.Contains("Re-pair, or share the key folder", rejected.Message, StringComparison.Ordinal);
            Assert.Equal(new[] { "XXpsk2", "XXpsk2" }, hub.Patterns);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AfterKKTheSameCloseIsAPlainRefusal()
    {
        using var hub = new LoopbackHub();
        var store = new HubPeer.Store();
        Assert.Null(await hub.AttemptAsync(store));
        // The hub could only complete KK with the key it pinned: this close says
        // nothing about the client's key.
        hub.CloseAfterHandshake = true;
        var error = await SettledAttemptAsync(hub, store, "test-password");
        Assert.IsType<ThalovantHubRefusedException>(error);
        Assert.Equal("KKpsk0", hub.Patterns[^1]);
    }

    [Fact]
    public async Task RunStopsAtOnceWhenTheHubRefusesTheClientsKey()
    {
        using var hub = new LoopbackHub();
        Assert.Null(await hub.AttemptAsync(new HubPeer.Store()));
        await using var session = Session(hub, new HubPeer.Store(), new HubSessionPolicy(0.05, 0.1, 0.05, 0.05, refusalGraceSeconds: 30));
        // A refusal would be retried for 30 seconds; this one ends RunAsync on the first.
        await Assert.ThrowsAsync<ThalovantClientKeyRejectedException>(() => session.RunAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(2, hub.Attempts); // the pinning connect, then the one refused
    }

    [Theory]
    [InlineData("The server returned status code '401' when status code '101' was expected.", 401)]
    [InlineData("The server returned status code '403' when status code '101' was expected.", 403)]
    [InlineData("The server returned status code '503' when status code '101' was expected.", 503)]
    [InlineData("Unable to connect to the remote server", null)]
    public void WithoutAStatusOnTheSocketTheUpgradeStatusIsReadFromTheMessage(string message, int? status)
    {
        // netstandard2.1 (Unity) has no ClientWebSocket.HttpStatusCode: this is how it tells a refused upgrade.
        Assert.Equal(status, HiveMindWssTransport.UpgradeStatus(message));
    }
}
