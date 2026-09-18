using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// One player's kingdom has changed its standing toward another's, allied, neutral or at war.
    ///
    /// The relation is a property of the pair, not of the sender, so both teams travel explicitly
    /// and the receiver does not have to work out who the other side was. Applied identically on
    /// every machine, including the sender's: unlike most of this mod's messages the decision is
    /// not applied locally first, because a relation change has consequences (gate re-baking,
    /// closing docks) that are easier to reason about when exactly one code path performs them.
    /// </summary>
    public class PlayerRelationMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.PlayerRelation; } }

        public ushort Origin { get; set; }

        public int TeamA;
        public int TeamB;

        /// <summary>A <c>World.Relations</c> value: 0 Neutral, 1 Allies, 2 Enemy.</summary>
        public int Relation;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(TeamA);
            m.AddInt(TeamB);
            m.AddInt(Relation);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            TeamA = m.GetInt();
            TeamB = m.GetInt();
            Relation = m.GetInt();
        }
    }
}
