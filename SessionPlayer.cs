// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System.Reflection;
using UnityEngine;

namespace KaCMultiplayer
{
    /// <summary>
    /// One participant in a multiplayer session, and the <see cref="Player"/> object that
    /// carries their kingdom.
    ///
    /// The local player reuses the game's own <c>Player.inst</c>. Every remote player gets a
    /// second, parallel <see cref="Player"/> on its own GameObject, the game was built around
    /// a single Player singleton, so remote kingdoms only work because Main's transpiler
    /// rewrites <c>Player.inst</c> to the receiver inside Player's instance methods.
    /// </summary>
    public class SessionPlayer
    {
        // Number of job/tool slots the game allocates per landmass, taken from the game rather
        // than guessed: Player.Awake does `defaultEnabledFlags = new bool[39]`, and
        // JobCategory.NumCategories is 39 on the current build. It was hardcoded at 38, which
        // left the LAST job category disabled on every remote player, silently, since a missing
        // job just means nobody ever takes it. The save path already reads the real length off
        // the live array (live.GetLength(0) in PlayerSaveData.Pack); this is the same fix.
        private const int JobSlotCount = 39;

        public ushort id;
        public string name;

        public string steamId;

        public string kingdomName;
        public int banner = 0;
        public bool ready = false;

        public Player inst;
        public GameObject gameObject;

        // A placeholder for a SAVED player who hasn't (re)connected to this loaded session. Their kingdom
        // is fully unpacked (buildings/ownership under their saved teamId) but nobody is driving it, and
        // they must not block the lobby's ready/Start gating. Cleared if the real player reconnects.
        public bool isGhost = false;

        // An AI kingdom's own parallel Player, built by BuildForAI rather than BuildRemotePlayer.
        // See BuildForAI's own doc comment for why one exists at all.
        public bool isAI = false;

        /// <summary>
        /// This player's Steam name, or their session name when Steam cannot tell us.
        ///
        /// Asked of Steam rather than trusted from the session, because the name that travelled in
        /// the handshake is whatever the sender happened to be called at the time, and it is blank
        /// for a kingdom restored from a save whose owner has not reconnected yet.
        ///
        /// Lives here rather than in whichever window needed it first, because three of them do now
        /// and a name that differs between the diplomacy list and the popup asking about the same
        /// player reads as two different people.
        /// </summary>
        public string SteamPersona()
        {
            try
            {
                ulong id;
                if (ulong.TryParse(steamId, out id))
                {
                    string persona = Steamworks.SteamFriends.GetFriendPersonaName(new Steamworks.CSteamID(id));
                    if (!string.IsNullOrWhiteSpace(persona) && persona != "[unknown]") return persona;
                }
            }
            catch (System.Exception) { }

            return string.IsNullOrWhiteSpace(name) ? "" : name;
        }

        public SessionPlayer(string name, ushort id, string steamId)
        {
            this.name = name;
            this.id = id;
            this.steamId = steamId;
            this.kingdomName = " ";

            if (id == NetClient.client.Id)
            {
                // Us: adopt the singleton the game already built rather than making a rival.
                gameObject = Player.inst.gameObject;
                inst = Player.inst;
            }
            else
            {
                gameObject = BuildRemotePlayer(name, id, steamId, out inst);
            }
        }

        public SessionPlayer(ushort id, Player player)
        {
            this.id = id;
            inst = player;
            gameObject = player.gameObject;
        }

        /// <summary>
        /// Builds the parallel <see cref="Player"/> for an AI kingdom, host-only.
        ///
        /// Reuses the exact same wiring a remote human's Player gets, CopySharedSceneRefs,
        /// EnableAllJobSlots, ResetAsIfSingleton, see those methods' own doc comments for what
        /// each one fixes, but points PlayerLandmassOwner at the <paramref name="existingOwner"/>
        /// AIKingdom.SetupBase already created and registered via TakeOwnership, rather than
        /// building a second, orphaned LandmassOwner for the same landmass/team the way
        /// BuildRemotePlayer would. World.GetLandmassOwner(landmass) must keep resolving to the
        /// ONE real LandmassOwner the AIKingdom component itself already owns; a duplicate here
        /// would silently diverge from it (separate Gold, separate standings) with nothing else
        /// in the game ever looking at it.
        ///
        /// Exists because AIKingdom.Update() and its Intention_* tick chain read and write
        /// through the ambient Player.inst singleton, AddVillager, SetJobPriorityOrder,
        /// GetBuildingListForLandMass, TotalResidentialSlotsOnLandMass, confirmed via the shipped
        /// IL, none of them take the AI's own identity as a parameter, they act on whichever
        /// Player instance happens to be Player.inst when called. Run that tick with Player.inst
        /// pointed at anything else and an AI kingdom's villagers and job-priority writes land on
        /// THAT kingdom instead, on the host, its own real kingdom, silently. This Player
        /// exists so the host has something correct to swap Player.inst to for the duration of
        /// that one tick (see AIKingdomOwnerHook), not as an optional nicety.
        /// </summary>
        public static SessionPlayer BuildForAI(ushort id, string name, LandmassOwner existingOwner)
        {
            GameObject host = new GameObject($"AI Kingdom ({name})");

            Player player = host.AddComponent<Player>();
            player.irrigation = host.AddComponent<IrrigationManager>();
            player.PlayerLandmassOwner = existingOwner;

            player.hazardPayWarmup = new Timer(5f);
            player.hazardPayWarmup.Enabled = false;

            CopySharedSceneRefs(player);
            EnableAllJobSlots(player);
            ResetAsIfSingleton(player);

            var sp = new SessionPlayer(id, player);
            sp.name = name;
            sp.steamId = "AI-" + existingOwner.teamId;
            sp.kingdomName = name;
            sp.banner = existingOwner.bannerIdx;
            sp.isAI = true;
            return sp;
        }

        /// <summary>
        /// Creates the parallel <see cref="Player"/> that represents a remote participant,
        /// wired up with the components its kingdom logic expects to find.
        /// </summary>
        private static GameObject BuildRemotePlayer(string name, ushort id, string steamId, out Player player)
        {
            GameObject host = new GameObject($"Client Player ({id} {name})");

            player = host.AddComponent<Player>();
            player.irrigation = host.AddComponent<IrrigationManager>();
            player.PlayerLandmassOwner = host.AddComponent<LandmassOwner>();

            // Fresh game: teamId is clientId + 4, computed from the Riptide client id alone so
            // every machine arrives at the same answer without anyone having to publish the
            // assignment. The +4 clears the game's own range, the per-team pathing arrays are
            // size 5, so it uses 0..4, and keeping the ids compact matters because those arrays
            // are indexed by team. This formula must agree with the one in ServerHandshake.
            //
            // Loaded game: the saved teamId wins, because the kingdom in the save carries it and
            // join order need not repeat. LoadIdentity returns the saved id when this steamId
            // appears in the save, and falls back to the formula otherwise.
            player.PlayerLandmassOwner.teamId = LoadSaveOverrides.LoadIdentity.TeamIdFor(steamId, id);

            // Hazard pay is a local-player UI affordance; a remote kingdom needs the timer to
            // exist so the game's code can read it, but never to run.
            player.hazardPayWarmup = new Timer(5f);
            player.hazardPayWarmup.Enabled = false;

            CopySharedSceneRefs(player);
            EnableAllJobSlots(player);
            ResetAsIfSingleton(player);

            return host;
        }

        /// <summary>
        /// Copies the scene and prefab references a Player carries, from the real local one.
        ///
        /// A remote kingdom's Player is built here, in code, so every field Unity would normally
        /// have wired from the scene is null on it. Most of those never matter, because a remote
        /// kingdom is simulated rather than driven. These matter, because VANILLA reads them
        /// off whichever Player it happens to be looking at, and this mod regularly arranges for
        /// that to be a remote one.
        ///
        ///   healthBarPrefab  Keep.OnBuildingPlacement does
        ///                    Instantiate(Player.inst.healthBarPrefab), and RestoreAbsentPlayer
        ///                    points Player.inst at the ghost while restoring its buildings. A null
        ///                    prefab there throws "The Object you want to instantiate is null" from
        ///                    the middle of the unpack. Caught and logged in Player.log only, never
        ///                    in output.txt, which is why it went unseen for so long.
        ///
        ///   unitIGUIprefab   the same shape of Instantiate, for unit UI.
        ///
        ///   *Integrity       Building.UpdateIntegrityOverlayMaterial chooses one of these four
        ///                    materials, and the Building-owner transpiler deliberately redirects
        ///                    that read from Player.inst to the building's OWNER. For another
        ///                    player's building the owner is one of these objects, so a null here
        ///                    is a building drawn with no material.
        ///
        ///   walkBounce,      Villager.UpdateAnim evaluates Player.inst.walkBounce for every
        ///   showBounce       villager every frame. A null curve throws out of Player.Update each
        ///                    frame and stops the whole kingdom, which is what a raid-year rotation
        ///                    left stuck on a guest's stand-in did to the host.
        ///
        /// buildingContainer is deliberately NOT copied. Reset gives each kingdom its own, and
        /// sharing one would parent every kingdom's buildings under the local player's.
        ///
        /// Null sources are skipped rather than copied, so being called at a moment when the
        /// singleton is itself a stand-in cannot overwrite good references with nothing.
        /// </summary>
        private static void CopySharedSceneRefs(Player target)
        {
            try
            {
                Player source = Player.inst;
                if (source == null || source == target) return;

                if (source.healthBarPrefab != null) target.healthBarPrefab = source.healthBarPrefab;
                if (source.unitIGUIprefab != null) target.unitIGUIprefab = source.unitIGUIprefab;
                if (source.HighIntegrity != null) target.HighIntegrity = source.HighIntegrity;
                if (source.MediumIntegrity != null) target.MediumIntegrity = source.MediumIntegrity;
                if (source.LowIntegrity != null) target.LowIntegrity = source.LowIntegrity;
                if (source.VeryLowIntegrity != null) target.VeryLowIntegrity = source.VeryLowIntegrity;
                if (source.walkBounce != null) target.walkBounce = source.walkBounce;
                if (source.showBounce != null) target.showBounce = source.showBounce;
            }
            catch (System.Exception ex) { Main.LogEx("copying shared scene references to a remote kingdom", ex); }
        }

        /// <summary>
        /// Turns on every job slot. <c>defaultEnabledFlags</c> is private and left null on a
        /// Player the game did not construct itself, so it has to be seeded by reflection or
        /// the first job lookup dereferences null.
        /// </summary>
        private static void EnableAllJobSlots(Player player)
        {
            bool[] enabled = new bool[JobSlotCount];
            for (int i = 0; i < enabled.Length; i++)
                enabled[i] = true;

            FieldInfo flags = typeof(Player).GetField(
                "defaultEnabledFlags", BindingFlags.NonPublic | BindingFlags.Instance);
            flags.SetValue(player, enabled);
        }

        /// <summary>
        /// Runs <see cref="Player.Reset"/> on a remote player to initialise its own collections,
        /// buildingContainer, and the per-landmass job/resource arrays that
        /// <c>Reset -> ResetPerLandMassData -> SetupJobPriorities</c> allocates (including
        /// <c>JobFilledAvailable</c>, which is otherwise null on a Player the game never Awoke).
        ///
        /// It must NOT point <c>Player.inst</c> at the fresh remote player first, even though the
        /// name says "as if singleton". Reset's own body carries no <c>Player.inst</c> reference,
        /// the singleton transpiler rewrites none inside it (the startup log shows no "in Reset"
        /// line), so it works on its receiver regardless of the singleton. But Reset also makes
        /// GLOBAL calls, and one of them reads the literal <c>Player.inst</c> deep inside:
        /// <c>JobSystem.ClearAllJobs -> Villager.QuitJob -> Job.OnEmployeeQuit</c> does
        /// <c>Player.inst.JobFilledAvailable.data[...]</c>. A just-built remote player's
        /// JobFilledAvailable is still null at that point (Reset allocates it only later), so
        /// aiming the singleton at it made ClearAllJobs throw NullReferenceException the moment the
        /// world already held an employed villager. That is what aborted a load/rejoin into a
        /// populated game mid-unpack and left the client with no kingdom (0 resources, no keep,
        /// villagers starving); a fresh lobby join slipped through only because it had no jobs to
        /// clear. Leaving Player.inst on the real local player lets ClearAllJobs, and every other
        /// global call Reset makes, read a valid, fully-initialised player.
        ///
        /// The finally still restores Player.inst defensively, in case any patched call touches it.
        /// </summary>
        private static void ResetAsIfSingleton(Player player)
        {
            Player previous = Player.inst;

            // Player.Reset() ends with `Object.Destroy(World.inst.caveContainer)`. That container
            // is GLOBAL world state, not this player's, and nothing recreates it: only World.Setup
            // builds it, and that runs once at world generation. So every remote player that joins
            // destroyed the world's cave container for everybody, permanently.
            //
            // The damage was not caves. It was the SEASON, and through it the harvest:
            //
            //   World.WorldSaveData.Pack reads caveContainer.transform.childCount unguarded, so
            //   after this every autosave threw NullReferenceException. Autosave is driven by
            //   AutoSave.OnOnSeasonChange, a subscriber to Weather.OnSeasonChange, and a .NET
            //   multicast delegate stops dispatching at the first subscriber that throws. Every
            //   farm's YieldProducerSeason.Inst_OnSeasonChange subscribes later than AutoSave
            //   does, so no farm ever received the season change, no yield was ever emitted, and
            //   actualYieldPercentage was never reset. Crops grew taller every year and were
            //   never harvested, on a farm that was built, Open and fully staffed.
            //
            // Hiding the container for the duration of Reset is enough: Object.Destroy(null) is a
            // no-op, so the real container and its children survive. This also stops a mid-game
            // join from deleting the caves, wolf dens and witch huts that are already standing.
            //
            // NOTE: Reset makes several more global calls in the same breath (RaiderSystem.Reset,
            // UnitSystem.Reset, World.DestroyStoneUIs, MiniMapViewer.Reset, and it clears
            // LoadSave's custom save data). Running it once per remote player resets all of those
            // too. Only the cave container is fixed here, because only it had a proven victim.
            try
            {
                // The cave-container protection lives in Main.ResetKingdomSafely now, so the map
                // reroll and this path cannot drift apart on it.
                Main.ResetKingdomSafely(player);
            }
            finally
            {
                Player.inst = previous;
            }
        }
    }
}
