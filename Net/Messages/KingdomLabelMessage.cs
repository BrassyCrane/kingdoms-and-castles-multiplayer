using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A player named (or renamed) their kingdom.
    ///
    /// Relayed to everyone including the sender. The namer already sees their own town
    /// name through the game's own UI, but the echo is what writes it into their copy of
    /// the player record, so the lobby list agrees with everyone else's.
    /// </summary>
    public class KingdomLabelMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.KingdomLabel; } }

        /// <summary>Whose kingdom this is. Stamped by the host on relay.</summary>
        public ushort Origin { get; set; }

        /// <summary>The kingdom's display name.</summary>
        public string KingdomName;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddString(KingdomName ?? string.Empty);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            KingdomName = m.GetString();
        }
    }
}
