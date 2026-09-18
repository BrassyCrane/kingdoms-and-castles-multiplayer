using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A building was destroyed and left rubble, as decided by the machine that arbitrates the
    /// ground it stood on.
    ///
    /// This is the long-standing "fire and rubble do not sync" report: a building burned to rubble
    /// on one machine still stood there, whole, on the other. Building HEALTH sync did not fix it,
    /// because fire never damages a building. <c>Fire.Burnout</c> calls
    /// <c>World.inst.WreckBuilding</c> directly, and so do the dragon's fires and a keep dying in
    /// survival mode, so destruction has a path of its own that no amount of health syncing reaches.
    ///
    /// Hooked at <c>WreckBuilding</c> rather than at each cause, because that is the one place all
    /// of them meet. Whatever destroyed it, every machine ends up agreeing that it is gone.
    ///
    /// Only the building's id travels. The rubble itself is built by the receiver's own
    /// WreckBuilding from its own copy of the building, so the two sides cannot disagree about what
    /// the ruins should contain.
    /// </summary>
    public class BuildingWreckedMessage : IOriginated
    {
        private const int GuidBytes = 16;

        public NetMessageId Id { get { return NetMessageId.BuildingWrecked; } }

        public ushort Origin { get; set; }

        public Guid Building;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddBytes(Building.ToByteArray(), false);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Building = new Guid(m.GetBytes(GuidBytes));
        }
    }
}
