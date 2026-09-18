using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Rubble was rebuilt, so everyone else's copy of it has to go.
    ///
    /// The replacement buildings need no help: <c>Rubble.Rebuild</c> puts them down with
    /// <c>World.Place</c>, which is already hooked and broadcast. What never travelled was the
    /// REMOVAL of the ruins, so other players ended up with the new building standing on top of
    /// rubble that, for them, was never cleared.
    ///
    /// Identified by CELL, not by guid, and that is the whole reason this message exists rather than
    /// reusing the demolish one. Rubble is created independently on each machine when a building is
    /// destroyed, and each creation calls Guid.NewGuid(), so the same pile of ruins has a DIFFERENT
    /// id everywhere and a guid lookup would find nothing. Its position is the one thing every
    /// machine agrees on.
    ///
    /// One cell is enough for a cluster: the receiver hands whatever it finds there to
    /// <c>World.TryClearRelatedRubbles</c>, which walks out to the rest of the same ruin itself.
    /// </summary>
    public class RubbleClearMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.RubbleClear; } }

        public ushort Origin { get; set; }

        public int X;
        public int Z;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(X);
            m.AddInt(Z);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            X = m.GetInt();
            Z = m.GetInt();
        }
    }
}
