using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A player placed a building. The core message of the mod.
    ///
    /// Carries the full building state rather than just a position and a type, because the
    /// receiver reconstructs the building through the game's own save-data unpack path,
    /// the same route a loaded save takes. That is what makes a remotely placed building
    /// indistinguishable from a locally placed one.
    /// </summary>
    public class BuildPlaceMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.BuildPlace; } }

        /// <summary>Whose building this is. Stamped by the host on relay.</summary>
        public ushort Origin { get; set; }

        public BuildingState State = new BuildingState();

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            State.Write(m);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            State = new BuildingState();
            State.Read(m);
        }
    }
}
