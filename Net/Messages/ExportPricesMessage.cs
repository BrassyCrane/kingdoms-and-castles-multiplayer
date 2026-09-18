using System.Collections.Generic;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// What one kingdom charges for the goods it ships out.
    ///
    /// The whole list travels, never a single resource. Two edits made a moment apart would
    /// otherwise cross on the wire and leave the two machines holding different halves of the same
    /// price list, and a price list that is half old is worse than one that is a second stale: the
    /// buyer would be quoted one number and charged another.
    ///
    /// Sent when a player changes a price, and again to anyone who joins, because a kingdom's
    /// prices have to be known on the BUYER's machine at the moment they open a hold. Asking for
    /// them then would show a window priced wrongly until the answer came back.
    ///
    /// See <see cref="KaCMultiplayer.Trade.ExportPrices"/> for why the seller sets these at all.
    /// </summary>
    public class ExportPricesMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.ExportPrices; } }

        public ushort Origin { get; set; }

        /// <summary>The kingdom these prices belong to.</summary>
        public int Team;

        /// <summary>
        /// The resources named, as <c>FreeResourceType</c> values.
        ///
        /// Sent alongside the prices rather than relying on a fixed order, so the pairing survives
        /// a game update that adds a resource and renumbers the enum. A receiver that meets a value
        /// it does not know simply records it and never looks at it again.
        /// </summary>
        public List<int> Types = new List<int>();

        /// <summary>One price per entry in <see cref="Types"/>, in the same order.</summary>
        public List<int> Prices = new List<int>();

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(Team);
            m.AddIntList(Types);
            m.AddIntList(Prices);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Team = m.GetInt();
            Types = m.GetIntList();
            Prices = m.GetIntList();
        }
    }
}
