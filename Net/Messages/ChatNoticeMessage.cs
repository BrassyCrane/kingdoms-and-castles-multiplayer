using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A system line in lobby chat, "X has joined the server", "X has left the server".
    ///
    /// Host-originated, so there is no relay and no <see cref="IOriginated"/>: nobody
    /// asked for it and there is no player to attribute it to. The host broadcasts to
    /// everyone, itself included, since the host's own chat pane should show the notice
    /// too.
    /// </summary>
    public class ChatNoticeMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.ChatNotice; } }

        /// <summary>Text to display. Already formatted by the host.</summary>
        public string Text;

        public void Serialize(Message m)
        {
            m.AddString(Text ?? string.Empty);
        }

        public void Deserialize(Message m)
        {
            Text = m.GetString();
        }
    }
}
