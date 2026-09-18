using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Asks the receiver to place a player's starting keep somewhere sensible on a given
    /// landmass.
    ///
    /// Only the landmass index travels, the receiver runs the same site-selection rules
    /// locally rather than being told a position. That works because the map is already
    /// identical everywhere by this point (the seed and generation settings were synced
    /// first), so the same rules over the same terrain reach the same cell.
    /// </summary>
    public class KeepPlaceRandomMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.KeepPlaceRandom; } }

        public ushort Origin { get; set; }

        /// <summary>Which landmass to place on.</summary>
        public int LandmassIndex;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(LandmassIndex);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            LandmassIndex = m.GetInt();
        }
    }
}
