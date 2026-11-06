// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using System.Collections.Generic;
using Assets.Code;
using Assets.Interface;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Keeps what is inside a player's warehouses and granaries the same on every machine.
    ///
    /// Only the owner's game puts villagers to work in its own kingdom, so on everyone else's
    /// machine those stores never fill or empty by themselves. The host packs its copy of a guest's
    /// kingdom when that guest reconnects, so a stale copy there used to hand the guest back empty
    /// stores. The owner now sends each public store's contents whenever they change, and every other
    /// machine sets its copy to match.
    /// </summary>
    public static class StorageSync
    {
        private static readonly Dictionary<string, string> last = new Dictionary<string, string>();
        /// <summary>Forgets what was last sent, so a new session starts by sending every store.</summary>
        public static void Reset() { last.Clear(); }

        /// <summary>
        /// Owner, about once a second: sends each of this kingdom's public stores whose contents
        /// changed since last time. Only our own buildings, because only our copy of them is real.
        /// </summary>
        public static void Tick()
        {
            SessionPlayer session;
            if (!NetClient.client.IsConnected ||
                !Main.kCPlayers.TryGetValue(Main.PlayerSteamID, out session) ||
                session == null || session.inst == null || session.inst.Buildings == null ||
                session.inst.PlayerLandmassOwner == null) return;

            Player local = session.inst;
            // About once per second at the normal fixed rate. Reconnect saves are packed from the
            // host's mirror, so a long interval directly becomes potential lost warehouse work.
            if (Main.FixedUpdateInterval % 50 != 0) return;
            for (int i=0; i<local.Buildings.Count; i++)
            {
                Building b=local.Buildings.data[i];
                if (b==null || b.TeamID()!=local.PlayerLandmassOwner.teamId) continue;
                IResourceStorage[] stores=b.GetComponents<IResourceStorage>();
                for (int s=0; s<stores.Length; s++)
                {
                    IResourceStorage store=stores[s];
                    if (store==null || store.IsPrivate()) continue;
                    StorageSnapshotMessage m=Pack(b.guid,(byte)s,store.StoredPublicResources());
                    string key=b.guid+":"+s, signature=Signature(m), old;
                    if (last.TryGetValue(key,out old) && old==signature) continue;
                    last[key]=signature;
                    NetRouter.Send(m);
                }
            }
        }

        /// <summary>
        /// Everyone else: sets our copy of the sender's store to what the sender says it holds. Refused
        /// unless the building really is the sender's, so nobody can rewrite another kingdom's stores.
        /// </summary>
        public static void Apply(StorageSnapshotMessage m)
        {
            SessionPlayer sender;
            if (!NetPlayers.TryGet(m.Origin,"storage snapshot",out sender) || sender.inst==null) return;
            Building b=Main.FindBuildingByGuidAnywhere(m.Building);
            if (b==null || sender.inst.PlayerLandmassOwner==null || b.TeamID()!=sender.inst.PlayerLandmassOwner.teamId) return;
            IResourceStorage[] stores=b.GetComponents<IResourceStorage>();
            if (m.Component>=stores.Length || stores[m.Component]==null || stores[m.Component].IsPrivate()) return;
            IResourceStorage store=stores[m.Component];
            ResourceAmount wanted=Unpack(m), current=store.StoredPublicResources();
            foreach (FreeResourceType type in PlayerRelations.Demandable)
            {
                if (type==FreeResourceType.Gold) continue;
                int delta=wanted.Get(type)-current.Get(type);
                if (delta>0) { ResourceAmount add=ResourceAmount.Make(type,delta); store.Deposit(ref add); }
                else if (delta<0) store.RemoveResources(ResourceAmount.Make(type,-delta));
            }
        }

        /// <summary>Copies one store's contents into a message.</summary>
        private static StorageSnapshotMessage Pack(Guid id,byte component,ResourceAmount r)
        {
            return new StorageSnapshotMessage { Building=id,Component=component,
                Wheat=r.Get(FreeResourceType.Wheat),Tree=r.Get(FreeResourceType.Tree),Stone=r.Get(FreeResourceType.Stone),
                Charcoal=r.Get(FreeResourceType.Charcoal),Gold=r.Get(FreeResourceType.Gold),Iron=r.Get(FreeResourceType.IronOre),
                Tools=r.Get(FreeResourceType.Tools),Armament=r.Get(FreeResourceType.Armament),Fish=r.Get(FreeResourceType.Fish),
                Apple=r.Get(FreeResourceType.Apples),Pork=r.Get(FreeResourceType.Pork) };
        }

        /// <summary>Reads a store's contents back out of a message.</summary>
        private static ResourceAmount Unpack(StorageSnapshotMessage m)
        {
            ResourceAmount r=new ResourceAmount();
            r.Set(FreeResourceType.Wheat,m.Wheat); r.Set(FreeResourceType.Tree,m.Tree); r.Set(FreeResourceType.Stone,m.Stone);
            r.Set(FreeResourceType.Charcoal,m.Charcoal); r.Set(FreeResourceType.Gold,m.Gold); r.Set(FreeResourceType.IronOre,m.Iron);
            r.Set(FreeResourceType.Tools,m.Tools); r.Set(FreeResourceType.Armament,m.Armament); r.Set(FreeResourceType.Fish,m.Fish);
            r.Set(FreeResourceType.Apples,m.Apple); r.Set(FreeResourceType.Pork,m.Pork); return r;
        }

        /// <summary>The contents as one comparable string, so an unchanged store is not sent again.</summary>
        private static string Signature(StorageSnapshotMessage m)
        {
            return m.Wheat+","+m.Tree+","+m.Stone+","+m.Charcoal+","+m.Gold+","+m.Iron+","+m.Tools+","+
                   m.Armament+","+m.Fish+","+m.Apple+","+m.Pork;
        }
    }
}
