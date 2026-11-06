// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>Host to guests: one authoritative foreign merchant ship.</summary>
    public class MerchantSpawnMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.MerchantSpawn; } }

        public Guid Ship;
        public Guid Dock;
        public float X, Y, Z;
        public int Upgrade;
        public int Wheat, Tree, Stone, Charcoal, Gold, Iron, Tools, Armament, Fish, Apple, Pork;
        public int BuyWheat, BuyTree, BuyStone, BuyCharcoal, BuyGold, BuyIron, BuyTools, BuyArmament, BuyFish, BuyApple, BuyPork;
        public int SellWheat, SellTree, SellStone, SellCharcoal, SellGold, SellIron, SellTools, SellArmament, SellFish, SellApple, SellPork;

        public void Serialize(Message m)
        {
            m.AddGuid(Ship).AddGuid(Dock).AddFloat(X).AddFloat(Y).AddFloat(Z).AddInt(Upgrade);
            AddResources(m, Wheat, Tree, Stone, Charcoal, Gold, Iron, Tools, Armament, Fish, Apple, Pork);
            AddResources(m, BuyWheat, BuyTree, BuyStone, BuyCharcoal, BuyGold, BuyIron, BuyTools, BuyArmament, BuyFish, BuyApple, BuyPork);
            AddResources(m, SellWheat, SellTree, SellStone, SellCharcoal, SellGold, SellIron, SellTools, SellArmament, SellFish, SellApple, SellPork);
        }

        public void Deserialize(Message m)
        {
            Ship=m.GetGuid(); Dock=m.GetGuid(); X=m.GetFloat(); Y=m.GetFloat(); Z=m.GetFloat(); Upgrade=m.GetInt();
            GetResources(m, out Wheat,out Tree,out Stone,out Charcoal,out Gold,out Iron,out Tools,out Armament,out Fish,out Apple,out Pork);
            GetResources(m, out BuyWheat,out BuyTree,out BuyStone,out BuyCharcoal,out BuyGold,out BuyIron,out BuyTools,out BuyArmament,out BuyFish,out BuyApple,out BuyPork);
            GetResources(m, out SellWheat,out SellTree,out SellStone,out SellCharcoal,out SellGold,out SellIron,out SellTools,out SellArmament,out SellFish,out SellApple,out SellPork);
        }

        /// <summary>Writes the eleven tradeable resources in a fixed order.</summary>
        private static void AddResources(Message m, params int[] values)
        {
            for (int i=0; i<values.Length; i++) m.AddInt(values[i]);
        }

        /// <summary>Reads them back in the same order.</summary>
        private static void GetResources(Message m, out int wheat,out int tree,out int stone,out int charcoal,out int gold,out int iron,out int tools,out int armament,out int fish,out int apple,out int pork)
        {
            wheat=m.GetInt(); tree=m.GetInt(); stone=m.GetInt(); charcoal=m.GetInt(); gold=m.GetInt(); iron=m.GetInt();
            tools=m.GetInt(); armament=m.GetInt(); fish=m.GetInt(); apple=m.GetInt(); pork=m.GetInt();
        }
    }
}
