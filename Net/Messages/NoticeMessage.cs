using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Tells one client to put a modal dialog on screen. Used for refusals the client
    /// cannot work out for itself, "Server is full", and similar.
    ///
    /// Host to a single client, never relayed, so no <see cref="IOriginated"/>. Note the
    /// host usually disconnects the client immediately after sending this, so it must go
    /// out reliably and be handled on arrival rather than queued behind anything.
    /// </summary>
    public class NoticeMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.Notice; } }

        /// <summary>Dialog heading.</summary>
        public string Title;

        /// <summary>Body text.</summary>
        public string Body;

        public void Serialize(Message m)
        {
            m.AddString(Title ?? string.Empty);
            m.AddString(Body ?? string.Empty);
        }

        public void Deserialize(Message m)
        {
            Title = m.GetString();
            Body = m.GetString();
        }
    }
}
