using System;
using System.Linq;
using Assets.Code;   // ResourceAmount
using UnityEngine;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// DEV ONLY. A virtual second player. One running instance behaves as if a real peer with their
    /// own kingdom had joined, so two-player LOGIC (roster, two-kingdom save/load, the remote-player
    /// pack/sync, relations, trade and combat) can be exercised on a single machine with no second
    /// Steam client.
    ///
    /// It is built to cover every cross-player feature the mod has, because there is no second
    /// tester and anything it cannot reach ships unexercised:
    ///
    ///   Ctrl+Shift+F  a second kingdom with a keep, and a stocked, funded dock, which is what
    ///                 cross-player TRADE needs: a merchant only settles against a foreign dock, and
    ///                 the trade window is empty unless that dock has goods and money behind it.
    ///   Ctrl+Shift+G  war, plus one of its armies beside your keep, which is what COMBAT needs:
    ///                 raiders are suppressed in multiplayer and the peer is otherwise peaceful, so
    ///                 without this there is nothing in the world to fight. Also gives you a foreign
    ///                 army and foreign buildings to aim at, which is what exercises attack ORDERS
    ///                 (a target that is not a plain cell) and the damage-authority model.
    ///
    /// The two are separate keys on purpose: declaring war closes the trade docks between the two
    /// kingdoms, so a session that went to war on arrival could never test trading. Trade first,
    /// then fight.
    ///
    /// What it does NOT do: it never touches the real Steam transport and cannot show cross-machine
    /// divergence (threaded pathing, real network timing). It closes the two-player-logic gap, not the
    /// interop one. Pairs with <see cref="NetLoopback"/>, which exercises the receive handlers.
    ///
    /// Double-gated so a player can never trigger it. <see cref="Enabled"/> follows Main.DevTestBuild
    /// and is false in anything given to players. Even then it only acts on deliberate dev hotkeys,
    /// and each checks this flag first, so with the flag off the keypresses do nothing at all.
    /// </summary>
    public static class FakePeer
    {
        /// <summary>DEV ONLY. Leave false in anything you upload.</summary>
        public static bool Enabled = false;

        // Far outside Riptide's real client-id range (ids start at 1) and unlike any real steamId, so a
        // fake peer is obvious in the log and can never collide with a genuine one.
        public const ushort SyntheticClientId = 62000;
        public const string SyntheticSteamId = "FAKEPEER0000000001";

        /// <summary>
        /// Highest team id the mod's per-team arrays are sized for, mirroring Main's
        /// MpPathTeamSlots. A team at or above this indexes off the end of every PathCell array.
        ///
        /// This bit: the session team id is normally DERIVED from the client id (clientId + 4), and
        /// a synthetic client id of 62000 therefore produced team 62004. The keep never got far
        /// enough to expose it, but any pathing by that kingdom would have gone straight out of
        /// bounds, which is the same size-by-team-array trap that has cost this project cycles in
        /// PathCell and OrdersManager. So the fake peer's team is PINNED into the real range instead
        /// of being derived, and the synthetic client id stays large purely to avoid id collisions.
        /// </summary>
        private const int MaxTeamSlots = 32;

        private static SessionPlayer peer;

        public static bool IsActive { get { return peer != null; } }

        /// <summary>Adds the fake peer with a kingdom, or removes it if already present. No-op unless the dev flag is on.</summary>
        public static void Toggle()
        {
            if (!Enabled) return;              // hard gate: never act unless the dev flag is set
            if (peer == null) Spawn();
            else Despawn();
        }

        private static void Spawn()
        {
            try
            {
                if (World.inst == null || Player.inst == null)
                {
                    NetLog.Warn("fake peer: no world yet, enter a game first");
                    return;
                }
                if (Main.kCPlayers.ContainsKey(SyntheticSteamId))
                {
                    NetLog.Warn("fake peer already present");
                    return;
                }

                // Pin the team BEFORE constructing, because the SessionPlayer constructor asks
                // LoadIdentity for one and would otherwise derive it from the client id.
                int team = FirstFreeTeam();
                if (team < 0)
                {
                    NetLog.Warn("fake peer: no free team id in range, not spawning");
                    return;
                }
                LoadSaveOverrides.LoadIdentity.AdoptAssignedTeam(SyntheticSteamId, team);

                // id != our own client id, so the SessionPlayer ctor takes the BuildRemotePlayer branch
                // and creates a second, parallel Player with its own kingdom.
                peer = new SessionPlayer("FakePeer", SyntheticClientId, SyntheticSteamId);
                peer.kingdomName = "Fakeburg";

                // A banner index, because Building.Init reads the owner's livery material and a
                // kingdom that never chose one has bannerIdx -1, which yields a NULL material and
                // throws inside Init. ApplyBuildPlace already guards remote players this way; the
                // fake peer needs the same, and without it the keep placement below died with
                // "ArgumentNullException ... Material..ctor".
                peer.banner = 1;
                Main.kCPlayers.Add(SyntheticSteamId, peer);
                Main.clientSteamIds[SyntheticClientId] = SyntheticSteamId;

                PlaceKeep(peer);
                GiveTradingPost(peer);

                int actualTeam = (peer.inst != null && peer.inst.PlayerLandmassOwner != null)
                    ? peer.inst.PlayerLandmassOwner.teamId : -1;
                NetLog.Warn("FAKE PEER spawned (dev only): '" + peer.name + "' teamId " + actualTeam +
                            ". The session now behaves as two players; save/load, roster and relations " +
                            "now exercise the two-kingdom paths.");
            }
            catch (Exception ex)
            {
                NetLog.Error("spawning the fake peer", ex);
                peer = null;
            }
        }

        private static void Despawn()
        {
            try
            {
                if (peer == null) return;
                Main.kCPlayers.Remove(SyntheticSteamId);
                Main.clientSteamIds.Remove(SyntheticClientId);

                // Never destroy the local player's object; the fake peer always has its own.
                if (peer.gameObject != null && (Player.inst == null || peer.gameObject != Player.inst.gameObject))
                    UnityEngine.Object.Destroy(peer.gameObject);

                NetLog.Warn("FAKE PEER despawned");
                peer = null;
            }
            catch (Exception ex) { NetLog.Error("despawning the fake peer", ex); }
        }

        /// <summary>
        /// Gives the fake peer a keep on a landmass the local player does not own, so it is a genuinely
        /// separate kingdom. Keep setup reads <c>Player.inst</c>, so the singleton is pointed at the fake
        /// peer for the placement and restored in a finally, the same discipline SessionSave uses.
        /// </summary>
        private static void PlaceKeep(SessionPlayer p)
        {
            int landmass = FindUnownedLandmass();
            if (landmass < 0)
            {
                NetLog.Warn("fake peer: no free landmass for a keep; it has a kingdom object but no keep");
                return;
            }

            Cell site = World.inst.GetCellsData().FirstOrDefault(c =>
                c != null && c.landMassIdx == landmass && c.Type != ResourceType.Water && c.TopMostStructure == null);
            if (site == null)
            {
                NetLog.Warn("fake peer: no buildable cell on landmass " + landmass);
                return;
            }

            Player previous = Player.inst;
            try
            {
                Player.inst = p.inst;

                // Give the kingdom its livery before anything calls Building.Init, which reads it.
                if (p.inst.PlayerLandmassOwner != null && p.inst.PlayerLandmassOwner.bannerIdx < 0)
                {
                    try { p.inst.SetIndexedBanner(Mathf.Max(0, p.banner)); }
                    catch (Exception e) { NetLog.Warn("fake peer: could not set a banner, " + e.Message); }
                }

                Building keep = UnityEngine.Object.Instantiate<Building>(
                    GameState.inst.GetPlaceableByUniqueName(World.keepName));
                keep.Init();
                keep.transform.position = site.Position;
                keep.SendMessage("OnPlayerPlacement", SendMessageOptions.DontRequireReceiver);

                p.inst.PlayerLandmassOwner.TakeOwnership(keep.LandMass());
                p.inst.keep = keep.GetComponent<Keep>();

                // Scope() suppresses the broadcast the build patches would otherwise send. This used
                // to be Bypass(), which does the reverse, and only looked harmless because a solo
                // session has nobody to send to. With NetLoopback on it is not harmless: the keep
                // would come straight back as a peer's placement.
                using (NetApply.Scope())
                    World.inst.Place(keep);

                NetLog.Info("fake peer keep placed on landmass " + landmass + " at " + site.x + "," + site.z);
            }
            finally { Player.inst = previous; }
        }

        /// <summary>
        /// Gold the fake kingdom starts with, so it can afford to buy a shipload from you, and the
        /// amount of each tradeable resource put in its dock, so it has something to sell you back.
        /// Both are round dev numbers, not balance: the point is that neither side of a test trade
        /// fails for the boring reason that somebody was broke or empty.
        /// </summary>
        private const int StartingGold = 5000;
        private const int StockPerResource = 200;

        /// <summary>
        /// Gives the fake peer a stocked, funded dock, which is what turns it from a second kingdom
        /// on the map into one you can actually trade with.
        ///
        /// Cross-player trade needs a FOREIGN dock to exist: a merchant only settles against a dock
        /// whose owner is not its own team, and the trade window only has anything in it if that dock
        /// has both stock and money behind it. Without this the merchant paths could be reasoned
        /// about but never run, which is the whole gap this class exists to close.
        ///
        /// Failure is reported and swallowed, never thrown. This is dev scaffolding; a peer with a
        /// keep but no dock is still useful for the save, roster and relation paths, so a map with
        /// nowhere to put a dock must not take the rest of it down with it.
        /// </summary>
        private static void GiveTradingPost(SessionPlayer p)
        {
            try
            {
                if (p.inst == null || p.inst.PlayerLandmassOwner == null) return;

                p.inst.PlayerLandmassOwner.Gold = StartingGold;

                int landmass = p.inst.keep != null
                    ? p.inst.keep.GetComponent<Building>().LandMass()
                    : FindUnownedLandmass();
                if (landmass < 0) { NetLog.Warn("fake peer: no landmass to put a dock on"); return; }

                Cell site = FindCoastalSite(landmass);
                if (site == null)
                {
                    NetLog.Warn("fake peer: no shore cell free on landmass " + landmass +
                                ", so it has no dock and cannot be traded with");
                    return;
                }

                // Same discipline as PlaceKeep: placement reads Player.inst throughout, so the
                // singleton is aimed at the fake peer for the duration and restored in a finally.
                Player previous = Player.inst;
                Dock dock = null;
                try
                {
                    Player.inst = p.inst;

                    Building b = UnityEngine.Object.Instantiate<Building>(
                        GameState.inst.GetPlaceableByUniqueName(World.dockName));
                    b.Init();
                    b.transform.position = site.Position;
                    b.SendMessage("OnPlayerPlacement", SendMessageOptions.DontRequireReceiver);

                    // Scope(), not Bypass(): Scope marks "this change came from elsewhere" and is what
                    // makes the build patches stay quiet. Bypass does the opposite, it forces depth to
                    // zero so the action IS broadcast. A dev fixture must not go on the wire at all,
                    // and it especially must not while NetLoopback is on, since the loopback would
                    // hand it straight back as a peer's action and place it twice.
                    using (NetApply.Scope())
                    {
                        World.inst.Place(b);
                        if (!b.IsBuilt()) b.CompleteBuild();
                    }

                    dock = b.GetComponent<Dock>();
                }
                finally { Player.inst = previous; }

                if (dock == null) { NetLog.Warn("fake peer: placed a dock that has no Dock component"); return; }

                // Water cells the ship actually aims at. Zero of them means the dock is landlocked,
                // and a merchant sent there would sail forever, so say so plainly rather than
                // leaving a later test to fail as "waiting to reach dock".
                if (dock.dockPositions == null || dock.dockPositions.Count == 0)
                    NetLog.Warn("fake peer: the dock at " + site.x + "," + site.z +
                                " has no water positions, merchants will never arrive there");

                StockDock(dock);

                NetLog.Info("fake peer trading post ready: dock at " + site.x + "," + site.z +
                            " on landmass " + landmass +
                            ", water positions " + (dock.dockPositions != null ? dock.dockPositions.Count : 0) +
                            ", gold " + StartingGold +
                            ". Route one of your merchants here to exercise cross-player trade.");
            }
            catch (Exception ex) { NetLog.Error("giving the fake peer a trading post", ex); }
        }

        /// <summary>
        /// Fills the dock with the resources a merchant can trade in. Gold is skipped because it is
        /// the currency rather than cargo, and DeadVillager because ResourceAmount does not store it.
        /// </summary>
        private static void StockDock(Dock dock)
        {
            if (dock == null || dock.loadingStorageComponent == null) return;

            ResourceAmount stock = new ResourceAmount();
            for (int i = 0; i < (int)FreeResourceType.NumTypes; i++)
            {
                FreeResourceType t = (FreeResourceType)i;
                if (t == FreeResourceType.Gold || t == FreeResourceType.DeadVillager) continue;
                stock.Set(t, StockPerResource);
            }
            dock.loadingStorageComponent.Deposit(ref stock);
        }

        /// <summary>
        /// A free, buildable cell on the given landmass with water in its immediate ring.
        ///
        /// The water test mirrors what the game itself will do: Dock.FillDockPositions walks the ring
        /// around the placed footprint and keeps the cells whose Type is Water, so a site with no
        /// adjacent water yields a dock no ship can dock at.
        /// </summary>
        private static Cell FindCoastalSite(int landmass)
        {
            foreach (Cell c in World.inst.GetCellsData())
            {
                if (c == null || c.landMassIdx != landmass) continue;
                if (c.Type == ResourceType.Water || c.TopMostStructure != null) continue;
                if (HasAdjacentWater(c)) return c;
            }
            return null;
        }

        /// <summary>True when any of the eight neighbouring cells is water.</summary>
        private static bool HasAdjacentWater(Cell c)
        {
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    Cell n = World.inst.GetCellData(c.x + dx, c.z + dz);
                    if (n != null && n.Type == ResourceType.Water) return true;
                }
            return false;
        }

        /// <summary>How many soldiers a test attack brings, and how far from your keep it appears.</summary>
        private const UnitSystem.ArmyType AttackerType = UnitSystem.ArmyType.Default;
        private const float SpawnDistanceFromKeep = 12f;

        /// <summary>
        /// Declares war and lands a fake-peer army next to your keep, so combat actually happens.
        ///
        /// This exists because combat could not be exercised alone at all. Raiders are suppressed in
        /// multiplayer and the fake peer, left to itself, is peaceful, so there was nothing in the
        /// world to fight and every combat path shipped untested. One keypress now produces a real
        /// two-kingdom battle on one machine.
        ///
        /// Deliberately NOT part of spawning the peer. Declaring war closes the trade docks between
        /// the two kingdoms, so a session that went to war on arrival could never test trading.
        /// Spawn the peer and trade first; press this when you want the fighting to start.
        ///
        /// The attackers appear on YOUR island, which means your machine owns the ground and is
        /// therefore the arbiter for the whole fight. To exercise the other side of the model, march
        /// your own army over to the peer's island and watch the authority change hands.
        /// </summary>
        public static void SendHostileArmy()
        {
            if (!Enabled) return;              // hard gate, same as Toggle
            try
            {
                if (World.inst == null || Player.inst == null || Player.inst.PlayerLandmassOwner == null)
                {
                    NetLog.Warn("fake peer: no world yet, enter a game first");
                    return;
                }

                if (peer == null)
                {
                    NetLog.Info("fake peer: spawning it first so it has a kingdom to attack from");
                    Spawn();
                    if (peer == null) return;
                }
                if (peer.inst == null || peer.inst.PlayerLandmassOwner == null) return;

                int localTeam = Player.inst.PlayerLandmassOwner.teamId;
                int peerTeam = peer.inst.PlayerLandmassOwner.teamId;

                // Without a state of war the two sides simply ignore each other and the armies walk
                // past one another, which reads exactly like broken combat sync.
                PlayerRelations.Set(localTeam, peerTeam, World.Relations.Enemy);

                Vector3 spawn = FindAttackerSpawn();
                UnitSystem.Army army = UnitSystem.inst.MakeArmy(spawn, peerTeam, AttackerType, addUnits: true);
                if (army == null)
                {
                    NetLog.Warn("fake peer: MakeArmy returned nothing, no attackers spawned");
                    return;
                }

                NetLog.Warn("FAKE PEER ATTACK: team " + peerTeam + " army " + army.guid +
                            " (" + army.units.Count + " units) spawned at " + spawn +
                            ", now at war with team " + localTeam +
                            ". Your machine owns this ground, so it arbitrates this fight.");
            }
            catch (Exception ex) { NetLog.Error("sending a hostile fake-peer army", ex); }
        }

        /// <summary>
        /// A spot near the local player's keep for attackers to appear, falling back to the keep
        /// itself and then to the world centre, so a missing keep never means no test.
        /// </summary>
        private static Vector3 FindAttackerSpawn()
        {
            Vector3 origin;
            if (Player.inst.keep != null)
                origin = Player.inst.keep.transform.position;
            else
                origin = new Vector3(World.inst.GridWidth * 0.5f, 0f, World.inst.GridHeight * 0.5f);

            // Walk outward until we find dry land, so the attackers do not start in the sea where
            // they cannot path. The keep's own cell is the last resort.
            for (float angle = 0f; angle < 360f; angle += 45f)
            {
                float rad = angle * Mathf.Deg2Rad;
                Vector3 candidate = origin + new Vector3(
                    Mathf.Cos(rad) * SpawnDistanceFromKeep, 0f, Mathf.Sin(rad) * SpawnDistanceFromKeep);

                Cell cell = World.inst.GetCellDataClamped(candidate);
                if (cell != null && cell.Type != ResourceType.Water)
                    return cell.Position;
            }

            return origin;
        }

        /// <summary>
        /// Lowest multiplayer team id not already taken, or -1 if the range is full.
        ///
        /// Bounded by MaxTeamSlots because the per-team arrays are sized for it; a team above that
        /// indexes off the end of every PathCell.
        /// </summary>
        private static int FirstFreeTeam()
        {
            for (int team = PlayerRelations.MpTeamBase; team < MaxTeamSlots; team++)
            {
                bool taken = false;

                foreach (SessionPlayer kp in Main.kCPlayers.Values)
                {
                    if (kp == null || kp.inst == null || kp.inst.PlayerLandmassOwner == null) continue;
                    if (kp.inst.PlayerLandmassOwner.teamId != team) continue;
                    taken = true;
                    break;
                }

                if (!taken) return team;
            }
            return -1;
        }

        /// <summary>First landmass the local player does not own, or -1 if it owns them all.</summary>
        private static int FindUnownedLandmass()
        {
            Player local = Player.inst;
            foreach (int lm in World.inst.GetCellsData().Select(c => c.landMassIdx).Where(i => i >= 0).Distinct())
            {
                bool localOwns = local != null && local.PlayerLandmassOwner != null && local.PlayerLandmassOwner.OwnsLandMass(lm);
                if (!localOwns) return lm;
            }
            return -1;
        }
    }
}
