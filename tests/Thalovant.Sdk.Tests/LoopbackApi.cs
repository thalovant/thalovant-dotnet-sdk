using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Thalovant.Sdk.Tests;

/// <summary>
/// A control-plane API on a loopback port that serves a case's exchanges in
/// order and checks every request against the one the case names: method,
/// path, body or body subset, <c>If-Match</c> and <c>Authorization</c>.
/// </summary>
/// <remarks>
/// Plain HTTP/1.1 over a <see cref="TcpListener"/>, one request per connection,
/// so the SDK's own <see cref="System.Net.Http.HttpClient"/> path is what runs
/// -- DNS, the socket, the headers and all -- the same on every operating
/// system, without the URL reservations an HttpListener needs on Windows.
/// </remarks>
internal sealed class LoopbackApi : IDisposable
{
    private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly JsonArray _exchanges;
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private readonly object _gate = new object();
    private int _index;

    internal List<string> Sent { get; } = new List<string>();
    internal List<string> Mismatches { get; } = new List<string>();
    internal int Port { get; }
    internal string Url => $"http://127.0.0.1:{Port}";

    internal bool AllUsed
    {
        get
        {
            lock (_gate) return _index == _exchanges.Count;
        }
    }

    internal LoopbackApi(JsonArray exchanges)
    {
        _exchanges = exchanges;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    /// <summary>A port nothing listens on: bound, read back, and let go.</summary>
    internal static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// An API that cannot be reached, and whatever keeps it so. On Linux and
    /// macOS that is a port nothing listens on, which refuses a connect at once.
    /// Windows retries a refused loopback connect for about two seconds -- the
    /// whole budget of the unreachable case -- so there a listener that resets
    /// every connection it accepts stands in: no answer from the API either way,
    /// and the SDK's deadline is not what the case ends up measuring.
    /// </summary>
    internal static (string Url, IDisposable? Owner) Unreachable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ($"http://127.0.0.1:{ClosedPort()}", null);
        }
        var resetting = new Resetting();
        return ($"http://127.0.0.1:{resetting.Port}", resetting);
    }

    /// <summary>Accepts every connection and resets it at once (a zero linger), before any answer.</summary>
    private sealed class Resetting : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        internal int Port { get; }

        internal Resetting()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                        client.Client.LingerState = new LingerOption(true, 0);
                        client.Close();
                    }
                    catch (Exception)
                    {
                        return;
                    }
                }
            });
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
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
                var (method, path, headers, body) = await ReadRequestAsync(stream);
                var response = Answer(method, path, headers, body);
                await stream.WriteAsync(response, _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch (Exception)
            {
                // A client that went away, or the server stopping: nothing to answer.
            }
        }
    }

    private byte[] Answer(string method, string path, Dictionary<string, string> headers, string body)
    {
        JsonObject? exchange;
        lock (_gate)
        {
            headers.TryGetValue("if-match", out var ifMatch);
            Sent.Add($"{method} {path}" + (ifMatch is null ? "" : $" If-Match={ifMatch}"));
            if (_index >= _exchanges.Count)
            {
                Mismatches.Add($"unexpected {method} {path}");
                exchange = null;
            }
            else
            {
                exchange = _exchanges[_index]!.AsObject();
                if (exchange["repeat"]?.GetValue<bool>() != true) _index++;
                Check(exchange["request"]!.AsObject(), method, path, headers, body, ifMatch);
            }
        }
        if (exchange is null)
        {
            return Response(599, "application/json", "{}", null);
        }
        var answer = exchange["response"]!.AsObject();
        return Response(
            answer["status"]!.GetValue<int>(),
            (string)answer["content_type"]!,
            (string)answer["body"]!,
            answer["headers"] as JsonObject);
    }

    private void Check(JsonObject expected, string method, string path, Dictionary<string, string> headers, string raw, string? ifMatch)
    {
        if (method != (string)expected["method"]! || path != (string)expected["path"]!)
            Mismatches.Add($"{method} {path} != {expected["method"]} {expected["path"]}");
        var body = raw.Length == 0 ? null : JsonNode.Parse(raw);
        if (expected.TryGetPropertyValue("json", out var json) && !JsonNode.DeepEquals(body, json))
            Mismatches.Add($"body {body?.ToJsonString()} != {json?.ToJsonString()}");
        if (expected.TryGetPropertyValue("json_subset", out var subset) && !Contains(body, subset))
            Mismatches.Add($"body lacks {subset?.ToJsonString()}");
        if (expected.TryGetPropertyValue("if_match", out var match) && ifMatch != (string?)match)
            Mismatches.Add($"If-Match {ifMatch} != {match}");
        if (expected.TryGetPropertyValue("authorization", out var authorization)
            && (headers.TryGetValue("authorization", out var sent) ? sent : null) != (string?)authorization)
            Mismatches.Add("wrong Authorization header");
    }

    private static bool Contains(JsonNode? value, JsonNode? subset)
    {
        if (subset is JsonObject wanted)
        {
            return value is JsonObject actual && wanted.All(pair => actual.ContainsKey(pair.Key) && Contains(actual[pair.Key], pair.Value));
        }
        return JsonNode.DeepEquals(value, subset);
    }

    private static byte[] Response(int status, string contentType, string body, JsonObject? extra)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(" Scripted\r\n");
        if (bytes.Length > 0) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        foreach (var pair in extra ?? new JsonObject())
        {
            head.Append(pair.Key).Append(": ").Append((string)pair.Value!).Append("\r\n");
        }
        head.Append("Content-Length: ").Append(bytes.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(head.ToString()).Concat(bytes).ToArray();
    }

    private async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)> ReadRequestAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(buffer))
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0) throw new EndOfStreamException();
            buffer.Add(one[0]);
            if (buffer.Count > 65536) throw new InvalidDataException("request head too large");
        }
        var lines = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n");
        var start = lines[0].Split(' ');
        var target = start[1];
        var query = target.IndexOf('?');
        var path = query >= 0 ? target.Substring(0, query) : target;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':');
            headers[line.Substring(0, colon).Trim().ToLowerInvariant()] = line.Substring(colon + 1).Trim();
        }
        var length = headers.TryGetValue("content-length", out var declared) ? int.Parse(declared, CultureInfo.InvariantCulture) : 0;
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var got = await stream.ReadAsync(body.AsMemory(read, length - read), _stop.Token);
            if (got == 0) throw new EndOfStreamException();
            read += got;
        }
        return (start[0], path, headers, Encoding.UTF8.GetString(body));
    }

    private static bool EndsWithBlankLine(List<byte> buffer) =>
        buffer.Count >= 4 && buffer[^4] == '\r' && buffer[^3] == '\n' && buffer[^2] == '\r' && buffer[^1] == '\n';

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
