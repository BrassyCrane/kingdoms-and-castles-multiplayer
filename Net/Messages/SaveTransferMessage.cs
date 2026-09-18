using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// One slice of a saved game on its way from host to joining client.
    ///
    /// A save is around a megabyte, far past what one message can carry, so it is cut into
    /// fixed-size chunks and reassembled at the far end. Each chunk carries enough context
    /// to be placed without depending on arrival order: where it belongs in the buffer, how
    /// big the whole save is, and how many chunks to expect.
    ///
    /// Host to one client, never relayed.
    /// </summary>
    public class SaveTransferMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.SaveTransfer; } }

        /// <summary>Index of this chunk, 0-based. Used to mark it received.</summary>
        public int ChunkId;

        /// <summary>How many chunks make up the whole save.</summary>
        public int TotalChunks;

        /// <summary>Total size of the assembled save, so the receiver can size its buffer.</summary>
        public int SaveSize;

        /// <summary>Byte offset this chunk belongs at.</summary>
        public int Offset;

        /// <summary>The bytes themselves.</summary>
        public byte[] Data;

        /// <summary>
        /// True when this is a live snapshot sent to somebody joining a game already in progress,
        /// rather than the lobby's saved game sent before anyone has started playing.
        ///
        /// The receiver needs to know, because the two end differently: a lobby transfer waits in
        /// the lobby for the host to press Start, while a resume has to walk into the world as soon
        /// as it has unpacked, there is no Start coming. Carried on every chunk rather than just
        /// the first, so it cannot be lost to an out-of-order arrival.
        /// </summary>
        public bool Resume;

        public void Serialize(Message m)
        {
            m.AddInt(ChunkId);
            m.AddInt(TotalChunks);
            m.AddInt(SaveSize);
            m.AddInt(Offset);
            m.AddBool(Resume);
            m.AddBytes(Data ?? new byte[0]);   // length-prefixed: chunks vary at the tail
        }

        public void Deserialize(Message m)
        {
            ChunkId = m.GetInt();
            TotalChunks = m.GetInt();
            SaveSize = m.GetInt();
            Offset = m.GetInt();
            Resume = m.GetBool();
            Data = m.GetBytes();
        }
    }
}
