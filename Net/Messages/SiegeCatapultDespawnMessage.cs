using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A siege catapult left the world for a reason that is NOT a fight.
    ///
    /// Combat deaths do not need this: the arbiter publishes a life of zero and every machine's
    /// copy dies through the game's own death code, which is what keeps the rubble, the effects
    /// and the orders bookkeeping identical everywhere. What is left over is DISBANDING, an owner
    /// action that returns the crew as villagers and destroys the catapult without any damage
    /// being dealt, and it is owner-authoritative for exactly that reason.
    ///
    /// Without it a disbanded catapult would stand on every other machine forever, and not merely
    /// as a visual: SiegeCatapult.Release is also what takes the unit out of OrdersManager, so a
    /// copy nobody released stays a pathing obstacle for everyone who still believes in it. The
    /// same lesson armies taught, see <see cref="ArmyDespawnMessage"/>.
    /// </summary>
    public class SiegeCatapultDespawnMessage : IOriginated
    {
        private const int GuidBytes = 16;

        public NetMessageId Id { get { return NetMessageId.SiegeCatapultDespawn; } }

        public ushort Origin { get; set; }

        public Guid Catapult;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddBytes(Catapult.ToByteArray(), false);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Catapult = new Guid(m.GetBytes(GuidBytes));
        }
    }
}
