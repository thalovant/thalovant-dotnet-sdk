using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Thalovant.Sdk.Tests;

/// <summary>
/// A HiveMind hub on a loopback port: a real WebSocket upgrade, then the real
/// Noise handshake (XXpsk2, or KKpsk0 for a client it has met), so what a client
/// meets is what <see cref="ClientWebSocket"/> and
/// <see cref="HiveMindWssTransport"/> meet against a hub.
/// </summary>
/// <remarks>
/// It changes the way a hub does: its password (the PSK it derives), its static
/// key, whether it offers KK, what it answers the upgrade with, and how it
/// closes right after a handshake. It records the pattern every client chose,
/// in order, which is what the <c>handshake</c> vectors compare. A handshake
/// message it cannot read -- a KK first message under another PSK or another
/// hub key -- is answered the way hivemind-core answers it: a close with no
/// status.
/// </remarks>
internal sealed class LoopbackHub : IDisposable
{
    internal const string NodeId = "test-hub";

    private static readonly Dictionary<string, byte[]> Psks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private readonly object _gate = new object();
    private readonly List<string> _patterns = new List<string>();
    private int _attempts;

    internal int Port { get; }

    /// <summary>The password the hub derives its PSK from; a changed one refuses the old.</summary>
    internal string Password { get; set; } = "test-password";

    /// <summary>The hub's Noise static key; a new one is a hub replaced.</summary>
    internal byte[] StaticKey { get; set; } = Enumerable.Repeat((byte)42, 32).ToArray();

    /// <summary>Whether the offer lists KKpsk0 beside XXpsk2.</summary>
    internal bool OfferKK { get; set; } = true;

    /// <summary>When set, the upgrade is answered with this status instead of 101.</summary>
    internal int? UpgradeStatus { get; set; }

    /// <summary>When set, the hub closes the moment a handshake completes, with <see cref="CloseAfterHandshakeWith"/>.</summary>
    internal bool CloseAfterHandshake { get; set; }

    /// <summary>The close status to use; null closes with no status at all.</summary>
    internal WebSocketCloseStatus? CloseAfterHandshakeWith { get; set; }

    /// <summary>The client's static key, learnt from its first XX: what KK needs.</summary>
    private byte[]? _clientKey;

    internal LoopbackHub()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    /// <summary>Every pattern a client chose, in order.</summary>
    internal IReadOnlyList<string> Patterns
    {
        get
        {
            lock (_gate) return _patterns.ToArray();
        }
    }

    /// <summary>How many upgrades clients asked for.</summary>
    internal int Attempts => Volatile.Read(ref _attempts);

    /// <summary>An identity for this hub, with <paramref name="password"/> when given.</summary>
    internal ThalovantIdentity Identity(string password = "test-password") => ThalovantIdentity.FromJson(
        $$"""{"access_key":"test-access","password":"{{password}}","site_id":"test-site","default_master":"ws://127.0.0.1:{{Port.ToString(CultureInfo.InvariantCulture)}}"}""");

    /// <summary>A client of this hub over a real <see cref="ClientWebSocket"/>, keeping its Noise state in <paramref name="store"/>.</summary>
    internal ThalovantClient Client(IHiveMindNoiseStore store, string password = "test-password")
    {
        var identity = Identity(password);
        var transport = new HiveMindWssTransport(identity, store, () => new ClientWebSocket());
        return new ThalovantClient(identity, transport, replySettle: TimeSpan.Zero);
    }

    /// <summary>One connect, and what it came to: null, or the connection error it threw.</summary>
    internal async Task<ThalovantConnectionException?> AttemptAsync(IHiveMindNoiseStore store, string password = "test-password")
    {
        using var client = Client(store, password);
        try
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(15));
            return null;
        }
        catch (ThalovantConnectionException error)
        {
            return error;
        }
        finally
        {
            await client.CloseAsync();
        }
    }

    private static byte[] Psk(string password)
    {
        lock (Psks)
        {
            if (!Psks.TryGetValue(password, out var psk))
            {
                Psks[password] = psk = Noise.DerivePsk(password, NodeId);
            }
            return psk;
        }
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var headers = await ReadUpgradeAsync(stream);
                Interlocked.Increment(ref _attempts);
                if (UpgradeStatus is int status)
                {
                    await WriteAsync(stream, $"HTTP/1.1 {status.ToString(CultureInfo.InvariantCulture)} Refused\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(headers["sec-websocket-key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await WriteAsync(stream, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                    + $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
                using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
                await new Connection(this, socket, stream).RunAsync(_stop.Token);
            }
            catch (Exception)
            {
                // The client went away, or the hub is stopping.
            }
        }
    }

    private async Task<Dictionary<string, string>> ReadUpgradeAsync(Stream stream)
    {
        var head = new List<byte>();
        var one = new byte[1];
        while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0) throw new EndOfStreamException();
            head.Add(one[0]);
        }
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Encoding.ASCII.GetString(head.ToArray()).Split("\r\n").Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':');
            headers[line.Substring(0, colon).Trim().ToLowerInvariant()] = line.Substring(colon + 1).Trim();
        }
        return headers;
    }

    private async Task WriteAsync(Stream stream, string text)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _stop.Token);
        await stream.FlushAsync(_stop.Token);
    }

    /// <summary>One client's connection: the hub's side of HELLO, the offer and the Noise handshake.</summary>
    private sealed class Connection
    {
        private readonly LoopbackHub _hub;
        private readonly WebSocket _socket;
        private readonly Stream _stream;
        private readonly JsonObject _hello = new JsonObject { ["node_id"] = NodeId, ["pubkey"] = "", ["peer"] = "test" };
        private readonly JsonObject _offer;
        private NoiseHandshake? _exchange;
        private NoiseSession? _session;

        internal Connection(LoopbackHub hub, WebSocket socket, Stream stream)
        {
            _hub = hub;
            _socket = socket;
            _stream = stream;
            var patterns = hub.OfferKK ? new JsonArray("XXpsk2", "KKpsk0") : new JsonArray("XXpsk2");
            _offer = new JsonObject
            {
                ["max_protocol_version"] = 3,
                ["binarize"] = true,
                ["encodings"] = new JsonArray("JSON-HEX"),
                ["ciphers"] = new JsonArray("AES-GCM"),
                ["noise"] = new JsonObject { ["patterns"] = patterns, ["suites"] = new JsonArray("25519_ChaChaPoly_SHA256", "25519_AESGCM_SHA256") },
            };
        }

        internal async Task RunAsync(CancellationToken stop)
        {
            await SendTextAsync(HiveWire.Encode(new HiveMessage("hello", _hello)), stop);
            await SendTextAsync(HiveWire.Encode(new HiveMessage("shake", _offer)), stop);
            var buffer = new byte[65536];
            while (_socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, stop);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                var bytes = message.ToArray();
                bool closed;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    closed = await OnShakeAsync(Encoding.UTF8.GetString(bytes), stop);
                }
                else
                {
                    closed = await OnFrameAsync(bytes, stop);
                }
                if (closed) return;
            }
        }

        /// <summary>The client's Noise message. True when the hub closed the connection over it.</summary>
        private async Task<bool> OnShakeAsync(string text, CancellationToken stop)
        {
            var noise = HiveWire.Decode(text).Payload["noise"]!.AsObject();
            try
            {
                if (_exchange is null)
                {
                    var pattern = (string)noise["pattern"]!;
                    lock (_hub._gate) _hub._patterns.Add(pattern);
                    var prologue = Noise.Prologue(_hello, _offer, "Noise_" + pattern + "_" + Noise.Suite);
                    var remote = pattern == "KKpsk0" ? _hub._clientKey ?? throw new CryptographicException("KK from a client this hub has not met") : null;
                    _exchange = new NoiseHandshake(pattern, Psk(_hub.Password), prologue, _hub.StaticKey, remote, false);
                }
                _exchange.Read(Noise.Unhex((string)noise["msg"]!));
            }
            catch (Exception)
            {
                // What hivemind-core does with a handshake it cannot read.
                await CloseAsync(null, stop);
                return true;
            }
            if (!_exchange.Finished)
            {
                var answer = new JsonObject { ["noise"] = new JsonObject { ["msg"] = Noise.Hex(_exchange.Write()) } };
                await SendTextAsync(HiveWire.Encode(new HiveMessage("shake", answer)), stop);
            }
            if (_exchange.Finished)
            {
                _session = _exchange.Session();
                _hub._clientKey = _exchange.RemoteStatic;
            }
            return false;
        }

        /// <summary>An encrypted frame. True when the hub closed the connection after it.</summary>
        private async Task<bool> OnFrameAsync(byte[] frame, CancellationToken stop)
        {
            var plain = _session!.Decrypt(frame);
            if (!plain.HasValue) return false;
            var message = HiveWire.Decode(Encoding.UTF8.GetString(plain.Value.Data));
            if (message.MsgType == "hello" && _hub.CloseAfterHandshake)
            {
                await CloseAsync(_hub.CloseAfterHandshakeWith, stop);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Closes with <paramref name="status"/>, or with no status at all: a close
        /// frame with no payload, which RFC 6455 allows and hivemind-core sends.
        /// .NET's own server socket cannot send one -- given Empty it writes 1005
        /// on the wire, which a client must reject -- so that frame is written by
        /// hand (FIN, opcode 8, no payload).
        /// </summary>
        private async Task CloseAsync(WebSocketCloseStatus? status, CancellationToken stop)
        {
            if (status is null || status == WebSocketCloseStatus.Empty)
            {
                await _stream.WriteAsync(new byte[] { 0x88, 0x00 }, stop);
                await _stream.FlushAsync(stop);
                return;
            }
            await _socket.CloseOutputAsync(status.Value, null, stop);
        }

        private Task SendTextAsync(string text, CancellationToken stop) =>
            _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, stop);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
