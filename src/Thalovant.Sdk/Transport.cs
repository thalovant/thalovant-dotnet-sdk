using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace Thalovant
{
    /// <summary>
    /// One-shot async gate: <see cref="WaitAsync"/> completes when <see cref="Open"/> or
    /// <see cref="Fail"/> is called, or when the timeout elapses. On timeout it either
    /// throws the configured error or, when none is given, returns normally.
    /// </summary>
    internal sealed class AsyncGate
    {
        private readonly TaskCompletionSource<bool> _completion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Open()
        {
            _completion.TrySetResult(true);
        }

        internal void Fail(Exception error)
        {
            _completion.TrySetException(error);
        }

        internal bool IsOpen => _completion.Task.Status == TaskStatus.RanToCompletion;

        internal async Task WaitAsync(TimeSpan timeout, Exception? timeoutError, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(timeout, timeoutSource.Token);
            var completed = await Task.WhenAny(_completion.Task, delay).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (completed == _completion.Task)
            {
                timeoutSource.Cancel();
                await _completion.Task.ConfigureAwait(false);
                return;
            }
            if (timeoutError is not null)
            {
                throw timeoutError;
            }
        }
    }

    /// <summary>Timers that wait at least the whole duration.</summary>
    internal static class Monotonic
    {
        /// <summary>
        /// Waits at least <paramref name="wait"/> by the monotonic clock. A timer
        /// can wake early -- Windows' did, by a millisecond on a second -- and a
        /// wait that ends early is a poll before its deadline, or a request before
        /// the API said it may be sent.
        /// </summary>
        internal static async Task DelayAtLeastAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            var clock = Stopwatch.StartNew();
            if (wait <= TimeSpan.Zero)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            await Task.Delay(Capped(wait), cancellationToken).ConfigureAwait(false);
            for (var left = wait - clock.Elapsed; left > TimeSpan.Zero; left = wait - clock.Elapsed)
            {
                await Task.Delay(Capped(TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(left.TotalMilliseconds)))), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>Task.Delay takes at most int.MaxValue milliseconds; a longer wait is taken in turns.</summary>
        private static TimeSpan Capped(TimeSpan wait) =>
            wait.TotalMilliseconds > int.MaxValue - 1 ? TimeSpan.FromMilliseconds(int.MaxValue - 1) : wait;

        /// <summary>A task that completes once <paramref name="wait"/> has wholly passed, or never faults: cancellation just ends it.</summary>
        internal static Task QuietlyAsync(TimeSpan wait, CancellationToken cancellationToken) =>
            DelayAtLeastAsync(wait, cancellationToken).ContinueWith(_ => { }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// The bus operations <see cref="ThalovantClient"/> needs from a transport:
    /// connect, emit a bus event, and observe bus payloads. The WSS transport is
    /// the one production implementation; tests supply a fake hub through the
    /// client's internal constructor, the way the sibling SDKs take a
    /// <c>transport</c> parameter.
    /// </summary>
    internal interface IHiveMindBus
    {
        Task ConnectAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);

        Task DisconnectAsync();

        Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default);

        Guid AddBusHandler(Action<JsonObject> handler);

        void RemoveBusHandler(Guid id);
    }

    /// <summary>
    /// WSS data-plane transport for the HiveMind runtime, backed by
    /// <see cref="ClientWebSocket"/>.
    ///
    /// Requires HiveMind v3 Noise (XXpsk2 or pinned KKpsk0, AESGCM/SHA256).
    /// Application frames are authenticated binary Noise frames; legacy and
    /// plaintext application frames are rejected.
    /// </summary>
    public sealed class HiveMindWssTransport : IDisposable, IHiveMindBus, IHiveMindRuntimeStatus, IHiveMindQueryBus
    {
        public ThalovantIdentity Identity { get; }
        public string UserAgent { get; }

        private readonly object _lock = new object();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private readonly TimeSpan _physicalSendTimeout = TimeSpan.FromSeconds(20);
        private WebSocket? _socket;
        private readonly Func<WebSocket> _socketFactory = () => new ClientWebSocket();
        private readonly IHiveMindNoiseStore _noiseStore;
        private JsonObject? _serverHello;
        private NoiseHandshake? _noiseHandshake;
        private NoiseSession? _noiseSession;
        private (string NodeId, byte[] Key)? _cachedPsk;
        private void ResetSession() { _connected = false; _handshakeComplete = false; _serverHello = null; _noiseHandshake = null; _noiseSession = null; _stopped.TrySetResult(true); }

        /// <summary>Completes when the current link ends; already complete while there is none.</summary>
        private TaskCompletionSource<bool> _stopped = Ended();

        /// <summary>Whether the hub closed the last link the way it refuses credentials.</summary>
        private bool _closedRefused;

        /// <summary>Whether that refusal came right as an XX handshake ended: the hub refusing this client's own key.</summary>
        private bool _closedKeyRejected;

        /// <summary>
        /// Whether the hub has sent anything that decrypted under the current
        /// session's keys: once it has, it accepted the credentials, and no close
        /// after that is a refusal.
        /// </summary>
        private bool _heardFromHub;

        private static TaskCompletionSource<bool> Ended()
        {
            var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ended.SetResult(true);
            return ended;
        }

        /// <summary>
        /// Completes when the current link ends, so a waiter need not poll. Already
        /// complete while no link is up.
        /// </summary>
        internal Task Stopped
        {
            get
            {
                lock (_lock)
                {
                    return _stopped.Task;
                }
            }
        }

        /// <summary>
        /// Whether the hub closed the last link the way it refuses credentials: a
        /// close with no status, 1000, 1005 or 1008. A hub that does not know a
        /// client's static key says so only by closing right after the handshake,
        /// so a caller that just connected can tell that from a drop. A socket
        /// that failed without a close (1006, a reset) is the network's trouble,
        /// not a verdict on the credentials.
        /// </summary>
        internal bool ClosedRefused
        {
            get
            {
                lock (_lock)
                {
                    return _closedRefused;
                }
            }
        }

        /// <summary>
        /// Whether the last link's refusal came right as an XX handshake ended,
        /// with nothing from the hub in between: the hub pinned another key for
        /// this client. After KK the same close is a plain refusal, since the hub
        /// could only complete KK with the key it pinned.
        /// </summary>
        internal bool ClosedKeyRejected
        {
            get
            {
                lock (_lock)
                {
                    return _closedKeyRejected;
                }
            }
        }

        /// <summary>
        /// What a hub that closed the link right after the handshake meant by it:
        /// <see cref="ThalovantClientKeyRejectedException"/> when it refused this
        /// client's own key, otherwise a plain refusal.
        /// </summary>
        internal ThalovantHubRefusedException RefusalAfterHandshake()
        {
            if (!ClosedKeyRejected)
            {
                return new ThalovantHubRefusedException(
                    "The hub closed the link right after the handshake: it does not accept these credentials, or not yet.");
            }
            var (used, other) = KeyFolders();
            var where = used is null ? " This client's key is kept by its IHiveMindNoiseStore." : $" This client's key is in {used}.";
            var elsewhere = other is null
                ? ""
                : $" Another program that reads the same identity may keep its key in {other}, and the hub may have pinned that one.";
            return new ThalovantClientKeyRejectedException(
                "The hub refused this client's Noise key: it pinned a different key for this connection when it first connected."
                + where + elsewhere
                + " A new handshake cannot fix this. Re-pair, or share the key folder: give every program that uses this identity"
                + " the folder holding the key the hub trusts (the HiveMindFileNoiseStore directory).",
                used,
                other);
        }

        /// <summary>The folder this transport's key is in, and the other likely one for the same identity.</summary>
        internal (string? Used, string? Other) KeyFolders()
        {
            if (_noiseStore is not HiveMindFileNoiseStore files) return (null, null);
            var used = files.DirectoryPath;
            foreach (var candidate in new[] { HiveMindFileNoiseStore.BesideIdentity(Identity), HiveMindFileNoiseStore.DefaultDirectory })
            {
                if (candidate != null && !HiveMindFileNoiseStore.SamePath(candidate, used)) return (used, Path.GetFullPath(candidate));
            }
            return (used, null);
        }

        private CancellationTokenSource? _receiveCancellation;
        private bool _connected;
        private bool _handshakeComplete;
        private string? _lastError;
        private AsyncGate _handshakeGate = new AsyncGate();
        private readonly Dictionary<Guid, Action<JsonObject>> _busHandlers = new Dictionary<Guid, Action<JsonObject>>();
        private readonly Dictionary<Guid, Action<HiveMessage>> _messageHandlers = new Dictionary<Guid, Action<HiveMessage>>();

        public HiveMindWssTransport(ThalovantIdentity identity, string? userAgent = null, IHiveMindNoiseStore? noiseStore = null)
        {
            Identity = identity;
            // With no store, the key lives beside the identity's file when it
            // came from one, so every program reading that file shares it.
            _noiseStore = noiseStore ?? HiveMindFileNoiseStore.ForIdentity(identity);
            // Resolved here rather than as a parameter default so that the
            // version is never inlined into a caller's assembly at their
            // compile time.
            UserAgent = userAgent ?? ThalovantDefaults.UserAgent;
        }

        // In-memory WebSocket peer seam keeps protocol tests independent of networks.
        internal HiveMindWssTransport(ThalovantIdentity identity, IHiveMindNoiseStore store, Func<WebSocket> socketFactory, TimeSpan? physicalSendTimeout = null)
            : this(identity, noiseStore: store) { _socketFactory = socketFactory; _physicalSendTimeout = physicalSendTimeout ?? TimeSpan.FromSeconds(20); }

        public bool Connected
        {
            get
            {
                lock (_lock)
                {
                    return _connected;
                }
            }
        }

        public bool HandshakeComplete
        {
            get
            {
                lock (_lock)
                {
                    return _handshakeComplete;
                }
            }
        }

        public string? LastError
        {
            get
            {
                lock (_lock)
                {
                    return _lastError;
                }
            }
        }

        internal string Authorization => HiveWire.Authorization(UserAgent, Identity.AccessKey);

        /// <summary>The fully authorized WSS URL for this identity.</summary>
        public Uri EndpointUri()
        {
            var endpoint = Identity.EndpointFor(HubProtocol.Wss);
            if (endpoint is null)
            {
                throw new ThalovantConnectionException("The identity does not include a WSS endpoint.");
            }
            return HiveWire.AuthorizedEndpoint(endpoint, Authorization);
        }

        // -- Event registration ----------------------------------------------

        public Guid AddBusHandler(Action<JsonObject> handler)
        {
            var id = Guid.NewGuid();
            lock (_lock)
            {
                _busHandlers[id] = handler;
            }
            return id;
        }

        public void RemoveBusHandler(Guid id)
        {
            lock (_lock)
            {
                _busHandlers.Remove(id);
            }
        }

        internal Guid AddMessageHandler(Action<HiveMessage> handler)
        {
            var id = Guid.NewGuid();
            lock (_lock)
            {
                _messageHandlers[id] = handler;
            }
            return id;
        }

        internal void RemoveMessageHandler(Guid id)
        {
            lock (_lock)
            {
                _messageHandlers.Remove(id);
            }
        }

        // -- Lifecycle -------------------------------------------------------

        Guid IHiveMindQueryBus.AddQueryHandler(Action<HiveMessage> handler) => AddMessageHandler(handler);
        void IHiveMindQueryBus.RemoveQueryHandler(Guid id) => RemoveMessageHandler(id);
        Task IHiveMindQueryBus.SendQueryFrameAsync(HiveMessage message, CancellationToken cancellationToken) => SendAsync(message, cancellationToken: cancellationToken);

        public async Task ConnectAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var budget = timeout ?? TimeSpan.FromSeconds(6);
            if (budget <= TimeSpan.Zero || budget.TotalMilliseconds > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(timeout));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(budget);
            try {
                await _connectLock.WaitAsync(deadline.Token).ConfigureAwait(false);
                try { await ConnectCoreAsync(budget, deadline.Token).ConfigureAwait(false); }
                finally { _connectLock.Release(); }
            } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
                // A queued caller's deadline never owns the active caller's socket.
                throw new ThalovantConnectionException("HiveMind WSS handshake timed out.");
            }
        }

        private async Task ConnectCoreAsync(TimeSpan? timeout, CancellationToken cancellationToken)
        {
            if (Connected && HandshakeComplete) return;
            var budget = timeout ?? TimeSpan.FromSeconds(6);
            var clock = Stopwatch.StartNew();
            try
            {
                await ConnectAttemptAsync(budget, forceXX: false, cancellationToken).ConfigureAwait(false);
            }
            catch (ThalovantHubRefusedException) when (AttemptPattern == "KKpsk0" && budget - clock.Elapsed > TimeSpan.Zero)
            {
                // A KK attempt that failed -- its answer did not authenticate, or
                // the hub closed with a refusal code, which is what a hub does when
                // it cannot read a KK first message -- is followed at once by one
                // XX attempt, whose outcome is the connect's. Only XX tells a
                // changed password (a refusal) from a changed hub key: the pin is
                // still checked when XX completes, so this is not a downgrade.
                await ConnectAttemptAsync(budget - clock.Elapsed, forceXX: true, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>The Noise pattern the last connect attempt chose; null before one was chosen.</summary>
        internal string? AttemptPattern
        {
            get
            {
                lock (_lock)
                {
                    return _attemptPattern;
                }
            }
        }

        private string? _attemptPattern;
        private bool _forceXX;
        private long _handshakeCompletedAt;

        private async Task ConnectAttemptAsync(TimeSpan effectiveTimeout, bool forceXX, CancellationToken cancellationToken)
        {
            await DisconnectAsync().ConfigureAwait(false);
            WebSocket socket;
            AsyncGate gate;
            CancellationToken receiveToken;
            var url = EndpointUri();
            lock (_lock) {
                _handshakeGate = gate = new AsyncGate();
                ResetSession();
                _stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _closedRefused = false;
                _closedKeyRejected = false;
                _heardFromHub = false;
                _attemptPattern = null;
                _forceXX = forceXX;
                _lastError = null;
                socket = _socketFactory();
                _socket = socket;
                _receiveCancellation = new CancellationTokenSource();
                receiveToken = _receiveCancellation.Token;
            }
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, receiveToken);
                connectTimeout.CancelAfter(effectiveTimeout);
                try
                {
                    if (socket is ClientWebSocket clientSocket)
                    {
#if NET8_0_OR_GREATER
                        // So a 401/403 on the upgrade reads as a refusal of the
                        // credentials rather than as an unreachable hub.
                        clientSocket.Options.CollectHttpResponseDetails = true;
#endif
                        await clientSocket.ConnectAsync(url, connectTimeout.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !receiveToken.IsCancellationRequested)
                {
                    throw new ThalovantConnectionException("HiveMind WSS connect timed out.");
                }
                catch (WebSocketException exception)
                {
                    var status = UpgradeStatus(socket, exception);
                    if (status == 401 || status == 403)
                    {
                        throw new ThalovantHubRefusedException(
                            $"The hub refused this connection's credentials (HTTP {status}).");
                    }
                    throw new ThalovantConnectionException($"HiveMind WSS connect failed: {exception.Message}", exception);
                }
                lock (_lock)
                {
                    RequireSocket(socket);
                    _connected = true;
                }
                _ = Task.Run(() => ReceiveLoopAsync(socket, receiveToken));
                using var registration = cancellationToken.Register(() => gate.Fail(new OperationCanceledException(cancellationToken)));
                await gate.WaitAsync(
                    effectiveTimeout,
                    new ThalovantTimeoutException("HiveMind WSS handshake timed out.")).ConfigureAwait(false);
                lock (_lock) { RequireSocket(socket); }
            }
            catch (Exception exception)
            {
                HandleSocketFailure(socket, exception);
                throw;
            }
        }

        /// <summary>
        /// The HTTP status a failed WebSocket upgrade was answered with, or null.
        /// .NET 8 reports it on the socket; netstandard2.1 (Unity) has no such
        /// property, and its <see cref="ClientWebSocket"/> names the status only in
        /// the exception's message ("The server returned status code '401' when
        /// status code '101' was expected."), so that is read instead.
        /// </summary>
        internal static int? UpgradeStatus(WebSocket socket, WebSocketException exception)
        {
#if NET8_0_OR_GREATER
            if (socket is ClientWebSocket upgraded && upgraded.HttpStatusCode != 0)
            {
                return (int)upgraded.HttpStatusCode;
            }
#endif
            return UpgradeStatus(exception.Message);
        }

        internal static int? UpgradeStatus(string? message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(message, @"status code '([1-5][0-9]{2})' when status code '101'");
            return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
        }

        /// <summary>The RFC 6455 close codes a hub refuses credentials with.</summary>
        internal static readonly IReadOnlyList<int> RefusalCloseCodes = new[] { 1000, 1005, 1008 };

        /// <summary>How long after the handshake a close is still the hub's answer to it.</summary>
        internal const int RefusalSettleMs = 750;

        /// <summary>How late a transport may learn a close's code and still have it count.</summary>
        internal const int CloseCodeGraceMs = 250;

        /// <summary>
        /// Whether a close is the hub refusing the credentials rather than a drop:
        /// a code of 1000, 1005 (which includes a close frame with no status at
        /// all) or 1008, during the handshake -- any step of it -- or within
        /// <see cref="RefusalSettleMs"/> after it. <paramref name="code"/> is null
        /// when the socket ended with no close frame. The close's own time decides
        /// the window, not when its code was learnt, and a code learnt more than
        /// <see cref="CloseCodeGraceMs"/> late is no code.
        /// </summary>
        internal static bool CloseRefuses(int? code, long? closedAfterHandshakeMs, long codeLateMs = 0) =>
            CloseRefuses(code, closedAfterHandshakeMs, codeLateMs, afterAuthenticatedFrame: false);

        /// <summary>
        /// <see cref="CloseRefuses(int?, long?, long)"/>, knowing whether the hub had
        /// sent a frame that decrypted under the session's keys before it closed:
        /// then it had accepted the credentials -- a hub refuses a key before it
        /// sends anything -- and the close is a drop.
        /// </summary>
        internal static bool CloseRefuses(int? code, long? closedAfterHandshakeMs, long codeLateMs, bool afterAuthenticatedFrame)
        {
            if (afterAuthenticatedFrame) return false;
            if (code is not int value || !RefusalCloseCodes.Contains(value) || codeLateMs > CloseCodeGraceMs)
            {
                return false;
            }
            return closedAfterHandshakeMs is null || closedAfterHandshakeMs <= RefusalSettleMs;
        }

        public Task DisconnectAsync()
        {
            WebSocket? socket;
            CancellationTokenSource? receiveCancellation;
            lock (_lock)
            {
                socket = _socket;
                receiveCancellation = _receiveCancellation;
                _socket = null;
                _receiveCancellation = null;
                ResetSession();
                _handshakeGate.Fail(new OperationCanceledException("HiveMind WSS disconnected."));
            }
            receiveCancellation?.Cancel();
            receiveCancellation?.Dispose();
            if (socket is not null)
            {
                try
                {
                    socket.Abort();
                }
                catch (Exception)
                {
                    // Best-effort teardown.
                }
                socket.Dispose();
            }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            DisconnectAsync().GetAwaiter().GetResult();
            // Retained writes may still release this managed semaphore after
            // disconnect. Do not dispose it while those owners are unwinding.
        }

        // -- Sending ---------------------------------------------------------

        public Task SendAsync(HiveMessage message, bool encrypt = true, CancellationToken cancellationToken = default)
        {
            if (!encrypt) throw new ThalovantConnectionException("Plaintext application messages are forbidden by HiveMind v3.");
            lock (_lock) {
                var socket = _socket ?? throw new ThalovantConnectionException("HiveMind WSS is not connected.");
                var session = _noiseSession ?? throw new ThalovantConnectionException("Noise handshake is incomplete.");
                if (!_handshakeComplete) throw new ThalovantConnectionException("Noise handshake is incomplete.");
                return SendCapturedAsync(socket, session, message, cancellationToken);
            }
        }

        // Capture the socket and cipher before waiting for the send queue. A queued
        // message from the previous connection can never use a replacement session.
        private async Task SendCapturedAsync(WebSocket socket, NoiseSession session, HiveMessage message, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try {
                lock (_lock) {
                    // Cancellation before admission does not consume a Noise nonce
                    // or own the current socket's failure path.
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireSocket(socket);
                    if (!ReferenceEquals(session, _noiseSession)) throw new OperationCanceledException("Noise session was replaced.");
                }
            } catch { _sendLock.Release(); throw; }

            var outcome = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Once admitted, only the independent physical deadline can interrupt
            // the frame sequence. The caller may leave without aborting peers.
            _ = RunOwnedSendAsync(socket, session, message, outcome);
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult(true));
            await Task.WhenAny(outcome.Task, cancelled.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var error = await outcome.Task.ConfigureAwait(false);
            if (error != null) throw error;
        }

        private async Task RunOwnedSendAsync(WebSocket socket, NoiseSession session, HiveMessage message, TaskCompletionSource<Exception?> outcome)
        {
            var completed = false;
            void Complete(Exception? error)
            {
                lock (_lock) {
                    if (completed) return;
                    completed = true;
                    if (error != null) HandleSocketFailure(socket, error);
                    outcome.TrySetResult(error);
                }
            }
            using var physicalDeadline = new CancellationTokenSource();
            using var registration = physicalDeadline.Token.Register(() =>
                Complete(new ThalovantTimeoutException("HiveMind WSS physical send timed out.")));
            physicalDeadline.CancelAfter(_physicalSendTimeout);
            try {
                await Task.Run(async () => {
                    byte[][] frames;
                    lock (_lock) {
                        physicalDeadline.Token.ThrowIfCancellationRequested();
                        RequireSocket(socket);
                        if (!ReferenceEquals(session, _noiseSession)) throw new OperationCanceledException("Noise session was replaced.");
                        var plain = Encoding.UTF8.GetBytes(HiveWire.Encode(message, cryptoKey: null, encrypt: false));
                        frames = session.Encrypt(plain).ToArray();
                    }
                    foreach (var frame in frames) {
                        lock (_lock) { RequireSocket(socket); physicalDeadline.Token.ThrowIfCancellationRequested(); }
                        await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, physicalDeadline.Token).ConfigureAwait(false);
                        lock (_lock) { RequireSocket(socket); }
                    }
                }).ConfigureAwait(false);
                Complete(null);
            } catch (Exception error) { Complete(error); }
            finally {
                // Even a physical timeout cannot release the semaphore until a
                // cancellation-resistant socket's actual write has settled.
                _sendLock.Release();
            }
        }

        public Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
        {
            return SendAsync(HiveWire.BusMessage(type, data, context), cancellationToken: cancellationToken);
        }

        private async Task SendTextAsync(WebSocket socket, string text, CancellationToken cancellationToken)
        {
            var buffer = Encoding.UTF8.GetBytes(text);
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_lock) { RequireSocket(socket); }
                await socket.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
                lock (_lock) { RequireSocket(socket); }
            }
            catch (WebSocketException exception)
            {
                throw new ThalovantConnectionException($"HiveMind WSS send failed: {exception.Message}", exception);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // -- Receiving -------------------------------------------------------

        private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var frame = new MemoryStream();
            try
            {
                while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    frame.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            var reason = result.CloseStatusDescription;
                            var suffix = string.IsNullOrEmpty(reason) ? "" : $": {reason}";
                            HandleSocketClosed(socket, result.CloseStatus, new ThalovantConnectionException(
                                $"HiveMind WSS closed before handshake completed ({(int?)result.CloseStatus ?? 0}){suffix}."));
                            return;
                        }
                        if (frame.Length + result.Count > (result.MessageType == WebSocketMessageType.Binary ? 65535 : 262144)) throw new ThalovantConnectionException("HiveMind frame exceeds its size limit.");
                        frame.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    HiveMessage message;
                    bool authenticated = result.MessageType == WebSocketMessageType.Binary;
                    lock (_lock) {
                        RequireSocket(socket);
                        var data = frame.ToArray();
                        if (authenticated) {
                            var state = _noiseSession ?? throw new ThalovantConnectionException("Binary frame received before Noise authentication.");
                            var decoded = state.Decrypt(data);
                            // Any frame that decrypts is the hub speaking under this
                            // session's keys -- a chunk of a larger message included.
                            _heardFromHub = true;
                            if (!decoded.HasValue) continue;
                            // The Noise framing marks each frame JSON or not. One
                            // marked binary is a WIRE-1 frame -- how a hub answers
                            // speak:synth with the rendered audio, and how a file
                            // arrives. Refusing it here made every one unreachable.
                            message = decoded.Value.Json
                                ? HiveWire.Decode(new UTF8Encoding(false, true).GetString(decoded.Value.Data), cryptoKey: null)
                                : HiveWire.DecodeBinaryFrame(decoded.Value.Data);
                        } else {
                            if (_noiseSession != null) throw new ThalovantConnectionException("Plaintext frame received after Noise authentication.");
                            message = HiveWire.Decode(new UTF8Encoding(false, true).GetString(data), cryptoKey: null);
                        }
                    }
                    await HandleFrameAsync(socket, message, cancellationToken, authenticated).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Disconnect requested.
            }
            catch (Exception exception)
            {
                HandleSocketFailure(socket, exception);
            }
        }

        // Call only while holding _lock. Identity checks and the mutations they
        // authorize must be atomic with respect to disconnect and replacement.
        private void RequireSocket(WebSocket socket)
        {
            if (!ReferenceEquals(socket, _socket)) throw new OperationCanceledException("Noise connection was replaced.");
        }

        private void HandleSocketClosed(WebSocket socket, WebSocketCloseStatus? status, ThalovantConnectionException error)
        {
            lock (_lock) {
                if (!ReferenceEquals(socket, _socket)) return;
                var complete = _handshakeComplete;
                // A close frame with no status is 1005; the close's own time,
                // counted from the end of the handshake, decides the window.
                long? after = complete
                    ? (long)((Stopwatch.GetTimestamp() - _handshakeCompletedAt) * 1000.0 / Stopwatch.Frequency)
                    : (long?)null;
                // ClientWebSocket reports a close's code with the close itself,
                // so the code is never late here.
                var refused = CloseRefuses(status.HasValue ? (int)status.Value : 1005, after, codeLateMs: 0, afterAuthenticatedFrame: _heardFromHub);
                _closedRefused = refused;
                _closedKeyRejected = refused && complete && _attemptPattern == "XXpsk2";
                ResetSession();
                if (!complete)
                {
                    if (refused) error = new ThalovantHubRefusedException("The hub refused this connection's credentials.");
                    _lastError = error.Message;
                    _handshakeGate.Fail(error);
                }
            }
        }

        private void HandleSocketFailure(WebSocket socket, Exception error)
        {
            lock (_lock) {
                if (!ReferenceEquals(socket, _socket)) return;
                ResetSession();
                try { socket.Abort(); }
                catch (Exception) { /* Teardown must not hide the failure or escape a deadline callback. */ }
                _lastError = error.Message;
                var failure = error as ThalovantConnectionException
                    ?? new ThalovantConnectionException($"HiveMind WSS connection failed: {error.Message}", error);
                _handshakeGate.Fail(failure);
            }
        }

        private async Task HandleFrameAsync(WebSocket socket, HiveMessage message, CancellationToken cancellationToken, bool authenticated)
        {
            if (message.MsgType == "handshake" || message.MsgType == "shake") {
                lock (_lock) {
                    RequireSocket(socket);
                    if (authenticated) throw new ThalovantConnectionException("Unexpected encrypted handshake.");
                }
                await HandleHandshakeAsync(socket, message.Payload, cancellationToken).ConfigureAwait(false);
            }
            Action<JsonObject>[] busHandlers = Array.Empty<Action<JsonObject>>();
            Action<HiveMessage>[] messageHandlers;
            lock (_lock) {
                RequireSocket(socket);
                switch (message.MsgType) {
                    case "hello":
                        if (!authenticated) {
                            if (_serverHello != null || _noiseHandshake != null || string.IsNullOrWhiteSpace(JsonUtil.GetString(message.Payload["node_id"])))
                                throw new ThalovantConnectionException("Invalid or duplicate server HELLO.");
                            _serverHello = message.Payload;
                        }
                        break;
                    case "handshake": case "shake": break;
                    case "bus":
                        if (!authenticated || !_handshakeComplete) throw new ThalovantConnectionException("Application frame received before Noise authentication.");
                        busHandlers = _busHandlers.Values.ToArray();
                        break;
                    default:
                        if (!authenticated) throw new ThalovantConnectionException("Unexpected plaintext application frame.");
                        break;
                }
                messageHandlers = _messageHandlers.Values.ToArray();
            }
            // The frame is admitted atomically above. Invoke application code
            // outside the lifecycle lock so callbacks can send a response or
            // disconnect without deadlocking an asynchronous send continuation.
            foreach (var handler in busHandlers) handler(message.Payload);
            foreach (var handler in messageHandlers) handler(message);
        }

        private async Task HandleHandshakeAsync(WebSocket socket, JsonObject payload, CancellationToken cancellationToken)
        {
            NoiseHandshake state;
            string nodeId;
            JsonObject? parameters = null;
            bool completing;
            lock (_lock) {
                RequireSocket(socket);
                var noise = payload["noise"] as JsonObject ?? throw new ThalovantConnectionException("HiveMind v3 Noise is required; legacy downgrade refused.");
                var hello = _serverHello ?? throw new ThalovantConnectionException("Noise offer arrived before server HELLO.");
                nodeId = JsonUtil.GetString(hello["node_id"])!;
                var encoded = JsonUtil.GetString(noise["msg"]);
                completing = encoded != null;
                if (!completing) {
                    if (_noiseHandshake != null) throw new ThalovantConnectionException("Duplicate Noise offer.");
                    if (!int.TryParse(payload["max_protocol_version"]?.ToJsonString(), out var version) || version < 3) throw new ThalovantConnectionException("Server does not advertise HiveMind v3.");
                    string[] Offered(string field) => (noise[field] as JsonArray)?.Select(v => JsonUtil.GetString(v) ?? "").ToArray() ?? Array.Empty<string>();
                    var pin = _noiseStore.LoadPin(nodeId);
                    var patterns = Offered("patterns");
                    var pattern = pin != null && !_forceXX && patterns.Contains("KKpsk0") ? "KKpsk0" : patterns.Contains("XXpsk2") ? "XXpsk2" : throw new ThalovantConnectionException("No supported Noise pattern offered.");
                    _attemptPattern = pattern;
                    if (!Offered("suites").Contains(Noise.Suite)) throw new ThalovantConnectionException("No supported Noise suite offered (requires 25519_AESGCM_SHA256).");
                    var psk = _cachedPsk.HasValue && _cachedPsk.Value.NodeId == nodeId ? _cachedPsk.Value.Key : Noise.DerivePsk(Identity.Password, nodeId);
                    _cachedPsk = (nodeId, psk);
                    state = new NoiseHandshake(pattern, psk, Noise.Prologue(hello, payload, "Noise_" + pattern + "_" + Noise.Suite), _noiseStore.LoadOrCreateStaticKey(), pin);
                    _noiseHandshake = state;
                    var first = state.Write(Encoding.UTF8.GetBytes("{\"binarize\":false,\"encodings\":[]}"));
                    parameters = new JsonObject { ["pattern"] = pattern, ["suite"] = Noise.Suite, ["msg"] = Noise.Hex(first) };
                } else {
                    state = _noiseHandshake ?? throw new ThalovantConnectionException("Noise message arrived before offer.");
                    try
                    {
                        state.Read(Noise.Unhex(encoded!));
                    }
                    catch (NoisePinMismatchException)
                    {
                        throw new ThalovantHubKeyChangedException(
                            "The hub's Noise key is not the one pinned for it: the hub was replaced, or something is standing in for it. "
                            + "Verify its rotation before removing the saved pin.");
                    }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                        // Its answer does not authenticate under the key this
                        // password derives: the password is wrong, or has changed.
                        throw new ThalovantHubRefusedException(
                            "The hub's Noise handshake did not authenticate: this connection's password is wrong, or has changed.");
                    }
                    if (!state.Finished) parameters = new JsonObject { ["msg"] = Noise.Hex(state.Write()) };
                }
            }
            if (parameters != null) {
                var message = new HiveMessage("shake", new JsonObject { ["noise"] = parameters });
                await SendTextAsync(socket, HiveWire.Encode(message, cryptoKey: null, encrypt: false), cancellationToken).ConfigureAwait(false);
            }
            if (!completing) return;
            NoiseSession session;
            lock (_lock) {
                RequireSocket(socket);
                if (!ReferenceEquals(state, _noiseHandshake)) throw new OperationCanceledException("Noise handshake was replaced.");
                var remote = state.RemoteStatic ?? throw new ThalovantConnectionException("Missing authenticated server key.");
                if (_noiseStore.LoadPin(nodeId) is byte[] pinned && !Noise.Equal(pinned, remote))
                {
                    throw new ThalovantHubKeyChangedException(
                        "The hub's Noise key is not the one pinned for it: the hub was replaced, or something is standing in for it. "
                        + "Verify its rotation before removing the saved pin.");
                }
                _noiseStore.VerifyOrPin(nodeId, remote);
                _noiseSession = session = state.Session(); _noiseHandshake = null;
            }
            await SendCapturedAsync(socket, session, HiveWire.HelloMessage(Identity.SiteId, Identity.PublicKey, "thalovant-dotnet-" + Guid.NewGuid().ToString("D")), cancellationToken).ConfigureAwait(false);
            lock (_lock) {
                RequireSocket(socket);
                _handshakeComplete = true;
                _handshakeCompletedAt = Stopwatch.GetTimestamp();
                _handshakeGate.Open();
            }
        }
    }
}
