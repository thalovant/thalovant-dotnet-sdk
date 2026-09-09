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
            public PeerSocket(byte[]? pin, Action<byte[]> pinClient, string mode = "") {
                _pinnedClient = pin; _pinClient = pinClient; _mode = mode;
                if (pin == null) _offer["noise"]!["patterns"] = new JsonArray("XXpsk2");
                QueueText(HiveWire.Encode(new HiveMessage("hello", _hello)));
                if (mode == "legacy") QueueText("{\"msg_type\":\"shake\",\"payload\":{\"preshared_key\":true}}");
                else if (mode == "plaintext") QueueText("{\"msg_type\":\"bus\",\"payload\":{\"type\":\"speak\"}}");
                else QueueText(HiveWire.Encode(new HiveMessage("shake", _offer)));
            }
            public void QueueText(string text) => _incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text));
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
            {
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
                    var plain = _session!.Decrypt(bytes); Assert.True(plain!.Value.Json);
                    var message = HiveWire.Decode(Encoding.UTF8.GetString(plain.Value.Data)); AuthenticatedTypes.Enqueue(message.MsgType);
                    if (message.MsgType == "bus") {
                        var context = message.Payload["context"]!.AsObject();
                        foreach (var response in new[] { HiveWire.BusMessage("speak", new JsonObject { ["utterance"] = "test answer" }, context), HiveWire.BusMessage("ovos.utterance.handled", new JsonObject(), context) })
                            foreach (var frame in _session.Encrypt(Encoding.UTF8.GetBytes(HiveWire.Encode(response)))) _incoming.Writer.TryWrite((frame, WebSocketMessageType.Binary));
                    }
                }
                return Task.CompletedTask;
            }
            public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) {
                if (!_reading.HasValue) { _reading = await _incoming.Reader.ReadAsync(token); _offset = 0; }
                var value = _reading.Value; var length = Math.Min(buffer.Count, value.Data.Length - _offset);
                Array.Copy(value.Data, _offset, buffer.Array!, buffer.Offset, length); _offset += length;
                var end = _offset == value.Data.Length; if (end) _reading = null;
                return new WebSocketReceiveResult(length, value.Type, end);
            }
            public override void Abort() { _state = WebSocketState.Aborted; _incoming.Writer.TryComplete(); }
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
