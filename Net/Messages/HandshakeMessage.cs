using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// The host's greeting to a newly connected client: here is your id, and here is whether
    /// we are loading a save.
    ///
    /// The assigned id is carried explicitly rather than read from the transport. The client
    /// could ask Riptide for its own id and get the same answer, but this message is where
    /// the client's identity is established, it goes on to build its player record and
    /// derive its team id from it, and a value that important should be stated, not
    /// inferred from a coincidence that happens to hold.
    ///
    /// Host to one client. Never relayed, never sent upward.
    /// </summary>
    public class HandshakeMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.Handshake; } }

        /// <summary>The client id the host has assigned to the recipient.</summary>
        public ushort AssignedClientId;

        /// <summary>True when the session is resuming a saved game rather than starting fresh.</summary>
        public bool LoadingSave;

        public void Serialize(Message m)
        {
            m.AddUShort(AssignedClientId);
            m.AddBool(LoadingSave);
        }

        public void Deserialize(Message m)
        {
            AssignedClientId = m.GetUShort();
            LoadingSave = m.GetBool();
        }
    }
}
