using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Thalovant.Internal.BouncyCastle.Crypto.Generators;
using Thalovant.Internal.BouncyCastle.Crypto.Parameters;
using Thalovant.Internal.BouncyCastle.Math.EC.Rfc7748;

namespace Thalovant
{
    // Noise revision 34. Primitive source provenance is in THIRD-PARTY-NOTICES.md.
    internal static class Noise
    {
        internal const string Suite = "25519_AESGCM_SHA256";
        internal static readonly byte[] Empty = Array.Empty<byte>();
        internal static byte[] RandomKey() { var key = new byte[32]; using var random = RandomNumberGenerator.Create(); random.GetBytes(key); return key; }
        internal static byte[] Hash(byte[] data) { using var hash = SHA256.Create(); return hash.ComputeHash(data); }
        internal static byte[] PublicKey(byte[] key) { if (key.Length != 32) throw new ArgumentException("Noise key must be 32 bytes."); var result = new byte[32]; X25519.ScalarMultBase(key, 0, result, 0); return result; }
        internal static byte[] Dh(byte[] key, byte[] remote)
        {
            if (key.Length != 32 || remote.Length != 32) throw new ArgumentException("Noise key must be 32 bytes.");
            var result = new byte[32];
            if (!X25519.CalculateAgreement(key, 0, remote, 0, result, 0)) throw new CryptographicException("Invalid Noise peer public key.");
            return result;
        }
        internal static byte[] DerivePsk(string password, string nodeId)
        {
            var parameters = new Argon2Parameters.Builder(Argon2Parameters.Argon2id).WithVersion(Argon2Parameters.Version13)
                .WithIterations(3).WithMemoryAsKB(65536).WithParallelism(1).WithSalt(Hash(Encoding.UTF8.GetBytes(nodeId))).Build();
            var generator = new Argon2BytesGenerator(); generator.Init(parameters);
            var output = new byte[32]; var bytes = Encoding.UTF8.GetBytes(password);
            try { generator.GenerateBytes(bytes, output); return output; }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            foreach (var c in value) switch (c) {
                case '"': result.Append("\\\""); break;
                case '\\': result.Append("\\\\"); break;
                case '\b': result.Append("\\b"); break;
                case '\f': result.Append("\\f"); break;
                case '\n': result.Append("\\n"); break;
                case '\r': result.Append("\\r"); break;
                case '\t': result.Append("\\t"); break;
                default: if (c < 32) result.Append("\\u" + ((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)); else result.Append(c); break;
            }
            return result.Append('"').ToString();
        }
        private static int CompareKeys(string left, string right)
        {
            int a = 0, b = 0;
            while (a < left.Length && b < right.Length) {
                int ca = char.ConvertToUtf32(left, a), cb = char.ConvertToUtf32(right, b);
                if (ca != cb) return ca.CompareTo(cb);
                a += ca > 65535 ? 2 : 1; b += cb > 65535 ? 2 : 1;
            }
            return (left.Length - a).CompareTo(right.Length - b);
        }
        internal static string Canonical(JsonNode? node)
        {
            if (node is JsonObject obj) return "{" + string.Join(",", obj.OrderBy(pair => pair.Key, Comparer<string>.Create(CompareKeys))
                .Select(pair => Quote(pair.Key) + ":" + Canonical(pair.Value))) + "}";
            if (node is JsonArray array) return "[" + string.Join(",", array.Select(Canonical)) + "]";
            if (node is JsonValue value && value.TryGetValue<string>(out var text)) return Quote(text);
            return node?.ToJsonString() ?? "null";
        }
        internal static byte[] Prologue(JsonObject hello, JsonObject offer, string name) => new UTF8Encoding(false, true).GetBytes(Canonical(hello) + Canonical(offer) + name);
        internal static byte[] Join(params byte[][] values)
        {
            var result = new byte[values.Sum(v => v.Length)]; var offset = 0;
            foreach (var value in values) { Buffer.BlockCopy(value, 0, result, offset, value.Length); offset += value.Length; } return result;
        }
        internal static byte[] Slice(byte[] data, int offset, int length) { var result = new byte[length]; Buffer.BlockCopy(data, offset, result, 0, length); return result; }
        internal static byte[] Hmac(byte[] key, byte[] input) { using var hmac = new HMACSHA256(key); return hmac.ComputeHash(input); }
        internal static byte[][] Hkdf(byte[] key, byte[] data, int count = 2)
        {
            var temp = Hmac(key, data); var result = new byte[count][]; var previous = Empty;
            for (var i = 0; i < count; i++) result[i] = previous = Hmac(temp, Join(previous, new[] { (byte)(i + 1) }));
            return result;
        }
        internal static bool Equal(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            var difference = 0; for (var i = 0; i < left.Length; i++) difference |= left[i] ^ right[i]; return difference == 0;
        }
        internal static string Hex(byte[] data) => ThalovantCrypto.HexEncode(data);
        internal static byte[] Unhex(string data) => ThalovantCrypto.HexDecode(data) ?? throw new ThalovantConnectionException("Invalid Noise hexadecimal value.");
        internal static void Write64(byte[] value, int offset, ulong number) { for (var i = 0; i < 8; i++) value[offset + 7 - i] = (byte)(number >> (8 * i)); }

        // AES-256 from the platform provider, GCM from the existing in-tree SP800-38D
        // primitive. AESGCM Noise nonces use a big-endian 64-bit counter after 4 zero bytes.
        internal static byte[] Aead(byte[] key, ulong counter, byte[] ad, byte[] input, bool encrypt)
        {
            if (key.Length != 32 || (!encrypt && input.Length < 16)) throw new CryptographicException("Invalid Noise AEAD input.");
            using var aes = Aes.Create(); aes.Key = key; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
            using var cipher = aes.CreateEncryptor();
            byte[] Block(byte[] block) { var output = new byte[16]; cipher.TransformBlock(block, 0, 16, output, 0); return output; }
            var h = Block(new byte[16]); var j0 = new byte[16]; Write64(j0, 4, counter); j0[15] = 1;
            var length = encrypt ? input.Length : input.Length - 16;
            var result = new byte[length]; var ctr = (byte[])j0.Clone();
            for (var offset = 0; offset < length; offset += 16)
            {
                for (var n = 15; n >= 12; n--) { if (++ctr[n] != 0) break; }
                var stream = Block(ctr); for (var i = 0; i < Math.Min(16, length - offset); i++) result[offset + i] = (byte)(input[offset + i] ^ stream[i]);
            }
            var ciphertext = encrypt ? result : Slice(input, 0, length);
            var paddedAd = (ad.Length + 15) / 16 * 16; var paddedCipher = (length + 15) / 16 * 16;
            var auth = new byte[paddedAd + paddedCipher + 16]; Buffer.BlockCopy(ad, 0, auth, 0, ad.Length);
            Buffer.BlockCopy(ciphertext, 0, auth, paddedAd, length); Write64(auth, auth.Length - 16, (ulong)ad.Length * 8); Write64(auth, auth.Length - 8, (ulong)length * 8);
            var tag = AesGcm128.Ghash(h, auth); var mask = Block(j0); for (var i = 0; i < 16; i++) tag[i] ^= mask[i];
            if (encrypt) return Join(result, tag);
            if (!Equal(tag, Slice(input, length, 16))) { Array.Clear(result, 0, result.Length); throw new CryptographicException("Noise authentication failed."); }
            return result;
        }
    }

    internal sealed class NoiseCipher
    {
        private readonly byte[]? _key; private ulong _counter;
        internal NoiseCipher(byte[]? key = null) { _key = key; }
        internal bool HasKey => _key != null;
        internal byte[] Crypt(byte[] input, byte[]? ad = null, bool encrypt = true)
        {
            if (_key == null) return input;
            if (_counter == ulong.MaxValue) throw new CryptographicException("Noise nonce exhausted.");
            var result = Noise.Aead(_key, _counter, ad ?? Noise.Empty, input, encrypt); _counter++; return result;
        }
    }
    internal sealed class NoiseSymmetric
    {
        private byte[] _hash; private byte[] _chainingKey; internal NoiseCipher Cipher = new NoiseCipher();
        internal NoiseSymmetric(string name) { var bytes = Encoding.UTF8.GetBytes(name); _hash = bytes.Length <= 32 ? Noise.Join(bytes, new byte[32 - bytes.Length]) : Noise.Hash(bytes); _chainingKey = (byte[])_hash.Clone(); }
        internal void MixHash(byte[] data) { _hash = Noise.Hash(Noise.Join(_hash, data)); }
        internal void MixKey(byte[] data) { var keys = Noise.Hkdf(_chainingKey, data); _chainingKey = keys[0]; Cipher = new NoiseCipher(keys[1]); }
        internal void MixKeyAndHash(byte[] data) { var keys = Noise.Hkdf(_chainingKey, data, 3); _chainingKey = keys[0]; MixHash(keys[1]); Cipher = new NoiseCipher(keys[2]); }
        internal byte[] Crypt(byte[] data, bool encrypt) { var result = Cipher.Crypt(data, _hash, encrypt); MixHash(encrypt ? result : data); return result; }
        internal (NoiseCipher, NoiseCipher) Split() { var keys = Noise.Hkdf(_chainingKey, Noise.Empty); return (new NoiseCipher(keys[0]), new NoiseCipher(keys[1])); }
    }
    internal sealed class NoiseHandshake
    {
        internal string Pattern { get; }
        private readonly string[][] _messages; private readonly NoiseSymmetric _symmetric;
        private readonly byte[] _psk, _staticKey; private readonly bool _initiator; private readonly Func<byte[]> _generateEphemeral;
        private byte[]? _ephemeral, _remoteEphemeral; internal byte[]? RemoteStatic { get; private set; }
        private int _index;
        internal bool Finished => _index == _messages.Length;
        internal NoiseHandshake(string pattern, byte[] psk, byte[] prologue, byte[] staticKey, byte[]? remote = null, bool initiator = true, Func<byte[]>? generateEphemeral = null)
        {
            if (psk.Length != 32 || staticKey.Length != 32 || (remote != null && remote.Length != 32)) throw new ArgumentException("Invalid Noise key length.");
            Pattern = pattern; _psk = psk; _staticKey = staticKey; RemoteStatic = remote; _initiator = initiator; _generateEphemeral = generateEphemeral ?? Noise.RandomKey;
            _messages = pattern == "XXpsk2" ? new[] { new[] { "e" }, new[] { "e", "ee", "s", "es", "psk" }, new[] { "s", "se" } }
                : pattern == "KKpsk0" ? new[] { new[] { "psk", "e", "es", "ss" }, new[] { "e", "ee", "se" } } : throw new ArgumentException("Unsupported Noise pattern.");
            _symmetric = new NoiseSymmetric("Noise_" + pattern + "_" + Noise.Suite); _symmetric.MixHash(prologue);
            if (pattern == "KKpsk0") { if (remote == null) throw new ArgumentException("KKpsk0 needs a persisted peer key."); _symmetric.MixHash(initiator ? Noise.PublicKey(staticKey) : remote); _symmetric.MixHash(initiator ? remote : Noise.PublicKey(staticKey)); }
        }
        internal byte[] Write(byte[]? payload = null)
        {
            if (Finished || ((_index % 2 == 0) != _initiator)) throw new ThalovantConnectionException("Unexpected Noise write.");
            var output = Noise.Empty;
            foreach (var token in _messages[_index]) switch (token)
            {
                case "e": _ephemeral = _generateEphemeral(); var pub = Noise.PublicKey(_ephemeral); output = Noise.Join(output, pub); _symmetric.MixHash(pub); _symmetric.MixKey(pub); break;
                case "s": output = Noise.Join(output, _symmetric.Crypt(Noise.PublicKey(_staticKey), true)); break;
                case "psk": _symmetric.MixKeyAndHash(_psk); break;
                default: _symmetric.MixKey(Dh(token)); break;
            }
            output = Noise.Join(output, _symmetric.Crypt(payload ?? Noise.Empty, true)); _index++; return output;
        }
        internal byte[] Read(byte[] message)
        {
            if (Finished || ((_index % 2 == 0) == _initiator) || message.Length > 65535) throw new ThalovantConnectionException("Unexpected Noise read.");
            var offset = 0;
            byte[] Take(int size) { if (offset > message.Length - size) throw new ThalovantConnectionException("Truncated Noise handshake."); var result = Noise.Slice(message, offset, size); offset += size; return result; }
            foreach (var token in _messages[_index]) switch (token)
            {
                case "e": _remoteEphemeral = Take(32); _symmetric.MixHash(_remoteEphemeral); _symmetric.MixKey(_remoteEphemeral); break;
                case "s": var key = _symmetric.Crypt(Take(_symmetric.Cipher.HasKey ? 48 : 32), false); if (RemoteStatic != null && !Noise.Equal(key, RemoteStatic)) throw new CryptographicException("Noise server key contradicts its persisted pin."); RemoteStatic = key; break;
                case "psk": _symmetric.MixKeyAndHash(_psk); break;
                default: _symmetric.MixKey(Dh(token)); break;
            }
            var payload = _symmetric.Crypt(Take(message.Length - offset), false); _index++; return payload;
        }
        private byte[] Dh(string token) => token switch {
            "ee" => Noise.Dh(_ephemeral!, _remoteEphemeral!),
            "es" => _initiator ? Noise.Dh(_ephemeral!, RemoteStatic!) : Noise.Dh(_staticKey, _remoteEphemeral!),
            "se" => _initiator ? Noise.Dh(_staticKey, _remoteEphemeral!) : Noise.Dh(_ephemeral!, RemoteStatic!),
            "ss" => Noise.Dh(_staticKey, RemoteStatic!),
            _ => throw new ThalovantConnectionException("Unknown Noise token.") };
        internal NoiseSession Session() { if (!Finished) throw new ThalovantConnectionException("Noise handshake incomplete."); var (a, b) = _symmetric.Split(); return _initiator ? new NoiseSession(a, b) : new NoiseSession(b, a); }
    }
    internal sealed class NoiseSession
    {
        internal const int Chunk = 65000, MaximumMessage = 32 * 1024 * 1024;
        private readonly NoiseCipher _send, _receive; private MemoryStream? _pending; private bool _pendingJson;
        internal NoiseSession(NoiseCipher send, NoiseCipher receive) { _send = send; _receive = receive; }
        internal List<byte[]> Encrypt(byte[] payload, bool json = true)
        {
            if (payload.Length > MaximumMessage) throw new ThalovantConnectionException("Noise message exceeds 32 MiB.");
            var output = new List<byte[]>();
            if (payload.Length <= Chunk) { output.Add(_send.Crypt(Noise.Join(new[] { (byte)(json ? 0 : 1) }, payload))); return output; }
            for (var offset = 0; offset < payload.Length; offset += Chunk) {
                var marker = offset == 0 ? (json ? 2 : 3) : offset + Chunk >= payload.Length ? 5 : 4;
                output.Add(_send.Crypt(Noise.Join(new[] { (byte)marker }, Noise.Slice(payload, offset, Math.Min(Chunk, payload.Length - offset)))));
            }
            return output;
        }
        internal (byte[] Data, bool Json)? Decrypt(byte[] frame)
        {
            if (frame.Length < 17 || frame.Length > 65535) throw new ThalovantConnectionException("Invalid Noise frame length.");
            var plain = _receive.Crypt(frame, encrypt: false); if (plain.Length == 0) throw new ThalovantConnectionException("Empty Noise frame.");
            var marker = plain[0];
            switch (marker) {
                case 0: case 1: if (_pending != null) throw new ThalovantConnectionException("Interrupted Noise chunk sequence."); return (Noise.Slice(plain, 1, plain.Length - 1), marker == 0);
                case 2: case 3: if (_pending != null) throw new ThalovantConnectionException("Overlapping Noise chunk sequences."); _pending = new MemoryStream(); _pendingJson = marker == 2; break;
                case 4: case 5: if (_pending == null) throw new ThalovantConnectionException("Unexpected Noise continuation."); break;
                default: throw new ThalovantConnectionException("Unknown Noise frame marker.");
            }
            if (_pending!.Length + plain.Length - 1 > MaximumMessage) throw new ThalovantConnectionException("Noise reassembly exceeds 32 MiB.");
            _pending.Write(plain, 1, plain.Length - 1);
            if (marker != 5) return null;
            var data = _pending.ToArray(); _pending.Dispose(); _pending = null; return (data, _pendingJson);
        }
    }
}
