using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Thalovant.Tests
{
    public sealed class NoiseTests
    {
        private readonly JsonObject _fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "noise-node.json")))!.AsObject();
        private byte[] Key(string name) => Noise.Unhex(_fixture[name]!.GetValue<string>());
        [Fact] public void ReproducesIndependentNodeHandshakeAndTransportTranscripts()
        {
            Assert.Equal(_fixture["public_i"]!.GetValue<string>(), Noise.Hex(Noise.PublicKey(Key("static_i"))));
            Assert.Equal(_fixture["public_r"]!.GetValue<string>(), Noise.Hex(Noise.PublicKey(Key("static_r"))));
            foreach (var exchange in _fixture["exchanges"]!.AsArray().Select(v => v!.AsObject()).Where(e => e["suite"]!.GetValue<string>() == Noise.Suite)) {
                var pattern = exchange["pattern"]!.GetValue<string>();
                var prologue = Noise.Prologue(_fixture["hello"]!.AsObject(), _fixture["offer"]!.AsObject(), exchange["protocol"]!.GetValue<string>());
                Assert.Equal(exchange["prologue"]!.GetValue<string>(), Noise.Hex(prologue));
                var state = new NoiseHandshake(pattern, Key("psk"), prologue, Key("static_i"), Key("public_r"), generateEphemeral: () => Key("ephemeral_i"));
                var messages = exchange["messages"]!.AsArray(); var payloads = exchange["payloads"]!.AsArray();
                Assert.Equal(messages[0]!.GetValue<string>(), Noise.Hex(state.Write(Encoding.UTF8.GetBytes(payloads[0]!.GetValue<string>()))));
                Assert.Equal(payloads[1]!.GetValue<string>(), Encoding.UTF8.GetString(state.Read(Noise.Unhex(messages[1]!.GetValue<string>()))));
                if (pattern == "XXpsk2") Assert.Equal(messages[2]!.GetValue<string>(), Noise.Hex(state.Write()));
                Assert.True(state.Finished);
                var session = state.Session(); var texts = exchange["plaintexts"]!.AsArray();
                for (var i = 0; i < texts.Count; i++) {
                    var text = texts[i]!.GetValue<string>();
                    Assert.Equal(exchange["transport_i"]![i]!.GetValue<string>(), Noise.Hex(session.Encrypt(Encoding.UTF8.GetBytes(text)).Single()));
                    var result = session.Decrypt(Noise.Unhex(exchange["transport_r"]![i]!.GetValue<string>()));
                    Assert.True(result!.Value.Json); Assert.Equal(text, Encoding.UTF8.GetString(result.Value.Data));
                }
                Assert.Throws<CryptographicException>(() => session.Decrypt(Noise.Unhex(exchange["transport_r"]![0]!.GetValue<string>())));
            }
        }
        [Fact] public void CanonicalJsonMatchesPythonUnicodeAndControlEscapes()
        {
            var value = new JsonObject { ["\uE000"] = "café\u001f", ["\U00010000"] = "\U0001f600" };
            Assert.Equal("{\"\uE000\":\"café\\u001f\",\"\U00010000\":\"\U0001f600\"}", Noise.Canonical(value));
        }
        [Fact] public void Argon2AndNonzeroNonceMatchUpstreamVectors()
        {
            Assert.Equal(_fixture["psk"]!.GetValue<string>(), Noise.Hex(Noise.DerivePsk(_fixture["password"]!.GetValue<string>(), _fixture["node_id"]!.GetValue<string>())));
            Assert.Equal("988418601dbad183fbd6116e7981e9ab8ffe93be3f3f45c27eb0b70c325f9cd8", Noise.Hex(Noise.DerivePsk("passé-wörd", "hub-ümläut")));
            Assert.Equal("634779e501b1642347721a75d47243559573cbfac3370330645b209d163adf1c04f90a6a4bc2", Noise.Hex(Noise.Aead(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), 258, Noise.Empty, Encoding.UTF8.GetBytes("noise-transport-vector"), true)));
        }
        [Fact] public void InvalidPasswordTranscriptAndPinsFailAuthentication()
        {
            var exchange = _fixture["exchanges"]!.AsArray().Select(v => v!.AsObject()).First(e => e["suite"]!.GetValue<string>() == Noise.Suite && e["pattern"]!.GetValue<string>() == "XXpsk2");
            for (var test = 0; test < 3; test++) {
                var state = new NoiseHandshake("XXpsk2", test == 0 ? new byte[32] : Key("psk"), test == 1 ? Encoding.UTF8.GetBytes("tampered") : Noise.Unhex(exchange["prologue"]!.GetValue<string>()), Key("static_i"), test == 2 ? Key("public_i") : Key("public_r"), generateEphemeral: () => Key("ephemeral_i"));
                state.Write(Encoding.UTF8.GetBytes(exchange["payloads"]![0]!.GetValue<string>()));
                Assert.Throws<CryptographicException>(() => state.Read(Noise.Unhex(exchange["messages"]![1]!.GetValue<string>())));
                Assert.False(state.Finished);
            }
            Assert.Throws<CryptographicException>(() => Noise.Dh(Key("static_i"), new byte[32]));
        }
        [Fact] public void ChunkingIsBoundedAndRejectsMissingFirstChunk()
        {
            var key = Key("psk"); var sender = new NoiseSession(new NoiseCipher(key), new NoiseCipher(key)); var receiver = new NoiseSession(new NoiseCipher(key), new NoiseCipher(key));
            var payload = Enumerable.Range(0, 130100).Select(i => (byte)(i % 251)).ToArray(); var frames = sender.Encrypt(payload);
            Assert.Equal(3, frames.Count); Assert.Null(receiver.Decrypt(frames[0])); Assert.Null(receiver.Decrypt(frames[1])); Assert.Equal(payload, receiver.Decrypt(frames[2])!.Value.Data);
            Assert.Throws<ThalovantConnectionException>(() => sender.Encrypt(new byte[NoiseSession.MaximumMessage + 1]));
            var raw = new NoiseCipher(key); var malformed = new NoiseSession(new NoiseCipher(key), new NoiseCipher(key));
            Assert.Throws<ThalovantConnectionException>(() => malformed.Decrypt(raw.Crypt(new byte[] { 4, 1 })));
        }
        [Fact] public async Task IndependentProcessesPublishAndReadOneCompleteStaticKey()
        {
            var parent = Path.Combine(Path.GetTempPath(), "dotnet-noise-process-" + Guid.NewGuid());
            var dir = Path.Combine(parent, "private");
            Directory.CreateDirectory(parent);
            try {
                var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
                var children = Enumerable.Range(0, 4).Select(_ => {
                    var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true };
                    start.ArgumentList.Add("vstest"); start.ArgumentList.Add(typeof(NoiseTests).Assembly.Location);
                    start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=Thalovant.Tests.NoiseTests.ConcurrentStoreWorker");
                    start.Environment["THALOVANT_NOISE_TEST_DIRECTORY"] = dir;
                    return Process.Start(start)!;
                }).ToArray();
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                foreach (var child in children) {
                    var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
                    await child.WaitForExitAsync(limit.Token);
                    Assert.True(child.ExitCode == 0, (await output) + (await error)); child.Dispose();
                }
                var keys = Directory.GetFiles(dir, "result-*").Select(File.ReadAllText).ToArray();
                Assert.Equal(4, keys.Length); Assert.Single(keys.Distinct()); Assert.Equal(64, keys[0].Length);
            } finally { Directory.Delete(parent, true); }
        }
        [Fact] public void ConcurrentStoreWorker()
        {
            var dir = Environment.GetEnvironmentVariable("THALOVANT_NOISE_TEST_DIRECTORY");
            if (dir == null) return;
            var store = new HiveMindFileNoiseStore(dir); var key = store.LoadOrCreateStaticKey();
            for (var i = 0; i < 20; i++) Assert.Equal(key, store.LoadOrCreateStaticKey());
            File.WriteAllText(Path.Combine(dir, "result-" + Guid.NewGuid()), Noise.Hex(key));
        }
        [Fact] public void FileStorePreservesKeysAndNeverOverwritesAConflictingPin()
        {
            var parent = Path.Combine(Path.GetTempPath(), "thalovant-noise-test-" + Guid.NewGuid()); var dir = Path.Combine(parent, "private");
            try {
                var first = new HiveMindFileNoiseStore(dir); var second = new HiveMindFileNoiseStore(dir);
                Assert.Equal(first.LoadOrCreateStaticKey(), second.LoadOrCreateStaticKey());
                first.VerifyOrPin("hub", Key("public_r")); Assert.Equal(Key("public_r"), second.LoadPin("hub"));
                Assert.Throws<CryptographicException>(() => second.VerifyOrPin("hub", Key("public_i")));
                Assert.Equal(Key("public_r"), first.LoadPin("hub"));
                File.WriteAllText(Path.Combine(dir, "noise-static.key"), "bad");
                Assert.Throws<ThalovantConnectionException>(() => first.LoadOrCreateStaticKey());
            } finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
        }
    }
}
