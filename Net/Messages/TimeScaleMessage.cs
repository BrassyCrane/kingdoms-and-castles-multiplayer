using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Someone changed the game speed, pause included (pause is speed 0). Sent client to
    /// host, relayed to everyone else.
    ///
    /// Relayed rather than host-authoritative on purpose: any player may change speed,
    /// and the last change wins. Preserves existing behaviour.
    /// </summary>
    public class TimeScaleMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.TimeScale; } }

        /// <summary>Who changed it. Stamped by the host on relay.</summary>
        public ushort Origin { get; set; }

        /// <summary>Game speed index as SpeedControlUI understands it. 0 is paused.</summary>
        public int Speed;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(Speed);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Speed = m.GetInt();
        }
    }
}
