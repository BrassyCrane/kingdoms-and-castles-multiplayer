using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A building's health, as decided by the machine that arbitrates the ground it stands on.
    ///
    /// Buildings were the hole in the combat-authority model. Damage aimed at a building is
    /// SUPPRESSED on every machine that is not the arbiter, exactly like damage to an army or a
    /// ship, but only armies and ships were ever published. So the arbiter burned a building down
    /// and nobody else was told: on every other machine it stood at full health, permanently
    /// undamageable, because their own damage was being suppressed too. That was strictly worse
    /// than resolving combat everywhere, which is what happened before arbitration was switched on.
    ///
    /// Carries the resulting HEALTH rather than the damage dealt. An absolute value is
    /// self-correcting, so a message lost or arriving out of order cannot leave a building
    /// permanently out of step, which a running total of deltas would.
    /// </summary>
    public class BuildingHealthMessage : IOriginated
    {
        private const int GuidBytes = 16;

        public NetMessageId Id { get { return NetMessageId.BuildingHealth; } }

        public ushort Origin { get; set; }

        public Guid Building;

        /// <summary>The building's life after the arbiter resolved the hit. Zero or less means it fell.</summary>
        public float Life;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddBytes(Building.ToByteArray(), false);
            m.AddFloat(Life);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Building = new Guid(m.GetBytes(GuidBytes));
            Life = m.GetFloat();
        }
    }
}
