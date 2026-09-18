using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// An army left the world and must disappear everywhere.
    ///
    /// The counterpart to <see cref="ArmySpawnMessage"/>, and until now the missing half: armies
    /// were created across the wire but never removed, so a disbanded or dead army stood on every
    /// other machine forever. That is not merely cosmetic. Releasing an army also takes it out of
    /// OrdersManager, so a phantom army is a permanent obstacle that degrades pathing for everyone
    /// who still thinks it is there.
    ///
    /// Only the guid travels. Why the army left is deliberately not sent: every reason ends in the
    /// same local action, and the receiver must never reproduce the CAUSE. Disbanding hands the
    /// soldiers back as villagers through Player.inst, which is the wrong kingdom on any other
    /// machine, and rolls SRand for the armament drop, which would pull the shared random sequence
    /// out of step. Receivers therefore release the army and nothing else; see ApplyArmyDespawn.
    /// </summary>
    public class ArmyDespawnMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.ArmyDespawn; } }

        public ushort Origin { get; set; }

        /// <summary>Shared army id, matching the one <see cref="ArmySpawnMessage"/> established.</summary>
        public Guid Army;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin).AddGuid(Army);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Army = m.GetGuid();
        }
    }
}
