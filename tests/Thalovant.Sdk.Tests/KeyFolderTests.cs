using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Thalovant.Sdk.Tests;

/// <summary>
/// Where an identity's Noise key lives when the caller names no store: beside
/// the file the identity was read from, so every program that reads that file
/// presents the same key to the hub, which pins the first key a connection
/// shows it. The first time that folder is used, the key this identity had in
/// the shared default folder is copied into it, when it has met this
/// identity's hub.
/// </summary>
public sealed class KeyFolderTests : IDisposable
{
    private const string Hub = "test-hub";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "thalovant-key-folder-" + Guid.NewGuid().ToString("N"));

    public KeyFolderTests()
    {
        Private(_root);
    }

    public void Dispose()
    {
        foreach (var directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Prepend(_root))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static string Private(string directory)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    /// <summary>An identity file in <paramref name="folder"/>, read back as a program would read it.</summary>
    private static ThalovantIdentity IdentityIn(string folder)
    {
        var path = Path.Combine(Private(folder), "identity.json");
        File.WriteAllText(path, """{"access_key":"test-access","password":"test-password","site_id":"test-site","default_master":"ws://127.0.0.1:5678"}""");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return ThalovantIdentity.FromFile(path);
    }

    [Fact]
    public void AnIdentityReadFromAFileKnowsWhichFile()
    {
        var identity = IdentityIn(Path.Combine(_root, "config"));
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "config", "identity.json")), identity.SourcePath);
        Assert.Null(ThalovantIdentity.FromJson("""{"access_key":"a","password":"p","site_id":"s","default_master":"ws://h"}""").SourcePath);
        // Not identity material: never serialized.
        Assert.DoesNotContain("identity.json", identity.ToJsonObject(includeSecrets: true).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyLivesBesideTheIdentityFile()
    {
        var identity = IdentityIn(Path.Combine(_root, "config"));
        var store = HiveMindFileNoiseStore.ForIdentity(identity, Path.Combine(_root, "legacy"));
        var beside = Path.Combine(_root, "config", "noise");
        Assert.Equal(beside, store.DirectoryPath);
        store.LoadOrCreateStaticKey();
        Assert.True(File.Exists(Path.Combine(beside, "noise-static.key")));
        // Two programs reading the same file present the same key.
        Assert.Equal(store.LoadOrCreateStaticKey(), HiveMindFileNoiseStore.ForIdentity(identity, Path.Combine(_root, "legacy")).LoadOrCreateStaticKey());
    }

    [Fact]
    public void AClientGivenNoStoreKeepsItsKeyBesideTheFile()
    {
        var identity = IdentityIn(Path.Combine(_root, "config"));
        var transport = new HiveMindWssTransport(identity);
        var (used, other) = transport.KeyFolders();
        Assert.Equal(Path.Combine(_root, "config", "noise"), used);
        // The other place a program reading this identity kept its key: the old default.
        Assert.Equal(Path.GetFullPath(HiveMindFileNoiseStore.DefaultDirectory), other);
    }

    [Fact]
    public void AKeyThatMetThisHubIsCopiedNotMoved()
    {
        var legacy = Path.Combine(_root, "legacy");
        var old = new HiveMindFileNoiseStore(legacy);
        var key = old.LoadOrCreateStaticKey();
        var pin = Noise.RandomKey();
        old.VerifyOrPin(Hub, pin);
        var before = Directory.GetFiles(legacy).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();

        var store = HiveMindFileNoiseStore.ForIdentity(IdentityIn(Path.Combine(_root, "config")), legacy);
        Assert.Equal(pin, store.LoadPin(Hub));
        Assert.Equal(key, store.LoadOrCreateStaticKey());
        // Another program may still read the old folder: it is left as it was.
        Assert.Equal(before, Directory.GetFiles(legacy).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(key, old.LoadOrCreateStaticKey());
    }

    [Fact]
    public void AKeyWhosePinForThisHubCannotBeReadIsNotCopied()
    {
        var legacy = Path.Combine(_root, "legacy");
        var old = new HiveMindFileNoiseStore(legacy);
        var key = old.LoadOrCreateStaticKey();
        old.VerifyOrPin(Hub, Noise.RandomKey());
        var ownPin = Directory.GetFiles(legacy, "noise-pin-*.key").Single();
        File.WriteAllText(ownPin, "not a key");

        var store = HiveMindFileNoiseStore.ForIdentity(IdentityIn(Path.Combine(_root, "config")), legacy);
        // Never the old key without the pin that checks this hub.
        Assert.Null(store.LoadPin(Hub));
        Assert.NotEqual(key, store.LoadOrCreateStaticKey());
    }

    [Fact]
    public void AnotherHubsUnreadablePinDoesNotStopThisHubsCopy()
    {
        if (OperatingSystem.IsWindows()) return;
        var legacy = Path.Combine(_root, "legacy");
        var old = new HiveMindFileNoiseStore(legacy);
        var key = old.LoadOrCreateStaticKey();
        var pin = Noise.RandomKey();
        old.VerifyOrPin(Hub, pin);
        var otherPin = Noise.RandomKey();
        old.VerifyOrPin("another-hub", otherPin);
        var other = Directory.GetFiles(legacy, "noise-pin-*.key").Single(file => File.ReadAllText(file) == Noise.Hex(otherPin));
        File.SetUnixFileMode(other, UnixFileMode.None);
        try
        {
            try { File.ReadAllText(other); return; } // running as root: nothing is unreadable
            catch (UnauthorizedAccessException) { }

            var store = HiveMindFileNoiseStore.ForIdentity(IdentityIn(Path.Combine(_root, "config")), legacy);
            Assert.Equal(pin, store.LoadPin(Hub));
            Assert.Equal(key, store.LoadOrCreateStaticKey());
            Assert.Null(store.LoadPin("another-hub"));
        }
        finally
        {
            File.SetUnixFileMode(other, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void AnOldFolderThatCannotBeListedStillGivesThisHubItsKey()
    {
        if (OperatingSystem.IsWindows()) return;
        var legacy = Path.Combine(_root, "legacy");
        var old = new HiveMindFileNoiseStore(legacy);
        var key = old.LoadOrCreateStaticKey();
        var pin = Noise.RandomKey();
        old.VerifyOrPin(Hub, pin);
        File.SetUnixFileMode(legacy, UnixFileMode.UserExecute);
        try
        {
            try { Directory.GetFiles(legacy); return; } // running as root: every folder can be listed
            catch (UnauthorizedAccessException) { }

            var store = HiveMindFileNoiseStore.ForIdentity(IdentityIn(Path.Combine(_root, "config")), legacy);
            Assert.Equal(pin, store.LoadPin(Hub));
            Assert.Equal(key, store.LoadOrCreateStaticKey());
        }
        finally
        {
            File.SetUnixFileMode(legacy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void AKeyThatNeverMetThisHubIsNotCopied()
    {
        var legacy = Path.Combine(_root, "legacy");
        var old = new HiveMindFileNoiseStore(legacy);
        var key = old.LoadOrCreateStaticKey();
        old.VerifyOrPin("another-hub", Noise.RandomKey());

        var store = HiveMindFileNoiseStore.ForIdentity(IdentityIn(Path.Combine(_root, "config")), legacy);
        Assert.Null(store.LoadPin(Hub));
        Assert.NotEqual(key, store.LoadOrCreateStaticKey());
        Assert.Null(store.LoadPin("another-hub"));
    }

    [Fact]
    public void AFolderThatHasAKeyKeepsIt()
    {
        var legacy = Path.Combine(_root, "legacy");
        var old = new HiveMindFileNoiseStore(legacy);
        old.LoadOrCreateStaticKey();
        old.VerifyOrPin(Hub, Noise.RandomKey());
        var identity = IdentityIn(Path.Combine(_root, "config"));
        var mine = new HiveMindFileNoiseStore(Path.Combine(_root, "config", "noise")).LoadOrCreateStaticKey();

        var store = HiveMindFileNoiseStore.ForIdentity(identity, legacy);
        Assert.Null(store.LoadPin(Hub));
        Assert.Equal(mine, store.LoadOrCreateStaticKey());
    }

    [Fact]
    public void AnIdentityInAFolderItCannotWriteToUsesTheSharedDefault()
    {
        if (OperatingSystem.IsWindows()) return; // no POSIX modes to take away
        var folder = Path.Combine(_root, "etc");
        var identity = IdentityIn(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            File.Create(Path.Combine(folder, "probe")).Dispose();
            return; // running as root: nothing is unwritable
        }
        catch (UnauthorizedAccessException)
        {
        }
        var legacy = Path.Combine(_root, "legacy");
        var store = HiveMindFileNoiseStore.ForIdentity(identity, legacy);
        store.LoadOrCreateStaticKey();
        Assert.Equal(Path.GetFullPath(legacy), store.DirectoryPath);
        Assert.True(File.Exists(Path.Combine(legacy, "noise-static.key")));
    }

    [Fact]
    public void AnIdentityInTheDefaultFoldersParentHasNothingToCopy()
    {
        // Beside the file is the shared default itself: one folder, no copy.
        var legacyParent = Path.Combine(_root, "Thalovant");
        var identity = IdentityIn(legacyParent);
        var store = HiveMindFileNoiseStore.ForIdentity(identity, Path.Combine(legacyParent, "noise"));
        Assert.Equal(Path.Combine(legacyParent, "noise"), store.DirectoryPath);
        Assert.Null(store.LoadPin(Hub));
    }
}
