using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// The weather changed. Host-authoritative, only the host's simulation decides
    /// weather, and the send site is guarded on the host running.
    ///
    /// The type travels as an int rather than the game's WeatherType enum. Enum values are
    /// the game's to renumber between patches; pinning the wire to int means a game update
    /// cannot silently change what a given byte means.
    /// </summary>
    public class WeatherSetMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.WeatherSet; } }

        public ushort Origin { get; set; }

        /// <summary>Weather.WeatherType, as an int.</summary>
        public int WeatherType;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(WeatherType);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            WeatherType = m.GetInt();
        }
    }
}
