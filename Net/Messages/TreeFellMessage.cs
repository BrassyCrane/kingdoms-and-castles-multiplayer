// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A tree was felled. Carries the cell so the receiver can resolve its own Cell
    /// instance, cell objects are per-machine, only coordinates travel.
    ///
    /// Also carries the tree's position, because the index is a slot in the game's per-chunk
    /// tree array that gets reshuffled on every removal, so the same tree has a different
    /// index on each machine. The receiver fells the tree in that cell nearest this position.
    /// The feller ignores its own echo, it already felled the tree.
    /// </summary>
    public class TreeFellMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.TreeFell; } }

        public ushort Origin { get; set; }

        /// <summary>Index of the tree within the cell.</summary>
        public int Index;

        public int X;
        public int Z;

        /// <summary>World position of the felled tree, to find the same tree on each machine.</summary>
        public float PosX;
        public float PosZ;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(Index);
            m.AddInt(X);
            m.AddInt(Z);
            m.AddFloat(PosX);
            m.AddFloat(PosZ);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Index = m.GetInt();
            X = m.GetInt();
            Z = m.GetInt();
            PosX = m.GetFloat();
            PosZ = m.GetFloat();
        }
    }
}
