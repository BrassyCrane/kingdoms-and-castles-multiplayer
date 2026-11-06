// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using System.Collections.Generic;
using UnityEngine;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Net
{
    /// <summary>The host runs Viking AI; guests display host-positioned transport ships.</summary>
    public static class RaiderSync
    {
        private const int SendEvery = 15;
        private static int tick;
        private static bool hostHadBoats;
        private static readonly HashSet<Guid> mirrored = new HashSet<Guid>();
        private static readonly HashSet<Guid> live = new HashSet<Guid>();
        private static readonly List<Guid> stale = new List<Guid>();

        /// <summary>Forgets every mirrored boat, for a session ending.</summary>
        public static void Reset() { tick=0; hostHadBoats=false; mirrored.Clear(); }

        /// <summary>
        /// Host: sends where every Viking transport is, a few times a second. One last empty message
        /// after the final boat goes, so guests remove theirs too.
        /// </summary>
        public static void Tick()
        {
            if (!Main.RaidsEnabled || !NetRouter.IsServer || ShipSystem.inst == null) return;
            if (++tick < SendEvery) return;
            tick=0;
            var m=new RaiderBoatsMessage();
            for(int i=0;i<ShipSystem.inst.ships.Count;i++)
            {
                ShipBase s=ShipSystem.inst.ships.data[i];
                if(s==null || s.type!=ShipBase.ShipType.VikingTroopTransport) continue;
                m.Boats.Add(s.guid); m.Positions.Add(s.GetPos()); m.Rotations.Add(s.GetRot());
            }
            if(m.Boats.Count>0 || hostHadBoats) NetRouter.Broadcast(m);
            hostHadBoats=m.Boats.Count>0;
        }

        /// <summary>
        /// Guest: moves each Viking transport to where the host has it, makes any it does not have
        /// yet, and removes the ones the host no longer has. The host runs the raid, so a guest only
        /// ever shows its boats.
        /// </summary>
        public static void Apply(RaiderBoatsMessage m)
        {
            if(NetRouter.IsServer || m==null || ShipSystem.inst==null || RaiderSystem.inst==null) return;
            int count=Math.Min(m.Boats.Count,Math.Min(m.Positions.Count,m.Rotations.Count));
            live.Clear();
            for(int i=0;i<count;i++)
            {
                live.Add(m.Boats[i]);
                ShipBase boat=Find(m.Boats[i]);
                if(boat==null)
                {
                    GameObject prefab=RaiderSystem.inst.raiderTransportShipPrefab;
                    if(prefab==null) continue;
                    boat=UnityEngine.Object.Instantiate(prefab).GetComponent<ShipBase>();
                    if(boat==null) continue;
                    boat.Init(1); boat.guid=m.Boats[i];
                    mirrored.Add(boat.guid);
                    NetLog.Info("Viking boat mirror: "+boat.guid);
                }
                boat.InitializePos(m.Positions[i]);
                boat.transform.rotation=m.Rotations[i];
            }

            stale.Clear();
            foreach(Guid id in mirrored) if(!live.Contains(id)) stale.Add(id);
            for(int i=0;i<stale.Count;i++)
            {
                ShipBase gone=Find(stale[i]);
                if(gone!=null) UnityEngine.Object.Destroy(gone.gameObject);
                mirrored.Remove(stale[i]);
            }
        }

        /// <summary>The ship with this id in this machine's ship list, or null.</summary>
        private static ShipBase Find(Guid id)
        {
            for(int i=0;i<ShipSystem.inst.ships.Count;i++)
            { ShipBase s=ShipSystem.inst.ships.data[i]; if(s!=null && s.guid==id) return s; }
            return null;
        }

        /// <summary>
        /// Whether this machine should simulate a ship. A guest leaves Viking transports to the host,
        /// whose positions arrive in Apply; everything else, and single player, runs as vanilla.
        /// </summary>
        public static bool ShouldTickShip(ShipBase ship)
        {
            if(!Main.RaidsEnabled || !NetRouter.IsConnected || NetRouter.IsServer || ship==null) return true;
            // Ogre transports are not included in RaiderBoatsMessage yet. Let vanilla simulate
            // them on guests instead of freezing an invisible or permanently stationary ship.
            return ship.type!=ShipBase.ShipType.VikingTroopTransport;
        }
    }
}
