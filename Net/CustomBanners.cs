// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using System.Collections.Generic;
using KaCMultiplayer.Net.Messages;
using UnityEngine;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Shows each player's custom banner on everyone's screen.
    ///
    /// The game's custom banner is a picture on the player's own machine, set as their kingdom's
    /// BannerTexture. Only the built-in banner index was ever sent, so other players saw the
    /// built-in banner instead. Here the owner sends the picture as PNG pieces, and each receiver
    /// keeps it on that player's SessionPlayer and puts it back every time something resets the
    /// kingdom's livery (LandmassOwner.SetBannerIdx overwrites BannerTexture).
    /// </summary>
    public static class CustomBanners
    {
        /// <summary>Bytes per piece, well under Riptide's 1225-byte payload.</summary>
        private const int ChunkBytes = 1000;

        /// <summary>A banner bigger than this is not sent: it would flood the link for a picture.</summary>
        private const int MaxBytes = 128 * 1024;

        /// <summary>Pieces received so far, per sender, for the send in progress.</summary>
        private class Incoming
        {
            public int Stamp;
            public byte[][] Chunks;
            public int Received;
        }

        private static readonly Dictionary<ushort, Incoming> incoming = new Dictionary<ushort, Incoming>();

        /// <summary>
        /// Sends this player's banner to everyone: the picture when a custom banner is in use, or
        /// a "none" when it is a built-in one, so the others drop a custom banner they still hold.
        /// Called when the banner is chosen and whenever someone joins, since a joiner has none.
        /// </summary>
        public static void SendOurs()
        {
            try
            {
                if (!NetClient.client.IsConnected || Player.inst == null) return;

                int stamp = UnityEngine.Random.Range(1, int.MaxValue);
                byte[] png = null;
                if (Player.inst.usingCustomBanner && Player.inst.customBannerTexture2D != null)
                    png = Player.inst.customBannerTexture2D.EncodeToPNG();

                if (png == null || png.Length == 0)
                {
                    NetRouter.Send(new CustomBannerMessage { Stamp = stamp, TotalChunks = 0 });
                    return;
                }
                if (png.Length > MaxBytes)
                {
                    Main.helper.Log("[BANNER] custom banner is " + (png.Length / 1024) + " KB, over the "
                                    + (MaxBytes / 1024) + " KB limit; the others keep seeing the built-in one");
                    return;
                }

                int total = (png.Length + ChunkBytes - 1) / ChunkBytes;
                for (int i = 0; i < total; i++)
                {
                    int len = Math.Min(ChunkBytes, png.Length - i * ChunkBytes);
                    byte[] piece = new byte[len];
                    Array.Copy(png, i * ChunkBytes, piece, 0, len);
                    NetRouter.Send(new CustomBannerMessage { Stamp = stamp, TotalChunks = total, ChunkId = i, Data = piece });
                }
                Main.helper.Log("[BANNER] sent our custom banner, " + (png.Length / 1024) + " KB in " + total + " pieces");
            }
            catch (Exception e)
            {
                // A texture that cannot be read back (not marked readable) throws here. The banner
                // still works locally; the others just see the built-in one.
                Main.helper.Log("[BANNER] could not send our custom banner: " + e.Message);
            }
        }

        /// <summary>
        /// Collects one piece; on the last one, decodes the picture and gives it to the sender's
        /// kingdom. A piece from a newer send discards an unfinished older one.
        /// </summary>
        public static void Receive(CustomBannerMessage m)
        {
            if (m.Origin == NetRouter.LocalClientId) return;   // our own, relayed back

            SessionPlayer player;
            if (!NetPlayers.TryGet(m.Origin, "custom banner", out player)) return;

            if (m.TotalChunks <= 0)
            {
                incoming.Remove(m.Origin);
                if (player.customBanner == null) return;
                player.customBanner = null;
                // Back to the built-in livery, which SetBannerIdx also puts in BannerTexture.
                if (player.inst != null && player.inst.PlayerLandmassOwner != null)
                    Main.SetKingdomBanner(player.inst.PlayerLandmassOwner, player.inst.PlayerLandmassOwner.bannerIdx, "custom banner cleared");
                return;
            }

            if (m.TotalChunks * ChunkBytes > MaxBytes + ChunkBytes || m.ChunkId < 0 || m.ChunkId >= m.TotalChunks) return;

            Incoming inc;
            if (!incoming.TryGetValue(m.Origin, out inc) || inc.Stamp != m.Stamp || inc.Chunks.Length != m.TotalChunks)
            {
                inc = new Incoming { Stamp = m.Stamp, Chunks = new byte[m.TotalChunks][] };
                incoming[m.Origin] = inc;
            }
            if (inc.Chunks[m.ChunkId] != null) return;
            inc.Chunks[m.ChunkId] = m.Data ?? new byte[0];
            if (++inc.Received < inc.Chunks.Length) return;

            incoming.Remove(m.Origin);
            try
            {
                List<byte> all = new List<byte>();
                foreach (byte[] piece in inc.Chunks) all.AddRange(piece);

                Texture2D tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, all.ToArray()))
                {
                    NetLog.Warn("custom banner from " + player.name + " could not be decoded");
                    return;
                }
                player.customBanner = tex;
                NetLog.Info("custom banner from " + player.name + ", " + tex.width + "x" + tex.height);
                Apply(player.inst != null ? player.inst.PlayerLandmassOwner : null);
            }
            catch (Exception e) { NetLog.Error("custom banner", e); }
        }

        /// <summary>
        /// Puts a remote player's custom banner back on their kingdom, if they have one. Called
        /// after anything sets the kingdom's livery, because that overwrites BannerTexture with the
        /// built-in picture. The local player's own banner is the game's business and is left alone.
        /// </summary>
        public static void Apply(LandmassOwner owner)
        {
            try
            {
                if (owner == null || !NetClient.client.IsConnected) return;
                SessionPlayer sp = NetPlayers.ByTeam(owner.teamId);
                if (sp == null || sp.customBanner == null || sp.id == NetRouter.LocalClientId) return;
                if (owner.BannerTexture == sp.customBanner) return;

                owner.BannerTexture = sp.customBanner;
                Main.MarkBannersDirty();
            }
            catch (Exception e) { Main.helper.Log("[BANNER] applying a custom banner failed: " + e.Message); }
        }
    }
}
