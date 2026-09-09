using System;
using System.Collections.Generic;
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
        private WebSocket? _socket;
        private readonly Func<WebSocket> _socketFactory = () => new ClientWebSocket();
        private readonly IHiveMindNoiseStore _noiseStore;
        private JsonObject? _serverHello;
        private NoiseHandshake? _noiseHandshake;
        private NoiseSession? _noiseSession;
        private (string NodeId, byte[] Key)? _cachedPsk;
        private void ResetSession() { _connected = false; _handshakeComplete = false; _serverHello = null; _noiseHandshake = null; _noiseSession = null; }
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
            _noiseStore = noiseStore ?? new HiveMindFileNoiseStore();
            // Resolved here rather than as a parameter default so that the
            // version is never inlined into a caller's assembly at their
            // compile time.
            UserAgent = userAgent ?? ThalovantDefaults.UserAgent;
        }

        // In-memory WebSocket peer seam keeps protocol tests independent of networks.
        internal HiveMindWssTransport(ThalovantIdentity identity, IHiveMindNoiseStore store, Func<WebSocket> socketFactory)
            : this(identity, noiseStore: store) { _socketFactory = socketFactory; }

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
            await DisconnectAsync().ConfigureAwait(false);
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(6);
            WebSocket socket;
            AsyncGate gate;
            CancellationToken receiveToken;
            var url = EndpointUri();
            lock (_lock) {
                _handshakeGate = gate = new AsyncGate();
                ResetSession();
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
                    if (socket is ClientWebSocket clientSocket) await clientSocket.ConnectAsync(url, connectTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !receiveToken.IsCancellationRequested)
                {
                    throw new ThalovantConnectionException("HiveMind WSS connect timed out.");
                }
                catch (WebSocketException exception)
                {
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
            _sendLock.Dispose();
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
                byte[][] frames;
                lock (_lock) {
                    RequireSocket(socket);
                    if (!ReferenceEquals(session, _noiseSession)) throw new OperationCanceledException("Noise session was replaced.");
                    var plain = Encoding.UTF8.GetBytes(HiveWire.Encode(message, cryptoKey: null, encrypt: false));
                    frames = session.Encrypt(plain).ToArray();
                }
                foreach (var frame in frames) {
                    lock (_lock) { RequireSocket(socket); }
                    await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                    lock (_lock) { RequireSocket(socket); }
                }
            }
            catch (Exception error) { HandleSocketFailure(socket, error); throw; }
            finally { _sendLock.Release(); }
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
                            HandleSocketClosed(socket, new ThalovantConnectionException(
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
                        string text;
                        if (authenticated) {
                            var state = _noiseSession ?? throw new ThalovantConnectionException("Binary frame received before Noise authentication.");
                            var decoded = state.Decrypt(data); if (!decoded.HasValue) continue;
                            if (!decoded.Value.Json) throw new ThalovantConnectionException("Binary HiveMind payloads were not negotiated.");
                            text = new UTF8Encoding(false, true).GetString(decoded.Value.Data);
                        } else {
                            if (_noiseSession != null) throw new ThalovantConnectionException("Plaintext frame received after Noise authentication.");
                            text = new UTF8Encoding(false, true).GetString(data);
                        }
                        message = HiveWire.Decode(text, cryptoKey: null);
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

        private void HandleSocketClosed(WebSocket socket, ThalovantConnectionException error)
        {
            lock (_lock) {
                if (!ReferenceEquals(socket, _socket)) return;
                var complete = _handshakeComplete;
                ResetSession();
                if (!complete) { _lastError = error.Message; _handshakeGate.Fail(error); }
            }
        }

        private void HandleSocketFailure(WebSocket socket, Exception error)
        {
            lock (_lock) {
                if (!ReferenceEquals(socket, _socket)) return;
                ResetSession();
                socket.Abort();
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
                    var pattern = pin != null && patterns.Contains("KKpsk0") ? "KKpsk0" : patterns.Contains("XXpsk2") ? "XXpsk2" : throw new ThalovantConnectionException("No supported Noise pattern offered.");
                    if (!Offered("suites").Contains(Noise.Suite)) throw new ThalovantConnectionException("No supported Noise suite offered (requires 25519_AESGCM_SHA256).");
                    var psk = _cachedPsk.HasValue && _cachedPsk.Value.NodeId == nodeId ? _cachedPsk.Value.Key : Noise.DerivePsk(Identity.Password, nodeId);
                    _cachedPsk = (nodeId, psk);
                    state = new NoiseHandshake(pattern, psk, Noise.Prologue(hello, payload, "Noise_" + pattern + "_" + Noise.Suite), _noiseStore.LoadOrCreateStaticKey(), pin);
                    _noiseHandshake = state;
                    var first = state.Write(Encoding.UTF8.GetBytes("{\"binarize\":false,\"encodings\":[]}"));
                    parameters = new JsonObject { ["pattern"] = pattern, ["suite"] = Noise.Suite, ["msg"] = Noise.Hex(first) };
                } else {
                    state = _noiseHandshake ?? throw new ThalovantConnectionException("Noise message arrived before offer.");
                    state.Read(Noise.Unhex(encoded!));
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
                _noiseStore.VerifyOrPin(nodeId, state.RemoteStatic ?? throw new ThalovantConnectionException("Missing authenticated server key."));
                _noiseSession = session = state.Session(); _noiseHandshake = null;
            }
            await SendCapturedAsync(socket, session, HiveWire.HelloMessage(Identity.SiteId, Identity.PublicKey, "thalovant-dotnet-" + Guid.NewGuid().ToString("D")), cancellationToken).ConfigureAwait(false);
            lock (_lock) {
                RequireSocket(socket);
                _handshakeComplete = true;
                _handshakeGate.Open();
            }
        }
    }
}
