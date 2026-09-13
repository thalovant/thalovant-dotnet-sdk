using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests;

[Collection("Runtime deadlines")]
public sealed class SessionInventoryTests
{
    [Fact]
    public void SharedPythonReferenceSurvivesSortedJson()
    {
        var data = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "inventory-vectors.json")))!;
        var inventory = Inventory.FromObject(data["inventory"]);
        foreach (var row in data["examples"]!.AsArray()) Assert.Equal(row!["expected"]!.AsArray().Select(v => v!.GetValue<string>()), inventory.Skills[0].Intents[0].Examples(row["language"]?.GetValue<string>(), row["limit"]!.GetValue<int>()));
        foreach (var row in data["speaks"]!.AsArray()) Assert.Equal(row!["expected"]!.GetValue<bool>(), inventory.Skills[0].Speaks(row["language"]!.GetValue<string>()));
        Assert.Null(inventory.Skills[1].Speaks("en"));
    }

    [Fact]
    public async Task AmbiguousDispatchIsNotReplayedAndSubscriptionsSurviveReplacement()
    {
        var first = new RuntimeTests.Fake();
        var second = new RuntimeTests.Fake();
        first.EmitAction = _ => throw new IOException("socket closed after send");
        int builds = 0, received = 0;
        await using var session = new HubSession(_ => Task.FromResult(RuntimeTests.Client(++builds == 1 ? first : second)), warm: false);
        using var subscription = session.On("speak", _ => received++);
        await Assert.ThrowsAsync<IOException>(() => session.EmitAsync("action"));
        Assert.Equal(1, builds);
        Assert.Single(first.Emitted);
        Assert.False(session.Held);
        await session.EmitAsync("next");
        first.Deliver("speak"); second.Deliver("speak");
        Assert.Equal(1, received);
        Assert.Equal(2, builds);
        subscription.Close(); second.Deliver("speak");
        Assert.Equal(1, received);
    }

    [Fact]
    public async Task ForegroundBypassesWarmBackoffAndCloseIsTerminal()
    {
        double now = 100;
        int attempts = 0;
        var fake = new RuntimeTests.Fake();
        await using var session = new HubSession(_ => ++attempts == 1
            ? Task.FromException<ThalovantClient>(new IOException("unavailable"))
            : Task.FromResult(RuntimeTests.Client(fake)), clock: () => now, warm: false);
        Assert.Equal(5, session.ProbeDelay());
        await session.WarmAsync(); await session.WarmAsync();
        Assert.Equal(1, attempts); Assert.Equal(110, session.RetryAt); Assert.Equal(20, session.RetryWait);
        await session.EmitAsync("foreground");
        Assert.Equal(2, attempts); Assert.Equal(60, session.ProbeDelay()); Assert.Equal(10, session.RetryWait);
        await session.CloseAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.EmitAsync("closed"));
        Assert.Throws<ObjectDisposedException>(() => session.On("speak", _ => { }));
        await session.WarmAsync(); Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CloseWaitsForAdmittedCallAndCancelledQueueDoesNotDispatch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new RuntimeTests.Fake { EmitAction = async _ => { entered.TrySetResult(); await release.Task; } };
        await using var session = new HubSession(_ => Task.FromResult(RuntimeTests.Client(fake)), warm: false);
        var owner = session.EmitAsync("owner"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancelled = new CancellationTokenSource();
        var queued = session.EmitAsync("queued", cancellationToken: cancelled.Token);
        cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        var closing = session.CloseAsync();
        Assert.False(closing.IsCompleted); Assert.True(fake.Connected);
        release.SetResult(); await owner; await closing;
        Assert.False(fake.Connected); Assert.Single(fake.Emitted);
    }

    [Fact]
    public async Task OriginFallbackPreservesHostAndCancellationDoesNotFallback()
    {
        double now = 10;
        var preference = new OriginPreference("10.0.0.2", clock: () => now);
        var attempts = new List<OriginAttempt>();
        Task<string> Build(OriginAttempt attempt, CancellationToken token)
        {
            attempts.Add(attempt);
            return attempt.Address == null ? Task.FromResult("public") : Task.FromException<string>(new IOException("offline"));
        }
        var options = new OriginAttempt("hub.example", TimeSpan.FromSeconds(12));
        Assert.Equal("public", await preference.ConnectAsync(options, Build));
        Assert.Equal(2, attempts.Count); Assert.All(attempts, a => Assert.Equal("hub.example", a.Host));
        Assert.Equal(TimeSpan.FromSeconds(1.5), attempts[0].HandshakeTimeout);
        Assert.True(preference.CoolingDown);
        await preference.ConnectAsync(options, Build); Assert.Equal(3, attempts.Count);
        now += 301;
        int cancelledAttempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preference.ConnectAsync<string>(options, (_, _) =>
        {
            cancelledAttempts++; throw new OperationCanceledException();
        }));
        Assert.Equal(1, cancelledAttempts);
        Assert.False(preference.CoolingDown);
        using var cancelled = new CancellationTokenSource();
        int interruptedAttempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preference.ConnectAsync<string>(options, (_, _) => {
            interruptedAttempts++; cancelled.Cancel(); throw new IOException("interrupted dial");
        }, cancelled.Token));
        Assert.Equal(1, interruptedAttempts); Assert.False(preference.CoolingDown);
    }

    private static Inventory Sample() => new Inventory("hub", "Kitchen", "hub", "2026-09-13T00:00:00Z", new[] {
        new Skill("weather", "Weather", new[] {"en-us"}, new[] {
            new Intent("weather.now", "weather.now", "weather", "padatious", new Dictionary<string, IReadOnlyList<string>> {
                ["fr-fr"] = new[] {"météo"}, ["en-us"] = new[] {"weather", "what is the weather"}
            })
        }), new Skill("unknown", "Unknown")
    });

    [Fact]
    public void InventoryRoundTripPreservesLocaleKnowledgeAndRejectsMalformedShape()
    {
        var inventory = Inventory.FromObject(Sample().AsObject());
        Assert.True(inventory.Live); Assert.True(inventory.HasPhrases);
        Assert.True(inventory.Skills[0].Speaks("en-gb")); Assert.False(inventory.Skills[0].Speaks("de"));
        Assert.Null(inventory.Skills[1].Speaks("en"));
        Assert.Equal(new[] { "météo" }, inventory.Skills[0].Intents[0].Examples(limit: 0));
        Assert.Equal(new[] { "weather", "what is the weather" }, inventory.Skills[0].Intents[0].Examples("en-gb", 0));
        Assert.Equal(new[] { "en-us", "fr-fr" }, InventoryHelpers.LanguagesPresent(inventory));
        var raw = inventory.AsObject(); raw.Remove("notes"); Assert.Throws<FormatException>(() => Inventory.FromObject(raw));
        raw = inventory.AsObject(); raw["cache_version"] = true; Assert.Throws<FormatException>(() => Inventory.FromObject(raw));
        raw = inventory.AsObject(); raw["skills"]![0]!["locales"] = 42; Assert.Throws<FormatException>(() => Inventory.FromObject(raw));
    }

    [Fact]
    public void CacheIsPrivateOptionalAndRejectsTraversalCorruptionAndExpiredData()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new InventoryCache(directory);
            cache.Store("valid", Sample()); Assert.NotNull(cache.Load("valid"));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(cache.PathFor("valid")));
            cache.Store("../../escape", Sample()); Assert.Null(cache.Load("../../escape"));
            File.SetLastWriteTimeUtc(cache.PathFor("valid"), DateTime.UtcNow.AddHours(-2)); Assert.Null(cache.Load("valid"));
            File.WriteAllText(cache.PathFor("valid"), "{broken"); Assert.Null(cache.Load("valid"));
            cache.Store("valid", Sample()); Assert.NotNull(cache.Load("valid"));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void PolicyAndPresentationBoundaries()
    {
        foreach (var invalid in new[] { 0, -1, double.NaN, double.PositiveInfinity }) Assert.Throws<ArgumentOutOfRangeException>(() => new HubSessionPolicy(retrySeconds: invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HubSessionPolicy(retrySeconds: 20, retryCeilingSeconds: 10));
        Assert.Equal("Weather", InventoryHelpers.FriendlyTitle("ovos-skill-weather.openvoiceos"));
        Assert.Equal(("suffix", "intent"), InventoryHelpers.CommonAffix(new[] { "weather.intent", "time.intent" }));
        Assert.Equal("", InventoryHelpers.StripAffix("", "suffix", ""));
        Assert.True(InventoryHelpers.CompareNames("Skill2", "Skill10") < 0);
    }
}
