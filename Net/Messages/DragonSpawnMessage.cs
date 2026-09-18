using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>Which dragon to spawn.</summary>
    public enum DragonKind
    {
        Siege = 0,
        Mama = 1,
        Baby = 2,
    }

    /// <summary>
    /// Spawn a dragon at a position. One message for all three kinds, they differed only
    /// in which DragonSpawn method to call, so three near-identical classes and three wire
    /// ids bought nothing.
    ///
    /// Relayed to everyone including the sender, and that matters here: the Harmony Prefix
    /// blocks the local spawn on a non-host client, so the initiating player has
    /// deliberately *not* spawned anything yet and depends on the echo coming back.
    /// Excluding the sender would mean their dragon never appears.
    /// </summary>
    public class DragonSpawnMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.DragonSpawn; } }

        public ushort Origin { get; set; }

        public DragonKind Kind;

        /// <summary>
        /// The spawner's own id for this dragon, adopted by every receiver.
        ///
        /// Without it each machine rolled its own Guid for its copy and there was no shared name for
        /// the same animal, so nothing about a dragon could be reported afterwards: not damage, not
        /// death. Sent with the spawn because that is the only moment all copies are known to exist
        /// and to correspond.
        /// </summary>
        public Guid Dragon;

        public float X;
        public float Y;
        public float Z;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt((int)Kind);
            m.AddBytes(Dragon.ToByteArray(), false);
            m.AddFloat(X);
            m.AddFloat(Y);
            m.AddFloat(Z);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Kind = (DragonKind)m.GetInt();
            Dragon = new Guid(m.GetBytes(16));
            X = m.GetFloat();
            Y = m.GetFloat();
            Z = m.GetFloat();
        }
    }
}
