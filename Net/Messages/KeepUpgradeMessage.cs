using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Another player upgraded their keep. The receiver applies it only when the local level
    /// is lower, so a late or duplicated message can never downgrade a castle.
    /// </summary>
    public class KeepUpgradeMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.KeepUpgrade; } }

        public ushort Origin { get; set; }

        public Guid Building;

        public int Level;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin).AddGuid(Building);
            m.AddInt(Level);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Building = m.GetGuid();
            Level = m.GetInt();
        }
    }
}
