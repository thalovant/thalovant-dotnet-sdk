using System;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    public class CryptoTests
    {
        private static byte[] Hex(string text)
        {
            return ThalovantCrypto.HexDecode(text)!;
        }

        // NIST GCM test case 1: empty plaintext, 12-byte IV.
        [Fact]
        public void NistVectorEmptyPlaintext()
        {
            var ciphertext = AesGcm128.Seal(new byte[16], new byte[12], Array.Empty<byte>(), out var tag);
            Assert.Empty(ciphertext);
            Assert.Equal("58e2fccefa7e3061367f1d57a4e7455a", ThalovantCrypto.HexEncode(tag));
        }

        // NIST GCM test case 2: one zero block, 12-byte IV.
        [Fact]
        public void NistVectorSingleBlock()
        {
            var ciphertext = AesGcm128.Seal(new byte[16], new byte[12], new byte[16], out var tag);
            Assert.Equal("0388dace60b6a392f328c2b971b2fe78", ThalovantCrypto.HexEncode(ciphertext));
            Assert.Equal("ab6e47d42cec13bdf53a67b21257bddf", ThalovantCrypto.HexEncode(tag));
        }

        // NIST GCM test case 3: four blocks, 12-byte IV, no AAD.
        [Fact]
        public void NistVectorFourBlocks()
        {
            var key = Hex("feffe9928665731c6d6a8f9467308308");
            var nonce = Hex("cafebabefacedbaddecaf888");
            var plaintext = Hex("d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b391aafd255");
            var ciphertext = AesGcm128.Seal(key, nonce, plaintext, out var tag);
            Assert.Equal(
                "42831ec2217774244b7221b784d0d49ce3aa212f2c02a4e035c17e2329aca12e21d514b25466931c7d8f6a5aac84aa051ba30b396a0aac973d58e091473f5985",
                ThalovantCrypto.HexEncode(ciphertext));
            Assert.Equal("4d5c2af327cd64a62cf35abd2ba6fab4", ThalovantCrypto.HexEncode(tag));
        }

        // The NIST test case 6 key/IV/plaintext with the AAD dropped (this wire never
        // uses AAD), exercising the GHASH-derived initial counter used for non-12-byte
        // nonces such as the 16-byte HiveMind nonce. Ciphertext matches NIST case 6;
        // the tag value was cross-checked with Node.js crypto.
        [Fact]
        public void LongNonceVector()
        {
            var key = Hex("feffe9928665731c6d6a8f9467308308");
            var nonce = Hex("9313225df88406e555909c5aff5269aa6a7a9538534f7da1e4c303d2a318a728c3c0c95156809539fcf0e2429a6b525416aedbf5a0de6a57a637b39b");
            var plaintext = Hex("d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39");
            var ciphertext = AesGcm128.Seal(key, nonce, plaintext, out var tag);
            Assert.Equal(
                "8ce24998625615b603a033aca13fb894be9112a5c3a211a8ba262a3cca7e2ca701e4a9a4fba43c90ccdcb281d48c7c6fd62875d2aca417034c34aee5",
                ThalovantCrypto.HexEncode(ciphertext));
            Assert.Equal("f64021ded4cecf71c87bb62049706692", ThalovantCrypto.HexEncode(tag));
        }

        /// <summary>
        /// Known-answer vector generated with Node.js crypto
        /// (aes-128-gcm, 16-byte nonce) — the exact configuration the Node SDK
        /// uses on the HiveMind wire. Proves cross-SDK byte-level interop.
        /// </summary>
        [Fact]
        public void NodeSdkInteropVector()
        {
            var key = Encoding.UTF8.GetBytes("0123456789abcdef");
            var nonce = Hex("000102030405060708090a0b0c0d0e0f");
            var plaintext = Encoding.UTF8.GetBytes("{\"msg_type\":\"bus\",\"payload\":{\"type\":\"speak\"}}");
            var ciphertext = AesGcm128.Seal(key, nonce, plaintext, out var tag);
            Assert.Equal(
                "eecae278f07b4787d3b62d79b68abd4a8ff45bb2414001531bef052e4bc58e1878e780fe92d226f6597a3fda42",
                ThalovantCrypto.HexEncode(ciphertext));
            Assert.Equal("9df782125f2077b8d91c96d9efecaff0", ThalovantCrypto.HexEncode(tag));

            var opened = AesGcm128.Open(key, nonce, ciphertext, tag);
            Assert.Equal(plaintext, opened);
        }

        /// <summary>
        /// Second Node.js known-answer vector (different key/nonce/plaintext),
        /// generated with createCipheriv("aes-128-gcm", key, 16-byte iv).
        /// </summary>
        [Fact]
        public void NodeSdkInteropVectorSecond()
        {
            var key = Encoding.UTF8.GetBytes("thalovant-key-16");
            var nonce = Hex("f0e1d2c3b4a5968778695a4b3c2d1e0f");
            var plaintext = Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");
            var ciphertext = AesGcm128.Seal(key, nonce, plaintext, out var tag);
            Assert.Equal(
                "1eb90797a5075e558d616d72fcf999545ab22b68a40440b64352823f87ba1edda537b1ecc96be17a976a8e",
                ThalovantCrypto.HexEncode(ciphertext));
            Assert.Equal("e00ff727ae999f753b16073124b3c0d0", ThalovantCrypto.HexEncode(tag));

            var opened = AesGcm128.Open(key, nonce, ciphertext, tag);
            Assert.Equal(plaintext, opened);
        }

        [Fact]
        public void TamperedTagOrCiphertextFails()
        {
            var key = Encoding.UTF8.GetBytes("0123456789abcdef");
            var nonce = Hex("000102030405060708090a0b0c0d0e0f");
            var ciphertext = AesGcm128.Seal(key, nonce, Encoding.UTF8.GetBytes("hello"), out var tag);
            var badTag = (byte[])tag.Clone();
            badTag[0] ^= 0x01;
            Assert.Null(AesGcm128.Open(key, nonce, ciphertext, badTag));
            var badCiphertext = (byte[])ciphertext.Clone();
            badCiphertext[0] ^= 0x01;
            Assert.Null(AesGcm128.Open(key, nonce, badCiphertext, tag));
        }

        [Fact]
        public void RuntimeKeyTruncatesToSixteenCharacters()
        {
            Assert.Equal(
                Encoding.UTF8.GetBytes("0123456789abcdef"),
                ThalovantCrypto.RuntimeKey("0123456789abcdefEXTRA-IGNORED"));
            Assert.Null(ThalovantCrypto.RuntimeKey(null));
            Assert.Null(ThalovantCrypto.RuntimeKey("   "));
        }

        [Fact]
        public void EncryptDecryptJsonEnvelopeRoundTrip()
        {
            var key = "0123456789abcdefextra";
            var plaintext = "{\"msg_type\":\"hello\",\"payload\":{}}";
            var envelopeText = ThalovantCrypto.EncryptJson(key, plaintext);
            var envelope = (JsonObject)JsonNode.Parse(envelopeText)!;
            // Hex-encoded fields with a 16-byte nonce, like the Node SDK.
            var nonce = (string)envelope["nonce"]!;
            Assert.Equal(32, nonce.Length);
            Assert.True(ThalovantCrypto.IsHexEncodedNonce(nonce));
            Assert.NotNull((string?)envelope["ciphertext"]);
            Assert.Equal(32, ((string)envelope["tag"]!).Length);

            var decrypted = ThalovantCrypto.DecryptJson(key, envelope);
            Assert.Equal(plaintext, decrypted);
        }

        [Fact]
        public void DecryptJsonDetectsBase64Encoding()
        {
            var key = "0123456789abcdef";
            var nonce = Hex("000102030405060708090a0b0c0d0e0f");
            var plaintext = "base64 payload";
            var ciphertext = AesGcm128.Seal(Encoding.UTF8.GetBytes(key), nonce, Encoding.UTF8.GetBytes(plaintext), out var tag);
            var envelope = new JsonObject
            {
                ["ciphertext"] = Convert.ToBase64String(ciphertext),
                ["tag"] = Convert.ToBase64String(tag),
                ["nonce"] = Convert.ToBase64String(nonce),
            };
            Assert.Equal(plaintext, ThalovantCrypto.DecryptJson(key, envelope));
        }

        [Fact]
        public void DecryptWithWrongKeyThrows()
        {
            var envelopeText = ThalovantCrypto.EncryptJson("correct-key-1234", "secret");
            var envelope = (JsonObject)JsonNode.Parse(envelopeText)!;
            Assert.Throws<ThalovantConnectionException>(() => ThalovantCrypto.DecryptJson("incorrect-key-99", envelope));
        }
    }
}
