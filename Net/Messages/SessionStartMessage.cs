using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// The host started the game, everyone leaves the lobby and enters play.
    ///
    /// Carries no payload. It exists purely as a signal, and the receiver already has
    /// everything it needs: the map is generated, the roster is known, and whether this is
    /// a fresh game or a loaded save is tracked locally.
    /// </summary>
    public class SessionStartMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.SessionStart; } }

        public void Serialize(Message m) { }

        public void Deserialize(Message m) { }
    }
}
