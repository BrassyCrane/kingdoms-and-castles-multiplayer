using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A villager moved into a house.
    ///
    /// Villagers already arrive on every machine, because creating one goes through
    /// VillagerSystem.AddVillager and that is broadcast. Where they LIVE did not travel with them.
    /// A villager settled by a festival is housed on their owner's machine, through
    /// Player.TrySettlePeople calling SetHome, but everyone else only learns that a villager exists
    /// and files them as homeless. So a kingdom viewed from another machine slowly filled up with
    /// homeless people who were perfectly well housed at home, and homelessness is not cosmetic: it
    /// feeds unhappiness and it kills.
    ///
    /// The home is named by its BUILDING guid, which every machine agrees on. Home is the only
    /// implementer of IResidence and is a component on an ordinary building, so the guid is enough
    /// to find it again on the other side.
    /// </summary>
    public class VillagerHomeMessage : IOriginated
    {
        private const int GuidBytes = 16;

        public NetMessageId Id { get { return NetMessageId.VillagerHome; } }

        public ushort Origin { get; set; }

        public Guid Villager;

        /// <summary>The building whose Home component they moved into.</summary>
        public Guid Home;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddBytes(Villager.ToByteArray(), false);
            m.AddBytes(Home.ToByteArray(), false);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Villager = new Guid(m.GetBytes(GuidBytes));
            Home = new Guid(m.GetBytes(GuidBytes));
        }
    }
}
