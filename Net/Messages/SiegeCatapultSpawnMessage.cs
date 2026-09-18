using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A siege catapult was built, and everyone else needs one too.
    ///
    /// Catapults are the only player-built fighting unit that existed on exactly ONE machine.
    /// They come out of Barracks.Tick, which is gated to the owning player (a foreign barracks
    /// forming its own army off a diverged local economy was the bug that gate exists for), and
    /// unlike an army the catapult branch never went through UnitSystem.MakeArmy, so nothing
    /// broadcast it. The result: you could roll a catapult onto a neighbour's island and they
    /// would see nothing at all coming, while its shots quietly wrecked their buildings on your
    /// machine only.
    ///
    /// Carries the spawner's own Guid, adopted by every receiver, for the same reason the dragon
    /// spawn does: SiegeCatapult's constructor calls Guid.NewGuid(), so without this each copy has
    /// a different name and nothing about the catapult, damage or death, can be reported later.
    ///
    /// Not relayed back to the sender: unlike a dragon, the sender's catapult was created by the
    /// game's own code and already exists there.
    /// </summary>
    public class SiegeCatapultSpawnMessage : IOriginated
    {
        private const int GuidBytes = 16;

        public NetMessageId Id { get { return NetMessageId.SiegeCatapultSpawn; } }

        public ushort Origin { get; set; }

        /// <summary>The shared id every machine will use for this catapult.</summary>
        public Guid Catapult;

        /// <summary>Owning team, so the receiver's copy answers TeamID() the same way.</summary>
        public int TeamId;

        public float X;
        public float Y;
        public float Z;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddBytes(Catapult.ToByteArray(), false);
            m.AddInt(TeamId);
            m.AddFloat(X);
            m.AddFloat(Y);
            m.AddFloat(Z);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Catapult = new Guid(m.GetBytes(GuidBytes));
            TeamId = m.GetInt();
            X = m.GetFloat();
            Y = m.GetFloat();
            Z = m.GetFloat();
        }
    }
}
