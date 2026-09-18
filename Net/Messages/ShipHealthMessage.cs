using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A ship's remaining life, from the machine arbitrating the fight it is in.
    ///
    /// Ships almost always sit on water, and water belongs to nobody, so under the landmass model
    /// naval combat falls to the host. That is deliberate rather than a gap: an unowned sea has no
    /// better claimant, and the host is the one participant guaranteed to be present.
    ///
    /// Only life travels, and no separate "it sank" message exists, because none is needed.
    /// ShipBase.Tick sinks a ship of its own accord the moment life drops to zero, so every machine
    /// reaches the same end by running the same local rule over a synced number. Sending a death
    /// event as well would mean two things could disagree about whether a ship is gone.
    /// </summary>
    public class ShipHealthMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.ShipHealth; } }

        public ushort Origin { get; set; }

        /// <summary>Shared ship id. In multiplayer a ship's guid is its launch building's.</summary>
        public Guid Ship;

        /// <summary>Remaining life. At or below zero the receiver's own Tick starts it sinking.</summary>
        public float Life;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin).AddGuid(Ship);
            m.AddFloat(Life);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Ship = m.GetGuid();
            Life = m.GetFloat();
        }
    }
}
