// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using Assets.Code;
using UnityEngine;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Net
{
    /// <summary>Creates foreign merchants once on the host and mirrors them to every guest.</summary>
    public static class MerchantSync
    {
        /// <summary>
        /// Host: tells every guest a foreign merchant has set sail, with its cargo, prices and dock.
        /// Only the host creates merchants, so every machine sees the same ship.
        /// </summary>
        public static void Publish(MerchantShip ship, Building dock)
        {
            if (!NetRouter.IsServer || ship == null || dock == null || NetApply.InProgress) return;

            MerchantShip.ShipData data = ship.GetShipData();
            MerchantSpawnMessage m = new MerchantSpawnMessage
            {
                Ship = ship.guid,
                Dock = dock.guid,
                X = ship.transform.position.x,
                Y = ship.transform.position.y,
                Z = ship.transform.position.z,
                Upgrade = (int)data.upgrade
            };
            Put(data.hold, m, 0);
            Put(data.buyFromPrice, m, 1);
            Put(data.sellToPrice, m, 2);
            NetRouter.Broadcast(m);
            NetLog.Info("merchant spawn: " + ship.guid + " -> dock " + dock.guid);
        }

        /// <summary>
        /// Guest: makes the host's merchant ship here, once, sailing to the same dock with the same
        /// cargo and prices.
        /// </summary>
        public static void Apply(MerchantSpawnMessage m)
        {
            if (NetRouter.IsServer || m == null || ShipSystem.inst == null) return;
            try
            {
                for (int i=0; i<ShipSystem.inst.ships.Count; i++)
                    if (ShipSystem.inst.ships.data[i] != null && ShipSystem.inst.ships.data[i].guid == m.Ship)
                        return;

                Building dock = Main.FindBuildingByGuidAnywhere(m.Dock);
                if (dock == null) { NetLog.Warn("merchant spawn: dock " + m.Dock + " not found"); return; }

                Transform tr = UnityEngine.Object.Instantiate(ShipSystem.inst.merchantShipPrefab);
                tr.position = new Vector3(m.X, m.Y, m.Z);
                MerchantShip ship = tr.GetComponent<MerchantShip>();
                if (ship == null) { UnityEngine.Object.Destroy(tr.gameObject); return; }

                MerchantShip.ShipData data = new MerchantShip.ShipData
                {
                    dockID = m.Dock,
                    status = MerchantShip.Status.SailingToDock,
                    hold = Get(m, 0),
                    buyFromPrice = Get(m, 1),
                    sellToPrice = Get(m, 2),
                    upgrade = (Player.UpgradeType)m.Upgrade,
                    waitTime = 0f
                };

                using (NetApply.Scope())
                {
                    ship.SetFromShipData(data);
                    ship.guid = m.Ship;
                }
                NetLog.Info("merchant mirror: " + m.Ship + " -> dock " + m.Dock);
            }
            catch (Exception ex) { NetLog.Error("merchant spawn", ex); }
        }

        /// <summary>Writes a set of resource amounts into the message: 0 is the hold, 1 buy prices, 2 sell prices.</summary>
        private static void Put(ResourceAmount r, MerchantSpawnMessage m, int set)
        {
            int wheat=r.Get(FreeResourceType.Wheat), tree=r.Get(FreeResourceType.Tree), stone=r.Get(FreeResourceType.Stone);
            int charcoal=r.Get(FreeResourceType.Charcoal), gold=r.Get(FreeResourceType.Gold), iron=r.Get(FreeResourceType.IronOre);
            int tools=r.Get(FreeResourceType.Tools), arms=r.Get(FreeResourceType.Armament), fish=r.Get(FreeResourceType.Fish);
            int apple=r.Get(FreeResourceType.Apples), pork=r.Get(FreeResourceType.Pork);
            if (set==0) { m.Wheat=wheat;m.Tree=tree;m.Stone=stone;m.Charcoal=charcoal;m.Gold=gold;m.Iron=iron;m.Tools=tools;m.Armament=arms;m.Fish=fish;m.Apple=apple;m.Pork=pork; }
            else if (set==1) { m.BuyWheat=wheat;m.BuyTree=tree;m.BuyStone=stone;m.BuyCharcoal=charcoal;m.BuyGold=gold;m.BuyIron=iron;m.BuyTools=tools;m.BuyArmament=arms;m.BuyFish=fish;m.BuyApple=apple;m.BuyPork=pork; }
            else { m.SellWheat=wheat;m.SellTree=tree;m.SellStone=stone;m.SellCharcoal=charcoal;m.SellGold=gold;m.SellIron=iron;m.SellTools=tools;m.SellArmament=arms;m.SellFish=fish;m.SellApple=apple;m.SellPork=pork; }
        }

        /// <summary>Reads one of those sets back out of the message.</summary>
        private static ResourceAmount Get(MerchantSpawnMessage m, int set)
        {
            ResourceAmount r = new ResourceAmount();
            int[] v = set==0
                ? new[]{m.Wheat,m.Tree,m.Stone,m.Charcoal,m.Gold,m.Iron,m.Tools,m.Armament,m.Fish,m.Apple,m.Pork}
                : set==1
                    ? new[]{m.BuyWheat,m.BuyTree,m.BuyStone,m.BuyCharcoal,m.BuyGold,m.BuyIron,m.BuyTools,m.BuyArmament,m.BuyFish,m.BuyApple,m.BuyPork}
                    : new[]{m.SellWheat,m.SellTree,m.SellStone,m.SellCharcoal,m.SellGold,m.SellIron,m.SellTools,m.SellArmament,m.SellFish,m.SellApple,m.SellPork};
            FreeResourceType[] t={FreeResourceType.Wheat,FreeResourceType.Tree,FreeResourceType.Stone,FreeResourceType.Charcoal,FreeResourceType.Gold,FreeResourceType.IronOre,FreeResourceType.Tools,FreeResourceType.Armament,FreeResourceType.Fish,FreeResourceType.Apples,FreeResourceType.Pork};
            for(int i=0;i<t.Length;i++) r.Set(t[i],v[i]);
            return r;
        }
    }
}
