using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    public sealed class RuntimeTests
    {
        private sealed class Fake : IHiveMindBus, IHiveMindQueryBus, IHiveMindRuntimeStatus
        {
            private readonly object _lock = new object();
            private readonly Dictionary<Guid, Action<JsonObject>> _bus = new Dictionary<Guid, Action<JsonObject>>();
            private readonly Dictionary<Guid, Action<HiveMessage>> _frames = new Dictionary<Guid, Action<HiveMessage>>();
            public bool Connected { get; private set; }
            public bool HandshakeComplete => Connected;
            public List<ThalovantEvent> Emitted { get; } = new List<ThalovantEvent>();
            public List<HiveMessage> Sent { get; } = new List<HiveMessage>();
            public Action<HiveMessage>? QueryAnswer { get; set; }
            public int BusCount { get { lock (_lock) return _bus.Count; } }
            public int FrameCount { get { lock (_lock) return _frames.Count; } }
            public Task ConnectAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Connected = true; return Task.CompletedTask; }
            public Task DisconnectAsync() { Connected = false; return Task.CompletedTask; }
            public Guid AddBusHandler(Action<JsonObject> handler) { var id = Guid.NewGuid(); lock (_lock) _bus[id] = handler; return id; }
            public void RemoveBusHandler(Guid id) { lock (_lock) _bus.Remove(id); }
            public Guid AddQueryHandler(Action<HiveMessage> handler) { var id = Guid.NewGuid(); lock (_lock) _frames[id] = handler; return id; }
            public void RemoveQueryHandler(Guid id) { lock (_lock) _frames.Remove(id); }
            public Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
            { Emitted.Add(new ThalovantEvent(type, data, context)); return Task.CompletedTask; }
            public Task SendQueryFrameAsync(HiveMessage message, CancellationToken cancellationToken)
            { Sent.Add(message); QueryAnswer?.Invoke(message); return Task.CompletedTask; }
            public void Deliver(string name, string text = "", string? request = null, string? session = null)
            {
                var payload = new JsonObject { ["type"] = name, ["data"] = new JsonObject { ["utterance"] = text },
                    ["context"] = ThalovantContext.WithCorrelation(null, sessionId: session, requestId: request) };
                Action<JsonObject>[] listeners; lock (_lock) listeners = _bus.Values.ToArray();
                foreach (var listener in listeners) listener(payload);
            }
            public void Reply(string id, string name, string text = "", bool cascade = false)
            {
                var bus = HiveWire.BusMessage(name, new JsonObject { ["utterance"] = text }, new JsonObject());
                var frame = new HiveMessage(cascade ? "cascade" : "query", bus.ToJsonObject(), new JsonObject { ["query_id"] = id });
                Action<HiveMessage>[] listeners; lock (_lock) listeners = _frames.Values.ToArray();
                foreach (var listener in listeners) listener(frame);
            }
        }
        private static ThalovantClient Client(Fake fake) => new ThalovantClient(ThalovantIdentity.FromJson("""
            {"access_key":"fixture","password":"password","site_id":"fixture","default_master":"wss://hub.example"}
            """), fake, replySettle: TimeSpan.Zero, emptyReplyWait: TimeSpan.Zero);
        private static async Task Until(Func<bool> predicate)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!predicate()) await Task.Delay(1, deadline.Token);
        }
        [Fact] public async Task QueryRejectsForeignIdsAndNormalizesNestedCascadeSpeech()
        {
            var fake = new Fake(); using var sdk = Client(fake);
            fake.QueryAnswer = _ => {
                fake.Reply("foreign", "speak", "wrong"); fake.Reply("q", "speak", " first   part ");
                fake.Reply("q", "speak", "first part"); fake.Reply("q", "ovos.utterance.speak", "second", true);
                fake.Reply("q", "hive.query.complete", cascade: true);
            };
            var reply = await sdk.QueryAsync("hello", sessionId: "s", requestId: "r", queryId: "q");
            Assert.Equal("first part second", reply.Text); Assert.True(reply.Ok);
            Assert.Equal("s", reply.SessionId); Assert.Equal("r", reply.RequestId);
            Assert.Equal("query", fake.Sent.Single().MsgType);
            Assert.Equal("r", (string?)fake.Sent.Single().Payload["payload"]!["context"]!["request_id"]);
            Assert.Equal(0, fake.FrameCount);
        }
        [Fact] public async Task QueryFailureSilenceCancellationAndLossRemoveHandlers()
        {
            var fake = new Fake(); using var sdk = Client(fake);
            fake.QueryAnswer = _ => fake.Reply("q", ThalovantEvents.PolicyDenied);
            await Assert.ThrowsAsync<ThalovantRuntimeException>(() => sdk.QueryAsync("test", queryId: "q"));
            Assert.Equal(0, fake.FrameCount); fake.QueryAnswer = null;
            await Assert.ThrowsAsync<ThalovantTimeoutException>(() => sdk.QueryAsync("test", TimeSpan.FromMilliseconds(20)));
            using var cancellation = new CancellationTokenSource();
            var pending = sdk.QueryAsync("test", cancellationToken: cancellation.Token); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.Equal(0, fake.FrameCount);
            var lost = sdk.QueryAsync("test"); await fake.DisconnectAsync();
            await Assert.ThrowsAsync<ThalovantConnectionException>(() => lost.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, fake.FrameCount);
        }
        [Fact] public async Task EventWaitFiltersWithRequestPrecedenceAndCleansHandlers()
        {
            var fake = new Fake(); using var sdk = Client(fake);
            var pending = sdk.WaitForEventAsync("speak", sessionId: "mine", requestId: "r", predicate: item => item.Text == "yes");
            fake.Deliver("speak", "yes", "wrong", "mine"); fake.Deliver("speak", "no", "r", "rewritten"); Assert.False(pending.IsCompleted);
            fake.Deliver("speak", "yes", "r", "rewritten"); Assert.Equal("yes", (await pending).Text); Assert.Equal(0, fake.BusCount);
            await Assert.ThrowsAsync<ThalovantTimeoutException>(() => sdk.WaitForEventAsync("never", TimeSpan.FromMilliseconds(20)));
            using var cancellation = new CancellationTokenSource();
            var cancelled = sdk.WaitForEventAsync("never", cancellationToken: cancellation.Token); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled); Assert.Equal(0, fake.BusCount);
        }
        [Fact] public async Task EventStreamStopsAtCountDeadlineCancellationAndLoss()
        {
            var fake = new Fake(); using var sdk = Client(fake);
            static async Task<List<ThalovantEvent>> Read(IAsyncEnumerable<ThalovantEvent> source) {
                var events = new List<ThalovantEvent>(); await foreach (var item in source) events.Add(item); return events;
            }
            var pending = Read(sdk.ListenAsync("speak", maxEvents: 2));
            fake.Deliver("other"); fake.Deliver("speak"); fake.Deliver("speak");
            Assert.Equal(2, (await pending).Count); Assert.Equal(0, fake.BusCount);
            Assert.Empty(await Read(sdk.ListenAsync("never", TimeSpan.FromMilliseconds(20)))); Assert.Equal(0, fake.BusCount);
            using var cancellation = new CancellationTokenSource();
            var cancelled = Read(sdk.ListenAsync("never", cancellationToken: cancellation.Token)); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled); Assert.Equal(0, fake.BusCount);
            var lost = Read(sdk.ListenAsync("never")); await fake.DisconnectAsync();
            await Assert.ThrowsAsync<ThalovantConnectionException>(() => lost.WaitAsync(TimeSpan.FromSeconds(2))); Assert.Equal(0, fake.BusCount);
        }
        [Fact] public async Task ConversationSharesSessionAndPreservesExactCodeMetadata()
        {
            var fake = new Fake(); using var sdk = Client(fake);
            var original = new JsonObject { ["input"] = new JsonObject { ["custom"] = "keep" } };
            var scope = sdk.Conversation("stable", "fr-fr", original);
            await scope.SendActionAsync(" launch ", "Go"); await scope.SendCodeAsync(" 001-09 ", label: "Ticket");
            var action = fake.Emitted[0]; var code = fake.Emitted[1];
            Assert.Equal("stable", action.SessionId); Assert.Equal(action.SessionId, code.SessionId); Assert.NotEqual(action.RequestId, code.RequestId);
            Assert.Equal("launch", action.Utterances.Single()); Assert.Equal("keep", (string?)action.Context["input"]!["custom"]);
            Assert.Equal("001-09", code.Utterances.Single()); Assert.True((bool)code.Data["input"]!["exact"]!);
            Assert.Equal("fr-fr", (string?)code.Data["lang"]); Assert.Null(original["input"]!["kind"]);
            Assert.True((await sdk.HealthcheckAsync()).Ok); Assert.True((await sdk.DoctorAsync()).Ok);
            Assert.Equal("ready", (await sdk.ConnectWithInfoAsync()).Phase);
            await fake.DisconnectAsync(); Assert.True((await sdk.HealthcheckAsync()).Ok);
        }
    }
}
