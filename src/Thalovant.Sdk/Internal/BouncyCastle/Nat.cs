// Extracted portable helpers from Bouncy Castle 2.6.2; see THIRD-PARTY-NOTICES.md.
#nullable disable
using System;
using System.Diagnostics;
using Thalovant.Internal.BouncyCastle.Utilities;
namespace Thalovant.Internal.BouncyCastle.Math.Raw {
internal static class Nat {
        public static void Xor64(int len, ulong[] x, ulong y, ulong[] z)
        {
            for (int i = 0; i < len; ++i)
            {
                z[i] = x[i] ^ y;
            }
        }

        public static void Xor64(int len, ulong[] x, int xOff, ulong y, ulong[] z, int zOff)
        {
            for (int i = 0; i < len; ++i)
            {
                z[zOff + i] = x[xOff + i] ^ y;
            }
        }

        public static void Xor64(int len, ulong[] x, ulong[] y, ulong[] z)
        {
            for (int i = 0; i < len; ++i)
            {
                z[i] = x[i] ^ y[i];
            }
        }

        public static void Xor64(int len, ulong[] x, int xOff, ulong[] y, int yOff, ulong[] z, int zOff)
        {
            for (int i = 0; i < len; ++i)
            {
                z[zOff + i] = x[xOff + i] ^ y[yOff + i];
            }
        }

        public static void XorTo64(int len, ulong[] x, ulong[] z)
        {
            for (int i = 0; i < len; ++i)
            {
                z[i] ^= x[i];
            }
        }

        public static void XorTo64(int len, ulong[] x, int xOff, ulong[] z, int zOff)
        {
            for (int i = 0; i < len; ++i)
            {
                z[zOff + i] ^= x[xOff + i];
            }
        }

        public static void XorBothTo64(int len, ulong[] x, ulong[] y, ulong[] z)
        {
            for (int i = 0; i < len; ++i)
            {
                z[i] ^= x[i] ^ y[i];
            }
        }

        public static void XorBothTo64(int len, ulong[] x, int xOff, ulong[] y, int yOff, ulong[] z, int zOff)
        {
            for (int i = 0; i < len; ++i)
            {
                z[zOff + i] ^= x[xOff + i] ^ y[yOff + i];
            }
        }

        public static int GetBitLength(int len, uint[] x)
        {
            for (int i = len - 1; i >= 0; --i)
            {
                uint x_i = x[i];
                if (x_i != 0)
                    return i * 32 + 32 - Integers.NumberOfLeadingZeros((int)x_i);
            }
            return 0;
        }

        public static int GetBitLength(int len, uint[] x, int xOff)
        {
            for (int i = len - 1; i >= 0; --i)
            {
                uint x_i = x[xOff + i];
                if (x_i != 0)
                    return i * 32 + 32 - Integers.NumberOfLeadingZeros((int)x_i);
            }
            return 0;
        }

        public static bool Gte(int len, uint[] x, uint[] y)
        {
            for (int i = len - 1; i >= 0; --i)
            {
                uint x_i = x[i], y_i = y[i];
                if (x_i < y_i)
                    return false;
                if (x_i > y_i)
                    return true;
            }
            return true;
        }

        public static int LessThan(int len, uint[] x, uint[] y)
        {
            long c = 0;
            for (int i = 0; i < len; ++i)
            {
                c += (long)x[i] - y[i];
                c >>= 32;
            }
            Debug.Assert(c == 0L || c == -1L);
            return (int)c;
        }

        public static int LessThan(int len, uint[] x, int xOff, uint[] y, int yOff)
        {
            long c = 0;
            for (int i = 0; i < len; ++i)
            {
                c += (long)x[xOff + i] - y[yOff + i];
                c >>= 32;
            }
            Debug.Assert(c == 0L || c == -1L);
            return (int)c;
        }
}}
