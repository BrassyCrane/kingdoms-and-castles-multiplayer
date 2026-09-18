using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Updated state for a building that already exists on the receiver, construction
    /// progress, damage, decay, open/closed. Sent as things change, unlike
    /// <see cref="BuildPlaceMessage"/> which creates the building in the first place.
    ///
    /// Adds resource progress on top of the shared state, which placement has no use for.
    /// </summary>
    public class BuildSnapshotMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.BuildSnapshot; } }

        public ushort Origin { get; set; }

        public BuildingState State = new BuildingState();

        /// <summary>Progress toward the building's next resource output.</summary>
        public float ResourceProgress;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            State.Write(m);
            m.AddFloat(ResourceProgress);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            State = new BuildingState();
            State.Read(m);
            ResourceProgress = m.GetFloat();
        }
    }
}
