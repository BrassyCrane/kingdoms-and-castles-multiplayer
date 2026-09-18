using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A player chose their kingdom banner.
    ///
    /// Relayed to everyone including the sender: the picking player does not apply it
    /// locally, so their own banner only appears once the host echoes it back.
    /// </summary>
    public class BannerPickMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.BannerPick; } }

        /// <summary>Whose banner this is. Stamped by the host on relay.</summary>
        public ushort Origin { get; set; }

        /// <summary>Index into the game's banner set.</summary>
        public int Banner;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(Banner);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Banner = m.GetInt();
        }
    }
}
