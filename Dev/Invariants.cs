using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

using KaCMultiplayer.Net;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Dev
{
    /// <summary>
    /// Checks for the BUG CLASSES this project keeps producing, rather than for individual bugs.
    ///
    /// WHY THIS IS A SEPARATE FILE FROM AutoTest. Everything in AutoTest asks "does this feature
    /// work": can a ship be built, does a catapult survive a save, does chat send. Every check
    /// here asks a different kind of question, "is this whole SHAPE of mistake absent from the
    /// world right now", and it asks it of every kingdom and every landmass rather than of one
    /// fixture. That distinction is worth keeping visible, because the two rot in opposite ways: a
    /// feature check rots when the feature changes, and a class check rots when we stop believing
    /// the class is real.
    ///
    /// WHERE THE LIST CAME FROM. A community contributor's patch (Workshop item 3802324628) fixed
    /// nine bugs in our 0.10.1, and what mattered more than the fixes was that six of them were
    /// the SAME FOUR MISTAKES, made in places nobody had looked:
    ///
    ///   1. A per-landmass array sized before this machine had the map, and never grown. Found in
    ///      the job tables and in the building registries. Nothing had checked the other nine
    ///      structures Player sizes the same way, in the same method, from the same number.
    ///      See <see cref="PerLandmassDataCoversTheWorld"/>.
    ///
    ///   2. Vanilla reading Player.inst from a type neither of our transpilers covers. Found in
    ///      LandmassOwner.CalcMaxGold and in JobSystem.Update. The singleton rewrite covers
    ///      Player's own instance methods and the owner rewrite covers Building's, so anything on
    ///      a third type is unguarded and silently answers for the local kingdom. The offline half
    ///      of this class is docs/singleton_audit.ps1, which enumerates the ones still unclaimed;
    ///      the runtime half is <see cref="EveryKingdomCountsItsOwnTreasury"/> and
    ///      <see cref="IslandsAreStaffedByTheirOwner"/>.
    ///
    ///   3. A throw that only ever reaches Player.log, inside a multicast delegate. One subscriber
    ///      throwing stops every later subscriber, and Weather.OnSeasonChange has AutoSave early
    ///      in its list and every farm in the game late in it. That is how a destroyed cave
    ///      container stopped the entire seasonal economy for weeks while presenting itself as
    ///      "there was a problem saving the level". See
    ///      <see cref="SeasonChangeReachesItsLastSubscriber"/>, which is the most valuable check in
    ///      this file: it fails for ANY future early subscriber that throws, not only that one.
    ///
    ///   4. State written by reflection instead of through the method that owns it, so the object
    ///      ends up marked done without being commissioned. Found in ApplyBuildSnapshot writing
    ///      Building.built. See <see cref="SnapshotCommissionsABuilding"/>.
    ///
    /// HOW THESE ARE WRITTEN, deliberately. Not one of them names a method that patch added. They
    /// assert the OBSERVABLE state those fixes produce, so they pass whether we adopt his
    /// implementation, write our own, or fix the cause somewhere else entirely. They therefore
    /// FAIL against our current source, which is the point: they are the acceptance criteria for
    /// the merge, not a record of it.
    /// </summary>
    public static class Invariants
    {
        private static Action<string, bool> check;
        private static Action<string> log;

        /// <summary>
        /// Runs every class check against the session that is already up.
        ///
        /// Reporting is borrowed rather than reimplemented: AutoTest owns the pass count, the
        /// failure list and the [SELFTEST] prefix, and a second copy of that bookkeeping would
        /// drift and start summarising a different run than the one that happened.
        /// </summary>
        public static void RunAll(Action<string, bool> checkReporter, Action<string> logger)
        {
            check = checkReporter;
            log = logger;

            PerLandmassDataCoversTheWorld();
            EveryBuildingIsFindableOnItsLandmass();
            TeamIdsAreUniqueAndResolvable();
            EveryKingdomRunsOnTheSameClock();
            SeasonChangeReachesItsLastSubscriber();
            SavePackLeavesNoFieldNull();
            EveryKingdomCountsItsOwnTreasury();
            IslandsAreStaffedByTheirOwner();
            SnapshotCommissionsABuilding();
            DragonIdsAreUnique();
        }

        // ---- CLASS 1: A PER-LANDMASS ARRAY SIZED BEFORE THE MAP EXISTED -------------------

        /// <summary>
        /// Every per-landmass structure on Player, named once, so the whole class is checked
        /// instead of the two instances somebody happened to find.
        ///
        /// All of these are allocated together in <c>Player.ResetPerLandMassData</c> from
        /// <c>World.NumLandMasses</c>. In a session a kingdom object can be built during the
        /// handshake, which on a joining machine is BEFORE the map is generated, so every one of
        /// them gets cut to the menu world's landmass count and nothing in the game ever grows
        /// them again. Two were found short in the wild and cost a peer's farms (unstaffed
        /// forever, because a table too short to cover an island makes the job lookup fall back to
        /// the local player) and a host's keep (owned but in no registry, so invisible to every
        /// lookup that matters). The other nine were never checked.
        ///
        /// Strings rather than typed accessors on purpose: four of these fields are private, their
        /// element types are a mix of jagged arrays, ArrayExt and List, and the only property any
        /// of them share is a row count. A twelfth structure is one line here.
        /// </summary>
        private static readonly string[] PerLandmassFields =
        {
            "JobPriorityOrder",
            "JobEnabledFlag",
            "JobCustomMaxEnabledFlag",
            "JobFilledAvailable",
            "ResidentialsPerLandmass",
            "landMassBuildingRegistry",
            "unbuiltBuildingsPerLandmass",
            "landMassHappiness",
            "landMassHealth",
            "landMassIntegrity",
            "CanUseTools",
        };

        private static void PerLandmassDataCoversTheWorld()
        {
            try
            {
                int need = (World.inst != null) ? World.inst.NumLandMasses : -1;
                if (need <= 0) { check("the world reports a landmass count", false); return; }

                List<string> tooShort = new List<string>();

                foreach (SessionPlayer kp in Main.kCPlayers.Values)
                {
                    if (kp == null || kp.inst == null) continue;

                    for (int i = 0; i < PerLandmassFields.Length; i++)
                    {
                        string name = PerLandmassFields[i];
                        int rows = RowCount(ReadField(kp.inst, name));

                        // Absent is a different fault from short and worth distinguishing: a
                        // kingdom that was never Reset at all has nothing to grow, and would be
                        // misdiagnosed as a sizing problem.
                        if (rows < 0) { tooShort.Add(kp.name + "." + name + "=absent"); continue; }
                        if (rows < need) tooShort.Add(kp.name + "." + name + "=" + rows);
                    }
                }

                // Named in the log, not merely counted. "three arrays are short" sends you
                // reading; "Polyton.landMassHealth=0" sends you to the line.
                if (tooShort.Count > 0)
                    log("per-landmass data short of the world's " + need + " landmass(es): "
                        + string.Join(", ", tooShort.ToArray()));

                check("every kingdom's per-landmass data covers the whole world", tooShort.Count == 0);
            }
            catch (Exception ex)
            {
                check("the per-landmass data check finished without throwing", false);
                Main.LogEx("[SELFTEST] per-landmass coverage", ex);
            }
        }

        /// <summary>
        /// Every building a kingdom owns can also be FOUND through the per-landmass registry that
        /// the rest of the game looks it up in.
        ///
        /// This is the direct consequence of a short registry, and the strongest candidate for the
        /// Workshop report "if I place a castle as the host, it disappears". A building whose
        /// landmass index is past the end of the array gets added to <c>Player.Buildings</c> and to
        /// no per-landmass registry at all, so it exists, is owned, and is invisible to
        /// <c>GetBuildingListForLandMass</c>, which is what the build menu, the treasury and the
        /// job system all ask.
        ///
        /// The keep is called out separately because that is the one a player notices in seconds.
        /// </summary>
        private static void EveryBuildingIsFindableOnItsLandmass()
        {
            try
            {
                int orphans = 0;
                int keepsLost = 0;
                List<string> sample = new List<string>();

                foreach (SessionPlayer kp in Main.kCPlayers.Values)
                {
                    Player p = (kp != null) ? kp.inst : null;
                    if (p == null || p.Buildings == null) continue;

                    Guid keepGuid = Guid.Empty;
                    if (p.keep != null)
                    {
                        Building kb = p.keep.GetComponent<Building>();
                        if (kb != null) keepGuid = kb.guid;
                    }

                    // Bounded by Count, never data.Length: the backing array is longer and its
                    // unfilled capacity is null. This is the recurring loop bug of this codebase.
                    for (int i = 0; i < p.Buildings.Count; i++)
                    {
                        Building b = p.Buildings.data[i];
                        if (b == null) continue;

                        int lm = b.LandMass();
                        if (lm < 0) continue;   // not on a landmass yet, nothing to be missing from

                        if (FoundOnLandmass(p, b, lm)) continue;

                        orphans++;
                        if (b.guid == keepGuid) keepsLost++;
                        if (sample.Count < 5)
                            sample.Add(kp.name + "'s " + b.UniqueName + " on landmass " + lm);
                    }
                }

                if (orphans > 0)
                    log("buildings owned but in no per-landmass registry: " + orphans
                        + " (" + string.Join(", ", sample.ToArray()) + ")");

                check("every owned building is findable on its own landmass", orphans == 0);
                check("no kingdom's keep has fallen out of its landmass registry", keepsLost == 0);
            }
            catch (Exception ex)
            {
                check("the building registry check finished without throwing", false);
                Main.LogEx("[SELFTEST] building registry coverage", ex);
            }
        }

        /// <summary>Asks the registry the way the game does, by landmass and name hash.</summary>
        private static bool FoundOnLandmass(Player p, Building b, int landMass)
        {
            ArrayExt<Building> list = p.GetBuildingListForLandMass(landMass, b.uniqueNameHash);
            if (list == null) return false;

            for (int i = 0; i < list.Count; i++)
                if (list.data[i] == b) return true;

            return false;
        }

        // ---- CLASS 3: A THROW INSIDE A MULTICAST DELEGATE ---------------------------------

        /// <summary>
        /// The season change reaches the LAST thing subscribed to it.
        ///
        /// This is the check that would have caught the worst bug in the project's history inside
        /// one run, and it is worth understanding why it is shaped the way it is.
        ///
        /// <c>Weather.OnSeasonChange</c> is a .NET multicast delegate, and a multicast delegate
        /// stops dispatching at the first subscriber that throws. <c>AutoSave.OnOnSeasonChange</c>
        /// subscribes early; every farm's <c>YieldProducerSeason.Inst_OnSeasonChange</c>
        /// subscribes late. So when a destroyed cave container made <c>WorldSaveData.Pack</c>
        /// throw, no farm in the game ever received a season change again: crops grew taller every
        /// year and nothing was ever harvested, on farms that were built, Open and fully staffed.
        /// The only trace was one line in Player.log, which we were not reading, and a dialog that
        /// said the level could not be saved.
        ///
        /// So this does NOT check the cave container. It subscribes its own probe, which is
        /// therefore last in the invocation list, fires the real delegate, and asks whether the
        /// probe ran. Any future early subscriber that throws for any reason fails this check, and
        /// the exception is caught here with its full inner chain, which names the culprit
        /// outright. That is the whole difference between fixing a bug and closing a class of them.
        ///
        /// Fired through the delegate rather than by invoking subscribers one at a time: the bug IS
        /// the dispatch, so a loop that called each handler in its own try block would pass
        /// happily on exactly the world that is broken.
        ///
        /// The probe is removed in a finally. Leaving a test handler subscribed to a game event for
        /// the rest of the session would be this very bug, introduced by its own check.
        /// </summary>
        private static void SeasonChangeReachesItsLastSubscriber()
        {
            if (Weather.inst == null)
            {
                check("the weather system exists to fire a season change", false);
                return;
            }

            bool probeRan = false;
            EventHandler<Weather.SeasonChangeArgs> probe = delegate { probeRan = true; };

            int subscribers = -1;
            try
            {
                Weather.inst.OnSeasonChange += probe;

                Delegate chain = PrivateField.Get<Delegate>(Weather.inst, "OnSeasonChange");
                subscribers = (chain != null) ? chain.GetInvocationList().Length : 0;

                Weather.SeasonChangeArgs args = new Weather.SeasonChangeArgs();
                args.season = Weather.inst.season;
                args.year = (Player.inst != null) ? Player.inst.CurrYear : 0;

                if (chain != null) chain.DynamicInvoke(new object[] { Weather.inst, args });
            }
            catch (Exception ex)
            {
                // The one line that would have found it. LogEx walks the inner chain, which
                // matters here because DynamicInvoke wraps everything in a
                // TargetInvocationException whose own message is the useless "exception has been
                // thrown by the target of an invocation".
                Main.LogEx("[SELFTEST] a season-change subscriber threw, which stops every LATER "
                           + "subscriber including every farm in the game. THIS STACK IS THE CULPRIT", ex);
            }
            finally
            {
                try { Weather.inst.OnSeasonChange -= probe; }
                catch (Exception ex) { Main.LogEx("[SELFTEST] removing the season probe", ex); }
            }

            log("season change dispatched to " + subscribers + " subscriber(s)");
            check("a season change reaches the last thing subscribed to it", probeRan);

            // The two pieces of global world state whose loss produced that silent throw. Checked
            // AFTER the dispatch rather than before, because a subscriber destroying them is
            // precisely the failure, and checking first would look straight past it.
            check("the world still has its cave container",
                  World.inst != null && World.inst.caveContainer != null);

            try
            {
                new World.WorldSaveData().Pack(World.inst);
                check("the world can still be packed for a save", true);
            }
            catch (Exception ex)
            {
                check("the world can still be packed for a save", false);
                Main.LogEx("[SELFTEST] packing the world after a season change", ex);
            }
        }

        // ---- CLASS 4: STATE WRITTEN BY REFLECTION INSTEAD OF BY ITS OWNER -----------------

        /// <summary>
        /// A building that a peer's snapshot says is finished ends up COMMISSIONED, not merely
        /// marked finished.
        ///
        /// <c>CompleteBuild</c> is the only thing that sends OnBuilt, registers the building's
        /// resource providers with FreeResourceManager, calls <c>Player.BuildingNowBuilt</c> (which
        /// takes it off the landmass's unbuilt list and recalculates max storage), creates its
        /// worker jobs and bakes its pathing. Writing <c>built = true</c> by reflection out of a
        /// snapshot does none of that, and then makes it unrecoverable: our own idempotency guard
        /// sees <c>IsBuilt()</c> already true and suppresses the local simulation's real completion
        /// a moment later as a duplicate. On a farm that is a field with no HarvesterJob that
        /// nobody can ever harvest.
        ///
        /// Three assertions, because only the first is obvious and the other two are what the bug
        /// actually cost:
        ///   built                   the snapshot was applied at all
        ///   off the unbuilt list    BuildingNowBuilt ran, so the kingdom knows it is finished
        ///   no skipped recompletes  the real completion was not suppressed as a duplicate
        ///
        /// The third is what makes this a class check rather than a regression test.
        /// <c>SkippedRecompletes</c> was added to diagnose this and then never asserted on; a
        /// second source of the same mistake anywhere in the mod moves it.
        /// </summary>
        private static void SnapshotCommissionsABuilding()
        {
            Building b = null;
            try
            {
                if (Player.inst == null || Player.inst.keep == null || World.inst == null)
                {
                    log("no keep to place a snapshot fixture beside, skipping");
                    return;
                }

                Building keep = Player.inst.keep.GetComponent<Building>();
                if (keep == null) { log("the keep has no Building component, skipping"); return; }

                Cell site = World.inst.GetCellDataClamped(keep.transform.position + new Vector3(4f, 0f, 0f));
                if (site == null) { log("no site for the snapshot fixture, skipping"); return; }

                int skippedBefore = Main.BuildingCompleteBuildHook.SkippedRecompletes;

                // A farm on purpose. It is the building where an uncommissioned completion is
                // worst (no HarvesterJob, so it is worked and never harvested) and it is the one
                // the original report was about.
                //
                // Placed UNDER CONSTRUCTION and privately, which is the state a peer's snapshot
                // arrives into. Scope() keeps the fixture off the wire; this check is about what a
                // receiver does with a snapshot, not about sending one.
                using (NetApply.Scope())
                {
                    b = UnityEngine.Object.Instantiate<Building>(
                        GameState.inst.GetPlaceableByUniqueName("farm"));
                    b.Init();
                    b.transform.position = site.Position;
                    b.SendMessage("OnPlayerPlacement", SendMessageOptions.DontRequireReceiver);
                    World.inst.Place(b);
                }

                // A farm needs ground a farm will accept, and Place does not promise to take it.
                // Checked the same way the host build fixture checks its own placement, so a site
                // the game refused is reported as an absent fixture rather than as a failed
                // invariant.
                if (Main.FindBuildingByGuidAnywhere(b.guid) == null)
                {
                    log("the snapshot fixture did not take at " + site.Position + ", skipping");
                    return;
                }

                if (b.IsBuilt())
                {
                    // Nothing to prove: the fixture finished on placement, so the snapshot would
                    // not be what completed it. Said out loud rather than passing vacuously.
                    log("the snapshot fixture was already built on placement, skipping");
                    return;
                }

                // Captured from the building's own save data rather than field by field. A
                // hand-written state would default Life to 0 and wreck the building the moment it
                // was applied, and it would drift from BuildSnapshotMessage the first time a field
                // is added to either.
                BuildingState state = BuildingState.From(new Building.BuildingSaveData().Pack(b));
                state.Built = true;

                BuildSnapshotMessage m = new BuildSnapshotMessage();
                m.Origin = 0;
                m.State = state;
                m.ResourceProgress = 0f;

                using (NetApply.Scope())
                    NetRegistrations.ApplyBuildSnapshot(m);

                check("a snapshot marked built does finish the building", b.IsBuilt());
                check("a building finished by a snapshot leaves the kingdom's unbuilt list",
                      !IsOnTheUnbuiltList(Player.inst, b));
                check("finishing a building by snapshot does not suppress its real completion",
                      Main.BuildingCompleteBuildHook.SkippedRecompletes == skippedBefore);
            }
            catch (Exception ex)
            {
                check("the snapshot commissioning check finished without throwing", false);
                Main.LogEx("[SELFTEST] snapshot commissioning", ex);
            }
            finally
            {
                // The fixture is a real building in a real town. Left standing it would be counted
                // by every later check, including the save round trip.
                try
                {
                    if (b != null)
                        using (NetApply.Scope()) World.inst.DemolishBuilding(b, false);
                }
                catch (Exception ex) { Main.LogEx("[SELFTEST] clearing the snapshot fixture", ex); }
            }
        }

        /// <summary>
        /// Whether a kingdom still holds this building on one of its per-landmass unbuilt lists.
        ///
        /// Private on Player, hence the reflection, and worth reaching for: it is the one piece of
        /// state that says whether <c>BuildingNowBuilt</c> ran, and a building that is built while
        /// still on the unbuilt list is the exact signature of a completion that never happened.
        /// </summary>
        private static bool IsOnTheUnbuiltList(Player p, Building b)
        {
            ArrayExt<ArrayExt<Building>> perLandmass =
                PrivateField.Get<ArrayExt<ArrayExt<Building>>>(p, "unbuiltBuildingsPerLandmass");
            if (perLandmass == null) return false;

            for (int lm = 0; lm < perLandmass.Count; lm++)
            {
                ArrayExt<Building> row = perLandmass.data[lm];
                if (row == null) continue;

                for (int i = 0; i < row.Count; i++)
                    if (row.data[i] == b) return true;
            }

            return false;
        }

        // ---- CLASS 2: VANILLA READING Player.inst FROM AN UNGUARDED TYPE ------------------

        /// <summary>
        /// Every kingdom's treasury is computed from ITS OWN throne rooms.
        ///
        /// <c>LandmassOwner.CalcMaxGold</c> belongs to a LandmassOwner and then asks
        /// <c>Player.inst</c> what that owner has built. Neither transpiler reaches it: the
        /// singleton rewrite covers Player's own instance methods and the owner rewrite covers
        /// Building's, and CalcMaxGold is on a third type. So computing a peer's capacity asked the
        /// LOCAL player for throne rooms on the PEER's islands, found none, and returned zero.
        ///
        /// Zero capacity is not cosmetic. Gold capacity comes only from throne rooms, so the
        /// kingdom cannot hold gold at all and <c>Gold</c> sits at 0 forever, which is what reached
        /// a player as a merchant bug: <c>ResourceLineItemUI.ClampOrder</c> recomputes
        /// <c>Gold / price</c> on every keystroke and writes the result back into the box, so every
        /// number typed into a merchant order snapped straight back to 0.
        ///
        /// Asked of EVERY kingdom that owns a throne room rather than of the peer specifically, so
        /// the same check covers three players as readily as two.
        /// </summary>
        private static void EveryKingdomCountsItsOwnTreasury()
        {
            try
            {
                int asked = 0;
                List<string> starved = new List<string>();

                foreach (SessionPlayer kp in Main.kCPlayers.Values)
                {
                    Player p = (kp != null) ? kp.inst : null;
                    if (p == null || p.PlayerLandmassOwner == null) continue;

                    int thrones = CountThroneRooms(p);
                    if (thrones == 0) continue;   // legitimately no capacity, nothing to assert

                    asked++;
                    p.PlayerLandmassOwner.CalcMaxGold();

                    if (p.PlayerLandmassOwner.MaxGoldStorage <= 0)
                        starved.Add(kp.name + " holds " + thrones + " throne room(s) and has no capacity");
                }

                if (starved.Count > 0)
                    log("treasuries computed from the wrong kingdom: " + string.Join(", ", starved.ToArray()));

                if (asked == 0)
                {
                    // Worth saying rather than reporting a silent pass. No throne room anywhere
                    // means this ran against a world that cannot exhibit the bug, and a PASS line
                    // would be a lie of omission.
                    log("no kingdom owns a throne room, so the treasury check proved nothing;"
                        + " build one on each side before trusting this line");
                    return;
                }

                check("every kingdom with a throne room can hold gold", starved.Count == 0);
            }
            catch (Exception ex)
            {
                check("the treasury check finished without throwing", false);
                Main.LogEx("[SELFTEST] treasury ownership", ex);
            }
        }

        private static int CountThroneRooms(Player p)
        {
            if (p.Buildings == null) return 0;

            int n = 0;
            for (int i = 0; i < p.Buildings.Count; i++)
            {
                Building b = p.Buildings.data[i];
                if (b == null) continue;

                if (b.uniqueNameHash == World.throneRoomHash
                    || b.uniqueNameHash == World.largeThroneRoomHash) n++;
            }
            return n;
        }

        /// <summary>
        /// An island is staffed by the rules of the kingdom that OWNS it.
        ///
        /// <c>JobSystem.Update</c> is the game's job-assignment engine and it reads the
        /// <c>Player.inst</c> singleton sixteen times, walking every landmass in the world and
        /// asking the local kingdom for each one's job priority order and enabled flags. Neither
        /// transpiler covers JobSystem either. So every island in the world was staffed according
        /// to the local player's decrees, other players' islands included: their farms, their
        /// barracks and their quarries hired and fired by somebody else's settings, independently
        /// on every machine.
        ///
        /// Tested through the accessors the engine actually calls, with the two kingdoms set to
        /// DISAGREE. Asking whether the peer's own table is intact would pass on the broken build,
        /// because the table was always fine; it was simply never consulted.
        ///
        /// Restored in a finally, since this deliberately turns a job category off mid-session.
        /// </summary>
        private static void IslandsAreStaffedByTheirOwner()
        {
            SessionPlayer peer = FindAnyPeer();
            if (peer == null || peer.inst == null || peer.inst.PlayerLandmassOwner == null)
            {
                log("no peer kingdom in this session, skipping the island staffing check");
                return;
            }

            Player owner = peer.inst;
            int landMass = FirstOwnedLandmass(owner.PlayerLandmassOwner);
            if (landMass < 0)
            {
                log("the peer owns no landmass, skipping the island staffing check");
                return;
            }

            // The short-table fault this check sits beside would make the rows unreachable, and a
            // NullReference here would be reported as "the check threw" rather than as the two
            // separate faults they are.
            if (!HasJobRow(owner, landMass) || !HasJobRow(Player.inst, landMass))
            {
                check("both kingdoms have a job row for landmass " + landMass, false);
                return;
            }

            bool peerHad = owner.JobEnabledFlag[landMass][0];
            bool localHad = Player.inst.JobEnabledFlag[landMass][0];
            try
            {
                // Made to disagree, so the answer can only have come from one of them.
                owner.JobEnabledFlag[landMass][0] = false;
                Player.inst.JobEnabledFlag[landMass][0] = true;

                bool[] answered = Player.inst.GetJobEnabledFlags(landMass);

                check("a peer's island is staffed by the peer's job settings, not ours",
                      answered != null && answered.Length > 0 && answered[0] == false);
            }
            catch (Exception ex)
            {
                check("the island staffing check finished without throwing", false);
                Main.LogEx("[SELFTEST] island staffing", ex);
            }
            finally
            {
                try
                {
                    owner.JobEnabledFlag[landMass][0] = peerHad;
                    Player.inst.JobEnabledFlag[landMass][0] = localHad;
                }
                catch (Exception ex) { Main.LogEx("[SELFTEST] restoring job flags", ex); }
            }
        }

        private static bool HasJobRow(Player p, int landMass)
        {
            return p != null
                && p.JobEnabledFlag != null
                && landMass < p.JobEnabledFlag.Length
                && p.JobEnabledFlag[landMass] != null
                && p.JobEnabledFlag[landMass].Length > 0;
        }

        private static int FirstOwnedLandmass(LandmassOwner owner)
        {
            if (owner == null || owner.ownedLandMasses == null || owner.ownedLandMasses.Count == 0)
                return -1;

            return owner.ownedLandMasses.data[0];
        }

        // ---- IDENTITY AND CLOCK ----------------------------------------------------------

        /// <summary>
        /// No two kingdoms share a team id, and every team id resolves back to its kingdom.
        ///
        /// A collision is invisible until it is catastrophic. Team id is what every relation, every
        /// combat arbitration and every ownership test is keyed on, so two kingdoms on one id means
        /// two players who cannot be at war, cannot own separate ground, and take each other's
        /// buildings. The way it happens is a handshake deriving an id from the fresh-game formula
        /// (clientId + 4) while a save carries a different one, and with exactly two players the
        /// formula lands on 6 and happens to agree, which is why it survived every two-player test
        /// this project ever ran.
        ///
        /// The reverse lookup is checked too, because a kingdom whose id resolves to nothing is the
        /// orphan-phantom case: the world holds their town while the game offers them a fresh
        /// castle to found beside it.
        /// </summary>
        private static void TeamIdsAreUniqueAndResolvable()
        {
            try
            {
                Dictionary<int, string> seen = new Dictionary<int, string>();
                List<string> clashes = new List<string>();
                List<string> unresolvable = new List<string>();

                foreach (SessionPlayer kp in Main.kCPlayers.Values)
                {
                    if (kp == null || kp.inst == null || kp.inst.PlayerLandmassOwner == null) continue;

                    int team = kp.inst.PlayerLandmassOwner.teamId;

                    string other;
                    if (seen.TryGetValue(team, out other))
                        clashes.Add("team " + team + " claimed by both " + other + " and " + kp.name);
                    else
                        seen[team] = kp.name;

                    if (World.GetLandmassOwnerByTeamId(team) == null)
                        unresolvable.Add(kp.name + " is team " + team + ", which resolves to nothing");
                }

                if (clashes.Count > 0) log("team id collisions: " + string.Join(", ", clashes.ToArray()));
                if (unresolvable.Count > 0) log("unresolvable teams: " + string.Join(", ", unresolvable.ToArray()));

                check("no two kingdoms share a team id", clashes.Count == 0);
                check("every kingdom's team id resolves back to a landmass owner", unresolvable.Count == 0);
            }
            catch (Exception ex)
            {
                check("the team id check finished without throwing", false);
                Main.LogEx("[SELFTEST] team ids", ex);
            }
        }

        /// <summary>
        /// Every kingdom runs on the same clock as the local one.
        ///
        /// <c>Player.timeScale</c> is per-kingdom, and a remote Player gets 1 straight from its
        /// constructor with nothing ever syncing it. <c>Weather.Update</c> reads the LOCAL player's,
        /// so a menu pause (which sets it to 0) stopped the calendar while a remote kingdom's
        /// Update went on ticking the whole tickable world with a real delta: crops kept growing
        /// toward a winter that never came.
        ///
        /// One comparison rather than a mechanism check, deliberately. Whatever the mechanism turns
        /// out to be, kingdoms disagreeing about how fast time passes is the observable fault, and
        /// this is true or false in one line.
        /// </summary>
        private static void EveryKingdomRunsOnTheSameClock()
        {
            try
            {
                if (Player.inst == null)
                {
                    check("there is a local kingdom to compare clocks against", false);
                    return;
                }

                float local = Player.inst.timeScale;
                List<string> drifting = new List<string>();

                foreach (SessionPlayer kp in Main.kCPlayers.Values)
                {
                    if (kp == null || kp.inst == null || kp.inst == Player.inst) continue;

                    if (Mathf.Abs(kp.inst.timeScale - local) > 0.001f)
                        drifting.Add(kp.name + " at " + kp.inst.timeScale);
                }

                if (drifting.Count > 0)
                    log("kingdoms on a different clock from the local " + local + ": "
                        + string.Join(", ", drifting.ToArray()));

                check("every kingdom runs at the local kingdom's time scale", drifting.Count == 0);
            }
            catch (Exception ex)
            {
                check("the clock check finished without throwing", false);
                Main.LogEx("[SELFTEST] kingdom clocks", ex);
            }
        }

        // ---- THE SAVE PACKER, CHECKED AGAINST THE TYPE IT FILLS --------------------------

        /// <summary>
        /// Fields our packer is allowed to leave empty, each with the reason. If this check fails
        /// on a field you believe is meant to be null, add it here WITH that reason; do not widen
        /// the rule, which is the only thing keeping the check honest.
        /// </summary>
        private static readonly HashSet<string> DeliberatelyEmpty = new HashSet<string>
        {
            // Cleared on purpose: a session does not carry research between saves, and restoring
            // it would hand a loaded kingdom upgrades it never bought.
            "upgrades",
        };

        /// <summary>
        /// Our replacement save packer leaves no reference field of PlayerSaveData null.
        ///
        /// A dropped field is the quietest bug this mod can produce. <c>landMassHappiness</c>,
        /// <c>landMassHealth</c> and <c>landMassIntegrity</c> are built together, one entry per
        /// landmass, and vanilla's Unpack reads all three back. Our packer wrote two of them.
        /// Nothing crashed, because Unpack guards the null and substitutes an EMPTY list, and empty
        /// is the problem: the list is indexed by landmass everywhere else, so it is meant to come
        /// back with one entry per landmass and instead comes back with none, and every read of it
        /// after a load is an ArgumentOutOfRange waiting to happen. Into Player.log, naturally.
        ///
        /// Reflection over the type rather than a list of fields we remember to maintain, so a
        /// field the GAME adds in an update is covered on the first run after it appears. That is
        /// the difference between this and a golden file.
        /// </summary>
        private static void SavePackLeavesNoFieldNull()
        {
            try
            {
                if (Player.inst == null) { check("there is a kingdom to pack", false); return; }

                Player.PlayerSaveData packed = new Player.PlayerSaveData().Pack(Player.inst);
                if (packed == null) { check("the local kingdom packs into save data", false); return; }

                List<string> nulls = new List<string>();
                FieldInfo[] fields = typeof(Player.PlayerSaveData)
                    .GetFields(BindingFlags.Instance | BindingFlags.Public);

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo f = fields[i];
                    if (f.FieldType.IsValueType) continue;            // 0 and false are legitimate
                    if (DeliberatelyEmpty.Contains(f.Name)) continue;

                    if (f.GetValue(packed) == null) nulls.Add(f.Name);
                }

                if (nulls.Count > 0)
                    log("save fields our packer leaves null: " + string.Join(", ", nulls.ToArray())
                        + " (each comes back as an EMPTY collection on load, and anything that"
                        + " indexes it by landmass then throws into Player.log)");

                check("our save packer fills every reference field the game declares", nulls.Count == 0);
            }
            catch (Exception ex)
            {
                check("the save packer completeness check finished without throwing", false);
                Main.LogEx("[SELFTEST] save pack completeness", ex);
            }
        }

        // ---- DRAGONS --------------------------------------------------------------------

        /// <summary>
        /// Every dragon in the world has a real id, and no id belongs to two dragons.
        ///
        /// Dragons are host-authoritative and are talked about across the wire purely by id, so an
        /// empty or duplicated one means a message about one dragon lands on another or on none.
        /// It is also what a missed spawn route looks like from the outside:
        /// <c>DragonSpawn.OnSeasonChange</c> has four spawn branches and one of them
        /// (<c>SpawnBabyDragonToVisit</c>) calls <c>Spawn</c> directly with no gate, so a client
        /// could spawn a dragon of its own accord that existed on exactly one machine.
        /// </summary>
        private static void DragonIdsAreUnique()
        {
            try
            {
                if (DragonSpawn.inst == null || DragonSpawn.inst.currentDragons == null)
                {
                    log("no dragon system in this world, skipping");
                    return;
                }

                var all = DragonSpawn.inst.currentDragons;
                if (all.Count == 0) { log("no dragons airborne, nothing to check"); return; }

                HashSet<Guid> ids = new HashSet<Guid>();
                int empty = 0, duplicate = 0;

                for (int i = 0; i < all.Count; i++)
                {
                    Dragon d = all.data[i];
                    if (d == null) continue;

                    if (d.id == Guid.Empty) { empty++; continue; }
                    if (!ids.Add(d.id)) duplicate++;
                }

                check("every dragon has an id", empty == 0);
                check("no two dragons share an id", duplicate == 0);
            }
            catch (Exception ex)
            {
                check("the dragon id check finished without throwing", false);
                Main.LogEx("[SELFTEST] dragon ids", ex);
            }
        }

        // ---- SHARED HELPERS -------------------------------------------------------------

        /// <summary>
        /// Any kingdom that is not the local one.
        ///
        /// Not AutoTest's FindPeer, which looks up the FakePeer id specifically. These checks are
        /// about the session as it actually is, so they should work against a real second player as
        /// readily as against the dev fixture.
        /// </summary>
        private static SessionPlayer FindAnyPeer()
        {
            foreach (SessionPlayer kp in Main.kCPlayers.Values)
                if (kp != null && kp.inst != null && kp.inst != Player.inst) return kp;

            return null;
        }

        /// <summary>Reads a field whether the game declares it public or private.</summary>
        private static object ReadField(Player p, string name)
        {
            FieldInfo f = typeof(Player).GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            return (f == null) ? null : f.GetValue(p);
        }

        /// <summary>
        /// Row count of an array, an ArrayExt or a List, without naming any of their element types.
        /// Minus one when the collection is absent.
        ///
        /// Reflection rather than eleven typed accessors: the point of the per-landmass check is
        /// that a twelfth structure should be one line to add, and requiring its exact generic type
        /// would defeat that.
        /// </summary>
        private static int RowCount(object collection)
        {
            if (collection == null) return -1;

            Array asArray = collection as Array;
            if (asArray != null) return asArray.Length;

            Type t = collection.GetType();

            PropertyInfo prop = t.GetProperty("Count");
            if (prop != null && prop.PropertyType == typeof(int))
                return (int)prop.GetValue(collection, null);

            FieldInfo field = t.GetField("Count");
            if (field != null && field.FieldType == typeof(int))
                return (int)field.GetValue(collection);

            return -1;
        }
    }
}
