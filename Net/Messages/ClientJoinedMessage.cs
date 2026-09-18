using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A client announcing itself once it has accepted the handshake: this is my name and
    /// my Steam id.
    ///
    /// Sent upward, then relayed to everyone including the sender. The sender needs its own
    /// copy back because the handshake created its player record with only partial
    /// information, the echo is what fills in and confirms it, by exactly the same code
    /// path every other client uses.
    ///
    /// This is also what triggers the host's whole catch-up sequence: roster, join notice,
    /// lobby settings, and then either the save transfer or the world seed and hazards.
    /// </summary>
    public class ClientJoinedMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.ClientJoined; } }

        /// <summary>Who joined. Stamped by the host on relay.</summary>
        public ushort Origin { get; set; }

        public string Name;

        /// <summary>Steam id, as a string. The key players are stored under.</summary>
        public string SteamId;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddString(Name ?? string.Empty);
            m.AddString(SteamId ?? string.Empty);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Name = m.GetString();
            SteamId = m.GetString();
        }
    }
}
