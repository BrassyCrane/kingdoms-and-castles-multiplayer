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

        /// <summary>Declared-but-not-yet-started wars, in seasons remaining. See ModSaveData.</summary>
        public Dictionary<long, int> savedPendingWars;

        // True only while Unpack() is running. Used by hooks (e.g. ShipUpdatePathingHook) to
        // suppress mid-load work that the world isn't ready for yet (ship pathing needs the
        // water nav grid, which isn't built until the unpack finishes).
        public static bool Unpacking = false;

        // Other kingdoms append to the job registry that base.Unpack already populated.
        public static bool RestoringAdditionalKingdom { get; private set; }

        [Harmony.HarmonyPatch(typeof(JobSystem), "InitJobList")]
        public class PreserveLoadedJobsHook
        {
            public static bool Prefix()
            {
                return !RestoringAdditionalKingdom;
            }
        }

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
            ss.savedPendingWars = mod.pendingWars;

            // Restored here rather than carried on the container, because prices live in one static
            // table for the whole session rather than per saved kingdom, and there is nothing for
            // Unpack to hand them to.
            KaCMultiplayer.Trade.ExportPrices.Unpack(
                mod.exportPriceTeams, mod.exportPriceTypes, mod.exportPriceValues);

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
        /// The job registry is shared by all kingdoms. Additional player unpacks must not run
        /// InitJobList again through ResetPerLandMassData, or earlier kingdoms lose their jobs.
        /// PreserveLoadedJobsHook suppresses that reset only during RestoreAbsentPlayer.
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
                {
                    // A player who was not in this save is not an error: someone can join a
                    // friend's loaded game for the first time. They get the world, everybody
                    // else's kingdoms, and the name-and-banner screen to found their own.
                    //
                    // It throws only when we cannot even do that, because by then the world is
                    // half-restored and carrying on would leave them standing in it with no
                    // kingdom and no way to make one.
                    Main.helper.Log($"[LOAD] no saved kingdom for this Steam id ({localSteamId}); " +
                                    "the save holds " + string.Join(", ", players.Keys.ToArray()) +
                                    ". Joining as a NEW kingdom.");

                    object fresh = base.Unpack(obj);
                    RestoreOtherKingdoms(localSteamId);
                    RelinkKeeps();
                    RepairLoadedPlayability();
                    RestoreNormalGameplayOptions();
                    LogLoadedKingdoms();
                    KaCMultiplayer.Net.PlayerRelations.Restore(savedRelations);
                    KaCMultiplayer.Net.PlayerRelations.RestorePendingWars(savedPendingWars);

                    Main.TransitionTo(KaCMultiplayer.Lobby.MenuState.NameAndBanner);
                    return fresh;
                }

                this.PlayerSaveData = localData;

                // The game restores the world, the local kingdom and every subsystem.
                object result = base.Unpack(obj);

                RestoreOtherKingdoms(localSteamId);
                RelinkKeeps();
                RepairLoadedPlayability();
                RestoreNormalGameplayOptions();
                LogLoadedKingdoms();

                // Tell the lobby what difficulty this save was made with.
                //
                // The lobby is the authority while a session is being set up, and it broadcasts
                // its settings continuously. Loading a Hard save into a lobby still holding the
                // default left the two disagreeing, and the lobby won: the world quietly became
                // Peaceful. Difficulty decides a great deal, dragons do not spawn on Peaceful at
                // all, so this is not cosmetic.
                try
                {
                    int loaded = (int)Player.inst.difficulty;
                    if (KaCMultiplayer.Net.LobbySettings.Current.Difficulty != loaded)
                    {
                        Main.helper.Log("[LOAD] difficulty from the save: "
                            + KaCMultiplayer.Lobby.GameDifficultyExtensions.Label(loaded)
                            + " (lobby held "
                            + KaCMultiplayer.Lobby.GameDifficultyExtensions.Label(
                                  KaCMultiplayer.Net.LobbySettings.Current.Difficulty)
                            + ")");
                        KaCMultiplayer.Net.LobbySettings.Current.Difficulty = loaded;
                    }
                }
                catch (Exception dex) { Main.helper.Log("[LOAD] could not adopt the save's difficulty: " + dex.Message); }

                SessionPlayer localPlayer = Main.kCPlayers[localSteamId];
                localPlayer.banner = Player.inst.PlayerLandmassOwner.bannerIdx;
                localPlayer.kingdomName = TownNameUI.inst.townName;

                // Relations last, once every kingdom exists and owns its landmasses. Restoring
                // earlier would rebake the pathing gates and re-apply dock policy against teams
                // still being assembled, so the war would be reinstated against a half-built world.
                KaCMultiplayer.Net.PlayerRelations.Restore(savedRelations);
                KaCMultiplayer.Net.PlayerRelations.RestorePendingWars(savedPendingWars);

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
                int savedTeam = (kvp.Value != null && kvp.Value.playerLandmassOwnerSaveData != null)
                    ? kvp.Value.playerLandmassOwnerSaveData.teamId : -1;

                SessionPlayer player;
                if (Main.kCPlayers.TryGetValue(kvp.Key, out player))
                {
                    // Already registered, because the handshake ran before this save was read.
                    //
                    // Their SessionPlayer was built while LoadIdentity was still empty, so its team
                    // came from the fresh-game formula (clientId + 4) rather than from the save, and
                    // nothing revisited it afterwards. Leaving it is the orphan-phantom this file
                    // exists to prevent, seen from the other side: the kingdom is restored under its
                    // SAVED team while the player drives a Player object on a DIFFERENT one, so they
                    // own nothing, have no keep, and the game offers them a fresh castle while the
                    // town they built stands untouched beside them.
                    //
                    // With two players the formula lands on 6 and happens to agree with the save,
                    // which is exactly why this survived every two-player test.
                    AdoptSavedTeam(player, kvp.Key, savedTeam);
                    continue;
                }

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

        /// <summary>
        /// Moves a player who was already registered onto the team their saved kingdom carries.
        ///
        /// Only ever corrects a DISAGREEMENT, and only towards the save. That is LoadIdentity's own
        /// rule, the save wins over anything the handshake derived, but the registry is not filled
        /// until a load actually begins, so a player who connected before that was handed a formula
        /// team with nothing to check it against.
        ///
        /// Safe to run on the local player too: their team is asserted from the same registry a few
        /// lines further down, so both paths settle on the value the save carries.
        /// </summary>
        private static void AdoptSavedTeam(SessionPlayer player, string steamId, int savedTeam)
        {
            try
            {
                if (savedTeam <= 0 || player == null || player.inst == null) return;

                LandmassOwner owner = player.inst.PlayerLandmassOwner;
                if (owner == null || owner.teamId == savedTeam) return;

                Main.helper.Log($"[LOADID] {steamId} joined as team {owner.teamId}, but their saved "
                                + $"kingdom is team {savedTeam}; moving them onto it");
                owner.teamId = savedTeam;
            }
            catch (Exception ex) { Main.LogEx("adopting a saved team id", ex); }
        }

        /// <summary>
        /// These flags include normal simulation rules, not just cheats. Restore vanilla defaults
        /// for ordinary kingdoms, repairing saves that incorrectly recorded all flags as off.
        /// Creative kingdoms retain their chosen settings.
        /// </summary>
        private static void RestoreNormalGameplayOptions()
        {
            try
            {
                foreach (var kingdom in Main.kCPlayers.Values)
                {
                    Player p = kingdom != null ? kingdom.inst : null;
                    if (p == null || p.creativeMode) continue;
                    p.ResetCreativeModeOptions();
                }
                Main.helper.Log("[LOAD] restored normal gameplay options, including road coverage, for non-creative kingdoms");
            }
            catch (Exception ex) { Main.LogEx("restoring normal gameplay options after a load", ex); }
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
        /// Makes a kingdom that restored with a keep immediately playable again.
        ///
        /// The save can prove that buildings, banners and jobs came back while the live UI still
        /// behaves like a new kingdom: build buttons disabled, ownership shaders stale, or the
        /// player being asked for a fresh keep. Those gates all look at live Player fields, not the
        /// raw save data, so refresh the small runtime pieces after keep relinking has established
        /// which kingdom belongs to whom.
        /// </summary>
        private void RepairLoadedPlayability()
        {
            try
            {
                try { BuildMenuMaterials.Refresh(true); }
                catch (Exception ex) { Main.LogEx("refreshing loaded build-menu materials", ex); }
                foreach (var kp in Main.kCPlayers.Values)
                {
                    Player pl = kp != null ? kp.inst : null;
                    if (pl == null || pl.keep == null) continue;

                    EnsureToolRows(pl, kp.steamId);
                    DetachJobRows(pl, kp.steamId);

                    if (pl == Player.inst)
                    {
                        try { pl.RefreshVisibility(true); }
                        catch (Exception ex) { Main.helper.Log("[LOAD] local visibility refresh failed: " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { Main.LogEx("repairing loaded kingdoms", ex); }
        }

        private static void DetachJobRows(Player pl, string steamId)
        {
            try
            {
                int landmasses = World.inst != null ? World.inst.NumLandMasses : 0;
                if (landmasses <= 0 || pl.JobPriorityOrder == null || pl.JobEnabledFlag == null) return;

                bool detached = false;
                for (int lm = 0; lm < landmasses; lm++)
                {
                    if (lm >= pl.JobPriorityOrder.Length || lm >= pl.JobEnabledFlag.Length) continue;

                    foreach (var other in Main.kCPlayers.Values)
                    {
                        Player op = other != null ? other.inst : null;
                        if (op == null || op == pl) continue;

                        if (op.JobPriorityOrder != null && lm < op.JobPriorityOrder.Length
                            && ReferenceEquals(pl.JobPriorityOrder[lm], op.JobPriorityOrder[lm])
                            && pl.JobPriorityOrder[lm] != null)
                        {
                            pl.JobPriorityOrder[lm] = (int[])pl.JobPriorityOrder[lm].Clone();
                            detached = true;
                        }

                        if (op.JobEnabledFlag != null && lm < op.JobEnabledFlag.Length
                            && ReferenceEquals(pl.JobEnabledFlag[lm], op.JobEnabledFlag[lm])
                            && pl.JobEnabledFlag[lm] != null)
                        {
                            pl.JobEnabledFlag[lm] = (bool[])pl.JobEnabledFlag[lm].Clone();
                            detached = true;
                        }

                        if (pl.JobCustomMaxEnabledFlag != null && op.JobCustomMaxEnabledFlag != null
                            && lm < pl.JobCustomMaxEnabledFlag.Length && lm < op.JobCustomMaxEnabledFlag.Length
                            && ReferenceEquals(pl.JobCustomMaxEnabledFlag[lm], op.JobCustomMaxEnabledFlag[lm])
                            && pl.JobCustomMaxEnabledFlag[lm] != null)
                        {
                            pl.JobCustomMaxEnabledFlag[lm] = (bool[])pl.JobCustomMaxEnabledFlag[lm].Clone();
                            detached = true;
                        }
                    }
                }

                if (detached)
                    Main.helper.Log("[LOAD] detached shared job rows for " + steamId);
            }
            catch (Exception ex) { Main.LogEx("detaching shared job rows", ex); }
        }

        private static void EnsureToolRows(Player pl, string steamId)
        {
            int landmasses = World.inst != null ? World.inst.NumLandMasses : 0;
            if (landmasses <= 0) return;

            bool repaired = false;
            if (pl.CanUseTools == null || pl.CanUseTools.Length < landmasses)
            {
                bool[][] rows = new bool[landmasses][];
                int oldRows = pl.CanUseTools != null ? pl.CanUseTools.Length : 0;
                for (int i = 0; i < oldRows && i < rows.Length; i++)
                    rows[i] = pl.CanUseTools[i];
                pl.CanUseTools = rows;
                repaired = true;
            }

            // Only a row that is MISSING is created, and it is created exactly the way
            // Player.ResetPerLandMassData creates one: five slots, one per ToolUser, all on. That
            // is the game's default for an island nobody has touched.
            //
            // A row that exists is never rewritten, even if every slot in it is off. An earlier
            // version of this repair treated "all off" as broken and switched them all back on,
            // but all-off is a real choice: it is what a player picks to stop quarries, mines,
            // foresters and fishmongers eating iron tools, and silently undoing it on every load
            // would spend a kingdom's tools behind its owner's back.
            for (int i = 0; i < landmasses; i++)
            {
                if (pl.CanUseTools[i] != null && pl.CanUseTools[i].Length > 0) continue;

                bool[] row = new bool[5];
                for (int j = 0; j < row.Length; j++) row[j] = true;
                pl.CanUseTools[i] = row;
                repaired = true;
            }

            if (repaired)
                Main.helper.Log("[LOAD] repaired tool unlock rows for " + steamId + " (" + landmasses + " landmass(es))");
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
                int landmasses = World.inst != null ? World.inst.NumLandMasses : -1;
                Main.helper.Log($"[LOADCHK] world has {landmasses} landmass(es)");

                foreach (var kp in Main.kCPlayers.Values)
                {
                    Player pl = kp.inst;
                    int bc = (pl != null && pl.Buildings != null) ? pl.Buildings.Count : -1;
                    Main.helper.Log($"Loaded player '{kp.name}' steamId={kp.steamId} teamId={pl?.PlayerLandmassOwner?.teamId} buildings={bc} keepLinked={(pl != null && pl.keep != null)} isLocal={(pl == Player.inst)}");

                    // The state that decides whether a loaded kingdom LOOKS right, none of which is
                    // visible in the line above, and all of which has been reported wrong after a
                    // load.
                    //
                    // banner = -1 means vanilla's Unpack skipped SetIndexedBanner altogether (it
                    // tests for exactly -1), so the kingdom has no livery: grey buildings, and
                    // EnsureUnitsAreVisible then refuses to run for ANY kingdom while one is still
                    // unbannered, which is villagers that simulate without ever drawing.
                    // armyMaterial is that same story from the other end. jobRows short of the
                    // landmass count is the job panel with one row in it. flagSubscribers = 0 means
                    // RefreshAllBanners has nothing to repaint for this kingdom, which is why its
                    // flags keep somebody else's colours.
                    if (pl == null) continue;

                    LandmassOwner lo = pl.PlayerLandmassOwner;
                    int jobRows = pl.JobPriorityOrder != null ? pl.JobPriorityOrder.Length : -1;
                    int flagSubs = pl.updateBanner != null ? pl.updateBanner.GetInvocationList().Length : 0;

                    Main.helper.Log($"[LOADCHK]   banner={(lo != null ? lo.bannerIdx : -1)} "
                                    + $"armyMaterial={(lo != null && lo.ArmyMaterial != null)} "
                                    + $"jobRows={jobRows} (world wants {landmasses}) "
                                    + $"flagSubscribers={flagSubs}");
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
            bool wasRestoring = RestoringAdditionalKingdom;
            int jobsBefore = CountRegisteredJobs();
            try
            {
                RestoringAdditionalKingdom = true;
                Player.inst = player.inst;
                saved.Unpack(player.inst);
            }
            catch (Exception ex)
            {
                // NOT rethrown. One kingdom that fails to restore must not abandon the whole load.
                //
                // This is the policy the rest of this file already states, in PrepareKingdoms:
                // "a kingdom that is missing or fails to reset must not abort the LOAD ... which is
                // far worse than one kingdom starting dirty". This path was the exception to it,
                // and the cost was real. A single throw from deep inside vanilla (a keep placement
                // hitting a null prefab, see SessionPlayer.CopySharedSceneRefs) propagated out
                // through RestoreOtherKingdoms and SessionSave.Unpack, so everything after it was
                // abandoned: a guest joining a SAVED game landed in a half-built world with a
                // half-built UI, and the only trace was one line in Player.log.
                //
                // The kingdom that failed is left as far as it got, which is the same state a
                // missing kingdom would be in, and the log says which one and why.
                Main.LogEx($"unpacking absent kingdom {steamId}", ex);
                Main.helper.Log($"[LOAD] kingdom {steamId} did not restore cleanly; carrying on so the "
                                + "rest of the world still loads");
            }
            finally
            {
                Player.inst = previous;
                RestoringAdditionalKingdom = wasRestoring;
            }

            Main.helper.Log($"[LOADJOBS] kingdom {steamId}: shared jobs before={jobsBefore}, after={CountRegisteredJobs()}");

            player.banner = player.inst.PlayerLandmassOwner.bannerIdx;

            // Their own saved name. Reading the local town name here was the bug that made every
            // absent player appear under your kingdom's name.
            if (kingdomNames.ContainsKey(steamId))
                player.kingdomName = kingdomNames[steamId];
        }

        private static int CountRegisteredJobs()
        {
            if (JobSystem.inst == null || JobSystem.inst.jobs == null) return 0;
            int count = 0;
            var jobs = JobSystem.inst.jobs;
            for (int lm = 0; lm < jobs.Count; lm++)
                for (int category = 0; category < jobs.data[lm].Count; category++)
                    count += jobs.data[lm].data[category].Count;
            return count;
        }

    }
}
