using System;
using System.Collections.Generic;
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
    /// WSS data-plane transport for the HiveMind runtime, backed by
    /// <see cref="ClientWebSocket"/>.
    ///
    /// Wire protocol (mirrors the Node SDK's WSS transport):
    /// <list type="number">
    /// <item>Connect to the identity's WSS endpoint with
    /// <c>?authorization=base64("&lt;user agent&gt;:&lt;access key&gt;")</c>.</item>
    /// <item>The hub sends a <c>handshake</c>/<c>shake</c> frame with
    /// <c>payload.preshared_key</c>.</item>
    /// <item>The client answers with a plaintext <c>hello</c> frame carrying
    /// <c>pubkey</c>, <c>session.session_id</c>, and <c>site_id</c>; the handshake is
    /// then complete.</item>
    /// <item>Subsequent frames are JSON HiveMessages, AES-128-GCM encrypted with the
    /// identity <c>crypto_key</c> when one is present.</item>
    /// </list>
    /// </summary>
    public sealed class HiveMindWssTransport : IDisposable
    {
        public ThalovantIdentity Identity { get; }
        public string UserAgent { get; }

        private readonly object _lock = new object();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private ClientWebSocket? _socket;
        private CancellationTokenSource? _receiveCancellation;
        private bool _connected;
        private bool _handshakeComplete;
        private string? _lastError;
        private AsyncGate _handshakeGate = new AsyncGate();
        private readonly Dictionary<Guid, Action<JsonObject>> _busHandlers = new Dictionary<Guid, Action<JsonObject>>();
        private readonly Dictionary<Guid, Action<HiveMessage>> _messageHandlers = new Dictionary<Guid, Action<HiveMessage>>();

        public HiveMindWssTransport(ThalovantIdentity identity, string? userAgent = null)
        {
            Identity = identity;
            // Resolved here rather than as a parameter default so that the
            // version is never inlined into a caller's assembly at their
            // compile time.
            UserAgent = userAgent ?? ThalovantDefaults.UserAgent;
        }

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
            var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(6);
            ClientWebSocket socket;
            lock (_lock)
            {
                if (_connected && _handshakeComplete)
                {
                    return;
                }
                _handshakeGate = new AsyncGate();
                _handshakeComplete = false;
                _lastError = null;
            }

            var url = EndpointUri();
            socket = new ClientWebSocket();
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
                    await socket.ConnectAsync(url, connectTimeout.Token).ConfigureAwait(false);
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
                await _handshakeGate.WaitAsync(
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
            ClientWebSocket? socket;
            CancellationTokenSource? receiveCancellation;
            lock (_lock)
            {
                socket = _socket;
                receiveCancellation = _receiveCancellation;
                _socket = null;
                _receiveCancellation = null;
                _connected = false;
                _handshakeComplete = false;
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
            ClientWebSocket? socket;
            bool ready;
            lock (_lock)
            {
                socket = _socket;
                ready = _handshakeComplete;
            }
            if (socket is null || socket.State != WebSocketState.Open)
            {
                throw new ThalovantConnectionException("HiveMind WSS transport is not connected.");
            }
            var payload = HiveWire.Encode(message, Identity.CryptoKey, encrypt && ready);
            await SendTextAsync(socket, payload, cancellationToken).ConfigureAwait(false);
        }

        public Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
        {
            return SendAsync(HiveWire.BusMessage(type, data, context), cancellationToken: cancellationToken);
        }

        private async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken cancellationToken)
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

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
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
                            HandleSocketClosed(new ThalovantConnectionException(
                                $"HiveMind WSS closed before handshake completed ({(int?)result.CloseStatus ?? 0}){suffix}."));
                            return;
                        }
                        frame.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var data = frame.ToArray();
                    var message = result.MessageType == WebSocketMessageType.Text
                        ? HiveWire.Decode(Encoding.UTF8.GetString(data), Identity.CryptoKey)
                        : HiveWire.Decode(data, Identity.CryptoKey);
                    await HandleFrameAsync(message, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Disconnect requested.
            }
            catch (Exception exception)
            {
                HandleSocketFailure(exception);
            }
        }

        private void HandleSocketClosed(ThalovantConnectionException error)
        {
            bool handshakeWasComplete;
            lock (_lock)
            {
                _connected = false;
                handshakeWasComplete = _handshakeComplete;
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
                _connected = false;
                _lastError = error.Message;
            }
            var failure = error as ThalovantConnectionException
                ?? new ThalovantConnectionException($"HiveMind WSS connection failed: {error.Message}", error);
            _handshakeGate.Fail(failure);
        }

        private async Task HandleFrameAsync(HiveMessage message, CancellationToken cancellationToken)
        {
            switch (message.MsgType)
            {
                case "handshake":
                case "shake":
                    await HandleHandshakeAsync(message.Payload, cancellationToken).ConfigureAwait(false);
                    break;
                case "bus":
                {
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
            if (!HiveWire.IsPresharedKeyHandshake(payload))
            {
                throw new ThalovantConnectionException("Only HiveMind preshared-key handshakes are supported by this SDK.");
            }
            if (ThalovantCrypto.RuntimeKey(Identity.CryptoKey) is null)
            {
                throw new ThalovantConnectionException("HiveMind requested a preshared key, but identity.crypto_key is missing.");
            }
            var hello = HiveWire.HelloMessage(
                Identity.SiteId,
                Identity.PublicKey,
                "thalovant-dotnet-" + Guid.NewGuid().ToString("D").ToLowerInvariant());
            ClientWebSocket? socket;
            lock (_lock)
            {
                socket = _socket;
            }
            if (socket is null)
            {
                throw new ThalovantConnectionException("HiveMind WSS transport is not connected.");
            }
            // The hello reply is always sent unencrypted.
            var payloadText = HiveWire.Encode(hello, cryptoKey: null, encrypt: false);
            await SendTextAsync(socket, payloadText, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _handshakeComplete = true;
            }
            _handshakeGate.Open();
        }
    }
}
