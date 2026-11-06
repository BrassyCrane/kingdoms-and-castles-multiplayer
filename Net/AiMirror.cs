// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// AI kingdoms on guests' screens.
    ///
    /// AI kingdoms are placed and run on the host only (Main.AIKingdomPlacementHook,
    /// AIKingdomUpdateHook), and none of the host's broadcasts carried them: BuildingWatcher
    /// reports only the host player's own buildings, and PlaceHook stays quiet during an AI tick
    /// so the AI's buildings are not handed to the host. So a guest had no AI kingdom at all,
    /// no owner for the island, no castle, nothing, until a save was loaded.
    ///
    /// HOST. Every AI building is watched the way BuildingWatcher watches the host's own (called
    /// from the same per-building hook), and sent as an <see cref="AiBuildMessage"/> the first
    /// time it is seen and whenever what a guest would see of it changes.
    ///
    /// GUEST. The island has to belong to the AI kingdom before its buildings arrive, because a
    /// building takes its colours from its island's owner (Building.Init reads
    /// World.GetLandmassOwner(LandMass()).BuildingMaterial). So a guest with no owner for that
    /// team makes one, a bare LandmassOwner with the AI's team and banner, the same component
    /// vanilla's AI kingdom prefab carries, without the brain, and gives it the island. Then the
    /// building is made the way ApplyBuildPlace makes a player's, except that it goes into this
    /// machine's own Player, as an AI building does in vanilla (and as
    /// Main.GetPlayerByBuilding resolves an AI team). After that the same message just updates
    /// it, through the same code player snapshots use.
    ///
    /// A guest never simulates any of it: nobody lives there on the guest's machine, and the job
    /// hooks leave foreign kingdoms' jobs alone.
    /// </summary>
    public static class AiMirror
    {
        /// <summary>How often one AI building is looked at, and at most how often it is sent.</summary>
        private const long CheckIntervalMs = 500;

        private static readonly FieldInfo ResourceProgressField =
            typeof(Building).GetField("resourceProgress", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo BuildingVisibleMethod =
            typeof(FogOfWar).GetMethod("BuildingVisible", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

        // ---- host --------------------------------------------------------------------------

        /// <summary>
        /// An unchanged building is sent again this often anyway. A guest still loading the world
        /// when it was first sent has nowhere to put it, and a finished castle never changes again.
        /// </summary>
        private const long ResendMs = 30000;

        private class Watched
        {
            public long NextCheckMs;
            public long SentMs;
            public string Signature;
        }

        private static readonly Dictionary<Guid, Watched> watched = new Dictionary<Guid, Watched>();

        // ---- guest -------------------------------------------------------------------------

        /// <summary>Owners this machine made for AI kingdoms, by team, to undo at session end.</summary>
        private static readonly Dictionary<int, LandmassOwner> madeOwners = new Dictionary<int, LandmassOwner>();

        private static readonly Dictionary<Guid, Building> mirrored = new Dictionary<Guid, Building>();
        // BuildingSaveData contains the host's seenByPlayer bit. That bit is not portable: each
        // guest has their own explored map. Remember which AI buildings have had that host bit
        // replaced with this machine's fog result so later updates preserve local discovery.
        private static readonly HashSet<Guid> fogInitialised = new HashSet<Guid>();
        private static readonly HashSet<string> warnedNoPrefab = new HashSet<string>();

        private static long NowMs { get { return DateTimeOffset.Now.ToUnixTimeMilliseconds(); } }

        /// <summary>
        /// Host: looks at one building, and sends it if it is an AI kingdom's and has changed.
        /// Called per building per frame from Main.BuildingUpdateHook, so everything but AI
        /// buildings leaves at the first line or two.
        /// </summary>
        public static void Poll(Building b)
        {
            // Not from the lobby: a guest there is still holding the world it had before the
            // save arrives, and anything built into it is left behind when the save replaces it.
            if (b == null || !NetRouter.IsServer || !ClockSync.InGame) return;

            int team;
            try { team = b.TeamID(); }
            catch { return; }
            if (team < AiDiplomacy.FirstAiTeam || team > AiDiplomacy.LastAiTeam) return;

            try
            {
                long now = NowMs;
                Watched w;
                if (watched.TryGetValue(b.guid, out w) && now < w.NextCheckMs) return;

                AIKingdom k = AiDiplomacy.KingdomFor(team);
                if (k == null || k.LandmassOwner == null) return;

                if (w == null) watched[b.guid] = w = new Watched();
                w.NextCheckMs = now + CheckIntervalMs;

                BuildingState s = Capture(b);
                float rp = ReadResourceProgress(b);
                string signature = Signature(s, rp);
                if (signature == w.Signature && now - w.SentMs < ResendMs) return;
                w.Signature = signature;
                w.SentMs = now;

                NetRouter.Broadcast(new AiBuildMessage
                {
                    AiTeam = team,
                    Banner = k.LandmassOwner.bannerIdx,
                    State = s,
                    ResourceProgress = rp
                });
            }
            catch (Exception ex) { NetLog.Error("AI building watcher", ex); }
        }

        /// <summary>Guest: one of an AI kingdom's buildings, new or changed.</summary>
        public static void Apply(AiBuildMessage m)
        {
            if (NetRouter.IsServer || m == null || m.State == null) return;   // the host's own broadcast
            if (!ClockSync.InGame) return;   // not into a world that is about to be replaced; see Poll

            BuildingState s = m.State;
            try
            {
                Building b = Find(s.Guid);
                if (b == null)
                {
                    EnsureOwner(m.AiTeam, m.Banner, null, LandmassAt(s.GlobalPosition));
                    b = Create(s);   // asks for a repaint itself
                    if (b == null) return;
                }

                bool wasBuilt = b.IsBuilt();
                bool hadLocalFog = fogInitialised.Contains(s.Guid);
                bool locallySeen = hadLocalFog && b.seenByPlayer;
                NetRegistrations.ApplyBuildingState(b, s, m.ResourceProgress);
                if (hadLocalFog) b.seenByPlayer = locallySeen;
                mirrored[s.Guid] = b;
                RefreshFog(b, !hadLocalFog);

                // A building that has just finished shows its full model, flags included, for the
                // first time. Only then: every other update leaves the flags as they were, and a
                // repaint on each of them would run the whole-world sweep every few seconds.
                if (!wasBuilt && b.IsBuilt()) Main.MarkBannersDirty();
            }
            catch (Exception ex) { NetLog.Error("AI building " + s.UniqueName + " " + s.Guid, ex); }
        }

        /// <summary>
        /// Guest: makes sure this machine has an owner for AI team <paramref name="team"/>, flying
        /// <paramref name="banner"/>, owning island <paramref name="landmass"/>, and (when a name
        /// is given) that the island carries the kingdom's name. Also called for every kingdom in
        /// the host's roster.
        /// </summary>
        public static void EnsureOwner(int team, int banner, string name, int landmass)
        {
            if (NetRouter.IsServer) return;

            // In the lobby this machine's world is not the game's yet: an owner made there would
            // claim islands in a world the save is about to replace, and outlive it.
            if (!ClockSync.InGame) return;
            if (team < AiDiplomacy.FirstAiTeam || team > AiDiplomacy.LastAiTeam) return;

            try
            {
                LandmassOwner lmo = World.GetLandmassOwnerByTeamId(team);
                bool ours = false;

                if (lmo == null)
                {
                    GameObject go = new GameObject("AI Kingdom " + team + " (from host)");
                    lmo = go.AddComponent<LandmassOwner>();
                    lmo.teamId = team;
                    madeOwners[team] = lmo;
                    ours = true;
                    NetLog.Info("AI mirror: set up AI kingdom team " + team + " (banner " + banner + ")");
                }
                else ours = madeOwners.ContainsKey(team);

                // Colours. Only on an owner made here: one the game made itself (from a loaded
                // save) already has them, and a missing livery would leave its materials null,
                // which LandmassOwner.Update does not survive.
                if (ours && (lmo.bannerIdx != banner || lmo.BuildingMaterial == null))
                    Main.SetKingdomBanner(lmo, ValidBanner(banner), "AI mirror team " + team);

                if (landmass >= 0 && !lmo.OwnsLandMass(landmass))
                {
                    LandmassOwner current = World.GetLandmassOwner(landmass);
                    if (current == null || madeOwners.ContainsValue(current))
                    {
                        lmo.TakeOwnership(landmass);
                        Main.MarkBannersDirty();
                        NetLog.Info("AI mirror: island " + landmass + " belongs to AI team " + team);
                    }
                    else if (current != lmo)
                    {
                        NetLog.Warn("AI mirror: island " + landmass + " is already team " + current.teamId +
                                    "'s here; not giving it to AI team " + team);
                    }
                }

                if (!string.IsNullOrEmpty(name) && landmass >= 0 && Player.inst != null &&
                    Player.inst.LandMassNames != null && landmass < Player.inst.LandMassNames.Count)
                    Player.inst.LandMassNames[landmass] = name + " (AI)";
            }
            catch (Exception ex) { NetLog.Error("setting up AI kingdom team " + team, ex); }
        }

        /// <summary>For a session ending: forget what was sent, and undo the owners made here.</summary>
        public static void Reset()
        {
            watched.Clear();
            mirrored.Clear();
            fogInitialised.Clear();
            warnedNoPrefab.Clear();

            foreach (LandmassOwner lmo in madeOwners.Values)
            {
                try
                {
                    if (lmo == null) continue;

                    if (lmo.ownedLandMasses != null && World.LandMassOwnerLookup != null)
                        for (int i = 0; i < lmo.ownedLandMasses.Count; i++)
                        {
                            int lm = lmo.ownedLandMasses.data[i];
                            if (lm >= 0 && lm < World.LandMassOwnerLookup.Length && World.LandMassOwnerLookup[lm] == lmo)
                                World.LandMassOwnerLookup[lm] = null;
                        }

                    lmo.ReleaseOwnership();
                    UnityEngine.Object.Destroy(lmo.gameObject);
                }
                catch (Exception ex) { NetLog.Error("removing an AI kingdom set up from the host", ex); }
            }
            madeOwners.Clear();
        }

        // ---- guest helpers --------------------------------------------------------------------

        private static Building Find(Guid id)
        {
            Building b;
            if (mirrored.TryGetValue(id, out b))
            {
                if (b != null) return b;
                mirrored.Remove(id);   // destroyed since
            }

            // From a loaded save, AI buildings come back inside the host's record, so look there
            // (and everywhere else) before deciding this one is new.
            if (Player.inst != null)
            {
                b = Player.inst.GetBuilding(id);
                if (b != null) return b;
            }

            foreach (SessionPlayer p in Main.kCPlayers.Values)
            {
                if (p == null || p.inst == null || p.inst == Player.inst) continue;
                b = p.inst.GetBuilding(id);
                if (b != null) return b;
            }

            return null;
        }

        /// <summary>
        /// Guest: makes one of an AI kingdom's buildings here, from the host's description of it,
        /// the same way a player's building placed elsewhere is made. It goes into this machine's own
        /// Player, as AI buildings do in vanilla.
        /// </summary>
        private static Building Create(BuildingState s)
        {
            Building prefab = GameState.inst != null ? GameState.inst.GetPlaceableByUniqueName(s.UniqueName) : null;
            if (prefab == null)
            {
                if (warnedNoPrefab.Add(s.UniqueName ?? ""))
                    NetLog.Info("AI mirror: no prefab for '" + s.UniqueName + "', not shown on this machine");
                return null;
            }

            Building building = UnityEngine.Object.Instantiate<Building>(prefab);
            building.transform.position = s.GlobalPosition;

            // Position first: Init colours the building from whoever owns the island under it,
            // which EnsureOwner has just made the AI kingdom.
            building.Init();
            building.transform.SetParent(Player.inst.buildingContainer.transform, true);

            Building.BuildingSaveData data = s.ToSaveData();
            data.Unpack(building);
            Player.inst.AddBuilding(building);
            World.inst.PlaceFromLoad(building);
            data.UnpackStage2(building);
            mirrored[s.Guid] = building;
            Main.MarkBannersDirty();
            NetLog.Info("AI mirror: " + s.UniqueName + " for AI team " + building.TeamID() + " at " + s.GlobalPosition);
            return building;
        }

        /// <summary>
        /// Re-evaluates mirrored AI buildings against this guest's fog. Called periodically so an
        /// exploration ship revealing an island also reveals its kingdom without another building
        /// update having to arrive from the host.
        /// </summary>
        public static void RefreshFog()
        {
            if (NetRouter.IsServer || !ClockSync.InGame || FogOfWar.inst == null || BuildingVisibleMethod == null) return;

            foreach (Building b in new List<Building>(mirrored.Values))
                if (b != null) RefreshFog(b, !fogInitialised.Contains(b.guid));
        }

        /// <summary>
        /// Shows or hides one mirrored AI building by this guest's own fog. The first time, replaces
        /// the host's "seen" bit with this machine's own, since each player explores their own map.
        /// </summary>
        private static void RefreshFog(Building building, bool initialiseLocalDiscovery)
        {
            if (building == null || FogOfWar.inst == null || BuildingVisibleMethod == null) return;

            try
            {
                bool visibleNow = (bool)BuildingVisibleMethod.Invoke(FogOfWar.inst, new object[] { building });
                if (initialiseLocalDiscovery)
                {
                    // Unpack/ApplyBuildingState copied the host's discovery bit. Replace it before
                    // SetVisibleForFog(false), whose vanilla behaviour intentionally preserves a
                    // building that this particular player has already discovered.
                    building.seenByPlayer = visibleNow;
                    fogInitialised.Add(building.guid);
                }

                building.SetVisibleForFog(visibleNow);
            }
            catch (Exception ex) { NetLog.Error("refreshing AI building fog", ex); }
        }

        /// <summary>The island under a position, or -1.</summary>
        private static int LandmassAt(Vector3 position)
        {
            try
            {
                Cell c = World.inst.GetCellDataClamped(position);
                return c != null ? c.landMassIdx : -1;
            }
            catch { return -1; }
        }

        /// <summary>The banner number if this game has that livery, otherwise the first one.</summary>
        private static int ValidBanner(int banner)
        {
            try
            {
                int count = World.inst.liverySets.Count;
                if (banner >= 0 && banner < count) return banner;
            }
            catch { }
            return 0;
        }

        // ---- host helpers ------------------------------------------------------------------

        private static BuildingState Capture(Building b)
        {
            return new BuildingState
            {
                Guid = b.guid,
                UniqueName = b.UniqueName,
                CustomName = b.customName,
                Rotation = b.transform.GetChild(0).rotation,
                GlobalPosition = b.transform.position,
                LocalPosition = b.transform.GetChild(0).localPosition,
                Built = b.IsBuilt(),
                Placed = b.IsPlaced(),
                Open = b.Open,
                DoBuildAnimation = b.doBuildAnimation,
                ConstructionPaused = b.constructionPaused,
                ConstructionProgress = b.constructionProgress,
                Life = b.Life,
                ModifiedMaxLife = b.ModifiedMaxLife,
                YearBuilt = b.YearBuilt,
                DecayProtection = b.decayProtection,
                SeenByPlayer = b.seenByPlayer
            };
        }

        /// <summary>The building's private production progress, read so it can travel with the building.</summary>
        private static float ReadResourceProgress(Building b)
        {
            if (ResourceProgressField == null) return 0f;
            try { return (float)ResourceProgressField.GetValue(b); }
            catch { return 0f; }
        }

        /// <summary>
        /// What a guest would notice about the building, as one comparable string. Coarser than
        /// BuildingWatcher on purpose: production progress and decay move every frame on a
        /// finished building, and nobody watching another kingdom can see either, so they ride
        /// along with a real change instead of causing one. Construction is counted in 2% steps.
        /// </summary>
        private static string Signature(BuildingState s, float rp)
        {
            return s.UniqueName + "|" + s.CustomName + "|" + s.Built + "|" + s.Placed + "|" + s.Open + "|" +
                   s.ConstructionPaused + "|" + Mathf.RoundToInt(s.ConstructionProgress * 50f) + "|" +
                   Mathf.RoundToInt(s.Life) + "|" + Mathf.RoundToInt(s.ModifiedMaxLife) + "|" + s.YearBuilt + "|" +
                   s.GlobalPosition + "|" + s.Rotation;
        }
    }
}
