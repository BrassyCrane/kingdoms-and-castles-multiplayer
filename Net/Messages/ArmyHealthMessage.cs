using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// The state of an army after the machine that arbitrates its fight has resolved the damage.
    ///
    /// Sent by whoever owns the ground the army is standing on, per the landmass-authority model in
    /// Combat/CombatRule.cs. Every other machine suppresses its own damage resolution for that army
    /// and takes this as the truth, which is what stops two machines rolling separate outcomes for
    /// the same battle and disagreeing about who won.
    ///
    /// Only the living unit COUNT travels, not each soldier's hit points. What a battle needs to
    /// agree on is how many men are still standing and whether the general is alive; which
    /// particular soldier is on four hit points rather than six changes nothing anyone can see, and
    /// vanilla picks that soldier with a random draw that was never going to match across machines
    /// anyway. Sending the count instead makes the message small enough to send often, and lets the
    /// receiver converge by releasing the difference.
    /// </summary>
    public class ArmyHealthMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.ArmyHealth; } }

        public ushort Origin { get; set; }

        /// <summary>Shared army id, matching the one ArmySpawnMessage established.</summary>
        public Guid Army;

        /// <summary>Soldiers still standing. The receiver releases its surplus to match.</summary>
        public int LivingUnits;

        /// <summary>The general's remaining life. Zero or less means the army is finished.</summary>
        public float GeneralLife;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin).AddGuid(Army);
            m.AddInt(LivingUnits);
            m.AddFloat(GeneralLife);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Army = m.GetGuid();
            LivingUnits = m.GetInt();
            GeneralLife = m.GetFloat();
        }
    }
}
