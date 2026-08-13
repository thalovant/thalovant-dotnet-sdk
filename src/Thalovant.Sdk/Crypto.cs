using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Thalovant
{
    /// <summary>
    /// AES-128-GCM primitives compatible with the HiveMind runtime wire format.
    ///
    /// The runtime key is the first 16 characters of the identity <c>crypto_key</c>,
    /// UTF-8 encoded. Encrypted JSON frames are <c>{"ciphertext": hex, "tag": hex,
    /// "nonce": hex}</c> with a 16-byte random nonce, exactly like the Node, Go, and
    /// Swift SDKs.
    ///
    /// The cipher is implemented in pure managed code (NIST FIPS-197 AES +
    /// SP 800-38D GCM) because .NET's <c>AesGcm</c> only accepts 12-byte nonces and
    /// does not exist on netstandard2.1 (Unity), while the HiveMind wire uses a
    /// 16-byte nonce.
    /// </summary>
    public static class ThalovantCrypto
    {
        internal const int BinaryNonceSize = 16;
        internal const int AuthTagSize = 16;

        /// <summary>
        /// First 16 characters of the crypto key, encoded as UTF-8 (mirrors the
        /// sibling SDKs). Returns null for missing/blank keys.
        /// </summary>
        public static byte[]? RuntimeKey(string? raw)
        {
            var normalized = raw?.Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                return null;
            }
            var prefix = normalized!.Length <= 16 ? normalized : normalized.Substring(0, 16);
            return Encoding.UTF8.GetBytes(prefix);
        }

        /// <summary>Encrypts a plaintext into the JSON envelope used on WSS frames.</summary>
        public static string EncryptJson(string key, string plaintext)
        {
            var runtimeKey = RuntimeKey(key);
            if (runtimeKey is null || runtimeKey.Length != 16)
            {
                throw new ThalovantConnectionException("Missing or invalid crypto key.");
            }
            var nonce = new byte[BinaryNonceSize];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(nonce);
            }
            var ciphertext = AesGcm128.Seal(runtimeKey, nonce, Encoding.UTF8.GetBytes(plaintext), out var tag);
            var envelope = new JsonObject
            {
                ["ciphertext"] = HexEncode(ciphertext),
                ["tag"] = HexEncode(tag),
                ["nonce"] = HexEncode(nonce),
            };
            return envelope.ToJsonString();
        }

        /// <summary>
        /// Decrypts the JSON envelope used on WSS frames. Field values may be hex
        /// (the SDK default) or base64; the encoding is detected from the nonce.
        /// </summary>
        public static string DecryptJson(string key, JsonObject envelope)
        {
            var runtimeKey = RuntimeKey(key);
            if (runtimeKey is null || runtimeKey.Length != 16)
            {
                throw new ThalovantConnectionException("Missing or invalid crypto key.");
            }
            var nonceText = JsonUtil.GetString(envelope["nonce"]) ?? "";
            var tagText = JsonUtil.GetString(envelope["tag"]) ?? "";
            var ciphertextText = JsonUtil.GetString(envelope["ciphertext"]) ?? "";
            var useHex = IsHexEncodedNonce(nonceText);
            var nonce = useHex ? HexDecode(nonceText) : Base64Decode(nonceText);
            var tag = useHex ? HexDecode(tagText) : Base64Decode(tagText);
            var ciphertext = useHex ? HexDecode(ciphertextText) : Base64Decode(ciphertextText);
            if (nonce is null || tag is null || ciphertext is null)
            {
                throw new ThalovantConnectionException("Invalid encrypted payload encoding.");
            }
            var plaintext = AesGcm128.Open(runtimeKey, nonce, ciphertext, tag);
            if (plaintext is null)
            {
                throw new ThalovantConnectionException("Failed to decrypt HiveMind payload (bad key or corrupt frame).");
            }
            try
            {
                return Encoding.UTF8.GetString(plaintext);
            }
            catch (Exception)
            {
                throw new ThalovantConnectionException("Decrypted HiveMind payload is not valid UTF-8.");
            }
        }

        internal static bool IsHexEncodedNonce(string value)
        {
            if (value.Length == 0 || value.Length % 2 != 0)
            {
                return false;
            }
            foreach (var character in value)
            {
                var isHexDigit = (character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F');
                if (!isHexDigit)
                {
                    return false;
                }
            }
            var byteCount = value.Length / 2;
            return byteCount == BinaryNonceSize || byteCount == 12;
        }

        internal static string HexEncode(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes)
            {
                builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        internal static byte[]? HexDecode(string text)
        {
            if (text.Length % 2 != 0)
            {
                return null;
            }
            var bytes = new byte[text.Length / 2];
            for (var index = 0; index < bytes.Length; index++)
            {
                var high = HexNibble(text[index * 2]);
                var low = HexNibble(text[index * 2 + 1]);
                if (high < 0 || low < 0)
                {
                    return null;
                }
                bytes[index] = (byte)((high << 4) | low);
            }
            return bytes;
        }

        private static int HexNibble(char character)
        {
            if (character >= '0' && character <= '9')
            {
                return character - '0';
            }
            if (character >= 'a' && character <= 'f')
            {
                return character - 'a' + 10;
            }
            if (character >= 'A' && character <= 'F')
            {
                return character - 'A' + 10;
            }
            return -1;
        }

        internal static byte[]? Base64Decode(string text)
        {
            try
            {
                return Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Minimal AES-128-GCM (encrypt-only AES core; GCM per NIST SP 800-38D,
    /// supporting arbitrary nonce lengths including the 16-byte HiveMind nonce).
    /// </summary>
    internal static class AesGcm128
    {
        internal static byte[] Seal(byte[] key, byte[] nonce, byte[] plaintext, out byte[] tag)
        {
            var roundKeys = Aes128.ExpandKey(key);
            var hashKey = Aes128.EncryptBlock(new byte[16], roundKeys);
            var j0 = InitialCounter(nonce, hashKey);
            var ciphertext = CounterModeCrypt(plaintext, roundKeys, Increment32(j0));
            tag = ComputeTag(hashKey, j0, ciphertext, roundKeys);
            return ciphertext;
        }

        internal static byte[]? Open(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
        {
            if (tag.Length != 16)
            {
                return null;
            }
            var roundKeys = Aes128.ExpandKey(key);
            var hashKey = Aes128.EncryptBlock(new byte[16], roundKeys);
            var j0 = InitialCounter(nonce, hashKey);
            var expected = ComputeTag(hashKey, j0, ciphertext, roundKeys);
            var difference = 0;
            for (var index = 0; index < 16; index++)
            {
                difference |= expected[index] ^ tag[index];
            }
            if (difference != 0)
            {
                return null;
            }
            return CounterModeCrypt(ciphertext, roundKeys, Increment32(j0));
        }

        private static byte[] InitialCounter(byte[] nonce, byte[] hashKey)
        {
            if (nonce.Length == 12)
            {
                var counter = new byte[16];
                Array.Copy(nonce, counter, 12);
                counter[15] = 1;
                return counter;
            }
            var padding = (16 - nonce.Length % 16) % 16;
            var input = new byte[nonce.Length + padding + 16];
            Array.Copy(nonce, input, nonce.Length);
            WriteLengthBlock(input, input.Length - 8, (ulong)nonce.Length * 8);
            return Ghash(hashKey, input);
        }

        private static byte[] ComputeTag(byte[] hashKey, byte[] j0, byte[] ciphertext, byte[][] roundKeys)
        {
            // No additional authenticated data is used on this wire.
            var padding = (16 - ciphertext.Length % 16) % 16;
            var input = new byte[ciphertext.Length + padding + 16];
            Array.Copy(ciphertext, input, ciphertext.Length);
            WriteLengthBlock(input, input.Length - 16, 0UL); // AAD length
            WriteLengthBlock(input, input.Length - 8, (ulong)ciphertext.Length * 8);
            var hash = Ghash(hashKey, input);
            var keystream = Aes128.EncryptBlock(j0, roundKeys);
            var tag = new byte[16];
            for (var index = 0; index < 16; index++)
            {
                tag[index] = (byte)(hash[index] ^ keystream[index]);
            }
            return tag;
        }

        private static byte[] CounterModeCrypt(byte[] input, byte[][] roundKeys, byte[] initialCounter)
        {
            var output = new byte[input.Length];
            var counter = initialCounter;
            var offset = 0;
            while (offset < input.Length)
            {
                var keystream = Aes128.EncryptBlock(counter, roundKeys);
                var chunk = Math.Min(16, input.Length - offset);
                for (var index = 0; index < chunk; index++)
                {
                    output[offset + index] = (byte)(input[offset + index] ^ keystream[index]);
                }
                counter = Increment32(counter);
                offset += chunk;
            }
            return output;
        }

        private static byte[] Increment32(byte[] block)
        {
            var result = (byte[])block.Clone();
            var carry = 1;
            for (var index = 15; index >= 12; index--)
            {
                var sum = result[index] + carry;
                result[index] = (byte)(sum & 0xFF);
                carry = sum >> 8;
            }
            return result;
        }

        private static void WriteLengthBlock(byte[] destination, int offset, ulong bitCount)
        {
            for (var index = 0; index < 8; index++)
            {
                destination[offset + 7 - index] = (byte)((bitCount >> (index * 8)) & 0xFF);
            }
        }

        /// <summary>GHASH over GF(2^128) with the reduction polynomial from SP 800-38D.</summary>
        private static byte[] Ghash(byte[] hashKey, byte[] input)
        {
            ToWords(hashKey, 0, out var hHigh, out var hLow);
            ulong yHigh = 0;
            ulong yLow = 0;
            for (var offset = 0; offset < input.Length; offset += 16)
            {
                ToWords(input, offset, out var blockHigh, out var blockLow);
                yHigh ^= blockHigh;
                yLow ^= blockLow;
                GfMultiply(yHigh, yLow, hHigh, hLow, out yHigh, out yLow);
            }
            return FromWords(yHigh, yLow);
        }

        /// <summary>Bitwise GF(2^128) multiplication (X * Y) per SP 800-38D algorithm 1.</summary>
        private static void GfMultiply(ulong xHigh, ulong xLow, ulong yHigh, ulong yLow, out ulong zHighOut, out ulong zLowOut)
        {
            ulong zHigh = 0;
            ulong zLow = 0;
            var vHigh = yHigh;
            var vLow = yLow;
            for (var bitIndex = 0; bitIndex < 128; bitIndex++)
            {
                var bit = bitIndex < 64
                    ? (xHigh >> (63 - bitIndex)) & 1
                    : (xLow >> (63 - (bitIndex - 64))) & 1;
                if (bit == 1)
                {
                    zHigh ^= vHigh;
                    zLow ^= vLow;
                }
                var lsb = vLow & 1;
                vLow = (vLow >> 1) | (vHigh << 63);
                vHigh >>= 1;
                if (lsb == 1)
                {
                    vHigh ^= 0xE100000000000000UL;
                }
            }
            zHighOut = zHigh;
            zLowOut = zLow;
        }

        private static void ToWords(byte[] bytes, int offset, out ulong high, out ulong low)
        {
            ulong highValue = 0;
            ulong lowValue = 0;
            for (var index = 0; index < 8; index++)
            {
                highValue = (highValue << 8) | bytes[offset + index];
                lowValue = (lowValue << 8) | bytes[offset + 8 + index];
            }
            high = highValue;
            low = lowValue;
        }

        private static byte[] FromWords(ulong high, ulong low)
        {
            var bytes = new byte[16];
            for (var index = 0; index < 8; index++)
            {
                bytes[index] = (byte)((high >> ((7 - index) * 8)) & 0xFF);
                bytes[8 + index] = (byte)((low >> ((7 - index) * 8)) & 0xFF);
            }
            return bytes;
        }
    }

    /// <summary>
    /// AES-128 block encryption (FIPS-197). Only encryption is needed: GCM uses
    /// the forward cipher for both sealing and opening.
    /// </summary>
    internal static class Aes128
    {
        private static readonly byte[] Sbox =
        {
            0x63, 0x7c, 0x77, 0x7b, 0xf2, 0x6b, 0x6f, 0xc5, 0x30, 0x01, 0x67, 0x2b, 0xfe, 0xd7, 0xab, 0x76,
            0xca, 0x82, 0xc9, 0x7d, 0xfa, 0x59, 0x47, 0xf0, 0xad, 0xd4, 0xa2, 0xaf, 0x9c, 0xa4, 0x72, 0xc0,
            0xb7, 0xfd, 0x93, 0x26, 0x36, 0x3f, 0xf7, 0xcc, 0x34, 0xa5, 0xe5, 0xf1, 0x71, 0xd8, 0x31, 0x15,
            0x04, 0xc7, 0x23, 0xc3, 0x18, 0x96, 0x05, 0x9a, 0x07, 0x12, 0x80, 0xe2, 0xeb, 0x27, 0xb2, 0x75,
            0x09, 0x83, 0x2c, 0x1a, 0x1b, 0x6e, 0x5a, 0xa0, 0x52, 0x3b, 0xd6, 0xb3, 0x29, 0xe3, 0x2f, 0x84,
            0x53, 0xd1, 0x00, 0xed, 0x20, 0xfc, 0xb1, 0x5b, 0x6a, 0xcb, 0xbe, 0x39, 0x4a, 0x4c, 0x58, 0xcf,
            0xd0, 0xef, 0xaa, 0xfb, 0x43, 0x4d, 0x33, 0x85, 0x45, 0xf9, 0x02, 0x7f, 0x50, 0x3c, 0x9f, 0xa8,
            0x51, 0xa3, 0x40, 0x8f, 0x92, 0x9d, 0x38, 0xf5, 0xbc, 0xb6, 0xda, 0x21, 0x10, 0xff, 0xf3, 0xd2,
            0xcd, 0x0c, 0x13, 0xec, 0x5f, 0x97, 0x44, 0x17, 0xc4, 0xa7, 0x7e, 0x3d, 0x64, 0x5d, 0x19, 0x73,
            0x60, 0x81, 0x4f, 0xdc, 0x22, 0x2a, 0x90, 0x88, 0x46, 0xee, 0xb8, 0x14, 0xde, 0x5e, 0x0b, 0xdb,
            0xe0, 0x32, 0x3a, 0x0a, 0x49, 0x06, 0x24, 0x5c, 0xc2, 0xd3, 0xac, 0x62, 0x91, 0x95, 0xe4, 0x79,
            0xe7, 0xc8, 0x37, 0x6d, 0x8d, 0xd5, 0x4e, 0xa9, 0x6c, 0x56, 0xf4, 0xea, 0x65, 0x7a, 0xae, 0x08,
            0xba, 0x78, 0x25, 0x2e, 0x1c, 0xa6, 0xb4, 0xc6, 0xe8, 0xdd, 0x74, 0x1f, 0x4b, 0xbd, 0x8b, 0x8a,
            0x70, 0x3e, 0xb5, 0x66, 0x48, 0x03, 0xf6, 0x0e, 0x61, 0x35, 0x57, 0xb9, 0x86, 0xc1, 0x1d, 0x9e,
            0xe1, 0xf8, 0x98, 0x11, 0x69, 0xd9, 0x8e, 0x94, 0x9b, 0x1e, 0x87, 0xe9, 0xce, 0x55, 0x28, 0xdf,
            0x8c, 0xa1, 0x89, 0x0d, 0xbf, 0xe6, 0x42, 0x68, 0x41, 0x99, 0x2d, 0x0f, 0xb0, 0x54, 0xbb, 0x16,
        };

        private static readonly byte[] RoundConstants = { 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1b, 0x36 };

        /// <summary>Expands a 16-byte key into 11 round keys of 16 bytes each.</summary>
        internal static byte[][] ExpandKey(byte[] key)
        {
            if (key.Length != 16)
            {
                throw new ArgumentException("AES-128 requires a 16-byte key.", nameof(key));
            }
            var words = new byte[44][];
            for (var wordIndex = 0; wordIndex < 4; wordIndex++)
            {
                words[wordIndex] = new[]
                {
                    key[wordIndex * 4], key[wordIndex * 4 + 1], key[wordIndex * 4 + 2], key[wordIndex * 4 + 3],
                };
            }
            for (var wordIndex = 4; wordIndex < 44; wordIndex++)
            {
                var temp = (byte[])words[wordIndex - 1].Clone();
                if (wordIndex % 4 == 0)
                {
                    temp = new[]
                    {
                        Sbox[temp[1]], Sbox[temp[2]], Sbox[temp[3]], Sbox[temp[0]],
                    };
                    temp[0] ^= RoundConstants[wordIndex / 4 - 1];
                }
                words[wordIndex] = new byte[4];
                for (var index = 0; index < 4; index++)
                {
                    words[wordIndex][index] = (byte)(words[wordIndex - 4][index] ^ temp[index]);
                }
            }
            var roundKeys = new byte[11][];
            for (var round = 0; round < 11; round++)
            {
                roundKeys[round] = new byte[16];
                for (var wordIndex = 0; wordIndex < 4; wordIndex++)
                {
                    Array.Copy(words[round * 4 + wordIndex], 0, roundKeys[round], wordIndex * 4, 4);
                }
            }
            return roundKeys;
        }

        internal static byte[] EncryptBlock(byte[] block, byte[][] roundKeys)
        {
            if (block.Length != 16)
            {
                throw new ArgumentException("AES block must be 16 bytes.", nameof(block));
            }
            var state = (byte[])block.Clone();
            AddRoundKey(state, roundKeys[0]);
            for (var round = 1; round < 10; round++)
            {
                SubBytes(state);
                ShiftRows(state);
                MixColumns(state);
                AddRoundKey(state, roundKeys[round]);
            }
            SubBytes(state);
            ShiftRows(state);
            AddRoundKey(state, roundKeys[10]);
            return state;
        }

        private static void AddRoundKey(byte[] state, byte[] roundKey)
        {
            for (var index = 0; index < 16; index++)
            {
                state[index] ^= roundKey[index];
            }
        }

        private static void SubBytes(byte[] state)
        {
            for (var index = 0; index < 16; index++)
            {
                state[index] = Sbox[state[index]];
            }
        }

        /// <summary>State is column-major: byte index = 4 * column + row.</summary>
        private static void ShiftRows(byte[] state)
        {
            var input = (byte[])state.Clone();
            for (var row = 1; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    state[4 * column + row] = input[4 * ((column + row) % 4) + row];
                }
            }
        }

        private static void MixColumns(byte[] state)
        {
            for (var column = 0; column < 4; column++)
            {
                var a0 = state[4 * column];
                var a1 = state[4 * column + 1];
                var a2 = state[4 * column + 2];
                var a3 = state[4 * column + 3];
                state[4 * column] = (byte)(Xtime(a0) ^ Xtime(a1) ^ a1 ^ a2 ^ a3);
                state[4 * column + 1] = (byte)(a0 ^ Xtime(a1) ^ Xtime(a2) ^ a2 ^ a3);
                state[4 * column + 2] = (byte)(a0 ^ a1 ^ Xtime(a2) ^ Xtime(a3) ^ a3);
                state[4 * column + 3] = (byte)(Xtime(a0) ^ a0 ^ a1 ^ a2 ^ Xtime(a3));
            }
        }

        private static byte Xtime(byte value)
        {
            return (byte)((value << 1) ^ ((value & 0x80) != 0 ? 0x1B : 0x00));
        }
    }
}
