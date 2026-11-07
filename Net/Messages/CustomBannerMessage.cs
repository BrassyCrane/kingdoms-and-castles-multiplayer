// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// One piece of a player's custom banner image, as PNG bytes.
    ///
    /// A custom banner is a picture file on the player's own machine, so the banner index that
    /// BannerPickMessage carries says nothing about it. The image is far bigger than one network
    /// message, so it travels in numbered pieces, and Stamp tells one send apart from the next so
    /// pieces of an older banner are never mixed into a newer one. TotalChunks 0 means the player
    /// went back to a built-in banner.
    /// </summary>
    public class CustomBannerMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.CustomBanner; } }

        /// <summary>Whose banner this is. Stamped by the host on relay.</summary>
        public ushort Origin { get; set; }

        /// <summary>Different for every send, so pieces from two sends cannot mix.</summary>
        public int Stamp;

        public int TotalChunks;

        public int ChunkId;

        public byte[] Data = new byte[0];

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(Stamp);
            m.AddInt(TotalChunks);
            m.AddInt(ChunkId);
            m.AddBytes(Data ?? new byte[0]);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Stamp = m.GetInt();
            TotalChunks = m.GetInt();
            ChunkId = m.GetInt();
            Data = m.GetBytes();
        }
    }
}
