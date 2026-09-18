using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A siege catapult's life, as decided by the machine arbitrating the ground it is standing on.
    ///
    /// The sixth and last thing in the game that implements IProjectileHitable, and the only one
    /// that was never wired into the damage-authority hooks. Everything else that can be hurt
    /// (army, unit, building, ship, dragon) has one machine resolve the blow and state the result;
    /// a catapult resolved its own damage wherever it happened to exist.
    ///
    /// Carries the resulting life rather than the damage, because an absolute value is
    /// self-correcting where a running total of deltas drifts, which is the same choice every
    /// other health message here makes.
    /// </summary>
    public class SiegeCatapultHealthMessage : IOriginated
    {
        private const int GuidBytes = 16;

        public NetMessageId Id { get { return NetMessageId.SiegeCatapultHealth; } }

        public ushort Origin { get; set; }

        public Guid Catapult;

        public float Life;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddBytes(Catapult.ToByteArray(), false);
            m.AddFloat(Life);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Catapult = new Guid(m.GetBytes(GuidBytes));
            Life = m.GetFloat();
        }
    }
}
