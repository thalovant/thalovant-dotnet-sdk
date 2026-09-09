using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Thalovant
{
    /// <summary>Local connection status; endpoint URLs and credentials are excluded.</summary>
    public sealed class ThalovantConnectionInfo
    {
        public string Phase { get; }
        public string? LastError { get; }
        public ThalovantConnectionInfo(string phase = "idle", string? lastError = null) { Phase = phase; LastError = lastError; }
    }
    public sealed class ThalovantHealth
    {
        public bool Connected { get; }
        public bool HandshakeComplete { get; }
        public bool TransportAlive { get; }
        public ThalovantConnectionInfo Connection { get; }
        public bool Ok => Connected && HandshakeComplete && TransportAlive && Connection.LastError == null;
        public ThalovantHealth(bool connected, bool handshakeComplete, bool transportAlive, ThalovantConnectionInfo connection)
        { Connected = connected; HandshakeComplete = handshakeComplete; TransportAlive = transportAlive; Connection = connection; }
    }
    public sealed class ThalovantDoctorCheck
    {
        public string Name { get; }
        public bool Ok { get; }
        public string Detail { get; }
        public ThalovantDoctorCheck(string name, bool ok, string detail) { Name = name; Ok = ok; Detail = detail; }
    }
    public sealed class ThalovantDoctorReport
    {
        public IReadOnlyList<ThalovantDoctorCheck> Checks { get; }
        public bool Ok { get { foreach (var check in Checks) if (!check.Ok) return false; return true; } }
        public ThalovantDoctorReport(IReadOnlyList<ThalovantDoctorCheck> checks) { Checks = checks; }
    }
    internal interface IHiveMindRuntimeStatus { bool Connected { get; } bool HandshakeComplete { get; } }
    internal interface IHiveMindQueryBus
    {
        Guid AddQueryHandler(Action<HiveMessage> handler);
        void RemoveQueryHandler(Guid id);
        Task SendQueryFrameAsync(HiveMessage message, CancellationToken cancellationToken);
    }

    public sealed partial class ThalovantClient
    {
        private bool RuntimeConnected => _bus is IHiveMindRuntimeStatus status ? status.Connected : _connected;
        private bool RuntimeHandshakeComplete => _bus is IHiveMindRuntimeStatus status ? status.HandshakeComplete : _connected;
        public ThalovantConnectionInfo ConnectionInfo() => new ThalovantConnectionInfo(
            Transport?.LastError != null ? "error" : RuntimeConnected && RuntimeHandshakeComplete ? "ready" : RuntimeConnected ? "handshake" : "idle",
            Transport?.LastError == null ? null : "HiveMind WSS connection failed.");
        public async Task<ThalovantConnectionInfo> ConnectWithInfoAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        { await ConnectAsync(timeout, cancellationToken).ConfigureAwait(false); return ConnectionInfo(); }
        /// <summary>Authenticated transport snapshot, not a guarantee of every hub skill's health.</summary>
        public async Task<ThalovantHealth> HealthcheckAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            return new ThalovantHealth(RuntimeConnected, RuntimeHandshakeComplete, RuntimeConnected, ConnectionInfo());
        }
        public async Task<ThalovantDoctorReport> DoctorAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var checks = new List<ThalovantDoctorCheck> {
                new ThalovantDoctorCheck("identity", true, "Client identity loaded."),
                new ThalovantDoctorCheck("endpoint", Identity.EndpointFor(HubProtocol.Wss) != null, "WSS endpoint availability.") };
            try {
                var health = await HealthcheckAsync(timeout, cancellationToken).ConfigureAwait(false);
                checks.Add(new ThalovantDoctorCheck("connect", health.Ok, "Authenticated WSS transport state."));
            } catch (OperationCanceledException) { throw; }
              catch (Exception) { checks.Add(new ThalovantDoctorCheck("connect", false, "Authenticated WSS connection failed.")); }
            return new ThalovantDoctorReport(checks);
        }

        /// <summary>Wait for one matching event; always removes the subscription on completion or cancellation.</summary>
        public async Task<ThalovantEvent> WaitForEventAsync(string eventName, TimeSpan? timeout = null,
            string? sessionId = null, string? requestId = null, Func<ThalovantEvent, bool>? predicate = null,
            CancellationToken cancellationToken = default)
        {
            var budget = RuntimeTimeout(timeout);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(budget);
            var answer = new TaskCompletionSource<ThalovantEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = On(eventName, item => {
                try { if (predicate == null || predicate(item)) answer.TrySetResult(item); }
                catch (Exception error) { answer.TrySetException(error); }
            }, sessionId, requestId);
            try {
                await ConnectAsync(budget, deadline.Token).ConfigureAwait(false);
                return await AwaitRuntimeAsync(answer.Task, deadline.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                throw new ThalovantTimeoutException("Hub did not emit the requested event in time.");
            }
        }

        /// <summary>Bounded event stream. A slow consumer receives an overflow error instead of silent event loss.</summary>
        public async IAsyncEnumerable<ThalovantEvent> ListenAsync(string eventName, TimeSpan? timeout = null,
            int? maxEvents = null, string? sessionId = null, string? requestId = null,
            Func<ThalovantEvent, bool>? predicate = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (timeout.HasValue) RuntimeTimeout(timeout);
            if (maxEvents < 0) throw new ArgumentOutOfRangeException(nameof(maxEvents));
            if (maxEvents == 0) yield break;
            cancellationToken.ThrowIfCancellationRequested();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout.HasValue) lifetime.CancelAfter(timeout.Value);
            var queue = new ConcurrentQueue<ThalovantEvent>();
            var queueLock = new object();
            Exception? failure = null;
            using var signal = new SemaphoreSlim(0);
            var active = true;
            using var subscription = On(eventName, item => {
                lock (queueLock) {
                    if (!active) return;
                    try {
                        if (predicate != null && !predicate(item)) return;
                        if (queue.Count >= 64) failure = new ThalovantRuntimeException("Event stream buffer overflow.");
                        else queue.Enqueue(item);
                    } catch (Exception error) { failure = error; }
                    signal.Release();
                }
            }, sessionId, requestId);
            void Retire()
            {
                lock (queueLock) { active = false; while (queue.TryDequeue(out _)) { } }
                subscription.Close();
            }
            using var cancellation = lifetime.Token.Register(Retire);
            try {
                var expired = false;
                try {
                    await ConnectAsync(timeout.HasValue && timeout.Value < TimeSpan.FromSeconds(6) ? timeout : TimeSpan.FromSeconds(6), lifetime.Token).ConfigureAwait(false);
                } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { expired = true; }
                if (expired) yield break;
                var count = 0;
                while (!maxEvents.HasValue || count < maxEvents.Value) {
                    var signaled = false;
                    try { signaled = await signal.WaitAsync(TimeSpan.FromMilliseconds(100), lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { expired = true; }
                    if (expired) yield break;
                    if (!signaled) { RequireRuntimeConnected(); continue; }
                    lock (queueLock) { if (failure != null) throw failure; }
                    if (queue.TryDequeue(out var item)) { count++; yield return item; }
                }
            } finally { Retire(); }
        }

        public Task SendActionAsync(string payload, string? title = null, string lang = "en-us", JsonObject? context = null,
            string? sessionId = null, string? requestId = null, CancellationToken cancellationToken = default)
        {
            var prompt = payload.Trim();
            if (prompt.Length == 0) throw new ArgumentException("SendActionAsync() requires a non-empty payload.", nameof(payload));
            var input = new JsonObject { ["kind"] = "action", ["title"] = title, ["payload"] = prompt };
            return SendUtteranceAsync(prompt, lang, MergeRuntimeContext(context, new JsonObject { ["input"] = input }), sessionId, requestId, cancellationToken);
        }
        public Task SendCodeAsync(string value, string kind = "code", string? label = null, string lang = "en-us", JsonObject? context = null,
            string? sessionId = null, string? requestId = null, CancellationToken cancellationToken = default)
        {
            var code = value.Trim();
            if (code.Length == 0) throw new ArgumentException("SendCodeAsync() requires a non-empty value.", nameof(value));
            var input = new JsonObject { ["kind"] = kind, ["label"] = label, ["value"] = code, ["exact"] = true };
            var data = ThalovantContext.UtterancePayload(code, lang); data["input"] = input.DeepClone();
            var correlated = ThalovantContext.WithCorrelation(MergeRuntimeContext(context, new JsonObject { ["input"] = input }),
                sessionId ?? ThalovantContext.NewSessionId(), Identity.SiteId, lang, requestId ?? ThalovantContext.NewRequestId());
            return EmitAsync(ThalovantEvents.RecognizerLoopUtterance, data, correlated, cancellationToken);
        }

        /// <summary>Direct query/cascade exchange, strictly scoped by query id. Never automatically replayed.</summary>
        public async Task<ThalovantReply> QueryAsync(string text, TimeSpan? timeout = null, string lang = "en-us", JsonObject? context = null,
            string? sessionId = null, string? requestId = null, string? queryId = null, CancellationToken cancellationToken = default)
        {
            var prompt = text.Trim();
            if (prompt.Length == 0) throw new ArgumentException("QueryAsync() requires a non-empty prompt.", nameof(text));
            var budget = RuntimeTimeout(timeout);
            if (!(_bus is IHiveMindQueryBus queryBus)) throw new ThalovantRuntimeException("This transport does not support HiveMind query frames.");
            var request = requestId ?? ThalovantContext.NewRequestId();
            var session = sessionId ?? ThalovantContext.NewSessionId();
            var query = queryId ?? request;
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stateLock = new object();
            var events = new List<ThalovantEvent>(); var fragments = new List<string>();
            ThalovantEvent? failure = null;
            Exception? sendError = null;
            string? responseSessionId = null;
            var token = queryBus.AddQueryHandler(message => {
                if (message.MsgType != "query" && message.MsgType != "cascade") return;
                if ((JsonUtil.GetString(message.Metadata["query_id"]) ?? JsonUtil.GetString(message.Metadata["queryId"])) != query) return;
                var item = QueryEvent(message.Payload); if (item == null) return;
                lock (stateLock) {
                    if (gate.Task.IsCompleted) return;
                    events.Add(item);
                    if (responseSessionId == null && !string.IsNullOrWhiteSpace(item.SessionId)) responseSessionId = item.SessionId;
                    if (item.Name == "hive.query.complete") gate.TrySetResult(true);
                    else if (item.Name == ThalovantEvents.Speak || item.Name == ThalovantEvents.OvosUtteranceSpeak) {
                        var fragment = Regex.Replace(item.Text.Trim(), @"\s+", " ");
                        if (fragment.Length > 0 && (fragments.Count == 0 || fragments[fragments.Count - 1] != fragment)) fragments.Add(fragment);
                    } else if (item.Name == ThalovantEvents.PolicyDenied || item.Name == ThalovantEvents.QueryTimeout) {
                        failure = item; gate.TrySetResult(true);
                    } else if (item.IsFailure) { failure = item; }
                }
            });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(budget);
            try {
                await ConnectAsync(budget, deadline.Token).ConfigureAwait(false);
                var correlated = ThalovantContext.WithCorrelation(context, session, Identity.SiteId, lang, request);
                var bus = HiveWire.BusMessage(ThalovantEvents.RecognizerLoopUtterance, ThalovantContext.UtterancePayload(prompt, lang), correlated);
                var operationToken = deadline.Token;
                // The task observes the physical write through transport cleanup.
                // A terminal response can complete collection without freeing or
                // replaying that write; cancellation still retains its send lock.
                _ = Task.Run(async () => {
                    try {
                        operationToken.ThrowIfCancellationRequested();
                        await queryBus.SendQueryFrameAsync(new HiveMessage("query", bus.ToJsonObject(), new JsonObject { ["query_id"] = query }), operationToken).ConfigureAwait(false);
                    } catch (OperationCanceledException) when (operationToken.IsCancellationRequested) {
                        // The collector translates its own deadline/cancellation.
                    } catch (Exception error) {
                        lock (stateLock) {
                            if (!gate.Task.IsCompleted && !operationToken.IsCancellationRequested) { sendError = error; gate.TrySetResult(false); }
                        }
                    }
                });
                await AwaitRuntimeAsync(gate.Task, deadline.Token).ConfigureAwait(false);
                lock (stateLock) {
                    if (sendError != null) throw sendError;
                    if (fragments.Count == 0) {
                        if (failure != null) throw new ThalovantRuntimeException($"Hub reported {failure.Name}.");
                        throw new ThalovantTimeoutException("Hub completed the query without a speak reply.");
                    }
                    var joined = string.Join(" ", fragments);
                    var terminalFailure = failure?.Name == ThalovantEvents.PolicyDenied || failure?.Name == ThalovantEvents.QueryTimeout ? failure : null;
                    return new ThalovantReply(joined, ThalovantContext.StripSsml(joined), fragments.ToArray(), terminalFailure == null, terminalFailure == null,
                        responseSessionId ?? session, request, events.ToArray(), terminalFailure);
                }
            } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                throw new ThalovantTimeoutException("Hub did not complete the query in time.");
            } finally { deadline.Cancel(); queryBus.RemoveQueryHandler(token); }
        }
        public ThalovantConversation Conversation(string? sessionId = null, string lang = "en-us", JsonObject? context = null) =>
            new ThalovantConversation(this, sessionId ?? ThalovantContext.NewSessionId(), lang, JsonUtil.CloneObject(context));
        private static TimeSpan RuntimeTimeout(TimeSpan? timeout)
        {
            var value = timeout ?? TimeSpan.FromSeconds(12);
            if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
            return value;
        }
        private void RequireRuntimeConnected()
        {
            if (!RuntimeConnected || !RuntimeHandshakeComplete) throw new ThalovantConnectionException("HiveMind transport disconnected while waiting.");
        }
        private async Task<T> AwaitRuntimeAsync<T>(Task<T> result, CancellationToken cancellationToken)
        {
            while (!result.IsCompleted) {
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(100, poll.Token);
                if (await Task.WhenAny(result, delay).ConfigureAwait(false) == result) { poll.Cancel(); break; }
                cancellationToken.ThrowIfCancellationRequested(); RequireRuntimeConnected();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return await result.ConfigureAwait(false);
        }
        private static ThalovantEvent? QueryEvent(JsonObject raw)
        {
            var payload = raw;
            for (var depth = 0; depth < 16; depth++) {
                var item = ThalovantEvent.FromBusPayload(payload); if (item != null) return item;
                if (!(payload["payload"] is JsonObject inner)) return null;
                payload = inner;
            }
            return null;
        }
        internal static JsonObject MergeRuntimeContext(JsonObject? context, JsonObject extra)
        {
            var merged = JsonUtil.CloneObject(context);
            foreach (var pair in extra) {
                merged[pair.Key] = merged[pair.Key] is JsonObject left && pair.Value is JsonObject right
                    ? MergeRuntimeContext(left, right) : pair.Value?.DeepClone();
            }
            return merged;
        }
    }

    /// <summary>Stable session wrapper sharing the client's connection.</summary>
    public sealed class ThalovantConversation
    {
        public ThalovantClient Client { get; }
        public string SessionId { get; }
        public string Lang { get; }
        private readonly JsonObject _context;
        public JsonObject Context => JsonUtil.CloneObject(_context);
        internal ThalovantConversation(ThalovantClient client, string sessionId, string lang, JsonObject context)
        { Client = client; SessionId = sessionId; Lang = lang; _context = context; }
        private JsonObject Merged(JsonObject? context) => ThalovantClient.MergeRuntimeContext(_context, context ?? new JsonObject());
        public Task<ThalovantReply> AskAsync(string text, TimeSpan? timeout = null, JsonObject? context = null, CancellationToken cancellationToken = default) =>
            Client.AskAsync(text, timeout, Lang, Merged(context), SessionId, cancellationToken: cancellationToken);
        public Task<ThalovantReply> QueryAsync(string text, TimeSpan? timeout = null, JsonObject? context = null, CancellationToken cancellationToken = default) =>
            Client.QueryAsync(text, timeout, Lang, Merged(context), SessionId, cancellationToken: cancellationToken);
        public Task SendUtteranceAsync(string text, JsonObject? context = null, CancellationToken cancellationToken = default) =>
            Client.SendUtteranceAsync(text, Lang, Merged(context), SessionId, cancellationToken: cancellationToken);
        public Task SendActionAsync(string payload, string? title = null, JsonObject? context = null, CancellationToken cancellationToken = default) =>
            Client.SendActionAsync(payload, title, Lang, Merged(context), SessionId, cancellationToken: cancellationToken);
        public Task SendCodeAsync(string value, string kind = "code", string? label = null, JsonObject? context = null, CancellationToken cancellationToken = default) =>
            Client.SendCodeAsync(value, kind, label, Lang, Merged(context), SessionId, cancellationToken: cancellationToken);
        public ThalovantSubscription On(string eventName, Action<ThalovantEvent> handler) => Client.On(eventName, handler, SessionId);
        public Task<ThalovantEvent> WaitForEventAsync(string eventName, TimeSpan? timeout = null, Func<ThalovantEvent, bool>? predicate = null, CancellationToken cancellationToken = default) =>
            Client.WaitForEventAsync(eventName, timeout, SessionId, predicate: predicate, cancellationToken: cancellationToken);
        public IAsyncEnumerable<ThalovantEvent> ListenAsync(string eventName, TimeSpan? timeout = null, int? maxEvents = null, CancellationToken cancellationToken = default) =>
            Client.ListenAsync(eventName, timeout, maxEvents, SessionId, cancellationToken: cancellationToken);
        public Task EmitAsync(string eventType, JsonObject? data = null, JsonObject? context = null, CancellationToken cancellationToken = default) =>
            Client.EmitAsync(eventType, data, ThalovantContext.WithCorrelation(Merged(context), SessionId, Client.Identity.SiteId, Lang), cancellationToken);
    }
}
