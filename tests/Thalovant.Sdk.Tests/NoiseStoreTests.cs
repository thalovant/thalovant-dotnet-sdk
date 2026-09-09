using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Tests
{
    public sealed class NoiseStoreTests
    {
        private static readonly byte[] Pin = Enumerable.Repeat((byte)0x21, 32).ToArray();
        private static string KeyPath(string directory, bool pin) => Path.Combine(directory, pin
            ? "noise-pin-" + Noise.Hex(Noise.Hash(System.Text.Encoding.UTF8.GetBytes("hub"))) + ".key"
            : "noise-static.key");

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void FailedWriteOrFlushNeverPublishesTrustedState(bool pin, bool afterFlush)
        {
            var directory = Path.Combine(Path.GetTempPath(), "noise-write-failure-" + Guid.NewGuid());
            try {
                var failure = new IOException("Simulated interrupted write or flush.");
                var broken = new HiveMindFileNoiseStore(directory, (stream, bytes) => {
                    stream.Write(bytes, 0, afterFlush ? bytes.Length : bytes.Length / 2);
                    if (afterFlush) stream.Flush(true);
                    Assert.False(File.Exists(KeyPath(directory, pin)));
                    throw failure;
                });
                Assert.Same(failure, Assert.Throws<IOException>(() => {
                    if (pin) broken.VerifyOrPin("hub", Pin); else broken.LoadOrCreateStaticKey();
                }));
                Assert.False(File.Exists(KeyPath(directory, pin)));
                Assert.Empty(Directory.GetFiles(directory, ".noise-*.tmp"));
                VerifyFreshStoreRecovers(directory, pin);
            } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PublicationCollisionNeverOverwritesAnExistingKey(bool pin)
        {
            var directory = Path.Combine(Path.GetTempPath(), "noise-publication-collision-" + Guid.NewGuid());
            try {
                var store = new HiveMindFileNoiseStore(directory, (stream, bytes) => {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                    // Simulate another writer publishing after our existence check.
                    var destination = KeyPath(directory, pin);
                    File.WriteAllText(destination, Noise.Hex(Pin));
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                });
                if (pin) Assert.Throws<CryptographicException>(() => store.VerifyOrPin("hub", new byte[32]));
                else Assert.Equal(Pin, store.LoadOrCreateStaticKey());
                Assert.Equal(Noise.Hex(Pin), File.ReadAllText(KeyPath(directory, pin)));
                Assert.Empty(Directory.GetFiles(directory, ".noise-*.tmp"));
            } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Fact]
        public void CorruptExistingPinIsRejectedWithoutResetOrReplacement()
        {
            var directory = Path.Combine(Path.GetTempPath(), "noise-corrupt-pin-" + Guid.NewGuid());
            try {
                var store = new HiveMindFileNoiseStore(directory);
                store.VerifyOrPin("hub", Pin);
                File.WriteAllText(KeyPath(directory, true), "truncated");
                Assert.Throws<ThalovantConnectionException>(() => store.LoadPin("hub"));
                Assert.Throws<ThalovantConnectionException>(() => store.VerifyOrPin("hub", Pin));
                Assert.Equal("truncated", File.ReadAllText(KeyPath(directory, true)));
            } finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task KilledWriterLeavesOnlyUntrustedStagingFile(bool pin, bool afterFlush)
        {
            var directory = Path.Combine(Path.GetTempPath(), "noise-killed-writer-" + Guid.NewGuid());
            using var child = StartWorker(directory, pin ? "crash-pin" : "crash-static", afterFlush ? "full" : "partial");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            try {
                await WaitFor(() => File.Exists(Path.Combine(directory, "staged")), child);
                Assert.False(File.Exists(KeyPath(directory, pin)));
                var temporary = Assert.Single(Directory.GetFiles(directory, ".noise-*.tmp"));
                Assert.Equal(afterFlush ? 64 : 32, new FileInfo(temporary).Length);
                AssertPrivateFile(temporary);
                child.Kill(entireProcessTree: true);
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await child.WaitForExitAsync(limit.Token);
                Assert.NotEqual(0, child.ExitCode);
                VerifyFreshStoreRecovers(directory, pin);
                // Crashed staging files are ignored, never promoted into trust or
                // removed along with any existing trusted key or pin.
                Assert.True(File.Exists(temporary));
            } finally {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                await output; await error;
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Fact]
        public async Task CompetingProcessesKeepExactlyOnePinAndRejectEveryReplacement()
        {
            var directory = Path.Combine(Path.GetTempPath(), "noise-pin-race-" + Guid.NewGuid());
            // Prepare the private directory without establishing a pin.
            new HiveMindFileNoiseStore(directory).LoadOrCreateStaticKey();
            var children = Enumerable.Range(1, 4).Select(i => StartWorker(directory, "pin-race", i.ToString())).ToArray();
            var logs = children.Select(async child => {
                var output = child.StandardOutput.ReadToEndAsync();
                var error = child.StandardError.ReadToEndAsync();
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                await child.WaitForExitAsync(limit.Token);
                Assert.True(child.ExitCode == 0, (await output) + (await error));
            }).ToArray();
            try {
                await WaitFor(() => Directory.GetFiles(directory, "ready-*").Length == children.Length);
                File.WriteAllText(Path.Combine(directory, "start"), "start");
                await Task.WhenAll(logs);
                var results = Directory.GetFiles(directory, "result-*").Select(File.ReadAllText).ToArray();
                Assert.Equal(4, results.Length);
                var winner = Assert.Single(results, value => value != "conflict");
                Assert.Equal(3, results.Count(value => value == "conflict"));
                var store = new HiveMindFileNoiseStore(directory);
                Assert.Equal(winner, Noise.Hex(store.LoadPin("hub")!));
                Assert.Throws<CryptographicException>(() => store.VerifyOrPin("hub", Pin));
                Assert.Equal(winner, Noise.Hex(store.LoadPin("hub")!));
                AssertPrivateFile(KeyPath(directory, true));
                Assert.Empty(Directory.GetFiles(directory, ".noise-*.tmp"));
            } finally {
                foreach (var child in children) {
                    if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
                    child.Dispose();
                }
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void StoreProcessWorker()
        {
            var operation = Environment.GetEnvironmentVariable("THALOVANT_NOISE_WORKER_OPERATION");
            if (operation == null) return;
            var directory = Environment.GetEnvironmentVariable("THALOVANT_NOISE_WORKER_DIRECTORY")!;
            var value = Environment.GetEnvironmentVariable("THALOVANT_NOISE_WORKER_VALUE")!;
            if (operation == "pin-race") {
                File.WriteAllText(Path.Combine(directory, "ready-" + value), "ready");
                var limit = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(directory, "start"))) {
                    if (limit.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("Pin race was not started.");
                    Thread.Sleep(10);
                }
                var pin = Enumerable.Repeat(byte.Parse(value), 32).ToArray();
                string result;
                try { new HiveMindFileNoiseStore(directory).VerifyOrPin("hub", pin); result = Noise.Hex(pin); }
                catch (CryptographicException) { result = "conflict"; }
                File.WriteAllText(Path.Combine(directory, "result-" + value), result);
                return;
            }
            var store = new HiveMindFileNoiseStore(directory, (stream, bytes) => {
                stream.Write(bytes, 0, value == "full" ? bytes.Length : bytes.Length / 2);
                stream.Flush(true);
                File.WriteAllText(Path.Combine(directory, "staged"), "staged");
                Thread.Sleep(Timeout.Infinite);
            });
            if (operation == "crash-pin") store.VerifyOrPin("hub", Pin); else store.LoadOrCreateStaticKey();
            throw new InvalidOperationException("Writer must be killed before returning.");
        }

        private static void VerifyFreshStoreRecovers(string directory, bool pin)
        {
            var store = new HiveMindFileNoiseStore(directory);
            if (pin) {
                Assert.Null(store.LoadPin("hub"));
                store.VerifyOrPin("hub", Pin);
                Assert.Equal(Pin, new HiveMindFileNoiseStore(directory).LoadPin("hub"));
                Assert.Throws<CryptographicException>(() => store.VerifyOrPin("hub", new byte[32]));
                Assert.Equal(Pin, store.LoadPin("hub"));
            } else {
                var key = store.LoadOrCreateStaticKey();
                Assert.Equal(32, key.Length);
                Assert.Equal(key, new HiveMindFileNoiseStore(directory).LoadOrCreateStaticKey());
            }
            AssertPrivateFile(KeyPath(directory, pin));
        }

        private static void AssertPrivateFile(string path)
        {
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        private static Process StartWorker(string directory, string operation, string value)
        {
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("vstest"); start.ArgumentList.Add(typeof(NoiseStoreTests).Assembly.Location);
            start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=Thalovant.Tests.NoiseStoreTests.StoreProcessWorker");
            start.Environment["THALOVANT_NOISE_WORKER_DIRECTORY"] = directory;
            start.Environment["THALOVANT_NOISE_WORKER_OPERATION"] = operation;
            start.Environment["THALOVANT_NOISE_WORKER_VALUE"] = value;
            return Process.Start(start)!;
        }

        private static async Task WaitFor(Func<bool> condition, Process? child = null)
        {
            var limit = Stopwatch.StartNew();
            while (!condition()) {
                if (child?.HasExited == true) throw new InvalidOperationException("Store worker exited before staging a write.");
                if (limit.Elapsed > TimeSpan.FromSeconds(25)) throw new TimeoutException("Store worker did not reach its checkpoint.");
                await Task.Delay(10);
            }
        }
    }
}
