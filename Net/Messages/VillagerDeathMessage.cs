using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A villager died, as decided by the machine that arbitrates the ground they died on.
    ///
    /// Deaths could not be left to resolve locally. Every machine simulates the WHOLE world,
    /// including other players' islands, so every machine independently starves, burns and buries
    /// villagers who are not its own. Those decisions run on local timers and local random draws,
    /// so the same villager dies at different moments on different machines, and the population,
    /// the jobs they held and the corpses left behind all drift apart from there. This is the same
    /// problem combat has, and it gets the same answer: one machine decides and says so.
    ///
    /// Identified by Guid, which is stable across machines, unlike an index into a local list.
    /// The Guid goes out as its raw 16 bytes with no length prefix, since the size is fixed.
    /// </summary>
    public class VillagerDeathMessage : IOriginated
    {
        private const int GuidBytes = 16;

        public NetMessageId Id { get { return NetMessageId.VillagerDeath; } }

        public ushort Origin { get; set; }

        public Guid Villager;

        /// <summary>
        /// Whether the death leaves a corpse. It travels rather than being assumed, because it is
        /// the caller's choice, not a property of the villager: a fire leaves a body, a villager
        /// walking off a departing ship does not. Getting it wrong on the other machines would
        /// leave a corpse for a gravedigger to collect that exists nowhere else, or drop one that
        /// everyone else can see.
        /// </summary>
        public bool LeaveBody;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddBytes(Villager.ToByteArray(), false);
            m.AddBool(LeaveBody);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Villager = new Guid(m.GetBytes(GuidBytes));
            LeaveBody = m.GetBool();
        }
    }
}
