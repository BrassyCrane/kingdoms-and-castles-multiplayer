// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Shrinks a save before it is sent to a joining player, and restores it on arrival.
    ///
    /// A save is a raw BinaryFormatter dump full of repeated runs. Fewer bytes is the only lever
    /// left for a faster join: the transfer rate is already as high as the connection safely
    /// takes (see SaveTransfer). Written by hand on plain byte arrays because the game's mod
    /// security check rejects everything under System.IO, GZipStream included.
    ///
    /// Two stages. First LZ, in the LZ4 block layout: a token byte (literal count, match length),
    /// the literals, a two-byte backwards offset, with 255-runs for long counts. Then one
    /// canonical Huffman code over the LZ output, at most 15 bits a code. On real saves LZ alone
    /// gives 2 to 3 times; both together give 3.3 to 5 times.
    ///
    /// Packed data starts with "KCZ2", the original length, the LZ length and the 256 code
    /// lengths (a nibble each). Anything without that header is passed through as it is, so
    /// Unpack is safe to call on bytes that were never packed.
    /// </summary>
    public static class SaveCodec
    {
        private static readonly byte[] Magic = { (byte)'K', (byte)'C', (byte)'Z', (byte)'2' };
        private const int HeaderBytes = 12 + 128;      // magic, raw length, LZ length, code lengths
        private const int MaxCodeBits = 15;
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
            int lzLen;
            byte[] lz = LzPack(src, out lzLen);

            long[] freq = new long[256];
            for (int i = 0; i < lzLen; i++) freq[lz[i]]++;
            int[] lens = CodeLengths(freq);
            int[] codes = CanonicalCodes(lens);

            byte[] dst = new byte[HeaderBytes + lzLen * 2 + 16];
            Buffer.BlockCopy(Magic, 0, dst, 0, Magic.Length);
            WriteInt(dst, 4, src.Length);
            WriteInt(dst, 8, lzLen);
            for (int s = 0; s < 256; s += 2) dst[12 + s / 2] = (byte)(lens[s] | (lens[s + 1] << 4));

            // Bits go out lowest first; the codes are bit-reversed to match.
            int p = HeaderBytes, bits = 0;
            ulong acc = 0;
            for (int i = 0; i < lzLen; i++)
            {
                int sym = lz[i];
                acc |= (ulong)codes[sym] << bits;
                bits += lens[sym];
                while (bits >= 8) { dst[p++] = (byte)acc; acc >>= 8; bits -= 8; }
            }
            if (bits > 0) dst[p++] = (byte)acc;

            byte[] result = new byte[p];
            Buffer.BlockCopy(dst, 0, result, 0, p);
            return result;
        }

        /// <summary>Restores packed bytes; bytes without the header come back unchanged.</summary>
        public static byte[] Unpack(byte[] src)
        {
            if (!IsPacked(src)) return src;

            int n = ReadInt(src, 4);
            int lzLen = ReadInt(src, 8);
            int[] lens = new int[256];
            for (int s = 0; s < 256; s += 2) { lens[s] = src[12 + s / 2] & 15; lens[s + 1] = src[12 + s / 2] >> 4; }

            // One lookup per symbol: every 15-bit pattern that starts with a code maps to it.
            int[] codes = CanonicalCodes(lens);
            int[] table = new int[1 << MaxCodeBits];
            for (int s = 0; s < 256; s++)
            {
                if (lens[s] == 0) continue;
                for (int j = codes[s]; j < table.Length; j += 1 << lens[s]) table[j] = s | (lens[s] << 8);
            }

            byte[] lz = new byte[lzLen];
            int q = HeaderBytes, bits = 0;
            ulong acc = 0;
            for (int i = 0; i < lzLen; i++)
            {
                while (bits < MaxCodeBits)
                {
                    if (q < src.Length) acc |= (ulong)src[q] << bits;
                    else if (q > src.Length + 2) throw new FormatException("save data is damaged (too short)");
                    q++;
                    bits += 8;
                }
                int e = table[(int)(acc & ((1u << MaxCodeBits) - 1))];
                int len = e >> 8;
                if (len == 0) throw new FormatException("save data is damaged (bad code)");
                lz[i] = (byte)e;
                acc >>= len;
                bits -= len;
            }
            return LzUnpack(lz, n);
        }

        /// <summary>
        /// Huffman code lengths for the byte counts, none longer than MaxCodeBits. When the tree
        /// comes out too deep the counts are halved (staying above zero) and it is built again,
        /// which flattens it at a tiny cost in size.
        /// </summary>
        private static int[] CodeLengths(long[] freqIn)
        {
            long[] freq = (long[])freqIn.Clone();
            while (true)
            {
                int[] lens = new int[256];
                long[] weight = new long[511];
                int[] parent = new int[511];
                bool[] used = new bool[511];
                int[] leafNode = new int[256];
                int nodes = 0;
                for (int s = 0; s < 256; s++)
                {
                    leafNode[s] = -1;
                    if (freq[s] == 0) continue;
                    leafNode[s] = nodes;
                    weight[nodes++] = freq[s];
                }
                if (nodes == 0) return lens;
                if (nodes == 1) { for (int s = 0; s < 256; s++) if (leafNode[s] >= 0) lens[s] = 1; return lens; }

                for (int i = 0; i < parent.Length; i++) parent[i] = -1;
                for (int live = nodes; live > 1; live--)
                {
                    int a = -1, b = -1;   // the two lightest unmerged nodes
                    for (int i = 0; i < nodes; i++)
                    {
                        if (used[i]) continue;
                        if (a < 0 || weight[i] < weight[a]) { b = a; a = i; }
                        else if (b < 0 || weight[i] < weight[b]) b = i;
                    }
                    used[a] = used[b] = true;
                    weight[nodes] = weight[a] + weight[b];
                    parent[a] = parent[b] = nodes;
                    nodes++;
                }

                int max = 0;
                for (int s = 0; s < 256; s++)
                {
                    if (leafNode[s] < 0) continue;
                    int d = 0;
                    for (int x = leafNode[s]; parent[x] >= 0; x = parent[x]) d++;
                    lens[s] = d;
                    if (d > max) max = d;
                }
                if (max <= MaxCodeBits) return lens;
                for (int s = 0; s < 256; s++) if (freq[s] > 0) freq[s] = (freq[s] >> 1) | 1;
            }
        }

        /// <summary>
        /// The canonical code for each length (shorter codes first, ties by byte value), so only
        /// the lengths need to travel. Bit-reversed for the lowest-bit-first stream.
        /// </summary>
        private static int[] CanonicalCodes(int[] lens)
        {
            int[] count = new int[MaxCodeBits + 1];
            foreach (int l in lens) if (l > 0) count[l]++;
            int[] next = new int[MaxCodeBits + 1];
            int code = 0;
            for (int l = 1; l <= MaxCodeBits; l++) { code = (code + count[l - 1]) << 1; next[l] = code; }

            int[] codes = new int[256];
            for (int s = 0; s < 256; s++)
            {
                int l = lens[s];
                if (l == 0) continue;
                int c = next[l]++, r = 0;
                for (int k = 0; k < l; k++) { r = (r << 1) | (c & 1); c >>= 1; }
                codes[s] = r;
            }
            return codes;
        }

        /// <summary>The LZ stage: returns a buffer whose first lzLen bytes are the LZ stream.</summary>
        private static byte[] LzPack(byte[] src, out int lzLen)
        {
            int n = src.Length;
            byte[] dst = new byte[n + n / 255 + 16];
            int p = 0;

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
            lzLen = Emit(src, anchor, n - anchor, dst, p, 0, 0);   // trailing literals, no match
            return dst;
        }

        /// <summary>The LZ stage undone, into a buffer of the original length n.</summary>
        private static byte[] LzUnpack(byte[] src, int n)
        {
            byte[] dst = new byte[n];
            int s = 0, d = 0;

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
