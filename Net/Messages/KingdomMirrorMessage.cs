using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// One chunk of a player's own kingdom, on its way to the host.
    ///
    /// Same shape as a save-transfer chunk and for the same reason: a packed kingdom is far larger
    /// than one reliable message, so it travels in pieces and is reassembled by the receiver.
    ///
    /// Text rather than bytes, because the game's mod security scanner rejects System.IO in mod
    /// code, so a kingdom is packed with the same JSON the save block uses (ModSaveData).
    /// </summary>
    public class KingdomMirrorMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.KingdomMirror; } }

        public ushort Origin { get; set; }

        /// <summary>Length of the whole packed kingdom, so the host knows when it has all of it.</summary>
        public int TotalChars;

        public int TotalChunks;

        public int ChunkId;

        public string Data;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(TotalChars);
            m.AddInt(TotalChunks);
            m.AddInt(ChunkId);
            m.AddString(Data ?? string.Empty);   // length-prefixed: the last chunk is short
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            TotalChars = m.GetInt();
            TotalChunks = m.GetInt();
            ChunkId = m.GetInt();
            Data = m.GetString();
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
