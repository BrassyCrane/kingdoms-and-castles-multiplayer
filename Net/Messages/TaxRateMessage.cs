using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A player changed the tax rate on one of their islands.
    ///
    /// Tax rates live on each kingdom's own Player object, and nothing else ever told the other
    /// machines about them, so the host's copy of a guest's kingdom stayed at 0 and was saved that
    /// way. This keeps every machine's copy of every kingdom's rates current.
    /// </summary>
    public class TaxRateMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.TaxRate; } }

        public ushort Origin { get; set; }

        public int LandMass;

        public float Rate;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(LandMass);
            m.AddFloat(Rate);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            LandMass = m.GetInt();
            Rate = m.GetFloat();
        }
    }
}
