// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Shrinks a save before it is sent to a joining player, and restores it on arrival.
    ///
    /// A save is a raw BinaryFormatter dump full of repeated runs, and squeezes to about a fifth
    /// of its size, which makes the join about five times faster: the transfer rate is already as
    /// high as the connection safely takes (see SaveTransfer), so fewer bytes is the only lever
    /// left. Written by hand on plain byte arrays because the game's mod security check rejects
    /// everything under System.IO, GZipStream included.
    ///
    /// The format is the LZ4 block layout: a token byte (literal count, match length), the
    /// literals, a two-byte backwards offset, with 255-runs for long counts. Packed data starts
    /// with "KCZ1" and the original length; anything without that header is passed through as it
    /// is, so Unpack is safe to call on bytes that were never packed.
    ///
    /// ponytail: LZ only, about 2.5x on real saves where gzip gets 5x; add a Huffman stage over
    /// the output if joins are still too slow.
    /// </summary>
    public static class SaveCodec
    {
        private static readonly byte[] Magic = { (byte)'K', (byte)'C', (byte)'Z', (byte)'1' };
        private const int HeaderBytes = 8;
        private const int MinMatch = 4;
        private const int MaxOffset = 65535;
        private const int HashBits = 16;

        /// <summary>True when the bytes start with the packed header.</summary>
        public static bool IsPacked(byte[] data)
        {
            if (data == null || data.Length < HeaderBytes) return false;
            for (int i = 0; i < Magic.Length; i++) if (data[i] != Magic[i]) return false;
            return true;
        }

        /// <summary>Packs a save for sending. Multiplayer reason: see the class.</summary>
        public static byte[] Pack(byte[] src)
        {
            int n = src.Length;
            byte[] dst = new byte[HeaderBytes + n + n / 255 + 16];
            Buffer.BlockCopy(Magic, 0, dst, 0, Magic.Length);
            WriteInt(dst, 4, n);
            int p = HeaderBytes;

            int[] table = new int[1 << HashBits];
            for (int t = 0; t < table.Length; t++) table[t] = -1;

            int i = 0, anchor = 0;
            while (i + MinMatch <= n)
            {
                uint word = Read32(src, i);
                int h = (int)((word * 2654435761u) >> (32 - HashBits));
                int cand = table[h];
                table[h] = i;

                if (cand >= 0 && i - cand <= MaxOffset && Read32(src, cand) == word)
                {
                    int len = MinMatch;
                    while (i + len < n && src[cand + len] == src[i + len]) len++;
                    p = Emit(src, anchor, i - anchor, dst, p, i - cand, len);
                    i += len;
                    anchor = i;
                }
                else i++;
            }
            p = Emit(src, anchor, n - anchor, dst, p, 0, 0);   // trailing literals, no match

            byte[] result = new byte[p];
            Buffer.BlockCopy(dst, 0, result, 0, p);
            return result;
        }

        /// <summary>Restores packed bytes; bytes without the header come back unchanged.</summary>
        public static byte[] Unpack(byte[] src)
        {
            if (!IsPacked(src)) return src;

            int n = ReadInt(src, 4);
            byte[] dst = new byte[n];
            int s = HeaderBytes, d = 0;

            while (d < n)
            {
                int token = src[s++];

                int lit = token >> 4;
                if (lit == 15) { int b; do { b = src[s++]; lit += b; } while (b == 255); }
                Buffer.BlockCopy(src, s, dst, d, lit);
                s += lit;
                d += lit;
                if (d >= n) break;

                int offset = src[s] | (src[s + 1] << 8);
                s += 2;
                if (offset <= 0 || offset > d) throw new FormatException("save data is damaged (bad offset)");

                int len = token & 15;
                if (len == 15) { int b; do { b = src[s++]; len += b; } while (b == 255); }
                len += MinMatch;

                // Byte by byte: a match may overlap the bytes it is producing.
                int from = d - offset;
                for (int k = 0; k < len; k++) dst[d++] = dst[from++];
            }
            return dst;
        }

        /// <summary>Writes one sequence: literals, then a match unless matchLen is zero.</summary>
        private static int Emit(byte[] src, int litStart, int litLen, byte[] dst, int p, int offset, int matchLen)
        {
            int m = matchLen == 0 ? 0 : matchLen - MinMatch;
            dst[p++] = (byte)(((litLen < 15 ? litLen : 15) << 4) | (m < 15 ? m : 15));
            if (litLen >= 15) p = WriteRun(dst, p, litLen - 15);
            Buffer.BlockCopy(src, litStart, dst, p, litLen);
            p += litLen;
            if (matchLen == 0) return p;

            dst[p++] = (byte)offset;
            dst[p++] = (byte)(offset >> 8);
            if (m >= 15) p = WriteRun(dst, p, m - 15);
            return p;
        }

        /// <summary>Writes a long count as 255s and a remainder.</summary>
        private static int WriteRun(byte[] dst, int p, int v)
        {
            while (v >= 255) { dst[p++] = 255; v -= 255; }
            dst[p++] = (byte)v;
            return p;
        }

        private static uint Read32(byte[] b, int i)
        {
            return (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
        }

        private static void WriteInt(byte[] b, int i, int v)
        {
            b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); b[i + 2] = (byte)(v >> 16); b[i + 3] = (byte)(v >> 24);
        }

        private static int ReadInt(byte[] b, int i)
        {
            return b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24);
        }
    }
}
