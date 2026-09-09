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

        internal async Task WaitAsync(TimeSpan timeout, Exception? timeoutError)
        {
            using var timeoutSource = new CancellationTokenSource();
            var delay = Task.Delay(timeout, timeoutSource.Token);
            var completed = await Task.WhenAny(_completion.Task, delay).ConfigureAwait(false);
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
    public sealed class HiveMindWssTransport : IDisposable, IHiveMindBus
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

        public async Task ConnectAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await ConnectCoreAsync(timeout, cancellationToken).ConfigureAwait(false); }
            finally { _connectLock.Release(); }
        }

        private async Task ConnectCoreAsync(TimeSpan? timeout, CancellationToken cancellationToken)
        {
            if (Connected && HandshakeComplete) return;
            await DisconnectAsync().ConfigureAwait(false);
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(6);
            WebSocket socket;
            lock (_lock)
            {
                if (_connected && _handshakeComplete)
                {
                    return;
                }
                _handshakeGate = new AsyncGate();
                ResetSession();
                _lastError = null;
            }

            var url = EndpointUri();
            socket = _socketFactory();
            var receiveCancellation = new CancellationTokenSource();
            lock (_lock)
            {
                _socket = socket;
                _receiveCancellation = receiveCancellation;
            }
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(effectiveTimeout);
                try
                {
                    if (socket is ClientWebSocket clientSocket) await clientSocket.ConnectAsync(url, connectTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ThalovantConnectionException("HiveMind WSS connect timed out.");
                }
                catch (WebSocketException exception)
                {
                    throw new ThalovantConnectionException($"HiveMind WSS connect failed: {exception.Message}", exception);
                }
                lock (_lock)
                {
                    _connected = true;
                }
                _ = Task.Run(() => ReceiveLoopAsync(socket, receiveCancellation.Token));
                var gate = _handshakeGate;
                using var registration = cancellationToken.Register(() => gate.Fail(new OperationCanceledException(cancellationToken)));
                await gate.WaitAsync(
                    effectiveTimeout,
                    new ThalovantTimeoutException("HiveMind WSS handshake timed out.")).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lock (_lock)
                {
                    _lastError = exception.Message;
                }
                await DisconnectAsync().ConfigureAwait(false);
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

        public async Task SendAsync(HiveMessage message, bool encrypt = true, CancellationToken cancellationToken = default)
        {
            if (!encrypt) throw new ThalovantConnectionException("Plaintext application messages are forbidden by HiveMind v3.");
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            WebSocket? socket = null;
            try {
                NoiseSession session;
                lock (_lock) {
                    socket = _socket ?? throw new ThalovantConnectionException("HiveMind WSS is not connected.");
                    session = _noiseSession ?? throw new ThalovantConnectionException("Noise handshake is incomplete.");
                }
                var plain = Encoding.UTF8.GetBytes(HiveWire.Encode(message, cryptoKey: null, encrypt: false));
                foreach (var frame in session.Encrypt(plain)) {
                    lock (_lock) { if (!ReferenceEquals(socket, _socket) || !ReferenceEquals(session, _noiseSession)) throw new OperationCanceledException("Noise connection was replaced during send."); }
                    await socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) { if (ReferenceEquals(socket, _socket)) HandleSocketFailure(error); throw; }
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
                await socket.SendAsync(
                    new ArraySegment<byte>(buffer),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
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
                            if (!ReferenceEquals(socket, _socket)) return;
                            HandleSocketClosed(new ThalovantConnectionException(
                                $"HiveMind WSS closed before handshake completed ({(int?)result.CloseStatus ?? 0}){suffix}."));
                            return;
                        }
                        if (frame.Length + result.Count > (result.MessageType == WebSocketMessageType.Binary ? 65535 : 262144)) throw new ThalovantConnectionException("HiveMind frame exceeds its size limit.");
                        frame.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (!ReferenceEquals(socket, _socket)) return;
                    var data = frame.ToArray();
                    bool authenticated = result.MessageType == WebSocketMessageType.Binary;
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
                    var message = HiveWire.Decode(text, cryptoKey: null);
                    await HandleFrameAsync(message, cancellationToken, authenticated).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Disconnect requested.
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(socket, _socket)) HandleSocketFailure(exception);
            }
        }

        private void HandleSocketClosed(ThalovantConnectionException error)
        {
            bool handshakeWasComplete;
            lock (_lock)
            {
                handshakeWasComplete = _handshakeComplete;
                ResetSession();
                if (!handshakeWasComplete)
                {
                    _lastError = error.Message;
                }
            }
            if (!handshakeWasComplete)
            {
                _handshakeGate.Fail(error);
            }
        }

        private void HandleSocketFailure(Exception error)
        {
            lock (_lock)
            {
                ResetSession();
                _socket?.Abort();
                _lastError = error.Message;
            }
            var failure = error as ThalovantConnectionException
                ?? new ThalovantConnectionException($"HiveMind WSS connection failed: {error.Message}", error);
            _handshakeGate.Fail(failure);
        }

        private async Task HandleFrameAsync(HiveMessage message, CancellationToken cancellationToken, bool authenticated)
        {
            switch (message.MsgType)
            {
                case "hello":
                    if (!authenticated) {
                        if (_serverHello != null || _noiseHandshake != null || string.IsNullOrWhiteSpace(JsonUtil.GetString(message.Payload["node_id"])))
                            throw new ThalovantConnectionException("Invalid or duplicate server HELLO.");
                        _serverHello = message.Payload;
                    }
                    break;
                case "handshake":
                case "shake":
                    if (authenticated) throw new ThalovantConnectionException("Unexpected encrypted handshake.");
                    await HandleHandshakeAsync(message.Payload, cancellationToken).ConfigureAwait(false);
                    break;
                case "bus":
                {
                    if (!authenticated || !HandshakeComplete) throw new ThalovantConnectionException("Application frame received before Noise authentication.");
                    List<Action<JsonObject>> handlers;
                    lock (_lock)
                    {
                        handlers = new List<Action<JsonObject>>(_busHandlers.Values);
                    }
                    foreach (var handler in handlers)
                    {
                        handler(message.Payload);
                    }
                    break;
                }
                default:
                    if (!authenticated) throw new ThalovantConnectionException("Unexpected plaintext application frame.");
                    break;
            }
            List<Action<HiveMessage>> messageHandlers;
            lock (_lock)
            {
                messageHandlers = new List<Action<HiveMessage>>(_messageHandlers.Values);
            }
            foreach (var handler in messageHandlers)
            {
                handler(message);
            }
        }

        private async Task HandleHandshakeAsync(JsonObject payload, CancellationToken cancellationToken)
        {
            var noise = payload["noise"] as JsonObject ?? throw new ThalovantConnectionException("HiveMind v3 Noise is required; legacy downgrade refused.");
            var hello = _serverHello ?? throw new ThalovantConnectionException("Noise offer arrived before server HELLO.");
            var nodeId = JsonUtil.GetString(hello["node_id"])!;
            var socket = _socket ?? throw new ThalovantConnectionException("HiveMind WSS is not connected.");
            async Task SendHandshake(JsonObject parameters) {
                var message = new HiveMessage("shake", new JsonObject { ["noise"] = parameters });
                await SendTextAsync(socket, HiveWire.Encode(message, cryptoKey: null, encrypt: false), cancellationToken).ConfigureAwait(false);
            }
            var encoded = JsonUtil.GetString(noise["msg"]);
            if (encoded == null) {
                if (_noiseHandshake != null) throw new ThalovantConnectionException("Duplicate Noise offer.");
                if (!int.TryParse(payload["max_protocol_version"]?.ToJsonString(), out var version) || version < 3) throw new ThalovantConnectionException("Server does not advertise HiveMind v3.");
                string[] Offered(string field) => (noise[field] as JsonArray)?.Select(v => JsonUtil.GetString(v) ?? "").ToArray() ?? Array.Empty<string>();
                var pin = _noiseStore.LoadPin(nodeId);
                var patterns = Offered("patterns");
                var pattern = pin != null && patterns.Contains("KKpsk0") ? "KKpsk0" : patterns.Contains("XXpsk2") ? "XXpsk2" : throw new ThalovantConnectionException("No supported Noise pattern offered.");
                if (!Offered("suites").Contains(Noise.Suite)) throw new ThalovantConnectionException("No supported Noise suite offered (requires 25519_AESGCM_SHA256).");
                var psk = _cachedPsk.HasValue && _cachedPsk.Value.NodeId == nodeId ? _cachedPsk.Value.Key : Noise.DerivePsk(Identity.Password, nodeId);
                _cachedPsk = (nodeId, psk);
                var state = new NoiseHandshake(pattern, psk, Noise.Prologue(hello, payload, "Noise_" + pattern + "_" + Noise.Suite), _noiseStore.LoadOrCreateStaticKey(), pin);
                _noiseHandshake = state;
                var first = state.Write(Encoding.UTF8.GetBytes("{\"binarize\":false,\"encodings\":[]}"));
                await SendHandshake(new JsonObject { ["pattern"] = pattern, ["suite"] = Noise.Suite, ["msg"] = Noise.Hex(first) }).ConfigureAwait(false);
            } else {
                var state = _noiseHandshake ?? throw new ThalovantConnectionException("Noise message arrived before offer.");
                state.Read(Noise.Unhex(encoded));
                if (!state.Finished) await SendHandshake(new JsonObject { ["msg"] = Noise.Hex(state.Write()) }).ConfigureAwait(false);
                _noiseStore.VerifyOrPin(nodeId, state.RemoteStatic ?? throw new ThalovantConnectionException("Missing authenticated server key."));
                if (!ReferenceEquals(socket, _socket)) throw new OperationCanceledException("Noise connection was replaced.");
                _noiseSession = state.Session(); _noiseHandshake = null;
                await SendAsync(HiveWire.HelloMessage(Identity.SiteId, Identity.PublicKey, "thalovant-dotnet-" + Guid.NewGuid().ToString("D")), cancellationToken: cancellationToken).ConfigureAwait(false);
                lock (_lock) { if (!ReferenceEquals(socket, _socket)) throw new OperationCanceledException("Noise connection was replaced."); _handshakeComplete = true; }
                _handshakeGate.Open();
            }
        }
    }
}
