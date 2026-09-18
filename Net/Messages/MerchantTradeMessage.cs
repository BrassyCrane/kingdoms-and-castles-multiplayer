using System;
using System.Collections.Generic;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A completed merchant trade: goods move between a ship's hold and a dock's stockpile,
    /// and gold moves between two kingdoms.
    ///
    /// Both teams travel explicitly rather than being inferred at the far end. Vanilla only
    /// credits the dock owner and never charges the merchant's side, because in single-player
    /// the merchant belongs to nobody, with two real kingdoms, both halves of the transfer
    /// have to be named.
    ///
    /// Resources are indexed by FreeResourceType, matching how the receiver reads them back.
    /// </summary>
    public class MerchantTradeMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.MerchantTrade; } }

        public ushort Origin { get; set; }

        public Guid Merchant;
        public Guid Dock;

        public int BuyerTeam;
        public int SellerTeam;
        public int Cost;

        /// <summary>True for a buy (goods leave the hold), false for a sell.</summary>
        public bool IsBuy;

        /// <summary>Amounts indexed by FreeResourceType.</summary>
        public List<int> Resources = new List<int>();

        public void Serialize(Message m)
        {
            m.AddUShort(Origin).AddGuid(Merchant).AddGuid(Dock);
            m.AddInt(BuyerTeam);
            m.AddInt(SellerTeam);
            m.AddInt(Cost);
            m.AddBool(IsBuy);
            m.AddIntList(Resources);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Merchant = m.GetGuid();
            Dock = m.GetGuid();
            BuyerTeam = m.GetInt();
            SellerTeam = m.GetInt();
            Cost = m.GetInt();
            IsBuy = m.GetBool();
            Resources = m.GetIntList();
        }
    }
}
