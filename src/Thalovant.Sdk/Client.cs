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
    /// Data-plane client for a Thalovant hub. Version 0.1 speaks WSS only;
    /// requesting the HTTPS or MQTT transport throws
    /// <see cref="ThalovantUnsupportedProtocolException"/>.
    /// </summary>
    public sealed class ThalovantClient : IDisposable
    {
        public ThalovantIdentity Identity { get; }

        internal HiveMindWssTransport Transport { get; }

        private readonly TimeSpan _replySettle;
        private readonly TimeSpan _emptyReplyWait;
        private readonly object _lock = new object();
        private bool _connected;

        public ThalovantClient(
            ThalovantIdentity identity,
            HubProtocol hubProtocol = HubProtocol.Wss,
            string? userAgent = null,
            TimeSpan? replySettle = null,
            TimeSpan? emptyReplyWait = null)
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
            Transport = new HiveMindWssTransport(identity, userAgent);
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
                if (_connected)
                {
                    return;
                }
            }
            await Transport.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _connected = true;
            }
        }

        public async Task CloseAsync()
        {
            await Transport.DisconnectAsync().ConfigureAwait(false);
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
            var id = Transport.AddBusHandler(payload =>
            {
                var busEvent = ThalovantEvent.FromBusPayload(payload);
                if (busEvent is null || busEvent.Name != eventName)
                {
                    return;
                }
                if (sessionId is not null && busEvent.SessionId is string eventSession && eventSession != sessionId)
                {
                    return;
                }
                if (requestId is not null && busEvent.RequestId is string eventRequest && eventRequest != requestId)
                {
                    return;
                }
                handler(busEvent);
            });
            var transport = Transport;
            return new ThalovantSubscription(() => transport.RemoveBusHandler(id));
        }

        /// <summary>Emits a bus event to the hub.</summary>
        public async Task EmitAsync(
            string eventType,
            JsonObject? data = null,
            JsonObject? context = null,
            CancellationToken cancellationToken = default)
        {
            await ConnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await Transport.EmitBusAsync(
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
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(12);
            var effectiveRequestId = requestId ?? ThalovantContext.NewRequestId();
            var effectiveSessionId = sessionId ?? ThalovantContext.NewSessionId();
            var correlatedContext = ThalovantContext.WithCorrelation(
                ContextWithIdentityMetadata(context ?? new JsonObject()),
                effectiveSessionId,
                Identity.SiteId,
                lang,
                effectiveRequestId);
            await ConnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            var state = new AskState();
            var handlerId = Transport.AddBusHandler(payload =>
            {
                var busEvent = ThalovantEvent.FromBusPayload(payload);
                if (busEvent is not null)
                {
                    state.Process(busEvent, effectiveRequestId);
                }
            });
            try
            {
                await Transport.EmitBusAsync(
                    ThalovantEvents.RecognizerLoopUtterance,
                    ThalovantContext.UtterancePayload(prompt, lang),
                    correlatedContext,
                    cancellationToken).ConfigureAwait(false);

                // Phase 1: wait until the hub reports the utterance handled or the
                // first speak fragment arrives.
                await state.ProgressGate.WaitAsync(
                    effectiveTimeout,
                    new ThalovantTimeoutException(
                        $"Hub did not finish handling the utterance within {(int)effectiveTimeout.TotalMilliseconds}ms.")).ConfigureAwait(false);

                // Phase 2: the hub finished handling but has not spoken yet; give the
                // reply a grace period.
                var effectiveEmptyReplyWait = emptyReplyWait ?? _emptyReplyWait;
                var afterProgress = state.Snapshot();
                if (afterProgress.Fragments.Count == 0 && afterProgress.FailureEvent is null && effectiveEmptyReplyWait > TimeSpan.Zero)
                {
                    await state.ReplyGate.WaitAsync(effectiveEmptyReplyWait, timeoutError: null).ConfigureAwait(false);
                }

                // Phase 3: let trailing fragments settle briefly.
                var effectiveReplySettle = replySettle ?? _replySettle;
                if (effectiveReplySettle > TimeSpan.Zero)
                {
                    await Task.Delay(effectiveReplySettle, cancellationToken).ConfigureAwait(false);
                }

                var final = state.Snapshot();
                if (final.FailureEvent is null && final.Fragments.Count == 0)
                {
                    throw new ThalovantTimeoutException(
                        $"Hub handled the utterance but did not emit a speak reply within {(int)effectiveEmptyReplyWait.TotalMilliseconds}ms.");
                }
                if (final.FailureEvent is ThalovantEvent failure && final.Fragments.Count == 0)
                {
                    var message = failure.Text.Length == 0 ? $"Hub reported {failure.Name}." : failure.Text;
                    throw new ThalovantRuntimeException(message);
                }
                var replyText = string.Join(" ", final.Fragments);
                return new ThalovantReply(
                    replyText,
                    ThalovantContext.StripSsml(replyText),
                    final.Fragments,
                    handled: final.FailureEvent is null,
                    ok: final.FailureEvent is null,
                    sessionId: effectiveSessionId,
                    requestId: effectiveRequestId,
                    events: final.Events,
                    failureEvent: final.FailureEvent);
            }
            finally
            {
                Transport.RemoveBusHandler(handlerId);
            }
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
            internal bool Handled { get; }

            internal StateSnapshot(IReadOnlyList<string> fragments, IReadOnlyList<ThalovantEvent> events, ThalovantEvent? failureEvent, bool handled)
            {
                Fragments = fragments;
                Events = events;
                FailureEvent = failureEvent;
                Handled = handled;
            }
        }

        private readonly object _lock = new object();
        private readonly List<string> _fragments = new List<string>();
        private readonly List<ThalovantEvent> _events = new List<ThalovantEvent>();
        private ThalovantEvent? _failureEvent;
        private bool _handled;

        /// <summary>Opens when the utterance is handled or the first fragment arrives.</summary>
        internal AsyncGate ProgressGate { get; } = new AsyncGate();

        /// <summary>Opens when the first speak fragment arrives.</summary>
        internal AsyncGate ReplyGate { get; } = new AsyncGate();

        internal StateSnapshot Snapshot()
        {
            lock (_lock)
            {
                return new StateSnapshot(_fragments.ToArray(), _events.ToArray(), _failureEvent, _handled);
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
            switch (busEvent.Name)
            {
                case ThalovantEvents.Speak:
                case ThalovantEvents.OvosUtteranceSpeak:
                {
                    var normalized = NormalizeFragment(busEvent.Text);
                    bool appended;
                    lock (_lock)
                    {
                        _events.Add(busEvent);
                        appended = normalized.Length > 0
                            && (_fragments.Count == 0 || _fragments[_fragments.Count - 1] != normalized);
                        if (appended)
                        {
                            _fragments.Add(normalized);
                        }
                    }
                    if (appended)
                    {
                        ReplyGate.Open();
                        ProgressGate.Open();
                    }
                    break;
                }
                case ThalovantEvents.UtteranceHandled:
                    lock (_lock)
                    {
                        _events.Add(busEvent);
                        _handled = true;
                    }
                    ProgressGate.Open();
                    break;
                case ThalovantEvents.IntentFailure:
                    lock (_lock)
                    {
                        _events.Add(busEvent);
                    }
                    break;
                case ThalovantEvents.PolicyDenied:
                case ThalovantEvents.QueryTimeout:
                    lock (_lock)
                    {
                        _events.Add(busEvent);
                        _failureEvent = busEvent;
                        _handled = true;
                    }
                    ProgressGate.Open();
                    break;
                default:
                    break;
            }
        }

        private static string NormalizeFragment(string text)
        {
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }
    }
}
