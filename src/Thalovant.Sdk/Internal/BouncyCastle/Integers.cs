// Extracted portable helpers from Bouncy Castle 2.6.2; see THIRD-PARTY-NOTICES.md.
#nullable disable
using System;
using System.Diagnostics;
using Thalovant.Internal.BouncyCastle.Utilities;
namespace Thalovant.Internal.BouncyCastle.Utilities {
internal static class Integers {
        private static readonly byte[] DeBruijnTZ = {
            0x1F, 0x00, 0x1B, 0x01, 0x1C, 0x0D, 0x17, 0x02, 0x1D, 0x15, 0x13, 0x0E, 0x18, 0x10, 0x03, 0x07,
            0x1E, 0x1A, 0x0C, 0x16, 0x14, 0x12, 0x0F, 0x06, 0x19, 0x0B, 0x11, 0x05, 0x0A, 0x04, 0x09, 0x08 };

        public static int NumberOfLeadingZeros(int i)
        {
            if (i <= 0)
                return (~i >> (31 - 5)) & (1 << 5);

            uint u = (uint)i;
            int n = 1;
            if (0 == (u >> 16)) { n += 16; u <<= 16; }
            if (0 == (u >> 24)) { n +=  8; u <<=  8; }
            if (0 == (u >> 28)) { n +=  4; u <<=  4; }
            if (0 == (u >> 30)) { n +=  2; u <<=  2; }
            n -= (int)(u >> 31);
            return n;
        }

        public static int NumberOfTrailingZeros(int i)
        {
            int n = DeBruijnTZ[(uint)((i & -i) * 0x0EF96A62) >> 27];
            int m = (((i & 0xFFFF) | (int)((uint)i >> 16)) - 1) >> 31;
            return n - m;
        }
}}
