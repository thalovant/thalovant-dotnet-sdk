// Minimal non-cryptographic adapters for the vendored primitive subset.
#nullable disable
using System;
using System.Text;
namespace Thalovant.Internal.BouncyCastle.Utilities {
    internal static class Arrays {
        internal static byte[] Clone(byte[] value) => value == null ? null : (byte[])value.Clone();
        internal static void Clear(byte[] value) { if (value != null) Array.Clear(value, 0, value.Length); }
        internal static void Fill(ulong[] value, ulong fill) { for (int i = 0; i < value.Length; i++) value[i] = fill; }
        internal static bool AreAllZeroes(byte[] value, int offset, int count) {
            int bits = 0; for (int i = offset; i < offset + count; i++) bits |= value[i]; return bits == 0;
        }
    }
}
namespace Thalovant.Internal.BouncyCastle.Crypto {
    internal static class Check {
        internal static void OutputLength(byte[] output, int offset, int length, string message) {
            if (offset < 0 || length < 0 || offset > output.Length - length) throw new ArgumentException(message);
        }
    }
    internal sealed class PasswordConverter : ICharToByteConverter {
        internal static readonly PasswordConverter Utf8 = new PasswordConverter();
        public string Name => "UTF8";
        public byte[] Convert(char[] password) => Encoding.UTF8.GetBytes(password);
    }
}
