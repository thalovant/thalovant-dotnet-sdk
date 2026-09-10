using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    // Keep short deadline regressions independent of CPU-heavy Noise/scrypt fixtures.
    [CollectionDefinition("Runtime deadlines", DisableParallelization = true)]
    public sealed class RuntimeDeadlineCollection { }

    [Collection("Runtime deadlines")]
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
            public Func<CancellationToken, Task>? ConnectAction { get; set; }
            public Func<CancellationToken, Task>? EmitAction { get; set; }
            public Func<CancellationToken, Task>? QueryAction { get; set; }
            public Action<JsonObject>? BusAnswer { get; set; }
            public int BusCount { get { lock (_lock) return _bus.Count; } }
            public int FrameCount { get { lock (_lock) return _frames.Count; } }
            public async Task ConnectAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); if (ConnectAction != null) await ConnectAction(cancellationToken); Connected = true; }
            public Task DisconnectAsync() { Connected = false; return Task.CompletedTask; }
            public Guid AddBusHandler(Action<JsonObject> handler) { var id = Guid.NewGuid(); lock (_lock) _bus[id] = handler; return id; }
            public void RemoveBusHandler(Guid id) { lock (_lock) _bus.Remove(id); }
            public Guid AddQueryHandler(Action<HiveMessage> handler) { var id = Guid.NewGuid(); lock (_lock) _frames[id] = handler; return id; }
            public void RemoveQueryHandler(Guid id) { lock (_lock) _frames.Remove(id); }
            public async Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
            { Emitted.Add(new ThalovantEvent(type, data, context)); if (EmitAction != null) await EmitAction(cancellationToken); BusAnswer?.Invoke(context); }
            public async Task SendQueryFrameAsync(HiveMessage message, CancellationToken cancellationToken)
            { Sent.Add(message); QueryAnswer?.Invoke(message); if (QueryAction != null) await QueryAction(cancellationToken); }
            public void Deliver(string name, string text = "", string? request = null, string? session = null)
            {
                var payload = new JsonObject { ["type"] = name, ["data"] = new JsonObject { ["utterance"] = text },
                    ["context"] = ThalovantContext.WithCorrelation(null, sessionId: session, requestId: request) };
                Action<JsonObject>[] listeners; lock (_lock) listeners = _bus.Values.ToArray();
                foreach (var listener in listeners) listener(payload);
            }
            public void Reply(string id, string name, string text = "", bool cascade = false, string? session = null)
            {
                var bus = HiveWire.BusMessage(name, new JsonObject { ["utterance"] = text }, ThalovantContext.WithCorrelation(null, sessionId: session));
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
        [Theory][InlineData(false)][InlineData(true)]
        public async Task CorrelationReservationEndsWithCollectorCancellation(bool query)
        {
            var fake = new Fake(); using var sdk = Client(fake);
            using var cancelled = new CancellationTokenSource();
            var first = query ? sdk.QueryAsync("cancel", queryId: "shared", cancellationToken: cancelled.Token)
                : sdk.AskAsync("cancel", requestId: "shared", cancellationToken: cancelled.Token);
            await Until(() => query ? fake.Sent.Count == 1 : fake.Emitted.Count == 1);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal(0, query ? fake.FrameCount : fake.BusCount);
            // The fake peer is quiescent; this verifies reservation release,
            // not permission to reuse an ID while old remote replies may arrive.
            var next = query ? sdk.QueryAsync("next", queryId: "shared") : sdk.AskAsync("next", requestId: "shared");
            await Until(() => query ? fake.Sent.Count == 2 : fake.Emitted.Count == 2);
            if (query) { fake.Reply("shared", "speak", "next-only"); fake.Reply("shared", "hive.query.complete"); }
            else fake.Deliver("speak", "next-only", "shared");
            Assert.Equal("next-only", (await next).Text);
        }

        [Fact]
        public async Task AskAndQueryHaveIndependentCorrelationNamespacesAndClients()
        {
            var fake = new Fake(); var other = new Fake();
            using var sdk = Client(fake); using var second = Client(other);
            var ask = sdk.AskAsync("ask", requestId: "shared");
            var query = sdk.QueryAsync("query", queryId: "shared");
            var remote = second.AskAsync("other", requestId: "shared");
            await Until(() => fake.Emitted.Count == 1 && fake.Sent.Count == 1 && other.Emitted.Count == 1);
            fake.Deliver("speak", "ask-only", "shared");
            fake.Reply("shared", "speak", "query-only"); fake.Reply("shared", "hive.query.complete");
            other.Deliver("speak", "other-only", "shared");
            Assert.Equal("ask-only", (await ask).Text); Assert.Equal("query-only", (await query).Text);
            Assert.Equal("other-only", (await remote).Text);
        }

        [Theory][InlineData(false)][InlineData(true)]
        public async Task DuplicateLiveCorrelationIdIsRejectedBeforeSecondDispatch(bool query)
        {
            var fake = new Fake(); using var sdk = Client(fake);
            Task<ThalovantReply> Start(string prompt) => query
                ? sdk.QueryAsync(prompt, requestId: prompt, queryId: "shared")
                : sdk.AskAsync(prompt, requestId: "shared");
            var owner = Start("owner");
            await Until(() => query ? fake.Sent.Count == 1 : fake.Emitted.Count == 1);
            var duplicate = Start("duplicate");
            await Until(() => duplicate.IsCompleted || (query ? fake.FrameCount == 2 : fake.BusCount == 2));
            if (query) { fake.Reply("shared", "speak", "only-owner"); fake.Reply("shared", "hive.query.complete"); }
            else fake.Deliver("speak", "only-owner", "shared");
            Assert.Equal("only-owner", (await owner).Text);
            await Assert.ThrowsAsync<ThalovantRuntimeException>(() => duplicate);
            Assert.Equal(1, query ? fake.Sent.Count : fake.Emitted.Count);
            Assert.Equal(0, query ? fake.FrameCount : fake.BusCount);
        }

        [Theory][InlineData(false)][InlineData(true)]
        public async Task AskReportsTheNegativeReplyWindowParameter(bool empty)
        {
            var fake = new Fake(); using var sdk = Client(fake);
            var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sdk.AskAsync("test",
                emptyReplyWait: empty ? TimeSpan.FromMilliseconds(-1) : TimeSpan.Zero,
                replySettle: empty ? TimeSpan.Zero : TimeSpan.FromMilliseconds(-1)));
            Assert.Equal(empty ? "emptyReplyWait" : "replySettle", error.ParamName);
            Assert.Empty(fake.Emitted); Assert.Equal(0, fake.BusCount);
        }
        [Theory]
        [InlineData("connect")][InlineData("send")][InlineData("empty")][InlineData("settle")][InlineData("no_speech")]
        public async Task AskBudgetIncludesAllPhases(string phase)
        {
            var fake = new Fake(); using var sdk = Client(fake);
            if (phase == "connect") fake.ConnectAction = token => Task.Delay(60000, token);
            if (phase == "send") fake.EmitAction = token => Task.Delay(60000, token);
            fake.BusAnswer = context => fake.Deliver((phase == "empty" || phase == "no_speech") ? ThalovantEvents.UtteranceHandled : "speak", "answer", (string?)context["request_id"]);
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var request = sdk.AskAsync("test", phase == "no_speech" ? TimeSpan.FromSeconds(60) : TimeSpan.FromMilliseconds(250), emptyReplyWait: phase == "no_speech" ? TimeSpan.Zero : TimeSpan.FromSeconds(60), replySettle: (phase == "settle" || phase == "no_speech") ? TimeSpan.FromSeconds(60) : TimeSpan.Zero, cancellationToken: watchdog.Token);
            if (phase == "settle") Assert.Equal("answer", (await request.WaitAsync(TimeSpan.FromSeconds(2))).Text);
            else await Assert.ThrowsAsync<ThalovantTimeoutException>(() => request.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, fake.BusCount);
        }
        [Fact] public async Task AskHardFailureFreezesCollectionAndSkipsSettle()
        {
            foreach (var partial in new[] { false, true }) {
                var fake = new Fake(); using var sdk = Client(fake);
                fake.BusAnswer = context => {
                    var id = (string?)context["request_id"];
                    if (partial) fake.Deliver("speak", "partial", id);
                    fake.Deliver(ThalovantEvents.PolicyDenied, request: id);
                    fake.Deliver("speak", "ignored", id);
                };
                using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                // A hard terminal reply must interrupt the long request/settle
                // windows; fixture startup speed is not part of this assertion.
                var request = sdk.AskAsync("test", TimeSpan.FromSeconds(60), replySettle: TimeSpan.FromSeconds(60), cancellationToken: watchdog.Token);
                if (partial) { var reply = await request; Assert.Equal("partial", reply.Text); Assert.False(reply.Ok); Assert.Equal(2, reply.Events.Count); }
                else await Assert.ThrowsAsync<ThalovantRuntimeException>(() => request);
                Assert.Equal(0, fake.BusCount);
            }
        }
        [Fact] public async Task AskCallerCancellationRemovesWaitingHandler()
        {
            var fake = new Fake(); using var sdk = Client(fake);
            using var cancellation = new CancellationTokenSource();
            var request = sdk.AskAsync("test", cancellationToken: cancellation.Token);
            await Until(() => fake.BusCount == 1); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(0, fake.BusCount);
        }
        [Theory][InlineData(false)][InlineData(true)]
        public async Task PausedEventStreamRetiresSubscriptionOnCancellationOrDeadline(bool expire)
        {
            var fake = new Fake(); using var sdk = Client(fake);
            using var cancellation = new CancellationTokenSource();
            await using var iterator = sdk.ListenAsync("speak", expire ? TimeSpan.FromMilliseconds(50) : null, cancellationToken: cancellation.Token).GetAsyncEnumerator();
            var next = iterator.MoveNextAsync().AsTask();
            fake.Deliver("speak", "one"); Assert.True(await next);
            if (!expire) cancellation.Cancel();
            await Until(() => fake.BusCount == 0);
        }
        [Theory][InlineData(false)][InlineData(true)]
        public async Task AskReturnsCollectedSpeechWhileAdmittedSendRetires(bool hard)
        {
            var fake = new Fake(); using var sdk = Client(fake);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.EmitAction = async token => {
                var id = fake.Emitted.Last().RequestId;
                fake.Deliver("speak", "answer", id);
                if (hard) { fake.Deliver(ThalovantEvents.PolicyDenied, request: id); fake.Deliver("speak", "ignored", id); }
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { await release.Task; retired.SetResult(); }
            };
            var request = sdk.AskAsync("test", hard ? TimeSpan.FromSeconds(60) : TimeSpan.FromMilliseconds(250), replySettle: TimeSpan.FromSeconds(60));
            try {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var reply = await request.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal("answer", reply.Text); Assert.Equal(!hard, reply.Ok);
                Assert.False(retired.Task.IsCompleted); Assert.Equal(0, fake.BusCount);
            } finally { release.TrySetResult(); }
            await retired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        [Theory][InlineData(false)][InlineData(true)]
        public async Task QueryTerminalReplyDoesNotWaitForAdmittedWrite(bool hard)
        {
            var fake = new Fake(); using var sdk = Client(fake);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fake.QueryAction = async token => {
                fake.Reply("q", "speak", "answer");
                fake.Reply("q", hard ? ThalovantEvents.PolicyDenied : "hive.query.complete");
                fake.Reply("q", "speak", "ignored");
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { await release.Task; retired.SetResult(); }
            };
            var pending = sdk.QueryAsync("test", TimeSpan.FromSeconds(60), queryId: "q");
            try {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var reply = await pending.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal("answer", reply.Text); Assert.Equal(!hard, reply.Ok); Assert.Equal(2, reply.Events.Count);
                Assert.Single(fake.Sent); Assert.Equal(0, fake.FrameCount); Assert.False(retired.Task.IsCompleted);
            } finally { release.TrySetResult(); }
            await retired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        [Theory][InlineData("speak")][InlineData(ThalovantEvents.UtteranceHandled)]
        public async Task AskSurfacesWriteFailureAfterProgress(string progress)
        {
            var fake = new Fake(); using var sdk = Client(fake);
            fake.EmitAction = _ => {
                fake.Deliver(progress, "answer", fake.Emitted.Last().RequestId);
                throw new ThalovantConnectionException("write failed after progress");
            };
            var error = await Assert.ThrowsAsync<ThalovantConnectionException>(() => sdk.AskAsync("test",
                TimeSpan.FromSeconds(60), replySettle: TimeSpan.FromSeconds(60), emptyReplyWait: TimeSpan.FromSeconds(60)).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("write failed after progress", error.Message);
            Assert.Single(fake.Emitted); Assert.Equal(0, fake.BusCount);
        }
        [Fact]
        public async Task AskExpiredSpeechWindowTakesPrecedenceOverLateWriteError()
        {
            var fake = new Fake(); using var sdk = Client(fake);
            fake.EmitAction = _ => {
                var id = fake.Emitted.Last().RequestId;
                fake.Deliver("speak", "answer", id);
                // Zero settling has already completed the response, even if
                // the collector continuation has not resumed yet.
                fake.Deliver("speak", "late", id);
                throw new ThalovantConnectionException("late write error");
            };
            var reply = await sdk.AskAsync("test", TimeSpan.FromSeconds(60), replySettle: TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("answer", reply.Text); Assert.True(reply.Ok); Assert.Single(reply.Events);
            Assert.Single(fake.Emitted); Assert.Equal(0, fake.BusCount);
        }
        [Theory][InlineData(false)][InlineData(true)]
        public async Task AskTerminalFailureTakesPrecedenceOverLateWriteError(bool partial)
        {
            var fake = new Fake(); using var sdk = Client(fake);
            fake.EmitAction = _ => {
                var id = fake.Emitted.Last().RequestId;
                if (partial) fake.Deliver("speak", "answer", id);
                fake.Deliver(ThalovantEvents.PolicyDenied, request: id);
                throw new ThalovantConnectionException("late write error");
            };
            var request = sdk.AskAsync("test", TimeSpan.FromSeconds(60), replySettle: TimeSpan.FromSeconds(60));
            if (partial) { var reply = await request.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("answer", reply.Text); Assert.False(reply.Ok); }
            else {
                var error = await Assert.ThrowsAsync<ThalovantRuntimeException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Contains(ThalovantEvents.PolicyDenied, error.Message);
            }
            Assert.Single(fake.Emitted); Assert.Equal(0, fake.BusCount);
        }
        [Fact] public async Task AskReturnsFirstCorrelatedRuntimeSessionReplacement()
        {
            var fake = new Fake(); using var sdk = Client(fake);
            fake.BusAnswer = context => {
                var id = (string?)context["request_id"];
                fake.Deliver("speak", "ignored", "other", "foreign");
                fake.Deliver(ThalovantEvents.UtteranceHandled, request: id, session: "runtime-first");
                fake.Deliver("speak", "answer", id, "runtime-later");
            };
            var reply = await sdk.AskAsync("test", sessionId: "requested", requestId: "r", emptyReplyWait: TimeSpan.FromSeconds(1));
            Assert.Equal("runtime-first", reply.SessionId); Assert.Equal("r", reply.RequestId); Assert.Equal("answer", reply.Text);
            Assert.Equal(new[] { "runtime-first", "runtime-later" }, reply.Events.Select(e => e.SessionId));
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
        [Fact]
        public async Task QueryReturnsFirstAcceptedRuntimeSessionReplacement()
        {
            var fake = new Fake(); var sdk = Client(fake);
            fake.QueryAnswer = _ => {
                fake.Reply("foreign", "speak", "ignored", session: "foreign");
                fake.Reply("q", ThalovantEvents.UtteranceHandled, session: "runtime-first");
                fake.Reply("q", "speak", "answer", session: "runtime-later");
                fake.Reply("q", "hive.query.complete", session: "runtime-final");
            };
            var reply = await sdk.QueryAsync("test", sessionId: "requested", requestId: "r", queryId: "q");
            Assert.Equal("runtime-first", reply.SessionId); Assert.Equal("r", reply.RequestId);
            Assert.Equal("answer", reply.Text); Assert.Equal(3, reply.Events.Count);
        }

        [Fact] public async Task QuerySoftMissesAllowSpeechAndHardFailuresRetainPartialSpeech()
        {
            foreach (var miss in new[] { ThalovantEvents.IntentUnmatched, ThalovantEvents.IntentFailure }) {
                var fake = new Fake(); using var sdk = Client(fake);
                fake.QueryAnswer = _ => { fake.Reply("q", miss); fake.Reply("q", "speak", "answer"); fake.Reply("q", "hive.query.complete"); };
                var reply = await sdk.QueryAsync("test", queryId: "q");
                Assert.Equal("answer", reply.Text); Assert.True(reply.Ok); Assert.Null(reply.FailureEvent);
                Assert.Equal(new[] { miss, "speak", "hive.query.complete" }, reply.Events.Select(item => item.Name));
                fake.QueryAnswer = _ => { fake.Reply("q", miss); fake.Reply("q", "hive.query.complete"); };
                await Assert.ThrowsAsync<ThalovantRuntimeException>(() => sdk.QueryAsync("test", queryId: "q"));
            }
            foreach (var hard in new[] { ThalovantEvents.PolicyDenied, ThalovantEvents.QueryTimeout }) {
                var fake = new Fake(); using var sdk = Client(fake);
                fake.QueryAnswer = _ => { fake.Reply("q", "speak", "partial"); fake.Reply("q", hard); fake.Reply("q", "speak", "ignored"); };
                var reply = await sdk.QueryAsync("test", queryId: "q");
                Assert.Equal("partial", reply.Text); Assert.False(reply.Ok); Assert.Equal(hard, reply.FailureEvent?.Name);
            }
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
        [Fact] public async Task StreamOverflowIsExplicitAndClosesSubscription()
        {
            var fake = new Fake(); using var sdk = Client(fake);
            await using var iterator = sdk.ListenAsync("speak").GetAsyncEnumerator();
            var first = iterator.MoveNextAsync().AsTask(); fake.Deliver("speak"); Assert.True(await first);
            for (var i = 0; i < 65; i++) fake.Deliver("speak");
            await Assert.ThrowsAsync<ThalovantRuntimeException>(() => iterator.MoveNextAsync().AsTask());
            Assert.Equal(0, fake.BusCount);
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
