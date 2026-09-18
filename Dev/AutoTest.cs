using System;
using System.Collections.Generic;
using UnityEngine;

using KaCMultiplayer.Combat;
using KaCMultiplayer.Lobby;
using KaCMultiplayer.Net;
using KaCMultiplayer.Net.Messages;
using KaCMultiplayer.Trade;
using Assets.Code;

namespace KaCMultiplayer.Dev
{
    /// <summary>
    /// An automated acceptance run: hosts a session, exercises every cross-player feature, and
    /// writes a PASS/FAIL line per check to output.txt.
    ///
    /// Why this exists. The headless tests in kcm-tests cover pure logic, and FakePeer makes the
    /// two-player paths reachable, but reaching them still meant a human clicking through a lobby
    /// and watching. That made every UI and combat change ship on reasoning alone. This drives the
    /// same clicks in code, so a full run is: build, launch, read the log.
    ///
    /// It invokes the REAL controls rather than reimplementing them. Starting a session calls
    /// LobbyScreen.StartButton.onClick, which is the same handler a player triggers, so the test
    /// exercises the shipped path including its guards. A copy of that logic would drift and start
    /// passing while the button broke.
    ///
    /// Double-gated exactly like FakePeer: it needs <see cref="Main.DevTestBuild"/> AND its own
    /// <see cref="Enabled"/> flag, because unlike the other aids this one CREATES A LOBBY and
    /// STARTS A GAME by itself. That must never happen to somebody who just wanted to play.
    /// </summary>
    public static class AutoTest
    {
        /// <summary>DEV ONLY, and off even in dev builds unless deliberately switched on.</summary>
        public static bool Enabled = false;

        private enum Phase { Idle, CreatingLobby, WaitingForLobby, Starting, WaitingForPlay, Settling, Checks, Battle, Done }

        private static Phase phase = Phase.Idle;
        private static float phaseStarted;
        private static int settleFrames;

        private static int passed;
        private static readonly List<string> failures = new List<string>();

        // Battle state. The static checks all finish inside one frame, but a FIGHT cannot: damage is
        // dealt by the army tick, so the only honest way to assert that combat works is to start one
        // and let the game run. These carry the fight across the frames it needs.
        private static int battleFrames;
        private static UnitSystem.Army battleEnemy;
        private static UnitSystem.Army battleOurs;
        private static float enemyLifeAtStart;
        private static float ourLifeAtStart;
        private static int publishedAtStart;
        private static float gameClockAtStart;

        /// <summary>
        /// How long any one phase may take before the run gives up and says where it stopped.
        /// Without this a run that never reaches play mode just sits there, and the log looks
        /// identical to a run that was never started.
        /// </summary>
        private const float PhaseTimeoutSeconds = 45f;

        /// <summary>Frames to let the world settle after the session starts, before checking it.</summary>
        private const int SettleFrames = 120;

        /// <summary>
        /// Frames to let the battle run. Generous on purpose: the fight is at max game speed but
        /// units still have to close, swing and die, and a test that ends the fight early would
        /// report "no damage" for a battle that was merely still forming up.
        /// </summary>
        private const int BattleFrames = 900;

        private static void Log(string line) { Main.helper.Log("[SELFTEST] " + line); }

        private static void Check(string name, bool ok)
        {
            if (ok) { passed++; Log("PASS  " + name); }
            else { failures.Add(name); Log("FAIL  " + name); }
        }

        /// <summary>
        /// Runs just the checks against a session that is already up, skipping the hosting phases.
        ///
        /// The other way in, for when a session already exists: hosting from the menu is only worth
        /// automating when nobody is there to do it. Needs DevTestBuild, but NOT AutoTest.Enabled,
        /// since running checks on a session that already exists cannot surprise anyone the way
        /// creating a lobby can.
        /// </summary>
        public static void RunChecksNow()
        {
            if (!Main.DevTestBuild) return;

            if (GameState.inst == null || !GameState.inst.IsPlayMode() || !NetClient.client.IsConnected)
            {
                Log("not in a running session, nothing to check");
                return;
            }

            passed = 0;
            failures.Clear();

            try
            {
                RunChecks();

                if (StartBattle())
                {
                    // Hand the rest to Tick, which is the only thing that can spend frames. Enabling
                    // the flag here cannot start a lobby: Begin() only fires from Phase.Idle and from
                    // the main menu, and we are neither.
                    phase = Phase.Battle;
                    phaseStarted = Time.realtimeSinceStartup;
                    Enabled = true;
                    return;
                }

                Summarise();
            }
            catch (Exception ex)
            {
                Log("ABORTED by an exception: " + ex.Message);
                Main.LogEx("[SELFTEST] manual run", ex);
            }

            phase = Phase.Done;   // do not also auto-run afterwards
        }

        /// <summary>Called every frame from Main.Update. Does nothing at all unless switched on.</summary>
        public static void Tick()
        {
            if (!Main.DevTestBuild || !Enabled) return;
            if (phase == Phase.Done) return;

            try
            {
                if (phase == Phase.Idle) Begin();
                else if (TimedOut()) return;
                else Advance();
            }
            catch (Exception ex)
            {
                Log("ABORTED by an exception in phase " + phase + ": " + ex.Message);
                Main.LogEx("[SELFTEST] phase " + phase, ex);
                phase = Phase.Done;
            }
        }

        private static void Enter(Phase next)
        {
            phase = next;
            phaseStarted = Time.realtimeSinceStartup;
        }

        private static bool TimedOut()
        {
            if (Time.realtimeSinceStartup - phaseStarted < PhaseTimeoutSeconds) return false;

            Log("TIMED OUT in phase " + phase + " after " + PhaseTimeoutSeconds + "s");
            Summarise();
            phase = Phase.Done;
            return true;
        }

        private static void Begin()
        {
            // Only from the main menu: starting a lobby on top of a running session would be
            // meaningless and destructive.
            if (GameState.inst == null || Main.menuState != MenuState.Menu) return;

            Log("starting an automated session run");
            Enter(Phase.CreatingLobby);
        }

        private static void Advance()
        {
            switch (phase)
            {
                case Phase.CreatingLobby:
                    SteamLobby.Active.CreateLobby();
                    Enter(Phase.WaitingForLobby);
                    break;

                case Phase.WaitingForLobby:
                    // The handshake fills the registry a moment after the lobby appears, and the
                    // Start handler refuses to run before it has, so wait for the same condition
                    // the button itself checks.
                    if (Main.kCPlayers.Count > 0 && LobbyScreen.StartButton != null)
                        Enter(Phase.Starting);
                    break;

                case Phase.Starting:
                    Log("invoking the real Start button");
                    LobbyScreen.StartButton.onClick.Invoke();
                    Enter(Phase.WaitingForPlay);
                    break;

                case Phase.WaitingForPlay:
                    if (GameState.inst.IsPlayMode())
                    {
                        settleFrames = SettleFrames;
                        Enter(Phase.Settling);
                    }
                    break;

                case Phase.Settling:
                    if (--settleFrames <= 0) Enter(Phase.Checks);
                    break;

                case Phase.Checks:
                    RunChecks();
                    if (StartBattle()) Enter(Phase.Battle);
                    else { Summarise(); phase = Phase.Done; }
                    break;

                case Phase.Battle:
                    if (--battleFrames <= 0)
                    {
                        FinishBattle();
                        Summarise();
                        phase = Phase.Done;
                    }
                    break;
            }
        }

        private static void Summarise()
        {
            Log("=== " + passed + " passed, " + failures.Count + " failed ===");
            for (int i = 0; i < failures.Count; i++) Log("    failed: " + failures[i]);
        }

        // ---- the checks ---------------------------------------------------------------------

        private static void RunChecks()
        {
            Log("session is up, running checks");

            CheckSessionBasics();
            CheckFakePeer();

            // The class checks, before anything below disturbs the world. They read the session as
            // it stands rather than building fixtures, so they are honest only while it is still
            // the session the earlier phases produced. See Dev/Invariants.cs for what a "class
            // check" is and why it is kept separate from the feature checks around it.
            Invariants.RunAll(Check, Log);

            CheckTradeFixture();
            CheckDiplomacy();
            CheckChat();
            CheckRelationsPersistence();
            CheckCombat();
            CheckVillagerDeaths();
            CheckShipArbitration();
            CheckSiegeCatapults();
            CheckWolves();
            CheckArmyPositions();
            CheckBannerRepaint();
            CheckSinglePlayerIsUntouched();
            PrepareIdSurvivalFixture();
            CheckSaveRoundTrip();
            CheckSaveLoadsBack();
            CheckMaterialsAreComplete();
            CheckIdsSurvivedTheLoad();
            PrepareHostBuildFixture();
            PrepareLobbyDriftTrap();
        }

        // The difficulty the world is actually running at, recorded so the soak check can ask
        // whether anything overwrote it. Minus one means the trap could not be set.
        private static int difficultyUnderTest = -1;

        /// <summary>
        /// Sets a trap for lobby state clobbering the running game, to be read after the battle.
        ///
        /// A CLASS, not a bug. The lobby is the authority while a session is being set up and it
        /// broadcasts its settings continuously, roughly twice a second, for the whole session. Any
        /// world state that both the lobby and the game own is therefore in a fight that the lobby
        /// wins, every half second, forever. Difficulty is the instance that was found: a Hard save
        /// loaded into a lobby still holding the default became Peaceful within half a second and
        /// would not stay changed, and difficulty decides a great deal, dragons do not spawn on
        /// Peaceful at all.
        ///
        /// So the trap is deliberately the wrong way round. It makes the LOBBY disagree with the
        /// GAME and then leaves them to fight for the length of the battle. If the game's value
        /// survives, play mode is properly insulated from the lobby; if it has become the lobby's
        /// value, whatever else was broadcast that half second is suspect too.
        ///
        /// Read in FinishBattle rather than here, because half a second is the whole point: a
        /// same-frame check would pass on the broken build. This is the same prepare-then-verify
        /// shape as the host build fixture, for the same reason.
        /// </summary>
        private static void PrepareLobbyDriftTrap()
        {
            difficultyUnderTest = -1;

            try
            {
                if (Player.inst == null) { Log("no local kingdom, cannot set the lobby drift trap"); return; }

                difficultyUnderTest = (int)Player.inst.difficulty;

                // Any value the game is NOT running at. Nudging by one and wrapping off the enum's
                // own length keeps this free of a hardcoded count, which the GameDifficulty file
                // warns about drifting for exactly this kind of reason.
                int choices = Enum.GetValues(typeof(GameDifficulty)).Length;
                int disagree = (difficultyUnderTest + 1) % choices;
                LobbySettings.Current.Difficulty = disagree;

                Log("lobby drift trap set: the game is on difficulty " + difficultyUnderTest
                    + " and the lobby now claims " + disagree
                    + "; the game's value must still be " + difficultyUnderTest + " after the battle");
            }
            catch (Exception ex)
            {
                difficultyUnderTest = -1;
                Main.LogEx("[SELFTEST] setting the lobby drift trap", ex);
            }
        }

        /// <summary>Reads the trap set by <see cref="PrepareLobbyDriftTrap"/>. See its note.</summary>
        private static void CheckLobbyDidNotClobberPlayMode()
        {
            if (difficultyUnderTest < 0) { Log("no lobby drift trap was set, nothing to re-check"); return; }

            try
            {
                int now = (int)Player.inst.difficulty;

                if (now != difficultyUnderTest)
                    Log("the lobby overwrote the running game's difficulty: " + difficultyUnderTest
                        + " became " + now + " while the session was playing");

                Check("the running game's difficulty survives the lobby's broadcasts",
                      now == difficultyUnderTest);
            }
            catch (Exception ex)
            {
                Check("the lobby drift check finished without throwing", false);
                Main.LogEx("[SELFTEST] lobby drift", ex);
            }
        }

        /// <summary>
        /// Loads the save that was just written, back into this session, and checks both kingdoms
        /// survived the trip.
        ///
        /// This is the check that matters most, and it did not exist until 2026-09-06. Everything
        /// else here exercises the SAVE half; the LOAD half was refactored on the game author's
        /// advice (SessionSave.Unpack now delegates to base.Unpack instead of reimplementing it)
        /// with no automated coverage at all, which is precisely how you break somebody's saves.
        ///
        /// It runs LAST and is deliberately destructive: unpacking replaces the running world, so
        /// nothing after it would be looking at the session the earlier checks measured.
        ///
        /// What it is really asking is whether the ordering risk in the refactor is real. Remote
        /// kingdoms are now restored after the subsystem unpacks rather than before, so if a
        /// kingdom comes back without its buildings or without its keep, this says so here rather
        /// than in a player's bug report.
        /// </summary>
        private static void CheckSaveLoadsBack()
        {
            int kingdomsBefore = Main.kCPlayers.Count;
            int teamBefore = LocalTeam();   // captured now; comparing after the load to a value read
                                            // after the load would compare it against itself

            string dir;
            try { dir = LoadSave.GetSaveDir() + "/" + Main.ResumeSaveFolder; }
            catch (Exception ex)
            {
                Main.LogEx("[SELFTEST] load back path", ex);
                Check("the saved session can be located", false);
                return;
            }

            try
            {
                LoadSave.LoadAtPath(dir, "world");
            }
            catch (Exception ex)
            {
                Main.LogEx("[SELFTEST] loading the save back", ex);
                Check("the saved session loads back without throwing", false);
                return;
            }

            Check("the saved session loads back without throwing", true);

            Check("every kingdom came back", Main.kCPlayers.Count == kingdomsBefore);

            // The specific failure the refactor could cause: a kingdom restored after the subsystem
            // unpacks, arriving empty or unlinked from its keep.
            int whole = 0;
            int total = 0;
            foreach (SessionPlayer kp in Main.kCPlayers.Values)
            {
                total++;
                Player pl = kp == null ? null : kp.inst;
                if (pl == null) continue;

                int buildings = pl.Buildings != null ? pl.Buildings.Count : 0;
                bool keep = pl.keep != null;

                Log("  after load: '" + kp.name + "' buildings=" + buildings + " keepLinked=" + keep
                    + " team=" + (pl.PlayerLandmassOwner != null ? pl.PlayerLandmassOwner.teamId : -1));

                if (buildings > 0 && keep) whole++;
            }

            Check("every kingdom kept its buildings and its keep", total > 0 && whole == total);

            // The local player must still be the LOCAL player. Handing base.Unpack the wrong
            // PlayerSaveData would quietly leave us playing somebody else's kingdom, which is the
            // failure the PlayerSaveData swap in Unpack exists to prevent.
            Check("loading a save did not bring witch huts back",
                  UnityEngine.Object.FindObjectsOfType<WitchHut>().Length == 0);

            Check("we still own our own kingdom after loading",
                  Player.inst != null && Player.inst.PlayerLandmassOwner != null
                  && Player.inst.PlayerLandmassOwner.teamId == teamBefore);
        }

        /// <summary>
        /// Every kingdom can actually be DRAWN.
        ///
        /// Written after a night of shipping four fixes for what turned out to be one missing null
        /// check, each of which was found by a player looking at the screen and saying the farms
        /// had gone hot pink. Nothing in this run could see it: the world loaded, the counts were
        /// right, the kingdoms were whole, and every one of those assertions passed while the game
        /// drew a magenta city. Magenta is what Unity paints when a material is null, and nothing
        /// here was checking for a null material.
        ///
        /// The specific chain it exists to catch: LandmassOwner.SetBannerIdx ends by calling
        /// UnitSystem.UpdateMaterialFor, which walks every army dereferencing generalComponent with
        /// no null check. One army without a general mid-load throws, and everything after that
        /// call is skipped -- including the loop that builds UniMaterialsCracked, the array every
        /// building picks its material from. So UniMaterialsCracked is asserted element by element
        /// rather than merely for being non-null: a half-built array is exactly what that bug
        /// leaves behind.
        /// </summary>
        private static void CheckMaterialsAreComplete()
        {
            try
            {
                int kingdoms = 0, litKingdoms = 0, crackedOk = 0;

                foreach (SessionPlayer kp in Main.kCPlayers.Values)
                {
                    if (kp == null || kp.inst == null) continue;

                    LandmassOwner owner = kp.inst.PlayerLandmassOwner;
                    if (owner == null) continue;

                    kingdoms++;

                    bool lit = owner.UniMaterial != null
                            && owner.BuildingMaterial != null
                            && owner.FlagMaterial != null
                            && owner.UniMaterialFogClip != null;

                    if (lit) litKingdoms++;
                    else
                        Log("  team " + owner.teamId + " is missing a livery material: "
                            + "uni=" + (owner.UniMaterial != null)
                            + " building=" + (owner.BuildingMaterial != null)
                            + " flag=" + (owner.FlagMaterial != null)
                            + " fogClip=" + (owner.UniMaterialFogClip != null));

                    // The array buildings pick from. Element by element, because the bug this
                    // catches leaves it allocated and empty rather than null.
                    bool cracked = owner.UniMaterialsCracked != null && owner.UniMaterialsCracked.Length > 0;
                    if (cracked)
                    {
                        for (int i = 0; i < owner.UniMaterialsCracked.Length; i++)
                            if (owner.UniMaterialsCracked[i] == null) { cracked = false; break; }
                    }

                    if (cracked) crackedOk++;
                    else
                        Log("  team " + owner.teamId + " has no usable damaged-building materials "
                            + "(UniMaterialsCracked), which is what draws a city magenta");
                }

                Check("every kingdom has its livery materials", kingdoms > 0 && litKingdoms == kingdoms);
                Check("every kingdom has a complete UniMaterialsCracked", kingdoms > 0 && crackedOk == kingdoms);

                // Ships take their colour once, in Init, and nothing ever asks again, so one built
                // before its owner had a banner keeps a null material for the rest of the session.
                int ships = 0, paintedShips = 0;
                if (ShipSystem.inst != null && ShipSystem.inst.ships != null)
                {
                    var all = ShipSystem.inst.ships;
                    for (int i = 0; i < all.Count; i++)   // .Count, never .data.Length
                    {
                        ShipBase ship = all.data[i];
                        if (ship == null || ship.meshes == null || ship.meshes.Length == 0) continue;

                        ships++;
                        bool painted = true;
                        for (int j = 0; j < ship.meshes.Length; j++)
                            if (ship.meshes[j] == null || ship.meshes[j].sharedMaterial == null) { painted = false; break; }

                        if (painted) paintedShips++;
                        else Log("  a " + ship.type + " on team " + ship.teamID + " has an unpainted hull");
                    }
                }

                if (ships == 0) Log("no ships in the world, so hull materials were not checked");
                else Check("every ship hull has a material", paintedShips == ships);

                // The other half of the same method: the first loop of UpdateMaterialFor fills
                // UnitCategory.mat, and the soldier draw loop skips any category without one.
                var categories = KaCMultiplayer.Net.PrivateField.Get<List<UnitSystem.UnitCategory>>(
                    UnitSystem.inst, "unitCategoriesGen");

                if (categories == null) Log("could not read unitCategoriesGen, so unit materials were not checked");
                else
                {
                    int cats = 0, litCats = 0;
                    for (int i = 0; i < categories.Count; i++)
                    {
                        UnitSystem.UnitCategory cat = categories[i];
                        if (cat == null) continue;

                        cats++;
                        if (cat.mat != null) litCats++;
                        else Log("  unit category for team " + cat.teamId + " has no material, so its soldiers do not draw");
                    }

                    Check("every unit category has a material", cats > 0 && litCats == cats);
                }
            }
            catch (Exception e)
            {
                Check("the material check ran without throwing", false);
                Log("material check threw: " + e.Message);
            }
        }

        /// <summary>
        /// Writes a real save of the running session and reads the mod's block back out of it.
        ///
        /// Built before refactoring the load path, deliberately. The game's developers asked us not
        /// to break player saves, and until now the save and load code had no automated coverage at
        /// all, so any change to it was a change made blind. This does not prove a save LOADS, which
        /// needs a world to load into, but it proves the parts that were silently breakable: that a
        /// session can be serialised, that our data survives the trip, and that every kingdom in the
        /// session is in it rather than just the local one.
        ///
        /// It also asserts the promise we made upstream: the container is a STOCK LoadSaveContainer
        /// and our data lives entirely in its mod dictionary. If that ever stops being true, saves
        /// stop opening in the unmodded game and we would not otherwise find out.
        /// </summary>
        private static void CheckSaveRoundTrip()
        {
            byte[] bytes;
            try { bytes = Main.PackLiveSnapshot(); }
            catch (Exception ex)
            {
                Main.LogEx("[SELFTEST] save round trip", ex);
                Check("the session can be saved", false);
                return;
            }

            Check("the session can be saved", bytes != null && bytes.Length > 0);
            if (bytes == null || bytes.Length == 0) return;

            Log("saved session snapshot: " + bytes.Length + " bytes");

            // WriteModSessionToDict rides along with the save it just made, so the live dictionary
            // now holds exactly what went into the file.
            LoadSaveOverrides.ModSessionData block;
            try { block = LoadSaveOverrides.ModSaveData.ReadSession(); }
            catch (Exception ex)
            {
                Main.LogEx("[SELFTEST] reading back the mod block", ex);
                Check("the mod's session block can be read back", false);
                return;
            }

            Check("the mod's session block can be read back", block != null);
            if (block == null) return;

            // Every kingdom, not only ours. Saving just the local player is the failure that would
            // quietly cost the other players their entire kingdom on the next load.
            Check("the save holds every kingdom in the session",
                  block.players.Count == Main.kCPlayers.Count);

            Check("each saved kingdom has a name and a team",
                  block.kingdomNames.Count == block.players.Count
                  && block.identity.Count == block.players.Count);

            // Relations are session state and were the thing silently forgotten on every load
            // before they were saved at all, so they are worth asserting separately.
            Check("declared wars are written into the save",
                  block.relations != null);

            Log("saved block: " + block.players.Count + " kingdom(s), "
                + (block.relations == null ? 0 : block.relations.Count) + " relation pair(s)");
        }

        /// <summary>
        /// The rule the game's developers asked us to hold to: with this mod installed, single
        /// player must behave exactly as though it were not.
        ///
        /// Written after we broke it. The mod used to patch LoadSave.GetSaveDir to point at its own
        /// directory whenever it believed a session was active, and the main menu's Load button
        /// calls that same function, so once the flag latched the game listed only multiplayer
        /// saves until the client was restarted. It was reported from outside, which is the wrong
        /// way to find out.
        ///
        /// Checked from INSIDE a running session on purpose. That is the state in which the old bug
        /// gave the wrong answer, so a check that only ran at the menu would have passed throughout.
        /// </summary>
        private static void CheckSinglePlayerIsUntouched()
        {
            string vanilla = Application.persistentDataPath + "/Saves";

            string actual;
            try { actual = LoadSave.GetSaveDir(); }
            catch (Exception ex)
            {
                Main.LogEx("[SELFTEST] save dir", ex);
                Check("the game's save directory is readable", false);
                return;
            }

            Log("save directory during a session: " + actual);

            // Normalised because the game builds this path by concatenation and a stray separator
            // would fail the comparison without meaning anything.
            // The re-completion bug is unpinned and currently silent: the log names a building once
            // and then holds its tongue, so a session where it happens five hundred times reads
            // exactly like one where it happened once. This is the line that would notice.
            Check("no building tried to complete itself twice",
                  Main.BuildingCompleteBuildHook.SkippedRecompletes == 0);

            Check("a multiplayer session does not move the game's save directory",
                  Normalise(actual) == Normalise(vanilla));
        }

        /// <summary>Trims trailing separators and squares up slashes, so two spellings of the same
        /// directory compare equal.</summary>
        private static string Normalise(string path)
        {
            if (path == null) return string.Empty;
            return path.Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>
        /// Who arbitrates a fight at sea. Not always the host, which is the interesting part.
        ///
        /// The first version of these checks assumed "ships are on water, water is unowned, so the
        /// host decides", and both assertions failed. The measurement said why: a dock's own water
        /// cells report landMassIdx 0 and owner team 6, the peer's island, even though the cell type
        /// is Water. **Coastal water belongs to the coast.** So the rule is the same one that
        /// settles every land fight, with no special case, and these now assert what it really does:
        /// somebody's harbour is theirs to arbitrate, and only water belonging to no landmass falls
        /// to the host. Both directions matter, because a model that answered "host" everywhere and
        /// one that answered "owner" everywhere would each pass half of this.
        /// </summary>
        /// <summary>
        /// Flags fly the colours of whoever owns the ground, not the colours of whoever is looking.
        ///
        /// The bug this pins down: the host saw every flag in the world blue and the client saw
        /// every flag yellow, each machine painting the whole map in its own livery. Both flag
        /// systems resolve their owner once, at the moment the object appears, and both then wait
        /// on Player.inst.updateBanner for a repaint. Nothing ever invoked it for anyone but the
        /// local player, so a remote kingdom's colours never reached a flag already standing.
        ///
        /// Driven the way the real thing happens: change the PEER's banner, the way an arriving
        /// BannerPick does, then ask for the repaint and look at a flag on the peer's island. The
        /// peer is deliberately moved to a livery the local player is not using, because if both
        /// kingdoms shared one the check would pass while painting entirely the wrong flag.
        /// </summary>
        private static void CheckBannerRepaint()
        {
            SessionPlayer peer = FindPeer();
            if (peer == null || peer.inst == null || peer.inst.PlayerLandmassOwner == null) return;
            if (Player.inst == null || Player.inst.PlayerLandmassOwner == null) return;

            try
            {
                LandmassOwner theirs = peer.inst.PlayerLandmassOwner;
                LandmassOwner ours = Player.inst.PlayerLandmassOwner;

                int liveries = (World.inst != null && World.inst.liverySets != null)
                    ? World.inst.liverySets.Count : 0;
                Check("the map offers more than one livery to tell kingdoms apart", liveries > 1);
                if (liveries <= 1) return;

                // Exactly what ApplyBanner does when a remote player picks a colour, including the
                // guard: vanilla's SetBannerIdx ends by walking every army with no null checks, so
                // it throws in a session where any army has lost its general. Everything a flag is
                // painted from is assigned before that point, which is why the checks below still
                // mean something when it does.
                int distinct = (ours.bannerIdx + 1) % liveries;
                Main.SetKingdomBanner(theirs, distinct, "the peer");

                Check("the peer flies a different banner from ours",
                      theirs.FlagMaterial != null && theirs.FlagMaterial != ours.FlagMaterial);

                if (peer.inst.keep == null)
                {
                    Log("the peer has no keep, so it owns no ground to check flags on");
                    return;
                }

                Cell theirGround = World.inst.GetCellDataClamped(peer.inst.keep.transform.position);
                Check("the peer has ground of its own", theirGround != null);
                if (theirGround == null) return;

                // What both flag systems actually read. If this is wrong the repaint cannot help,
                // and the bug would be in ownership rather than in painting.
                Check("the peer's island names the peer as its owner",
                      World.GetLandmassOwner(theirGround.landMassIdx) == theirs);

                Main.RefreshAllBanners();

                // The BUILDING flag, which is what the report is about, and it is IGBanner rather
                // than FlagMaterialUpdater: IGBanner is the component carrying a Building and a
                // banner mesh, and it paints by writing the owner's BannerTexture. Found on the
                // peer's own island rather than assumed, since which prefabs carry one is the
                // game's business.
                Log("the peer's buildings: " + PeerBuildingSummary(peer));

                // Taken from the peer's own keep rather than searched for in the scene. The scene
                // search kept coming back empty, and the reason is worth remembering: a keep's flag
                // hangs off the keep itself and can be inactive while fog hides the building, and
                // FindObjectsOfType does not return inactive objects. Asking the keep for it works
                // either way, and it is unambiguously the flag we mean.
                FlagMaterialUpdater theirFlag =
                    peer.inst.keep.GetComponentInChildren<FlagMaterialUpdater>(true);

                if (theirFlag == null)
                {
                    Log("the peer's keep carries no flag, skipping the paint check");
                    return;
                }

                MeshRenderer paint = theirFlag.GetComponent<MeshRenderer>();
                Check("the peer's keep flag can be painted", paint != null);
                if (paint == null) return;

                Log("the peer's keep flag is painted with: "
                    + (paint.sharedMaterial != null ? paint.sharedMaterial.name : "nothing")
                    + ", the peer's livery is "
                    + (theirs.FlagMaterial != null ? theirs.FlagMaterial.name : "nothing")
                    + ", ours is "
                    + (ours.FlagMaterial != null ? ours.FlagMaterial.name : "nothing"));

                // The bug in one line: every flag showed the LOCAL player's colours.
                Check("the peer's keep flag does not fly OUR colours",
                      paint.sharedMaterial != ours.FlagMaterial);

                Check("the peer's keep flag flies the peer's colours",
                      paint.sharedMaterial == theirs.FlagMaterial);
            }
            catch (Exception ex)
            {
                Check("the banner checks finished without throwing", false);
                Main.LogEx("[SELFTEST] banner repaint", ex);
            }
        }

        // A building the host placed through the REAL player path, checked again after the battle
        // has run. Guid.Empty means it could not be placed at all, which is itself the report.
        private static Guid hostBuilt = Guid.Empty;
        private static Guid hostKeepAtBuildTime = Guid.Empty;

        /// <summary>
        /// Places one building as a PLAYER does, broadcast and all, and remembers it.
        ///
        /// Written for two Workshop reports against 0.10.0: "if I place a castle as the host, it
        /// disappears, however the guest can place a castle and play normally", and a host "not
        /// being able to build anything". Neither is reproduced by anything the suite did, and the
        /// reason turned out to be a hole in the suite itself: PlaceHouseForLocalPlayer wraps
        /// World.Place in a NetApply.Scope, which SUPPRESSES the broadcast. Every placement the
        /// suite made was therefore a private one, and the path a real host actually takes, where
        /// World.Place trips PlaceHook and the placement goes on the wire, had never run here.
        ///
        /// So this one deliberately does NOT take that scope. It is the player's path.
        ///
        /// Placed after the load-back check on purpose, since that replaces the running world, and
        /// read again after the battle so several seconds of real simulation pass in between. A
        /// building that vanishes needs time to vanish in.
        /// </summary>
        private static void PrepareHostBuildFixture()
        {
            hostBuilt = Guid.Empty;
            hostKeepAtBuildTime = Guid.Empty;

            try
            {
                if (Player.inst == null || Player.inst.keep == null) return;

                Building keep = Player.inst.keep.GetComponent<Building>();
                if (keep == null) return;
                hostKeepAtBuildTime = keep.guid;

                Cell site = World.inst.GetCellDataClamped(keep.transform.position + new Vector3(-4f, 0f, 0f));
                if (site == null) { Log("no site for the host build fixture"); return; }

                Building b = UnityEngine.Object.Instantiate<Building>(
                    GameState.inst.GetPlaceableByUniqueName("smallhouse"));
                b.Init();
                b.transform.position = site.Position;
                b.SendMessage("OnPlayerPlacement", SendMessageOptions.DontRequireReceiver);

                // No NetApply.Scope. This is the whole point: let it broadcast exactly as a
                // player's click does, so whatever the wire does to it happens here too.
                World.inst.Place(b);

                hostBuilt = b.guid;
                Check("the host can place a building at all", Main.FindBuildingByGuidAnywhere(hostBuilt) != null);
                Log("host build fixture placed: " + hostBuilt + " at " + b.transform.position);
            }
            catch (Exception ex)
            {
                Check("placing a building as the host did not throw", false);
                Main.LogEx("[SELFTEST] host build fixture", ex);
            }
        }

        /// <summary>
        /// The host's building, and the host's keep, are both still there after the session has
        /// been running for a while. Straight from the two Workshop reports.
        /// </summary>
        private static void CheckHostBuildSurvived()
        {
            try
            {
                if (hostBuilt != Guid.Empty)
                    Check("a building the host placed is still there later",
                          Main.FindBuildingByGuidAnywhere(hostBuilt) != null);
                else
                    Log("no host build fixture was placed, nothing to re-check");

                Check("the host still has a keep", Player.inst != null && Player.inst.keep != null);

                if (hostKeepAtBuildTime != Guid.Empty)
                    Check("the host's keep is still the same building",
                          Main.FindBuildingByGuidAnywhere(hostKeepAtBuildTime) != null);
            }
            catch (Exception ex)
            {
                Check("the host build checks finished without throwing", false);
                Main.LogEx("[SELFTEST] host build survival", ex);
            }
        }

        // What we expect to find again on the other side of a save and a load. Guid.Empty means the
        // fixture could not be built, and the checks say so rather than passing vacuously.
        private static Guid survivingCatapult = Guid.Empty;
        private static Guid survivingArmy = Guid.Empty;
        private static Guid survivingDen = Guid.Empty;

        /// <summary>
        /// Leaves a catapult, an army and a wolf den in the world with their ids noted, so the load
        /// back can be asked whether they are the SAME ones.
        ///
        /// This is the late-joiner test, arrived at the long way round. Somebody joining a game in
        /// progress is not sent a stream of spawn messages; they are sent a live snapshot
        /// (SessionHandlers.QueueResumeTransfer -> Main.PackLiveSnapshot) and they load it. So what
        /// a late joiner actually depends on is that a save round trip preserves the ids every
        /// machine uses to talk about these things, and nothing tested that.
        ///
        /// It is worth testing rather than assuming, because one detail is load-bearing and easy to
        /// break: vanilla's catapult Unpack calls SiegeCatapult.Init BEFORE it assigns the saved
        /// guid, and Init is where the spawn broadcast hooks in. The only thing stopping a restore
        /// from announcing every catapult under the throwaway id its constructor rolled is the
        /// SessionSave.Unpacking guard. If that guard ever stopped covering the subsystem unpack,
        /// this check is what would notice.
        /// </summary>
        private static void PrepareIdSurvivalFixture()
        {
            survivingCatapult = Guid.Empty;
            survivingArmy = Guid.Empty;
            survivingDen = Guid.Empty;

            try
            {
                SiegeCatapult cat = BuildTestCatapult();
                if (cat != null) survivingCatapult = cat.guid;

                if (UnitSystem.inst != null)
                {
                    var armies = UnitSystem.inst.armies;
                    for (int i = 0; i < armies.Count; i++)   // .Count, never .data.Length
                        if (armies.data[i] != null) { survivingArmy = armies.data[i].guid; break; }
                }

                var dens = WolfDen.wolfDens;
                if (dens != null)
                    for (int i = 0; i < dens.Count; i++)
                        if (dens[i] != null) { survivingDen = dens[i].guid; break; }

                Log("ids to look for after the load: catapult " + survivingCatapult
                    + ", army " + survivingArmy + ", wolf den " + survivingDen);
            }
            catch (Exception ex) { Main.LogEx("[SELFTEST] preparing the id survival fixture", ex); }
        }

        /// <summary>
        /// The same things came back, under the same ids, exactly once each.
        ///
        /// "Exactly once" matters as much as "at all". A restore that re-broadcast its spawns, or
        /// one that rolled fresh ids, would leave the world looking right while every later message
        /// about these units landed on nothing, which is the silent half of the failure.
        /// </summary>
        private static void CheckIdsSurvivedTheLoad()
        {
            try
            {
                if (survivingCatapult != Guid.Empty)
                {
                    Check("a siege catapult keeps its shared id across a save and a load",
                          Main.FindSiegeCatapultByGuid(survivingCatapult) != null);

                    Check("the load restored exactly one catapult under that id",
                          CountCatapultsWithId(survivingCatapult) == 1);
                }
                else Log("no catapult was in the world to follow through the load");

                if (survivingArmy != Guid.Empty)
                    Check("an army keeps its shared id across a save and a load",
                          UnitSystem.inst != null &&
                          UnitSystem.inst.FindArmyByGuid(survivingArmy) != null);
                else Log("no army was in the world to follow through the load");

                if (survivingDen != Guid.Empty)
                    Check("a wolf den keeps its shared id across a save and a load",
                          Main.FindWolfDenByGuid(survivingDen) != null);
                else Log("no wolf den was in the world to follow through the load");
            }
            catch (Exception ex)
            {
                Check("the id survival checks finished without throwing", false);
                Main.LogEx("[SELFTEST] id survival", ex);
            }
        }

        /// <summary>How many catapults answer to one id. More than one means a duplicated restore.</summary>
        private static int CountCatapultsWithId(Guid id)
        {
            int found = 0;
            var all = SiegeCatapultSystem.siegeCatapults;
            if (all == null) return 0;

            for (int i = 0; i < all.Count; i++)   // .Count, never .data.Length
            {
                SiegeCatapult c = all.data[i];
                if (c != null && c.guid == id) found++;
            }
            return found;
        }

        /// <summary>
        /// A position stated by another player moves our copy of their army, and only when it has
        /// really drifted.
        ///
        /// Both halves are asserted because the threshold is the whole design. Correcting on every
        /// small difference would fight the local pathing that is already walking the army to the
        /// same place, and the army would stutter; correcting on nothing would leave the divergence
        /// this exists to fix. So one check proves a big gap is closed and the other proves a small
        /// one is left alone.
        ///
        /// Driven by calling Apply directly rather than by sending a message, because the point
        /// under test is the correction rule, not the wire. The peer's army stands in for "somebody
        /// else's", which is what makes it eligible at all; our own armies are authoritative here
        /// and are deliberately skipped.
        /// </summary>
        private static void CheckArmyPositions()
        {
            try
            {
                // The fake peer's attacking army, spawned by CheckCombat, is the one army in the
                // session that belongs to somebody else.
                UnitSystem.Army theirs = null;
                int localTeam = LocalTeam();
                var armies = UnitSystem.inst != null ? UnitSystem.inst.armies : null;

                if (armies != null)
                    for (int i = 0; i < armies.Count; i++)   // .Count, never .data.Length
                    {
                        UnitSystem.Army a = armies.data[i];
                        if (a == null || a.teamId == localTeam) continue;
                        theirs = a;
                        break;
                    }

                if (theirs == null)
                {
                    Log("no other player's army in the world, skipping the position checks");
                    return;
                }

                Vector3 start = theirs.generalPos;
                int correctedBefore = ArmyPositionSync.Corrected;

                // A small difference must be ignored, or every correction fights local pathing.
                ArmyPositionSync.Apply(new ArmyPositionsMessage
                {
                    Armies = new List<Guid> { theirs.guid },
                    Positions = new List<Vector3> { start + new Vector3(0.5f, 0f, 0f) }
                });

                Check("a small difference in position is left alone",
                      ArmyPositionSync.Corrected == correctedBefore);

                // A real divergence must be closed.
                Vector3 far = start + new Vector3(8f, 0f, 8f);
                ArmyPositionSync.Apply(new ArmyPositionsMessage
                {
                    Armies = new List<Guid> { theirs.guid },
                    Positions = new List<Vector3> { far }
                });

                Log("army position: " + start + " -> " + theirs.generalPos
                    + ", the owner said " + far);

                Check("a drifted army is moved to where its owner says it is",
                      ArmyPositionSync.Corrected > correctedBefore);

                Check("the army actually ended up there",
                      (theirs.generalPos - far).sqrMagnitude < 1f);

                // Our own armies are ours to state, never to be told about.
                UnitSystem.Army ours = null;
                if (armies != null)
                    for (int i = 0; i < armies.Count; i++)
                    {
                        UnitSystem.Army a = armies.data[i];
                        if (a == null || a.teamId != localTeam) continue;
                        ours = a;
                        break;
                    }

                if (ours != null)
                {
                    Vector3 mine = ours.generalPos;
                    ArmyPositionSync.Apply(new ArmyPositionsMessage
                    {
                        Armies = new List<Guid> { ours.guid },
                        Positions = new List<Vector3> { mine + new Vector3(20f, 0f, 20f) }
                    });

                    Check("nobody else can move OUR army", ours.generalPos == mine);
                }

                // Dark by default, so with the flag off the sweep must never put anything on the
                // wire. Same shape as the combat-sync silence check.
                int publishedBefore = ArmyPositionSync.Published;
                for (int i = 0; i < 40; i++) ArmyPositionSync.Tick();

                if (Main.ArmyPositionSyncEnabled)
                    Check("army positions are announced while the feature is on",
                          ArmyPositionSync.Published > publishedBefore);
                else
                    Check("army position sync stays silent while the feature is off",
                          ArmyPositionSync.Published == publishedBefore);
            }
            catch (Exception ex)
            {
                Check("the army position checks finished without throwing", false);
                Main.LogEx("[SELFTEST] army positions", ex);
            }
        }

        /// <summary>
        /// Wolves: one machine decides how a wolf fight went, and says so.
        ///
        /// The last of the six things in the game that implement IProjectileHitable, and the last
        /// one nobody arbitrated. A wolf is an entry in its den's list rather than an object with a
        /// name, which is why the pack travels as a den's worth of lives keyed by the den's Guid,
        /// and why this checks the pack rather than an individual animal.
        ///
        /// Skips rather than fails on a map with no wolf den on our own ground: dens come from
        /// world generation, so whether one is reachable is the map's business, not the mod's.
        /// </summary>
        private static void CheckWolves()
        {
            try
            {
                var dens = WolfDen.wolfDens;
                Log("wolf dens in the world: " + (dens == null ? 0 : dens.Count));

                WolfDen ours = null;
                if (dens != null)
                    for (int i = 0; i < dens.Count; i++)
                    {
                        WolfDen d = dens[i];
                        if (d == null || d.wolfData == null || d.wolfData.Count == 0) continue;
                        if (!CombatAuthority.ResolvesHere(d.GetPos())) continue;
                        ours = d;
                        break;
                    }

                // Whether a map generates a den on our island is the map's business, and two runs on
                // two seeds should not test different things. Build one if the map did not.
                if (ours == null) ours = BuildTestWolfDen();

                Check("a wolf den with wolves stands on ground we arbitrate", ours != null);
                if (ours == null) return;

                Check("a wolf den we arbitrate has a shared id",
                      Main.FindWolfDenByGuid(ours.guid) == ours);

                WolfDen.WolfData wolf = ours.wolfData.data[0];
                Check("the den has a wolf to test with", wolf != null);
                if (wolf == null) return;

                float before = wolf.life;
                int publishedBefore = CombatSync.PublishedUpdates;

                // Through the projectile path, the one the authority hook guards, and the only way
                // a wolf can be hurt at all.
                wolf.TakeProjectileDamage(1f, DamageType.Standard, DamageSource.Ranged,
                                          wolf.pos, Vector3.zero, EnemyTeamForTest());

                Check("a wolf on ground we arbitrate takes damage", wolf.life < before);

                // The sweep runs only on every Nth call, so drive it until it sweeps.
                for (int i = 0; i < 40 && CombatSync.PublishedUpdates == publishedBefore; i++)
                    CombatSync.Tick();

                Log("wolf damage: " + before + " -> " + wolf.life
                    + ", updates published " + (CombatSync.PublishedUpdates - publishedBefore));

                if (Main.CombatAuthorityEnabled)
                    Check("the arbiter announced the wolf pack's state",
                          CombatSync.PublishedUpdates > publishedBefore);
                else
                    Check("wolf sync stays silent while the feature is off",
                          CombatSync.PublishedUpdates == publishedBefore);

                // Now play the other machine. A verdict arrives saying the pack is worse off than
                // we think, and it must land without going near the damage method our own hook is
                // suppressing.
                List<float> verdict = new List<float>();
                for (int i = 0; i < ours.wolfData.Count; i++) verdict.Add(1f);

                CombatSync.ApplyWolfPackHealth(
                    new WolfPackHealthMessage { Den = ours.guid, Lives = verdict });

                Check("a verdict from the arbiter wounds the local pack", wolf.life <= 1f);

                // And the restraint that matters: a pack is never handed health back.
                float wounded = wolf.life;
                List<float> healing = new List<float>();
                for (int i = 0; i < ours.wolfData.Count; i++) healing.Add(9999f);

                CombatSync.ApplyWolfPackHealth(
                    new WolfPackHealthMessage { Den = ours.guid, Lives = healing });

                Check("a verdict never heals a wolf back up", wolf.life == wounded);
            }
            catch (Exception ex)
            {
                Check("the wolf checks finished without throwing", false);
                Main.LogEx("[SELFTEST] wolves", ex);
            }
        }

        /// <summary>
        /// Puts a wolf den with a pack on our own island, the way the game does: AddWolfDen at a
        /// cell, then a wolf count.
        ///
        /// Note this permanently retypes the cell, exactly as generation would, so it is only ever
        /// done in a throwaway automated session. A den is ordinary furniture for a map, and the
        /// save checks that run afterwards are the better for having one to carry.
        /// </summary>
        private static WolfDen BuildTestWolfDen()
        {
            try
            {
                if (Player.inst == null || Player.inst.keep == null || World.inst == null) return null;

                Cell home = World.inst.GetCellDataClamped(Player.inst.keep.transform.position);
                if (home == null) return null;

                // A clear cell on our own landmass, far enough from the keep not to sit inside it.
                Cell site = null;
                foreach (Cell c in World.inst.GetCellsData())
                {
                    if (c == null) continue;
                    if (c.landMassIdx != home.landMassIdx) continue;
                    if (c.Type == ResourceType.Water) continue;
                    if (c.TopMostStructure != null) continue;
                    if (Mathf.Abs(c.x - home.x) + Mathf.Abs(c.z - home.z) < 4) continue;
                    site = c;
                    break;
                }

                if (site == null) { Log("no clear cell on our island for a test wolf den"); return null; }

                WolfDen den = World.inst.AddWolfDen(site.x, site.z);
                if (den == null) { Log("AddWolfDen returned nothing"); return null; }

                den.SetWolfCount(3);
                Log("built a test wolf den at (" + site.x + "," + site.z + ") with "
                    + den.WolfCount() + " wolves");
                return den.wolfData != null && den.wolfData.Count > 0 ? den : null;
            }
            catch (Exception ex)
            {
                Main.LogEx("[SELFTEST] building a test wolf den", ex);
                return null;
            }
        }

        /// <summary>What the peer actually built, so a skipped flag check can be read against it.</summary>
        private static string PeerBuildingSummary(SessionPlayer peer)
        {
            try
            {
                if (peer == null || peer.inst == null || peer.inst.Buildings == null) return "none";

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                var all = peer.inst.Buildings;
                for (int i = 0; i < all.Count; i++)   // .Count, never .data.Length
                {
                    Building b = all.data[i];
                    if (b == null) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(b.UniqueName).Append(" lm=").Append(b.LandMass())
                      .Append(b.GetComponent<IGBanner>() != null ? " [has flag]" : " [no flag]");
                }
                return sb.Length > 0 ? sb.ToString() : "none";
            }
            catch (Exception ex) { return "unreadable: " + ex.Message; }
        }

        /// <summary>
        /// Siege catapults: that they exist for everybody, that one machine arbitrates their
        /// fights, and that the verdict travels in both directions.
        ///
        /// Catapults were the last hitable thing in the game with no multiplayer story at all.
        /// They are built by Barracks.Tick, a foreign player's barracks is deliberately not ticked
        /// here, and the catapult branch never went through MakeArmy, so one existed on exactly one
        /// machine. This walks the whole path solo: build one, confirm we arbitrate it, damage it
        /// through the projectile route the hook actually guards, confirm the outcome was
        /// published, then play the other machine's part and confirm an arriving verdict both
        /// hurts and, at zero, destroys the local copy.
        ///
        /// The death half is the one worth having. Writing the number alone would leave a catapult
        /// at zero life, intact and manned, on every machine but the arbiter's, and undamageable
        /// there because its own damage is suppressed. That is exactly the regression buildings
        /// shipped with once, so it gets a check rather than an argument.
        /// </summary>
        private static void CheckSiegeCatapults()
        {
            SiegeCatapult cat = BuildTestCatapult();

            Check("a siege catapult can be built for the local kingdom", cat != null);
            if (cat == null) return;

            try
            {
                Guid id = cat.guid;

                Check("a catapult is found by the id every machine shares",
                      Main.FindSiegeCatapultByGuid(id) == cat);

                Vector3 where = cat.transform.position;

                Check("a catapult on our own ground belongs to our team",
                      CombatAuthority.LandOwnerTeamAt(where) == LocalTeam());

                Check("we arbitrate a catapult standing on our own ground",
                      CombatAuthority.ResolvesHere(where));

                // The peer's island is the other half of the rule: their ground, their verdict,
                // even when the catapult on it is ours. That is the case that matters for a siege
                // weapon, whose whole purpose is to be standing on somebody else's land.
                SessionPlayer peer = FindPeer();
                if (peer != null && peer.inst != null && peer.inst.keep != null)
                {
                    Vector3 theirs = peer.inst.keep.transform.position;
                    Check("we do not arbitrate a catapult on the peer's ground",
                          !CombatAuthority.ResolvesHere(theirs));
                }

                // Through the PROJECTILE path, because that is the one the authority hook guards.
                // A catapult has no other damage method at all, which is why the apply side needs
                // to get past our own suppression to use this same route.
                float before = cat.life;
                int publishedBefore = CombatSync.PublishedUpdates;

                ((IProjectileHitable)cat).TakeProjectileDamage(
                    10f, DamageType.Standard, DamageSource.Ranged,
                    where, Vector3.zero, EnemyTeamForTest());

                Check("a catapult on our own ground takes damage", cat.life < before);

                // The sweep only runs on every Nth call, so a single Tick() almost never reaches
                // it. Drive it until it sweeps.
                for (int i = 0; i < 40 && CombatSync.PublishedUpdates == publishedBefore; i++)
                    CombatSync.Tick();

                Log("catapult damage: " + before + " -> " + cat.life
                    + ", updates published " + (CombatSync.PublishedUpdates - publishedBefore));

                if (Main.CombatAuthorityEnabled)
                    Check("the arbiter announced the catapult damage",
                          CombatSync.PublishedUpdates > publishedBefore);
                else
                    Check("catapult sync stays silent while the feature is off",
                          CombatSync.PublishedUpdates == publishedBefore);

                // Now play the other machine: a verdict arrives saying the catapult is worse off
                // than we think. It has to get through the suppression hook, which is the part
                // that has no equivalent in the building or dragon paths.
                float verdict = cat.life - 20f;
                CombatSync.ApplyCatapultHealth(
                    new SiegeCatapultHealthMessage { Catapult = id, Life = verdict });

                Check("a verdict from the arbiter damages the local catapult", cat.life <= verdict);

                // And the verdict that matters: zero means gone, not standing at zero life.
                CombatSync.ApplyCatapultHealth(
                    new SiegeCatapultHealthMessage { Catapult = id, Life = 0f });

                Check("a verdict of zero destroys the local catapult, it does not leave it standing",
                      Main.FindSiegeCatapultByGuid(id) == null);

                // Catapults are frozen for a DEPARTED kingdom, the same as its armies and ships,
                // now that everyone holds a copy of everyone else's. The failure that would hurt is
                // the opposite one: freezing a kingdom that is still playing stops that player
                // dead, so what gets asserted is that our own, very much present, kingdom is not.
                Check("a live kingdom's catapults are never frozen",
                      !FrozenKingdoms.IsFrozen(LocalTeam()));
            }
            catch (Exception ex)
            {
                Check("the catapult checks finished without throwing", false);
                Main.LogEx("[SELFTEST] siege catapults", ex);
            }
            finally
            {
                // Whatever went wrong above, do not leave a test catapult standing in the session
                // the later save and load checks are about to measure.
                if (cat != null && !cat.IsDead()) cat.Release();
            }
        }

        /// <summary>
        /// Builds one catapult for the local team beside the keep, the same way the game does:
        /// instantiate the prefab, place it, Init it with the owning team.
        ///
        /// Both of the game's own spawn sites take the prefab from RaiderSystem, barracks included,
        /// so this does too and falls back to SiegeCatapultSystem's copy rather than giving up.
        /// </summary>
        private static SiegeCatapult BuildTestCatapult()
        {
            try
            {
                if (Player.inst == null || Player.inst.keep == null) return null;

                GameObject prefab = (RaiderSystem.inst != null) ? RaiderSystem.inst.siegeCatapultPrefab : null;
                if (prefab == null && SiegeCatapultSystem.inst != null)
                    prefab = SiegeCatapultSystem.inst.siegeCatapultPrefab;
                if (prefab == null) { Log("no siege catapult prefab available"); return null; }

                Cell at = World.inst.GetCellDataClamped(Player.inst.keep.transform.position);
                if (at == null) return null;

                GameObject go = UnityEngine.Object.Instantiate<GameObject>(prefab);
                go.transform.position = at.Center;

                SiegeCatapult c = go.GetComponent<SiegeCatapult>();
                if (c == null) { UnityEngine.Object.Destroy(go); return null; }

                c.Init(LocalTeam());
                c.SetPos(at.Center);
                return c;
            }
            catch (Exception ex)
            {
                Main.LogEx("[SELFTEST] building a test catapult", ex);
                return null;
            }
        }

        private static void CheckShipArbitration()
        {
            SessionPlayer peer = FindPeer();
            if (peer == null || peer.inst == null || peer.inst.PlayerLandmassOwner == null) return;

            int peerTeam = peer.inst.PlayerLandmassOwner.teamId;

            Dock dock = FindDockOfTeam(peerTeam);
            if (dock == null || dock.dockPositions == null || dock.dockPositions.Count == 0) return;

            Vector3 harbour = dock.dockPositions[0].Center;

            Check("a player's own harbour water belongs to that player",
                  CombatAuthority.LandOwnerTeamAt(harbour) == peerTeam);

            Check("we do not arbitrate a fight in someone else's harbour",
                  !CombatAuthority.ResolvesHere(harbour));

            // Genuinely unowned water, found rather than guessed at: a position whose cell belongs
            // to no landmass owner at all.
            Vector3 openSea;
            if (FindUnownedWater(out openSea))
            {
                Check("open sea belongs to nobody",
                      CombatAuthority.LandOwnerTeamAt(openSea) == CombatRule.NoTeam);

                // This machine hosts every automated run, so unowned ground falls to it.
                Check("the host arbitrates fights on unowned water",
                      CombatAuthority.ResolvesHere(openSea));
            }
            else
            {
                Log("no unowned water found on this map, skipping the open-sea checks");
            }

            // Whatever ships exist by now must each be arbitrated by exactly one machine; from here
            // we can only confirm our own half, which is that we claim the ones on our own water.
            int afloat = 0;
            int ours = 0;
            var ships = ShipSystem.inst != null ? ShipSystem.inst.ships : null;
            if (ships != null)
                for (int i = 0; i < ships.Count; i++)
                {
                    ShipBase ship = ships.data[i];
                    if (ship == null) continue;

                    afloat++;

                    bool mine = CombatAuthority.ResolvesHere(ship.GetPos());
                    int owner = CombatAuthority.LandOwnerTeamAt(ship.GetPos());
                    bool shouldBeMine = owner == LocalTeam() || owner == CombatRule.NoTeam;

                    if (mine == shouldBeMine) ours++;
                }

            Log("ships afloat: " + afloat + ", arbitration agreed with the rule for: " + ours);
            Check("every ship afloat is arbitrated by the rule its position implies", ours == afloat);
        }

        /// <summary>
        /// A water position owned by no landmass, or false if this map has none reachable.
        ///
        /// Walks outward from the middle of the map rather than scanning every cell, since maps are
        /// large and one hit is enough. Deep ocean is normally the centre-most thing between
        /// islands, so this usually succeeds on the first ring.
        /// </summary>
        private static bool FindUnownedWater(out Vector3 found)
        {
            found = Vector3.zero;
            if (World.inst == null) return false;

            int w = World.inst.GridWidth;
            int h = World.inst.GridHeight;

            for (int step = 0; step < 40; step++)
            {
                for (int dz = -step; dz <= step; dz += (step == 0 ? 1 : 2 * step))
                    for (int dx = -step; dx <= step; dx++)
                    {
                        int x = w / 2 + dx;
                        int z = h / 2 + dz;
                        if (x < 0 || z < 0 || x >= w || z >= h) continue;

                        Cell c = World.inst.GetCellData(x, z);
                        if (c == null || c.Type != ResourceType.Water) continue;
                        if (CombatAuthority.LandOwnerTeamAt(c.Center) != CombatRule.NoTeam) continue;

                        found = c.Center;
                        return true;
                    }
            }
            return false;
        }

        /// <summary>
        /// Villagers die by being destroyed, not damaged, and that path is arbitrated too.
        ///
        /// Worth its own checks because the failure is silent in both directions: a machine that
        /// wrongly declines to resolve leaves villagers who never die, and one that wrongly resolves
        /// puts every machine back to killing its own copy on its own schedule. Both look like a
        /// working game right up until two players compare populations.
        /// </summary>
        private static void CheckVillagerDeaths()
        {
            Villager victim = FindLocalVillager();

            Check("the local kingdom has villagers to test with", victim != null);
            if (victim == null) return;

            // Our own people stand on our own ground, so we must be the ones who decide.
            Check("we arbitrate deaths among our own villagers",
                  CombatAuthority.ResolvesHere(victim.Pos));

            CheckMovingHouseIsAnnounced();

            int before = Villager.villagers.Count;
            int publishedBefore = Main.VillagerDeathAuthorityHook.Published;
            int deferredBefore = Main.VillagerDeathAuthorityHook.Deferred;

            Player.inst.DestroyPerson(victim, leaveBehindBody: false);

            Check("a villager we arbitrate actually dies",
                  Villager.villagers.Count == before - 1);

            Check("we did not defer a death on our own ground",
                  Main.VillagerDeathAuthorityHook.Deferred == deferredBefore);

            // Same shape as the combat publish check: only meaningful with the feature switched on,
            // and with it off the assertion flips to "stayed quiet", which is equally worth knowing.
            int published = Main.VillagerDeathAuthorityHook.Published - publishedBefore;

            if (Main.CombatAuthorityEnabled)
                Check("the death was announced to the other machines", published == 1);
            else
                Check("death sync stays silent while the feature is off", published == 0);
        }

        /// <summary>
        /// Houses a villager and checks the move is announced to the other machines.
        ///
        /// The first version of this asserted that our villagers were housed rather than homeless,
        /// and it failed on a correct game: a session seconds old has no houses in it yet, so every
        /// starting villager is legitimately homeless. It was measuring the world instead of the
        /// thing that was actually broken, which is that where a villager LIVES never travelled.
        /// So this builds a house, moves somebody in, and asserts the announcement went out.
        ///
        /// Without the fix a villager is housed on their owner's machine and filed as homeless on
        /// every other one, forever. Homelessness is not cosmetic here: it drives unhappiness and
        /// it kills, so another player's kingdom quietly rotted when viewed from anywhere else.
        /// </summary>
        private static void CheckMovingHouseIsAnnounced()
        {
            Home house = PlaceHouseForLocalPlayer();

            Check("a house can be built to move someone into", house != null);
            if (house == null) return;

            Villager mover = FindLocalVillager();
            if (mover == null) return;

            int before = Main.VillagerSetHomeHook.Published;

            mover.SetHome(house);

            Check("the villager actually moved in", mover.Residence != null);
            Check("moving house is announced to the other machines",
                  Main.VillagerSetHomeHook.Published > before);
        }

        /// <summary>
        /// Builds one house next to the local keep, the same way the fake-peer fixture places its
        /// dock: aim the Player singleton at the owner, place inside a NetApply.Scope so the fixture
        /// never goes on the wire, and complete the build so it is usable immediately.
        /// </summary>
        private static Home PlaceHouseForLocalPlayer()
        {
            if (Player.inst == null || Player.inst.keep == null) return null;

            try
            {
                Building keep = Player.inst.keep.GetComponent<Building>();
                if (keep == null) return null;

                Cell site = World.inst.GetCellDataClamped(keep.transform.position + new Vector3(4f, 0f, 0f));
                if (site == null) return null;

                Building b = UnityEngine.Object.Instantiate<Building>(
                    GameState.inst.GetPlaceableByUniqueName("smallhouse"));
                b.Init();
                b.transform.position = site.Position;
                b.SendMessage("OnPlayerPlacement", SendMessageOptions.DontRequireReceiver);

                using (NetApply.Scope())
                {
                    World.inst.Place(b);
                    if (!b.IsBuilt()) b.CompleteBuild();
                }

                return b.GetComponent<Home>();
            }
            catch (Exception ex)
            {
                Main.LogEx("[SELFTEST] placing a test house", ex);
                return null;
            }
        }

        /// <summary>How many villagers have a home, counted the Count-bounded way.</summary>
        private static int CountHousedVillagers()
        {
            var all = Villager.villagers;
            if (all == null) return 0;

            int n = 0;
            for (int i = 0; i < all.Count; i++)
            {
                Villager v = all.data[i];
                if (v != null && v.Residence != null) n++;
            }
            return n;
        }

        /// <summary>
        /// A villager belonging to the local player, found the safe way.
        ///
        /// Count-bounded and null-checked rather than a LINQ pass over .data, which is the backing
        /// array and holds nulls and stale entries past Count.
        /// </summary>
        private static Villager FindLocalVillager()
        {
            var all = Villager.villagers;
            if (all == null) return null;

            int local = LocalTeam();
            for (int i = 0; i < all.Count; i++)
            {
                Villager v = all.data[i];
                if (v == null) continue;

                try { if (CombatAuthority.LandOwnerTeamAt(v.Pos) == local) return v; }
                catch { /* unowned ground, keep looking */ }
            }
            return null;
        }

        private static void CheckSessionBasics()
        {
            Check("a session is running and this machine is the host",
                  NetClient.client.IsConnected && NetHost.IsRunning);

            Check("the local player has a kingdom and a team",
                  Player.inst != null && Player.inst.PlayerLandmassOwner != null
                  && Player.inst.PlayerLandmassOwner.teamId >= PlayerRelations.MpTeamBase);

            Check("the local player was given a keep", Player.inst != null && Player.inst.keep != null);
        }

        private static void CheckFakePeer()
        {
            FakePeer.Toggle();   // spawn

            Check("the fake peer spawned as a second kingdom", FakePeer.IsActive);

            SessionPlayer peer = FindPeer();
            Check("the fake peer is in the roster with its own team",
                  peer != null && peer.inst != null && peer.inst.PlayerLandmassOwner != null
                  && peer.inst.PlayerLandmassOwner.teamId != LocalTeam());

            Check("the fake peer has a keep on its own island",
                  peer != null && peer.inst != null && peer.inst.keep != null);
        }

        private static void CheckTradeFixture()
        {
            SessionPlayer peer = FindPeer();
            if (peer == null || peer.inst == null) { Check("fake peer trading post", false); return; }

            Check("the fake peer has gold to trade with",
                  peer.inst.PlayerLandmassOwner != null && peer.inst.PlayerLandmassOwner.Gold > 0);

            Dock dock = FindDockOfTeam(peer.inst.PlayerLandmassOwner.teamId);
            Check("the fake peer has a dock", dock != null);

            // A landlocked dock is the failure this fixture is most likely to hit, and it would
            // otherwise show up much later as a merchant that never arrives.
            Check("the fake peer's dock has water positions a merchant can reach",
                  dock != null && dock.dockPositions != null && dock.dockPositions.Count > 0);

            if (dock == null || dock.loadingStorageComponent == null) return;

            // The trade economics, against real stock rather than a mock: what the dock actually
            // holds, priced with the real pay costs, must round-trip through the wire form.
            ResourceAmount stored = ((Assets.Interface.IResourceStorage)dock.loadingStorageComponent).StoredPublicResources();
            Check("the fake peer's dock is stocked", stored.GetTotalCount() > 0);

            List<int> wire = TradeMath.ToList(stored);
            Check("real dock stock survives the trade wire format",
                  TradeMath.FromList(wire) == stored);
        }

        private static void CheckDiplomacy()
        {
            Check("the diplomacy prefabs are in the loaded bundle",
                  LobbyPrefabs.DiplomacyScreen != null && LobbyPrefabs.DiplomacyRow != null);

            DiplomacyWindow.Show();
            Check("the diplomacy window opens", DiplomacyWindow.IsOpen);

            DiplomacyWindow.Hide();
            Check("the diplomacy window closes", !DiplomacyWindow.IsOpen);
        }

        private static void CheckChat()
        {
            InGameChat.Reset();
            InGameChat.Add("Tester", "hello", false);

            // Capturing drives the hook that suppresses the game's keyboard, so a stuck value would
            // leave a player unable to control anything.
            Check("chat is not capturing the keyboard while closed", !InGameChat.Capturing);
        }

        private static void CheckRelationsPersistence()
        {
            SessionPlayer peer = FindPeer();
            if (peer == null || peer.inst == null || peer.inst.PlayerLandmassOwner == null)
            {
                Check("relations round-trip", false);
                return;
            }

            int local = LocalTeam();
            int other = peer.inst.PlayerLandmassOwner.teamId;

            PlayerRelations.Set(local, other, World.Relations.Enemy);
            Check("a declaration of war is recorded",
                  PlayerRelations.Get(local, other) == World.Relations.Enemy);

            // Exactly what a save does: snapshot, wipe, restore.
            Dictionary<long, World.Relations> snapshot = PlayerRelations.Snapshot();
            PlayerRelations.Reset();
            Check("resetting clears the war", PlayerRelations.Get(local, other) == World.Relations.Neutral);

            PlayerRelations.Restore(snapshot);
            Check("a saved war survives snapshot and restore",
                  PlayerRelations.Get(local, other) == World.Relations.Enemy);

            PlayerRelations.Set(local, other, World.Relations.Neutral);   // leave it peaceful
        }

        private static void CheckCombat()
        {
            int armiesBefore = UnitSystem.inst.armies.Count;

            FakePeer.SendHostileArmy();

            Check("a hostile army was spawned", UnitSystem.inst.armies.Count > armiesBefore);

            UnitSystem.Army enemy = NewestArmy();
            if (enemy == null) { Check("combat arbitration", false); return; }

            Check("the enemy army belongs to the fake peer, not us",
                  enemy.teamId != LocalTeam());

            // The attackers land on OUR island, so under landmass authority this machine is the
            // arbiter for that fight. Getting this wrong in either direction is the whole failure
            // mode of the model: nobody resolving, or everybody.
            Check("this machine arbitrates a fight on its own ground",
                  CombatAuthority.ResolvesHere(enemy.generalPos));

            Check("the arbiter for the enemy's position is a real team",
                  CombatAuthority.LandOwnerTeamAt(enemy.generalPos) != CombatRule.NoTeam);

            Check("the enemy army was given a full squad of units",
                  enemy.units != null && enemy.units.Count == enemy.squadSize);

            CheckDefencesCanSeeTheEnemy(enemy);

            // Handed to the battle phase rather than released here: a fight needs frames, and
            // releasing is asserted at the end of it instead.
            battleEnemy = enemy;
        }

        /// <summary>
        /// Asks the question an archer or ballista tower asks, and checks it finds the enemy.
        ///
        /// Combat is not only melee. Defensive buildings pick their target through
        /// OrdersManager.ClosestEnemyUnitRankedArmyFirst, and that method walks unitsByTeamID BY
        /// INDEX, treating the index as a team id and testing RelationBetween against it. Vanilla
        /// allocates that array with five slots, so before it was widened the loop could not reach a
        /// multiplayer team at all: towers would have been blind to every enemy in the game, while
        /// looking and sounding perfectly healthy.
        ///
        /// Checked with the query rather than with a real tower on purpose. A tower only fires when
        /// it is staffed, so building one here would test the job system as much as the targeting,
        /// and would report a red herring whenever no archer happened to be assigned.
        /// </summary>
        private static void CheckDefencesCanSeeTheEnemy(UnitSystem.Army enemy)
        {
            if (enemy == null || OrdersManager.inst == null) return;

            int local = LocalTeam();

            // Declare first: a tower shoots enemies, and the fixture starts everyone neutral.
            SessionPlayer peer = FindPeer();
            if (peer != null && peer.inst != null && peer.inst.PlayerLandmassOwner != null)
                PlayerRelations.Set(local, peer.inst.PlayerLandmassOwner.teamId, World.Relations.Enemy);

            IMoveableUnit found = null;
            try
            {
                found = OrdersManager.inst.ClosestEnemyUnitRankedArmyFirst(
                    enemy.generalPos, 0f, 30f, local);
            }
            catch (Exception ex)
            {
                Main.LogEx("[SELFTEST] tower target query", ex);
            }

            Check("a defensive building can find an enemy army to shoot at", found != null);
        }

        // ---- the battle ---------------------------------------------------------------------

        /// <summary>
        /// Sets up a real fight between the local player and the fake peer, and returns whether the
        /// run should now spend frames on it.
        ///
        /// Everything above this point is a snapshot: it proves objects exist and have the right
        /// shape. None of it proves an army can actually FIGHT, which is the feature. Combat damage
        /// is applied by the army tick, so the only way to assert it is to start a battle, let the
        /// game run, and compare life before and after. This is what caught the missing unit
        /// categories: creating the enemy army at all threw, and no snapshot check could see that.
        /// </summary>
        private static bool StartBattle()
        {
            if (battleEnemy == null || UnitSystem.inst == null || Player.inst == null)
            {
                Check("a battle could be set up", false);
                return false;
            }

            int local = LocalTeam();

            // The two sides must be at war or nothing will engage. CheckRelationsPersistence leaves
            // them neutral on purpose, so declare here.
            SessionPlayer peer = FindPeer();
            if (peer != null && peer.inst != null && peer.inst.PlayerLandmassOwner != null)
                PlayerRelations.Set(local, peer.inst.PlayerLandmassOwner.teamId, World.Relations.Enemy);

            // Our army spawns on top of the attackers so the fight starts immediately rather than
            // after a march the test would have to wait out.
            battleOurs = UnitSystem.inst.MakeArmy(
                battleEnemy.generalPos + new Vector3(1.5f, 0f, 0f), local, UnitSystem.ArmyType.Default, true);

            Check("the local player can raise an army of their own", battleOurs != null);
            if (battleOurs == null) return false;

            Check("our army is on our own team", battleOurs.teamId == local);
            Check("our army was given a full squad of units",
                  battleOurs.units != null && battleOurs.units.Count == battleOurs.squadSize);

            // The whole point of the stance fix: a multiplayer melee army must start on Attack, or
            // it stands still while an enemy walks past it.
            Check("a new melee army defaults to attacking, not holding",
                  battleOurs.armyBehavior == UnitSystem.ArmyBehavior.Attack);

            // Pursue on both sides so neither can win by the other failing to close the gap; this
            // tests that damage happens, not that the approach logic picks a good radius.
            battleOurs.armyBehavior = UnitSystem.ArmyBehavior.Pursue;
            battleEnemy.armyBehavior = UnitSystem.ArmyBehavior.Pursue;

            enemyLifeAtStart = TotalLife(battleEnemy);
            ourLifeAtStart = TotalLife(battleOurs);
            publishedAtStart = CombatSync.PublishedUpdates;

            // The world clock, so the end of the battle can prove the simulation is still running.
            // This is the measurement raids need: a raid does not throw when it goes wrong, it
            // STOPS THE CLOCK, and nothing else here would notice.
            gameClockAtStart = GameClock();

            TryPlanARaid();

            Check("both armies start at full health",
                  enemyLifeAtStart > 0f && ourLifeAtStart > 0f);

            // Reported from a live game: multiplayer soldiers were invisible, only their weapon and
            // selection outline showed. Nothing measured here could see it, since an invisible army
            // fights exactly as well as a visible one. The draw loop skips any unit category whose
            // material is null, so that is the thing to assert.
            Check("our soldiers have a material and will actually be drawn",
                  ArmyIsDrawable(battleOurs));

            Check("the enemy's soldiers have a material too",
                  ArmyIsDrawable(battleEnemy));

            // Unpause and run fast, otherwise the fight resolves at whatever speed the session
            // happened to start at and the frame budget below would be guesswork.
            if (SpeedControlUI.inst != null) SpeedControlUI.inst.SetSpeed(3);

            Log("battle joined: our team " + local + " vs team " + battleEnemy.teamId
                + ", life " + ourLifeAtStart + " vs " + enemyLifeAtStart);

            battleFrames = BattleFrames;
            return true;
        }

        /// <summary>
        /// Reads the battle's outcome. Deliberately asserts that SOMEBODY took damage rather than
        /// naming a winner: which side wins depends on unit rolls, but a fight in which no life is
        /// lost at all means combat is not running, which is the failure worth catching.
        /// </summary>
        private static void FinishBattle()
        {
            try
            {
                float enemyNow = TotalLife(battleEnemy);
                float oursNow = TotalLife(battleOurs);

                Log("battle over: life " + oursNow + " vs " + enemyNow);

                Check("the fight dealt real damage to somebody",
                      enemyNow < enemyLifeAtStart || oursNow < ourLifeAtStart);

                Check("armies remain consistent after fighting", ArmiesAreConsistent());

                // THE RAID CHECK THAT MATTERS. Raids were disabled because one froze the world
                // clock the instant it registered, with no exception anywhere, so the only way to
                // see it is to look at whether game time actually moved while the battle ran.
                float clockNow = GameClock();
                Log("game clock: " + gameClockAtStart + " -> " + clockNow
                    + (Main.RaidsEnabled ? " (raids ON)" : " (raids off)"));

                Check("the simulation is still running after the battle", clockNow != gameClockAtStart);

            // Buildings were the hole in the arbitration model: their damage is suppressed on every
            // machine but the arbiter's, and until 2026-09-06 nothing published the outcome, so a
            // building the arbiter burned down stood at full health and undamageable everywhere
            // else. Damaging one here proves the arbiter both applies AND announces it.
            CheckBuildingDamageIsPublished();

                // The half of the model that only matters across machines. Damage landing locally
                // proves the fight ran; this proves its outcome was told to anybody else. Only
                // meaningful with combat authority switched on, since the sweep is dark otherwise,
                // so with it off the check states that nothing was published, which is equally
                // worth knowing: a dark feature that still chattered would be the bug.
                int published = CombatSync.PublishedUpdates - publishedAtStart;
                Log("combat updates published during the battle: " + published);

                if (Main.CombatAuthorityEnabled)
                    Check("the arbiter published the fight's outcome", published > 0);
                else
                    Check("combat sync stays silent while the feature is off", published == 0);

                // The two Workshop reports: a host's building, and the host's keep, still standing
                // after the session has actually been running.
                CheckHostBuildSurvived();
                CheckLobbyDidNotClobberPlayMode();

                // Releasing is the single choke point every army death funnels through, so it is
                // asserted on an army that has actually been in combat.
                if (battleOurs != null)
                {
                    Guid id = battleOurs.guid;
                    UnitSystem.inst.ReleaseArmy(battleOurs);
                    Check("an army can be released and is gone from the system",
                          UnitSystem.inst.FindArmyByGuid(id) == null);
                }
            }
            catch (Exception ex)
            {
                Check("the battle finished without throwing", false);
                Main.LogEx("[SELFTEST] battle", ex);
            }
        }

        /// <summary>
        /// Every army in the world still has a live general and a units list no longer than its
        /// squad. A fight is the moment stale entries appear, and an army whose units array has
        /// outgrown its squad size means a death path put something back that it should not have.
        /// </summary>
        private static bool ArmiesAreConsistent()
        {
            var armies = UnitSystem.inst.armies;
            for (int i = 0; i < armies.Count; i++)
            {
                UnitSystem.Army a = armies.data[i];
                if (a == null) return false;
                if (a.units == null || a.units.Count > a.squadSize) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether an army's soldiers would survive the draw loop's first test.
        ///
        /// UnitSystem draws each unit category with `Graphics.DrawMeshInstanced(cat.mesh, 0,
        /// cat.mat, cat.matrices)` and skips the whole batch on `if (cat.mat == null) continue;`,
        /// so a null material is exactly the difference between an army you can see and one you
        /// cannot. The category is reached through the same mapping the game uses, via
        /// Main.UnitCategoryTeamFor, so this asks the question the renderer will ask.
        /// </summary>
        private static bool ArmyIsDrawable(UnitSystem.Army army)
        {
            if (army == null) return false;

            try
            {
                int categoryTeam = Main.UnitCategoryTeamFor(army.teamId);

                var field = typeof(UnitSystem).GetField("unitCategoriesGen",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field == null) return false;

                var categories = field.GetValue(UnitSystem.inst) as List<UnitSystem.UnitCategory>;
                if (categories == null) return false;

                for (int i = 0; i < categories.Count; i++)
                {
                    UnitSystem.UnitCategory c = categories[i];
                    if (c == null || c.teamId != categoryTeam || c.type != army.armyType) continue;
                    return c.mat != null;
                }
                return false;   // no category at all is the original bug, and is not drawable either
            }
            catch (Exception ex)
            {
                Main.LogEx("[SELFTEST] drawable check", ex);
                return false;
            }
        }

        /// <summary>
        /// Hits one of our own buildings and checks the damage both lands and gets announced.
        ///
        /// Our own, so we are certainly the arbiter for it: the building stands on our landmass.
        /// A small fixed amount, well under any building's life, so this damages rather than
        /// destroys and the session is left standing for the checks that follow.
        /// </summary>
        private static void CheckBuildingDamageIsPublished()
        {
            // Player.keep is the Keep COMPONENT, and damage lives on the Building it sits on.
            Building target = (Player.inst != null && Player.inst.keep != null)
                ? Player.inst.keep.GetComponent<Building>() : null;

            Check("we have a building to damage", target != null);
            if (target == null) return;

            float before = target.Life;
            int publishedBefore = CombatSync.PublishedUpdates;

            // Through the PROJECTILE path, because that is the one the authority hook guards.
            // Calling Building.TakeDamage directly would damage the building without ever reaching
            // the hook, which is exactly the mistake this check was written with the first time: it
            // reported the damage landing and nothing being published, and both were true.
            //
            // A small amount on purpose. The keep is the one building whose destruction triggers the
            // defeat conversation, so this has to damage it without ever getting close to killing it.
            target.TakeProjectileDamage(1f, DamageType.Standard, DamageSource.Ranged,
                                        target.transform.position, Vector3.zero, EnemyTeamForTest());

            Check("a building on our own ground takes damage", target.Life < before);
            Check("the test did not destroy the keep", target.Life > 0f);

            // The sweep is what sends it, and it only runs on every Nth fixed tick rather than on
            // every call, so a single Tick() almost never reaches it. Drive it until it sweeps.
            // (Getting this wrong is why this check first reported "damage landed, nothing
            // published": both halves were true, and neither was the bug.)
            for (int i = 0; i < 40 && CombatSync.PublishedUpdates == publishedBefore; i++)
                CombatSync.Tick();

            Log("building damage: " + before + " -> " + target.Life
                + ", updates published " + (CombatSync.PublishedUpdates - publishedBefore));

            if (Main.CombatAuthorityEnabled)
                Check("the arbiter announced the building damage",
                      CombatSync.PublishedUpdates > publishedBefore);
        }

        /// <summary>
        /// A team to attribute test damage to, preferring the peer so the hit looks like a real
        /// attack rather than self-harm. Falls back to our own team if there is no peer.
        /// </summary>
        private static int EnemyTeamForTest()
        {
            SessionPlayer peer = FindPeer();
            if (peer != null && peer.inst != null && peer.inst.PlayerLandmassOwner != null)
                return peer.inst.PlayerLandmassOwner.teamId;
            return LocalTeam();
        }

        /// <summary>
        /// The world clock, as a single number that must keep moving.
        ///
        /// Weather.TimeInYear advances with GAME time rather than real time, which is exactly the
        /// distinction that matters: the raid freeze left the process alive and the UI responsive
        /// while the simulation stood still, so any real-time measure would have reported health.
        /// </summary>
        private static float GameClock()
        {
            // seasonTime, not TimeInYear. TimeInYear is summer plus winter length, a CONSTANT
            // describing how long a year is, and the first version of this check used it and
            // reported a frozen clock on a perfectly healthy game. seasonTime is the one that
            // actually accumulates.
            try { return Weather.inst != null ? Weather.inst.seasonTime : -1f; }
            catch { return -1f; }
        }

        /// <summary>
        /// Asks the game to plan a raid, when raids are switched on for this run.
        ///
        /// Planning is the half that can be read statically and was: the objective loop and the
        /// target scan both terminate. So the interesting question is not whether this RETURNS, it
        /// is whether the world keeps ticking afterwards, which the clock check at the end of the
        /// battle answers. Raiders reach the other machines through the army spawn broadcast that
        /// already exists, so nothing extra is needed to make them visible.
        ///
        /// Silent when raids are off, which is how the mod ships, so this costs a normal run nothing.
        /// </summary>
        private static void TryPlanARaid()
        {
            if (!Main.RaidsEnabled) return;
            if (RaiderSystem.inst == null) { Check("the raider system exists", false); return; }

            int armiesBefore = UnitSystem.inst.armies.Count;

            try
            {
                RaiderSystem.inst.SetupRaid();
            }
            catch (Exception ex)
            {
                Main.LogEx("[SELFTEST] planning a raid", ex);
                Check("planning a raid does not throw", false);
                return;
            }

            Check("planning a raid does not throw", true);
            Log("raid planned; armies went from " + armiesBefore
                + " to " + UnitSystem.inst.armies.Count);

            // Planning is the safe half, and reading it said as much. The freeze was reported at the
            // moment a raid REGISTERS, so a boat is forced in here rather than waiting out the
            // timer the planner would otherwise use: without this the run ends before anything
            // lands and reports a clean bill of health for a raid that never happened.
            Vector3 sea;
            if (!FindUnownedWater(out sea))
            {
                Log("no open water found, skipping the landing");
                return;
            }

            try
            {
                // Private, so reflected. The mod patches it by explicit signature, which works on
                // private methods; calling one does not.
                var spawn = typeof(RaiderSystem).GetMethod(
                    "SpawnVikingBoat",
                    System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Public,
                    null, new Type[] { typeof(Vector3) }, null);

                if (spawn == null)
                {
                    Check("the viking boat spawner can be found", false);
                    return;
                }

                spawn.Invoke(RaiderSystem.inst, new object[] { sea });
            }
            catch (Exception ex)
            {
                Main.LogEx("[SELFTEST] spawning a viking boat", ex);
                Check("a raid can put a boat in the water", false);
                return;
            }

            Check("a raid can put a boat in the water", true);
            Log("viking boat spawned at " + sea + "; the clock check at the end of the battle is "
                + "what says whether the raid stalls the simulation");
        }

        /// <summary>The general plus every surviving unit, which is what a fight actually drains.</summary>
        private static float TotalLife(UnitSystem.Army army)
        {
            if (army == null) return 0f;

            float total = army.generalLife;
            if (army.units != null)
                for (int i = 0; i < army.units.Count; i++)
                {
                    UnitSystem.Unit u = army.units.data[i];
                    if (u != null) total += u.life;
                }
            return total;
        }

        // ---- helpers ------------------------------------------------------------------------

        private static int LocalTeam()
        {
            return (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
        }

        private static SessionPlayer FindPeer()
        {
            SessionPlayer found;
            return Main.kCPlayers.TryGetValue(FakePeer.SyntheticSteamId, out found) ? found : null;
        }

        private static UnitSystem.Army NewestArmy()
        {
            var armies = UnitSystem.inst.armies;
            return armies.Count == 0 ? null : armies.data[armies.Count - 1];
        }

        private static Dock FindDockOfTeam(int teamId)
        {
            foreach (Building b in UnityEngine.Object.FindObjectsOfType<Building>())
            {
                if (b == null) continue;

                Dock d = b.GetComponent<Dock>();
                if (d == null) continue;

                try { if (b.TeamID() == teamId) return d; }
                catch { /* unowned landmass, not this one */ }
            }
            return null;
        }
    }
}
