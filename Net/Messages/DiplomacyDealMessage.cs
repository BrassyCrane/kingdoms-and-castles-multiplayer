using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>What one kingdom is saying to another about money and peace.</summary>
    public enum DealKind
    {
        /// <summary>"Pay me this and I will stop." The recipient is the one who pays.</summary>
        Demand = 0,

        /// <summary>"Take this and stop." The sender is the one who pays.</summary>
        Offer = 1,

        /// <summary>The recipient agrees. The gold moves and the war ends, in that order.</summary>
        Accept = 2,

        /// <summary>The recipient declines. Nothing moves and the deal is forgotten.</summary>
        Refuse = 3,
    }

    /// <summary>
    /// A payment proposed between two kingdoms, and the answer to one.
    ///
    /// Covers tribute and peace with the same message because they are the same transaction seen
    /// from different ends: somebody pays, and if there is a war it stops. An attacker naming a
    /// price and a defender offering one differ only in who pays, which is what
    /// <see cref="DealKind"/> records.
    ///
    /// Both teams travel explicitly, as with <see cref="PlayerRelationMessage"/>, so a receiver
    /// never has to work out who the other side was. Relayed to everyone including the sender, and
    /// applied by the same code on every machine, so nobody has to be told separately that gold
    /// moved: each machine reaches that conclusion itself from the same message.
    ///
    /// GOLD ONLY. Goods would need a resource picker in a UI that does not exist yet, and gold is
    /// what a ransom is actually made of. See PlayerRelations for what accepting does.
    /// </summary>
    public class DiplomacyDealMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.DiplomacyDeal; } }

        public ushort Origin { get; set; }

        /// <summary>The team proposing, or the team answering.</summary>
        public int FromTeam;

        /// <summary>The team being proposed to, or whose proposal is being answered.</summary>
        public int ToTeam;

        public int Kind;

        /// <summary>How much of it. Ignored on Accept and Refuse, which answer the open deal.</summary>
        public int Amount;

        /// <summary>
        /// Which resource, as a <c>FreeResourceType</c>. Gold is simply one of them (value 4).
        ///
        /// Carried as an int rather than the enum so the wire format does not move if the game
        /// ever inserts a type: the receiver validates it against the live enum before use, and a
        /// value it does not recognise is dropped rather than cast blindly into something real.
        /// </summary>
        public int Resource;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(FromTeam);
            m.AddInt(ToTeam);
            m.AddInt(Kind);
            m.AddInt(Amount);
            m.AddInt(Resource);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            FromTeam = m.GetInt();
            ToTeam = m.GetInt();
            Kind = m.GetInt();
            Amount = m.GetInt();
            Resource = m.GetInt();
        }
    }
}
