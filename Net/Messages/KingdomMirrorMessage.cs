using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// One chunk of a player's own kingdom, on its way to the host.
    ///
    /// Same shape as a save-transfer chunk and for the same reason: a packed kingdom is far larger
    /// than one reliable message, so it travels in pieces and is reassembled by the receiver.
    /// </summary>
    public class KingdomMirrorMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.KingdomMirror; } }

        public ushort Origin { get; set; }

        /// <summary>Size of the whole packed kingdom, so the host can size its buffer once.</summary>
        public int TotalBytes;

        public int TotalChunks;

        public int ChunkId;

        public byte[] Data;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(TotalBytes);
            m.AddInt(TotalChunks);
            m.AddInt(ChunkId);
            m.AddBytes(Data ?? new byte[0]);   // length-prefixed: the last chunk is short
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            TotalBytes = m.GetInt();
            TotalChunks = m.GetInt();
            ChunkId = m.GetInt();
            Data = m.GetBytes();
        }
    }

    /// <summary>
    /// The host asking every player to send their kingdom again. Sent after each save, so the next
    /// save has fresh copies without a kingdom being packed more often than it is needed.
    /// </summary>
    public class KingdomMirrorRequestMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.KingdomMirrorRequest; } }

        public void Serialize(Message m) { }

        public void Deserialize(Message m) { }
    }
}
