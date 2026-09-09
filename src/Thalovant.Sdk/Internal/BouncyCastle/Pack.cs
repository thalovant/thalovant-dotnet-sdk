// Extracted portable helpers from Bouncy Castle 2.6.2; see THIRD-PARTY-NOTICES.md.
#nullable disable
using System;
using System.Diagnostics;
using Thalovant.Internal.BouncyCastle.Utilities;
namespace Thalovant.Internal.BouncyCastle.Crypto.Utilities {
internal static class Pack {
        internal static uint LE_To_UInt32(byte[] bs)
        {
            return bs[0]
                | (uint)bs[1] << 8
                | (uint)bs[2] << 16
                | (uint)bs[3] << 24;
        }

        internal static uint LE_To_UInt32(byte[] bs, int off)
        {
            return bs[off]
                | (uint)bs[off + 1] << 8
                | (uint)bs[off + 2] << 16
                | (uint)bs[off + 3] << 24;
        }

        internal static void LE_To_UInt32(byte[] bs, int off, uint[] ns)
        {
            for (int i = 0; i < ns.Length; ++i)
            {
                ns[i] = LE_To_UInt32(bs, off);
                off += 4;
            }
        }

        internal static void LE_To_UInt32(byte[] bs, int bsOff, uint[] ns, int nsOff, int nsLen)
        {
            for (int i = 0; i < nsLen; ++i)
            {
                ns[nsOff + i] = LE_To_UInt32(bs, bsOff);
                bsOff += 4;
            }
        }

        internal static uint[] LE_To_UInt32(byte[] bs, int off, int count)
        {
            uint[] ns = new uint[count];
            LE_To_UInt32(bs, off, ns);
            return ns;
        }

        internal static ulong LE_To_UInt64(byte[] bs)
        {
            uint lo = LE_To_UInt32(bs);
            uint hi = LE_To_UInt32(bs, 4);
            return ((ulong)hi << 32) | (ulong)lo;
        }

        internal static ulong LE_To_UInt64(byte[] bs, int off)
        {
            uint lo = LE_To_UInt32(bs, off);
            uint hi = LE_To_UInt32(bs, off + 4);
            return ((ulong)hi << 32) | (ulong)lo;
        }

        internal static void LE_To_UInt64(byte[] bs, int off, ulong[] ns)
        {
            for (int i = 0; i < ns.Length; ++i)
            {
                ns[i] = LE_To_UInt64(bs, off);
                off += 8;
            }
        }

        internal static void LE_To_UInt64(byte[] bs, int bsOff, ulong[] ns, int nsOff, int nsLen)
        {
            for (int i = 0; i < nsLen; ++i)
            {
                ns[nsOff + i] = LE_To_UInt64(bs, bsOff);
                bsOff += 8;
            }
        }

        internal static ulong[] LE_To_UInt64(byte[] bs, int off, int count)
        {
            ulong[] ns = new ulong[count];
            LE_To_UInt64(bs, off, ns);
            return ns;
        }

        internal static void UInt64_To_LE(ulong n, byte[] bs)
        {
            UInt32_To_LE((uint)n, bs);
            UInt32_To_LE((uint)(n >> 32), bs, 4);
        }

        internal static void UInt64_To_LE(ulong n, byte[] bs, int off)
        {
            UInt32_To_LE((uint)n, bs, off);
            UInt32_To_LE((uint)(n >> 32), bs, off + 4);
        }

        internal static void UInt64_To_LE(ulong[] ns, byte[] bs, int off)
        {
            for (int i = 0; i < ns.Length; ++i)
            {
                UInt64_To_LE(ns[i], bs, off);
                off += 8;
            }
        }

        internal static void UInt64_To_LE(ulong[] ns, int nsOff, int nsLen, byte[] bs, int bsOff)
        {
            for (int i = 0; i < nsLen; ++i)
            {
                UInt64_To_LE(ns[nsOff + i], bs, bsOff);
                bsOff += 8;
            }
        }

        internal static byte[] UInt64_To_LE(ulong n)
        {
            byte[] bs = new byte[8];
            UInt64_To_LE(n, bs);
            return bs;
        }

        internal static byte[] UInt64_To_LE(ulong[] ns, int nsOff, int nsLen)
        {
            byte[] bs = new byte[8 * nsLen];
            UInt64_To_LE(ns, nsOff, nsLen, bs, 0);
            return bs;
        }

        internal static void UInt32_To_LE(uint n, byte[] bs)
        {
            bs[0] = (byte)n;
            bs[1] = (byte)(n >> 8);
            bs[2] = (byte)(n >> 16);
            bs[3] = (byte)(n >> 24);
        }

        internal static void UInt32_To_LE(uint n, byte[] bs, int off)
        {
            bs[off] = (byte)n;
            bs[off + 1] = (byte)(n >> 8);
            bs[off + 2] = (byte)(n >> 16);
            bs[off + 3] = (byte)(n >> 24);
        }

        internal static void UInt32_To_LE(uint[] ns, byte[] bs, int off)
        {
            for (int i = 0; i < ns.Length; ++i)
            {
                UInt32_To_LE(ns[i], bs, off);
                off += 4;
            }
        }

        internal static void UInt32_To_LE(uint[] ns, int nsOff, int nsLen, byte[] bs, int bsOff)
        {
            for (int i = 0; i < nsLen; ++i)
            {
                UInt32_To_LE(ns[nsOff + i], bs, bsOff);
                bsOff += 4;
            }
        }

        internal static byte[] UInt32_To_LE(uint n)
        {
            byte[] bs = new byte[4];
            UInt32_To_LE(n, bs);
            return bs;
        }

        internal static byte[] UInt32_To_LE(uint[] ns, int nsOff, int nsLen)
        {
            byte[] bs = new byte[4 * nsLen];
            UInt32_To_LE(ns, nsOff, nsLen, bs, 0);
            return bs;
        }
}}
