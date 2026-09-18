using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A villager was born into a player's kingdom. Carries only the guid, the receiver
    /// creates its own villager and adopts that id, so later position corrections and
    /// teleports resolve to the same villager on every machine.
    /// </summary>
    public class VillagerAddMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.VillagerAdd; } }

        public ushort Origin { get; set; }

        public Guid Villager;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin).AddGuid(Villager);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Villager = m.GetGuid();
        }
    }
}
