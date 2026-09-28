using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace Thalovant
{
    /// <summary>How a session waits: the retry ladder, the probe cadence and the refusal grace, in seconds.</summary>
    public sealed class HubSessionPolicy
    {
        /// <summary>After a failed connect, the wait before the next unattended attempt.</summary>
        public double RetrySeconds { get; }

        /// <summary>The ladder doubles towards this and stays there.</summary>
        public double RetryCeilingSeconds { get; }

        /// <summary>How often a held session is checked for having died while idle.</summary>
        public double ProbeSeconds { get; }

        /// <summary>How often the probe comes round while no session is held.</summary>
        public double ProbeDownSeconds { get; }

        /// <summary>
        /// How long <see cref="HubSession.RunAsync"/> keeps trying through refusals
        /// before it gives up. A connection just created is refused until its hub
        /// has admitted it -- about ninety seconds -- so a refusal is only final
        /// once it has lasted this long. 600 by default.
        /// </summary>
        public double RefusalGraceSeconds { get; }

        public HubSessionPolicy(double retrySeconds = 10, double retryCeilingSeconds = 120, double probeSeconds = 60, double probeDownSeconds = 5)
            : this(retrySeconds, retryCeilingSeconds, probeSeconds, probeDownSeconds, 600)
        {
        }

        public HubSessionPolicy(double retrySeconds, double retryCeilingSeconds, double probeSeconds, double probeDownSeconds, double refusalGraceSeconds)
        {
            foreach (var value in new[] { retrySeconds, retryCeilingSeconds, probeSeconds, probeDownSeconds }) if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(retrySeconds));
            if (double.IsNaN(refusalGraceSeconds) || double.IsInfinity(refusalGraceSeconds) || refusalGraceSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(refusalGraceSeconds));
            if (retryCeilingSeconds < retrySeconds) throw new ArgumentOutOfRangeException(nameof(retryCeilingSeconds));
            RetrySeconds = retrySeconds; RetryCeilingSeconds = retryCeilingSeconds; ProbeSeconds = probeSeconds; ProbeDownSeconds = probeDownSeconds; RefusalGraceSeconds = refusalGraceSeconds;
        }

        public double NextWait(double current) => Math.Min(current * 2, RetryCeilingSeconds);
    }

    /// <summary>What one attempt to keep a link up came to.</summary>
    internal enum LinkOutcome
    {
        /// <summary>The link came up.</summary>
        Up,

        /// <summary>An established link went down.</summary>
        Dropped,

        /// <summary>The hub or the network could not be reached.</summary>
        Failed,

        /// <summary>The hub turned the credentials away.</summary>
        Refused,

        /// <summary>The hub's Noise key is not the pinned one.</summary>
        KeyChanged,
    }

    internal enum LinkAction
    {
        Hold,
        Retry,
        GiveUp,
    }

    /// <summary>What to do after an outcome: hold, retry after <see cref="Wait"/>, or give up for <see cref="Reason"/>.</summary>
    internal readonly struct LinkDecision
    {
        internal LinkDecision(LinkAction action, TimeSpan wait = default, string? reason = null)
        {
            Action = action;
            Wait = wait;
            Reason = reason;
        }

        internal LinkAction Action { get; }
        internal TimeSpan Wait { get; }
        internal string? Reason { get; }
    }

    /// <summary>
    /// How a long-lived link is kept up, as a pure function of what happened and
    /// when: <see cref="HubSession.RunAsync"/> asks it after every attempt, and
    /// <c>link-keeping-vectors.json</c> holds every SDK to the same answers.
    /// </summary>
    internal sealed class LinkSupervisor
    {
        private readonly HubSessionPolicy _policy;
        private double _wait;
        private double? _refusedSince;

        internal LinkSupervisor(HubSessionPolicy policy)
        {
            _policy = policy;
            _wait = policy.RetrySeconds;
        }

        /// <summary>The decision after <paramref name="outcome"/>, observed at <paramref name="now"/> seconds on any monotonic origin.</summary>
        internal LinkDecision After(LinkOutcome outcome, double now)
        {
            switch (outcome)
            {
                case LinkOutcome.Up:
                    _wait = _policy.RetrySeconds;
                    _refusedSince = null;
                    return new LinkDecision(LinkAction.Hold);
                case LinkOutcome.Dropped:
                    return new LinkDecision(LinkAction.Retry, TimeSpan.Zero);
                case LinkOutcome.KeyChanged:
                    return new LinkDecision(LinkAction.GiveUp, reason: "key_changed");
                case LinkOutcome.Refused:
                    _refusedSince ??= now;
                    if (now - _refusedSince.Value >= _policy.RefusalGraceSeconds)
                    {
                        return new LinkDecision(LinkAction.GiveUp, reason: "refused");
                    }
                    break;
                case LinkOutcome.Failed:
                    _refusedSince = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome));
            }
            var wait = _wait;
            _wait = _policy.NextWait(_wait);
            return new LinkDecision(LinkAction.Retry, TimeSpan.FromSeconds(wait));
        }
    }

    /// <summary>One managed connection. Failed admitted calls are never replayed.</summary>
    /// <remarks>
    /// <para>
    /// Calls (<see cref="AskAsync"/>, <see cref="EmitAsync"/>) connect for
    /// themselves; a broken connection costs one failed call and is rebuilt
    /// before the next. Subscriptions made with <see cref="On"/> are wired onto
    /// every client the session builds.
    /// </para>
    /// <para>
    /// A link that has to stay up -- one a hub sends requests down, such as the
    /// Home Assistant link (<see cref="AnswerHomeRequests"/>) -- is kept by
    /// <see cref="RunAsync"/>: after a failed attempt it waits
    /// <see cref="HubSessionPolicy.RetrySeconds"/>, doubling up to
    /// <see cref="HubSessionPolicy.RetryCeilingSeconds"/>, and a held link is
    /// looked at every <see cref="HubSessionPolicy.ProbeSeconds"/> as well as the
    /// moment it drops. A hub that refuses the credentials is retried like any
    /// other failure until the refusals have lasted
    /// <see cref="HubSessionPolicy.RefusalGraceSeconds"/>. The SDK logs nothing;
    /// <see cref="OnStateChange"/> tells the application when the link comes up
    /// or goes down, and what deserves saying is the application's call.
    /// </para>
    /// </remarks>
    public sealed class HubSession : IAsyncDisposable
    {
        private readonly Func<CancellationToken, Task<ThalovantClient>> _connect;
        private readonly Func<double> _clock;
        private readonly object _state = new object();
        private readonly SemaphoreSlim _busy = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _closing = new CancellationTokenSource();
        private ThalovantClient? _client, _retired;
        private bool _closed;
        private Task? _warming;
        private double _retryAt, _retryWait;
        private bool _up;
        private TimeSpan _settle = TimeSpan.FromSeconds(0.75);
        private readonly List<Action<bool>> _stateCallbacks = new List<Action<bool>>();
        private sealed class Listener { public string Name = ""; public Action<ThalovantEvent> Handler = _ => { }; public ThalovantSubscription? Bound; }
        private readonly List<Listener> _listeners = new List<Listener>();
        public HubSessionPolicy Policy { get; }
        public HubSession(Func<CancellationToken, Task<ThalovantClient>> connect, HubSessionPolicy? policy = null, Func<double>? clock = null, bool warm = true)
        {
            _connect = connect ?? throw new ArgumentNullException(nameof(connect)); Policy = policy ?? new HubSessionPolicy(); _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency); _retryWait = Policy.RetrySeconds;
            if (warm) _ = WarmAsync();
        }

        /// <summary>
        /// A session whose clients connect over WSS with <paramref name="identity"/>.
        /// It does not dial until <see cref="ConnectAsync"/>, <see cref="RunAsync"/>
        /// or a call asks it to.
        /// </summary>
        public static HubSession ForIdentity(
            ThalovantIdentity identity,
            HubSessionPolicy? policy = null,
            TimeSpan? connectTimeout = null,
            IHiveMindNoiseStore? noiseStore = null,
            string? userAgent = null)
        {
            if (identity is null) throw new ArgumentNullException(nameof(identity));
            async Task<ThalovantClient> Connect(CancellationToken cancellationToken)
            {
                var client = new ThalovantClient(identity, HubProtocol.Wss, userAgent, noiseStore: noiseStore);
                try
                {
                    await client.ConnectAsync(connectTimeout, cancellationToken).ConfigureAwait(false);
                    return client;
                }
                catch
                {
                    try { await client.CloseAsync().ConfigureAwait(false); } catch (Exception) { /* The connect failure is the one to report. */ }
                    throw;
                }
            }
            return new HubSession(Connect, policy, warm: false);
        }

        public static bool Alive(ThalovantClient? client) { if (client == null) return false; try { var phase = client.ConnectionInfo().Phase; return phase != "closed" && phase != "error"; } catch { return true; } }
        public bool Held { get { lock (_state) return _client != null; } }

        /// <summary>Whether a client is held and its link is up right now.</summary>
        public bool Connected { get { ThalovantClient? client; lock (_state) client = _client; return client != null && client.LinkUp; } }

        public double RetryAt { get { lock (_state) return _retryAt; } }
        public double RetryWait { get { lock (_state) return _retryWait; } }
        public double ProbeDelay() => Held ? Policy.ProbeSeconds : Policy.ProbeDownSeconds;

        /// <summary>
        /// How long a new link must stay up before <see cref="ConnectAsync"/>
        /// counts it: a hub that does not know the client's static key says so
        /// only by closing right after the handshake. 0.75 seconds by default;
        /// zero turns the check off.
        /// </summary>
        public TimeSpan SettleWindow
        {
            get { lock (_state) return _settle; }
            set { if (value < TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(value)); lock (_state) _settle = value; }
        }

        public ThalovantSubscription On(string eventName, Action<ThalovantEvent> handler)
        {
            lock (_state)
            {
                if (_closed) throw new ObjectDisposedException(nameof(HubSession)); var listener = new Listener { Name = eventName, Handler = handler, Bound = _client?.On(eventName, handler) }; _listeners.Add(listener);
                return new ThalovantSubscription(() => { lock (_state) { listener.Bound?.Close(); _listeners.Remove(listener); } });
            }
        }

        /// <summary>
        /// Calls <paramref name="callback"/> with <c>true</c> when the link comes up
        /// and <c>false</c> when it goes down. A callback that throws does not stop
        /// the others. Close the subscription to stop.
        /// </summary>
        public ThalovantSubscription OnStateChange(Action<bool> callback)
        {
            if (callback is null) throw new ArgumentNullException(nameof(callback));
            lock (_state) _stateCallbacks.Add(callback);
            return new ThalovantSubscription(() => { lock (_state) _stateCallbacks.Remove(callback); });
        }

        private void SetState(bool up)
        {
            Action<bool>[] callbacks;
            lock (_state) { if (_up == up) return; _up = up; callbacks = _stateCallbacks.ToArray(); }
            foreach (var callback in callbacks)
            {
                try { callback(up); } catch (Exception) { /* One observer's failure is not the link's. */ }
            }
        }

        private async Task CleanupAsync() { if (_retired != null) { await _retired.CloseAsync().ConfigureAwait(false); _retired = null; } }
        private async Task DropAsync()
        {
            bool dropped;
            lock (_state) { dropped = _client != null; if (_client != null) { _retired = _client; _client = null; } foreach (var listener in _listeners) { listener.Bound?.Close(); listener.Bound = null; } }
            if (dropped) SetState(false);
            await CleanupAsync().ConfigureAwait(false);
        }
        private async Task<ThalovantClient> EnsureAsync(CancellationToken cancellationToken, bool settle = false)
        {
            lock (_state) { if (_closed) throw new ObjectDisposedException(nameof(HubSession)); }
            await CleanupAsync().ConfigureAwait(false); if (_client != null) return _client;
            ThalovantClient? fresh = null;
            try
            {
                fresh = await _connect(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); if (fresh == null) throw new InvalidOperationException("Hub factory returned no client");
                lock (_state) { if (_closed) throw new ObjectDisposedException(nameof(HubSession)); foreach (var listener in _listeners) listener.Bound = fresh.On(listener.Name, listener.Handler); if (!settle) _client = fresh; }
                if (settle)
                {
                    // Held only once it has settled: until then it is an attempt,
                    // not a link, and nothing may report it as connected.
                    await SettleAsync(fresh, cancellationToken).ConfigureAwait(false);
                    lock (_state) { if (_closed) throw new ObjectDisposedException(nameof(HubSession)); _client = fresh; }
                }
                lock (_state) { _retryAt = 0; _retryWait = Policy.RetrySeconds; }
                SetState(true);
                return fresh;
            }
            catch { lock (_state) { _retryAt = _clock() + _retryWait; _retryWait = Policy.NextWait(_retryWait); } _retired = fresh; await DropAsync().ConfigureAwait(false); throw; }
        }

        /// <summary>A new link that closes inside the settle window was refused, or dropped, rather than opened.</summary>
        private async Task SettleAsync(ThalovantClient client, CancellationToken cancellationToken)
        {
            TimeSpan settle;
            lock (_state) settle = _settle;
            var stopped = client.LinkStopped;
            if (stopped is null || settle <= TimeSpan.Zero) return;
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var first = await Task.WhenAny(stopped, Monotonic.QuietlyAsync(settle, timer.Token)).ConfigureAwait(false);
            timer.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (first != stopped) return;
            if (client.LinkRefused)
                throw new ThalovantHubRefusedException("The hub closed the link right after the handshake: it does not accept these credentials, or not yet.");
            throw new ThalovantConnectionException("The hub closed the link right after the handshake.");
        }

        public Task WarmAsync(CancellationToken cancellationToken = default)
        {
            lock (_state)
            {
                if (_closed || _clock() < _retryAt) return Task.CompletedTask; if (_warming != null && !_warming.IsCompleted) return _warming;
                _warming = Task.Run(async () => { try { await _busy.WaitAsync(cancellationToken).ConfigureAwait(false); try { await EnsureAsync(cancellationToken).ConfigureAwait(false); } finally { _busy.Release(); } } catch {/* Off-path state exposes backoff; foreground calls surface failures. */} }, CancellationToken.None); return _warming;
            }
        }
        public async Task ProbeAsync(CancellationToken cancellationToken = default)
        {
            if (!await _busy.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
            try { lock (_state) { if (_closed) return; } if (_client != null && !Alive(_client)) await DropAsync().ConfigureAwait(false); } finally { _busy.Release(); }
            if (!Held) await WarmAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Makes one attempt: returns with a live link, or throws why there is none.
        /// A link already up is kept.
        /// </summary>
        /// <remarks>
        /// Throws <see cref="ThalovantHubRefusedException"/> when the hub turns the
        /// credentials away -- including by closing inside <see cref="SettleWindow"/>
        /// right after the handshake -- and <see cref="ThalovantConnectionException"/>
        /// or <see cref="ThalovantTimeoutException"/> for everything else. A failed
        /// attempt moves the retry ladder on.
        /// </remarks>
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThalovantClient? held;
                lock (_state) { if (_closed) throw new ObjectDisposedException(nameof(HubSession)); held = _client; }
                if (held != null && held.LinkUp) return;
                if (held != null) await DropAsync().ConfigureAwait(false);
                await EnsureAsync(cancellationToken, settle: true).ConfigureAwait(false);
            }
            finally { _busy.Release(); }
        }

        /// <summary>
        /// Stays connected until the session is closed (then it returns) or
        /// <paramref name="cancellationToken"/> is cancelled (then it throws
        /// <see cref="OperationCanceledException"/>). A link <see cref="ConnectAsync"/>
        /// already opened is the one this keeps.
        /// </summary>
        /// <remarks>
        /// After every attempt it does what <c>link-keeping-vectors.json</c> says:
        /// a link that came up is held, and resets the ladder and the refusal
        /// clock; one that dropped is dialled again at once; a failed attempt waits
        /// the ladder's step (<see cref="HubSessionPolicy.RetrySeconds"/>, doubling
        /// to <see cref="HubSessionPolicy.RetryCeilingSeconds"/>); refusals wait the
        /// same way until they have lasted <see cref="HubSessionPolicy.RefusalGraceSeconds"/>,
        /// then this throws <see cref="ThalovantHubRefusedException"/>; and a
        /// changed hub key throws <see cref="ThalovantHubKeyChangedException"/> at
        /// once, since retrying cannot change it. A fault that will not fix itself
        /// -- a factory that cannot build a client -- is thrown at once too.
        /// </remarks>
        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
            var token = running.Token;
            var supervisor = new LinkSupervisor(Policy);
            while (!IsClosed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThalovantClient? held;
                lock (_state) held = _client;
                if (held != null && held.LinkUp)
                {
                    await StoppedOrProbeAsync(held, token).ConfigureAwait(false);
                    if (IsClosed) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!held.LinkUp)
                    {
                        await DropIfHeldAsync(held).ConfigureAwait(false);
                        supervisor.After(LinkOutcome.Dropped, _clock()); // dial again at once
                    }
                    continue;
                }
                LinkOutcome outcome;
                Exception failure;
                try
                {
                    await ConnectAsync(token).ConfigureAwait(false);
                    supervisor.After(LinkOutcome.Up, _clock());
                    continue;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (IsClosed) break;
                    throw;
                }
                catch (ObjectDisposedException) when (IsClosed)
                {
                    break;
                }
                catch (ThalovantHubKeyChangedException error)
                {
                    outcome = LinkOutcome.KeyChanged;
                    failure = error;
                }
                catch (ThalovantHubRefusedException error)
                {
                    outcome = LinkOutcome.Refused;
                    failure = error;
                }
                catch (Exception error) when (Retryable(error))
                {
                    outcome = LinkOutcome.Failed;
                    failure = error;
                }
                var decision = supervisor.After(outcome, _clock());
                if (decision.Action == LinkAction.GiveUp)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                }
                try
                {
                    await Monotonic.DelayAtLeastAsync(decision.Wait, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (IsClosed) break;
                    throw;
                }
            }
        }

        private bool IsClosed { get { lock (_state) return _closed; } }

        /// <summary>What a kept link retries: the hub or the network, not the configuration.</summary>
        private static bool Retryable(Exception error) =>
            error is ThalovantConnectionException || error is ThalovantTimeoutException
            || error is System.IO.IOException || error is System.Net.WebSockets.WebSocketException
            || error is System.Net.Http.HttpRequestException;

        /// <summary>Waits until the held link stops, the probe comes round, or the run ends.</summary>
        private async Task StoppedOrProbeAsync(ThalovantClient held, CancellationToken token)
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
            var probe = Monotonic.QuietlyAsync(TimeSpan.FromSeconds(Policy.ProbeSeconds), timer.Token);
            var stopped = held.LinkStopped;
            await (stopped is null ? probe : Task.WhenAny(stopped, probe)).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            timer.Cancel();
        }

        /// <summary>Drops <paramref name="held"/> when it is still the client held; a call may have replaced it.</summary>
        private async Task DropIfHeldAsync(ThalovantClient held)
        {
            await _busy.WaitAsync().ConfigureAwait(false);
            try
            {
                bool current;
                lock (_state) current = ReferenceEquals(_client, held);
                if (current && !held.LinkUp) await DropAsync().ConfigureAwait(false);
            }
            finally { _busy.Release(); }
        }

        private async Task<T> CallAsync<T>(Func<ThalovantClient, Task<T>> call, CancellationToken cancellationToken)
        {
            await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_client != null && !Alive(_client)) await DropAsync().ConfigureAwait(false); var client = await EnsureAsync(cancellationToken).ConfigureAwait(false);
                try { return await call(client).ConfigureAwait(false); } catch (ThalovantRuntimeException) { throw; } catch { await DropAsync().ConfigureAwait(false); throw; }
            }
            finally { _busy.Release(); }
        }
        public Task<ThalovantReply> AskAsync(string text, TimeSpan? timeout = null, string lang = "en-us", JsonObject? context = null, string? sessionId = null, string? requestId = null, TimeSpan? replySettle = null, TimeSpan? emptyReplyWait = null, CancellationToken cancellationToken = default) => CallAsync(client => client.AskAsync(text, timeout, lang, context, sessionId, requestId, replySettle, emptyReplyWait, cancellationToken), cancellationToken);
        public async Task EmitAsync(string eventType, JsonObject? data = null, JsonObject? context = null, CancellationToken cancellationToken = default) { await CallAsync(async client => { await client.EmitAsync(eventType, data, context, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false); }

        /// <summary>
        /// <see cref="ThalovantClient.ReplyAsync"/> on this session: answers a
        /// message the hub sent, back along the route it came.
        /// </summary>
        /// <remarks>
        /// A reply goes out on the link that is up without queueing behind a call
        /// in flight, because the hub waiting on it keeps its own clock. With no
        /// link up it connects first, like any call.
        /// </remarks>
        public async Task ReplyAsync(ThalovantEvent request, string messageType, JsonObject? data = null, JsonObject? context = null, CancellationToken cancellationToken = default)
        {
            ThalovantClient? held;
            lock (_state) { if (_closed) throw new ObjectDisposedException(nameof(HubSession)); held = _client; }
            if (held != null && held.LinkUp)
            {
                await held.ReplyAsync(request, messageType, data, context, cancellationToken).ConfigureAwait(false);
                return;
            }
            await CallAsync(async client => { await client.ReplyAsync(request, messageType, data, context, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Answers every <c>thalovant.home.request</c> on every client this session
        /// builds with one <c>thalovant.home.response</c> (<see cref="ThalovantHome"/>).
        /// Pair it with <see cref="RunAsync"/> to keep the link up. Close the
        /// returned subscription to stop; that cancels the answers still running.
        /// </summary>
        /// <param name="handler">What the home says. It runs off the receive loop, one task per request.</param>
        /// <param name="timeout">How long it has, <see cref="ThalovantHome.DefaultHandlerTimeout"/> by default.</param>
        public ThalovantSubscription AnswerHomeRequests(HomeRequestHandler handler, TimeSpan? timeout = null) =>
            ThalovantHome.AnswerEvery(
                (name, listener) => On(name, listener),
                (request, payload, token) => ReplyAsync(request, ThalovantHome.ResponseEvent, payload, cancellationToken: token),
                handler,
                timeout);

        public async Task CloseAsync() { lock (_state) { _closed = true; } _closing.Cancel(); await _busy.WaitAsync().ConfigureAwait(false); try { await DropAsync().ConfigureAwait(false); lock (_state) { _listeners.Clear(); } } finally { _busy.Release(); } }
        public ValueTask DisposeAsync() => new ValueTask(CloseAsync());
    }
    public sealed class OriginAttempt
    {
        public string Host { get; }
        public string? Address { get; }
        public TimeSpan ConnectTimeout { get; }
        public TimeSpan? HandshakeTimeout { get; }
        public OriginAttempt(string host, TimeSpan connectTimeout, TimeSpan? handshakeTimeout = null, string? address = null) { Host = host; ConnectTimeout = connectTimeout; HandshakeTimeout = handshakeTimeout; Address = address; }
    }
    /// <summary>The builder owns per-transport address binding and failed-attempt cleanup, preserving URL host and TLS/SNI.</summary>
    public sealed class OriginPreference
    {
        public string Address { get; }
        public TimeSpan HandshakeTimeout { get; }
        public TimeSpan Cooldown { get; }
        private readonly Func<double> _clock; private readonly object _state = new object(); private double _quietUntil;
        public OriginPreference(string address, TimeSpan? handshakeTimeout = null, TimeSpan? cooldown = null, Func<double>? clock = null) { Address = address; HandshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(1.5); Cooldown = cooldown ?? TimeSpan.FromMinutes(5); if (HandshakeTimeout <= TimeSpan.Zero || Cooldown <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(handshakeTimeout)); _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency); }
        public bool CoolingDown { get { lock (_state) return _clock() < _quietUntil; } }
        public async Task<T> ConnectAsync<T>(OriginAttempt options, Func<OriginAttempt, CancellationToken, Task<T>> build, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(options.Host) && !CoolingDown)
            {
                try { var client = await build(new OriginAttempt(options.Host, options.ConnectTimeout, HandshakeTimeout, Address), cancellationToken).ConfigureAwait(false); lock (_state) { _quietUntil = 0; } return client; }
                catch (OperationCanceledException) { throw; }
                catch { cancellationToken.ThrowIfCancellationRequested(); lock (_state) { _quietUntil = _clock() + Cooldown.TotalSeconds; } }
            }
            return await build(new OriginAttempt(options.Host, options.ConnectTimeout, options.HandshakeTimeout), cancellationToken).ConfigureAwait(false);
        }
        public static string HubHostname(string? master) { if (string.IsNullOrWhiteSpace(master)) return ""; var text = master!.Trim(); return Uri.TryCreate(text.Contains("://") ? text : "wss://" + text, UriKind.Absolute, out var uri) ? uri.Host : ""; }
    }
}
