using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace Thalovant
{
    public sealed class HubSessionPolicy
    {
        public double RetrySeconds { get; }
        public double RetryCeilingSeconds { get; }
        public double ProbeSeconds { get; }
        public double ProbeDownSeconds { get; }
        public HubSessionPolicy(double retrySeconds = 10, double retryCeilingSeconds = 120, double probeSeconds = 60, double probeDownSeconds = 5)
        {
            foreach (var value in new[] { retrySeconds, retryCeilingSeconds, probeSeconds, probeDownSeconds }) if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(retrySeconds));
            if (retryCeilingSeconds < retrySeconds) throw new ArgumentOutOfRangeException(nameof(retryCeilingSeconds));
            RetrySeconds = retrySeconds; RetryCeilingSeconds = retryCeilingSeconds; ProbeSeconds = probeSeconds; ProbeDownSeconds = probeDownSeconds;
        }
        public double NextWait(double current) => Math.Min(current * 2, RetryCeilingSeconds);
    }
    /// <summary>One managed connection. Failed admitted calls are never replayed.</summary>
    public sealed class HubSession : IAsyncDisposable
    {
        private readonly Func<CancellationToken, Task<ThalovantClient>> _connect;
        private readonly Func<double> _clock;
        private readonly object _state = new object();
        private readonly SemaphoreSlim _busy = new SemaphoreSlim(1, 1);
        private ThalovantClient? _client, _retired;
        private bool _closed;
        private Task? _warming;
        private double _retryAt, _retryWait;
        private sealed class Listener { public string Name = ""; public Action<ThalovantEvent> Handler = _ => { }; public ThalovantSubscription? Bound; }
        private readonly List<Listener> _listeners = new List<Listener>();
        public HubSessionPolicy Policy { get; }
        public HubSession(Func<CancellationToken, Task<ThalovantClient>> connect, HubSessionPolicy? policy = null, Func<double>? clock = null, bool warm = true)
        {
            _connect = connect ?? throw new ArgumentNullException(nameof(connect)); Policy = policy ?? new HubSessionPolicy(); _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency); _retryWait = Policy.RetrySeconds;
            if (warm) _ = WarmAsync();
        }
        public static bool Alive(ThalovantClient? client) { if (client == null) return false; try { var phase = client.ConnectionInfo().Phase; return phase != "closed" && phase != "error"; } catch { return true; } }
        public bool Held { get { lock (_state) return _client != null; } }
        public double RetryAt { get { lock (_state) return _retryAt; } }
        public double RetryWait { get { lock (_state) return _retryWait; } }
        public double ProbeDelay() => Held ? Policy.ProbeSeconds : Policy.ProbeDownSeconds;
        public ThalovantSubscription On(string eventName, Action<ThalovantEvent> handler)
        {
            lock (_state)
            {
                if (_closed) throw new ObjectDisposedException(nameof(HubSession)); var listener = new Listener { Name = eventName, Handler = handler, Bound = _client?.On(eventName, handler) }; _listeners.Add(listener);
                return new ThalovantSubscription(() => { lock (_state) { listener.Bound?.Close(); _listeners.Remove(listener); } });
            }
        }
        private async Task CleanupAsync() { if (_retired != null) { await _retired.CloseAsync().ConfigureAwait(false); _retired = null; } }
        private async Task DropAsync()
        {
            lock (_state) { if (_client != null) { _retired = _client; _client = null; } foreach (var listener in _listeners) { listener.Bound?.Close(); listener.Bound = null; } }
            await CleanupAsync().ConfigureAwait(false);
        }
        private async Task<ThalovantClient> EnsureAsync(CancellationToken cancellationToken)
        {
            lock (_state) { if (_closed) throw new ObjectDisposedException(nameof(HubSession)); }
            await CleanupAsync().ConfigureAwait(false); if (_client != null) return _client;
            ThalovantClient? fresh = null;
            try
            {
                fresh = await _connect(cancellationToken).ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested(); if (fresh == null) throw new InvalidOperationException("Hub factory returned no client");
                lock (_state) { if (_closed) throw new ObjectDisposedException(nameof(HubSession)); foreach (var listener in _listeners) listener.Bound = fresh.On(listener.Name, listener.Handler); _client = fresh; _retryAt = 0; _retryWait = Policy.RetrySeconds; }
                return fresh;
            }
            catch { lock (_state) { _retryAt = _clock() + _retryWait; _retryWait = Policy.NextWait(_retryWait); } _retired = fresh; await DropAsync().ConfigureAwait(false); throw; }
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
        public async Task CloseAsync() { lock (_state) { _closed = true; } await _busy.WaitAsync().ConfigureAwait(false); try { await DropAsync().ConfigureAwait(false); lock (_state) { _listeners.Clear(); } } finally { _busy.Release(); } }
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
