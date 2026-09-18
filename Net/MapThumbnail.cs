using System;

using UnityEngine;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// A tiny map picture the host publishes so joining players can see the world before they
    /// commit to a download.
    ///
    /// The lobby's own preview is built from <c>World.inst.GetCellsData()</c>, the world
    /// actually loaded on this machine. A client browsing servers has no such world, and
    /// generating one from the seed would mean running the terrain generator and trampling
    /// <c>World.inst</c>. So the host encodes what it already has and puts it in Steam lobby
    /// data, alongthe name and difficulty it already publishes.
    ///
    /// Format: a 32x32 nearest-neighbour downsample, one byte per cell, base64. That is 1,024
    /// bytes raw and ~1,368 as base64, against Steam's 8 KB per-value limit, comfortable, and
    /// small enough to re-publish without thinking about it.
    ///
    /// Byte meaning: 0 = water, 255 = land with no landmass index, otherwise landmass index + 1
    /// (so index 0 is distinguishable from water). Colours come from <see cref="ColourFor"/>,
    /// which is also what the lobby preview uses, one palette, so a server's thumbnail and its
    /// lobby preview agree.
    /// </summary>
    public static class MapThumbnail
    {
        /// <summary>Steam lobby data key.</summary>
        public const string LobbyDataKey = "map";

        public const int Size = 32;

        private const byte Water = 0;
        private const byte LandNoIndex = 255;

        // Public so the lobby's live preview draws from the same palette. A server's thumbnail
        // in the browser and its preview in the lobby have to be the same picture.
        public static readonly Color WaterColour = new Color(0.16f, 0.42f, 0.72f, 1f);
        public static readonly Color PlainLandColour = new Color(0.45f, 0.65f, 0.35f, 1f);

        /// <summary>
        /// Colour for a landmass index. Hues are spread so adjacent landmasses look clearly
        /// different rather than two shades of the same green.
        /// </summary>
        public static Color LandmassColour(int index)
        {
            float hue = (index * 0.37f) % 1f;
            if (hue < 0f) hue += 1f;
            return Color.HSVToRGB(hue, 0.5f, 0.85f);
        }

        /// <summary>Colour for one encoded byte. Shared by the encoder and the decoder.</summary>
        public static Color ColourFor(byte value)
        {
            if (value == Water) return WaterColour;
            if (value == LandNoIndex) return PlainLandColour;
            return LandmassColour(value - 1);
        }

        // Encoding walks every cell in the world, so the result is cached. The publish loop runs
        // on a heartbeat and would otherwise redo this every tick.
        private static string cached;
        private static string cachedKey;

        /// <summary>
        /// Base64 thumbnail of the current world, or null if there is no world yet.
        ///
        /// Cached against <paramref name="key"/>, which must cover everything that changes the
        /// generated map - not just the seed. Changing World Size or Rivers regenerates the map
        /// while keeping the same seed, so a seed-only key would serve a stale picture.
        /// </summary>
        public static string ForKey(string key)
        {
            if (cached != null && cachedKey == key) return cached;

            string encoded = Encode();
            if (encoded == null) return null;   // no world yet - retry on the next heartbeat

            cached = encoded;
            cachedKey = key;
            return cached;
        }

        /// <summary>Drops the cache, so the next <see cref="ForKey"/> rebuilds.</summary>
        public static void Invalidate()
        {
            cached = null;
            cachedKey = null;
        }

        /// <summary>Encodes the loaded world, or null if there isn't one.</summary>
        public static string Encode()
        {
            try
            {
                if (World.inst == null) return null;

                int w = World.inst.GridWidth;
                int h = World.inst.GridHeight;
                if (w <= 0 || h <= 0) return null;

                Cell[] cells = World.inst.GetCellsData();
                if (cells == null) return null;

                // Full-resolution index grid first: cells arrive in an arbitrary order, so they
                // cannot be sampled directly.
                byte[] full = new byte[w * h];
                for (int i = 0; i < cells.Length; i++)
                {
                    Cell cell = cells[i];
                    if (cell == null) continue;
                    if (cell.x < 0 || cell.x >= w || cell.z < 0 || cell.z >= h) continue;

                    byte value;
                    if (cell.deepWater || cell.saltWater) value = Water;
                    else if (cell.landMassIdx >= 0)
                        value = (byte)Mathf.Clamp(cell.landMassIdx + 1, 1, 254);
                    else value = LandNoIndex;

                    full[cell.z * w + cell.x] = value;
                }

                byte[] small = new byte[Size * Size];
                for (int y = 0; y < Size; y++)
                {
                    int sy = Mathf.Min(h - 1, y * h / Size);
                    for (int x = 0; x < Size; x++)
                    {
                        int sx = Mathf.Min(w - 1, x * w / Size);
                        small[y * Size + x] = full[sy * w + sx];
                    }
                }

                return Convert.ToBase64String(small);
            }
            catch (Exception e)
            {
                NetLog.Warn("map thumbnail encode failed: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Decodes a published thumbnail into a texture, or null if the string is missing or the
        /// wrong shape. The caller owns the texture and must Destroy it.
        /// </summary>
        public static Texture2D Decode(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;

            try
            {
                byte[] data = Convert.FromBase64String(base64);
                if (data.Length != Size * Size)
                {
                    NetLog.Warn("map thumbnail wrong size: " + data.Length +
                                " bytes, expected " + (Size * Size));
                    return null;
                }

                Color[] pixels = new Color[Size * Size];
                for (int i = 0; i < data.Length; i++) pixels[i] = ColourFor(data[i]);

                Texture2D tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Point;   // a 32px map should look deliberately blocky
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.SetPixels(pixels);
                tex.Apply();
                return tex;
            }
            catch (Exception e)
            {
                NetLog.Warn("map thumbnail decode failed: " + e.Message);
                return null;
            }
        }
    }
}
