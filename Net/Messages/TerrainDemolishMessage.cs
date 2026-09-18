using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A building was demolished. Identified by guid; the receiver temporarily swaps
    /// Player.inst to the building's owner so the game's own demolish bookkeeping credits the
    /// right kingdom rather than the local one.
    /// </summary>
    public class TerrainDemolishMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.TerrainDemolish; } }

        public ushort Origin { get; set; }

        public Guid Building;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin).AddGuid(Building);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Building = m.GetGuid();
        }
    }
}
