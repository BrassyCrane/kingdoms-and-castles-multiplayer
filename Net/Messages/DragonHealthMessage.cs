using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A dragon's health, as decided by the machine arbitrating the ground it is over.
    ///
    /// Dragons already SPAWNED on every machine, because the host is the only one allowed to start
    /// one and it broadcasts, but nothing carried what happened to them afterwards. A dragon shot
    /// down over one player's island went on flying, unhurt and immortal, for everyone else, and
    /// damage aimed at it was resolved independently on each machine.
    ///
    /// Identified by the dragon's own Guid, which now travels with the spawn so both sides agree on
    /// which dragon is which. Carries the resulting HP rather than the damage, because an absolute
    /// value is self-correcting where a running total of deltas would drift.
    /// </summary>
    public class DragonHealthMessage : IOriginated
    {
        private const int GuidBytes = 16;

        public NetMessageId Id { get { return NetMessageId.DragonHealth; } }

        public ushort Origin { get; set; }

        public Guid Dragon;

        public float Hp;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddBytes(Dragon.ToByteArray(), false);
            m.AddFloat(Hp);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Dragon = new Guid(m.GetBytes(GuidBytes));
            Hp = m.GetFloat();
        }
    }
}
