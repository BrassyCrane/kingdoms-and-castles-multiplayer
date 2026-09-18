using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A fire started at a cell, as decided by the machine that arbitrates that ground.
    ///
    /// Sent for the SPREAD as well as the first spark, because spreading is not a special case in
    /// the game: a fire reaching a neighbouring tile calls StartFireAt again, exactly like the
    /// original. Arbitrating that one method therefore keeps whole firestorms in step, and stops
    /// two machines burning different halves of the same town.
    ///
    /// Carries the cell, not a fire id. Fires are created independently on each machine and have no
    /// shared identity, while the grid coordinate is something everyone already agrees on. It is
    /// the same reasoning as RubbleClearMessage, which identifies ruins by cell for the same reason.
    /// </summary>
    public class FireStartMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.FireStart; } }

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
