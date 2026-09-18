using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A villager was teleported. Villagers are identified by Guid, which is stable across
    /// machines, unlike the index into a local list.
    ///
    /// The Guid goes out as its raw 16 bytes with no length prefix, the size is fixed, so
    /// a prefix would just be four wasted bytes on a message that can fire often.
    /// </summary>
    public class VillagerWarpMessage : IOriginated
    {
        private const int GuidBytes = 16;

        public NetMessageId Id { get { return NetMessageId.VillagerWarp; } }

        public ushort Origin { get; set; }

        public Guid Villager;

        public float X;
        public float Y;
        public float Z;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddBytes(Villager.ToByteArray(), false);
            m.AddFloat(X);
            m.AddFloat(Y);
            m.AddFloat(Z);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Villager = new Guid(m.GetBytes(GuidBytes));
            X = m.GetFloat();
            Y = m.GetFloat();
            Z = m.GetFloat();
        }
    }
}
