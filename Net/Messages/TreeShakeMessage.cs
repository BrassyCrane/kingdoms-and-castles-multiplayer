using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A tree was shaken, the cosmetic wobble when something brushes past it. Identified
    /// by global tree index rather than cell, which is what TreeSystem.ShakeTree takes.
    /// </summary>
    public class TreeShakeMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.TreeShake; } }

        public ushort Origin { get; set; }

        /// <summary>Global tree index.</summary>
        public int Index;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(Index);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Index = m.GetInt();
        }
    }
}
