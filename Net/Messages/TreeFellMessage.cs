using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A tree was felled. Carries the cell so the receiver can resolve its own Cell
    /// instance, cell objects are per-machine, only coordinates travel.
    ///
    /// Relayed to everyone including the sender. The feller has already felled it locally, so
    /// its own echo re-applies to an already-felled tree, TreeSystem tolerates that, and the
    /// blind relay is cheaper than tracking who to skip.
    /// </summary>
    public class TreeFellMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.TreeFell; } }

        public ushort Origin { get; set; }

        /// <summary>Index of the tree within the cell.</summary>
        public int Index;

        public int X;
        public int Z;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(Index);
            m.AddInt(X);
            m.AddInt(Z);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Index = m.GetInt();
            X = m.GetInt();
            Z = m.GetInt();
        }
    }
}
