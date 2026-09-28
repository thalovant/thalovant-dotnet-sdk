using System;
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

namespace Thalovant.Sdk.Tests;

/// <summary>
/// A hub on the other end of an in-memory WebSocket: it speaks the real
/// HiveMind v3 Noise handshake (XXpsk2, AESGCM/SHA256), so what reaches it went
/// through <see cref="HiveMindWssTransport"/> exactly as it would to a hub, and
/// it can refuse, close and drop the way a hub does.
/// </summary>
internal sealed class HubPeer : WebSocket
{
    internal enum Opening
    {
        /// <summary>HELLO, then the Noise offer: a hub that admits the connection.</summary>
        Admit,

        /// <summary>Closes with no status before its HELLO: a hub that does not know the access key.</summary>
        RefuseBeforeHello,

        /// <summary>Sends HELLO, then closes while the client waits for the offer: says nothing about the credentials.</summary>
        CloseAfterHello,
    }

    internal static ThalovantIdentity Identity() => ThalovantIdentity.FromJson("""
        {"access_key":"test-access","password":"test-password","site_id":"test-site","default_master":"ws://test.invalid"}
        """);

    private static readonly byte[] ServerKey = Enumerable.Repeat((byte)42, 32).ToArray();
    private static readonly Lazy<byte[]> Psk = new Lazy<byte[]>(() => Noise.DerivePsk("test-password", "test-hub"));

    private readonly record struct Frame(byte[] Data, WebSocketMessageType Type, WebSocketCloseStatus? Status);

    private readonly Channel<Frame> _incoming = Channel.CreateUnbounded<Frame>();
    private readonly JsonObject _hello = new JsonObject { ["node_id"] = "test-hub", ["pubkey"] = "", ["peer"] = "test" };
    private readonly JsonObject _offer = JsonNode.Parse("""{"max_protocol_version":3,"binarize":true,"encodings":["JSON-HEX"],"ciphers":["AES-GCM"],"noise":{"patterns":["XXpsk2"],"suites":["25519_ChaChaPoly_SHA256","25519_AESGCM_SHA256"]}}""")!.AsObject();
    private readonly object _gate = new object();
    private NoiseHandshake? _exchange;
    private NoiseSession? _session;
    private WebSocketState _state = WebSocketState.Open;
    private WebSocketCloseStatus? _closeStatus;
    private Frame? _reading;
    private int _offset;

    /// <summary>Every bus message the client sent after authenticating, in order.</summary>
    internal Channel<JsonObject> Received { get; } = Channel.CreateUnbounded<JsonObject>();

    /// <summary>Completes when the client's encrypted HELLO arrives: the handshake is done from both ends.</summary>
    internal TaskCompletionSource Authenticated { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When set, the hub closes with this status the moment the handshake completes.</summary>
    internal bool CloseAfterHandshake { get; init; }

    /// <summary>
    /// When set, the next encrypted frame the client writes waits on this before
    /// it lands: a frame still being written, which is what makes the next one
    /// queue behind it.
    /// </summary>
    internal Func<Task>? HoldNextFrame { get; set; }

    internal WebSocketCloseStatus? CloseAfterHandshakeWith { get; init; }

    internal HubPeer(Opening opening = Opening.Admit)
    {
        switch (opening)
        {
            case Opening.RefuseBeforeHello:
                Close(null);
                break;
            case Opening.CloseAfterHello:
                QueueText(HiveWire.Encode(new HiveMessage("hello", _hello)));
                Close(null);
                break;
            default:
                QueueText(HiveWire.Encode(new HiveMessage("hello", _hello)));
                QueueText(HiveWire.Encode(new HiveMessage("shake", _offer)));
                break;
        }
    }

    /// <summary>Sends a bus message down to the client, encrypted.</summary>
    internal void SendBus(string type, JsonObject data, JsonObject context)
    {
        lock (_gate)
        {
            foreach (var frame in _session!.Encrypt(Encoding.UTF8.GetBytes(HiveWire.Encode(HiveWire.BusMessage(type, data, context)))))
            {
                _incoming.Writer.TryWrite(new Frame(frame, WebSocketMessageType.Binary, null));
            }
        }
    }

    /// <summary>Closes the way a hub does: a close frame carrying <paramref name="status"/> (null: none).</summary>
    internal void Close(WebSocketCloseStatus? status) =>
        _incoming.Writer.TryWrite(new Frame(Array.Empty<byte>(), WebSocketMessageType.Close, status));

    /// <summary>Drops the connection with no close at all, the way a network does.</summary>
    internal void Drop() => _incoming.Writer.TryComplete();

    /// <summary>The next bus message the client sent, within <paramref name="timeout"/>.</summary>
    internal async Task<JsonObject> NextAsync(TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        return await Received.Reader.ReadAsync(deadline.Token);
    }

    private void QueueText(string text) =>
        _incoming.Writer.TryWrite(new Frame(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, null));

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = buffer.ToArray();
        if (type == WebSocketMessageType.Binary && HoldNextFrame is Func<Task> hold)
        {
            HoldNextFrame = null;
            await hold();
        }
        Receive(bytes, type);
    }

    private void Receive(byte[] bytes, WebSocketMessageType type)
    {
        lock (_gate)
        {
            if (type == WebSocketMessageType.Text)
            {
                var envelope = HiveWire.Decode(Encoding.UTF8.GetString(bytes)).Payload["noise"]!.AsObject();
                if (_exchange == null)
                {
                    var pattern = envelope["pattern"]!.GetValue<string>();
                    Assert.Equal(Noise.Suite, envelope["suite"]!.GetValue<string>());
                    _exchange = new NoiseHandshake(pattern, Psk.Value,
                        Noise.Prologue(_hello, _offer, "Noise_" + pattern + "_" + Noise.Suite), ServerKey, null, false);
                }
                _exchange.Read(Noise.Unhex(envelope["msg"]!.GetValue<string>()));
                if (!_exchange.Finished)
                {
                    QueueText(HiveWire.Encode(new HiveMessage("shake", new JsonObject { ["noise"] = new JsonObject { ["msg"] = Noise.Hex(_exchange.Write()) } })));
                }
                if (_exchange.Finished)
                {
                    _session = _exchange.Session();
                }
                return;
            }
            var plain = _session!.Decrypt(bytes);
            if (!plain.HasValue)
            {
                return; // A chunked message is delivered only after its final frame.
            }
            var message = HiveWire.Decode(Encoding.UTF8.GetString(plain.Value.Data));
            if (message.MsgType == "hello")
            {
                Authenticated.TrySetResult();
                if (CloseAfterHandshake)
                {
                    Close(CloseAfterHandshakeWith);
                }
            }
            else if (message.MsgType == "bus")
            {
                Received.Writer.TryWrite(message.Payload);
            }
        }
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
    {
        if (!_reading.HasValue)
        {
            _reading = await _incoming.Reader.ReadAsync(token);
            _offset = 0;
        }
        var frame = _reading.Value;
        if (frame.Type == WebSocketMessageType.Close)
        {
            _reading = null;
            _state = WebSocketState.CloseReceived;
            _closeStatus = frame.Status;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, frame.Status, null);
        }
        var length = Math.Min(buffer.Count, frame.Data.Length - _offset);
        Array.Copy(frame.Data, _offset, buffer.Array!, buffer.Offset, length);
        _offset += length;
        var end = _offset == frame.Data.Length;
        if (end)
        {
            _reading = null;
        }
        return new WebSocketReceiveResult(length, frame.Type, end);
    }

    public override void Abort()
    {
        _state = WebSocketState.Aborted;
        _incoming.Writer.TryComplete();
    }

    public override void Dispose() => Abort();

    public override WebSocketCloseStatus? CloseStatus => _closeStatus;

    public override string? CloseStatusDescription => null;

    public override string? SubProtocol => null;

    public override WebSocketState State => _state;

    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token)
    {
        Abort();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) =>
        CloseAsync(status, description, token);

    /// <summary>The client's Noise identity: a static key and the hub's pinned key, kept in memory.</summary>
    internal sealed class Store : IHiveMindNoiseStore
    {
        private byte[] _key = Noise.RandomKey();
        private readonly Dictionary<string, byte[]> _pins = new Dictionary<string, byte[]>();

        /// <summary>A new static key for this client, keeping the hub pins it has.</summary>
        internal void ReplaceKey() => _key = Noise.RandomKey();

        public byte[] LoadOrCreateStaticKey() => (byte[])_key.Clone();

        public byte[]? LoadPin(string id) => _pins.TryGetValue(id, out var pin) ? pin : null;

        public void VerifyOrPin(string id, byte[] key)
        {
            if (_pins.TryGetValue(id, out var pin) && !Noise.Equal(pin, key))
            {
                throw new CryptographicException();
            }
            _pins[id] = key;
        }
    }

    /// <summary>A client whose WSS transport dials <paramref name="peer"/>.</summary>
    internal static ThalovantClient ClientFor(HubPeer peer, IHiveMindNoiseStore? store = null)
    {
        var transport = new HiveMindWssTransport(Identity(), store ?? new Store(), () => peer);
        return new ThalovantClient(Identity(), transport, replySettle: TimeSpan.Zero);
    }
}
