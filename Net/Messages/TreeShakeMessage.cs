// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A tree was shaken, the cosmetic wobble while a woodcutter chops it. Carries the cell and
    /// the tree's index within it, which is what TreeSystem.ShakeItem takes.
    /// </summary>
    public class TreeShakeMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.TreeShake; } }

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
