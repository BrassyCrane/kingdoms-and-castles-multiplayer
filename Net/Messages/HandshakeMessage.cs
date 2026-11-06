// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// The host's greeting to a newly connected client: here is your id, and here is whether
    /// your world and your kingdom are coming from us.
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

        /// <summary>
        /// True when the recipient's world and kingdom arrive from the host rather than being made
        /// here: a saved game being resumed, or a game already in progress.
        ///
        /// It decides one thing and it decides it silently, so it is worth stating plainly: a
        /// client told false goes to the name-and-banner screen and founds a NEW kingdom. That is
        /// right for somebody joining a fresh lobby and wrong for everybody else, and it is what a
        /// player rejoining a game in progress used to be told.
        /// </summary>
        public bool WorldComesFromHost;

        /// <summary>
        /// The host's mod version, so a guest on a different build is told (see
        /// SessionHandlers.OnHandshake). Last on the wire and read only if present: a build from
        /// before this field reads the two above and stops, and a newer one reads "" from a host
        /// that predates it.
        /// </summary>
        public string HostVersion = "";

        public void Serialize(Message m)
        {
            m.AddUShort(AssignedClientId);
            m.AddBool(WorldComesFromHost);
            m.AddString(HostVersion ?? "");
        }

        public void Deserialize(Message m)
        {
            AssignedClientId = m.GetUShort();
            WorldComesFromHost = m.GetBool();
            HostVersion = m.UnreadBits > 0 ? m.GetString() : "";
        }
    }
}
