using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A player typed a line into lobby chat. Sent client to host, relayed to everyone
    /// else. The host does not display it locally from the relay path, its own chat box
    /// echoes on send, same as before.
    /// </summary>
    public class ChatSayMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.ChatSay; } }

        /// <summary>Who said it. Stamped by the host on relay; ignored on send.</summary>
        public ushort Origin { get; set; }

        /// <summary>Display name, carried so clients need no roster lookup to render.</summary>
        public string PlayerName;

        /// <summary>The line itself.</summary>
        public string Text;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddString(PlayerName ?? string.Empty);
            m.AddString(Text ?? string.Empty);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            PlayerName = m.GetString();
            Text = m.GetString();
        }
    }
}
