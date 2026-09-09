using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    [Collection("Runtime deadlines")]
    public sealed class NoiseTransportTests
    {
        private static ThalovantIdentity Identity() => ThalovantIdentity.FromJson("""
        {"access_key":"test-access","password":"test-password","site_id":"test-site","default_master":"ws://test.invalid"}
        """);
        [Fact] public async Task InMemoryPeerAuthenticatesEncryptedHelloAskReplyAndSameClientReconnect()
        {
            var store = new MemoryStore(); var peers = new List<PeerSocket>(); byte[]? pinnedClient = null;
            using var transport = new HiveMindWssTransport(Identity(), store, () => {
                var peer = new PeerSocket(pinnedClient, key => pinnedClient = key); peers.Add(peer); return peer;
            });
            using var client = new ThalovantClient(Identity(), transport, replySettle: TimeSpan.Zero);
            for (var run = 0; run < 2; run++) {
                await client.ConnectAsync(TimeSpan.FromSeconds(10)); Assert.True(transport.Connected && transport.HandshakeComplete);
                var reply = await client.AskAsync("test question", timeout: TimeSpan.FromSeconds(5));
                Assert.Equal("test answer", reply.Text);
                Assert.Equal(run == 0 ? "XXpsk2" : "KKpsk0", peers[run].Pattern);
                Assert.Equal(new[] { "hello", "bus" }, peers[run].AuthenticatedTypes.ToArray());
                await client.CloseAsync(); Assert.False(transport.Connected); Assert.False(transport.HandshakeComplete);
            }
            Assert.NotNull(pinnedClient);
        }
        [Fact] public async Task QueuedDeadlineAndCancellationCannotCancelActiveConnect()
        {
            PeerSocket? peer = null; var connections = 0;
            using var transport = new HiveMindWssTransport(Identity(), new MemoryStore(), () => {
                connections++; return peer = new PeerSocket(null, _ => { }, "held");
            });
            var owner = transport.ConnectAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(peer);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<ThalovantConnectionException>(() => transport.ConnectAsync(TimeSpan.FromMilliseconds(30)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
            using var cancellation = new CancellationTokenSource();
            var queued = transport.ConnectAsync(TimeSpan.FromSeconds(10), cancellation.Token); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(owner.IsCompleted); peer!.ReleaseHandshake();
            await owner;
            Assert.Equal(1, connections); Assert.True(transport.Connected && transport.HandshakeComplete);
        }
        [Theory]
        [InlineData("legacy")]
        [InlineData("plaintext")]
        [InlineData("wrong-password")]
        public async Task InvalidPeersNeverReportReady(string mode)
        {
            using var transport = new HiveMindWssTransport(Identity(), new MemoryStore(), () => new PeerSocket(null, _ => { }, mode));
            await Assert.ThrowsAnyAsync<Exception>(() => transport.ConnectAsync(TimeSpan.FromSeconds(5)));
            Assert.False(transport.Connected); Assert.False(transport.HandshakeComplete);
        }
        [Fact] public async Task AuthenticatedSessionClosesOnPlaintextAndCanReconnect()
        {
            var peers = new List<PeerSocket>();
            using var transport = new HiveMindWssTransport(Identity(), new MemoryStore(), () => { var p = new PeerSocket(null, _ => { }); peers.Add(p); return p; });
            await transport.ConnectAsync(TimeSpan.FromSeconds(10));
            peers[0].QueueText("{\"msg_type\":\"bus\",\"payload\":{\"type\":\"speak\"}}");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (transport.Connected && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.False(transport.Connected); Assert.False(transport.HandshakeComplete);
            await transport.ConnectAsync(TimeSpan.FromSeconds(10)); Assert.True(transport.HandshakeComplete);
        }
        [Fact] public async Task DelayedOldFrameCannotDecryptOrDispatchThroughAReplacementSession()
        {
            var peers = new List<PeerSocket>(); var received = new ConcurrentQueue<string>();
            using var transport = new HiveMindWssTransport(Identity(), new MemoryStore(), () => { var peer = new PeerSocket(null, _ => { }); peers.Add(peer); return peer; });
            transport.AddBusHandler(message => received.Enqueue(message["type"]!.GetValue<string>()));
            await transport.ConnectAsync(TimeSpan.FromSeconds(10));
            var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            peers[0].BeforeNextReceive = async () => { held.SetResult(true); await release.Task; };
            peers[0].QueueBus("test.stale");
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await transport.DisconnectAsync();
            await transport.ConnectAsync(TimeSpan.FromSeconds(10));
            release.SetResult(true);
            await peers[0].HeldReceiveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            peers[1].QueueBus("test.current");
            var limit = DateTime.UtcNow.AddSeconds(5);
            while (received.IsEmpty && DateTime.UtcNow < limit) await Task.Delay(10);
            Assert.Equal(new[] { "test.current" }, received.ToArray());
            Assert.True(transport.Connected && transport.HandshakeComplete);
        }

        [Fact] public async Task DelayedOldSendFailureCannotResetReplacementHandshake()
        {
            var peers = new List<PeerSocket>();
            using var transport = new HiveMindWssTransport(Identity(), new MemoryStore(), () => { var peer = new PeerSocket(null, _ => { }); peers.Add(peer); return peer; });
            await transport.ConnectAsync(TimeSpan.FromSeconds(10));
            var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            peers[0].BeforeNextSend = async () => { held.SetResult(true); await release.Task; throw new WebSocketException("delayed old write failure"); };
            var oldSend = transport.EmitBusAsync("test.old", new JsonObject(), new JsonObject());
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await transport.DisconnectAsync();
            var reconnect = transport.ConnectAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, peers.Count);
            release.SetResult(true);
            await Assert.ThrowsAsync<WebSocketException>(() => oldSend);
            await reconnect;
            Assert.True(transport.Connected && transport.HandshakeComplete);
            Assert.Null(transport.LastError);
            await transport.EmitBusAsync("test.current", new JsonObject(), new JsonObject());
            Assert.Equal(new[] { "hello", "bus" }, peers[1].AuthenticatedTypes.ToArray());
        }

        [Theory][InlineData(false)][InlineData(true)]
        public async Task CallerCancellationPreservesOwnedChunkSequenceAndSharedSession(bool queuedCancellation)
        {
            PeerSocket? peer = null; var connections = 0;
            using var transport = new HiveMindWssTransport(Identity(), new MemoryStore(), () => { connections++; return peer = new PeerSocket(null, _ => { }); });
            await transport.ConnectAsync(TimeSpan.FromSeconds(10));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            peer!.BeforeNextSendWithToken = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
            using var cancellation = new CancellationTokenSource();
            var owner = transport.EmitBusAsync("test.owner", new JsonObject { ["large"] = new string('x', NoiseSession.Chunk * 3) }, new JsonObject(), queuedCancellation ? default : cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued = transport.EmitBusAsync("test.queued", new JsonObject(), new JsonObject(), queuedCancellation ? cancellation.Token : default);
            try {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => (queuedCancellation ? queued : owner).WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.True(transport.Connected && transport.HandshakeComplete); Assert.Equal(WebSocketState.Open, peer.State);
                Assert.False((queuedCancellation ? owner : queued).IsCompleted);
                Assert.Equal(new[] { "hello" }, peer.AuthenticatedTypes.ToArray());
            } finally { release.TrySetResult(); }
            await (queuedCancellation ? owner : queued).WaitAsync(TimeSpan.FromSeconds(10));
            await transport.EmitBusAsync("test.reused", new JsonObject(), new JsonObject());
            Assert.Equal(queuedCancellation ? 3 : 4, peer.AuthenticatedTypes.Count);
            Assert.Equal(1, connections); Assert.True(transport.Connected && transport.HandshakeComplete);
        }

        [Theory][InlineData(false)][InlineData(true)]
        public async Task PhysicalDeadlineRetainsWriteOwnershipAndCannotPoisonReplacement(bool abortThrows)
        {
            var peers = new List<PeerSocket>();
            using var transport = new HiveMindWssTransport(Identity(), new MemoryStore(), () => {
                var peer = new PeerSocket(null, _ => { }); peers.Add(peer); return peer;
            }, physicalSendTimeout: TimeSpan.FromSeconds(2));
            await transport.ConnectAsync(TimeSpan.FromSeconds(10));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            peers[0].ThrowOnAbort = abortThrows;
            peers[0].BeforeNextSendWithToken = async _ => { entered.SetResult(); await release.Task; };
            var owner = transport.EmitBusAsync("test.owner", new JsonObject(), new JsonObject());
            try {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var queued = transport.EmitBusAsync("test.stale", new JsonObject(), new JsonObject());
                await Assert.ThrowsAsync<ThalovantTimeoutException>(() => owner.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.False(transport.Connected); Assert.Equal(WebSocketState.Aborted, peers[0].State);
                Assert.False(queued.IsCompleted); // The actual old write still owns the lock.
                peers[0].ThrowOnAbort = false; // Later explicit Dispose uses the ordinary fixture teardown.
                var reconnect = transport.ConnectAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(2, peers.Count); Assert.False(reconnect.IsCompleted);
                release.TrySetResult();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(5)));
                await reconnect;
                await transport.EmitBusAsync("test.current", new JsonObject(), new JsonObject());
                Assert.True(transport.Connected && transport.HandshakeComplete); Assert.Null(transport.LastError);
                Assert.Equal(new[] { "hello", "bus" }, peers[1].AuthenticatedTypes.ToArray());
            } finally {
                // Never let intentionally failing Abort teardown mask an earlier
                // assertion or leave the synthetic physical write suspended.
                peers[0].ThrowOnAbort = false;
                release.TrySetResult();
            }
        }

        [Fact] public async Task BusCallbackCanSynchronouslySendAnImmediateResponse()
        {
            PeerSocket? peer = null;
            using var transport = new HiveMindWssTransport(Identity(), new MemoryStore(), () => peer = new PeerSocket(null, _ => { }));
            await transport.ConnectAsync(TimeSpan.FromSeconds(10));
            var replied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.AddBusHandler(message => {
                if (message["type"]!.GetValue<string>() != "test.trigger") return;
                try {
                    var send = transport.EmitBusAsync("test.immediate-response", new JsonObject(), new JsonObject());
                    // Bound the synchronous wait so a regressed callback lock
                    // fails the test instead of hanging the test process.
                    Assert.True(send.Wait(TimeSpan.FromSeconds(2)), "callback blocked its own send continuation");
                    send.GetAwaiter().GetResult();
                    replied.SetResult(true);
                } catch (Exception error) { replied.SetException(error); }
            });
            peer!.BeforeNextSend = () => Task.Delay(10); // Force an asynchronous send continuation.
            peer.QueueBus("test.trigger");
            await replied.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(transport.Connected && transport.HandshakeComplete);
            Assert.Equal(new[] { "hello", "bus" }, peer.AuthenticatedTypes.ToArray());
        }

        private sealed class MemoryStore : IHiveMindNoiseStore
        {
            private readonly byte[] _key = Noise.RandomKey(); private readonly Dictionary<string, byte[]> _pins = new Dictionary<string, byte[]>();
            public byte[] LoadOrCreateStaticKey() => (byte[])_key.Clone();
            public byte[]? LoadPin(string id) => _pins.TryGetValue(id, out var pin) ? pin : null;
            public void VerifyOrPin(string id, byte[] key) { if (_pins.TryGetValue(id, out var pin) && !Noise.Equal(pin, key)) throw new CryptographicException(); _pins[id] = key; }
        }
        private sealed class PeerSocket : WebSocket
        {
            private static readonly byte[] ServerKey = Enumerable.Repeat((byte)42, 32).ToArray();
            private static readonly Lazy<byte[]> Psk = new Lazy<byte[]>(() => Noise.DerivePsk("test-password", "test-hub"));
            private readonly Channel<(byte[] Data, WebSocketMessageType Type)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
            private readonly byte[]? _pinnedClient; private readonly Action<byte[]> _pinClient; private readonly string _mode;
            private readonly JsonObject _hello = new JsonObject { ["node_id"] = "test-hub", ["pubkey"] = "", ["peer"] = "test" };
            private readonly JsonObject _offer = JsonNode.Parse("""{"max_protocol_version":3,"binarize":true,"encodings":["JSON-HEX"],"ciphers":["AES-GCM"],"noise":{"patterns":["XXpsk2","KKpsk0"],"suites":["25519_ChaChaPoly_SHA256","25519_AESGCM_SHA256"]}}""")!.AsObject();
            private NoiseHandshake? _exchange; private NoiseSession? _session; private WebSocketState _state = WebSocketState.Open;
            private (byte[] Data, WebSocketMessageType Type)? _reading; private int _offset;
            public ConcurrentQueue<string> AuthenticatedTypes { get; } = new ConcurrentQueue<string>();
            public string? Pattern { get; private set; }
            public Func<Task>? BeforeNextReceive { get; set; }
            public Func<Task>? BeforeNextSend { get; set; }
            public Func<CancellationToken, Task>? BeforeNextSendWithToken { get; set; }
            public bool ThrowOnAbort { get; set; }
            public TaskCompletionSource<bool> HeldReceiveCompleted { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public void QueueBus(string type) {
                foreach (var frame in _session!.Encrypt(Encoding.UTF8.GetBytes(HiveWire.Encode(HiveWire.BusMessage(type, new JsonObject(), new JsonObject())))))
                    _incoming.Writer.TryWrite((frame, WebSocketMessageType.Binary));
            }
            public PeerSocket(byte[]? pin, Action<byte[]> pinClient, string mode = "") {
                _pinnedClient = pin; _pinClient = pinClient; _mode = mode;
                if (pin == null) _offer["noise"]!["patterns"] = new JsonArray("XXpsk2");
                if (mode == "held") return;
                QueueText(HiveWire.Encode(new HiveMessage("hello", _hello)));
                if (mode == "legacy") QueueText("{\"msg_type\":\"shake\",\"payload\":{\"preshared_key\":true}}");
                else if (mode == "plaintext") QueueText("{\"msg_type\":\"bus\",\"payload\":{\"type\":\"speak\"}}");
                else QueueText(HiveWire.Encode(new HiveMessage("shake", _offer)));
            }
            public void ReleaseHandshake() {
                QueueText(HiveWire.Encode(new HiveMessage("hello", _hello)));
                QueueText(HiveWire.Encode(new HiveMessage("shake", _offer)));
            }
            public void QueueText(string text) => _incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text));
            public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
            {
                var held = BeforeNextSend; BeforeNextSend = null;
                if (held != null) await held();
                var heldWithToken = BeforeNextSendWithToken; BeforeNextSendWithToken = null;
                if (heldWithToken != null) await heldWithToken(token);
                token.ThrowIfCancellationRequested();
                var bytes = buffer.ToArray();
                if (type == WebSocketMessageType.Text) {
                    var envelope = HiveWire.Decode(Encoding.UTF8.GetString(bytes)).Payload["noise"]!.AsObject();
                    if (_exchange == null) {
                        Pattern = envelope["pattern"]!.GetValue<string>(); Assert.Equal(Noise.Suite, envelope["suite"]!.GetValue<string>());
                        _exchange = new NoiseHandshake(Pattern, _mode == "wrong-password" ? new byte[32] : Psk.Value,
                            Noise.Prologue(_hello, _offer, "Noise_" + Pattern + "_" + Noise.Suite), ServerKey, _pinnedClient, false);
                    }
                    _exchange.Read(Noise.Unhex(envelope["msg"]!.GetValue<string>()));
                    if (!_exchange.Finished) QueueText(HiveWire.Encode(new HiveMessage("shake", new JsonObject { ["noise"] = new JsonObject { ["msg"] = Noise.Hex(_exchange.Write()) } })));
                    if (_exchange.Finished) { _session = _exchange.Session(); _pinClient(_exchange.RemoteStatic!); }
                } else {
                    var plain = _session!.Decrypt(bytes);
                    if (!plain.HasValue) return; // A chunked message is delivered only after its final frame.
                    Assert.True(plain.Value.Json);
                    var message = HiveWire.Decode(Encoding.UTF8.GetString(plain.Value.Data)); AuthenticatedTypes.Enqueue(message.MsgType);
                    if (message.MsgType == "bus") {
                        var context = message.Payload["context"]!.AsObject();
                        foreach (var response in new[] { HiveWire.BusMessage("speak", new JsonObject { ["utterance"] = "test answer" }, context), HiveWire.BusMessage("ovos.utterance.handled", new JsonObject(), context) })
                            foreach (var frame in _session.Encrypt(Encoding.UTF8.GetBytes(HiveWire.Encode(response)))) _incoming.Writer.TryWrite((frame, WebSocketMessageType.Binary));
                    }
                }
            }
            public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) {
                if (!_reading.HasValue) { _reading = await _incoming.Reader.ReadAsync(token); _offset = 0; }
                var held = BeforeNextReceive; BeforeNextReceive = null;
                if (held != null) { await held(); HeldReceiveCompleted.TrySetResult(true); }
                var value = _reading.Value; var length = Math.Min(buffer.Count, value.Data.Length - _offset);
                Array.Copy(value.Data, _offset, buffer.Array!, buffer.Offset, length); _offset += length;
                var end = _offset == value.Data.Length; if (end) _reading = null;
                return new WebSocketReceiveResult(length, value.Type, end);
            }
            public override void Abort() { _state = WebSocketState.Aborted; _incoming.Writer.TryComplete(); if (ThrowOnAbort) throw new InvalidOperationException("fixture teardown failed"); }
            public override void Dispose() => Abort();
            public override WebSocketCloseStatus? CloseStatus => null;
            public override string? CloseStatusDescription => null;
            public override string? SubProtocol => null;
            public override WebSocketState State => _state;
            public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) { Abort(); return Task.CompletedTask; }
            public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => CloseAsync(status, description, token);
        }
    }
}
