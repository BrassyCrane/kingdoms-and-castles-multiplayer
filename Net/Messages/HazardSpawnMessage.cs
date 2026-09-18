using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Host-authoritative hazard placement. Wolf dens spawn from caves at runtime and witch
    /// huts are placed during generation, both on non-deterministic timing and RNG, so left
    /// alone, every machine ends up with a different set. The host is the only source: it
    /// places them and broadcasts the cell, and clients never spawn their own.
    /// </summary>
    public class HazardSpawnMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.HazardSpawn; } }

        public int X;
        public int Z;

        /// <summary>0 = wolf den, 1 = witch hut.</summary>
        public int HazardType;

        public void Serialize(Message m)
        {
            m.AddInt(X);
            m.AddInt(Z);
            m.AddInt(HazardType);
        }

        public void Deserialize(Message m)
        {
            X = m.GetInt();
            Z = m.GetInt();
            HazardType = m.GetInt();
        }
    }
}
