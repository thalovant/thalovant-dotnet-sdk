using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Thalovant
{
    /// <summary>What an accepted callback says: a code, a refusal, or neither.</summary>
    internal enum LoopbackOutcome
    {
        /// <summary>The person approved: exactly one non-empty <c>code</c>.</summary>
        Code,

        /// <summary><c>error=access_denied</c>, exactly once.</summary>
        Denied,

        /// <summary>Any other <c>error</c>, or neither an error nor exactly one code.</summary>
        Failed,
    }

    /// <summary>
    /// A callback the loopback listener accepted: the browser came back with
    /// this sign-in's <c>state</c>.
    /// </summary>
    internal sealed class LoopbackCallback
    {
        internal LoopbackCallback(LoopbackOutcome outcome, string? code = null, string? error = null)
        {
            Outcome = outcome;
            Code = code;
            Error = error;
        }

        internal LoopbackOutcome Outcome { get; }

        /// <summary>The authorization code, when <see cref="Outcome"/> is <see cref="LoopbackOutcome.Code"/>.</summary>
        internal string? Code { get; }

        /// <summary>The <c>error</c> the consent page sent, when it sent a non-empty one.</summary>
        internal string? Error { get; }

        /// <summary>Never the code: it is a secret until it has been exchanged.</summary>
        public override string ToString() => $"LoopbackCallback({Outcome}, error={Error ?? "none"})";
    }

    /// <summary>
    /// The loopback listener a browser sign-in waits on (RFC 8252 section 7.3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Start"/> binds <c>127.0.0.1</c> on a port the system picks,
    /// before any URL is built, because the redirect names the port. Only
    /// <c>GET /callback</c> is served; anything else is a 404. A callback whose
    /// <c>state</c> is missing, repeated or not this sign-in's is a 400 and
    /// changes nothing: a stray tab -- an old attempt's, another program's, a
    /// page probing local ports -- must not end a real sign-in. The first
    /// callback with the right state is accepted, the port stops listening, and
    /// its tab is answered once the caller says how the sign-in ended
    /// (<see cref="Finish"/>).
    /// </para>
    /// <para>
    /// A plain <see cref="TcpListener"/> with a small reader rather than
    /// <c>HttpListener</c>, which cannot bind port 0 and needs a URL
    /// reservation on Windows. Nothing here writes to a log: a request line
    /// carries the code and the state.
    /// </para>
    /// <para>
    /// Everything that decides what a request means is in <see cref="Classify"/>,
    /// which touches no socket, so it can be checked against the shared vectors
    /// on its own.
    /// </para>
    /// </remarks>
    internal sealed class LoopbackListener
    {
        /// <summary>The one path served.</summary>
        internal const string CallbackPath = "/callback";

        /// <summary>What the tab shows when the sign-in worked.</summary>
        internal const string SignedInText = "Signed in to Thalovant. You can close this tab.";

        /// <summary>What the tab shows when it did not: denied, failed, refused, or timed out.</summary>
        internal const string NotCompletedText = "Sign-in was not completed.";

        /// <summary>What a tab whose answer belongs to another sign-in shows.</summary>
        internal const string WrongStateText = "This answer does not belong to the sign-in in progress.";

        /// <summary>What anything other than <c>GET /callback</c> shows.</summary>
        internal const string NotFoundText = "Not found.";

        /// <summary>
        /// Sent with every answer. The pages load nothing, and nothing they show
        /// may be cached or leak the callback's address (code and state) as a
        /// referrer.
        /// </summary>
        internal static readonly IReadOnlyList<KeyValuePair<string, string>> PageHeaders = new[]
        {
            new KeyValuePair<string, string>("Content-Type", "text/html; charset=utf-8"),
            new KeyValuePair<string, string>("Cache-Control", "no-store"),
            new KeyValuePair<string, string>("Referrer-Policy", "no-referrer"),
            new KeyValuePair<string, string>("Content-Security-Policy", "default-src 'none'"),
            new KeyValuePair<string, string>("X-Content-Type-Options", "nosniff"),
        };

        /// <summary>The largest request head read; a callback's is a few hundred bytes.</summary>
        internal const int MaxRequestHead = 16 * 1024;

        /// <summary>
        /// The most connections held open at once. A browser opens a handful; a
        /// local process opening many silent ones gets its oldest closed instead
        /// of crowding out the callback (<see cref="AcceptLoopAsync"/>).
        /// </summary>
        internal const int MaxConnections = 32;

        /// <summary>The first read buffer of a connection; it grows only as a head arrives.</summary>
        private const int FirstReadSize = 1024;

        /// <summary>How long one connection may take to send its request head.</summary>
        internal static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);

        /// <summary>How long an accepted tab waits for its page while the code is exchanged.</summary>
        internal static readonly TimeSpan DefaultAnswerTimeout = TimeSpan.FromSeconds(60);

        /// <summary>How long closing waits for an accepted tab's page to be written.</summary>
        internal static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(1);

        /// <summary>The longest single timer a wait arms; a longer wait arms several.</summary>
        private static readonly TimeSpan LongestTimer = TimeSpan.FromHours(1);

        private static readonly byte[] HeadEnd = { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };

        private readonly string _state;
        private readonly object _gate = new object();
        private readonly TaskCompletionSource<LoopbackCallback> _accepted =
            new TaskCompletionSource<LoopbackCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _answer =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _acceptedAnswered =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Open connections, oldest first.</summary>
        private readonly List<Connection> _connections = new List<Connection>();
        private readonly HashSet<Task> _handlers = new HashSet<Task>();
        private TcpListener? _listener;
        private Task? _acceptLoop;
        private bool _acceptedOne;
        private bool _stopped;
        private bool _closed;

        internal LoopbackListener(string state)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
        }

        /// <summary>How long an accepted tab waits for <see cref="Finish"/> before it is told the sign-in was not completed.</summary>
        internal TimeSpan AnswerTimeout { get; set; } = DefaultAnswerTimeout;

        /// <summary>How many connections are open now; never more than <see cref="MaxConnections"/>.</summary>
        internal int ConnectionCount
        {
            get
            {
                lock (_gate)
                {
                    return _connections.Count;
                }
            }
        }

        /// <summary>The bound address, once started: always <c>127.0.0.1</c>.</summary>
        internal IPEndPoint? LocalEndpoint { get; private set; }

        /// <summary>Whether no other socket may share the port (set on Windows, where that is not the default).</summary>
        internal bool ExclusiveAddressUse { get; private set; }

        /// <summary>
        /// Binds <c>127.0.0.1</c> on a port the system picks and starts
        /// accepting; returns the port. Never binds anything but the IPv4
        /// loopback address.
        /// </summary>
        internal int Start()
        {
            lock (_gate)
            {
                if (_listener != null || _closed)
                {
                    throw new InvalidOperationException("The loopback listener was already started.");
                }
                // IPAddress.Loopback is 127.0.0.1: never 0.0.0.0, ::1, or a
                // name such as localhost that a hosts file can send elsewhere.
                var listener = new TcpListener(IPAddress.Loopback, 0);
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        // Windows lets another socket bind the same port with
                        // SO_REUSEADDR unless this one asks for it exclusively.
                        listener.ExclusiveAddressUse = true;
                        ExclusiveAddressUse = true;
                    }
                    listener.Start(8);
                    LocalEndpoint = (IPEndPoint)listener.LocalEndpoint;
                }
                catch
                {
                    listener.Stop();
                    throw;
                }
                _listener = listener;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(listener));
                return LocalEndpoint.Port;
            }
        }

        /// <summary>
        /// The first accepted callback, or null once <paramref name="timeout"/>
        /// has passed. Measured on the monotonic clock and never cut short: a
        /// timer that wakes early waits again for what is left. A cancelled wait
        /// throws <see cref="OperationCanceledException"/>.
        /// </summary>
        internal async Task<LoopbackCallback?> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_listener is null)
                {
                    throw new InvalidOperationException("The loopback listener was not started.");
                }
            }
            var started = Stopwatch.GetTimestamp();
            var accepted = _accepted.Task;
            while (!accepted.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = timeout - Elapsed(started);
                if (remaining <= TimeSpan.Zero)
                {
                    return null;
                }
                using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var timer = Task.Delay(remaining < LongestTimer ? remaining : LongestTimer, stop.Token);
                    await Task.WhenAny(accepted, timer).ConfigureAwait(false);
                    stop.Cancel();
                }
            }
            return await accepted.ConfigureAwait(false);
        }

        /// <summary>Answers the accepted tab: signed in, or not completed. The first answer stands.</summary>
        internal void Finish(bool signedIn) => _answer.TrySetResult(signedIn);

        /// <summary>
        /// Stops listening, gives an accepted tab its page (waiting at most
        /// <see cref="CloseGrace"/>), and closes every other connection without
        /// waiting for it: a browser opens connections it never sends anything
        /// on, and the sign-in must not wait on them.
        /// </summary>
        internal async Task CloseAsync()
        {
            bool acceptedOne;
            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }
                _closed = true;
                acceptedOne = _acceptedOne;
            }
            Finish(false);
            StopListening();
            if (acceptedOne)
            {
                await AtMostAsync(_acceptedAnswered.Task, CloseGrace).ConfigureAwait(false);
            }
            TcpClient[] connections;
            Task[] handlers;
            lock (_gate)
            {
                connections = _connections.Select(connection => connection.Client).ToArray();
                handlers = _handlers.ToArray();
            }
            foreach (var connection in connections)
            {
                Close(connection);
            }
            if (handlers.Length > 0)
            {
                await AtMostAsync(Task.WhenAll(handlers), CloseGrace).ConfigureAwait(false);
            }
            if (_acceptLoop != null)
            {
                await AtMostAsync(_acceptLoop, CloseGrace).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// What the listener answers one request with, and the callback it
        /// accepts.
        /// </summary>
        /// <remarks>
        /// <c>(404, null)</c> for anything but <c>GET</c> on exactly
        /// <c>/callback</c> (another path, a trailing slash, another case,
        /// <c>POST</c>, <c>HEAD</c>); <c>(400, null)</c> when <c>state</c> is
        /// missing, given more than once, or not this sign-in's, compared in
        /// constant time as UTF-8 bytes; otherwise <c>(200, callback)</c>. An
        /// <c>error</c> wins over a <c>code</c> sent with it: a refusal that also
        /// carries a code is still a refusal. Query values are decoded once, the
        /// way a form decodes them (Python's <c>parse_qsl</c>).
        /// </remarks>
        internal static (int Status, LoopbackCallback? Callback) Classify(string method, string target, string state)
        {
            var mark = target.IndexOf('?');
            var path = mark < 0 ? target : target.Substring(0, mark);
            var query = mark < 0 ? "" : target.Substring(mark + 1);
            if (!string.Equals(method, "GET", StringComparison.Ordinal) || !string.Equals(path, CallbackPath, StringComparison.Ordinal))
            {
                return (404, null);
            }
            var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (name, value) in ParseQuery(query))
            {
                if (!values.TryGetValue(name, out var list))
                {
                    list = new List<string>();
                    values[name] = list;
                }
                list.Add(value);
            }
            if (!values.TryGetValue("state", out var given) || given.Count != 1 || !SameState(given[0], state))
            {
                return (400, null);
            }
            if (values.TryGetValue("error", out var errors) && errors.Count > 0)
            {
                if (errors.Count == 1 && errors[0] == "access_denied")
                {
                    return (200, new LoopbackCallback(LoopbackOutcome.Denied, error: "access_denied"));
                }
                return (200, new LoopbackCallback(LoopbackOutcome.Failed, error: errors[0].Length > 0 ? errors[0] : null));
            }
            if (values.TryGetValue("code", out var codes) && codes.Count == 1 && codes[0].Length > 0)
            {
                return (200, new LoopbackCallback(LoopbackOutcome.Code, code: codes[0]));
            }
            return (200, new LoopbackCallback(LoopbackOutcome.Failed));
        }

        /// <summary>
        /// A query string as Python's <c>parse_qsl(query, keep_blank_values=True)</c>
        /// reads it: pairs split on <c>&amp;</c> only, empty pairs skipped, a pair
        /// without <c>=</c> kept with an empty value, and every name and value
        /// form-decoded once (<see cref="FormDecode"/>).
        /// </summary>
        internal static List<(string Name, string Value)> ParseQuery(string query)
        {
            var pairs = new List<(string, string)>();
            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0)
                {
                    continue;
                }
                var split = pair.IndexOf('=');
                var name = split < 0 ? pair : pair.Substring(0, split);
                var value = split < 0 ? "" : pair.Substring(split + 1);
                pairs.Add((FormDecode(name), FormDecode(value)));
            }
            return pairs;
        }

        /// <summary>
        /// Decodes one query value as a form does: <c>+</c> is a space, then each
        /// <c>%XX</c> is a byte, and each run of ASCII is read as UTF-8 with
        /// U+FFFD for what is not. A <c>%</c> without two hex digits stays as it
        /// is, and a character outside ASCII (sent raw in the request line) stays
        /// as itself. Decoded once: <c>%2541</c> is <c>%41</c>.
        /// </summary>
        internal static string FormDecode(string text)
        {
            var value = text.Replace('+', ' ');
            if (value.IndexOf('%') < 0)
            {
                return value;
            }
            var output = new StringBuilder(value.Length);
            var bytes = new List<byte>();
            void Flush()
            {
                if (bytes.Count > 0)
                {
                    output.Append(Encoding.UTF8.GetString(bytes.ToArray()));
                    bytes.Clear();
                }
            }
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character > 0x7F)
                {
                    Flush();
                    output.Append(character);
                }
                else if (character == '%' && index + 2 < value.Length && IsHex(value[index + 1]) && IsHex(value[index + 2]))
                {
                    bytes.Add((byte)((HexValue(value[index + 1]) << 4) | HexValue(value[index + 2])));
                    index += 2;
                }
                else
                {
                    bytes.Add((byte)character);
                }
            }
            Flush();
            return output.ToString();
        }

        private static bool IsHex(char character) =>
            (character >= '0' && character <= '9') || (character >= 'A' && character <= 'F') || (character >= 'a' && character <= 'f');

        private static int HexValue(char character) =>
            character <= '9' ? character - '0' : (character | 0x20) - 'a' + 10;

        /// <summary>Constant time over the UTF-8 bytes, so a value that is not ASCII is a mismatch rather than a crash.</summary>
        private static bool SameState(string given, string state) =>
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(state));

        /// <summary>A page that loads nothing: no script, no style sheet, no image, no link.</summary>
        private static byte[] Page(string text) => Encoding.UTF8.GetBytes(
            "<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + "<title>Thalovant</title></head><body><p>" + text + "</p></body></html>\n");

        /// <summary>A whole HTTP/1.1 answer: the status, <see cref="PageHeaders"/>, and a small page.</summary>
        internal static byte[] ResponseBytes(int status, string text)
        {
            var body = Page(text);
            var reason = status == 200 ? "OK" : status == 400 ? "Bad Request" : status == 404 ? "Not Found" : "Error";
            var head = new StringBuilder();
            head.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
            foreach (var header in PageHeaders)
            {
                head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
            head.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");
            var start = Encoding.ASCII.GetBytes(head.ToString());
            var whole = new byte[start.Length + body.Length];
            Buffer.BlockCopy(start, 0, whole, 0, start.Length);
            Buffer.BlockCopy(body, 0, whole, start.Length, body.Length);
            return whole;
        }

        /// <summary>
        /// The method and target of a request head, read as Latin-1, or null
        /// when its first line is not <c>METHOD TARGET HTTP/x</c>.
        /// </summary>
        internal static (string Method, string Target)? RequestLine(byte[] head)
        {
            var end = 0;
            while (end + 1 < head.Length && !(head[end] == '\r' && head[end + 1] == '\n'))
            {
                end++;
            }
            if (end + 1 >= head.Length)
            {
                end = head.Length;
            }
            var line = new StringBuilder(end);
            for (var index = 0; index < end; index++)
            {
                line.Append((char)head[index]);
            }
            var parts = line.ToString().Split(' ');
            if (parts.Length != 3 || !parts[2].StartsWith("HTTP/", StringComparison.Ordinal))
            {
                return null;
            }
            return (parts[0], parts[1]);
        }

        private async Task AcceptLoopAsync(TcpListener listener)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return; // Stopped listening.
                }
                lock (_gate)
                {
                    if (_closed)
                    {
                        Close(client);
                        continue;
                    }
                    if (_connections.Count >= MaxConnections)
                    {
                        // Full: the oldest connection that has sent no request
                        // makes room. A callback sends its request at once, so a
                        // flood of silent connections cannot hold the port.
                        var idle = _connections.FirstOrDefault(connection => !connection.Busy);
                        if (idle is null)
                        {
                            Close(client);
                            continue;
                        }
                        _connections.Remove(idle);
                        Close(idle.Client);
                    }
                    var opened = new Connection(client);
                    _connections.Add(opened);
                    _handlers.RemoveWhere(handler => handler.IsCompleted);
                    _handlers.Add(Task.Run(() => HandleAsync(opened)));
                }
            }
        }

        /// <summary>One open connection, and whether it has sent its request head.</summary>
        private sealed class Connection
        {
            internal Connection(TcpClient client) => Client = client;

            internal TcpClient Client { get; }

            /// <summary>Set once a request head (or too much of one) arrived; never evicted after that.</summary>
            internal bool Busy { get; set; }
        }

        /// <summary>One connection: one request, one answer. Its failure is that connection's alone.</summary>
        private async Task HandleAsync(Connection connection)
        {
            try
            {
                await ServeAsync(connection).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A tab that went away, or the listener closing it: nothing to answer.
            }
            finally
            {
                lock (_gate)
                {
                    _connections.Remove(connection);
                }
                Close(connection.Client);
            }
        }

        private async Task ServeAsync(Connection connection)
        {
            var client = connection.Client;
            var stream = client.GetStream();
            var (head, overrun) = await ReadHeadAsync(client, stream).ConfigureAwait(false);
            if (head != null || overrun)
            {
                lock (_gate)
                {
                    connection.Busy = true;
                }
            }
            if (overrun)
            {
                await AnswerAsync(client, stream, 400, WrongStateText).ConfigureAwait(false);
                return;
            }
            if (head is null)
            {
                return; // Closed, broken or silent before a whole request head.
            }
            var request = RequestLine(head);
            if (request is null)
            {
                await AnswerAsync(client, stream, 400, WrongStateText).ConfigureAwait(false);
                return;
            }
            var (status, callback) = Classify(request.Value.Method, request.Value.Target, _state);
            var accepted = false;
            if (callback != null)
            {
                lock (_gate)
                {
                    // A second answer with the right state, after the first was
                    // accepted, is not this sign-in's any more either.
                    if (!_closed && !_acceptedOne)
                    {
                        _acceptedOne = true;
                        accepted = true;
                    }
                }
            }
            if (!accepted)
            {
                if (callback != null)
                {
                    status = 400;
                }
                await AnswerAsync(client, stream, status, status == 404 ? NotFoundText : WrongStateText).ConfigureAwait(false);
                return;
            }
            try
            {
                // The first accepted callback ends the listening; a tab already
                // connected is still answered. Stopped before the waiter hears
                // of it, so that nothing it does next can still connect.
                StopListening();
                _accepted.TrySetResult(callback!);
                var signedIn = false;
                using (var stop = new CancellationTokenSource())
                {
                    var timer = Task.Delay(AnswerTimeout, stop.Token);
                    if (await Task.WhenAny(_answer.Task, timer).ConfigureAwait(false) == _answer.Task)
                    {
                        signedIn = _answer.Task.Result;
                    }
                    stop.Cancel();
                }
                await WriteAsync(stream, ResponseBytes(200, signedIn ? SignedInText : NotCompletedText)).ConfigureAwait(false);
                EndSending(client);
            }
            finally
            {
                _acceptedAnswered.TrySetResult(true);
            }
            await DrainAsync(client, stream).ConfigureAwait(false);
        }

        /// <summary>
        /// The request head, up to and with its blank line; overrun when it is
        /// longer than <see cref="MaxRequestHead"/>; or null when the connection
        /// ends, breaks or stays silent for <see cref="RequestReadTimeout"/>
        /// before sending one.
        /// </summary>
        private static async Task<(byte[]? Head, bool Overrun)> ReadHeadAsync(TcpClient client, NetworkStream stream)
        {
            var limit = MaxRequestHead + HeadEnd.Length;
            var buffer = new byte[FirstReadSize];
            var filled = 0;
            using (var timeout = new CancellationTokenSource(RequestReadTimeout))
            // Closing the socket is what ends a pending read on every runtime.
            using (timeout.Token.Register(() => Close(client)))
            {
                try
                {
                    while (filled < limit)
                    {
                        if (filled == buffer.Length)
                        {
                            Array.Resize(ref buffer, Math.Min(limit, buffer.Length * 4));
                        }
                        var read = await stream.ReadAsync(buffer, filled, buffer.Length - filled, timeout.Token).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            return (null, false);
                        }
                        var from = Math.Max(0, filled - (HeadEnd.Length - 1));
                        filled += read;
                        var end = IndexOfHeadEnd(buffer, from, filled);
                        if (end >= 0)
                        {
                            var head = new byte[end];
                            Buffer.BlockCopy(buffer, 0, head, 0, end);
                            return (head, false);
                        }
                    }
                }
                catch (Exception)
                {
                    return (null, false);
                }
            }
            return (null, true);
        }

        /// <summary>The index just past the first blank line in <c>buffer[from..filled)</c>, or -1.</summary>
        private static int IndexOfHeadEnd(byte[] buffer, int from, int filled)
        {
            for (var index = from; index + HeadEnd.Length <= filled; index++)
            {
                if (buffer[index] == '\r' && buffer[index + 1] == '\n' && buffer[index + 2] == '\r' && buffer[index + 3] == '\n')
                {
                    return index + HeadEnd.Length;
                }
            }
            return -1;
        }

        private static async Task AnswerAsync(TcpClient client, NetworkStream stream, int status, string text)
        {
            await WriteAsync(stream, ResponseBytes(status, text)).ConfigureAwait(false);
            EndSending(client);
            await DrainAsync(client, stream).ConfigureAwait(false);
        }

        private static async Task WriteAsync(NetworkStream stream, byte[] bytes)
        {
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>Half-closes the connection once the answer is written, so the tab sees its end at once.</summary>
        private static void EndSending(TcpClient client)
        {
            try
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch (Exception)
            {
                // Already gone.
            }
        }

        /// <summary>
        /// Reads what the tab still sends, briefly, before the socket is closed:
        /// closing with unread input resets the connection, and a reset can
        /// discard the answer before the tab has read it (an oversized head is
        /// the case that leaves input unread).
        /// </summary>
        private static async Task DrainAsync(TcpClient client, NetworkStream stream)
        {
            var sink = new byte[4096];
            var total = 0;
            using (var timeout = new CancellationTokenSource(CloseGrace))
            using (timeout.Token.Register(() => Close(client)))
            {
                try
                {
                    while (total < 4 * MaxRequestHead)
                    {
                        var read = await stream.ReadAsync(sink, 0, sink.Length, timeout.Token).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            return;
                        }
                        total += read;
                    }
                }
                catch (Exception)
                {
                    // The tab is gone, or took too long: nothing more to read.
                }
            }
        }

        private void StopListening()
        {
            lock (_gate)
            {
                if (_stopped || _listener is null)
                {
                    return;
                }
                _stopped = true;
                try
                {
                    _listener.Stop();
                }
                catch (Exception)
                {
                    // Already stopped.
                }
            }
        }

        private static void Close(TcpClient client)
        {
            try
            {
                client.Dispose();
            }
            catch (Exception)
            {
                // Already closed.
            }
        }

        /// <summary>Waits for <paramref name="task"/>, but no longer than <paramref name="limit"/>.</summary>
        private static async Task AtMostAsync(Task task, TimeSpan limit)
        {
            using (var stop = new CancellationTokenSource())
            {
                await Task.WhenAny(task, Task.Delay(limit, stop.Token)).ConfigureAwait(false);
                stop.Cancel();
            }
        }

        private static TimeSpan Elapsed(long started) =>
            TimeSpan.FromTicks((long)((Stopwatch.GetTimestamp() - started) * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency)));
    }
}
