// Extracted portable helpers from Bouncy Castle 2.6.2; see THIRD-PARTY-NOTICES.md.
#nullable disable
using System;
using System.Diagnostics;
using Thalovant.Internal.BouncyCastle.Utilities;
namespace Thalovant.Internal.BouncyCastle.Utilities {
internal static class Longs {
        public static long RotateRight(long i, int distance)
        {
            return (long)((ulong)i >> distance) | (i << -distance);
        }

        public static ulong RotateRight(ulong i, int distance)
        {
            return (i >> distance) | (i << -distance);
        }
}}
