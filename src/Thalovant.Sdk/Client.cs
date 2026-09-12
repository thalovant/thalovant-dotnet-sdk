using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Thalovant
{
    /// <summary>Handle for a registered event handler; <see cref="Close"/> removes it.</summary>
    public sealed class ThalovantSubscription : IDisposable
    {
        private readonly Action _close;
        private int _closed;

        internal ThalovantSubscription(Action close)
        {
            _close = close;
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _close();
            }
        }

        public void Unsubscribe()
        {
            Close();
        }

        public void Dispose()
        {
            Close();
        }
    }

    /// <summary>
    /// Data-plane client for a Thalovant hub using HiveMind v3 Noise over WSS;
    /// requesting the HTTPS or MQTT transport throws
    /// <see cref="ThalovantUnsupportedProtocolException"/>.
    /// </summary>
    public sealed partial class ThalovantClient : IDisposable
    {
        /// <summary>The language queries default to when none is given, as in the sibling SDKs.</summary>
        internal const string DefaultLang = "en-us";

        public ThalovantIdentity Identity { get; }

        /// <summary>The WSS transport this client owns, or null when a test supplied its own bus.</summary>
        internal HiveMindWssTransport? Transport { get; }

        private readonly IHiveMindBus _bus;
        private readonly TimeSpan _replySettle;
        private readonly TimeSpan _emptyReplyWait;
        private readonly object _lock = new object();
        private bool _connected;
        private readonly HashSet<string> _activeAskIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _activeQueryIds = new HashSet<string>(StringComparer.Ordinal);

        private IDisposable ReserveRuntimeId(string id, bool query)
        {
            var active = query ? _activeQueryIds : _activeAskIds;
            lock (_lock) {
                if (!active.Add(id)) throw new ThalovantRuntimeException("The correlation ID is already active for this operation type on this client.");
            }
            return new ThalovantSubscription(() => { lock (_lock) active.Remove(id); });
        }

        public ThalovantClient(
            ThalovantIdentity identity,
            HubProtocol hubProtocol = HubProtocol.Wss,
            string? userAgent = null,
            TimeSpan? replySettle = null,
            TimeSpan? emptyReplyWait = null,
            IHiveMindNoiseStore? noiseStore = null)
        {
            switch (hubProtocol)
            {
                case HubProtocol.Wss:
                    break;
                case HubProtocol.Https:
                    throw new ThalovantUnsupportedProtocolException(
                        "The HTTPS data-plane transport is not supported by the .NET SDK yet; use wss.");
                case HubProtocol.Mqtt:
                    throw new ThalovantUnsupportedProtocolException(
                        "The MQTT data-plane transport is not supported by the .NET SDK yet; use wss.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(hubProtocol));
            }
            if (identity.EndpointFor(HubProtocol.Wss) is null)
            {
                throw new ThalovantUnsupportedProtocolException(
                    "WSS is enabled, but the identity does not include a WSS endpoint.");
            }
            Identity = identity;
            Transport = new HiveMindWssTransport(identity, userAgent, noiseStore);
            _bus = Transport;
            _replySettle = replySettle ?? TimeSpan.FromMilliseconds(250);
            _emptyReplyWait = emptyReplyWait ?? TimeSpan.FromSeconds(5);
        }

        /// <summary>
        /// A client over a caller-supplied bus: the test seam, equivalent to the
        /// sibling SDKs' <c>transport</c> parameter. No endpoint is required.
        /// </summary>
        internal ThalovantClient(
            ThalovantIdentity identity,
            IHiveMindBus bus,
            TimeSpan? replySettle = null,
            TimeSpan? emptyReplyWait = null)
        {
            Identity = identity;
            Transport = bus as HiveMindWssTransport;
            _bus = bus;
            _replySettle = replySettle ?? TimeSpan.FromMilliseconds(250);
            _emptyReplyWait = emptyReplyWait ?? TimeSpan.FromSeconds(5);
        }

        public static ThalovantClient FromIdentityFile(string path, HubProtocol hubProtocol = HubProtocol.Wss)
        {
            return new ThalovantClient(ThalovantIdentity.FromFile(path), hubProtocol);
        }

        public async Task ConnectAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_connected && RuntimeConnected && RuntimeHandshakeComplete)
                {
                    return;
                }
            }
            await _bus.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _connected = true;
            }
        }

        public async Task CloseAsync()
        {
            await _bus.DisconnectAsync().ConfigureAwait(false);
            lock (_lock)
            {
                _connected = false;
            }
        }

        public void Dispose()
        {
            CloseAsync().GetAwaiter().GetResult();
        }

        // -- Events ----------------------------------------------------------

        /// <summary>
        /// Registers a handler for a named bus event, optionally filtered by
        /// correlation ids. Returns a subscription; call <see cref="ThalovantSubscription.Close"/>
        /// to remove it.
        /// </summary>
        public ThalovantSubscription On(
            string eventName,
            Action<ThalovantEvent> handler,
            string? sessionId = null,
            string? requestId = null)
        {
            var id = _bus.AddBusHandler(payload =>
            {
                var busEvent = ThalovantEvent.FromBusPayload(payload);
                if (busEvent is null || busEvent.Name != eventName)
                {
                    return;
                }
                // The request id decides when both sides carry one: a hub does
                // not echo a client-declared session id, it substitutes its own
                // (observed live on 2026-09-03), so comparing session ids
                // rejected replies the request id had already identified as
                // ours and Ask() timed out.
                if (requestId is not null && busEvent.RequestId is string eventRequest)
                {
                    if (eventRequest != requestId)
                    {
                        return;
                    }
                }
                else if (sessionId is not null && busEvent.SessionId is string eventSession && eventSession != sessionId)
                {
                    return;
                }
                handler(busEvent);
            });
            var bus = _bus;
            return new ThalovantSubscription(() => bus.RemoveBusHandler(id));
        }

        /// <summary>Emits a bus event to the hub.</summary>
        public async Task EmitAsync(
            string eventType,
            JsonObject? data = null,
            JsonObject? context = null,
            CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await _bus.EmitBusAsync(
                eventType,
                data ?? new JsonObject(),
                ContextWithIdentityMetadata(context ?? new JsonObject()),
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Sends an utterance without waiting for a reply.</summary>
        public Task SendUtteranceAsync(
            string text,
            string lang = "en-us",
            JsonObject? context = null,
            string? sessionId = null,
            string? requestId = null,
            CancellationToken cancellationToken = default)
        {
            var prompt = text.Trim();
            if (prompt.Length == 0)
            {
                throw new ThalovantRuntimeException("SendUtteranceAsync() requires a non-empty text prompt.");
            }
            var correlated = ThalovantContext.WithCorrelation(
                context,
                sessionId ?? ThalovantContext.NewSessionId(),
                Identity.SiteId,
                lang,
                requestId ?? ThalovantContext.NewRequestId());
            return EmitAsync(
                ThalovantEvents.RecognizerLoopUtterance,
                ThalovantContext.UtterancePayload(prompt, lang),
                correlated,
                cancellationToken);
        }

        // -- Ask -------------------------------------------------------------

        /// <summary>
        /// Sends an utterance and aggregates the correlated <c>speak</c> replies into a
        /// single <see cref="ThalovantReply"/>, using the request id for correlation.
        /// </summary>
        public async Task<ThalovantReply> AskAsync(
            string text,
            TimeSpan? timeout = null,
            string lang = "en-us",
            JsonObject? context = null,
            string? sessionId = null,
            string? requestId = null,
            TimeSpan? replySettle = null,
            TimeSpan? emptyReplyWait = null,
            CancellationToken cancellationToken = default)
        {
            var prompt = text.Trim();
            if (prompt.Length == 0)
            {
                throw new ThalovantRuntimeException("AskAsync() requires a non-empty text prompt.");
            }
            var effectiveTimeout = RuntimeTimeout(timeout);
            var effectiveEmptyReplyWait = emptyReplyWait ?? _emptyReplyWait;
            var effectiveReplySettle = replySettle ?? _replySettle;
            if (effectiveEmptyReplyWait < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(emptyReplyWait), "Reply waits must be non-negative.");
            if (effectiveReplySettle < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(replySettle), "Reply waits must be non-negative.");
            cancellationToken.ThrowIfCancellationRequested();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            TimeSpan Remaining() { var left = effectiveTimeout - clock.Elapsed; return left > TimeSpan.Zero ? left : TimeSpan.Zero; }
            TimeSpan Bounded(TimeSpan wait, long? since) {
                var elapsed = since.HasValue ? TimeSpan.FromSeconds((System.Diagnostics.Stopwatch.GetTimestamp() - since.Value) / (double)System.Diagnostics.Stopwatch.Frequency) : TimeSpan.Zero;
                var phase = wait > elapsed ? wait - elapsed : TimeSpan.Zero;
                var left = Remaining(); return phase < left ? phase : left;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(effectiveTimeout);
            var effectiveRequestId = requestId ?? ThalovantContext.NewRequestId();
            using var correlation = ReserveRuntimeId(effectiveRequestId, query: false);
            var effectiveSessionId = sessionId ?? ThalovantContext.NewSessionId();
            var correlatedContext = ThalovantContext.WithCorrelation(
                ContextWithIdentityMetadata(context ?? new JsonObject()),
                effectiveSessionId,
                Identity.SiteId,
                lang,
                effectiveRequestId);
            var state = new AskState(Remaining, effectiveEmptyReplyWait, effectiveReplySettle);
            var handlerId = _bus.AddBusHandler(payload =>
            {
                var busEvent = ThalovantEvent.FromBusPayload(payload);
                if (busEvent is not null)
                {
                    state.Process(busEvent, effectiveRequestId);
                }
            });
            var operationToken = deadline.Token;
            // This task retains the physical send lock until transport cleanup completes.
            // The caller can finish at its deadline without replaying or freeing that write.
            _ = Task.Run(async () => {
                try {
                    operationToken.ThrowIfCancellationRequested();
                    var connectBudget = Remaining();
                    if (connectBudget <= TimeSpan.Zero) throw new ThalovantTimeoutException("Request budget expired before connecting.");
                    await ConnectAsync(connectBudget, operationToken).ConfigureAwait(false);
                    operationToken.ThrowIfCancellationRequested();
                    await _bus.EmitBusAsync(ThalovantEvents.RecognizerLoopUtterance,
                        ThalovantContext.UtterancePayload(prompt, lang), correlatedContext, operationToken).ConfigureAwait(false);
                } catch (OperationCanceledException) when (operationToken.IsCancellationRequested) {
                    // The collector owns deadline/caller-cancellation precedence.
                } catch (Exception error) { state.Fail(error); }
            });
            try
            {
                try {
                    await state.ProgressGate.WaitAsync(Remaining(),
                        new ThalovantTimeoutException("Hub did not finish handling the utterance within the request budget."), cancellationToken).ConfigureAwait(false);
                } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                    var captured = state.Snapshot();
                    if (captured.Fragments.Count == 0 && captured.FailureEvent == null && captured.SoftFailureEvent == null)
                        throw new ThalovantTimeoutException("Hub did not finish handling the utterance within the request budget.");
                } catch (ThalovantTimeoutException) {
                    var captured = state.Snapshot();
                    if (captured.Fragments.Count == 0 && captured.FailureEvent == null && captured.SoftFailureEvent == null) throw;
                }
                var afterProgress = state.Snapshot();
                if (afterProgress.Fragments.Count == 0 && afterProgress.FailureEvent is null)
                    await state.ReplyGate.WaitAsync(Bounded(effectiveEmptyReplyWait, afterProgress.EmptyStartedAt), null, cancellationToken).ConfigureAwait(false);
                // Optional settling shares the original budget and hard failure interrupts it.
                var afterEmpty = state.Snapshot();
                if (afterEmpty.FailureEvent is null && afterEmpty.Fragments.Count > 0)
                    await state.TerminalGate.WaitAsync(Bounded(effectiveReplySettle, afterEmpty.FirstSpeechAt), null, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var final = state.Finish();
                if (final.Error != null) throw final.Error;
                // A soft intent-miss becomes the surfaced failure only if no reply
                // (not even a fallback) arrived; a reply means a fallback recovered.
                var effectiveFailure = final.FailureEvent ?? (final.Fragments.Count == 0 ? final.SoftFailureEvent : null);
                if (effectiveFailure is null && final.Fragments.Count == 0)
                {
                    throw new ThalovantTimeoutException(
                        $"Hub handled the utterance but did not emit a speak reply within {(int)effectiveEmptyReplyWait.TotalMilliseconds}ms.");
                }
                if (effectiveFailure is ThalovantEvent failure && final.Fragments.Count == 0)
                {
                    var message = failure.Text.Length == 0 ? $"Hub reported {failure.Name}." : failure.Text;
                    throw new ThalovantRuntimeException(message);
                }
                var replyText = string.Join(" ", final.Fragments);
                return new ThalovantReply(
                    replyText,
                    ThalovantContext.StripSsml(replyText),
                    final.Fragments,
                    handled: effectiveFailure is null,
                    ok: effectiveFailure is null,
                    sessionId: final.ResponseSessionId ?? effectiveSessionId,
                    requestId: effectiveRequestId,
                    events: final.Events,
                    failureEvent: effectiveFailure) { DroppedMedia = final.DroppedMedia };
            }
            finally
            {
                deadline.Cancel();
                _bus.RemoveBusHandler(handlerId);
            }
        }

        // -- Intents ---------------------------------------------------------

        /// <summary>Registered fallback handlers; null means unavailable, empty means none registered.</summary>
        public Task<IReadOnlyList<HubFallback>?> ListFallbacksAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            HubIntentQueries.ListFallbacksAsync(this, timeout ?? TimeSpan.FromSeconds(5), cancellationToken);



        /// <summary>
        /// Everything the hub can be asked, per language, grouped by skill.
        /// <para>
        /// Read from the runtime's intent manifest over this session, so no
        /// control-plane credential is involved. Each intent carries the sentences
        /// a person says to reach it, as the skill's locale files wrote them,
        /// <c>{slot}</c> placeholders included. <paramref name="languages"/>
        /// defaults to <c>en-us</c>.
        /// </para>
        /// <para>
        /// Throws <see cref="ThalovantPolicyDeniedException"/> when the hub refuses
        /// the query and <see cref="IntentInventoryOptions.Fallback"/> is off; with
        /// it on (the default), a hub allowed for only the engines' manifests yields
        /// intent names with <see cref="HubIntentInventory.Source"/> set to
        /// <see cref="HubIntentInventory.SourceEngineManifests"/>.
        /// </para>
        /// </summary>
        public Task<HubIntentInventory> IntentsAsync(
            IEnumerable<string>? languages = null,
            IntentInventoryOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var chosen = new List<string>();
            if (languages is not null)
            {
                chosen.AddRange(languages);
            }
            if (chosen.Count == 0)
            {
                chosen.Add(DefaultLang);
            }
            return HubIntentQueries.InventoryAsync(this, chosen, options ?? new IntentInventoryOptions(), cancellationToken);
        }

        /// <summary>
        /// The hub's intent manifest for one language, one row per registration.
        /// A hub that answers <c>ovos.intent.list</c> with <c>ok: false</c> has
        /// refused the query, not reported an empty hub: that throws
        /// <see cref="ThalovantRuntimeException"/> carrying the hub's error.
        /// </summary>
        public Task<IReadOnlyList<IntentRegistration>> ListIntentsAsync(
            string? lang = null,
            IntentListOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return HubIntentQueries.ListIntentsAsync(this, lang ?? DefaultLang, options ?? new IntentListOptions(), cancellationToken);
        }

        /// <summary>
        /// The registrations behind one intent in one language, sentences
        /// included. Empty when the hub answers <c>ok: false</c>, which there is
        /// a real answer: it does not know that registration.
        /// </summary>
        public Task<IReadOnlyList<IntentDefinition>> DescribeIntentAsync(
            string skillId,
            string intentName,
            string? lang = null,
            IntentDescribeOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return HubIntentQueries.DescribeIntentAsync(
                this, skillId, intentName, lang ?? DefaultLang, options ?? new IntentDescribeOptions(), cancellationToken);
        }

        private JsonObject ContextWithIdentityMetadata(JsonObject context)
        {
            if (Identity.Metadata.Count == 0)
            {
                return context;
            }
            var merged = JsonUtil.CloneObject(Identity.Metadata);
            if (JsonUtil.AsObject(context["metadata"]) is JsonObject existing)
            {
                foreach (var pair in JsonUtil.CloneObject(existing))
                {
                    merged[pair.Key] = pair.Value?.DeepClone();
                }
            }
            var next = JsonUtil.CloneObject(context);
            next["metadata"] = merged;
            return next;
        }
    }

    /// <summary>Accumulates correlated events for one <see cref="ThalovantClient.AskAsync"/> call.</summary>
    internal sealed class AskState
    {
        internal sealed class StateSnapshot
        {
            internal IReadOnlyList<string> Fragments { get; }
            internal IReadOnlyList<ThalovantEvent> Events { get; }
            internal ThalovantEvent? FailureEvent { get; }
            internal ThalovantEvent? SoftFailureEvent { get; }
            internal bool Handled { get; }
            internal string? ResponseSessionId { get; }
            internal long? FirstSpeechAt { get; }
            internal long? EmptyStartedAt { get; }
            internal Exception? Error { get; }
            internal int DroppedMedia { get; set; }

            internal StateSnapshot(IReadOnlyList<string> fragments, IReadOnlyList<ThalovantEvent> events, ThalovantEvent? failureEvent, ThalovantEvent? softFailureEvent, bool handled, long? firstSpeechAt, long? emptyStartedAt, string? responseSessionId, Exception? error)
            {
                Fragments = fragments;
                Events = events;
                FailureEvent = failureEvent;
                SoftFailureEvent = softFailureEvent;
                Handled = handled; FirstSpeechAt = firstSpeechAt; EmptyStartedAt = emptyStartedAt; ResponseSessionId = responseSessionId; Error = error;
            }
        }

        private readonly object _lock = new object();
        private readonly List<string> _fragments = new List<string>();
        private readonly List<ThalovantEvent> _events = new List<ThalovantEvent>();
        private readonly ReplyMediaBudget _mediaBudget = new ReplyMediaBudget();
        private ThalovantEvent? _failureEvent;
        // An intent miss is a soft failure: it ends phase 1 but leaves _failureEvent
        // null so the empty-reply wait still runs and a fallback reply can win.
        private ThalovantEvent? _softFailureEvent;
        private bool _handled;
        private long? _firstSpeechAt, _emptyStartedAt;
        private string? _responseSessionId;
        private Exception? _error;
        private bool _stopped;
        private readonly Func<TimeSpan>? _remaining;
        private readonly TimeSpan _emptyReplyWait, _replySettle;

        internal AskState(Func<TimeSpan>? remaining = null, TimeSpan emptyReplyWait = default, TimeSpan replySettle = default)
        { _remaining = remaining; _emptyReplyWait = emptyReplyWait; _replySettle = replySettle; }

        private bool Expired()
        {
            if (_remaining == null) return false;
            if (_remaining() <= TimeSpan.Zero) return true;
            var started = _firstSpeechAt ?? _emptyStartedAt;
            if (!started.HasValue) return false;
            var elapsed = TimeSpan.FromSeconds((System.Diagnostics.Stopwatch.GetTimestamp() - started.Value) / (double)System.Diagnostics.Stopwatch.Frequency);
            return elapsed >= (_firstSpeechAt.HasValue ? _replySettle : _emptyReplyWait);
        }

        internal void Fail(Exception error)
        {
            lock (_lock) {
                if (_stopped || _failureEvent != null || Expired()) return;
                _error = error; _stopped = true;
                // All phases wake, even when an earlier progress gate is open.
                // Store the exception once instead of faulting unobserved gates.
                ProgressGate.Open(); ReplyGate.Open(); TerminalGate.Open();
            }
        }

        internal StateSnapshot Finish()
        {
            lock (_lock) { _stopped = true; return Snapshot(); }
        }


        /// <summary>Opens when the utterance is handled or the first fragment arrives.</summary>
        internal AsyncGate ProgressGate { get; } = new AsyncGate();

        /// <summary>Opens when the first speak fragment arrives.</summary>
        internal AsyncGate ReplyGate { get; } = new AsyncGate();
        internal AsyncGate TerminalGate { get; } = new AsyncGate();

        internal StateSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new StateSnapshot(_fragments.ToArray(), _events.ToArray(), _failureEvent, _softFailureEvent, _handled, _firstSpeechAt, _emptyStartedAt, _responseSessionId, _error) { DroppedMedia = _mediaBudget.Dropped };
            }
        }

        /// <summary>
        /// Correlation rule (mirrors the Node SDK): only events carrying the
        /// matching request id participate in the reply.
        /// </summary>
        internal void Process(ThalovantEvent busEvent, string requestId)
        {
            if (busEvent.RequestId != requestId)
            {
                return;
            }
            lock (_lock)
            {
                if (_stopped || _failureEvent != null || Expired()) return;
                if (!_mediaBudget.Accept(busEvent)) return;
                switch (busEvent.Name)
                {
                    case ThalovantEvents.AudioQueue:
                        _events.Add(busEvent); break;
                    case ThalovantEvents.Speak:
                    case ThalovantEvents.OvosUtteranceSpeak:
                        _events.Add(busEvent);
                        var normalized = NormalizeFragment(busEvent.Text);
                        if (normalized.Length > 0 && (_fragments.Count == 0 || _fragments[_fragments.Count - 1] != normalized))
                        {
                            _firstSpeechAt ??= System.Diagnostics.Stopwatch.GetTimestamp();
                            _fragments.Add(normalized); ReplyGate.Open(); ProgressGate.Open();
                        }
                        break;
                    case ThalovantEvents.UtteranceHandled:
                        _emptyStartedAt ??= System.Diagnostics.Stopwatch.GetTimestamp();
                        _events.Add(busEvent); _handled = true; ProgressGate.Open(); break;
                    case ThalovantEvents.IntentFailure:
                    case ThalovantEvents.IntentUnmatched:
                        _emptyStartedAt ??= System.Diagnostics.Stopwatch.GetTimestamp();
                        _events.Add(busEvent); _softFailureEvent = busEvent; _handled = true; ProgressGate.Open(); break;
                    case ThalovantEvents.PolicyDenied:
                    case ThalovantEvents.QueryTimeout:
                        _events.Add(busEvent); _failureEvent = busEvent; _handled = true;
                        ProgressGate.Open(); ReplyGate.Open(); TerminalGate.Open(); break;
                    default: return;
                }
                if (_responseSessionId == null && !string.IsNullOrWhiteSpace(busEvent.SessionId)) _responseSessionId = busEvent.SessionId;
            }
        }

        private static string NormalizeFragment(string text)
        {
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }
    }
}
