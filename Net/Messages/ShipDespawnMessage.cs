using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A ship was consumed on one machine and must disappear everywhere. Currently the
    /// settle case: settling runs Disband() on the settler's machine only, leaving everyone
    /// else's copy floating. Settle cannot be replayed remotely, it spawns settlers via
    /// Player.inst, which would be the wrong player, so the ship is destroyed by guid.
    /// </summary>
    public class ShipDespawnMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.ShipDespawn; } }

        public ushort Origin { get; set; }

        public Guid Ship;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin).AddGuid(Ship);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Ship = m.GetGuid();
        }
    }
}
