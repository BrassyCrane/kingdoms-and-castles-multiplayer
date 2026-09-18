using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A player toggled their ready state in the lobby.
    ///
    /// The value travelling up is ignored: the host reads the player's current state,
    /// flips it, and puts its own answer in the field before relaying. That keeps the
    /// host authoritative over readiness, two rapid clicks can't get the lobby into a
    /// state where players disagree about who is ready.
    ///
    /// Relayed back to the sender as well, because the clicking player has not changed
    /// anything locally and is waiting for the host's answer to render their own row.
    /// </summary>
    public class PlayerReadyMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.ReadyState; } }

        /// <summary>Whose readiness this is. Stamped by the host on relay.</summary>
        public ushort Origin { get; set; }

        /// <summary>Resulting state. Meaningful on the way down, ignored on the way up.</summary>
        public bool IsReady;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddBool(IsReady);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            IsReady = m.GetBool();
        }
    }
}
