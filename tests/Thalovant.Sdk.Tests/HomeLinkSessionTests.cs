using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests;

/// <summary>
/// A link kept by <see cref="HubSession"/>, against an in-memory hub that speaks
/// the real Noise handshake: the settle window, the refusal grace, the ladder and
/// the redial after a drop, and home requests answered over the session.
/// </summary>
[Collection("Runtime deadlines")]
public sealed class HomeLinkSessionTests
{
    private static readonly HubSessionPolicy Fast = new HubSessionPolicy(
        retrySeconds: 0.05, retryCeilingSeconds: 0.2, probeSeconds: 0.05, probeDownSeconds: 0.05, refusalGraceSeconds: 0.4);

    /// <summary>A session whose every dial goes to the next hub <paramref name="next"/> builds.</summary>
    private static (HubSession Session, ConcurrentQueue<HubPeer> Peers) Session(Func<int, HubPeer> next, HubSessionPolicy? policy = null)
    {
        var peers = new ConcurrentQueue<HubPeer>();
        var store = new HubPeer.Store();
        var attempts = 0;
        var session = new HubSession(async cancellationToken =>
        {
            var peer = next(Interlocked.Increment(ref attempts));
            peers.Enqueue(peer);
            var client = HubPeer.ClientFor(peer, store);
            try
            {
                await client.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return client;
            }
            catch
            {
                await client.CloseAsync();
                throw;
            }
        }, policy ?? Fast, warm: false);
        return (session, peers);
    }

    private static async Task Eventually(Func<bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition never became true");
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(WebSocketCloseStatus.NormalClosure)]
    [InlineData(WebSocketCloseStatus.Empty)]
    [InlineData(WebSocketCloseStatus.PolicyViolation)]
    public async Task AHubThatClosesRightAfterTheHandshakeRefusedTheCredentials(WebSocketCloseStatus? status)
    {
        var (session, peers) = Session(attempt => attempt == 1
            ? new HubPeer { CloseAfterHandshake = true, CloseAfterHandshakeWith = status }
            : new HubPeer());
        await using var _ = session;
        session.SettleWindow = TimeSpan.FromSeconds(2);
        // As an XX handshake ended, with nothing from the hub: the refusal is of
        // this client's own key, which is still a refusal.
        await Assert.ThrowsAsync<ThalovantClientKeyRejectedException>(() => session.ConnectAsync());
        Assert.False(session.Held);
        Assert.False(session.Connected);
        // Refused is a failed attempt: the ladder moved.
        Assert.True(session.RetryAt > 0);
        // A link that stays up waits the whole window out; a short one will do.
        session.SettleWindow = TimeSpan.FromMilliseconds(50);
        await session.ConnectAsync();
        Assert.True(session.Connected);
        Assert.Equal(2, peers.Count);
    }

    [Fact]
    public async Task AHubThatFailsRightAfterTheHandshakeDroppedTheLinkButRefusedNothing()
    {
        var (session, _) = Session(_ => new HubPeer { CloseAfterHandshake = true, CloseAfterHandshakeWith = WebSocketCloseStatus.InternalServerError });
        await using var owned = session;
        session.SettleWindow = TimeSpan.FromSeconds(2);
        var error = await Assert.ThrowsAsync<ThalovantConnectionException>(() => session.ConnectAsync());
        Assert.IsNotType<ThalovantHubRefusedException>(error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANoStatusCloseAnywhereInTheHandshakeIsARefusal(bool beforeHello)
    {
        // Including between the hub's HELLO and its offer: every step of the
        // handshake counts (link-keeping-vectors.json).
        var client = HubPeer.ClientFor(new HubPeer(beforeHello ? HubPeer.Opening.RefuseBeforeHello : HubPeer.Opening.CloseAfterHello));
        await Assert.ThrowsAsync<ThalovantHubRefusedException>(() => client.ConnectAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task RefusalsAreRetriedThroughTheGraceThenThrown()
    {
        var (session, peers) = Session(_ => new HubPeer(HubPeer.Opening.RefuseBeforeHello));
        await using var owned = session;
        await Assert.ThrowsAsync<ThalovantHubRefusedException>(() => session.RunAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        // More than one attempt: the first refusals read as "not admitted yet".
        Assert.True(peers.Count >= 2, $"only {peers.Count} attempt(s)");
    }

    [Fact]
    public async Task ARefusalThatClearsInsideTheGraceConnects()
    {
        var policy = new HubSessionPolicy(0.05, 0.1, 0.05, 0.05, refusalGraceSeconds: 30);
        var (session, peers) = Session(attempt => attempt < 3 ? new HubPeer(HubPeer.Opening.RefuseBeforeHello) : new HubPeer(), policy);
        var states = new ConcurrentQueue<bool>();
        using var watching = session.OnStateChange(states.Enqueue);
        var running = session.RunAsync();
        await Eventually(() => session.Connected);
        Assert.Equal(3, peers.Count);
        Assert.Equal(new[] { true }, states.ToArray());
        await session.CloseAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { true, false }, states.ToArray());
    }

    [Fact]
    public async Task RunKeepsTheLinkConnectOpenedAndDialsAgainAfterADrop()
    {
        var (session, peers) = Session(_ => new HubPeer());
        var states = new ConcurrentQueue<bool>();
        using var watching = session.OnStateChange(states.Enqueue);
        session.SettleWindow = TimeSpan.FromMilliseconds(50);
        await session.ConnectAsync();
        Assert.True(session.Connected);
        using var cancel = new CancellationTokenSource();
        var running = session.RunAsync(cancel.Token);
        await Task.Delay(200);
        // The link ConnectAsync opened is the one RunAsync keeps.
        Assert.Single(peers);
        peers.Last().Drop();
        await Eventually(() => peers.Count == 2 && session.Connected);
        Assert.Equal(new[] { true, false, true }, states.ToArray());
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        await session.CloseAsync();
    }

    [Fact]
    public async Task HomeRequestsAreAnsweredBackAlongTheirRouteOverTheSession()
    {
        var (session, peers) = Session(_ => new HubPeer());
        await using var owned = session;
        session.SettleWindow = TimeSpan.FromMilliseconds(50);
        var heard = new ConcurrentQueue<HomeRequest>();
        using var answering = session.AnswerHomeRequests((request, _) =>
        {
            heard.Enqueue(request);
            return new ValueTask<HomeAnswer>(HomeAnswer.ActionDone("<speak>Turned off the kitchen light.</speak>"));
        });
        await session.ConnectAsync();
        var peer = peers.Single();
        peer.SendBus(ThalovantHome.RequestEvent,
            new JsonObject { ["request_id"] = "r1", ["utterance"] = "turn off the kitchen light", ["lang"] = "en-US" },
            new JsonObject { ["source"] = "thalovant-skill-home", ["destination"] = new JsonArray("ha-peer"), ["session"] = new JsonObject { ["session_id"] = "kitchen" } });
        var sent = await peer.NextAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("turn off the kitchen light", heard.Single().Utterance);
        Assert.Equal("en-US", heard.Single().Lang);
        Assert.Equal(ThalovantHome.ResponseEvent, (string?)sent["type"]);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"request_id":"r1","speech":"Turned off the kitchen light.","response_type":"action_done","continue_conversation":false}"""),
            sent["data"]), sent["data"]!.ToJsonString());
        Assert.Equal("thalovant-skill-home", (string?)sent["context"]!["destination"]);
        Assert.Equal("ha-peer", (string?)sent["context"]!["source"]);
        Assert.Equal("kitchen", (string?)sent["context"]!["session"]!["session_id"]);

        // Closed: the next request goes unanswered here, and the hub answers the device itself.
        answering.Close();
        peer.SendBus(ThalovantHome.RequestEvent, new JsonObject { ["request_id"] = "r2", ["utterance"] = "again" }, new JsonObject());
        await Task.Delay(200);
        Assert.False(peer.Received.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AnswersRunOffTheReceiveLoopSoASlowOneDoesNotHoldUpTheNext()
    {
        var peer = new HubPeer();
        using var client = HubPeer.ClientFor(peer);
        await client.ConnectAsync(TimeSpan.FromSeconds(10));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var answering = client.AnswerHomeRequests(async (request, cancellationToken) =>
        {
            if (request.RequestId == "slow") await release.Task.WaitAsync(cancellationToken);
            return HomeAnswer.QueryAnswer(request.RequestId);
        });
        peer.SendBus(ThalovantHome.RequestEvent, new JsonObject { ["request_id"] = "slow" }, new JsonObject());
        peer.SendBus(ThalovantHome.RequestEvent, new JsonObject { ["request_id"] = "quick" }, new JsonObject());
        var first = await peer.NextAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("quick", (string?)first["data"]!["request_id"]);
        release.SetResult();
        var second = await peer.NextAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("slow", (string?)second["data"]!["request_id"]);
    }

    [Fact]
    public async Task AReplyWithdrawnWhileQueuedIsNeitherSentNorAFailureOfTheLink()
    {
        var peer = new HubPeer();
        using var client = HubPeer.ClientFor(peer);
        await client.ConnectAsync(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.HoldNextFrame = async () => { entered.TrySetResult(); await release.Task; };
        // Another frame is being written: the reply has to queue behind it.
        var writing = client.EmitAsync("test.first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var request = HomeRequest.FromEvent(new ThalovantEvent(ThalovantHome.RequestEvent,
            new JsonObject { ["request_id"] = "w1", ["utterance"] = "x" }, new JsonObject { ["source"] = "skill" }));
        var sent = await ThalovantHome.AnswerAsync(
            request,
            (_, _) => new ValueTask<HomeAnswer>(HomeAnswer.ActionDone("Done.")),
            (payload, token) => client.ReplyAsync(request.Event!, ThalovantHome.ResponseEvent, payload, cancellationToken: token),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(300));
        Assert.Null(sent); // withdrawn at the hub's bound
        release.SetResult();
        await writing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("test.first", (string?)(await peer.NextAsync(TimeSpan.FromSeconds(5)))["type"]);
        await Task.Delay(200);
        // Never sent once the lock came free, and the link is as it was.
        Assert.False(peer.Received.Reader.TryRead(out _));
        Assert.True(client.LinkUp);
        Assert.Null(client.Transport!.LastError);
        await client.EmitAsync("test.after");
        Assert.Equal("test.after", (string?)(await peer.NextAsync(TimeSpan.FromSeconds(5)))["type"]);
    }

    [Theory]
    [InlineData("<b\"x\">bold</b>", "bold")]
    [InlineData("<a <b>c", "c")]
    [InlineData("<!-- unclosed comment", "<!-- unclosed comment")]
    [InlineData("<?unclosed <b>x</b>", "<?unclosed x")]
    [InlineData("<a title='5 > 3'>x</a>", "x")]
    [InlineData("<a title='unclosed>x", "<a title='unclosed>x")]
    public void ATagRunsFromALetterToTheNextUnquotedGreaterThan(string text, string plain)
    {
        Assert.Equal(plain, ThalovantHome.PlainSpeech(text));
    }

    [Theory]
    [InlineData("5 < 6 and 7 > 3", "5 < 6 and 7 > 3")]
    [InlineData("<speak>Hello <break time=\"1s\"/>there</speak>", "Hello there")]
    [InlineData("a<!-- note -->b<?pi x?>c", "abc")]
    [InlineData("<a title='5 > 3'>x</a>", "x")]
    [InlineData("  keeps  its  spaces  ", "  keeps  its  spaces  ")]
    public void DisplayTextUsesTheSameTagRule(string text, string display)
    {
        // Only the tags go: unlike PlainSpeech, the white space is left alone.
        Assert.Equal(display, ThalovantContext.StripSsml(text));
    }

    [Fact]
    public void MarkupIsRemovedInLinearTime()
    {
        // Each of these made a backtracking expression quadratic or worse.
        var inputs = new[]
        {
            "<a" + new string(' ', 200_000),
            string.Concat(Enumerable.Repeat("<a '", 50_000)),
            string.Concat(Enumerable.Repeat("<!--", 50_000)),
            string.Concat(Enumerable.Repeat("<?", 100_000)),
            string.Concat(Enumerable.Repeat("<a \"", 50_000)) + ">",
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var input in inputs) ThalovantHome.PlainSpeech(input);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
    }

    [Fact]
    public void TheHandlerTimeoutMustBePositive()
    {
        var client = HubPeer.ClientFor(new HubPeer());
        Assert.Throws<ArgumentOutOfRangeException>(() => client.AnswerHomeRequests((_, _) => default, TimeSpan.Zero));
        Assert.Throws<ArgumentNullException>(() => client.AnswerHomeRequests(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubSessionPolicy(1, 2, 3, 4, refusalGraceSeconds: 0));
    }

    [Theory]
    [InlineData("<speak>It is <say-as interpret-as=\"cardinal\">21</say-as>&nbsp;degrees\n  &amp; rising.</speak>", "It is 21 degrees & rising.")]
    [InlineData(null, "")]
    [InlineData("  a\u001cb\u2003c\t", "a\u001cb c")]
    [InlineData("a < b", "a < b")]
    public void SpeechIsPlainText(string? text, string plain)
    {
        Assert.Equal(plain, ThalovantHome.PlainSpeech(text));
    }

    [Fact]
    public void AnAnswerOutsideTheContractIsUnknownAndKeepsItsSpeech()
    {
        var request = new HomeRequest("r1", "dim the lights", conversationId: "c1");
        var payload = ThalovantHome.Response(request, HomeAnswer.Error("no_such_code", "Nope."));
        Assert.Equal("error", (string?)payload["response_type"]);
        Assert.Equal("unknown", (string?)payload["error_code"]);
        Assert.Equal("Nope.", (string?)payload["speech"]);
        Assert.Equal("c1", (string?)payload["conversation_id"]);
        // No answer at all is unknown too; an error code without error is dropped.
        Assert.Equal("unknown", (string?)ThalovantHome.Response(request, null)["error_code"]);
        Assert.False(ThalovantHome.Response(request, new HomeAnswer("Done.", errorCode: "timeout")).ContainsKey("error_code"));
    }
}
