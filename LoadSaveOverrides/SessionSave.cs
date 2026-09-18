using Assets.Code;
using KaCMultiplayer.Net;
using Riptide;
using Riptide.Transports;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace KaCMultiplayer.LoadSaveOverrides
{
    [Serializable]
    public class SessionSave : LoadSaveContainer
    {
        public Dictionary<string, Player.PlayerSaveData> players = new Dictionary<string, Player.PlayerSaveData>();
        public Dictionary<string, string> kingdomNames = new Dictionary<string, string>();

        /// <summary>
        /// Relations carried from the save dictionary into <see cref="Unpack"/>, keyed by the packed
        /// team pair PlayerRelations uses.
        ///
        /// [NonSerialized] on purpose. This class is no longer written to disk (a save is a stock
        /// container now, with the mod's state in its dictionary), and the only SessionSave objects
        /// still DESERIALISED are old pre-migration saves, which predate this field. Marking it
        /// keeps it out of the serialised shape entirely, so an old save cannot fail to load over a
        /// field it never had. Null there simply means no relations were recorded, which for an old
        /// save is the truth.
        /// </summary>
        [NonSerialized]
        public Dictionary<long, World.Relations> savedRelations;

        // True only while Unpack() is running. Used by hooks (e.g. ShipUpdatePathingHook) to
        // suppress mid-load work that the world isn't ready for yet (ship pathing needs the
        // water nav grid, which isn't built until the unpack finishes).
        public static bool Unpacking = false;

        /// <summary>
        /// Builds the save record for a multiplayer session: everything vanilla stores, plus one
        /// <see cref="Player.PlayerSaveData"/> per connected kingdom instead of the single one
        /// the base container holds.
        ///
        /// It must be an <c>override</c> rather than a <c>new</c> method. The game's
        /// <c>LoadSave.Save</c> calls <c>Pack</c> virtually on whatever container it constructed,
        /// so overriding is what lets a one-instruction transpiler
        /// (<see cref="Main.LoadSaveSaveHook"/>) swap in this type and leave the rest of the save
        /// routine alone. Hiding it with <c>new</c> would send the call to the base method and
        /// silently save a single-kingdom file.
        /// </summary>
        /// <summary>
        /// Builds a transient SessionSave from a stock <see cref="LoadSaveContainer"/> plus the mod's
        /// session block, so the existing <see cref="Unpack"/> can run on a new-format save (a vanilla
        /// container whose extra kingdoms live in the CustomSaveData dictionary).
        ///
        /// This object is never serialised to disk; it exists only to reuse the proven load path
        /// unchanged. The file itself stays a stock container, which is the whole point of the
        /// migration. See docs/save-migration-plan.md.
        /// </summary>
        public static SessionSave FromContainer(LoadSaveContainer c, ModSessionData mod)
        {
            SessionSave ss = new SessionSave();

            // Copy across every inherited section Unpack reads, so it behaves exactly as it does for
            // an old serialised SessionSave. These were packed by the game's own vanilla Pack.
            ss.CameraSaveData = c.CameraSaveData;

            // VR was the one section the old hand-written unpack forgot, so multiplayer silently
            // never restored it. It is carried across now for the same reason every other section
            // is: base.Unpack reads it, and anything base.Unpack reads has to be here.
            ss.VRSaveData = c.VRSaveData;
            ss.WorldSaveData = c.WorldSaveData;
            ss.FishSystemSaveData = c.FishSystemSaveData;
            ss.TownNameSaveData = c.TownNameSaveData;
            ss.FreeResourceManagerSaveData = c.FreeResourceManagerSaveData;
            ss.JobSystemSaveData = c.JobSystemSaveData;
            ss.WeatherSaveData = c.WeatherSaveData;
            ss.FireManagerSaveData = c.FireManagerSaveData;
            ss.DragonSpawnSaveData = c.DragonSpawnSaveData;
            ss.UnitSystemSaveData = c.UnitSystemSaveData;
            ss.SiegeMonsterSaveData = c.SiegeMonsterSaveData;
            ss.SiegeCatapultSystemSaveData = c.SiegeCatapultSystemSaveData;
            ss.ShipSystemSaveData = c.ShipSystemSaveData;
            ss.CartSystemSaveData = c.CartSystemSaveData;
            ss.RaidSystemSaveData2 = c.RaidSystemSaveData2;
            ss.OrdersManagerSaveData = c.OrdersManagerSaveData;
            ss.AIBrainsSaveData = c.AIBrainsSaveData;
            ss.CustomSaveData = c.CustomSaveData;
            ss.PlayerSaveData = c.PlayerSaveData;   // the saver's local kingdom; unused by our Unpack, copied for completeness

            // The mod's own state, from the dictionary block instead of subclass fields.
            ss.players = mod.players ?? new Dictionary<string, Player.PlayerSaveData>();
            ss.kingdomNames = mod.kingdomNames ?? new Dictionary<string, string>();
            ss.savedRelations = mod.relations;
            return ss;
        }

        public override LoadSaveContainer Pack(object obj)
        {
            this.CameraSaveData = new Cam.CamSaveData().Pack(Cam.inst);
            this.TownNameSaveData = new TownNameUI.TownNameSaveData().Pack(TownNameUI.inst);

            Main.helper.Log($"packing save for {Main.kCPlayers.Count} kingdom(s)");

            foreach (var player in Main.kCPlayers.Values)
            {
                // One failing kingdom must not abort the save. Packing runs on the main thread
                // inside the game's autosave call, so an escaping exception stalls the whole
                // simulation, the "freeze at year N" report. Losing one kingdom from a save is
                // recoverable; a hung session is not.
                try
                {
                    this.players.Add(player.steamId, new Player.PlayerSaveData().Pack(player.inst));
                }
                catch (Exception e)
                {
                    Main.LogEx($"packing kingdom '{player.name}' ({player.steamId})", e);
                    continue;
                }

                // Your own SessionPlayer.kingdomName is only ever written from other players' packets,
                // so for yourself it stays blank, take the real town name from TownNameUI
                // instead. Remote players' names arrive over the wire, so theirs is already right.
                bool isLocal = player.steamId == Main.PlayerSteamID;
                bool haveTownName = TownNameUI.inst != null && !string.IsNullOrWhiteSpace(TownNameUI.inst.townName);
                kingdomNames.Add(player.steamId, (isLocal && haveTownName) ? TownNameUI.inst.townName : player.kingdomName);
            }

            // The terrain and the things growing on it.
            this.WorldSaveData = new World.WorldSaveData().Pack(World.inst);
            this.WeatherSaveData = new Weather.WeatherSaveData().Pack(Weather.inst);
            this.FishSystemSaveData = new FishSystem.FishSystemSaveData().Pack(FishSystem.inst);

            // Kingdom-wide bookkeeping that is not owned by any one player.
            this.JobSystemSaveData = new JobSystem.JobSystemSaveData().Pack(JobSystem.inst);
            this.FreeResourceManagerSaveData = new FreeResourceManager.FreeResourceManagerSaveData().Pack(FreeResourceManager.inst);
            this.OrdersManagerSaveData = new OrdersManager.OrdersManagerSaveData().Pack(OrdersManager.inst);

            // Everything that moves. Ships are pruned first, see PruneDestroyedShips.
            PruneDestroyedShips();
            this.ShipSystemSaveData = new ShipSystem.ShipSystemSaveData().Pack(ShipSystem.inst);
            this.CartSystemSaveData = new CartSystem.CartSystemSaveData().Pack(CartSystem.inst);
            this.UnitSystemSaveData = new UnitSystem.UnitSystemSaveData().Pack(UnitSystem.inst);

            // Threats, and the AI that drives them.
            this.RaidSystemSaveData2 = new RaiderSystem.RaiderSystemSaveData2().Pack(RaiderSystem.inst);
            this.SiegeMonsterSaveData = new SiegeMonster.SiegeMonsterSaveData().Pack(null);
            this.SiegeCatapultSystemSaveData = new SiegeCatapultSystem.SiegeCatapultSystemSaveData().Pack(SiegeCatapultSystem.inst);
            this.DragonSpawnSaveData = new DragonSpawn.DragonSpawnSaveData().Pack(DragonSpawn.inst);
            this.FireManagerSaveData = new FireManager.FireManagerSaveData().Pack(FireManager.inst);
            this.AIBrainsSaveData = new AIBrainsContainer.SaveData().Pack(AIBrainsContainer.inst);

            // Other mods' data. Not ours to interpret, carried through as-is.
            this.CustomSaveData = LoadSave.CustomSaveData_DontAccessDirectly;

            return this;
        }

        // Remove destroyed/null ship entries from ShipSystem before the vanilla ship pack runs.
        // ShipSystemSaveData.Pack calls GetComponent<…>() on every entry in ShipSystem.ships; a
        // destroyed Unity object there throws (managed-to-native GetComponentFastPath), which aborts
        // the WHOLE save, and it was firing on every host autosave. Ships normally remove themselves
        // in OnDisableInternal, but a duplicate/orphan ship (the MP double-spawn bug) could leave a
        // corpse behind. Iterate backwards with RemoveAtSwap so indices stay valid; the Unity-
        // overloaded `== null` is true for both real-null and destroyed objects.
        public static void PruneDestroyedShips()
        {
            try
            {
                ShipSystem ss = ShipSystem.inst;
                if (ss == null) return;

                int pruned = 0;
                if (ss.ships != null)
                    for (int i = ss.ships.Count - 1; i >= 0; i--)
                    {
                        ShipBase sb = ss.ships.data[i];
                        if (sb == null) { ss.ships.RemoveAtSwap(i); pruned++; }
                    }

                if (ss.playerMerchantShips != null)
                    for (int i = ss.playerMerchantShips.Count - 1; i >= 0; i--)
                    {
                        ShipBase sb = ss.playerMerchantShips.data[i];
                        if (sb == null) ss.playerMerchantShips.RemoveAtSwap(i);
                    }

                if (pruned > 0)
                    Main.helper.Log($"[SHIP] Pruned {pruned} destroyed ship(s) from ShipSystem before save");
            }
            catch (Exception e) { Main.helper.Log("[SHIP] PruneDestroyedShips error: " + e.Message); }
        }

        /// <summary>
        /// Restores a multiplayer session, by letting the GAME restore the world and then adding
        /// the other players' kingdoms around it.
        ///
        /// REWRITTEN 2026-09-06, on Michael Peterson's advice. This method used to reimplement the
        /// whole of LoadSaveContainer.Unpack by hand: it called Unpack on the camera, the world, the
        /// fish, the town name, then on FreeResourceManager, JobSystem, Weather, FireManager,
        /// DragonSpawn, UnitSystem, SiegeMonster, SiegeCatapult, ShipSystem, CartSystem,
        /// RaidSystem, OrdersManager and AIBrains, and finished with the upscale, visibility,
        /// material and tick-delay tail. Every one of those lines was a copy of a line in the game's
        /// own method.
        ///
        /// That is a slow-motion bug. The day LionShield adds a subsystem to their Unpack, single
        /// player restores it and multiplayer silently does not, and nothing anywhere reports a
        /// problem. It had already happened once: the copy omitted VRSaveData, so VR state was never
        /// restored in a multiplayer save. The mod is a guest in their save format and should be
        /// asking their code to do the work.
        ///
        /// So the shape is now: prepare, delegate, extend.
        ///
        ///   PREPARE  Register who owns which team, build the SessionPlayer for each saved kingdom,
        ///            assert the local player's saved team before anything attaches ownership to it,
        ///            and reset every kingdom rather than only the local one, which is the single
        ///            thing base.Unpack cannot know to do.
        ///
        ///            The last step is the important one: PlayerSaveData is pointed at the LOCAL
        ///            player's saved kingdom. The container's own copy belongs to whoever wrote the
        ///            file, so on a joining client base.Unpack would otherwise restore the HOST's
        ///            kingdom into their Player.inst.
        ///
        ///   DELEGATE base.Unpack does the world, the local kingdom and every subsystem, exactly as
        ///            it does in single player, including anything added to it in future.
        ///
        ///   EXTEND   Restore the other kingdoms, relink keeps, put the kingdom name back and
        ///            reinstate declared wars.
        ///
        /// KNOWN ORDERING RISK, stated rather than hidden. The old code restored every kingdom
        /// BEFORE the subsystem unpacks; the other kingdoms now land after them. Jobs are owned per
        /// player, so a remote kingdom's jobs may need re-registering. This is why RelinkKeeps and
        /// the per-player summary below log building counts and keep links: a remote kingdom that
        /// came back without them is the symptom to look for. Verify on a real save/load with two
        /// kingdoms before trusting it.
        /// </summary>
        public override object Unpack(object obj)
        {
            Unpacking = true;
            try
            {
                string localSteamId = SteamUser.GetSteamID().ToString();

                PrepareKingdoms(localSteamId);

                // Hand the local player's saved kingdom to the game's own unpack. Without this a
                // joining client restores the save author's kingdom as their own.
                Player.PlayerSaveData localData;
                if (!players.TryGetValue(localSteamId, out localData) || localData == null)
                    throw new InvalidOperationException(
                        $"no saved kingdom matches this Steam id ({localSteamId}); the save holds " +
                        string.Join(", ", players.Keys.ToArray()));

                this.PlayerSaveData = localData;

                // The game restores the world, the local kingdom and every subsystem.
                object result = base.Unpack(obj);

                RestoreOtherKingdoms(localSteamId);
                RelinkKeeps();
                LogLoadedKingdoms();

                SessionPlayer localPlayer = Main.kCPlayers[localSteamId];
                localPlayer.banner = Player.inst.PlayerLandmassOwner.bannerIdx;
                localPlayer.kingdomName = TownNameUI.inst.townName;

                // Relations last, once every kingdom exists and owns its landmasses. Restoring
                // earlier would rebake the pathing gates and re-apply dock policy against teams
                // still being assembled, so the war would be reinstated against a half-built world.
                KaCMultiplayer.Net.PlayerRelations.Restore(savedRelations);

                // Quiet: the loud form announces a royal decree on every single load, and with a
                // blank value it also overwrites the name base.Unpack just restored.
                string finalName = kingdomNames.ContainsKey(Main.PlayerSteamID) ? kingdomNames[Main.PlayerSteamID] : null;
                if (!string.IsNullOrWhiteSpace(finalName))
                    TownNameUI.inst.SetTownNameQuiet(finalName);

                return result;
            }
            finally
            {
                Unpacking = false;
            }
        }

        /// <summary>
        /// Everything that has to be true before the game's own unpack runs.
        ///
        /// Team identity first, because ownership attaches to a team id the moment buildings are
        /// restored, and the handshake may have handed out the fresh-game id before this save (and
        /// therefore this registry) existed.
        /// </summary>
        private void PrepareKingdoms(string localSteamId)
        {
            // LOAD-IDENTITY FRAMEWORK: register every saved player's steamId -> saved teamId. From
            // here on ALL teamId assignment (SessionPlayer ctor, handshake) resolves through this
            // registry, so each returning player gets the kingdom they saved with regardless of join
            // order, and absent players' kingdoms keep their real team.
            foreach (var kvp in players)
            {
                int savedTeam = (kvp.Value != null && kvp.Value.playerLandmassOwnerSaveData != null)
                    ? kvp.Value.playerLandmassOwnerSaveData.teamId : -1;
                if (savedTeam > 0) LoadIdentity.RegisterSavedPlayer(kvp.Key, savedTeam);
            }

            foreach (var kvp in players)
            {
                SessionPlayer player;
                if (Main.kCPlayers.TryGetValue(kvp.Key, out player)) continue;

                if (kvp.Key == localSteamId)
                {
                    // THE ORPHAN-PHANTOM FIX: the LOCAL player's entry must wrap the real
                    // Player.inst (the constructor does that when the id matches our client id). A
                    // placeholder here instead would leave the saved kingdom in Player.inst while
                    // the registry pointed at an empty stand-in with no keep.
                    player = new SessionPlayer(SteamFriends.GetPersonaName(), NetClient.client.Id, kvp.Key);
                    Main.helper.Log($"[LOADID] local player entry wired to Player.inst ({kvp.Key})");
                }
                else
                {
                    // Absent player: a GHOST placeholder holding their saved kingdom under their
                    // SAVED teamId (the ctor resolves it via LoadIdentity now that the registry is
                    // filled). Ghosts do not block lobby Start; if the real player reconnects, they
                    // take over.
                    player = new SessionPlayer("", 50, kvp.Key);
                    player.isGhost = true;
                }

                player.kingdomName = kingdomNames[kvp.Key];
                Main.kCPlayers.Add(kvp.Key, player);
            }

            if (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
            {
                Player.inst.PlayerLandmassOwner.teamId = LoadIdentity.TeamIdFor(localSteamId, NetClient.client.Id);
                Main.helper.Log($"[LOADID] local player teamId asserted -> {Player.inst.PlayerLandmassOwner.teamId}");
            }

            // base.Unpack resets only Player.inst, because single player has only one kingdom. The
            // others have to be cleared here or they load on top of their previous contents.
            //
            // Guarded per kingdom, the same way Pack is: a kingdom that is missing or fails to reset
            // must not abort the LOAD. The throw would escape Unpack and the save would refuse to
            // open, which is far worse than one kingdom starting dirty.
            foreach (var player in Main.kCPlayers.Values)
            {
                try
                {
                    if (player != null && player.inst != null && player.inst != Player.inst)
                        player.inst.Reset();
                }
                catch (Exception ex)
                {
                    Main.LogEx("resetting a kingdom during load", ex);
                }
            }
        }

        /// <summary>Restores every saved kingdom except the local one, which base.Unpack did.</summary>
        private void RestoreOtherKingdoms(string localSteamId)
        {
            foreach (var kvp in players)
            {
                if (kvp.Key == localSteamId) continue;
                RestoreAbsentPlayer(kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// Links every loaded kingdom to its keep by scanning its own buildings.
        ///
        /// Belt and braces. The per-building hook only assigns Player.keep when a building's team id
        /// matches the player's, which can silently miss after a load, and then the game offers a
        /// brand-new keep instead of continuing the saved kingdom.
        /// </summary>
        private void RelinkKeeps()
        {
            try
            {
                foreach (var kp in Main.kCPlayers.Values)
                {
                    Player pl = kp.inst;
                    if (pl == null || pl.keep != null || pl.Buildings == null) continue;

                    for (int i = 0; i < pl.Buildings.Count; i++)
                    {
                        Building b = pl.Buildings.data[i];
                        Keep k = b == null ? null : b.GetComponent<Keep>();
                        if (k == null) continue;

                        pl.keep = k;
                        Main.helper.Log($"Relinked keep for player {kp.name} (teamId {pl.PlayerLandmassOwner?.teamId})");
                        break;
                    }
                }
            }
            catch (Exception e) { Main.helper.Log("Keep relink error: " + e.Message); }
        }

        /// <summary>
        /// One line per kingdom confirming it came back whole.
        ///
        /// This is the check for the ordering risk noted on Unpack: a remote kingdom that restored
        /// after the subsystem unpacks and lost something shows up here as buildings=0 or
        /// keepLinked=False, rather than as a confusing report three sessions later.
        /// </summary>
        private void LogLoadedKingdoms()
        {
            try
            {
                foreach (var kp in Main.kCPlayers.Values)
                {
                    Player pl = kp.inst;
                    int bc = (pl != null && pl.Buildings != null) ? pl.Buildings.Count : -1;
                    Main.helper.Log($"Loaded player '{kp.name}' steamId={kp.steamId} teamId={pl?.PlayerLandmassOwner?.teamId} buildings={bc} keepLinked={(pl != null && pl.keep != null)} isLocal={(pl == Player.inst)}");
                }
            }
            catch (Exception e) { Main.helper.Log("Player summary log error: " + e.Message); }
        }

        /// <summary>
        /// Restores a kingdom whose owner is not in the session, into a placeholder
        /// <see cref="SessionPlayer"/> so their buildings, land and name survive the load.
        ///
        /// The game's save code reads <c>Player.inst</c> throughout, so the singleton is aimed at
        /// the placeholder for the duration and put back afterwards, in a <c>finally</c>, since
        /// leaving it aimed at someone else's kingdom silently corrupts every later read of the
        /// local player, and an exception in the middle of an unpack is exactly when that
        /// happens.
        /// </summary>
        private void RestoreAbsentPlayer(string steamId, Player.PlayerSaveData saved)
        {
            SessionPlayer player;
            if (!Main.kCPlayers.TryGetValue(steamId, out player))
            {
                // The constructor resolves their saved teamId through LoadIdentity, which the
                // registry pass at the top of Unpack has already filled in.
                player = new SessionPlayer("", 50, steamId);
                player.isGhost = true;
                Main.kCPlayers.Add(steamId, player);
            }

            Player previous = Player.inst;
            try
            {
                Player.inst = player.inst;
                saved.Unpack(player.inst);
            }
            catch (Exception ex)
            {
                Main.LogEx($"unpacking absent kingdom {steamId}", ex);
                throw;
            }
            finally
            {
                Player.inst = previous;
            }

            player.banner = player.inst.PlayerLandmassOwner.bannerIdx;

            // Their own saved name. Reading the local town name here was the bug that made every
            // absent player appear under your kingdom's name.
            if (kingdomNames.ContainsKey(steamId))
                player.kingdomName = kingdomNames[steamId];
        }

    }
}
