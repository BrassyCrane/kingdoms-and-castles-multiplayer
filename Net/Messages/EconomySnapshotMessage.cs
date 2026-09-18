using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A player's current stockpile, so other machines can display their economy without
    /// simulating it.
    ///
    /// Named fields rather than an array indexed by FreeResourceType: the receiver sets each
    /// resource explicitly by name, and a game update that renumbered that enum would
    /// silently shuffle an indexed payload into the wrong resources.
    /// </summary>
    public class EconomySnapshotMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.EconomySnapshot; } }

        public ushort Origin { get; set; }

        public int Wheat;
        public int Tree;
        public int Stone;
        public int Charcoal;
        public int Gold;
        public int Iron;
        public int Tools;
        public int Armament;
        public int Fish;
        public int Apple;
        public int Pork;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(Wheat);
            m.AddInt(Tree);
            m.AddInt(Stone);
            m.AddInt(Charcoal);
            m.AddInt(Gold);
            m.AddInt(Iron);
            m.AddInt(Tools);
            m.AddInt(Armament);
            m.AddInt(Fish);
            m.AddInt(Apple);
            m.AddInt(Pork);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Wheat = m.GetInt();
            Tree = m.GetInt();
            Stone = m.GetInt();
            Charcoal = m.GetInt();
            Gold = m.GetInt();
            Iron = m.GetInt();
            Tools = m.GetInt();
            Armament = m.GetInt();
            Fish = m.GetInt();
            Apple = m.GetInt();
            Pork = m.GetInt();
        }
    }
}
