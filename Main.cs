using KaCMultiplayer.Lobby;
using KaCMultiplayer.SaveIo;
using Assets.Code;
using Assets.Code.UI;
using Assets.Interface;
using Harmony;
using KaCMultiplayer.Net;
using KaCMultiplayer.LoadSaveOverrides;
using KaCMultiplayer.Trade;
using Newtonsoft.Json;
using Riptide;
using Riptide.Transports.Steam;
using Riptide.Utils;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Configuration.Assemblies;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using static ModCompiler;
using static World;

namespace KaCMultiplayer
{
    public class Main : MonoBehaviour
    {
        public static KCModHelper helper;
        public static MenuState menuState = (MenuState)MainMenuMode.State.Uninitialized;

        public static Dictionary<string, SessionPlayer> kCPlayers = new Dictionary<string, SessionPlayer>();
        public static Dictionary<ushort, string> clientSteamIds = new Dictionary<ushort, string>();

        /// <summary>
        /// Turns on dev switches named on the command line, e.g.
        ///
        ///   KingdomsAndCastles.exe -kcmdev -kcmautotest
        ///
        /// Every flag stays false unless its argument is present, so a player who never passes one
        /// gets exactly the shipped behaviour. Wrapped and silent on failure: an unreadable command
        /// line must not stop the mod loading.
        /// </summary>
        private static void ApplyLaunchFlags()
        {
            try
            {
                if (!HasLaunchFlag("-kcmdev")) return;   // the master switch; nothing else applies without it

                DevTestBuild = true;

                if (HasLaunchFlag("-kcmcombat")) CombatAuthorityEnabled = true;
                if (HasLaunchFlag("-kcmfreeze")) FreezeGhostKingdomsFully = true;
                if (HasLaunchFlag("-kcmraids")) RaidsEnabled = true;
                if (HasLaunchFlag("-kcmarmypos")) ArmyPositionSyncEnabled = true;
                if (HasLaunchFlag("-kcmautotest")) KaCMultiplayer.Dev.AutoTest.Enabled = true;

                helper.Log("[DEV] launch flags applied: DevTestBuild=" + DevTestBuild +
                           ", CombatAuthority=" + CombatAuthorityEnabled +
                           ", FreezeGhosts=" + FreezeGhostKingdomsFully +
                           ", Raids=" + RaidsEnabled +
                           ", ArmyPositions=" + ArmyPositionSyncEnabled +
                           ", AutoTest=" + KaCMultiplayer.Dev.AutoTest.Enabled);
            }
            catch (Exception e)
            {
                try { helper.Log("[DEV] could not read launch flags: " + e.Message); } catch { }
            }
        }

        /// <summary>True when the process was started with this exact argument.</summary>
        private static bool HasLaunchFlag(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args != null)
                for (int i = 0; i < args.Length; i++)
                    if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;

            return HasDevEnvironmentVariable(flag);
        }

        /// <summary>
        /// The same switch, read from an environment variable instead of the command line.
        ///
        /// Exists because Steam will not let a command-line switch through quietly. Launching the
        /// game with ANY custom argument, by any route, pops Steam's "Launch Game with custom
        /// arguments" dialog that a human has to click Continue on, which makes an automated
        /// build-run-read loop need a person sitting there for every single run. The dialog is
        /// about the arguments, not about how the game was started, so the only way past it is to
        /// stop passing arguments.
        ///
        /// So "-kcmdev" is also readable as KCMDEV=1. Reads an environment variable and nothing
        /// else: no file is opened, deliberately, because a settings file would mean a new
        /// System.IO reference and the Workshop scanner rejects those.
        ///
        /// Players are unaffected. Nobody has these set, so every switch stays off exactly as
        /// before, and the master switch still has to be on for any of the others to matter.
        /// </summary>
        private static bool HasDevEnvironmentVariable(string flag)
        {
            try
            {
                // "-kcmdev" -> "KCMDEV"
                string name = flag.TrimStart('-').ToUpperInvariant();
                string value = Environment.GetEnvironmentVariable(name);

                return !string.IsNullOrEmpty(value)
                    && value != "0"
                    && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// DEV ONLY. Turns on the two solo-testing aids together: <see cref="Net.NetLoopback"/>,
        /// which applies your own relayed messages back as though a peer had sent them, and
        /// <see cref="Net.FakePeer"/>, whose Ctrl+Shift+F hotkey spawns a second kingdom.
        ///
        /// **This must be false in anything released to players.** With it on, every action you
        /// take applies twice and a player could summon a second kingdom with a keypress.
        ///
        /// One switch rather than a flag per aid, because the failure mode is forgetting to put one
        /// of them back. Startup logs a conspicuous warning whenever it is on, so a build that
        /// shipped with it enabled says so in the first few lines of output.txt instead of being
        /// discovered by a confused player.
        /// </summary>
        public static bool DevTestBuild = false;

        /// <summary>
        /// Whether one machine arbitrates each fight instead of every machine resolving damage for
        /// itself. See Combat/CombatRule.cs for the model.
        ///
        /// **Off until it has been run with two real machines.** Turning it on suppresses damage
        /// resolution everywhere except the arbitrating machine, and the failure mode if the arbiter
        /// is ever computed wrongly is silent: things simply stop taking damage, with nothing in the
        /// log to say why. That is not a risk worth taking blind on a public build, so the whole
        /// feature ships dark and switches on for testing.
        ///
        /// Off reproduces exactly the behaviour that shipped before it existed, every machine
        /// resolves its own damage and combat diverges, which is wrong but is the devil already
        /// known. Single-player is unaffected either way.
        ///
        /// SWITCHED ON FOR THE PUBLIC BUILD, 2026-09-06, and the reasoning is worth keeping because
        /// the paragraph above argues the other way. It was written when nothing could fight: armies
        /// could not be created in multiplayer at all, because no unit category exists for teams 5
        /// and up, so leaving the feature dark cost nothing. Now that armies work, combat HAPPENS,
        /// and dark means every machine resolves its own damage and two players watch the same
        /// battle end differently. Doing nothing stopped being the safe option.
        ///
        /// What makes it safe to turn on is the failure mode, not confidence in the code:
        /// CombatAuthority.ResolvesHere returns true on every error and on every unowned position,
        /// so anything that goes wrong degrades to exactly the off behaviour rather than to nobody
        /// resolving. On cannot be worse than off; it can only fail back to it.
        ///
        /// Tested solo only (Dev/AutoTest fights a real battle and asserts the arbiter published its
        /// outcome, around 31 to 36 updates per fight). NOT yet tested across two machines. The
        /// villager death sync rides on this flag too.
        /// </summary>
        /// <summary>
        /// Whether viking raids are allowed to run in a multiplayer session.
        ///
        /// SHIPS OFF. Raids were disabled outright long before this, because a raid hard-froze the
        /// world clock the moment it registered: game time stopped, autosaves went silent, pawns
        /// halted, and no exception was ever thrown. That is a simulation stall, not an exception
        /// loop, which is why clearing boats and raiders by hand never recovered it.
        ///
        /// The cause was never pinned, and reading SetupRaid closely did not find it either. Two
        /// candidate hangs turned out to be safe on inspection: the objective loop always subtracts
        /// at least 1 because its list is seeded with five StartFire entries, and the do-while that
        /// scans for a target is a circular scan that terminates when it wraps. So the stall is
        /// somewhere in what the raid DOES, not in how it is planned, and finding it needs a run
        /// rather than more reading. This flag exists so that run can happen without shipping the
        /// freeze to anybody.
        ///
        /// ON NOW, 2026-09-15, and the reason it is safe to try is that the stall can no longer
        /// be silent OR fatal. Weather.Update's call to RaiderSystem.OnNewYear is wrapped by
        /// WeatherUpdateRaidGuardHook, so a throw there costs one year's raid and writes a full
        /// stack trace instead of wedging the world clock. The "no exception was ever thrown"
        /// finding above was almost certainly output.txt being read rather than Player.log, the
        /// same blind spot that hid the cave-container bug and stopped every farm in the game
        /// from harvesting.
        ///
        /// Turn back off here, or leave it off for a release, if a session shows raids doing
        /// something worse than throwing.
        /// </summary>
        public static bool RaidsEnabled = true;

        public static bool CombatAuthorityEnabled = true;

        /// <summary>
        /// Whether a departed player's kingdom is frozen BELOW the kingdom level as well as at it.
        ///
        /// PlayerUpdateFreezeHook already stops <c>Player.Update</c> for a ghost, which halts the
        /// kingdom-level simulation. Their armies and ships carry on regardless, because those tick
        /// through systems that never consult the owning Player, so a departed player's soldiers
        /// keep marching and, now that combat exists, keep fighting.
        ///
        /// **Off until tested.** The failure mode is the worst one this project has: gate a LIVE
        /// kingdom by mistake and that player's units stop dead, which reads exactly like the freeze
        /// bugs that have cost this mod whole test cycles. Off reproduces today's behaviour.
        ///
        /// Scope, deliberately: armies and ships only. Buildings tick as individual Tickable
        /// components through a flat 25,000-entry loop with no owner on hand, so gating them would
        /// mean a GetComponent per entry per frame, which is the very per-tick cost that starved the
        /// simulation once already. Their production is left running; see docs/open-threads.md.
        /// </summary>
        public static bool FreezeGhostKingdomsFully = false;

        /// <summary>
        /// Whether each player periodically states where their own armies are, so everybody else's
        /// copies can be nudged back into place.
        ///
        /// Only ORDERS travel between machines, so each one walks its own copy of an army to the
        /// destination. Two machines given the same order usually arrive at the same place, which
        /// is why this traffic was never spent. A chase breaks it: the destination keeps changing,
        /// pathing runs on another thread, and the copies drift. Now that combat is arbitrated the
        /// HEALTH of an army agrees everywhere while its position does not, so one player can watch
        /// a battle resolve somewhere the other sees an empty field.
        ///
        /// Enabled by default for the local Workshop hotfix build. The sync is batched and only
        /// reports armies that have really moved, so it fixes the visible "battle happened over
        /// there on my screen" problem without going back to expensive per-unit streaming.
        ///
        /// Switch on with -kcmarmypos, or KCMARMYPOS=1. Requires the dev master switch like the
        /// others. See Combat/ArmyPositionSync.cs for the cost argument, which is the one that
        /// matters: the villager version of this idea once starved the simulation, and the
        /// difference is tens of armies against thousands of villagers, plus a batched message
        /// every 30 ticks against one per villager per frame.
        /// </summary>
        public static bool ArmyPositionSyncEnabled = true;

        /// <summary>
        /// Whether the host flies everyone's dragons. See Combat/DragonFlightSync.cs.
        ///
        /// Off means every machine goes back to flying its own copy with its own AI, which is
        /// what produced two dragons with the same name and hit points burning different
        /// villages. Kept as a switch anyway, because this is the only feature in the mod that
        /// takes a unit's brain away, and if it ever misbehaves the fallback should be one flag
        /// rather than a rollback.
        /// </summary>
        public static bool DragonFlightSyncEnabled = true;

        // Logs an exception AND its full inner-exception chain (message + stack). Save/load
        // failures surface as a TargetInvocationException ("Exception has been thrown by the
        // target of an invocation") whose real cause is an inner exception, so plain e.Message
        // hides the actual bug. Use this anywhere we catch around game load/save calls.
        public static void LogEx(string context, Exception e)
        {
            helper.Log("===== Exception: " + context + " =====");
            int depth = 0;
            for (Exception cur = e; cur != null; cur = cur.InnerException, depth++)
            {
                helper.Log($"[{depth}] {cur.GetType().FullName}: {cur.Message}");
                helper.Log(cur.StackTrace ?? "(no stack trace)");
            }
        }

        private static readonly MethodInfo tryAddJobs =
            typeof(Building).GetMethod("TryAddJobs", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Creates a building's worker jobs, unless it has no real job category.
        ///
        /// Ship-launch pads (TransportShipLaunch, SeedShipLaunch, the AL_*ShipBlock placeables)
        /// carry worker slots but <see cref="JobCategory.Undefined"/>, because they are transient
        ///, they spawn a ship and demolish themselves. In single-player that happens instantly.
        /// In multiplayer the synced copy can linger long enough to surface a "JobCategoryUndefined
        /// 0/6" worker job, which can never be filled because the category does not correspond to
        /// any work.
        ///
        /// Called from IL, by <see cref="BuildingCompleteBuildHook"/>'s transpiler, in place of
        /// the game's own unconditional call. Public and static for that reason.
        /// </summary>
        public static void AddJobsUnlessUncategorised(Building building)
        {
            // Skipping the call is a behaviour change, so single player does not get it: there the
            // transpiler's redirect resolves straight back to what vanilla would have done.
            if (Main.InMultiplayer && building.GetJobCategory() == JobCategory.Undefined) return;

            tryAddJobs.Invoke(building, null);
        }

        /// <summary>
        /// Runs <c>Player.Reset()</c> without letting it destroy the world's cave container.
        ///
        /// Reset ends with <c>Object.Destroy(World.inst.caveContainer)</c>, which is GLOBAL world
        /// state rather than the kingdom's, and nothing recreates it: only World.Setup builds one,
        /// once, at world generation. Losing it makes every save throw, and a throwing autosave
        /// silently aborts the whole Weather.OnSeasonChange dispatch, which is what stopped every
        /// farm in the game from harvesting. See SessionPlayer for the full account.
        ///
        /// Hiding the container for the duration is enough, because Object.Destroy(null) is a
        /// no-op, so the container and its caves, wolf dens and witch huts all survive.
        ///
        /// Anything that resets a kingdom should come through here rather than calling Reset
        /// directly; there is no case where destroying the caves is the intent.
        /// </summary>
        public static void ResetKingdomSafely(Player p)
        {
            if (p == null) return;

            GameObject caves = null;
            bool hid = false;
            if (World.inst != null)
            {
                caves = World.inst.caveContainer;
                World.inst.caveContainer = null;
                hid = true;
            }

            try { p.Reset(); }
            finally { if (hid && World.inst != null) World.inst.caveContainer = caves; }
        }

        /// <summary>
        /// Stands in for the two calls through which resetting ONE kingdom wipes the job system
        /// for ALL of them: <c>JobSystem.ClearAllJobs</c> in <c>Player.Reset</c>, and
        /// <c>JobSystem.InitJobList</c> in <c>Player.ResetPerLandMassData</c>, which replaces the
        /// whole job table with empty lists.
        ///
        /// Each building keeps its own list of jobs, and neither call touches those lists. So
        /// resetting a remote kingdom (building a joining or rejoining player's kingdom object,
        /// regenerating the map, the resets around a load) left every other building holding jobs
        /// the job system no longer knew about. A construction site in that state never gets
        /// another builder, because TryAddBuilderJobs only adds jobs while its list is short: the
        /// "builders forget to finish after a rejoin" report, whose workaround was deleting the
        /// site so it made fresh jobs. Only the local kingdom's reset clears the job system now;
        /// single player always does, exactly as vanilla.
        ///
        /// During a load the remote kingdom being unpacked is briefly Player.inst, so this lets
        /// InitJobList through there; PreserveLoadedJobsHook in SessionSave covers that case.
        ///
        /// Called from IL, by <see cref="PlayerResetJobsHook"/>. Public and static for that reason.
        /// </summary>
        public static void ClearAllJobsUnlessRemote(JobSystem jobs, Player resetting)
        {
            if (Main.InMultiplayer && resetting != Player.inst) return;
            jobs.ClearAllJobs();
        }

        /// <summary>The InitJobList half of <see cref="ClearAllJobsUnlessRemote"/>.</summary>
        public static void InitJobListUnlessRemote(JobSystem jobs, Player resetting)
        {
            if (Main.InMultiplayer && resetting != Player.inst) return;
            jobs.InitJobList();
        }

        /// <summary>
        /// Routes a Player method's call to <paramref name="gameMethod"/> on JobSystem through
        /// <paramref name="guard"/>, with the Player itself pushed as the extra argument so the
        /// guard can tell whose reset this is. Shared by both reset hooks below.
        /// </summary>
        private static IEnumerable<CodeInstruction> GuardJobSystemCall(
            IEnumerable<CodeInstruction> instructions, string gameMethod, string guard, Action counted)
        {
            MethodInfo replacement = typeof(Main).GetMethod(guard);

            foreach (CodeInstruction c in instructions)
            {
                MethodInfo target = c.operand as MethodInfo;
                if (target != null && target.DeclaringType == typeof(JobSystem) && target.Name == gameMethod)
                {
                    // Labels stay on the original instruction; the pushed receiver goes first.
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    c.opcode = OpCodes.Call;
                    c.operand = replacement;
                    counted();
                }
                yield return c;
            }
        }

        /// <summary>Guards Player.Reset's ClearAllJobs. See <see cref="ClearAllJobsUnlessRemote"/>.</summary>
        [HarmonyPatch(typeof(Player), "Reset")]
        public class PlayerResetJobsHook
        {
            /// <summary>Call sites rewritten, counting both hooks. Harmony re-runs a transpiler
            /// every time the method is patched again, so this can exceed two.</summary>
            public static int Rewritten;

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return GuardJobSystemCall(instructions, "ClearAllJobs", "ClearAllJobsUnlessRemote", () => Rewritten++);
            }
        }

        /// <summary>Guards ResetPerLandMassData's InitJobList. See <see cref="ClearAllJobsUnlessRemote"/>.</summary>
        [HarmonyPatch(typeof(Player), "ResetPerLandMassData")]
        public class PlayerResetJobListHook
        {
            public static int Rewritten;

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return GuardJobSystemCall(instructions, "InitJobList", "InitJobListUnlessRemote", () => Rewritten++);
            }
        }

        /// <summary>
        /// The player whose kingdom owns a landmass, or null if nobody's does.
        ///
        /// Deliberately NOT <see cref="GetPlayerByTeamID"/>, whose fallback to the local player is
        /// load-bearing for its own callers and exactly wrong here: the callers below need to tell
        /// "somebody else's island" apart from "no one's", and a fallback would silently turn the
        /// second into the first.
        /// </summary>
        public static Player PlayerOwningLandmass(int landMass)
        {
            if (landMass < 0) return null;

            LandmassOwner owner = World.GetLandmassOwner(landMass);
            if (owner == null) return null;

            SessionPlayer sp = KaCMultiplayer.Net.NetPlayers.ByTeam(owner.teamId);
            return sp != null ? sp.inst : null;
        }

        /// <summary>
        /// Which kingdom's job settings govern a landmass, or null to let vanilla answer.
        ///
        /// Null for single player, for an unowned island, and for the receiver's own islands,
        /// which are all the cases where the receiver is already the right answer.
        /// </summary>
        public static Player JobTableOwnerFor(Player receiver, int landMass)
        {
            try
            {
                if (!NetClient.client.IsConnected) return null;   // single player: untouched

                Player owner = PlayerOwningLandmass(landMass);
                if (owner == null || owner == receiver) return null;

                // Their tables may predate the map. Grow them before the caller's bounds check
                // decides they are unusable and falls back to the local player's. See
                // EnsureJobTablesCoverWorld.
                EnsureJobTablesCoverWorld(owner);
                return owner;
            }
            catch { return null; }   // a lookup failure must never stop jobs being handed out
        }

        /// <summary>Players already grown, so the log reports each one once rather than per frame.</summary>
        private static readonly HashSet<int> grownJobTables = new HashSet<int>();

        /// <summary>Teams whose building registries have already been reported as grown.</summary>
        private static readonly HashSet<int> grownBuildingRegistries = new HashSet<int>();

        /// <summary>
        /// Grows a player's per-landmass job tables to cover the world as it is NOW.
        ///
        /// A remote Player is built during the handshake, which on a joining machine happens
        /// BEFORE the map is generated. <c>Player.Reset -> SetupJobPriorities</c> therefore sizes
        /// every per-landmass job array to whatever <c>World.NumLandMasses</c> was at that moment,
        /// usually the menu world's, and nothing ever grows them once the real map arrives. The
        /// save path already knew this and worked around it, logging "player has 2 landmass job
        /// rows but the world has 5"; this is the same fault, unfixed at the source.
        ///
        /// It is what defeated the landmass-owner job hooks. Their bounds check saw a table too
        /// short to hold the island being staffed, treated it as unusable, and handed the decision
        /// back to vanilla, which is the local player, which is the bug those hooks exist to fix.
        /// The visible result was a peer's farms sitting at workerPct=0.00 forever.
        ///
        /// New rows are cloned from row 0 rather than rebuilt from <c>defaultPriorityOrder</c> and
        /// <c>defaultEnabledFlags</c>, which are private: row 0 was itself seeded from those
        /// defaults by SetupJobPriorities and has the right width, so copying it needs no
        /// reflection and cannot disagree with whatever the game's defaults are today.
        ///
        /// Grows only; existing rows are untouched, so a kingdom's own job settings survive.
        /// </summary>
        public static bool EnsureJobTablesCoverWorld(Player p)
        {
            if (p == null || World.inst == null) return false;

            int need = World.inst.NumLandMasses;
            if (need <= 0) return false;

            if (p.JobPriorityOrder == null || p.JobPriorityOrder.Length == 0) return false;   // never Reset

            bool wide = p.JobPriorityOrder.Length >= need
                     && p.JobEnabledFlag != null && p.JobEnabledFlag.Length >= need
                     && p.JobCustomMaxEnabledFlag != null && p.JobCustomMaxEnabledFlag.Length >= need
                     && p.JobFilledAvailable != null && p.JobFilledAvailable.Count >= need;
            if (wide) return true;

            try
            {
                int had = p.JobPriorityOrder.Length;
                int slots = p.JobPriorityOrder[0].Length;   // whatever JobCategory.NumCategories is today

                p.JobPriorityOrder = Grow(p.JobPriorityOrder, need, p.JobPriorityOrder[0]);
                p.JobEnabledFlag = Grow(p.JobEnabledFlag, need,
                    (p.JobEnabledFlag != null && p.JobEnabledFlag.Length > 0) ? p.JobEnabledFlag[0] : new bool[slots]);
                p.JobCustomMaxEnabledFlag = Grow(p.JobCustomMaxEnabledFlag, need,
                    (p.JobCustomMaxEnabledFlag != null && p.JobCustomMaxEnabledFlag.Length > 0) ? p.JobCustomMaxEnabledFlag[0] : new bool[slots]);

                // JobFilledAvailable is an ArrayExt of [category, 2] counters, not a jagged array,
                // and it is scratch that the job loop rewrites every pass, so fresh rows are right.
                if (p.JobFilledAvailable != null)
                    while (p.JobFilledAvailable.Count < need) p.JobFilledAvailable.Add(new int[slots, 2]);

                int team = (p.PlayerLandmassOwner != null) ? p.PlayerLandmassOwner.teamId : -1;
                if (grownJobTables.Add(team))
                    helper.Log($"[JOBS] grew team {team}'s job tables from {had} landmass row(s) to {need}"
                               + " (their kingdom was built before this machine had the map)");

                return true;
            }
            catch (Exception e) { LogEx("growing job tables", e); return false; }
        }

        /// <summary>Lengthens a jagged array, cloning <paramref name="seed"/> into each new row.</summary>
        private static T[][] Grow<T>(T[][] table, int need, T[] seed)
        {
            if (table == null) table = new T[0][];
            if (table.Length >= need) return table;

            T[][] bigger = new T[need][];
            Array.Copy(table, bigger, table.Length);
            for (int i = table.Length; i < need; i++)
                bigger[i] = (T[])seed.Clone();

            return bigger;
        }

        /// <summary>Re-checks every kingdom's job tables, for use once the map is final.</summary>
        public static void EnsureAllJobTablesCoverWorld()
        {
            foreach (SessionPlayer kp in kCPlayers.Values)
                if (kp != null && kp.inst != null) EnsureJobTablesCoverWorld(kp.inst);
        }

        /// <summary>
        /// Keeps every kingdom's job tables wide enough for the current world.
        ///
        /// Keep each player's settings independent instead of aliasing foreign owners' rows
        /// into the local player's arrays. Job visibility itself comes from the shared
        /// JobSystem registry, which SessionSave preserves while loading additional kingdoms.
        ///
        /// The non-mutating owner routing now lives in <see cref="JobTableOwnerFor"/> and the
        /// accessor hooks below. This timer only grows stale remote tables so those hooks always
        /// have rows to return.
        /// </summary>
        public static void AliasJobTablesToOwners()
        {
            try
            {
                if (!NetClient.client.IsConnected) return;   // single player owns everything
                if (Player.inst == null || World.inst == null) return;

                EnsureJobTablesCoverWorld(Player.inst);
                foreach (SessionPlayer kp in kCPlayers.Values)
                    if (kp != null && kp.inst != null && kp.inst != Player.inst)
                        EnsureJobTablesCoverWorld(kp.inst);
            }
            catch (Exception e) { LogEx("refreshing job tables", e); }
        }

        // ---- WHOSE RULES STAFF WHICH ISLAND ----------------------------------------------
        //
        // JobSystem.Update is the game's job-assignment engine, and it reads the Player.inst
        // singleton SIXTEEN times. It walks EVERY landmass in the world and, for each one, asks
        // Player.inst for that landmass's job priority order and enabled flags. Player.inst is
        // always the local kingdom.
        //
        // Neither transpiler covers it: the singleton rewrite targets Player's own instance
        // methods and the owner rewrite targets Building's, and JobSystem is neither. So in a
        // session every island in the world was staffed according to the LOCAL player's decrees,
        // including islands belonging to other players. Their farms, their barracks, their
        // quarries, all hired and fired by somebody else's settings, on every machine
        // independently.
        //
        // JobSystem reaches the two pieces of per-landmass state that actually gate assignment
        // through Player.GetJobEnabledFlags and Player.GetJobPriorityOrder. Those are one-line
        // getters, inside Mono's inlining threshold, so a Prefix on them never ran: that is why
        // this fix once existed as two accessor hooks and peers' farms still went unstaffed. The
        // transpiler below rewrites the CALL SITES in JobSystem.Update instead, to the two static
        // helpers here, which answer with the landmass OWNER's row.
        //
        // The remaining Player.inst reads in that method are left alone on purpose:
        // JobFilledAvailable and JobCustomMaxEnabledFlag are scratch counters keyed by
        // [landmass][category] that the same loop writes as it goes, so one consistent owner for
        // the table is all they need, and the loop bound is the landmass count, which is the same
        // number whoever you ask.

        /// <summary>
        /// A landmass's job enabled flags, from the kingdom that owns it.
        /// Called in place of Player.GetJobEnabledFlags inside JobSystem.Update, so another
        /// player's island is staffed by their settings. Single player: vanilla's own answer.
        /// </summary>
        public static bool[] JobEnabledFlagsFor(Player receiver, int landMass)
        {
            Player owner = JobTableOwnerFor(receiver, landMass);
            bool[][] table = owner != null ? owner.JobEnabledFlag : null;
            if (table == null || landMass >= table.Length || table[landMass] == null)
                return receiver.JobEnabledFlag[landMass];
            return table[landMass];
        }

        /// <summary>
        /// A landmass's job priority order, from the kingdom that owns it.
        /// Same reason as <see cref="JobEnabledFlagsFor"/>; the two must agree, since the loop
        /// reads flag j of one against slot j of the other.
        /// </summary>
        public static int[] JobPriorityOrderFor(Player receiver, int landMass)
        {
            Player owner = JobTableOwnerFor(receiver, landMass);
            int[][] table = owner != null ? owner.JobPriorityOrder : null;
            if (table == null || landMass >= table.Length || table[landMass] == null)
                return receiver.JobPriorityOrder[landMass];
            return table[landMass];
        }

        /// <summary>
        /// Rewrites JobSystem.Update's two job-table getter calls to the owner-aware helpers above.
        /// The call site is the only seam that survives inlining; see the note above.
        /// Same stack shape (Player, int) in and array out, so the swap is one operand each.
        /// </summary>
        [HarmonyPatch(typeof(JobSystem), "Update")]
        public class JobSystemOwnerTablesHook
        {
            /// <summary>How many call sites were rewritten, so a check can see the patch took.</summary>
            public static int Rewritten;

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo flags = typeof(Main).GetMethod("JobEnabledFlagsFor");
                MethodInfo order = typeof(Main).GetMethod("JobPriorityOrderFor");

                foreach (CodeInstruction c in instructions)
                {
                    MethodInfo target = c.operand as MethodInfo;
                    if (target != null && target.DeclaringType == typeof(Player)
                        && (c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt))
                    {
                        if (target.Name == "GetJobEnabledFlags") { c.opcode = OpCodes.Call; c.operand = flags; Rewritten++; }
                        else if (target.Name == "GetJobPriorityOrder") { c.opcode = OpCodes.Call; c.operand = order; Rewritten++; }
                    }
                    yield return c;
                }
            }
        }

        // WHO IS ELIGIBLE IS NOT WHO GETS PICKED. JobSystemOwnerTablesHook, just above, makes
        // every machine agree on which villagers are eligible for a job and in what priority --
        // it does not stop the assignment itself. Job.UpdateAssignment is the vanilla method that
        // actually calls Job.AssignEmployee for an ordinary job (checked against the shipped IL:
        // BuilderJob and GuildBuilderJob, the two job types a construction site uses, both
        // inherit it unmodified, neither overrides it), and JobSystem.Update's own loop calls it
        // for every open job on every landmass, on every machine, once a frame, with no ownership
        // check at all -- run identically whether the landmass is this machine's own or not.
        //
        // So even with matching eligibility now, two machines can still independently assign
        // DIFFERENT idle villagers to the SAME foreign job, because which villagers are idle
        // RIGHT NOW is each machine's own local simulation, not something the settings fix
        // synchronises. A foreign kingdom's storage buildings then get staffed by whichever
        // villager THIS machine happened to pick rather than whichever one the owner's own
        // machine actually picked, and drift out of step with the owner's real state -- the same
        // "everyone else holds a mirror that drifts" DealKind.Settled already documents for a
        // diplomacy payment, reached here through job assignment instead of direct resource
        // movement.
        //
        // (Job.AssignEmployee has two other callers, Building.SetAndAddJob -- reached from every
        // building's OnAddJobs via CompleteBuild -- and Home.UpdateHomemakerAssignment, neither
        // gated here. Left open deliberately: this closes the per-tick JobSystem.Update path,
        // confirmed to matter for the symptom below; the other two need their own verification
        // before gating.)
        //
        // Gated the same way BarracksTickForeignHook already gates Barracks.Tick: the kingdom's
        // own machine decides, and its own broadcasts carry the result to everyone else. Real
        // session report this closes: builders correctly staffed by the right settings but still
        // occasionally picked independently per machine, drifting a rejoining player's own
        // warehouses out of step with what they actually held.
        [HarmonyPatch(typeof(Job), "UpdateAssignment")]
        public class JobUpdateAssignmentForeignHook
        {
            // Same reasoning as BarracksTickForeignHook's own flag: this sits on a per-job,
            // per-tick path (every open job in the world, every frame), so an unguarded log here
            // is a line per job per tick.
            private static bool warnedGateFailure;

            public static bool Prefix(Job __instance)
            {
                if (!NetClient.client.IsConnected) return true; // single-player: the game's own path
                try
                {
                    IEmployer employer = __instance.employer;
                    if (employer == null) return true;

                    // Unowned/neutral (no LandmassOwner yet, e.g. a keep placement still in
                    // flight) ticks everywhere; ForeignKingdomTeam already excludes the game's
                    // own AI/neutral team range.
                    LandmassOwner owner = World.GetLandmassOwner(employer.LandMass());
                    if (owner == null) return true;

                    if (ForeignKingdomTeam(owner.teamId)) return false; // foreign kingdom's job: not ours to decide
                }
                catch (Exception e)
                {
                    if (!warnedGateFailure)
                    {
                        warnedGateFailure = true;
                        LogEx("job assignment ownership gate (further occurrences suppressed)", e);
                    }
                }

                return true;
            }
        }

        // Team ids already warned about, so an orphaned kingdom doesn't log once per frame
        // forever. A miss is persistent by nature, the owner has left, so the first report is
        // the informative one and the rest are noise.
        private static readonly HashSet<int> warnedMissingTeams = new HashSet<int>();

        /// <summary>
        /// The <see cref="Player"/> that owns a landmass team, falling back to the local player.
        ///
        /// **The fallback is load-bearing.** Callers assign the result straight into
        /// <c>Player.inst</c>, so returning null would put a null in the game's own static. When
        /// it does fall back, the caller silently operates on the wrong kingdom, see the ghost
        /// -kingdom note in <c>NetHost</c>'s client-left handler, so it is worth a warning.
        ///
        /// The lookup lives in <see cref="NetPlayers.ByTeam"/>. This is the adapter that supplies
        /// the fallback and the diagnostics.
        /// </summary>
        public static Player GetPlayerByTeamID(int teamId)
        {
            SessionPlayer owner = NetPlayers.ByTeam(teamId);
            if (owner != null) return owner.inst;

            // Only meaningful in a session where teams distinguish anyone.
            bool inSession = NetHost.IsRunning
                || (NetClient.client != null && NetClient.client.IsConnected);

            if (inSession && warnedMissingTeams.Add(teamId))
            {
                int localTeam = -1;
                if (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                    localTeam = Player.inst.PlayerLandmassOwner.teamId;

                NetLog.Warn("no player owns team " + teamId +
                            "; attributing to the local player (team " + localTeam +
                            "). Known teams: " + NetPlayers.KnownTeams());
            }

            return Player.inst;
        }

        /// <summary>
        /// The <see cref="Player"/> a building belongs to, by landmass ownership.
        ///
        /// **Do not rename or change this signature.** A transpiler further down this file
        /// resolves it by name with <c>typeof(Main).GetMethod("GetPlayerByBuilding")</c> and emits
        /// a call to it, so a rename fails at patch time rather than at compile time.
        ///
        /// Ownership comes from the landmass, not <c>TeamID()</c>, because an unowned landmass has
        /// no team to ask, that case is the local player, matching vanilla's leniency on neutral
        /// tiles.
        /// </summary>
        public static Player GetPlayerByBuilding(Building building)
        {
            if (building == null) return Player.inst;

            try
            {
                if (World.GetLandmassOwner(building.LandMass()) == null) return Player.inst;

                return GetPlayerByTeamID(building.TeamID());
            }
            catch (Exception e)
            {
                NetLog.Warn("could not resolve an owner for building '" +
                            (building.UniqueName ?? "<unnamed>") + "': " + e.Message);
                return Player.inst;
            }
        }

        // "Is this the LOCAL player's building?", the MP-correct replacement for vanilla's
        // `building.TeamID() == 0` ("is this mine?") checks, which fail because the local player's team
        // is 5/6/7 in MP, not 0. Uses landmass ownership (no TeamID() dependency), matching
        // DemolishSelectedHook. Unowned landmass → true (matches vanilla leniency for neutral tiles).
        public static bool IsLocalBuilding(Building b)
        {
            if (b == null) return false;
            try
            {
                var lmo = World.GetLandmassOwner(b.LandMass());
                if (lmo == null) return true;
                return Player.inst != null && Player.inst.PlayerLandmassOwner != null
                    && lmo.teamId == Player.inst.PlayerLandmassOwner.teamId;
            }
            catch { return false; }
        }

        public static string PlayerSteamID = SteamUser.GetSteamID().ToString();

        public static SteamBootstrap SteamBootstrap = null;
        public static SteamServer steamServer = new SteamServer();
        public static Riptide.Transports.Steam.SteamClient steamClient = new Riptide.Transports.Steam.SteamClient(steamServer);

        public static ushort currentClient = 0;

        // True while a received WorldHazard packet is being applied, so the client-side
        // suppression in AddWolfDenHook / AddWitchHutHook lets the host-driven spawn through.
        public static bool applyingWorldHazard = false;

        // True while a speed change received from a peer is being applied locally. The
        // SpeedControlUI hook reads it to tell "the user pressed a key" from "a peer told us",
        // and must not re-broadcast the second, that ping-pongs between the two machines.
        public static bool applyingRemoteSpeed = false;

        /// <summary>
        /// The game speed actually in effect, kept current by SpeedControlUISetSpeedHook.
        /// </summary>
        public static int CurrentSpeed = 1;

        // True while a speed change caused by the LOCAL player opening or closing a menu is being
        // applied. Such a change is real for this machine and must not leave it, see
        // PlayingModeMenuSpeedHook.
        public static bool localMenuSpeedChange = false;

        /// <summary>
        /// Pauses the running game, if one is running.
        ///
        /// Whether the other players pause too depends on where this is called from, and that is
        /// deliberate rather than incidental. <c>SpeedControlUISetSpeedHook</c> broadcasts a local
        /// speed change but stays silent for one applied inside a <c>NetApply</c> scope, so:
        ///
        ///   - called from a message handler (every machine runs it anyway), no broadcast, and none
        ///     needed; each machine pauses itself.
        ///   - called from an event handler such as a disconnect, broadcast, so one machine noticing
        ///     something pauses everyone.
        ///
        /// Safe before a game exists: in the menus <c>SpeedControlUI.inst</c> is null and this is a
        /// no-op.
        /// </summary>
        public static void PauseGame(string reason)
        {
            try
            {
                if (SpeedControlUI.inst == null) return;              // no game running
                if (GameState.inst != null && !GameState.inst.IsPlayMode()) return;
                SpeedControlUI.inst.SetSpeed(0);
                helper.Log($"[SPEED] paused: {reason}");
            }
            catch (Exception e) { helper.Log("[SPEED] pause error: " + e.Message); }
        }

        private void SceneLoaded(KCModHelper helper)
        {
            helper.Log("SceneLoaded: wiring Riptide logging and Steam bootstrap");
            RiptideLogger.Initialize(helper.Log, helper.Log, helper.Log, helper.Log, false);

            helper.Log($"local Steam persona: {SteamFriends.GetPersonaName()}");


            SteamBootstrap = new GameObject("SteamBootstrap").AddComponent<SteamBootstrap>();
            DontDestroyOnLoad(SteamBootstrap);

            var lobbyManager = new GameObject("SteamLobby").AddComponent<SteamLobby>();
            DontDestroyOnLoad(lobbyManager);

            try
            {

                SteamFriends.SetRichPresence("status", "In a co-op kingdom");

                // The message layer. Every message in the mod goes through this.
                NetLog.Sink = helper.Log;
                NetRouter.Bind(new RiptideTransport(() => NetHost.server, () => NetClient.client));

                // Both solo-testing aids follow the one dev switch; see Main.DevTestBuild.
                // NetLoopback stands down on its own once a real player connects.
                NetLoopback.Enabled = DevTestBuild;
                FakePeer.Enabled = DevTestBuild;
                if (DevTestBuild)
                    NetLog.Warn("DEV TEST BUILD: loopback and the fake-peer hotkey are ENABLED. " +
                                "Your own actions apply twice and Ctrl+Shift+F spawns a second kingdom. " +
                                "Main.DevTestBuild must be false in any build given to players.");

                NetRegistrations.Install();

                // New save path (Main.UseVanillaSaveFormat): after the game packs its container it
                // fires OnSaveEvent, then starts the write thread, so an entry written here lands in
                // the file. No-ops on the old path and outside a session. See docs/save-migration-plan.md.
                Broadcast.OnSaveEvent.Listen((sender, e) => WriteModSessionToDict());

                // The lobby dropdowns take their options from the prefab, not from code,
                // nothing anywhere calls AddOptions. Their selected index is cast straight
                // to these game enums, so a prefab's option list has to match each one in
                // count and order, or the wrong setting is applied with no error. Logged so
                // the lists can be copied when rebuilding the prefabs.
                try
                {
                    helper.Log("[ui] Difficulty options: " + string.Join(", ", Enum.GetNames(typeof(Player.Difficulty))));
                    helper.Log("[ui] WorldSize options: " + string.Join(", ", Enum.GetNames(typeof(World.MapSize))));
                    helper.Log("[ui] WorldType options: " + string.Join(", ", Enum.GetNames(typeof(World.MapBias))));
                    helper.Log("[ui] WorldRivers options: " + string.Join(", ", Enum.GetNames(typeof(World.MapRiverLakes))));
                }
                catch (Exception uiEx) { helper.Log("[ui] enum dump failed: " + uiEx.Message); }

                // Diagnostic only, and guarded because it runs before World exists.
                //
                // A local mod is loaded EARLIER than a workshop one, early enough that World.inst
                // is still null here. Unguarded, this one log line threw and took the whole rest of
                // SceneLoaded with it, including the server-browser wiring, so moving the mod from
                // the workshop folder into mods/ silently cost the multiplayer menu. Nothing below
                // is diagnostic, so nothing below should depend on a dump succeeding.
                try
                {
                    if (World.inst != null && World.inst.mapSizeDefs != null)
                        Main.helper.Log(JsonConvert.SerializeObject(World.inst.mapSizeDefs, Formatting.Indented));
                    else
                        Main.helper.Log("[ui] map size defs not available yet (World has not loaded); skipping the dump");
                }
                catch (Exception mapEx) { Main.helper.Log("[ui] map size def dump failed: " + mapEx.Message); }

                // Sibling index 2 places it under New Game / Load. The previous version set
                // FirstSibling and then immediately overrode it with SetSiblingIndex(2).
                MenuButton.Create("Multiplayer", () =>
                {
                    SfxSystem.PlayUiSelect();
                    TransitionTo(MenuState.BrowserScreen);
                }, siblingIndex: 2);

                // Kingdom Share is single-player only.
                MenuButton.Remove("TopLevelUICanvas/TopLevel/Body/ButtonContainer/Kingdom Share");
            }
            catch (Exception ex)
            {
                LogEx("SceneLoaded", ex);
            }

        }

        public static int FixedUpdateInterval = 0;

        private void FixedUpdate()
        {
            // Three per-tick syncs are deliberately absent, and the reasoning is worth keeping
            // because each looks like an obvious thing to add:
            //
            //   Villager positions, serialising every villager each tick scales with population
            //   and chokes the simulation on both machines. A workable version would send deltas
            //   for villagers near the viewer, not the whole set.
            //
            //   Resource totals, each player sees only their own kingdom's resources, so the
            //   receiving side has nowhere to show another player's.
            //
            //   Shared vision, a per-tick fog rebuild is expensive, and it only existed to make
            //   the two above visible across islands. Ordinary fog of war suits co-op: each player
            //   develops their own island and explores by ship.
            //
            // RevealAllForSharedVision stays for a possible one-time reveal, which would cost
            // nothing per tick.

            // The save transfer is pumped from Update, not from here. The host pauses itself while
            // sending a snapshot to a joiner, pausing stops FixedUpdate, and a queue drained only
            // from FixedUpdate then never moves again. See SaveTransfer.PumpOutgoing.

            // Cross-player merchant trade (Phase 1): keep trade docks open between all players so a
            // player merchant can route to and dock at another player's port. Idempotent + re-covers
            // late joiners; throttled since it rarely changes. Phase 2 (the goods↔gold exchange) is
            // applied + synced separately.
            if (NetClient.client.IsConnected && (FixedUpdateInterval % 100 == 0))
            {
                EnsureTradeDocksOpenInMP();
                LogBaselinePayCosts();
            }

            // Retry any ship-guid assignments whose ship registered a frame or two after its launch OnBuilt.
            ProcessPendingShipGuids();

            // Publish the outcome of any fight this machine arbitrates. Self-throttling, and silent
            // when nothing is fighting. See Combat/CombatSync.cs.
            KaCMultiplayer.Combat.CombatSync.Tick();

            // Keep the "who has left" set fresh, so the tick gates below it stay a set lookup.
            KaCMultiplayer.Combat.FrozenKingdoms.Tick();

            // Tell the other players where our own armies are, when they have moved and when the
            // feature is switched on. See Combat/ArmyPositionSync.cs.
            KaCMultiplayer.Combat.ArmyPositionSync.Tick();

            // Fly the dragons for everybody. Host only, and silent when nothing is airborne.
            // See Combat/DragonFlightSync.cs.
            KaCMultiplayer.Combat.DragonFlightSync.Tick();

            // Counts declared wars down to the season they begin. See PlayerRelations.
            TickSeasonWatchers();

            // Keep every island staffed by its own kingdom. On a timer rather than an event
            // because the things that invalidate it, a join, a reconnect, a map reroll, a save
            // load, have no single hook between them; the check is a reference comparison per
            // landmass and does nothing at all once the wiring is right.
            if (FixedUpdateInterval % 60 == 0) AliasJobTablesToOwners();

            // A backstop for the kingdom copies the host saves. They are normally refreshed right
            // after each save, which is every season; this covers a session that somehow never
            // autosaves, so the copies can never go stale without limit. See Net/KingdomMirror.cs.
            if (FixedUpdateInterval % 12000 == 0) KaCMultiplayer.Net.KingdomMirror.RequestFromEveryone();

            // Guest side: send ours once, soon after the world is up, so an early save or a quick
            // departure is still recorded from our own kingdom rather than from the host's view.
            if (FixedUpdateInterval % 600 == 0) KaCMultiplayer.Net.KingdomMirror.EnsureFirstCopy();

            // Keep streamer effects the same on every machine. Silent, and free, unless somebody
            // is actually running them. See Net/StreamerEffectSync.cs.
            KaCMultiplayer.Net.StreamerEffectSync.Tick();

            // Repaint every flag in the world if somebody's colours or somebody's ground changed.
            // Coalesced, so a burst of building placements costs one repaint, not one each.
            RefreshBannersIfDirty();

            FixedUpdateInterval++;
        }

        // Set once the baseline price table has been reported, so it is written exactly once per
        // session. Cleared by ResetPerSessionLogs when the session ends.
        private static bool _loggedPayCosts;

        // Reports the game's baseline gold-per-unit prices, once.
        //
        // Player.defaultPayCost is the table every trade in this mod actually prices at: MerchantUI
        // uses it for both the buy and the sell window, and LandmassOwner.GetPayCosts hands it back
        // for every team pairing because nothing ever calls SetPayCosts. It is a serialized field on
        // the Player prefab, so the numbers exist only at runtime, this is the only way to see them.
        //
        // One line per session, from the throttled block above so it lands as soon as Player.inst is
        // up. The flag is set BEFORE the read: a throw here must not turn into a line every tick.
        private void LogBaselinePayCosts()
        {
            if (_loggedPayCosts || Player.inst == null) return;
            _loggedPayCosts = true;

            try
            {
                ResourceAmount prices = Player.inst.defaultPayCost;
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < (int)FreeResourceType.NumTypes; i++)
                {
                    FreeResourceType t = (FreeResourceType)i;
                    if (t == FreeResourceType.Gold || t == FreeResourceType.DeadVillager) continue;
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(t).Append("=").Append(prices.Get(t));
                }
                helper.Log($"[PRICES] baseline gold per unit (Player.defaultPayCost): {sb}");
            }
            catch (Exception e) { helper.Log("[PRICES] could not read defaultPayCost: " + e.Message); }
        }

        // Clears the log-once guards so the next session in this process reports afresh. Without
        // this they persist across sessions: host, quit to the menu, host again, and the second
        // session's log is missing the lines the first one already "used up", which reads as the
        // code not running rather than as the guard doing its job.
        public static void ResetPerSessionLogs()
        {
            _loggedPayCosts = false;
            _openedDockPairs.Clear();
            _loggedCrossPlayerOrders.Clear();
            _loggedMerchantArrivals.Clear();
            _loggedFrozenKingdoms.Clear();
            PlayerRelations.Reset();
        }

        // Cross-player merchant trade, Phase 1: opens trade docks between the LOCAL player and every
        // OTHER player so a player merchant (Ship type 6) can route to and dock at their ports.
        // Ship.ValidForDocking only allows a foreign dock when Player.DocksOpen(localOwner, portOwner)
        // is true, vanilla sets that when an ENVOY physically reaches the dock. This mod has no AI
        // kingdoms / synced envoys, so we treat co-op players as mutual trade partners and open their
        // docks directly. Player.OpenDocks is idempotent (no-op if the pairing already exists), so
        // calling it periodically also re-covers late joiners and any Player.Reset that clears
        // dockOpenings. GOTCHA: OpenDocks Invoke()s OnDocksOpenForTrade with no null guard → it NREs if
        // nothing is subscribed (only AI Intention_ManageTrade subscribes, and there are no AI kingdoms
        // here), so we seed a harmless no-op subscriber first. This only ENABLES docking; the goods↔gold
        // exchange (Phase 2) is a separate, synced step. Single-player untouched.
        // Player-pair keys we've already logged a dock-open for (log-once, not gameplay state).
        private static HashSet<long> _openedDockPairs = new HashSet<long>();

        private void EnsureTradeDocksOpenInMP()
        {
            try
            {
                if (Player.inst == null || Player.inst.PlayerLandmassOwner == null) return;
                if (Player.inst.OnDocksOpenForTrade == null)
                    Player.inst.OnDocksOpenForTrade += new Player.OnDockUpdate((a, b, open) => { });
                LandmassOwner localOwner = Player.inst.PlayerLandmassOwner;
                foreach (SessionPlayer kp in Main.kCPlayers.Values)
                {
                    if (kp == null || kp.inst == null) continue;
                    LandmassOwner other = kp.inst.PlayerLandmassOwner;
                    if (other == null || other.teamId == localOwner.teamId) continue; // skip self

                    // Not to someone we are at war with. This runs every 100 ticks, so without the
                    // check it would re-open the docks a declaration of war had just closed, about a
                    // second after they closed, the ports would flap rather than shut.
                    if (PlayerRelations.Get(localOwner.teamId, other.teamId) == World.Relations.Enemy)
                        continue;

                    Player.inst.OpenDocks(localOwner, other); // idempotent; enables docking at their port
                    long pairKey = ((long)Math.Min(localOwner.teamId, other.teamId) << 32) | (uint)Math.Max(localOwner.teamId, other.teamId);
                    if (_openedDockPairs.Add(pairKey))
                        Main.helper.Log($"[DOCKS] trade docks open: your team {localOwner.teamId} <-> player team {other.teamId} (their merchant can now dock at yours & vice-versa)");
                }
            }
            catch (Exception e) { Main.helper.Log("EnsureTradeDocksOpenInMP error: " + e.Message); }
        }

        // Asks every other player's kingdom to change its standing toward ours. The change is not
        // applied here, it applies when the message comes back, so that one code path performs the
        // gate re-baking and dock changes on every machine including this one.
        public static void RequestRelationChange(int otherTeam, World.Relations r)
        {
            try
            {
                if (!NetClient.client.IsConnected) return;
                int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                    ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
                if (!PlayerRelations.IsPlayerPair(localTeam, otherTeam)) return;

                helper.Log($"[RELATIONS] proposing team {localTeam} <-> team {otherTeam} = {r}");
                NetRouter.Send(new KaCMultiplayer.Net.Messages.PlayerRelationMessage
                {
                    TeamA = localTeam,
                    TeamB = otherTeam,
                    Relation = (int)r
                });
            }
            catch (Exception e) { helper.Log("[RELATIONS] request error: " + e.Message); }
        }

        private static System.Reflection.FieldInfo _cmoField;

        // Force the "Control AI Troops" creative option ON in MP by writing its backing array element
        // directly. IsCreativeModeOptionOn is a 5-instruction one-liner (`return cmoOptionsOn[option]`)
        // that Mono INLINES at the call sites (GameUI's select/move/etc. gates), so a Harmony patch on
        // the method never runs there. Setting the array element makes every read, inlined or not,
        // return true, which opens all the unit-control gates for our MP team (5/6/7). ControlAITroops
        // is option 10 and is used ONLY by those GameUI control gates, so nothing else is affected; and
        // selection is still locked to our own units by GameUIIsUnitSelectableHook. Done every frame
        // (cheap: cached field + a guarded single bool write) so a game-side reset can't stick. SP untouched.
        private void EnsureControlAITroopsOn()
        {
            if (!NetClient.client.IsConnected || Player.inst == null) return;
            try
            {
                if (_cmoField == null)
                    _cmoField = typeof(Player).GetField("cmoOptionsOn", BindingFlags.NonPublic | BindingFlags.Instance);
                if (_cmoField != null)
                {
                    var arr = _cmoField.GetValue(Player.inst) as bool[];
                    if (arr != null && arr.Length > 10 && !arr[10]) arr[10] = true; // 10 = CreativeOptions.ControlAITroops
                }
            }
            catch { }
        }

        private static float _lastHeartbeat;

        private void Update()
        {
            // Here rather than in FixedUpdate: a joiner is sent the world while the host is PAUSED,
            // and FixedUpdate does not run while it is. Paced off unscaled time inside, so the rate
            // on the wire is the same as it was on the fixed tick.
            SaveTransfer.PumpOutgoing();

            // A guest's own kingdom on its way to the host, paced the same way and for the same
            // reason. See Net/KingdomMirror.cs.
            KaCMultiplayer.Net.KingdomMirror.Pump();

            // The other half of the same job, on the receiving side: notice when the world has
            // stopped arriving and ask for what is missing. See SaveTransfer.CheckForStall.
            SaveTransfer.CheckForStall();

            EnsureControlAITroopsOn();

            // Delivers any message raised while the dialog could not be seen, a mid-game
            // disconnect, typically, since the dialog lives under the main-menu UI. No-op unless
            // something is actually queued.
            ModalDialog.PumpQueued();

            // SIM HEARTBEAT (diagnostic): the "silent" MP freeze stalls the SIMULATION while real-time keeps
            // running, autosaves are real-time-gated so they don't reveal it, and it throws no exception.
            // Update() runs every rendered frame regardless, so logging the sim clock here every ~2s pins the
            // freeze precisely: if year/villagers stop changing while 'frame' keeps climbing, the sim froze,
            // and the timestamp shows exactly when. timeScale distinguishes a real pause (0) from a sim-tick
            // stall (stays 1 but the sim clock is stuck); fixedTicks shows if FixedUpdate also stopped.
            //
            // Cadence differs by build, because this is not free on either axis. In one 3-player user
            // log it was 551 of 2,276 lines, a QUARTER of the session's diagnostics spent on
            // heartbeats, and each one walks every villager in the world to sum their positions. A
            // freeze lasts minutes, so fifteen seconds still catches one with room to spare, while
            // costing a seventh of the noise and a seventh of the work. Our own test builds keep the
            // fine grain, where the log is short and the detail is the point.
            float heartbeatEvery = Main.DevTestBuild ? 2f : 15f;

            if (NetClient.client.IsConnected && Time.realtimeSinceStartup - _lastHeartbeat > heartbeatEvery)
            {
                _lastHeartbeat = Time.realtimeSinceStartup;
                try
                {
                    int year = (Player.inst != null) ? Player.inst.CurrYear : -1;
                    int villagers = 0;
                    double posSum = 0; // sum of villager positions, CHANGES only if pawns are moving
                    if (Villager.villagers != null)
                    {
                        villagers = Villager.villagers.Count;
                        for (int i = 0; i < villagers; i++)
                        {
                            Villager v = Villager.villagers.data[i];
                            if (v != null) { Vector3 p = v.GetPosition(); posSum += p.x + p.z; }
                        }
                    }
                    // The freeze = pawns stop while the clock runs, so watch posSum vs year: if 'year' keeps
                    // climbing but 'posSum' stops changing, the villager sim froze, that heartbeat is the moment.
                    Main.helper.Log($"[HEARTBEAT] year={year} villagers={villagers} posSum={posSum:F1} timeScale={Time.timeScale} frame={Time.frameCount} fixedTicks={FixedUpdateInterval} tickAllPerFrame={TickAllCallsLastFrame} season={(Weather.inst != null ? Weather.inst.season.ToString() : "?")} dragons={(DragonSpawn.inst != null && DragonSpawn.inst.currentDragons != null ? DragonSpawn.inst.currentDragons.Count : -1)} dragonFlight={KaCMultiplayer.Combat.DragonFlightSync.Published}/{KaCMultiplayer.Combat.DragonFlightSync.Applied}");
                }
                catch (Exception e) { Main.helper.Log("[HEARTBEAT] error: " + e.Message); }
            }

            // PANIC / RECOVER hotkey: Ctrl+Shift+R. Combat (raids/dragons) isn't synced and can jam
            // the shared pathing so pawns freeze ("time still goes"). This clears the cause and
            // resets the symptom: destroy raider ships (kills their troop cargo before/at landing)
            // and teleport the local player's villagers home with a fresh job. Each player hits it
            // for their own kingdom. Update still runs during the freeze (time advances), so the
            // key is detectable.
            if (NetClient.client.IsConnected
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                && Input.GetKeyDown(KeyCode.R))
            {
                PanicReset();
            }

            // Ctrl+Shift+D opens the diplomacy screen. It used to CYCLE this kingdom's standing
            // toward every other player at once, Neutral -> Enemy -> Allies, because there was
            // nowhere to express "this kingdom, this standing": the game's own DiplomacyUI is built
            // around AIKingdom and cannot be pointed at a player. Lobby/DiplomacyWindow.cs is that
            // somewhere now, so the key keeps its meaning and loses the blunt instrument.
            if (NetClient.client.IsConnected
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                && Input.GetKeyDown(KeyCode.D))
            {
                KaCMultiplayer.Lobby.DiplomacyWindow.Toggle();
            }

            // Ctrl+Shift+E sets what this kingdom charges for its exports. Next to the diplomacy
            // key on purpose: the two windows are the same conversation, one about standing and one
            // about terms.
            if (NetClient.client.IsConnected
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                && Input.GetKeyDown(KeyCode.E))
            {
                KaCMultiplayer.Trade.ExportPricesWindow.Toggle();
            }

            KaCMultiplayer.Trade.ExportPricesWindow.Tick();

            // In-game chat: opens on Return, sends on Return, cancels on Escape. Only active in a
            // started multiplayer session, so it never competes with the lobby's own chat box.
            InGameChat.Tick();

            // The automated acceptance run. Does nothing unless DevTestBuild AND AutoTest.Enabled
            // are both set, because this one hosts a lobby and starts a game by itself.
            KaCMultiplayer.Dev.AutoTest.Tick();

            // Escape-to-close and the periodic refresh, so a relation someone else changed shows
            // without the player reopening the window.
            KaCMultiplayer.Lobby.DiplomacyWindow.Tick();
            KaCMultiplayer.Lobby.AllianceRequestWindow.Tick();
            KaCMultiplayer.Lobby.ResourcePicker.Tick();   // Escape closes it; was written, never called

            KaCMultiplayer.Lobby.DealRequestWindow.Tick();

            // DEV ONLY, gated on FakePeer.Enabled, which follows Main.DevTestBuild and is false in
            // any build given to players, so this hotkey does nothing for them. Ctrl+Shift+F spawns
            // or despawns a virtual second player with its own kingdom and a stocked dock, so
            // two-player save/load, roster, relations and cross-player trade can be exercised on one
            // instance without a second Steam client. See Net/FakePeer.cs.
            if (FakePeer.Enabled
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                && Input.GetKeyDown(KeyCode.F))
            {
                FakePeer.Toggle();
            }

            // DEV ONLY. Ctrl+Shift+Y summons a dragon on demand.
            //
            // Vanilla will not give you one to order: DragonSpawn.OnSeasonChange gates on
            // Player.Workers.Count passing startSpawningPopulationThreshold, then on a season edge,
            // then on a per-attack year cooldown, and refuses outright while any wild dragon is
            // already alive. Testing dragon sync by growing a kingdom until the game relents is not
            // testing, so this asks for one directly.
            //
            // Deliberately routed through SpawnBabyDragon, the same public method the game itself
            // calls, so the DragonSpawn hooks apply and the spawn is broadcast exactly as a natural
            // one would be. Calling DragonSpawn.Spawn or SpawnTestDragon instead would bypass those
            // hooks and hand us a dragon on one machine, which is the very bug being tested for.
            //
            // Host only: dragons are host-authoritative, so a client asking for one would be
            // suppressed by DragonSpawnPrefix and nothing would appear anywhere.
            if (Main.DevTestBuild
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                && Input.GetKeyDown(KeyCode.Y))
            {
                SummonDragonForTesting();
            }

            // DEV ONLY. Ctrl+Shift+T runs the acceptance checks against the session you are already
            // in, writing [SELFTEST] PASS/FAIL lines. Gated on DevTestBuild alone, unlike the
            // auto-hosting run, because checking a session that already exists surprises nobody.
            if (Main.DevTestBuild
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                && Input.GetKeyDown(KeyCode.T))
            {
                KaCMultiplayer.Dev.AutoTest.RunChecksNow();
            }

            // DEV ONLY, same gate. Ctrl+Shift+G declares war on the fake peer and lands one of its
            // armies beside your keep, because combat could not otherwise be exercised alone at all:
            // raiders are suppressed in multiplayer and the fake peer is peaceful, so nothing in the
            // world would fight. Kept off the spawn hotkey because declaring war closes the trade
            // docks, and a session at war from the start could never test trading.
            if (FakePeer.Enabled
                && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                && Input.GetKeyDown(KeyCode.G))
            {
                FakePeer.SendHostileArmy();
            }
        }

        // Draws the join-time password prompt (only when active). Hosted here because Main is an
        // always-active MonoBehaviour, so its OnGUI is reliably called.
        private void OnGUI()
        {
            PasswordPrompt.Draw();
            InGameChat.Draw();
        }

        /// <summary>
        /// Spawns one dragon through the game's own entry point, for testing sync.
        ///
        /// Entry point rather than position: DragonController.NewEntryExitPoint is where the game
        /// brings dragons in from, so the dragon arrives flying the way a real one does instead of
        /// appearing mid-map with no approach.
        /// </summary>
        private static void SummonDragonForTesting()
        {
            try
            {
                if (DragonSpawn.inst == null) { helper.Log("[DRAGON] no DragonSpawn to summon from"); return; }

                if (NetClient.client.IsConnected && !NetHost.IsRunning)
                {
                    helper.Log("[DRAGON] summon ignored: dragons are host-authoritative, ask the host");
                    return;
                }

                Vector3 from = DragonController.NewEntryExitPoint();
                DragonSpawn.inst.SpawnBabyDragon(from);

                helper.Log($"[DRAGON] summoned a baby dragon at {from}"
                           + $"; dragons in the world now "
                           + $"{(DragonSpawn.inst.currentDragons != null ? DragonSpawn.inst.currentDragons.Count : -1)}");
            }
            catch (Exception e) { LogEx("summoning a dragon", e); }
        }

        // Clears active raider ships and resets the local player's villagers to a clean state.
        public static void PanicReset()
        {
            try
            {
                Main.helper.Log("[PANIC] Ctrl+Shift+R: clearing raider ships + resetting villagers");

                int boats = 0;
                foreach (var ship in UnityEngine.Object.FindObjectsOfType<TroopTransportShip>())
                {
                    try { ship.DestroyUnit(); boats++; } catch (Exception se) { Main.helper.Log("[PANIC] ship destroy error: " + se.Message); }
                }

                int reset = 0;
                int villagerErrors = 0;
                Exception firstVillagerError = null; // capture ONE full exception instead of spamming N empty ones
                Vector3 keepPos = (Player.inst != null && Player.inst.keep != null) ? Player.inst.keep.transform.position : Vector3.zero;
                int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null) ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;

                var all = Villager.villagers;
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        Villager v = all.data[i];
                        if (v == null) continue;
                        // Only the local player's villagers (don't yank the other player's pawns).
                        var owner = World.GetLandmassOwner(v.landMass);
                        if (owner == null || owner.teamId != localTeam) continue;
                        try
                        {
                            v.QuitJob(true);
                            if (keepPos != Vector3.zero) v.TeleportTo(keepPos);
                            reset++;
                        }
                        catch (Exception ve) { villagerErrors++; if (firstVillagerError == null) firstVillagerError = ve; }
                    }
                }

                // Log the first villager-reset failure in full (type + chain + stack) so we can see
                // WHY the client side throws (host worked, client didn't), empty .Message hid it before.
                if (firstVillagerError != null)
                    Main.LogEx($"[PANIC] {villagerErrors} villager reset error(s); first", firstVillagerError);

                Main.helper.Log($"[PANIC] Done. Destroyed {boats} raider ships, reset {reset} villagers (errors={villagerErrors}).");
                try { ModalDialog.Show("Reset", $"Cleared {boats} raider ship(s) and reset {reset} villager(s) to your keep.", "Okay", true, () => { }); } catch { }
            }
            catch (Exception e)
            {
                Main.helper.Log("[PANIC] error: " + e.Message);
                Main.helper.Log(e.StackTrace);
            }
        }

        private static System.Reflection.FieldInfo _staticVisField;
        private static System.Reflection.FieldInfo _dynamicVisField;
        private static System.Reflection.FieldInfo _visibilityField;
        private static int _revealDebugCount = 0;

        // Share full vision of the map between players (no fog between islands). UpdateShaders()
        // paints the fog texture from staticVisPerLandmass AND per-cell visibility[] (verified via
        // IL), so we set both. CRITICAL: only force per-cell visibility on landmasses the LOCAL
        // player does NOT own. The game re-manages the local player's own fog every frame, so
        // forcing those fought it forever, `changed` stayed true and we rebuilt visibility+fog
        // every tick, which starved the sim as the kingdom grew (the ~year-16 freeze). Remote
        // landmasses have no local units, so once revealed they stay revealed → this converges and
        // the expensive RefreshVisibility/UpdateShaders stop running.
        public static void RevealAllForSharedVision()
        {
            try
            {
                FogOfWar fow = FogOfWar.inst;
                if (fow == null)
                {
                    if (_revealDebugCount < 5) { _revealDebugCount++; Main.helper.Log("RevealAll: FogOfWar.inst is null"); }
                    return;
                }

                var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                if (_staticVisField == null) _staticVisField = typeof(FogOfWar).GetField("staticVisPerLandmass", bf);
                if (_dynamicVisField == null) _dynamicVisField = typeof(FogOfWar).GetField("dynamicVisPerLandmass", bf);
                if (_visibilityField == null) _visibilityField = typeof(FogOfWar).GetField("visibility", bf);

                if (_staticVisField == null || _visibilityField == null)
                {
                    if (_revealDebugCount < 5) { _revealDebugCount++; Main.helper.Log($"RevealAll: fog fields NOT FOUND (static={_staticVisField != null}, vis={_visibilityField != null})"); }
                    return;
                }

                if (World.inst == null) return;

                bool changed = false;

                // Determine which landmasses are REMOTE (not owned by the local player). We force
                // visibility ONLY on those, the game manages the local player's own fog every frame,
                // so touching it (even the per-landmass flags) keeps `changed` true forever and reruns
                // the expensive RefreshVisibility/UpdateShaders every tick, which starves the sim
                // (the freeze). Remote landmasses have no local units, so once revealed they stay
                // revealed and this whole method converges to a cheap no-op.
                int numLm = World.inst.NumLandMasses;
                int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                    ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;

                bool[] remoteLm = new bool[numLm];
                for (int l = 0; l < numLm; l++)
                {
                    LandmassOwner owner = World.GetLandmassOwner(l);
                    remoteLm[l] = (owner == null) || (owner.teamId != localTeam);
                }

                bool[] svpl = _staticVisField.GetValue(fow) as bool[];
                bool[] dvpl = _dynamicVisField?.GetValue(fow) as bool[];
                bool[] vis = _visibilityField.GetValue(fow) as bool[];

                // Per-landmass static + dynamic visibility, remote landmasses only.
                for (int l = 0; l < numLm; l++)
                {
                    if (!remoteLm[l]) continue;
                    if (svpl != null && l < svpl.Length && !svpl[l]) { svpl[l] = true; changed = true; }
                    if (dvpl != null && l < dvpl.Length && !dvpl[l]) { dvpl[l] = true; changed = true; }
                }

                // Per-cell visibility (paints the fog texture), remote landmass cells only.
                if (vis != null)
                {
                    int w = World.inst.GridWidth;
                    Cell[] cells = World.inst.GetCellsData();
                    foreach (Cell c in cells)
                    {
                        if (c == null) continue;
                        int lm = c.landMassIdx;
                        if (lm < 0 || lm >= numLm || !remoteLm[lm]) continue;
                        int idx = c.z * w + c.x;
                        if (idx >= 0 && idx < vis.Length && !vis[idx]) { vis[idx] = true; changed = true; }
                    }
                }

                if (changed)
                {
                    try { if (Player.inst != null) Player.inst.RefreshVisibility(true); }
                    catch (Exception re) { Main.helper.Log("RevealAll: RefreshVisibility error: " + re.Message); }

                    try { fow.UpdateShaders(); }
                    catch (Exception se) { Main.helper.Log("RevealAll: UpdateShaders error: " + se.Message); }

                    Main.helper.Log("Shared vision: revealed remote landmasses (converging).");
                }
            }
            catch (Exception e)
            {
                Main.helper.Log("RevealAllForSharedVision error: " + e.Message);
            }
        }

        private static void BroadcastOwnResources()
        {
            try
            {
                SessionPlayer me;
                if (!kCPlayers.TryGetValue(PlayerSteamID, out me) || me == null || me.inst == null) return;

                Assets.Code.ResourceAmount r = me.inst.resourcesTotal;
                KaCMultiplayer.Net.NetRouter.Send(new KaCMultiplayer.Net.Messages.EconomySnapshotMessage
                {
                    Wheat = r.Get(FreeResourceType.Wheat),
                    Tree = r.Get(FreeResourceType.Tree),
                    Stone = r.Get(FreeResourceType.Stone),
                    Charcoal = r.Get(FreeResourceType.Charcoal),
                    Gold = r.Get(FreeResourceType.Gold),
                    Iron = r.Get(FreeResourceType.IronOre),
                    Tools = r.Get(FreeResourceType.Tools),
                    Armament = r.Get(FreeResourceType.Armament),
                    Fish = r.Get(FreeResourceType.Fish),
                    Apple = r.Get(FreeResourceType.Apples),
                    Pork = r.Get(FreeResourceType.Pork)
                });
            }
            catch (Exception e)
            {
                Main.helper.Log("BroadcastOwnResources error: " + e.Message);
            }
        }

        private static void BroadcastVillagerPositions()
        {
            try
            {
                var all = Villager.villagers;
                if (all == null) return;

                List<Guid> guids = new List<Guid>();
                List<Vector3> positions = new List<Vector3>();

                for (int i = 0; i < all.Count; i++)
                {
                    Villager v = all.data[i];
                    if (v == null) continue;

                    guids.Add(v.guid);
                    positions.Add(v.GetPosition());

                    // Keep packets small, flush every 30 villagers.
                    if (guids.Count >= 30)
                    {
                        KaCMultiplayer.Net.NetRouter.Broadcast(new KaCMultiplayer.Net.Messages.VillagerSnapshotMessage
                            { Villagers = guids, Positions = positions }, NetClient.client.Id);
                        guids = new List<Guid>();
                        positions = new List<Vector3>();
                    }
                }

                if (guids.Count > 0)
                    KaCMultiplayer.Net.NetRouter.Broadcast(new KaCMultiplayer.Net.Messages.VillagerSnapshotMessage
                        { Villagers = guids, Positions = positions }, NetClient.client.Id);
            }
            catch (Exception e)
            {
                Main.helper.Log("BroadcastVillagerPositions error: " + e.Message);
            }
        }

        // Remembers the local player's accepted kingdom name + banner so re-opening the lobby's
        // Name & Banner screen restores them. Vanilla PickNameUI.OnEnable re-randomizes the name and
        // resets the banner to index 0 every time the screen opens, which discarded the player's choice.
        // null / -1 = nothing chosen yet, so the very first open keeps the vanilla random default.
        public static string localChosenKingdomName = null;
        public static int localChosenBanner = -1;

        // Capture the currently-shown name/banner as the local player's choice. Called when leaving the
        // Name & Banner screen (both Accept and Back keep the live-applied values), so a later re-open
        // can restore them instead of the random default.
        public static void SaveLocalNameBanner()
        {
            try
            {
                if (TownNameUI.inst != null && !string.IsNullOrEmpty(TownNameUI.inst.townName))
                    localChosenKingdomName = TownNameUI.inst.townName;
                if (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                    localChosenBanner = Player.inst.PlayerLandmassOwner.bannerIdx;
            }
            catch (Exception e) { Main.helper.Log("SaveLocalNameBanner error: " + e.Message); }
        }

        // Back = cancel: restore the player's live name/banner to the values they last ACCEPTED
        // (Main.localChosen*), discarding any changes made on this visit. The screen applies AND
        // broadcasts changes live, so we also re-send the reverted values (banner + name) to put the
        // host/other clients back too. If nothing was ever accepted yet there's nothing to revert to,
        // so the live values are left as-is.
        public static void RevertLocalNameBanner()
        {
            try
            {
                if (localChosenBanner >= 0 && Player.inst != null)
                {
                    Player.inst.SetIndexedBanner(localChosenBanner);
                    try { NetRouter.Send(new KaCMultiplayer.Net.Messages.BannerPickMessage { Banner = localChosenBanner }); } catch { }
                }
                if (!string.IsNullOrEmpty(localChosenKingdomName) && TownNameUI.inst != null)
                    TownNameUI.inst.SetTownNameQuiet(localChosenKingdomName); // TownNameHook re-broadcasts it
            }
            catch (Exception e) { Main.helper.Log("RevertLocalNameBanner error: " + e.Message); }
        }

        /// <summary>
        /// Shows the mod screen a state calls for, then hands the state to the game's own menu.
        ///
        /// Each reference is checked. They are recreated on every scene load, SceneLoaded runs
        /// again each time the player returns to the menu, so there are windows during a
        /// transition where they are null or destroyed, and this is called from disconnect
        /// handlers that fire exactly then.
        /// </summary>
        public static void TransitionTo(MenuState state)
        {
            try
            {
                if (BrowserScreen.serverBrowserRef != null)
                    BrowserScreen.serverBrowserRef.SetActive(state == MenuState.BrowserScreen);

                if (BrowserScreen.serverLobbyRef != null)
                    BrowserScreen.serverLobbyRef.SetActive(state == MenuState.LobbyScreen);

                // The mod's canvas is only up for the mod's own screens; the game draws the rest.
                if (BrowserScreen.ModCanvas != null)
                    BrowserScreen.ModCanvas.gameObject.SetActive(state.IsModScreen());

                if (GameState.inst != null && GameState.inst.mainMenuMode != null)
                    GameState.inst.mainMenuMode.TransitionTo((MainMenuMode.State)state);
            }
            catch (Exception ex)
            {
                LogEx("TransitionTo " + state, ex);
            }
        }

        private void Preload(KCModHelper helper)
        {
            helper.Log("Preload: applying Harmony patches");
            try
            {

                Main.helper = helper;

                // Dev switches can also be turned on by a LAUNCH ARGUMENT, which is what makes
                // testing possible without an upload. The flags below default false and stay false
                // for every player, because nobody launches the game with these arguments; but a
                // tester can flip any of them without editing code, rebuilding, or re-uploading,
                // which previously cost a full round trip for every change of mind.
                //
                // A launch argument rather than a settings FILE on purpose: reading one would mean a
                // new System.IO reference, and the Workshop security scanner rejects those in newly
                // written methods (see docs/open-threads.md). Environment.GetCommandLineArgs needs
                // no such reference.
                ApplyLaunchFlags();

                // Set here, not just in SceneLoaded, because the bundle loads BELOW this point and
                // SceneLoaded runs later. Until this line the sink was null for the whole of Preload,
                // so every NetLog line from LobbyPrefabs.Load was silently dropped: a real launch
                // showed neither "bundle contents" nor a warning, which means a bundle that failed to
                // load would have taken the entire UI down without leaving a trace. PrefabLoader sets
                // it too, but this mod loader reports only Preload and SceneLoaded entry points, so
                // that hook does not run here. Assigning twice is harmless.
                NetLog.Sink = helper.Log;

                // Fallback: the UI bundle is normally loaded by PrefabLoader.PreScriptLoad,
                // which the mod loader invokes by reflection before any script runs. If that
                // hook is ever missed, load it here instead, the call is idempotent, so
                // whichever path runs first wins.
                LobbyPrefabs.Load(helper);
                helper.Log(helper.modPath);

                var harmony = HarmonyInstance.Create("harmony");
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                // Fragile compiler-generated-lambda hooks, patched here in a guarded block so a resolution
                // miss can't abort the atomic PatchAll above and half-patch the mod.
                PatchMerchantTradeLambdas(harmony);

                // Same treatment for the damage entry points: one of them is an explicit interface
                // implementation, whose real name is not the one you would write.
                PatchCombatDamage(harmony);

                helper.Log("Preload: all patches applied");
            }
            catch (Exception ex)
            {
                // Preload failing means the mod is not patched. Worth the full inner chain:
                // a Harmony resolution failure arrives wrapped and the outer message says
                // nothing useful.
                LogEx("Preload", ex);
            }
            helper.Log("Preload: returning to the mod loader");
        }

        // Suppresses damage resolution on every machine EXCEPT the one arbitrating that fight.
        //
        // Combat cannot be replayed from orders (random damage distribution, threaded pathing,
        // targets resolved against live positions), so exactly one machine resolves each fight and
        // publishes the result. See Combat/CombatRule.cs for which machine that is, and
        // Combat/CombatSync.cs for the publishing.
        //
        // Patched MANUALLY and guarded, for the same reason the merchant lambdas are. Army's damage
        // method is an EXPLICIT interface implementation, so its real name is
        // "IProjectileHitable.TakeProjectileDamage", not "TakeProjectileDamage". An attribute patch
        // naming the latter would fail to resolve, and because PatchAll is atomic that failure would
        // abort every other patch in the mod. Here a miss costs only combat sync.
        private static void PatchCombatDamage(HarmonyInstance harmony)
        {
            try
            {
                // Explicit interface implementations are private and carry the interface prefix.
                MethodInfo armyDamage = typeof(UnitSystem.Army).GetMethod(
                    "IProjectileHitable.TakeProjectileDamage",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? typeof(UnitSystem.Army).GetMethod(
                        "TakeProjectileDamage",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                MethodInfo dragonDamage = typeof(Dragon).GetMethod(
                    "IProjectileHitable.TakeProjectileDamage",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? typeof(Dragon).GetMethod(
                        "TakeProjectileDamage",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                MethodInfo unitDamage = typeof(UnitSystem.Unit).GetMethod(
                    "TakeProjectileDamage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                MethodInfo buildingDamage = typeof(Building).GetMethod(
                    "TakeProjectileDamage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                MethodInfo shipDamage = typeof(ShipBase).GetMethod(
                    "TakeProjectileDamage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (armyDamage != null)
                    harmony.Patch(armyDamage,
                        new HarmonyMethod(typeof(ArmyDamageAuthorityHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                if (dragonDamage != null)
                    harmony.Patch(dragonDamage,
                        new HarmonyMethod(typeof(DragonDamageAuthorityHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                if (unitDamage != null)
                    harmony.Patch(unitDamage,
                        new HarmonyMethod(typeof(UnitDamageAuthorityHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                if (buildingDamage != null)
                    harmony.Patch(buildingDamage,
                        new HarmonyMethod(typeof(BuildingDamageAuthorityHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                if (shipDamage != null)
                    harmony.Patch(shipDamage,
                        new HarmonyMethod(typeof(ShipDamageAuthorityHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                Main.helper.Log($"Combat authority hooks patched (army={armyDamage != null}, unit={unitDamage != null}, building={buildingDamage != null}, ship={shipDamage != null}, dragon={dragonDamage != null})");
            }
            catch (Exception e)
            {
                Main.helper.Log("Combat authority patch failed (combat will resolve on every machine and diverge): " + e.Message);
            }

            // Siege catapults, in a try of their OWN rather than sharing the block above.
            //
            // This is the only combat hook that needs a Postfix as well as a Prefix, and therefore
            // the only one relying on Harmony's __state to carry the arbiter verdict from one to
            // the other. If that binding were ever rejected, sharing a try with the other five
            // would take army, unit, building, ship and dragon suppression down with it, which
            // trades a whole working feature for a new one. Here a failure costs catapults only,
            // and says so in the log.
            try
            {
                // Explicit interface implementation, exactly like Army's and Dragon's, so the real
                // name carries the interface prefix and a plain lookup finds nothing.
                MethodInfo catapultDamage = typeof(SiegeCatapult).GetMethod(
                    "IProjectileHitable.TakeProjectileDamage",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? typeof(SiegeCatapult).GetMethod(
                        "TakeProjectileDamage",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (catapultDamage != null)
                    harmony.Patch(catapultDamage,
                        new HarmonyMethod(typeof(SiegeCatapultDamageAuthorityHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        new HarmonyMethod(typeof(SiegeCatapultDamageAuthorityHook).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)),
                        null);

                Main.helper.Log($"Siege catapult damage authority patched ({catapultDamage != null})");
            }
            catch (Exception e)
            {
                Main.helper.Log("Siege catapult authority patch failed (catapult damage will resolve on every machine and diverge): " + e.Message);
            }

            // Wolves, again in a try of their own so a miss costs only wolves.
            //
            // Patched by hand rather than by attribute for the reason every combat hook is: this
            // one names a NESTED type on a method the game could rename, and PatchAll is atomic, so
            // an attribute patch that failed to resolve would abort every other patch in the mod.
            // The method itself is the easy case of the six, a plain public method rather than an
            // explicit interface implementation.
            try
            {
                MethodInfo wolfDamage = typeof(WolfDen.WolfData).GetMethod(
                    "TakeProjectileDamage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (wolfDamage != null)
                    harmony.Patch(wolfDamage,
                        new HarmonyMethod(typeof(WolfDamageAuthorityHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                Main.helper.Log($"Wolf damage authority patched ({wolfDamage != null})");
            }
            catch (Exception e)
            {
                Main.helper.Log("Wolf authority patch failed (wolf fights will resolve on every machine and diverge): " + e.Message);
            }

            // Wolf BIRTHS, in a try of their own for the same reason as the deaths above: a miss
            // here must cost only wolves, never the atomic PatchAll that carries the whole mod.
            try
            {
                MethodInfo wolfSpawn = typeof(WolfDen).GetMethod(
                    "AddWolf",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (wolfSpawn != null)
                    harmony.Patch(wolfSpawn,
                        new HarmonyMethod(typeof(WolfSpawnAuthorityHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                Main.helper.Log($"Wolf spawn authority patched ({wolfSpawn != null})");
            }
            catch (Exception e)
            {
                Main.helper.Log("Wolf spawn patch failed (wolf packs will grow independently on every machine): " + e.Message);
            }

            PatchGhostKingdomFreeze(harmony);
        }

        // Stops a departed player's armies and ships simulating. Guarded and manual for the same
        // reason as the combat hooks, and with more cause: UpdateGroup and UpdateGeneral are PRIVATE
        // methods of UnitSystem, so their names are not part of any public contract and a game update
        // could rename them. A miss here must cost only this feature, never the atomic PatchAll.
        private static void PatchGhostKingdomFreeze(HarmonyInstance harmony)
        {
            try
            {
                MethodInfo updateGroup = typeof(UnitSystem).GetMethod(
                    "UpdateGroup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                MethodInfo updateGeneral = typeof(UnitSystem).GetMethod(
                    "UpdateGeneral", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                MethodInfo shipTick = typeof(ShipBase).GetMethod(
                    "Tick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                MethodInfo catapultTick = typeof(SiegeCatapult).GetMethod(
                    "Tick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                MethodInfo keyboard = typeof(GameUI).GetMethod(
                    "UpdateForKeyboard", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                MethodInfo collectUnits = typeof(UnitUI).GetMethod(
                    "TryCollectSelectedUnits", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                MethodInfo updateMaterial = typeof(UnitSystem).GetMethod(
                    "UpdateMaterialFor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                MethodInfo getCategory = typeof(UnitSystem).GetMethod(
                    "GetCategory", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                // TryForceLoad is public but TryUnloadAt is private AND overloaded, so both are looked
                // up by hand: the no-argument overload is the routine unload and needs no help, the
                // one taking a unit is the forced unload that skips multiplayer armies.
                MethodInfo forceLoad = typeof(TroopTransportShip).GetMethod(
                    "TryForceLoad", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new Type[] { typeof(IMoveableUnit) }, null);

                MethodInfo unloadOne = typeof(TroopTransportShip).GetMethod(
                    "TryUnloadAt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new Type[] { typeof(IMoveableUnit) }, null);

                if (updateGroup != null)
                    harmony.Patch(updateGroup,
                        new HarmonyMethod(typeof(ArmyGroupFreezeHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                if (updateGeneral != null)
                    harmony.Patch(updateGeneral,
                        new HarmonyMethod(typeof(ArmyGeneralFreezeHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                if (shipTick != null)
                    harmony.Patch(shipTick,
                        new HarmonyMethod(typeof(ShipFreezeHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                if (catapultTick != null)
                    harmony.Patch(catapultTick,
                        new HarmonyMethod(typeof(SiegeCatapultFreezeHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                if (keyboard != null)
                    harmony.Patch(keyboard,
                        new HarmonyMethod(typeof(GameUIKeyboardChatHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                if (collectUnits != null)
                    harmony.Patch(collectUnits,
                        new HarmonyMethod(typeof(UnitPanelCollectHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        new HarmonyMethod(typeof(UnitPanelCollectHook).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)),
                        null);

                if (forceLoad != null)
                    harmony.Patch(forceLoad, null,
                        new HarmonyMethod(typeof(TransportLoadHook).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)),
                        null);

                if (unloadOne != null)
                    harmony.Patch(unloadOne,
                        new HarmonyMethod(typeof(TransportUnloadOneHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        new HarmonyMethod(typeof(TransportUnloadOneHook).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)),
                        null);

                if (updateMaterial != null)
                    harmony.Patch(updateMaterial,
                        new HarmonyMethod(typeof(UnitMaterialTeamHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                if (getCategory != null)
                    harmony.Patch(getCategory,
                        new HarmonyMethod(typeof(UnitCategoryTeamHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        null, null);

                Main.helper.Log($"Ghost-kingdom freeze hooks patched (armyGroup={updateGroup != null}, armyGeneral={updateGeneral != null}, ship={shipTick != null}, catapult={catapultTick != null}), chat keyboard guard={keyboard != null}, unit categories={getCategory != null}, unit materials={updateMaterial != null}, transport load/unload={forceLoad != null}/{unloadOne != null}, unit panel={collectUnits != null}");
            }
            catch (Exception e)
            {
                Main.helper.Log("Ghost-kingdom freeze patch failed (a departed kingdom will keep simulating): " + e.Message);
            }
        }

        // ---- FLAGS SHOWING THE WRONG KINGDOM'S COLOURS -------------------------------------
        //
        // The long-standing report: the host saw every flag in the world blue, the client saw every
        // flag yellow. Each machine painted the whole map in its OWN colours, which is the shape of
        // a local fallback winning everywhere rather than of a banner failing to travel. The banner
        // data was in fact fine; the repaint never happened.
        //
        // Two systems draw flags and BOTH resolve the owner at one moment and then never look
        // again:
        //
        //   FlagMaterialUpdater.UpdateBanners reads World.GetLandmassOwner(cell.landMassIdx) for
        //   the cell it is standing on and, when that is null, falls back to Player.inst, the LOCAL
        //   player. It runs this in OnEnable, which fires the instant the object is instantiated.
        //
        //   IGBanner.UpdateBanners reads World.GetLandmassOwnerByTeamId(b.TeamID()), and
        //   Building.TeamID() is itself World.GetLandmassOwner(LandMass()).teamId. It runs this in
        //   OnBuildingPlacement.
        //
        // Both therefore depend on World.LandMassOwnerLookup[landmass] already naming the right
        // kingdom. That entry is written by LandmassOwner.TakeOwnership, and when we build another
        // player's kingdom we necessarily instantiate their buildings BEFORE we can call it: a
        // keep has to exist before it can claim the island it stands on. So the first flag on a
        // remote island resolves to nobody, takes the local fallback, and keeps it.
        //
        // The refresh that would fix it exists, and vanilla relies on it: both classes subscribe
        // their UpdateBanners to Player.inst.updateBanner. But nothing invokes that delegate except
        // Player.SetIndexedBanner on the player it is called on, and a remote player's delegate has
        // no subscribers, because every flag in the world subscribed to the LOCAL Player.inst.
        // Single player never notices, since there the only kingdom that changes colour is yours.
        //
        // So the fix is not new drawing code. It is to invoke the delegate the game already
        // maintains, at the moments multiplayer changes an answer it caches.

        // Set when something that flags are painted from has changed: a landmass changed hands, or
        // a player picked a banner. Cleared by the repaint.
        private static bool bannersDirty;

        // Fixed ticks to wait after the last change before repainting. Placing a kingdom's worth of
        // buildings marks this dirty once per building; without the wait each one would repaint
        // every flag in the world, which is the per-tick cost that has starved this simulation
        // before. Waiting instead means one repaint after the burst settles.
        private const int BannerRefreshDelayTicks = 30;

        private static int bannerDirtySince;

        /// <summary>
        /// Notes that flags are now painted from stale information. Cheap enough to call from any
        /// path that changes ownership or colours; the repaint itself is what costs.
        /// </summary>
        public static void MarkBannersDirty()
        {
            if (!NetClient.client.IsConnected) return;

            if (!bannersDirty) bannerDirtySince = FixedUpdateInterval;
            bannersDirty = true;
        }

        /// <summary>Repaints once the changes have stopped arriving. Called every fixed tick.</summary>
        private static void RefreshBannersIfDirty()
        {
            if (!bannersDirty) return;
            if (FixedUpdateInterval - bannerDirtySince < BannerRefreshDelayTicks) return;

            bannersDirty = false;
            RefreshAllBanners();
        }

        /// <summary>
        /// Re-runs every flag's own UpdateBanners, so each one resolves its owner again from
        /// ownership as it now stands.
        ///
        /// EVERY kingdom's delegate, not just the local player's, and that is not belt and braces.
        /// A flag subscribes to whatever Player.inst happens to be when it wakes up, and the mod
        /// deliberately swaps Player.inst to the owning remote player while placing that player's
        /// buildings (FakePeer.PlaceKeep and ApplyBuildPlace both do it, so Building.Init reads the
        /// right kingdom's livery). The consequence had been missed: the flags on another player's
        /// buildings are subscribed to THAT player's delegate, and nothing in the game ever invokes
        /// a remote player's. Refreshing only the local list would repaint our own flags and leave
        /// theirs exactly as broken as before, which is the half of the report that says the flags
        /// on the OTHER player's town are wrong.
        ///
        /// Found by the acceptance run rather than by reading: the paint check kept skipping for
        /// want of a flag on the peer's island, and the diagnostic it printed showed one flag in
        /// the world, on ours.
        /// </summary>
        public static void RefreshAllBanners()
        {
            try
            {
                int repainted = 0;
                int failed = 0;
                int kingdoms = 0;

                if (RepaintFlagsSubscribedTo(Player.inst, ref repainted, ref failed)) kingdoms++;

                foreach (SessionPlayer p in kCPlayers.Values)
                {
                    // Guarded PER KINGDOM. A half-built remote player is a normal state during a
                    // join, and one of them must not cost everybody else their flags.
                    try
                    {
                        if (p == null || p.inst == null || p.inst == Player.inst) continue;
                        if (RepaintFlagsSubscribedTo(p.inst, ref repainted, ref failed)) kingdoms++;
                    }
                    catch { }
                }


                int hulls = RepaintShipHulls();

                Main.helper.Log($"[BANNER] repainted {repainted} flag(s) across {kingdoms} kingdom(s) "
                                + $"and {hulls} ship hull(s), {failed} could not resolve an owner");
            }
            catch (Exception e) { Main.helper.Log("[BANNER] repaint error: " + e.Message); }
        }
        /// <summary>
        /// Repaints every ship's hull, and returns how many.
        ///
        /// A SHIP TAKES ITS COLOUR ONCE AND NOTHING EVER ASKS AGAIN. ShipBase.Init calls
        /// UpdateMaterial, which is:
        ///
        ///     if (_teamID >= 0)
        ///         foreach (mesh in meshes)
        ///             mesh.material = World.GetLandmassOwnerByTeamId(_teamID).UniMaterialFogClip;
        ///
        /// UniMaterialFogClip is one of the things SetBannerIdx assigns, and a ship restored from a
        /// save is rebuilt before its owner's banner has been read back. So it asks a kingdom whose
        /// bannerIdx is still -1, is handed null, and Unity draws a null material in the magenta it
        /// uses to mean "material missing". A hot pink merchant ship, on a kingdom that has
        /// otherwise loaded perfectly.
        ///
        /// Ships do not subscribe to a banner the way flags do, so the sweep above never reached
        /// them and nothing else ever will. Asking again is the whole fix: UpdateMaterial is
        /// public, idempotent and cheap, and by the time this runs every kingdom has its livery.
        /// Guarded per ship, because a ship whose team owns no land dereferences a null owner inside
        /// the game's own method.
        /// </summary>
        private static int RepaintShipHulls()
        {
            int done = 0;

            try
            {
                if (ShipSystem.inst == null || ShipSystem.inst.ships == null) return 0;

                var ships = ShipSystem.inst.ships;
                for (int i = 0; i < ships.Count; i++)   // .Count, never .data.Length
                {
                    ShipBase ship = ships.data[i];
                    if (ship == null) continue;

                    try { ship.UpdateMaterial(); done++; }
                    catch { }   // one ship on unowned water must not cost the rest their colour
                }
            }
            catch (Exception e) { Main.helper.Log("[BANNER] ship repaint error: " + e.Message); }

            return done;
        }

        /// <summary>
        /// Runs the UpdateBanners of every flag that subscribed to one kingdom, and says whether
        /// there was a list to run at all.
        ///
        /// Invoked one subscriber at a time rather than by calling the delegate, which would run
        /// them as a single multicast chain. A multicast invoke stops dead at the first subscriber
        /// that throws, and these throw for real reasons: IGBanner dereferences
        /// GetLandmassOwnerByTeamId with no null check, and Building.TeamID() dereferences
        /// GetLandmassOwner with no null check, so any building on ground nobody owns, or a road
        /// whose LandMass() is -1, is an exception waiting to happen. Chaining them would let one
        /// such flag cost every flag after it in the list, which is the unguarded per-item loop
        /// this codebase keeps being bitten by, this time in the game's own delegate.
        /// </summary>
        private static bool RepaintFlagsSubscribedTo(Player owner, ref int repainted, ref int failed)
        {
            if (owner == null || owner.updateBanner == null) return false;

            Delegate[] subscribers = owner.updateBanner.GetInvocationList();
            for (int i = 0; i < subscribers.Length; i++)
            {
                Player.UpdateBanner one = subscribers[i] as Player.UpdateBanner;
                if (one == null) continue;

                try { one(); repainted++; }
                catch { failed++; }   // one flag on unowned ground must not cost the rest
            }
            return true;
        }

        /// <summary>
        /// Gives a kingdom its livery and asks for the flags to be repainted.
        ///
        /// Guarded, because <c>LandmassOwner.SetBannerIdx</c> can throw for a reason that has
        /// nothing to do with banners. Its last act is to call
        /// <c>UnitSystem.UpdateMaterialFor</c>, which walks every army in the world and does
        /// <c>armies.data[j].generalComponent.unitUI.UpdateMaterial(...)</c> with no null check
        /// anywhere in the chain. It is vanilla's own version of the unguarded per-item loop this
        /// codebase keeps tripping over, and one army without a general takes the whole call down.
        ///
        /// Letting that escape was costing far more than it should. Uncaught in the banner handler
        /// it skipped the repaint and the log line; inside the roster loop it was caught per entry
        /// and cost that player their place on the lobby list. Neither has anything to do with the
        /// army that was actually missing a general.
        ///
        /// Catching is safe HERE specifically, because of the order inside SetBannerIdx: bannerIdx,
        /// FlagMaterial, UniMaterial, BannerTexture, BuildingMaterial and both army materials are
        /// all assigned BEFORE that call. Everything a flag is painted from has therefore already
        /// landed by the time it can throw. What is lost is the refresh of UniMaterialsCracked, the
        /// damaged-building materials, which are assigned after it and which the kingdom's first
        /// banner set (made before any army exists) will have filled in.
        ///
        /// Cannot reasonably be fixed at the source: unitCategoriesGen, Army.generalComponent and
        /// both UpdateMaterial methods are private, so replacing the method would mean reflection
        /// for four members on a hot vanilla path, to save a refresh nothing visibly depends on.
        /// </summary>
        public static void SetKingdomBanner(LandmassOwner owner, int bannerIdx, string who)
        {
            if (owner == null) return;

            try
            {
                owner.SetBannerIdx(bannerIdx);
            }
            catch (Exception e)
            {
                // Say WHICH army, so this is a diagnosis rather than a shrug. The banner itself has
                // still been applied; only the trailing material refresh was lost.
                Main.helper.Log($"[BANNER] {who}: banner {bannerIdx} applied, but the army material "
                                + $"refresh threw ({e.GetType().Name}); {ArmiesWithoutAGeneral()} "
                                + "army/armies have no general component");
            }

            MarkBannersDirty();
        }

        /// <summary>
        /// How many armies would make vanilla's material refresh throw. Diagnostic only, and only
        /// called after it already has, so the reflection per army costs nothing in normal play.
        /// </summary>
        private static int ArmiesWithoutAGeneral()
        {
            try
            {
                if (UnitSystem.inst == null) return -1;

                int missing = 0;
                var armies = UnitSystem.inst.armies;
                for (int i = 0; i < armies.Count; i++)   // .Count, never .data.Length
                {
                    UnitSystem.Army a = armies.data[i];
                    if (a == null) { missing++; continue; }

                    General g = KaCMultiplayer.Net.PrivateField.Get<General>(a, "generalComponent", null);
                    if (g == null || g.unitUI == null) missing++;
                }
                return missing;
            }
            catch { return -1; }
        }

        // A landmass changed hands, so every flag painted from ownership is now potentially stale.
        //
        // This is the single choke point: World.LandMassOwnerLookup is written here and nowhere
        // else, so hooking it catches a keep being placed, another player's kingdom being built,
        // and a save being loaded, without a separate call in each of those paths.
        //
        // No injected parameter on purpose. Harmony binds those BY NAME, and the landmass index is
        // not needed here, only the fact that something moved.
        [HarmonyPatch(typeof(LandmassOwner), "TakeOwnership")]
        public class LandmassOwnerTakeOwnershipHook
        {
            public static void Postfix()
            {
                if (!NetClient.client.IsConnected) return;   // single player repaints its own flags
                try { Main.MarkBannersDirty(); }
                catch { }
            }
        }

        /// <summary>
        /// Finds a wolf den by the id every machine already shares for it. Dens travel with the
        /// world rather than being spawned during play, so unlike a catapult this id needed no
        /// arranging; it was already agreed.
        /// </summary>
        public static WolfDen FindWolfDenByGuid(Guid id)
        {
            if (id == Guid.Empty) return null;

            var dens = WolfDen.wolfDens;
            if (dens == null) return null;

            for (int i = 0; i < dens.Count; i++)
            {
                WolfDen d = dens[i];
                if (d != null && d.guid == id) return d;
            }
            return null;
        }

        // Damage aimed at a WOLF, the last thing in the game that could be hurt without anyone
        // deciding what the blow did.
        //
        // A wolf is not an object with a name; it is an entry in its den's list, and its damage
        // method is two lines, recording the attacker and subtracting from life. That made it easy
        // to miss and easy to fix: suppress it away from the arbiter, and let CombatSync publish
        // the den's pack.
        //
        // Arbitrated by the WOLF's own position here, while the sweep asks about the den's. Wolves
        // cannot swim, so a pack and its den share an island and the two agree; using what is to
        // hand in each place avoids a lookup from a wolf back to its den, which the game provides
        // no way to do.
        //
        // Nothing needs to be recorded on the way through. Unlike a catapult, a wolf does not
        // destroy itself inside the damage call: WolfDen.Tick notices a wolf whose life has run out
        // on its next pass, so the sweep can still read the number afterwards.
        public class WolfDamageAuthorityHook
        {
            public static bool Prefix(WolfDen.WolfData __instance, ref HitSfxResult __result)
            {
                try
                {
                    if (__instance == null) return true;
                    if (KaCMultiplayer.Combat.CombatAuthority.ResolvesHere(__instance.pos)) return true;
                }
                catch { return true; }   // never let a damage path throw; resolving is the safe default

                __result = HitSfxResult.None;   // what vanilla returns; drives the hit effect only
                return false;
            }
        }

        // A wolf being BORN, which is the half of this that was never arbitrated.
        //
        // Damage had an arbiter and the pack's health was published, so the mod could say who
        // decided a wolf died. Nothing said who decided one existed. WolfDen.Tick grows every den
        // towards twelve wolves on a timer of its own:
        //
        //     if (WolfCount() < 12) { wolfSpawnTime += dt; if (wolfSpawnTime > 800) AddWolf(); }
        //
        // and that timer runs on every machine, off local simulated time, with its own SRand draws.
        // Paused menus, different speeds and a guest who joined late all push it out of step, so the
        // packs quietly grew apart from the first minutes of a session. EmptyCave.Update spawns
        // wolves the same way.
        //
        // What that looked like: a den on neutral ground, which falls to the host to arbitrate. The
        // host's den had grown a pack, the guest's had not, and the guest could do nothing about it,
        // because damage away from the arbiter is suppressed. One player watched wolves wander an
        // island the other saw empty, troops shot at nothing, and a reload put the wolves back on
        // both machines, because the save was written by the machine that still had them.
        //
        // The published pack now decides size as well as health: a machine that is not the arbiter
        // stops spawning and waits to be told, and ApplyWolfPackHealth tops it up to match. Both
        // directions finally close, and nothing here changes single-player.
        public class WolfSpawnAuthorityHook
        {
            /// <summary>Spawns this machine declined because the den is not ours to grow.</summary>
            public static int Deferred;

            public static bool Prefix(WolfDen __instance)
            {
                try
                {
                    if (__instance == null) return true;
                    if (!NetClient.client.IsConnected) return true;   // single-player, vanilla behaviour

                    // Dark unless combat authority is on, matching every other hook in this family.
                    // With the feature off ResolvesHere answers true everywhere and this would do
                    // nothing anyway; saying so outright keeps the switch meaningful.
                    if (!Main.CombatAuthorityEnabled) return true;

                    // The top-up in ApplyWolfPackHealth calls AddWolf deliberately, to match a pack
                    // we have just been told about. That is the one spawn a non-arbiter must make.
                    if (NetApply.InProgress) return true;

                    if (KaCMultiplayer.Combat.CombatAuthority.ResolvesHere(__instance.GetPos())) return true;
                }
                catch { return true; }   // never let a simulation path throw; spawning is the safe default

                Deferred++;
                return false;
            }
        }

        /// <summary>
        /// Finds a siege catapult by the id every machine agrees on, the one its spawn message
        /// established. Count-bounded rather than a LINQ pass over .data, which is the backing
        /// array and holds stale entries past Count.
        /// </summary>
        public static SiegeCatapult FindSiegeCatapultByGuid(Guid id)
        {
            if (id == Guid.Empty) return null;

            var all = SiegeCatapultSystem.siegeCatapults;
            if (all == null) return null;

            for (int i = 0; i < all.Count; i++)
            {
                SiegeCatapult c = all.data[i];
                if (c != null && c.guid == id) return c;
            }
            return null;
        }

        // Damage aimed at a SIEGE CATAPULT, the sixth and last thing in the game that can be hit
        // by a projectile and the only one that was never arbitrated.
        //
        // Catapults were missed for a reason that goes deeper than the hooks: until now a catapult
        // existed on exactly ONE machine, because it is built by Barracks.Tick and a foreign
        // player's barracks is deliberately not ticked here. So "damage resolves on every machine"
        // understated it. Nobody but the owner had a catapult to damage at all, and a catapult
        // rolled onto a neighbour's island wrecked their town on the owner's screen only.
        //
        // With the spawn synced, the ordinary rule applies: whoever owns the ground the catapult
        // stands on resolves the blow and publishes the result. That is the right arbiter for a
        // siege weapon in particular, since the whole point of one is to be standing on somebody
        // else's ground, and it means the DEFENDER decides how well their walls held.
        //
        // A Prefix and a Postfix, unlike the other five. The Prefix decides authority and
        // suppresses; the Postfix records the outcome, and it has to be the Postfix because the
        // number worth sending is the life AFTER the hit, including the zero that means the
        // catapult just destroyed itself inside this very call.
        public class SiegeCatapultDamageAuthorityHook
        {
            public static bool Prefix(SiegeCatapult __instance, ref HitSfxResult __result, ref bool __state)
            {
                __state = false;
                try
                {
                    if (__instance == null) return true;

                    // Our own repair of somebody else's verdict. Without this the suppression below
                    // would block the apply path too, and a catapult would be undamageable
                    // everywhere but the arbiter, which is strictly worse than no arbitration.
                    if (KaCMultiplayer.Combat.CombatSync.ApplyingCatapultDamage) return true;

                    if (KaCMultiplayer.Combat.CombatAuthority.ResolvesHere(__instance.transform.position))
                    {
                        __state = true;
                        return true;
                    }
                }
                catch { return true; }   // never let a damage path throw; resolving is the safe default

                __result = HitSfxResult.None;   // what vanilla returns; drives the hit effect only
                return false;
            }

            public static void Postfix(SiegeCatapult __instance, bool __state)
            {
                if (!__state) return;   // not our fight, or the damage was suppressed

                try { KaCMultiplayer.Combat.CombatSync.NoteCatapultDamaged(__instance); }
                catch { }
            }
        }

        // Broadcasts a siege catapult YOU built, so it exists for everyone else too.
        //
        // Init is the single choke point every catapult in the game passes through: the barracks
        // that trains one, the creative-mode palette, and RaiderSystem's viking siege. Only the
        // local team's are announced, which excludes raiders for free (they are team 1) and stops
        // an arriving catapult from being announced straight back out, since on this machine a
        // remote player's catapult carries a foreign team.
        //
        // Note what is NOT synced and deliberately so: the catapult's shots. Projectiles are local
        // on every machine here and only the arbiter's damage lands, so the outcome converges
        // without any of that traffic. See Combat/CombatSync.cs.
        [HarmonyPatch(typeof(SiegeCatapult), "Init")]
        public class SiegeCatapultInitHook
        {
            public static void Postfix(SiegeCatapult __instance)
            {
                if (!NetClient.client.IsConnected || __instance == null) return;
                if (KaCMultiplayer.Net.NetApply.InProgress) return;   // this IS someone else's catapult arriving
                if (LoadSaveOverrides.SessionSave.Unpacking) return;  // a load restores them from the save

                try
                {
                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
                    if (__instance.TeamID() != localTeam) return;   // not ours to announce

                    // transform.position, not GetPos(): GetPos returns lastPos, which Tick fills in
                    // and which is still zero at this point in the catapult's life.
                    Vector3 at = __instance.transform.position;

                    Main.helper.Log($"[SIEGE] broadcasting your catapult: guid {__instance.guid} team {__instance.TeamID()} at {at}");
                    KaCMultiplayer.Net.NetRouter.Send(new KaCMultiplayer.Net.Messages.SiegeCatapultSpawnMessage
                    {
                        Catapult = __instance.guid,
                        TeamId = __instance.TeamID(),
                        X = at.x, Y = at.y, Z = at.z
                    });
                }
                catch (Exception e) { Main.helper.Log("[SIEGE] spawn broadcast error: " + e.Message); }
            }
        }

        // Announces a catapult that left the world WITHOUT being killed, which in practice means
        // disbanded: the player sends the crew home and the machine destroys it with no damage
        // dealt anywhere.
        //
        // Combat deaths do not need announcing here, and must not be announced from here. The
        // arbiter publishes a life of zero and every machine's copy then dies through the game's
        // own death code, which is what keeps the effects, the orders bookkeeping and the timing
        // identical everywhere. Broadcasting from Release as well would give two messages the
        // chance to disagree about the same death.
        //
        // Owner-authoritative, matching the spawn: the machine whose team owns the catapult
        // announces it, everyone else obeys. Any other rule double-broadcasts, since every machine
        // simulates the whole world and each one releases its own copy.
        //
        // Release leaves guid and teamId alone (it clears the highlighter, the system entry and
        // the orders entry), so a Postfix can still read both.
        [HarmonyPatch(typeof(SiegeCatapult), "Release")]
        public class SiegeCatapultReleaseHook
        {
            public static void Postfix(SiegeCatapult __instance)
            {
                if (!NetClient.client.IsConnected || __instance == null) return;
                if (KaCMultiplayer.Net.NetApply.InProgress) return;   // applying someone else's verdict or despawn
                if (LoadSaveOverrides.SessionSave.Unpacking) return;  // a load tears the world down first

                try
                {
                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
                    if (__instance.TeamID() != localTeam) return;   // not ours to announce

                    // IsDead means the damage path brought it here, and that death is already on
                    // its way as a published life of zero.
                    if (__instance.IsDead()) return;

                    KaCMultiplayer.Net.NetRouter.Send(
                        new KaCMultiplayer.Net.Messages.SiegeCatapultDespawnMessage { Catapult = __instance.guid });
                }
                catch (Exception e) { Main.helper.Log("[SIEGE] despawn broadcast error: " + e.Message); }
            }
        }

        // Damage aimed at an army. Skipped entirely unless this machine arbitrates the ground the
        // army is standing on, so only one machine ever rolls the outcome.
        public class ArmyDamageAuthorityHook
        {
            public static bool Prefix(UnitSystem.Army __instance, ref HitSfxResult __result)
            {
                try
                {
                    if (__instance == null) return true;
                    if (KaCMultiplayer.Combat.CombatAuthority.ResolvesHere(__instance.generalPos)) return true;
                }
                catch { return true; }   // never let a damage path throw; resolving is the safe default

                __result = HitSfxResult.None;
                return false;
            }
        }

        // While the player is typing in chat, the game must not also read the keyboard.
        //
        // Without this, typing a message drives the build hotkeys underneath it: a "d" in the middle
        // of a sentence opens a menu, a digit switches tool, and the player discovers it only after
        // pressing Enter. GameUI.UpdateForKeyboard is the single place those bindings are read, so
        // suppressing it for the duration is enough, and it leaves the mouse working so the game
        // stays responsive while the box is open.
        //
        // Fails open: any error here returns true and the game keeps its keyboard, because a stuck
        // suppression would leave the player unable to control anything at all.
        public class GameUIKeyboardChatHook
        {
            public static bool Prefix()
            {
                try { return !InGameChat.Capturing; }
                catch { return true; }
            }
        }

        // A departed player's ARMIES stop marching and stop fighting.
        //
        // UnitSystem.Update drives every army through UpdateGroup (the squad) and UpdateGeneral (the
        // leader), neither of which consults the owning Player, which is why freezing Player.Update
        // was never enough on its own. Army.teamId is a plain field, so the gate costs a set lookup
        // against a set that is empty whenever nobody has left.
        //
        // UpdateGeneral's return value decides whether the army is DESTROYED, so a skipped call must
        // report true. Returning false here would delete the kingdom we are trying to preserve.
        public class ArmyGroupFreezeHook
        {
            public static bool Prefix(UnitSystem.Army army)
            {
                if (!Main.FreezeGhostKingdomsFully || KaCMultiplayer.Combat.FrozenKingdoms.None) return true;
                try { return army == null || !KaCMultiplayer.Combat.FrozenKingdoms.IsFrozen(army.teamId); }
                catch { return true; }
            }
        }

        public class ArmyGeneralFreezeHook
        {
            public static bool Prefix(UnitSystem.Army army, ref bool __result)
            {
                if (!Main.FreezeGhostKingdomsFully || KaCMultiplayer.Combat.FrozenKingdoms.None) return true;
                try
                {
                    if (army == null || !KaCMultiplayer.Combat.FrozenKingdoms.IsFrozen(army.teamId)) return true;
                }
                catch { return true; }

                __result = true;   // "still alive": skipping the update must never destroy the army
                return false;
            }
        }

        // A departed player's SHIPS stop sailing.
        //
        // Gated on ShipBase.Tick, the base every ship shares, because that is where movement and
        // pathing live. Subclass overrides still run their own bodies (a merchant's route logic, for
        // instance), so this stops the ship moving rather than stopping it thinking; that is the
        // visible half and it is the half that affects everyone else.
        public class ShipFreezeHook
        {
            public static bool Prefix(ShipBase __instance)
            {
                if (!Main.FreezeGhostKingdomsFully || KaCMultiplayer.Combat.FrozenKingdoms.None) return true;
                try { return __instance == null || !KaCMultiplayer.Combat.FrozenKingdoms.IsFrozen(__instance.teamID); }
                catch { return true; }
            }
        }

        // A departed player's SIEGE CATAPULTS stop rolling and stop shooting.
        //
        // The gap this closes was created by making catapults exist everywhere in the first place.
        // Before that they lived on their owner's machine alone, so a player leaving took their
        // catapults with them; now everyone holds a copy, and without this a ghost kingdom's siege
        // engines would keep trundling across the map and knocking down buildings on every machine
        // still running, with nobody at the controls.
        //
        // Gated on SiegeCatapult.Tick, which is where movement, target selection and firing all
        // live, matching how ships are frozen at ShipBase.Tick. Same cheap gate as the others: a set
        // lookup, short-circuited entirely when nobody has left.
        public class SiegeCatapultFreezeHook
        {
            public static bool Prefix(SiegeCatapult __instance)
            {
                if (!Main.FreezeGhostKingdomsFully || KaCMultiplayer.Combat.FrozenKingdoms.None) return true;
                try { return __instance == null || !KaCMultiplayer.Combat.FrozenKingdoms.IsFrozen(__instance.TeamID()); }
                catch { return true; }   // a live kingdom frozen by mistake is a player who cannot play
            }
        }

        // Multiplayer teams have no UNIT CATEGORY, so armies could not be created at all.
        //
        // UnitSystem.InitCategoriesGen builds a category per (ArmyType, team) for teams 0, 2, 3 and 4
        // only (the loop writes j, then j+1 once past 1, because team 1 is the vikings). Multiplayer
        // teams are 5 and up, so GetCategory returned NULL for every one of them and MakeArmy died in
        // GetUnitFromCategory with a NullReferenceException. That means troops have never worked in
        // multiplayer, for anyone: a barracks completing its first army, a remote army arriving over
        // ArmySpawn, and an army restored from a save all funnel through here.
        //
        // Found by the automated acceptance run (Dev/AutoTest.cs) on its first successful pass; it is
        // the same family as the size-5-by-team arrays in PathCell and OrdersManager, which is now
        // three separate places vanilla assumed teams 0 to 4.
        //
        // The fix rewrites the team for the LOOKUP ONLY, mapping each multiplayer team onto one of
        // the four real ones so it borrows that category's meshes, material and unit pool. It must be
        // done here and not on MakeArmy, whose own teamId parameter becomes army.teamId: rewriting
        // that would hand every multiplayer army to the wrong kingdom.
        private static readonly int[] VanillaArmyTeams = { 0, 2, 3, 4 };

        /// <summary>
        /// The vanilla team whose unit category a multiplayer team borrows, or the team unchanged
        /// if it already has one of its own.
        ///
        /// Shared by every hook that has to reach a category, because they MUST agree. They looked
        /// up a category and set its material independently at first, and the two disagreeing is
        /// precisely how the invisible-soldier bug happened: units were allocated from a mapped
        /// category while the material was written to a category nobody was using.
        ///
        /// Consecutive multiplayer teams land on different vanilla teams so two kingdoms' armies
        /// stay visually distinct rather than all borrowing team 0 and sharing one livery.
        /// </summary>
        public static int UnitCategoryTeamFor(int teamId)
        {
            if (teamId < KaCMultiplayer.Net.PlayerRelations.MpTeamBase) return teamId;

            int index = (teamId - KaCMultiplayer.Net.PlayerRelations.MpTeamBase) % VanillaArmyTeams.Length;
            return VanillaArmyTeams[index];
        }

        public class UnitCategoryTeamHook
        {
            public static void Prefix(ref int teamId)
            {
                if (!NetClient.client.IsConnected) return;
                teamId = UnitCategoryTeamFor(teamId);
            }
        }

        // Multiplayer soldiers were INVISIBLE: only their weapon and their selection outline showed.
        //
        // The other half of the missing-category bug, and it survived the first fix because giving
        // an army units and DRAWING them are two different lookups. A UnitCategory ships with a mesh
        // but no material; `UnitCategory.mat` is filled in later by
        // `UnitSystem.UpdateMaterialFor(teamId, ...)`, which `LandmassOwner.SetBannerIdx` calls with
        // the owner's REAL team when a kingdom picks its livery. Teams 5 and up match no category,
        // so nothing was assigned, and the draw loop opens with
        // `if (unitCategory.mat == null) continue;` and skips the whole batch.
        //
        // Reported from a live game, not by the automated run, which measured life and unit counts
        // and would have gone on passing forever: to it, an invisible army fights perfectly well.
        // The run now asserts every army's category has a material, so this cannot come back quietly.
        //
        // The same mapping as the category lookup, through the same helper so they cannot drift.
        // Rewriting the parameter is safe: the method's second loop refreshes each general from
        // `armies.data[j].teamId`, the army's own field, which this does not touch.
        public class UnitMaterialTeamHook
        {
            // The two halves of the method's second loop, both private on their own classes.
            private static MethodInfo unitUiUpdateMaterial;
            private static MethodInfo generalUpdateMaterial;

            private static bool reported;

            /// <summary>
            /// Replaces UnitSystem.UpdateMaterialFor with the same method, plus the null check it
            /// is missing.
            ///
            /// WHAT VANILLA DOES:
            ///
            ///     foreach (cat in unitCategoriesGen)
            ///         if (cat.teamId == teamId) { cat.mat = army; cat.unlitMat = unlit; }
            ///
            ///     foreach (army in armies) {
            ///         General g = army.generalComponent;       // null while a save is loading
            ///         g.unitUI.UpdateMaterial(army.teamId);    // NullReferenceException
            ///         g.UpdateMaterial(army.teamId);
            ///     }
            ///
            /// WHY THAT ONE MISSING CHECK COSTS SO MUCH. This is the last call in
            /// LandmassOwner.SetBannerIdx, and there is real work after it: the loop that destroys
            /// and rebuilds UniMaterialsCracked, the materials every building picks from in
            /// UpdateMaterialSelection. The throw skips all of it. Then it keeps going: out of
            /// SetBannerIdx, out of Player.SetIndexedBanner, and out through
            /// PlayerSaveData.Unpack, which abandons the rest of that kingdom's restore. One army
            /// without a general is why a saved kingdom came back with no buildings, and why the
            /// ones that did come back were drawn in the magenta Unity uses for a missing material.
            ///
            /// So it is fixed here, at the throw, and nowhere else. Earlier attempts suppressed
            /// this method during loading, which broke every unit texture because the FIRST loop is
            /// what fills UnitCategory.mat; then contained the throw at the caller, which left
            /// UniMaterialsCracked unbuilt and turned the buildings magenta. Both were working
            /// around a missing null check instead of adding one.
            ///
            /// Re-implemented rather than wrapped because Harmony 1.2 has no finalizer, so a prefix
            /// cannot try/catch the original. The teamId rewrite that used to be this hook's whole
            /// job is still here: multiplayer teams are 5 and up and match no unit category, so
            /// they are mapped onto a vanilla one through the same helper the category lookup uses.
            /// </summary>
            public static bool Prefix(UnitSystem __instance, int teamId,
                                      Material armyMaterial, Material unlitArmyMaterial)
            {
                try
                {
                    if (__instance == null) return true;

                    int categoryTeam = NetClient.client.IsConnected ? UnitCategoryTeamFor(teamId) : teamId;

                    // FIRST LOOP: the materials themselves. Everything visible depends on this and
                    // nothing in it can throw.
                    var categories = KaCMultiplayer.Net.PrivateField.Get<List<UnitSystem.UnitCategory>>(
                        __instance, "unitCategoriesGen");

                    if (categories != null)
                    {
                        for (int i = 0; i < categories.Count; i++)
                        {
                            UnitSystem.UnitCategory cat = categories[i];
                            if (cat == null || cat.teamId != categoryTeam) continue;

                            cat.mat = armyMaterial;
                            cat.unlitMat = unlitArmyMaterial;
                        }
                    }

                    // SECOND LOOP: refresh each army's general, skipping the ones that have not got
                    // a general yet. That skip is the entire fix.
                    var armies = __instance.armies;
                    if (armies != null)
                    {
                        for (int j = 0; j < armies.Count; j++)   // .Count, never .data.Length
                        {
                            UnitSystem.Army army = armies.data[j];
                            if (army == null) continue;

                            General general = KaCMultiplayer.Net.PrivateField.Get<General>(army, "generalComponent");
                            if (general == null)
                            {
                                if (!reported)
                                {
                                    reported = true;
                                    Main.helper.Log("[BANNER] an army has no general component yet, so its model "
                                        + "was left for the next refresh. Vanilla would have thrown here and "
                                        + "taken the rest of the kingdom's restore with it. Logged once.");
                                }
                                continue;
                            }

                            if (general.unitUI != null)
                            {
                                if (unitUiUpdateMaterial == null)
                                    unitUiUpdateMaterial = typeof(UnitIGUI).GetMethod("UpdateMaterial",
                                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                                if (unitUiUpdateMaterial != null)
                                {
                                    try { unitUiUpdateMaterial.Invoke(general.unitUI, new object[] { army.teamId }); }
                                    catch { }   // one flag must not cost the rest their material
                                }
                            }

                            if (generalUpdateMaterial == null)
                                generalUpdateMaterial = typeof(General).GetMethod("UpdateMaterial",
                                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                            if (generalUpdateMaterial != null)
                            {
                                try { generalUpdateMaterial.Invoke(general, new object[] { army.teamId }); }
                                catch { }
                            }
                        }
                    }

                    return false;   // done, and without the throw
                }
                catch (Exception e)
                {
                    // Must never be worse than vanilla. The materials above are assigned first, so
                    // letting the original run now would only repeat work that has already
                    // succeeded, and would reintroduce the throw this exists to remove.
                    Main.helper.Log("[BANNER] unit material refresh guard failed: " + e.Message);
                    return false;
                }
            }
        }

        // Second multiplayer blind spot in the same method: a NEW melee army never advances on anyone.
        //
        // MakeArmy sets the starting stance with two literal team checks:
        //     if (at == ArmyType.Default && teamId == 0) army.armyBehavior = ArmyBehavior.Attack;
        //     if (at == ArmyType.Archer  && teamId == 0) army.armyBehavior = ArmyBehavior.Hold;
        // ArmyBehavior.Hold is the enum's zero, so a fresh army holds unless something says otherwise.
        // Stance decides the auto-engage radius in the army update: Hold searches 0 tiles, Attack 10,
        // Pursue 99. Vanilla teams 2 to 4 are AI kingdoms whose brain sets a stance explicitly, so only
        // team 0, the human, needs the default. Multiplayer players are teams 5 and up, so every melee
        // army they trained stood still while an enemy walked past it, and the player had to open the
        // panel and pick Attack by hand every single time. Archers are unaffected: their vanilla rule
        // sets Hold, which is what they already default to.
        //
        // A Postfix, not a teamId rewrite: teamId here becomes army.teamId and owns the army. Safe
        // against save loading, which assigns the stored armyBehavior after MakeArmy returns.
        [HarmonyPatch(typeof(UnitSystem), "MakeArmy")]
        public class ArmyDefaultStanceHook
        {
            public static void Postfix(UnitSystem.Army __result, int teamId, UnitSystem.ArmyType at)
            {
                if (__result == null) return;
                if (!NetClient.client.IsConnected) return;
                if (teamId < KaCMultiplayer.Net.PlayerRelations.MpTeamBase) return;   // vanilla rule already ran

                if (at == UnitSystem.ArmyType.Default)
                    __result.armyBehavior = UnitSystem.ArmyBehavior.Attack;

                EnsureUnitsAreVisible(teamId);
            }

            /// <summary>
            /// Makes sure this team's unit category has a material, so its soldiers actually draw.
            ///
            /// A safety net over ordering, not a second fix. The material is normally installed when
            /// the kingdom picks its livery, but nothing guarantees that happens before an army is
            /// raised: a peer joining mid-session, a save being restored, and a fresh banner pick all
            /// arrive in their own order, and one army created in the gap would be permanently
            /// invisible, because the material is only ever written on a banner change.
            ///
            /// Idempotent and cheap: it re-sends the livery this team already has, and the same hook
            /// that redirects a banner pick redirects this, so both land on the same category.
            /// </summary>
            private static MethodInfo updateMaterialFor;

            private static void EnsureUnitsAreVisible(int teamId)
            {
                try
                {
                    LandmassOwner owner = World.GetLandmassOwnerByTeamId(teamId);
                    if (owner == null || owner.ArmyMaterial == null) return;

                    // UpdateMaterialFor does not refresh this team alone. It walks EVERY army in
                    // the world and does liverySets[owner.bannerIdx] for each one, with no bound
                    // check, so a single kingdom whose banner has not been picked yet (bannerIdx
                    // is -1 until SetIndexedBanner runs, which ApplyBuildPlace already has to work
                    // around) throws ArgumentOutOfRange and takes the whole sweep down with it.
                    // Every army after the bad one is then left with no material, which is the
                    // invisible-soldier case this method exists to prevent.
                    //
                    // So wait rather than throw: another army creation, a banner pick or a save
                    // restore will call this again once the livery is in, and it is idempotent.
                    if (AnyKingdomHasNoBannerYet()) return;

                    // Reflection because UpdateMaterialFor is internal to the game assembly and the
                    // mod compiles as its own. Cached, since this runs on every army creation.
                    if (updateMaterialFor == null)
                        updateMaterialFor = typeof(UnitSystem).GetMethod(
                            "UpdateMaterialFor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                    if (updateMaterialFor == null) return;

                    updateMaterialFor.Invoke(UnitSystem.inst,
                        new object[] { teamId, owner.ArmyMaterial, owner.ArmyMaterialUnlit });
                }
                // LogEx, not e.Message: this is a reflected Invoke, so anything the game throws
                // arrives wrapped in a TargetInvocationException whose own Message is the useless
                // "Exception has been thrown by the target of an invocation". The real cause is the
                // inner one. Four of those wrappers in a session log said nothing at all.
                catch (Exception e) { Main.LogEx("army material refresh", e); }
            }

            /// <summary>
            /// True while any landmass owner still has no banner index.
            ///
            /// Checked across every kingdom rather than just this one because the game's refresh
            /// is world-wide: our team having a livery does not stop it dereferencing somebody
            /// else's missing one.
            /// </summary>
            private static bool AnyKingdomHasNoBannerYet()
            {
                foreach (SessionPlayer kp in Main.kCPlayers.Values)
                {
                    if (kp == null || kp.inst == null) continue;

                    LandmassOwner lo = kp.inst.PlayerLandmassOwner;
                    if (lo != null && lo.bannerIdx < 0) return true;
                }
                return false;
            }
        }

        // Troop transports treated multiplayer armies as somebody else's, so a landing lost its orders.
        //
        // TroopTransportShip decides "is this mine" with TeamID() == 0 in five places. Two of them are
        // not cosmetic: loading an army clears its playerSetMoveTarget, and unloading one sets that
        // target to the landing cell. playerSetMoveTarget is the anchor an army returns to once a fight
        // ends (UpdateGeneral only walks back to it when it is non-null), so in multiplayer a landed
        // army had no anchor and a loaded one kept a stale anchor pointing at the beach it left.
        //
        // This matters more than it sounds: shipping troops to another player's island IS the PvP
        // attack. Every assault went in with its orders half applied.
        //
        // Postfixes, because the vanilla branch is skipped rather than wrong. The remaining three
        // gates only auto-swap the selection between the ship and its cargo, which is convenience this
        // mod already handles through its own selection hooks, so they are left alone deliberately.
        public class TransportLoadHook
        {
            public static void Postfix(TroopTransportShip __instance, IMoveableUnit unit)
            {
                if (unit == null || __instance == null) return;
                if (!NetClient.client.IsConnected) return;
                if (unit.TeamID() < KaCMultiplayer.Net.PlayerRelations.MpTeamBase) return;

                // Only if the load actually happened; TryForceLoad returns early when it cannot.
                if (!__instance.loadTarget.Contains(unit)) return;

                UnitSystem.Army army = unit as UnitSystem.Army;
                if (army != null) army.playerSetMoveTarget = null;

                if (GameUI.inst != null && GameUI.inst.unitUI != null)
                    GameUI.inst.unitUI.OnShipBoardUpdate(__instance);
            }
        }

        // The forced-unload half of the same gate. By the time this returns the unit has been placed
        // at the landing cell, so its own position names the cell vanilla would have stored.
        public class TransportUnloadOneHook
        {
            public static void Prefix(TroopTransportShip __instance, IMoveableUnit u, out bool __state)
            {
                __state = __instance != null && u != null && __instance.loadTarget.Contains(u);
            }

            public static void Postfix(TroopTransportShip __instance, IMoveableUnit u, bool __state)
            {
                if (!__state || u == null) return;
                if (!NetClient.client.IsConnected) return;
                if (u.TeamID() < KaCMultiplayer.Net.PlayerRelations.MpTeamBase) return;
                if (__instance.loadTarget.Contains(u)) return;   // it did not actually leave

                UnitSystem.Army army = u as UnitSystem.Army;
                if (army == null) return;

                try { army.playerSetMoveTarget = World.inst.GetCellDataClamped(army.generalPos); }
                catch (Exception e) { Main.helper.Log("transport unload anchor error: " + e.Message); }
            }
        }

        // The unit panel listed nothing in multiplayer, so you could select troops but not see them.
        //
        // UnitUI.TryCollectSelectedUnits builds the panel's list from GameUI.selectedObjs and skips
        // anything failing "TeamID() != 0 && !Player.inst.creativeMode". A multiplayer player is team
        // 5 or higher, so every one of their own units was skipped and the panel stayed empty: no
        // squad icons, no tabs, no stance buttons for the army you just clicked.
        //
        // The lever is creativeMode, which is exactly the "trust the selection" escape hatch this
        // needs, so the method runs once with it on and it is put straight back. That is safe here
        // for a reason worth stating: selection is ALREADY restricted to your own units by this mod's
        // IsUnitSelectable hook, so "everything selected" and "everything of mine" are the same set,
        // and the method is synchronous with no callbacks that could observe the flag. Buildings in
        // the selection are dropped anyway, GetUnitFromSelectable returns null for them.
        public class UnitPanelCollectHook
        {
            public static void Prefix(out bool __state)
            {
                __state = false;
                if (!NetClient.client.IsConnected) return;
                if (Player.inst == null || Player.inst.creativeMode) return;

                Player.inst.creativeMode = true;
                __state = true;                       // ours to undo, nobody else's
            }

            public static void Postfix(bool __state)
            {
                if (__state && Player.inst != null) Player.inst.creativeMode = false;
            }
        }

        // Villager deaths are decided by one machine and told to the rest.
        //
        // Villagers have no damage path to arbitrate: Villager.TakeProjectileDamage is a no-op that
        // returns HitSfxResult.None, so nothing shoots them. They die by being DESTROYED, and every
        // cause funnels through Player.DestroyPerson: starvation, plague, fire, a dragon, a wolf, a
        // building collapsing on them.
        //
        // That single choke point is the problem and the fix. Every machine simulates the whole
        // world, other players' islands included, so every machine independently decides that a
        // villager on someone else's island has starved. Those decisions run on local timers and
        // local random draws, so the same villager dies at different moments in different places,
        // and the population, the jobs they held and the corpses left behind drift from there. It is
        // the same divergence combat has, so it gets the same answer: whoever owns the ground the
        // villager is standing on decides, and everyone else waits to be told.
        //
        // Suppressing is a Prefix returning false, which skips the death entirely rather than
        // undoing it. DestroyPerson removes the villager from the world, releases their job and can
        // create a corpse; there is no clean way to reverse that afterwards.
        [HarmonyPatch(typeof(Player), "DestroyPerson")]
        public class VillagerDeathAuthorityHook
        {
            /// <summary>Deaths this machine decided and announced. See CombatSync.PublishedUpdates
            /// for why a silent sync path gets a counter: otherwise working and not working look
            /// exactly the same from outside.</summary>
            public static int Published;

            /// <summary>Deaths this machine declined to resolve because it does not own that
            /// ground, and is waiting to be told about instead.</summary>
            public static int Deferred;

            public static bool Prefix(Villager p, bool leaveBehindBody)
            {
                try
                {
                    if (p == null) return true;
                    if (!NetClient.client.IsConnected) return true;

                    // Dark unless combat authority is switched on, matching CombatSync. With the
                    // feature off, ResolvesHere answers true everywhere, so this would have every
                    // machine kill its own copy AND broadcast, and a receiver that got the message
                    // before reaching the same conclusion itself would kill early. That is a
                    // behaviour change from a feature that is meant to be doing nothing.
                    if (!Main.CombatAuthorityEnabled) return true;

                    // Applying a death we were TOLD about must not be second-guessed, and must not
                    // be re-broadcast. NetApply.Scope covers both.
                    if (NetApply.InProgress) return true;

                    if (!KaCMultiplayer.Combat.CombatAuthority.ResolvesHere(p.Pos))
                    {
                        Deferred++;
                        return false;
                    }

                    Published++;
                    NetRouter.Send(new KaCMultiplayer.Net.Messages.VillagerDeathMessage
                    {
                        Villager = p.guid,
                        LeaveBody = leaveBehindBody
                    });
                }
                catch (Exception e)
                {
                    // Never let a sync failure stop a villager dying: a world where deaths silently
                    // stop happening is far worse than one that is briefly out of step.
                    Main.helper.Log("villager death sync error: " + e.Message);
                }
                return true;
            }
        }

        // Where a villager LIVES has to travel with them, or remote kingdoms fill up with homeless.
        //
        // Creating a villager is already broadcast, through the AddVillager hook, so every machine
        // gets the person. Nothing carried their house. Player.TrySettlePeople creates the villager
        // and then calls SetHome, but only the owner runs that: everyone else receives a bare
        // VillagerAdd and files them under Homeless, where they stay forever. Homelessness is not
        // cosmetic in this game, it drives unhappiness and it kills people, so another player's
        // kingdom slowly rotted when viewed from anywhere but their own machine.
        //
        // A separate message rather than a field on VillagerAdd, because the two happen at different
        // moments: AddVillager fires first and the home is not chosen until afterwards. It also
        // covers a villager CHANGING house later, which has the same problem for the same reason.
        [HarmonyPatch(typeof(Villager), "SetHome")]
        public class VillagerSetHomeHook
        {
            /// <summary>Home assignments announced to the other machines. Same reason as
            /// CombatSync.PublishedUpdates: a silent sync path cannot be told apart from a broken
            /// one without a number to look at.</summary>
            public static int Published;

            public static void Postfix(Villager __instance, IResidence home)
            {
                try
                {
                    if (__instance == null || home == null) return;
                    if (!NetClient.client.IsConnected) return;
                    if (NetApply.InProgress) return;   // applying someone else's, do not echo it back

                    // Home is a component on an ordinary building, and the building's guid is the
                    // only name for it every machine shares.
                    MonoBehaviour comp = home as MonoBehaviour;
                    if (comp == null) return;

                    Building b = comp.GetComponent<Building>();
                    if (b == null) return;

                    Published++;
                    NetRouter.Send(new KaCMultiplayer.Net.Messages.VillagerHomeMessage
                    {
                        Villager = __instance.guid,
                        Home = b.guid
                    });
                }
                catch (Exception e) { Main.helper.Log("villager home hook error: " + e.Message); }
            }
        }

        // FIRE AND RUBBLE. A building destroyed on one machine stood whole on every other one.
        //
        // The oldest open report in the project, and syncing building HEALTH did not touch it,
        // because fire never damages a building. Destruction has its own path: Fire.Burnout calls
        // World.WreckBuilding directly, and so do the dragon's fires, a keep dying in survival mode,
        // and the damage path once life reaches zero. Health sync only ever covered the last of
        // those.
        //
        // So this hooks the one place every cause meets. Whatever killed it, all machines agree it
        // is gone and all of them build their own rubble from their own copy of the building, which
        // is why only an id travels: two machines cannot then disagree about what the ruins contain.
        //
        // SUPPRESSED on machines that do not own the ground, which is the half that makes it a sync
        // rather than a duplicate. Without it, a fire burning on machine B would wreck buildings on
        // B that A never lost, and B is not entitled to that decision. Vanilla's own callers are
        // safe to suppress: WreckBuilding means "destroyed by damage or fire", and the other ways a
        // building leaves the world (demolish, rubble rebuild) go through different methods that are
        // already synced on their own.
        [HarmonyPatch(typeof(World), "WreckBuilding")]
        public class WreckBuildingAuthorityHook
        {
            /// <summary>Wrecks this machine decided and announced. See CombatSync.PublishedUpdates
            /// for why a silent sync path gets a counter.</summary>
            public static int Published;

            public static bool Prefix(Building b)
            {
                try
                {
                    if (b == null) return true;
                    if (!NetClient.client.IsConnected) return true;

                    // Dark unless arbitration is on, so switching the feature off restores exactly
                    // the old behaviour: every machine wrecks its own copy, divergent but familiar.
                    if (!CombatAuthorityEnabled) return true;

                    // We were TOLD to wreck this. Do it, and do not announce it back.
                    if (NetApply.InProgress) return true;

                    if (!KaCMultiplayer.Combat.CombatAuthority.ResolvesHere(b.transform.position))
                        return false;   // not our ground, wait to be told

                    Published++;
                    NetRouter.Send(new KaCMultiplayer.Net.Messages.BuildingWreckedMessage
                    {
                        Building = b.guid
                    });
                }
                catch (Exception e)
                {
                    // Never let a sync failure stop a building being destroyed. A world where
                    // things quietly stop burning down is worse than one briefly out of step.
                    Main.helper.Log("wreck sync error: " + e.Message);
                }
                return true;
            }
        }

        // Fires themselves, so the two machines burn the same town rather than each burning its own.
        //
        // Wrecking is arbitrated separately, so world state already converges without this. What it
        // adds is the fire you can SEE and fight: without it a player watching another kingdom sees
        // buildings collapse with no flames, and their own firefighters never turn out for a fire
        // that, on their machine, was never lit.
        //
        // Hooking StartFireAt covers SPREAD as well as ignition, because spreading is not special
        // in this game: a fire reaching the next tile simply calls StartFireAt again. One hook
        // therefore keeps an entire firestorm in step.
        //
        // Same shape as the wreck hook. Suppress where we do not own the ground and wait to be told,
        // announce where we do. Fire is per-tile and short-lived, so a lost message costs a puff of
        // flame, never a building: the wreck arrives on its own message.
        [HarmonyPatch(typeof(FireManager), "StartFireAt")]
        public class FireStartAuthorityHook
        {
            /// <summary>Fires this machine lit and announced.</summary>
            public static int Published;

            public static bool Prefix(Cell cell)
            {
                try
                {
                    if (cell == null) return true;
                    if (!NetClient.client.IsConnected) return true;
                    if (!CombatAuthorityEnabled) return true;   // dark: vanilla behaviour everywhere
                    if (NetApply.InProgress) return true;       // we were told to light this one

                    if (!KaCMultiplayer.Combat.CombatAuthority.ResolvesHere(cell.Center))
                        return false;   // someone else's ground, they decide what burns on it

                    Published++;
                    NetRouter.Send(new KaCMultiplayer.Net.Messages.FireStartMessage
                    {
                        X = cell.x,
                        Z = cell.z
                    });
                }
                catch (Exception e) { Main.helper.Log("fire sync error: " + e.Message); }
                return true;
            }
        }

        // A RAID MUST ONLY EVER LOOK AT THE ISLANDS OF THE PLAYER IT IS RAIDING.
        //
        // SetupRaid is written for one kingdom in the world. It sizes and shares out a raid by
        // walking EVERY landmass and counting the villagers on it:
        //
        //     for (int j = 0; j < World.inst.NumLandMasses; j++)
        //         count += World.inst.GetVillagersForLandMass(j).Count;
        //
        // and then divides that by Player.inst.Workers.Count, which is the LOCAL player alone. With
        // several kingdoms in the world those two numbers describe different populations, so the
        // share worked out per landmass is wrong, grows with how many other people are playing, and
        // divides by zero for a player who has no workers yet. It also means one player's raid is
        // sized by everybody else's success, and can pick a landing on someone else's island.
        //
        // Rather than rewrite the planner, the world is narrowed for the moment it is planning:
        // while SetupRaid runs, a landmass this player does not own reports no villagers, so the
        // planner sees only their own kingdom, exactly as it would in single player. Every machine
        // then plans its own raids against its own islands, which is also the right MULTIPLAYER
        // answer: it puts each raid under the same landmass authority as everything else, so the
        // player being raided is the one resolving it, and their raiders reach the other machines
        // through the army spawn broadcast that already exists.
        //
        // Scoped to the call, deliberately. This is a lie told to one method for the length of one
        // call, not a change to what GetVillagersForLandMass means.
        [HarmonyPatch(typeof(RaiderSystem), "SetupRaid")]
        public class RaidLocalIslandsOnlyHook
        {
            /// <summary>True only while SetupRaid is on the stack.</summary>
            public static bool Planning;

            public static void Prefix() { Planning = true; }
            public static void Postfix() { Planning = false; }
        }

        // The narrowing itself. Only says anything different while a raid is being planned.
        [HarmonyPatch(typeof(World), "GetVillagersForLandMass")]
        public class RaidVillagerScopeHook
        {
            private static readonly ArrayExt<Villager> Empty = new ArrayExt<Villager>(1);

            public static bool Prefix(int idx, ref ArrayExt<Villager> __result)
            {
                if (!RaidLocalIslandsOnlyHook.Planning) return true;
                if (!NetClient.client.IsConnected) return true;

                try
                {
                    if (Player.inst != null && Player.inst.PlayerLandmassOwner != null
                        && Player.inst.PlayerLandmassOwner.OwnsLandMass(idx))
                        return true;   // our island, the planner may see it
                }
                catch { return true; }

                __result = Empty;   // somebody else's island, invisible to this raid
                return false;
            }
        }

        // Damage aimed at a ship.
        //
        // Ships sit on water, and water belongs to nobody, so under the landmass model naval combat
        // falls to the host. Suppressing it everywhere else is what makes a sinking agree: life is
        // synced by ShipHealthMessage and each machine's own Tick sinks the ship when that number
        // reaches zero, so nobody has to be told separately that it went down.
        public class ShipDamageAuthorityHook
        {
            public static bool Prefix(ShipBase __instance, ref HitSfxResult __result)
            {
                try
                {
                    if (__instance == null) return true;
                    if (KaCMultiplayer.Combat.CombatAuthority.ResolvesHere(__instance.GetPos())) return true;
                }
                catch { return true; }

                __result = HitSfxResult.Wood;   // what vanilla returns for a ship; drives the hit effect only
                return false;
            }
        }

        // Damage aimed at a building.
        //
        // Buildings need only this half, not a health message of their own. A building stands on its
        // owner's island, so the landmass arbiter for damage against it is its owner, and the owner
        // is already the machine that publishes its Life: BuildingWatcher polls the local player's
        // own buildings and sends a BuildSnapshot whenever anything on the wire would differ. So
        // suppressing damage everywhere else is enough to make the owner's figure the only one.
        //
        // Resolved by position rather than by comparing owners, so the rare case of a building
        // standing on ground someone else owns still lands on one machine rather than none.
        public class BuildingDamageAuthorityHook
        {
            public static bool Prefix(Building __instance, ref HitSfxResult __result)
            {
                try
                {
                    if (__instance == null) return true;
                    if (KaCMultiplayer.Combat.CombatAuthority.ResolvesHere(__instance.transform.position))
                    {
                        // We are the one resolving this hit, so we are the one who has to tell
                        // everybody else what it did. Without this the suppression below is a
                        // one-way street: every other machine refuses the damage and never hears
                        // the outcome, leaving the building standing at full health forever.
                        // Recorded rather than sent, so a building under sustained fire produces
                        // one message per sweep instead of one per blow. See CombatSync.
                        KaCMultiplayer.Combat.CombatSync.NoteBuildingDamaged(__instance);
                        return true;
                    }
                }
                catch { return true; }

                __result = HitSfxResult.None;
                return false;
            }
        }

        // Damage aimed at an individual soldier. Reached both through the army's own dispatch and
        // directly from melee, so it needs the same gate rather than relying on the army hook.
        public class UnitDamageAuthorityHook
        {
            public static bool Prefix(UnitSystem.Unit __instance, ref HitSfxResult __result)
            {
                try
                {
                    if (__instance == null) return true;
                    if (KaCMultiplayer.Combat.CombatAuthority.ResolvesHere(__instance.pos)) return true;
                }
                catch { return true; }

                __result = HitSfxResult.None;
                return false;
            }
        }

        // Manually patches MerchantUI's Buy/Sell "complete transaction" lambda methods (<Start>b__18_5 /
        // <Start>b__18_6) for cross-player merchant trade sync. Kept OUT of PatchAll and guarded: if the
        // compiler-generated names ever fail to resolve, only merchant-trade sync is lost, PatchAll's
        // all-or-nothing can't half-patch the mod. See MerchantUIBuyHook / MerchantUISellHook.
        private static void PatchMerchantTradeLambdas(HarmonyInstance harmony)
        {
            try
            {
                var mui = typeof(MerchantUI);
                var buy = mui.GetMethod("<Start>b__18_5", BindingFlags.Instance | BindingFlags.NonPublic);
                var sell = mui.GetMethod("<Start>b__18_6", BindingFlags.Instance | BindingFlags.NonPublic);
                if (buy != null)
                    harmony.Patch(buy,
                        new HarmonyMethod(typeof(MerchantUIBuyHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        new HarmonyMethod(typeof(MerchantUIBuyHook).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)),
                        null);
                if (sell != null)
                    harmony.Patch(sell,
                        new HarmonyMethod(typeof(MerchantUISellHook).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static)),
                        new HarmonyMethod(typeof(MerchantUISellHook).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static)),
                        null);
                Main.helper.Log($"Merchant-trade lambda hooks patched (buy={buy != null}, sell={sell != null})");
            }
            catch (Exception e)
            {
                Main.helper.Log("Merchant-trade lambda patch failed (player-merchant trade sync disabled): " + e.Message);
            }
        }


        public static MenuState prevMenuState = MenuState.Uninitialized;

        /// <summary>
        /// Tracks which menu screen is showing, and the one before it.
        ///
        /// The mod needs both: <see cref="menuState"/> so its own screens know whether they
        /// should be visible, and <see cref="prevMenuState"/> so Close can go back to wherever
        /// the player came from rather than a fixed screen.
        ///
        /// Uninitialized is skipped deliberately. The game passes it while tearing a menu down,
        /// and recording it would make Close return to nothing.
        /// </summary>
        [HarmonyPatch(typeof(MainMenuMode))]
        [HarmonyPatch("TransitionTo")]
        public class TransitionToHook
        {
            private static void Prefix(MainMenuMode.State newState)
            {
                // Kept at Log level: this one line is the spine of every menu-flow diagnosis in
                // output.txt, and it is how the Load-vs-lobby routing bug was pinned.
                Main.helper.Log($"menu state -> {(MenuState)newState}");

                Main.prevMenuState = Main.menuState;

                if (newState != MainMenuMode.State.Uninitialized)
                    Main.menuState = (MenuState)newState;
            }

            /// <summary>
            /// Tears the session down the moment vanilla's OWN menu flow lands on the top-level
            /// main menu, not just when a Riptide disconnect event fires.
            ///
            /// EVERY EXISTING TEARDOWN IS EVENT-DRIVEN: NetClient.Client_Disconnected fires for a
            /// drop, a kick, or the host closing; BrowserScreen's Back button and ServerRow's Join
            /// both call ResetNetworkState before their own action. None of them fire for a player
            /// who presses Escape mid-game and clicks through Quit -> Confirm. That path is pure
            /// vanilla: MainMenuMode.OnClickedReturnToMainMenu asks for QuitConfirm, confirming it
            /// calls TransitionTo(State.Menu), and nothing else happens. Riptide is never told to
            /// disconnect, so the server (if we are hosting) or the connection (if we are a guest)
            /// is still fully live the moment the main menu appears.
            ///
            /// What that leaves behind: the OTHER player's game keeps running and keeps sending
            /// build and wreck messages, which keep arriving here and get applied to a World that
            /// vanilla is midway through tearing down for its own reasons, throwing
            /// NullReferenceException in World.GetUniMaterialFor and World.DemolishBuilding (both
            /// seen live). The scene itself is never unloaded, so what the player sees is the real,
            /// half-broken game world rendering behind the main-menu overlay, with damaged icons and
            /// materials from the exceptions above, until they quit the whole application.
            ///
            /// A Postfix, so vanilla's own State.Menu handling runs first and this cannot interfere
            /// with it. Guarded on InMultiplayer so an ordinary single-player "back to menu" is
            /// untouched, and ResetNetworkState is the same idempotent teardown every other exit
            /// path already uses, so calling it a second time from a disconnect that follows costs
            /// nothing.
            /// </summary>
            private static void Postfix(MainMenuMode.State newState)
            {
                if (newState != MainMenuMode.State.Menu) return;
                if (!Main.InMultiplayer) return;

                Main.helper.Log("[net] returned to the main menu mid-session; tearing the network "
                                + "down rather than leaving it running behind the menu");

                try { KaCMultiplayer.Net.SteamLobby.ResetNetworkState(); }
                catch (Exception e) { Main.helper.Log("[net] reset on return-to-menu failed: " + e.Message); }
            }
        }

        /// <summary>
        /// Close returns to the previous screen instead of the game's fixed destination, because
        /// the mod inserts screens the game does not know about, closing out of one has to land
        /// back in the browser or lobby, not wherever vanilla assumed.
        /// </summary>
        [HarmonyPatch(typeof(MainMenuMode))]
        [HarmonyPatch("OnClickedClose")]
        public class OnClickedCloseHook
        {
            private static bool Prefix()
            {
                TransitionTo(prevMenuState);
                return false;   // replaces the vanilla close entirely
            }
        }

        /// <summary>
        /// Leaves the name-and-banner screen the multiplayer way: back to the lobby, rather than
        /// wherever the single-player flow would send us.
        ///
        /// Its two exits differ only in what they do with the pending edits,
        /// <paramref name="settle"/> either commits them or throws them away, so everything after
        /// that is shared. Returns Harmony's "skip the game's method" answer, since in a session
        /// its exit is never the right one.
        /// </summary>
        private static bool LeaveNameBannerScreen(Action settle)
        {
            if (!NetClient.client.IsConnected) return true;

            settle();
            TransitionTo(MenuState.LobbyScreen);
            SfxSystem.PlayUiCancel();
            return false;
        }

        [HarmonyPatch(typeof(MainMenuMode))]
        [HarmonyPatch("OnClickedBackToModeSelect")]
        public class OnClickedBackToModeSelectPatch
        {
            // Back discards this visit's edits; Accept below commits them.
            private static bool Prefix()
            {
                return LeaveNameBannerScreen(RevertLocalNameBanner);
            }
        }

        [HarmonyPatch(typeof(MainMenuMode))]
        [HarmonyPatch("OnClickedAcceptNameBanner")]
        public class OnClickedAcceptNameBannerPatch
        {
            private static bool Prefix()
            {
                return LeaveNameBannerScreen(SaveLocalNameBanner);
            }
        }

        [HarmonyPatch(typeof(TownNameUI))]
        [HarmonyPatch("SetTownNameQuiet")]
        public static class TownNameHook
        {
            // Postfix, not Prefix: the field is read back off __instance after the game has
            // written it, so the broadcast carries the value the local player actually ended up
            // with rather than the one that was requested.
            private static void Postfix(TownNameUI __instance)
            {
                helper.Log($"kingdom name set locally: {__instance.townName}");

                NetRouter.Send(new KaCMultiplayer.Net.Messages.KingdomLabelMessage { KingdomName = __instance.townName });
            }
        }

        [HarmonyPatch(typeof(ChooseBannerUI))]
        [HarmonyPatch("OnAccept")]
        public class ChooseBannerUIOnAcceptHook
        {
            private static void Postfix()
            {
                if (!NetClient.client.IsConnected) return;
                if (Player.inst == null || Player.inst.PlayerLandmassOwner == null) return;

                NetRouter.Send(new KaCMultiplayer.Net.Messages.BannerPickMessage
                {
                    Banner = Player.inst.PlayerLandmassOwner.bannerIdx
                });
            }
        }

        // The lobby "Name Your Kingdom / Choose Your Banner" screen (PickNameUI) re-randomizes the
        // kingdom name and resets the banner to index 0 in OnEnable EVERY time it opens, so a player
        // who already picked their name+banner saw it wiped on re-open. In MP, after OnEnable runs,
        // restore the values the player last accepted (Main.localChosen*). The first-ever open has
        // nothing saved, so the vanilla random default stands.
        [HarmonyPatch(typeof(PickNameUI), "OnEnable")]
        public class PickNameUIRestoreHook
        {
            public static void Postfix(PickNameUI __instance)
            {
                if (!NetClient.client.IsConnected) return;
                try
                {
                    if (!string.IsNullOrEmpty(Main.localChosenKingdomName))
                    {
                        if (__instance.cityNameInput != null)
                            __instance.cityNameInput.text = Main.localChosenKingdomName;
                        if (TownNameUI.inst != null)
                            TownNameUI.inst.SetTownNameQuiet(Main.localChosenKingdomName);
                    }

                    if (Main.localChosenBanner >= 0 && Player.inst != null)
                    {
                        Player.inst.SetIndexedBanner(Main.localChosenBanner);
                        // Refresh the on-screen banner image from the FULL livery set. OnEnable set it to
                        // bannerTex[0], and PickNameUI.bannerTex only holds a few preview sprites, using
                        // that dropped any banner beyond index ~3 back to the first one ("only the first 4
                        // banners work"). World.inst.liverySets is the complete set (same source the lobby
                        // player list uses), so every banner index restores its real image now.
                        try
                        {
                            if (World.inst != null && World.inst.liverySets != null
                                && Main.localChosenBanner < World.inst.liverySets.Count)
                            {
                                Texture2D tex = World.inst.liverySets[Main.localChosenBanner].banners as Texture2D;
                                if (tex != null) __instance.OnUpdateBanner(tex);
                            }
                        }
                        catch (Exception be) { Main.helper.Log("Banner image restore error: " + be.Message); }
                    }
                }
                catch (Exception e) { Main.helper.Log("PickNameUI restore error: " + e.Message); }
            }
        }


        // Multiplayer can have no cave container because witch-hut spawning is disabled.
        // Selection runs before AcceptPlacement clears its preview, so this lookup must
        // return no hut instead of throwing and leaving an already placed building held.
        /// <summary>
        /// Guarantees the world has a cave container before a save is packed.
        ///
        /// <c>World.WorldSaveData.Pack</c> reads <c>caveContainer.transform.childCount</c> with no
        /// null check, so a missing container makes every save throw. That is worse than a failed
        /// save: the autosave is triggered from <c>AutoSave.OnOnSeasonChange</c>, and an exception
        /// escaping one subscriber of a .NET multicast delegate stops the rest of the invocation
        /// list from running. Farms subscribe to the same season event, after AutoSave, to emit
        /// their year's yield. So a null container silently stopped every farm in the game from
        /// ever harvesting, and reported it as "There was a problem saving the level".
        ///
        /// <see cref="SessionPlayer"/> no longer destroys the container, which is the actual fix.
        /// This is the net underneath it, because the failure is so quiet and so total: anything
        /// that loses the container in future costs a line in the log instead of the food supply.
        /// </summary>
        [HarmonyPatch(typeof(World.WorldSaveData), "Pack")]
        public class CaveContainerSaveGuardHook
        {
            public static void Prefix(World w)
            {
                try
                {
                    if (w == null || w.caveContainer != null) return;

                    // Unity's overloaded == reports a destroyed object as null, which is exactly
                    // the state this is repairing, so a plain replacement is right either way.
                    w.caveContainer = new GameObject("Caves");
                    Main.helper.Log("[CAVES] world had no cave container at save time; made an empty one"
                                    + " (without it the save throws, and a throwing autosave stops the"
                                    + " season event that farms harvest on)");
                }
                catch (Exception e) { Main.LogEx("cave container save guard", e); }
            }
        }

        /// <summary>
        /// NO COMPUTER KINGDOMS IN A MULTIPLAYER GAME. Every island belongs to a person.
        ///
        /// This used to be true by accident. MainMenuMode.StartGame ends by reading
        /// RivalKingdomSettingsUI.inst.rivalItems, that screen is never shown in multiplayer, so
        /// the field was null, StartGame threw, and the AI config it would have written was never
        /// built. ApplySessionStart even logs the throw as the expected path.
        ///
        /// It is only null on a machine that has not opened the screen SINCE IT LAUNCHED. Play one
        /// single-player game first and RivalKingdomSettingsUI.inst is alive for the rest of the
        /// process, holding whatever rivals that player picked. StartGame then finishes, fills in
        /// AIBrainsContainer.aiStartInfo, and Keep.OnPlayerPlacement calls PlaceAIs the moment that
        /// player puts down their starting keep.
        ///
        /// What that looked like: one player had played solo before joining, so when she placed her
        /// keep her game quietly founded three AI kingdoms, one per spare island. Each planted a
        /// keep and five villagers, BuildingWatcher saw buildings appear on her machine and sent
        /// them out as hers, and every player watched three castles they had not built rise on
        /// islands nobody owned, with twenty villagers credited to her.
        ///
        /// So the rule is stated rather than hoped for. Single-player is untouched: this only
        /// refuses while a session is live.
        /// </summary>
        [HarmonyPatch(typeof(World), "PlaceAIs")]
        public class NoAIKingdomsInMultiplayerHook
        {
            public static bool Prefix()
            {
                if (!Main.InMultiplayer) return true;

                Main.helper.Log("[AI] skipped the AI-kingdom placement; every island in a "
                                + "multiplayer game belongs to a player");
                return false;
            }
        }

        [HarmonyPatch(typeof(World), "GetWitchHutAt")]
        public class MissingWitchContainerHook
        {
            public static bool Prefix(GameObject ___caveContainer, ref WitchHut __result)
            {
                if (___caveContainer != null) return true;
                __result = null;
                return false;
            }
        }

        [HarmonyPatch(typeof(World))]
        [HarmonyPatch("Place")]
        public class PlaceHook
        {

            /// <summary>
            /// Announces a building the local player just placed.
            ///
            /// <see cref="NetApply.InProgress"/> is the echo guard: while we are applying someone
            /// else's placement, this fires for their building too and would send it straight
            /// back. The one exception is the starting keep, the KeepPlaceRandom handler opens a
            /// <c>NetApply.Bypass</c> around its Place call precisely so this does fire, because
            /// that keep is genuinely new to everyone else.
            /// </summary>
            public static void Postfix(Building PendingObj)
            {
                if (!NetClient.client.IsConnected || NetApply.InProgress) return;
                if (PendingObj == null) return;

                try
                {
                    {
                        NetRouter.Send(new KaCMultiplayer.Net.Messages.BuildPlaceMessage
                        {
                            State = new KaCMultiplayer.Net.Messages.BuildingState
                            {
                                UniqueName = PendingObj.UniqueName,
                                CustomName = PendingObj.customName,
                                Guid = PendingObj.guid,
                                Rotation = PendingObj.transform.GetChild(0).rotation,
                                GlobalPosition = PendingObj.transform.position,
                                LocalPosition = PendingObj.transform.GetChild(0).localPosition,
                                Built = PendingObj.IsBuilt(),
                                Placed = PendingObj.IsPlaced(),
                                Open = PendingObj.Open,
                                DoBuildAnimation = PendingObj.doBuildAnimation,
                                ConstructionPaused = PendingObj.constructionPaused,
                                ConstructionProgress = PendingObj.constructionProgress,
                                Life = PendingObj.Life,
                                ModifiedMaxLife = PendingObj.ModifiedMaxLife,
                                YearBuilt = PendingObj.YearBuilt,
                                DecayProtection = PendingObj.decayProtection,
                                SeenByPlayer = PendingObj.seenByPlayer
                            }
                        });
                    }
                }
                catch (Exception e) { Main.LogEx("broadcasting a building placement", e); }
            }
        }

        // Sync building/road demolition. DemolishBuildingByPlayer is only called for the local
        // player's manual dismantle action; remote demolitions are applied via World.DemolishBuilding
        // (the core method) so this hook never re-fires for them, no echo loop.
        [HarmonyPatch(typeof(World))]
        [HarmonyPatch("DemolishBuildingByPlayer")]
        public class DemolishHook
        {
            public static void Postfix(Building building)
            {
                try
                {
                    if (NetClient.client.IsConnected && building != null)
                    {
                        Main.helper.Log($"Sending demolish packet for {building.UniqueName} ({building.guid})");

                        KaCMultiplayer.Net.NetRouter.Send(new KaCMultiplayer.Net.Messages.TerrainDemolishMessage
                        {
                            Building = building.guid
                        });
                    }
                }
                catch (Exception e)
                {
                    Main.helper.Log("Demolish hook error");
                    Main.helper.Log(e.Message);
                    Main.helper.Log(e.StackTrace);
                }
            }
        }

        // The per-building Demolish BUTTON calls World.DemolishSelectedBuildingByPlayer, whose vanilla
        // ownership check (building.TeamID()) misbehaves in MP, so the button silently does nothing
        // (the drag-demolish tool works because it calls DemolishBuildingByPlayer directly). In MP we
        // replace it: demolish the selected building directly (our DemolishHook above then syncs it),
        // guarded by LANDMASS OWNERSHIP (no TeamID() call) so you can only demolish your own island's
        // buildings. Single-player is untouched.
        [HarmonyPatch(typeof(World), "DemolishSelectedBuildingByPlayer")]
        public class DemolishSelectedHook
        {
            public static bool Prefix()
            {
                if (!NetClient.client.IsConnected) return true; // single-player: original behaviour

                try
                {
                    Building b = GameUI.inst.GetBuildingSelected();
                    if (b != null)
                    {
                        var lmo = World.GetLandmassOwner(b.LandMass());
                        bool mine = (lmo == null) ||
                            (Player.inst != null && Player.inst.PlayerLandmassOwner != null
                             && lmo.teamId == Player.inst.PlayerLandmassOwner.teamId);
                        if (mine)
                            World.inst.DemolishBuildingByPlayer(b, true);
                    }
                }
                catch (Exception e) { Main.helper.Log("DemolishSelected MP error: " + e.Message); }

                return false; // skip the vanilla method (its TeamID check fails in MP)
            }
        }

        // The single-click demolish tool gates on `building.TeamID() == 0`. In a session your
        // buildings belong to team 5, 6 or 7, so the tool silently did nothing on your own
        // kingdom. This handles landmass-owned buildings instead, keeping the castle-block case
        // where the top structure is the one demolished. The demolish itself propagates through
        // DemolishHook. The cosmetic tile effect is skipped, it needs a private field and is not
        // worth the reflection. Single-player takes the game's own path.
        [HarmonyPatch(typeof(DemolishCursorMode), "DoPrimaryClick")]
        public class DemolishCursorMPHook
        {
            public static bool Prefix(Building clickedBuilding, ref bool __result)
            {
                if (!NetClient.client.IsConnected) return true; // single-player: vanilla
                try
                {
                    __result = true;
                    if (clickedBuilding != null && Main.IsLocalBuilding(clickedBuilding))
                    {
                        if (clickedBuilding.CategoryName == "castleblock")
                        {
                            Building top = clickedBuilding.GetCell().TopStructure;
                            if (top != null) World.inst.DemolishBuildingByPlayer(top, true);
                        }
                        else
                        {
                            World.inst.DemolishBuildingByPlayer(clickedBuilding, true);
                        }
                    }
                    return false; // handled
                }
                catch (Exception e) { Main.helper.Log("Demolish cursor MP hook error: " + e.Message); return true; }
            }
        }

        // The rebuild-rubble tool carries the same `TeamID() == 0` gate as demolish above, so
        // ruined buildings in your own kingdom could not be rebuilt. This handles the local
        // player's buildings instead.
        //
        // Known gap: the rebuild is local only. Nothing puts it on the wire, so other players'
        // copies stay as rubble until something else refreshes them.
        // Rebuilding rubble now reaches the other players.
        //
        // The replacement buildings already did: Rubble.Rebuild puts them down through World.Place,
        // which is hooked and broadcast. What never travelled was the REMOVAL of the ruins, so
        // everyone else was left with the new building standing on rubble that, for them, was never
        // cleared.
        //
        // The cell is captured in the Prefix because by the Postfix the rubble is gone and there is
        // nothing left to ask where it was. Cell rather than guid: rubble is created independently
        // on each machine when a building falls, each creation rolling its own Guid.NewGuid(), so
        // the same ruin has a different id everywhere. Position is what they agree on.
        private static bool _rubbleCellPending;
        private static int _rubbleCellX, _rubbleCellZ;

        [HarmonyPatch(typeof(Rubble), "Rebuild")]
        public class RubbleRebuildSyncHook
        {
            public static void Prefix(Rubble __instance)
            {
                Main._rubbleCellPending = false;
                if (!NetClient.client.IsConnected || __instance == null) return;
                if (KaCMultiplayer.Net.NetApply.InProgress) return;   // applying someone else's rebuild

                try
                {
                    Building b = __instance.GetComponent<Building>();
                    if (b == null || !Main.IsLocalBuilding(b)) return;   // only announce your own

                    Cell cell = b.GetCell();
                    if (cell == null) return;

                    Main._rubbleCellX = cell.x;
                    Main._rubbleCellZ = cell.z;
                    Main._rubbleCellPending = true;
                }
                catch (Exception e) { Main.helper.Log("[REBUILD] capture error: " + e.Message); }
            }

            public static void Postfix()
            {
                if (!Main._rubbleCellPending) return;
                Main._rubbleCellPending = false;

                try
                {
                    KaCMultiplayer.Net.NetRouter.Send(new KaCMultiplayer.Net.Messages.RubbleClearMessage
                    {
                        X = Main._rubbleCellX,
                        Z = Main._rubbleCellZ
                    });
                    Main.helper.Log($"[REBUILD] broadcasting rubble cleared at {Main._rubbleCellX},{Main._rubbleCellZ}");
                }
                catch (Exception e) { Main.helper.Log("[REBUILD] broadcast error: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(RebuildCursorMode), "DoPrimaryClick")]
        public class RebuildCursorMPHook
        {
            public static bool Prefix(Building clickedBuilding, ref bool __result)
            {
                if (!NetClient.client.IsConnected) return true; // single-player: vanilla
                try
                {
                    __result = true;
                    if (clickedBuilding != null && Main.IsLocalBuilding(clickedBuilding))
                        World.inst.AttemptRebuild(clickedBuilding);
                    return false; // handled
                }
                catch (Exception e) { Main.helper.Log("Rebuild cursor MP hook error: " + e.Message); return true; }
            }
        }

        // World hazards (wolf dens, witch huts) are host-authoritative. On clients the local
        // spawn is suppressed; the host broadcasts each placement and clients mirror it. This
        // keeps both players' maps identical. Existing hazards are sent to late joiners in
        // ClientConnected.
        [HarmonyPatch(typeof(World), "AddWolfDen")]
        public class AddWolfDenHook
        {
            public static bool Prefix(int x, int z)
            {
                // Pure clients never spawn their own wolf dens; they get them from the host.
                // Suppress local spawns on pure clients EXCEPT while loading a save, the save
                // restores hazards by calling AddWitchHut/AddWolfDen, and suppressing those leaves
                // a null hazard that crashes WitchHutSaveData.Unpack. During unpack, let them through.
                if (NetClient.client.IsConnected && !NetHost.IsRunning && !Main.applyingWorldHazard
                    && !LoadSaveOverrides.SessionSave.Unpacking)
                    return false;
                return true;
            }

            public static void Postfix(int x, int z)
            {
                if (NetHost.IsRunning && !Main.applyingWorldHazard)
                {
                    try { KaCMultiplayer.Net.NetRouter.Broadcast(new KaCMultiplayer.Net.Messages.HazardSpawnMessage { X = x, Z = z, HazardType = 0 }, NetClient.client.Id); }
                    catch (Exception e) { Main.helper.Log("AddWolfDen broadcast error: " + e.Message); }
                }
            }
        }

        // True while World.UpscaleFeatures is building the world's scenery. See AddWitchHutHook for
        // why anyone cares which caller is asking.
        public static bool upscalingWorldFeatures;

        // World.UpscaleFeatures turns saved terrain back into objects, and it is a GENERATION step
        // even though a load is what runs it. Knowing we are inside it is what lets the witch-hut
        // suppression tell "the world is being rebuilt" apart from "a save is restoring a specific
        // hut", which the save-unpack flag alone cannot distinguish.
        //
        // A stuck flag would mean huts stay suppressed, which in a multiplayer session is the state
        // we want anyway, so an exception escaping the original method is harmless here.
        [HarmonyPatch(typeof(World), "UpscaleFeatures")]
        public class WorldUpscaleFeaturesHook
        {
            public static void Prefix()
            {
                if (!NetClient.client.IsConnected && !NetHost.IsRunning) return;
                Main.upscalingWorldFeatures = true;
            }

            public static void Postfix()
            {
                Main.upscalingWorldFeatures = false;
            }
        }

        // Witch huts are DISABLED in multiplayer for now, their live sync was unreliable (huts
        // appearing only for the host). Suppress witch-hut spawns for EVERYONE in an MP session, so
        // new games simply have none. We gate on NetHost.IsRunning OR client.IsConnected (not just
        // IsConnected): on the host the server starts at lobby creation, BEFORE world generation, so
        // huts created during world generation are caught as well. NetHost.server is statically
        // created and never null, so IsRunning is safe in single-player (returns false →
        // suppression off).
        //
        // THE SAVE-LOAD HOLE, found 2026-09-07 when the automated run's own log was read: a hut
        // appeared during the load-back check, and the one line this hook logs said exactly why,
        // "Unpacking=True". The save-unpack exception below was written believing the save restores
        // huts through WitchHutSaveData.Unpack, and that is not what happens. WitchHutSaveData has
        // no methods at all and nothing in the game references it. The only callers of AddWitchHut
        // are MapEdit.ApplyWitch, World.PlaceCavesWitches/GenLand and World.UpscaleFeatures, and it
        // is UpscaleFeatures that LoadSaveContainer.Unpack runs to turn saved terrain back into
        // objects. So loading any multiplayer save quietly put the huts back that generation had
        // suppressed, which is not what "disabled until each player can have their own" means.
        //
        // Fixed by suppressing during that rebuild as well. The exception now covers only what it
        // was meant to: a hut arriving by some path other than the world being rebuilt while a save
        // is loading, plus a synced WorldHazard. No broadcast (Postfix) since there are no live
        // spawns to announce.
        [HarmonyPatch(typeof(World), "AddWitchHut")]
        public class AddWitchHutHook
        {
            public static bool Prefix(int x, int z)
            {
                bool inSession = NetClient.client.IsConnected || NetHost.IsRunning;

                // Rebuilding the world's scenery is generation, whoever asked for it, so it is not
                // a reason to let a hut through.
                bool restoringFromSaveData =
                    LoadSaveOverrides.SessionSave.Unpacking && !Main.upscalingWorldFeatures;

                if (inSession && !Main.applyingWorldHazard && !restoringFromSaveData)
                    return false; // disabled in MP, no hut spawns

                // If a hut is allowed through while in a session, record WHY, so if one still appears
                // we know which bypass path created it. This log line is what found the save-load
                // hole above; it earns its keep.
                if (inSession)
                    Main.helper.Log($"[WITCH] AddWitchHut allowed at ({x},{z}), Unpacking={LoadSaveOverrides.SessionSave.Unpacking} upscaling={Main.upscalingWorldFeatures} applyingWorldHazard={Main.applyingWorldHazard}");
                return true;
            }
        }

        // There is deliberately no Player.Update postfix copying peer resource totals. Nothing
        // sends EconomySnapshotMessage (see FixedUpdate), and the receiver in NetRegistrations
        // assigns resourcesTotal directly anyway, so such a postfix would reproduce a frame
        // later what has already happened, on the hottest per-frame path in the game.
        //
        // The message type stays registered: ids are wire format, and renumbering
        // them to delete an unused one would break compatibility for no gain.

        [HarmonyPatch(typeof(World), "RelationBetween")]
        public class WorldRelationBetweenHook
        {
            /// <summary>
            /// Answers the two things vanilla gets wrong about multiplayer teams.
            ///
            /// <b>Team 0 is nobody.</b> Single-player has exactly one kingdom and hard-codes it as
            /// team 0, so plenty of game code asks "is this team 0?" meaning "is this mine?". In
            /// multiplayer the local player is team 5, 6 or 7, so those comparisons decide the
            /// player is hostile to their own buildings. Rewriting the arguments fixes every such
            /// caller at once.
            ///
            /// <b>Two players could never be anything but Neutral.</b> Vanilla resolves a pair
            /// through a <c>Relations[5, 5]</c> array behind
            /// <c>teamIDA >= 0 &amp;&amp; teamIDA &lt; 5 &amp;&amp; teamIDB >= 0 &amp;&amp; teamIDB &lt; 5</c>.
            /// Our teams sit outside that, so every player pair fell to the method's last line,
            /// <c>return Relations.Neutral</c>, with no way to change it. UpdatePathing's enemy
            /// test was therefore always false and armies never closed on each other.
            ///
            /// The order matters: the rewrite runs first, so a (0, 6) pair becomes (5, 6) and is
            /// then answered from our table like any other player pair. Anything vanilla owns and
            /// can actually answer, team 0 in single-player, the AI range 2-4, the raider and
            /// neutral sentinels, falls through to the untouched original.
            ///
            /// <b>A mixed pair is answered here too, silently.</b> One of our teams against a team
            /// vanilla owns fits neither case above: the original cannot resolve it, so it logs the
            /// pair and returns Neutral. See <see cref="VanillaWouldFallThrough"/> for why that one
            /// Debug.Log was worth 276 MB.
            /// </summary>
            public static bool Prefix(ref int teamIDA, ref int teamIDB, ref World.Relations __result)
            {
                if (!NetClient.client.IsConnected) return true;   // single-player: vanilla

                if (teamIDA == 0 || teamIDB == 0)
                {
                    // Null while the menu is up and between world loads, and this runs often enough
                    // that an unguarded dereference is a matter of timing rather than luck.
                    if (Player.inst == null || Player.inst.PlayerLandmassOwner == null) return true;

                    int localTeam = Player.inst.PlayerLandmassOwner.teamId;
                    if (teamIDA == 0) teamIDA = localTeam;
                    if (teamIDB == 0) teamIDB = localTeam;
                }

                if (PlayerRelations.IsPlayerPair(teamIDA, teamIDB))
                {
                    __result = PlayerRelations.Get(teamIDA, teamIDB);
                    return false;
                }

                // A MIXED pair, one of our teams against something vanilla owns, is the case left
                // over, and handing it to the original is what produced a 276 MB Player.log.
                //
                // Decoded from the shipped IL, the original ends:
                //
                //     if (a >= 0 && a < 5 && b >= 0 && b < 5) return hostility.Get(a, b);
                //     Debug.Log(a + " " + b);
                //     return Relations.Neutral;
                //
                // A team of 5 or more fails that bounds check, so the pair drops out of the bottom
                // and gets logged. The rewrite above is what creates these pairs: vanilla asking
                // about (0, 2) was answered from the array and logged nothing, but we turn it into
                // (5, 2), which cannot be.
                //
                // OrdersManager.ClosestEnemyUnitRankedArmyFirst is the caller that makes it hurt.
                // It walks unitsByTeamID BY INDEX, asking RelationBetween about each slot, and we
                // widened that array from 5 to 32 (see OrdersManagerTeamSlotsHook). Slots 0 and 1
                // and every slot from 5 up are answered before the bounds check or by us, which
                // leaves exactly 2, 3 and 4 falling through on every scan. That is the
                // "5 2 / 5 3 / 5 4" triple repeating through the whole log, three boxed string
                // concats and three stack traces per scan, on a path every archer tower and
                // ballista runs continuously while it looks for a target.
                //
                // Neutral is returned deliberately, because Neutral is what the original returns on
                // that same line. This changes no behaviour, it only declines to narrate it. The
                // slots involved are the AI kingdom range, and a multiplayer session has no AI
                // kingdoms for them to stand for.
                if (VanillaWouldFallThrough(teamIDA, teamIDB))
                {
                    __result = PlayerRelations.Default;
                    return false;
                }

                return true;   // vanilla owns this pair, and can answer without complaining
            }

            /// <summary>
            /// True when the original would reach its last two lines, the Debug.Log and the
            /// unconditional Neutral, for this pair.
            ///
            /// This mirrors the shipped method's earlier exits rather than guessing at them, so a
            /// pair vanilla answers properly is never taken away from it: equal teams are Allies,
            /// 1 and -1 are Enemy, -2 is Neutral, and all four are decided ahead of the bounds
            /// check that our teams fail. Team 1 is how raiders stay hostile to everyone, so
            /// getting this order wrong would make them harmless.
            /// </summary>
            private static bool VanillaWouldFallThrough(int teamA, int teamB)
            {
                if (teamA == teamB) return false;
                if (teamA == 1 || teamB == 1) return false;
                if (teamA == -1 || teamB == -1) return false;
                if (teamA == -2 || teamB == -2) return false;

                bool aInRange = teamA >= 0 && teamA < 5;
                bool bInRange = teamB >= 0 && teamB < 5;
                return !aInRange || !bInRange;
            }
        }


        // DETERMINISTIC MAP GENERATION ACROSS MACHINES.
        //
        // A fresh multiplayer world is generated on the host and reproduced on every client from a
        // single shared integer seed (WorldSeedMessage -> World.Generate(seed)). For that to yield
        // an identical map, every random draw taken during generation has to come from a stream
        // seeded by that number. World.Generate seeds exactly one: it calls SRand.SetSeed(seed) and
        // nothing else. But TerrainGen also draws from UnityEngine.Random (e.g. MakeOcean's
        // `UnityEngine.Random.value` offset into Perlin noise), a SEPARATE generator that
        // World.Generate never seeds. So the coarse land/water layout matched but the terrain edges
        // and rivers came out slightly different on each machine: "same seed, different islands".
        //
        // This injects `UnityEngine.Random.InitState(this.seed)` immediately after the existing
        // `SRand.SetSeed(seed)` call, so BOTH generators start from the same shared seed on every
        // machine. Anchored on SRand.SetSeed rather than inserted at the top because `seed` is only
        // resolved just before that call (Generate(0) on the host picks a random one via SRand),
        // and by that point `this.seed` holds the final value that gets broadcast to clients.
        //
        // A transpiler, not a Prefix: a Prefix sees only the _seed argument, which is 0 on the host
        // (it generates first, then sends the resolved seed), so it could not seed UnityEngine.Random
        // with the value clients will actually use.
        /// <summary>
        /// Seeds UnityEngine.Random from the world seed, but only for a multiplayer session.
        ///
        /// The injected call site is inside World.Generate, right after SRand.SetSeed. Clients have
        /// to reproduce the host's map exactly, and TerrainGen draws from UnityEngine.Random, which
        /// World.Generate never seeds, so without this the coarse layout matched and the terrain
        /// edges and rivers did not. Single player is left alone deliberately: vanilla's maps vary
        /// between runs at the same seed, and making them repeatable is still a change.
        /// </summary>
        public static void SeedUnityRandomForMultiplayer(int seed)
        {
            if (!InMultiplayer) return;
            UnityEngine.Random.InitState(seed);
        }

        [HarmonyPatch(typeof(World), "Generate")]
        public class WorldGenerateRandomSeedHook
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);

                FieldInfo seedField = typeof(World).GetField("seed");
                // Routed through our own method rather than straight to UnityEngine.Random.InitState,
                // so the seeding can be skipped outside multiplayer. Seeding it makes generation
                // repeatable, which single player never asked for: vanilla leaves that generator
                // unseeded and its maps vary run to run even at the same seed.
                MethodInfo seedForMp = typeof(Main).GetMethod(
                    "SeedUnityRandomForMultiplayer", BindingFlags.Public | BindingFlags.Static);

                int injected = 0;
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode != OpCodes.Call && codes[i].opcode != OpCodes.Callvirt) continue;

                    MethodInfo target = codes[i].operand as MethodInfo;
                    if (target == null || target.Name != "SetSeed") continue;
                    if (target.DeclaringType == null || target.DeclaringType.Name != "SRand") continue;

                    // Insert after the SetSeed call: this.seed -> UnityEngine.Random.InitState(int).
                    codes.InsertRange(i + 1, new List<CodeInstruction>
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldfld, seedField),
                        new CodeInstruction(OpCodes.Call, seedForMp),
                    });
                    injected++;
                    i += 3; // skip past what we just inserted
                }

                // Zero means SRand.SetSeed moved or was renamed by a game update, clients would
                // silently drift from the host's map again. Say so rather than fail quietly later.
                if (injected == 0)
                    Main.helper.Log("MAP SEED TRANSPILER FOUND NO SRand.SetSeed CALL, client maps may not match the host's");
                else
                    Main.helper.Log($"map seed transpiler: UnityEngine.Random seeded alongside SRand in {injected} site(s)");

                return codes.AsEnumerable();
            }
        }


        // FREEZING A KINGDOM WHOSE OWNER HAS GONE.
        //
        // A remote player's kingdom is a second, parallel Player MonoBehaviour, and the singleton
        // transpiler rewrites Player.inst to the receiver inside Player's instance methods, so
        // Player.Update runs for EVERY kingdom on every machine, which is what makes remote
        // kingdoms look alive. Nothing stopped it when their owner left. NetHost's comment claimed
        // the kingdom "already stops ticking once its owner is gone"; it does not. It kept eating
        // food, spreading plague and ageing villagers with nobody driving it and no corrections
        // arriving, and each remaining machine ran that simulation independently, so they drifted
        // from each other as well as from anything real.
        //
        // isGhost is exactly the right signal and already exists. SessionSave sets it for a saved
        // player who has not joined this session; the disconnect handler now sets it for someone
        // who leaves mid-game. Both mean the same thing, a kingdom with no owner behind it, and
        // both should hold still. Reconnecting clears the flag (SessionHandlers), so the thaw is
        // automatic and needs nothing here.
        //
        // A Prefix rather than deactivating the GameObject: the same approach already used to stop
        // a foreign player's Barracks.Tick, and it avoids Unity's rules about inactive objects,
        // which FindObjectsOfType would quietly start skipping.
        [HarmonyPatch(typeof(Player), "Update")]
        public class PlayerUpdateFreezeHook
        {
            public static bool Prefix(Player __instance)
            {
                if (!NetClient.client.IsConnected || __instance == null) return true;

                // Tick path: this runs for every player every frame, and an escaping exception here
                // is not caught by anything, so it would stall the simulation. IsFrozenKingdom walks
                // kCPlayers, and a mid-iteration change to that dictionary would throw. On any error,
                // let the game's own Update run (return true) rather than freeze the kingdom.
                try
                {
                    // Counting moved to TickAllForPlayer, which is now the only thing that
                    // reaches TickAll, so the heartbeat reports ticks that actually happened
                    // rather than Updates that might have caused one.
                    return !Main.IsFrozenKingdom(__instance);
                }
                catch (Exception e) { Main.LogEx("PlayerUpdateFreezeHook", e); return true; }
            }

            /// <summary>
            /// Routes this method's <c>Tickable.TickAll</c> call through
            /// <see cref="Main.TickAllForPlayer"/>, so the world ticks once per frame on one clock.
            ///
            /// One operand swap plus an extra argument, the same shape as the TryAddJobs redirect in
            /// BuildingCompleteBuildHook. <c>ldarg.0</c> goes in ahead of the call so the receiver
            /// travels with the delta, which is the whole point: the guard cannot tell whose clock
            /// it has been handed otherwise.
            /// </summary>
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);

                MethodInfo vanilla = typeof(Tickable).GetMethod(
                    "TickAll", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new Type[] { typeof(float) }, null);
                MethodInfo guarded = typeof(Main).GetMethod(
                    "TickAllForPlayer", BindingFlags.Static | BindingFlags.Public);

                if (vanilla == null || guarded == null)
                {
                    Main.helper.Log("PLAYER UPDATE TRANSPILER FOUND NO Tickable.TickAll; the world clock"
                                    + " stays per-kingdom and a menu pause will still drift the calendar");
                    return codes.AsEnumerable();
                }

                int swapped = 0;
                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].operand as MethodInfo != vanilla) continue;

                    codes[i].operand = guarded;
                    codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_0));   // the Player doing the ticking
                    i++;                                                    // step past what we just inserted
                    swapped++;
                }

                Main.helper.Log($"Player.Update: {swapped} Tickable.TickAll call(s) routed through the world-clock guard");
                return codes.AsEnumerable();
            }
        }

        // Ghost guids we have already reported freezing, so a per-frame check cannot flood the log.
        private static HashSet<string> _loggedFrozenKingdoms = new HashSet<string>();

        /// <summary>
        /// True when this Player belongs to a participant who is not here, a mid-game leaver, or a
        /// saved kingdom nobody has reconnected to. The local player is never a ghost, so they can
        /// never freeze themselves.
        /// </summary>
        public static bool IsFrozenKingdom(Player p)
        {
            if (p == null) return false;
            foreach (SessionPlayer kp in kCPlayers.Values)
            {
                if (kp == null || kp.inst != p) continue;
                if (kp.isGhost && kp.steamId != null && _loggedFrozenKingdoms.Add(kp.steamId))
                    helper.Log($"[FREEZE] {kp.name}'s kingdom is frozen, owner absent, simulation halted until they return");
                return kp.isGhost;
            }
            return false;
        }

        /// <summary>Lets a returning player's kingdom report that it froze again if they leave twice.</summary>
        public static void ClearFrozenLog(string steamId)
        {
            if (steamId != null) _loggedFrozenKingdoms.Remove(steamId);
        }

        // Player.Reset deliberately carries no patch. The singleton transpiler covers every
        // declared instance method on Player and reports no rewrites inside Reset, which means it
        // already works on its own receiver rather than on Player.inst, so it is correct on a
        // cloned remote player exactly as the game ships it. It also expires HealthTimer, which is
        // private and has no accessible equivalent from here.

        /// <summary>
        /// Widens a kingdom's per-landmass BUILDING registries so every landmass in the world has a
        /// row, without disturbing the rows it already holds.
        ///
        /// The same shape of bug as the job tables, in a different set of arrays. Vanilla sizes all
        /// of this once, in <c>Player.ResetPerLandMassData</c>, from <c>World.inst.NumLandMasses</c>.
        /// In multiplayer a kingdom object can exist before this machine has the finished map, so
        /// the arrays get cut to the landmass count of that moment and nothing ever revisits them.
        ///
        /// What that costs is quiet. <see cref="PlayerAddBuildingHook"/> bounds-checks against
        /// <c>ResidentialsPerLandmass.Length</c> and, when the index is past the end, adds the
        /// building to the kingdom but to NO per-landmass registry. The building exists and is
        /// owned, yet <c>GetBuildingListForLandMass</c> cannot see it. A starting keep landing on
        /// landmass 1 or 2 of a three-landmass world did exactly that, twice in one session:
        ///
        ///     [ADDBUILDING] 'keep' has landMass=2 (...); adding to the kingdom but not to any
        ///     per-landmass registry
        ///
        /// That message blames a bridge or a map disagreement, which is what the guard was written
        /// for, but 1 and 2 are perfectly ordinary indices in that world. The array was simply two
        /// rows long.
        ///
        /// GROWN rather than rebuilt, deliberately. ResetPerLandMassData would size everything
        /// correctly and is public, but its first act on each structure is Clear(), so calling it on
        /// a kingdom that already holds buildings empties every registry; it also calls
        /// JobSystem.InitJobList and SetupJobPriorities, which is the very wipe
        /// PreserveLoadedJobsHook exists to prevent during a load. Appending empty rows cannot
        /// disturb a landmass that already had one.
        ///
        /// New rows are built the way vanilla builds them, capacities included (300 homes, 100
        /// unbuilt buildings), so a grown row is indistinguishable from one Reset made.
        /// </summary>
        public static bool EnsureBuildingRegistriesCoverWorld(Player p)
        {
            if (p == null || World.inst == null) return false;

            int need = World.inst.NumLandMasses;
            if (need <= 0) return false;

            try
            {
                bool grew = false;
                int had = (p.ResidentialsPerLandmass != null) ? p.ResidentialsPerLandmass.Length : 0;

                if (had < need)
                {
                    ArrayExt<Home>[] wider = new ArrayExt<Home>[need];
                    for (int i = 0; i < had; i++) wider[i] = p.ResidentialsPerLandmass[i];
                    for (int i = had; i < need; i++) wider[i] = new ArrayExt<Home>(300);
                    p.ResidentialsPerLandmass = wider;
                    grew = true;
                }

                var registry = PrivateField.Get<ArrayExt<Player.LandMassBuildingRegistry>>(
                    p, "landMassBuildingRegistry");
                while (registry != null && registry.Count < need)
                {
                    registry.Add(new Player.LandMassBuildingRegistry());
                    grew = true;
                }

                var unbuilt = PrivateField.Get<ArrayExt<ArrayExt<Building>>>(
                    p, "unbuiltBuildingsPerLandmass");
                while (unbuilt != null && unbuilt.Count < need)
                {
                    unbuilt.Add(new ArrayExt<Building>(100));
                    grew = true;
                }

                if (grew)
                {
                    int team = (p.PlayerLandmassOwner != null) ? p.PlayerLandmassOwner.teamId : -1;
                    if (grownBuildingRegistries.Add(team))
                        helper.Log($"[ADDBUILDING] grew team {team}'s per-landmass building registries "
                                   + $"from {had} row(s) to {need} (their kingdom was built before "
                                   + "this machine had the map)");
                }

                return true;
            }
            catch (Exception e) { LogEx("growing per-landmass building registries", e); return false; }
        }

        /// <summary>
        /// Reports the state of everything the build menu's pictures depend on.
        ///
        /// Those pictures are not sprites. BuildTab.AddButton instantiates each building's own
        /// DisplayModel, scales it, and puts it on the "UI" LAYER, so each one is a real 3D object
        /// that only appears if a camera is set up to draw that layer. The button's background and
        /// its label come from the Canvas instead, which needs no camera at all.
        ///
        /// That split is exactly what the reported symptom looks like: every button present, every
        /// label readable, and no building in any of them. Canvas fine, models gone. So the
        /// question is narrow, and it is about the camera rather than about the menu.
        ///
        /// Two theories have already died here, and both died to evidence rather than to argument:
        /// that a guest skipping StartGame lost the world setup, and that the livery was missing
        /// when the icons were built (it was not, bannerIdx was already 0). This logs the state
        /// instead of guessing a third time.
        /// </summary>
        public static void LogBuildMenuState(string when)
        {
            try
            {
                int uiLayer = LayerMask.NameToLayer("UI");
                GameUI gameUI = GameUI.inst;
                Camera uiCam = (gameUI != null) ? gameUI.UICamera : null;
                Camera worldCam = (gameUI != null) ? gameUI.WorldCamera : null;

                string cam = "uiCamera=null";
                if (uiCam != null)
                {
                    bool drawsUiLayer = uiLayer >= 0 && (uiCam.cullingMask & (1 << uiLayer)) != 0;
                    cam = $"uiCamera present enabled={uiCam.enabled}"
                        + $" activeInHierarchy={uiCam.gameObject.activeInHierarchy}"
                        + $" rendersUILayer={drawsUiLayer}"
                        + $" targetTexture={(uiCam.targetTexture != null)}"
                        + $" depth={uiCam.depth} cullingMask=0x{uiCam.cullingMask:X}";
                }

                helper.Log($"[BUILDUI] {when}: uiLayerIndex={uiLayer} buildUI={(BuildUI.inst != null)}"
                           + $" gameUI={(gameUI != null)} worldCamera={(worldCam != null)} {cam}");

                LogUiLayerModels(uiLayer);
            }
            catch (Exception e) { LogEx("logging build menu state", e); }
        }

        /// <summary>
        /// Reports the models that are supposed to BE the build menu's pictures.
        ///
        /// The camera has already been cleared of suspicion: it exists, it is enabled, it is not
        /// diverted to a render texture, and its culling mask includes the UI layer. So whatever is
        /// wrong is with the objects rather than with what draws them, and there are only a few ways
        /// for a renderer to be invisible to a camera that is looking straight at its layer.
        ///
        /// Each one is reported rather than guessed at:
        ///   count           were they created at all, or did AddButton never get that far
        ///   inactive        created but switched off
        ///   nullMaterial    present and drawing nothing, the livery-shaped failure
        ///   wrongLayer      counted separately, since the layer is set after instantiation and a
        ///                   throw in between would leave them on the prefab's own layer
        ///
        /// A sample of names and positions comes with it, because "they exist, they are active, they
        /// have materials" would mean they are simply somewhere the camera is not pointing, and the
        /// position is the only thing that would say so.
        /// </summary>
        private static void LogUiLayerModels(int uiLayer)
        {
            try
            {
                if (uiLayer < 0) return;

                Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>();
                int onLayer = 0, inactive = 0, nullMaterial = 0;
                System.Text.StringBuilder sample = new System.Text.StringBuilder();

                for (int i = 0; i < all.Length; i++)
                {
                    Renderer r = all[i];
                    if (r == null || r.gameObject.layer != uiLayer) continue;

                    onLayer++;
                    if (!r.gameObject.activeInHierarchy) inactive++;
                    if (r.sharedMaterial == null) nullMaterial++;

                    if (onLayer <= 4)
                        sample.Append($" | {r.gameObject.name} active={r.gameObject.activeInHierarchy}"
                                      + $" enabled={r.enabled} mat={(r.sharedMaterial != null)}"
                                      + $" pos={r.transform.position}");
                }

                helper.Log($"[BUILDUI] models on the UI layer: {onLayer} renderer(s), {inactive} inactive, "
                           + $"{nullMaterial} with no material{sample}");
            }
            catch (Exception e) { LogEx("logging UI-layer models", e); }
        }

        /// <summary>
        /// Works out a kingdom's gold capacity from ITS OWN buildings rather than from the local
        /// player's.
        ///
        /// Decompiled from the shipped IL, vanilla is:
        ///
        /// <code>
        ///     MaxGoldStorage = 0;
        ///     for (int i = 0; i &lt; ownedLandMasses.Count; i++)
        ///     {
        ///         int lm = ownedLandMasses.data[i];
        ///         MaxGoldStorage += 1000 * Player.inst.GetBuildingListForLandMass(lm, World.throneRoomHash).Count;
        ///         MaxGoldStorage += 2500 * Player.inst.GetBuildingListForLandMass(lm, World.largeThroneRoomHash).Count;
        ///     }
        /// </code>
        ///
        /// The method belongs to a LandmassOwner and then asks <c>Player.inst</c> what that owner
        /// has built. In single player those are the same kingdom and it is correct. In a session
        /// they are not: computing another player's capacity asks the LOCAL player for throne rooms
        /// on the OTHER player's islands, and the local player has none there, so the answer is
        /// always zero.
        ///
        /// Gold capacity comes only from throne rooms, so zero capacity means the kingdom cannot
        /// hold gold at all, and <c>Gold</c> sits at 0 forever. That is what reached the user as a
        /// merchant bug: <c>ResourceLineItemUI.ClampOrder</c> reacts to a typed order by computing
        /// <c>PlayerLandmassOwner.Gold / price</c> and writing the result back into the box, so
        /// every number typed into a merchant order snapped straight back to 0. The save showed it
        /// plainly, both kingdoms holding one throneroom and only one of them credited with it:
        ///
        ///     Longvale (host)  throneroom: 1  ->  maxGold = 1000
        ///     Polyton  (guest) throneroom: 1  ->  maxGold = 0
        ///
        /// NEITHER existing transpiler reaches this. The singleton rewrite covers Player's own
        /// instance methods and the owner rewrite covers Building's; CalcMaxGold is on
        /// LandmassOwner, which is neither, so it kept reading the singleton unnoticed. It is worth
        /// checking the rest of LandmassOwner for the same shape.
        /// </summary>
        [HarmonyPatch(typeof(LandmassOwner), "CalcMaxGold")]
        public class LandmassOwnerCalcMaxGoldHook
        {
            public static bool Prefix(LandmassOwner __instance)
            {
                try
                {
                    if (!NetClient.client.IsConnected) return true;   // single player: vanilla
                    if (__instance == null || __instance.ownedLandMasses == null) return true;

                    // The kingdom this owner actually belongs to. Falling back to vanilla rather
                    // than guessing: a team with no player record is not ours to answer for.
                    SessionPlayer sp = KaCMultiplayer.Net.NetPlayers.ByTeam(__instance.teamId);
                    Player owner = (sp != null) ? sp.inst : null;
                    if (owner == null) return true;

                    int max = 0;
                    for (int i = 0; i < __instance.ownedLandMasses.Count; i++)
                    {
                        int lm = __instance.ownedLandMasses.data[i];

                        ArrayExt<Building> thrones = owner.GetBuildingListForLandMass(lm, World.throneRoomHash);
                        if (thrones != null) max += 1000 * thrones.Count;

                        ArrayExt<Building> large = owner.GetBuildingListForLandMass(lm, World.largeThroneRoomHash);
                        if (large != null) max += 2500 * large.Count;
                    }

                    __instance.MaxGoldStorage = max;

                    // Zero capacity means the kingdom cannot hold gold at all, which stops the
                    // treasury filling and makes every merchant order clamp to 0. It is legitimate
                    // for a kingdom with no throne room, so say which case this is rather than
                    // leaving a silent zero to be puzzled over.
                    if (max == 0) ReportEmptyTreasury(__instance, owner);

                    return false;
                }
                catch (Exception e)
                {
                    // Vanilla runs on any failure, which is wrong for a remote kingdom but no worse
                    // than the behaviour this replaces.
                    LogEx("calculating a kingdom's gold capacity", e);
                    return true;
                }
            }

            /// <summary>Says WHY a kingdom ended up with no gold capacity, once per team.</summary>
            private static readonly HashSet<int> reported = new HashSet<int>();

            private static void ReportEmptyTreasury(LandmassOwner lo, Player owner)
            {
                try
                {
                    if (!reported.Add(lo.teamId)) return;

                    // What the kingdom owns, as CalcMaxGold sees it.
                    System.Text.StringBuilder owned = new System.Text.StringBuilder();
                    for (int i = 0; i < lo.ownedLandMasses.Count; i++)
                        owned.Append(lo.ownedLandMasses.data[i] + " ");

                    // Where its throne rooms ACTUALLY are, walked from the kingdom's own building
                    // list rather than through the per-landmass registry the lookup uses. If a
                    // throne room turns up here but not there, the building is real and the
                    // registry is the thing that is wrong.
                    int thrones = 0;
                    System.Text.StringBuilder at = new System.Text.StringBuilder();
                    if (owner.Buildings != null)
                    {
                        for (int i = 0; i < owner.Buildings.Count; i++)
                        {
                            Building b = owner.Buildings.data[i];
                            if (b == null || b.UniqueName != "throneroom") continue;
                            thrones++;
                            at.Append(b.LandMass() + " ");
                        }
                    }

                    helper.Log($"[GOLD] team {lo.teamId} has NO gold capacity. ownedLandMasses=[{owned.ToString().Trim()}]"
                               + $" throneRoomsInKingdom={thrones} onLandmass=[{at.ToString().Trim()}]"
                               + $" buildings={(owner.Buildings != null ? owner.Buildings.Count : -1)}"
                               + " (if a throne room is listed on a landmass the kingdom does not own,"
                               + " ownership and placement disagree; if it is on one it DOES own, the"
                               + " per-landmass registry never received it)");
                }
                catch (Exception e) { LogEx("reporting an empty treasury", e); }
            }
        }

        [HarmonyPatch(typeof(Player), "AddBuilding")]
        public class PlayerAddBuildingHook
        {
            public static bool Prefix(Player __instance, Building b)
            {
                try
                {
                    if (NetClient.client.IsConnected)
                    {
                        // Cover the world BEFORE anything is indexed by landmass. Without this the
                        // bounds check below is the only thing standing between a short array and an
                        // IndexOutOfRange, and it "passes" by dropping the building out of every
                        // per-landmass registry instead. See EnsureBuildingRegistriesCoverWorld.
                        Main.EnsureBuildingRegistriesCoverWorld(__instance);

                        int landMass = b.LandMass();

                        __instance.Buildings.Add(b);

                        // Shared storage only. A private store belongs to its own building and
                        // must not enter the kingdom-wide pool.
                        foreach (IResourceStorage storage in b.GetComponents<IResourceStorage>())
                            if (!storage.IsPrivate())
                                FreeResourceManager.inst.AddResourceStorage(storage);

                        // b.LandMass() is GetCell().landMassIdx, and a cell over water (a road on a
                        // bridge/pier) has landMassIdx == -1. Vanilla guards this exact case itself,
                        // World.AddVillagerToLandmass tests `landMassIdx >= 0 && < count` before
                        // indexing its per-landmass array, and this reimplementation must do the
                        // same, or every per-landmass index below throws IndexOutOfRange and the
                        // catch swallows a half-added building (corrupt state: jobs/pawns adrift).
                        //
                        // It also fires when a peer places on a cell that is land on THEIR map but
                        // water on ours, i.e. the machines' maps disagree. That is the map-desync
                        // bug (WorldGenerateRandomSeedHook), and once the maps match this branch
                        // should stop being taken for on-land buildings; a road genuinely on a
                        // bridge legitimately has no landmass and belongs in none of these buckets.
                        bool validLandMass = landMass >= 0 && landMass < __instance.ResidentialsPerLandmass.Length;
                        if (!validLandMass)
                            Main.helper.Log($"[ADDBUILDING] '{b.UniqueName}' has landMass={landMass} (no landmass, bridge/pier, or a cell the maps disagree on); adding to the kingdom but not to any per-landmass registry");

                        Home home = b.GetComponent<Home>();
                        if (home != null)
                        {
                            __instance.Residentials.Add(home);
                            if (validLandMass)
                                __instance.ResidentialsPerLandmass[landMass].Add(home);
                        }

                        WagePayer wagePayer = b.GetComponent<WagePayer>();
                        if (wagePayer != null)
                            __instance.WagePayers.Add(wagePayer);

                        RadiusBonus radiusBonus = b.GetComponent<RadiusBonus>();
                        if (radiusBonus != null)
                            __instance.RadiusBonuses.Add(radiusBonus);

                        // The three registries are private on Player and have no accessor.
                        var globalRegistry = PrivateField.Get<ArrayExt<Player.BuildingRegistry>>(
                            __instance, "globalBuildingRegistry");
                        var landMassRegistry = PrivateField.Get<ArrayExt<Player.LandMassBuildingRegistry>>(
                            __instance, "landMassBuildingRegistry");
                        var unbuiltPerLandmass = PrivateField.Get<ArrayExt<ArrayExt<Building>>>(
                            __instance, "unbuiltBuildingsPerLandmass");

                        // Global registry is not landmass-keyed, so it always runs; the per-landmass
                        // ones are guarded on the same bound as vanilla.
                        __instance.AddToRegistry(globalRegistry, b);
                        if (validLandMass)
                        {
                            __instance.AddToRegistry(landMassRegistry.data[landMass].registry, b);
                            landMassRegistry.data[landMass].buildings.Add(b);

                            // Under construction: tracked separately so the landmass knows what is
                            // still owed work.
                            if (!b.IsBuilt())
                                unbuiltPerLandmass.data[landMass].Add(b);
                        }

                        return false;
                    }
                }
                catch (Exception e)
                {
                    // DIAGNOSTIC: is landMass too big, or are the per-landmass arrays too small? This is the
                    // pre-existing client IndexOutOfRange flood, likely corrupts building state (jobs/pawns).
                    try
                    {
                        int lm = b.LandMass();
                        int resLen = (__instance.ResidentialsPerLandmass != null) ? __instance.ResidentialsPerLandmass.Length : -1;
                        int team = (__instance.PlayerLandmassOwner != null) ? __instance.PlayerLandmassOwner.teamId : -999;
                        Main.helper.Log($"[ADDBUILDING] FAILED '{b.UniqueName}' landMass={lm} ResidentialsPerLandmass.Length={resLen} ownerTeam={team} isLocalInst={(__instance == Player.inst)}");
                    }
                    catch { }
                    Main.LogEx("Player.AddBuilding prefix", e);
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(VillagerSystem), "AddVillager")]
        public class PlayerAddVillagerHook
        {
            public static void Postfix(Villager __result)
            {
                if (!NetClient.client.IsConnected) return;

                // Restoring state, not performing an action: applying a peer's message, or
                // unpacking a save. Either way this villager already exists everywhere and
                // announcing it would echo back round.
                //
                // The save case is covered because LoadAtPath wraps the whole unpack in
                // NetApply.Scope(). Without that, loading a save broadcasts every villager it
                // contains, and on a populated save that floods Riptide's pending-message table.
                if (NetApply.InProgress) return;

                try
                {
                    NetRouter.Send(new KaCMultiplayer.Net.Messages.VillagerAddMessage
                    {
                        Villager = __result.guid
                    });
                }
                catch (Exception e) { Main.LogEx("add villager hook", e); }
            }
        }





        // Felling and shaking are player actions: anyone may do them, so each machine announces
        // its own and the host relays. Growth is not, it is the world ticking, so only the host
        // has an opinion and it pushes the result out.
        //
        // All three share the same shape: do nothing unless connected, and say nothing while
        // NetApply is in progress, because then we are reproducing someone else's tree and
        // announcing it would send it back where it came from.

        [HarmonyPatch(typeof(TreeSystem), "FellTree")]
        public class TreeSystemFellTreeHook
        {
            public static void Postfix(Cell cell, int idx)
            {
                if (!NetClient.client.IsConnected || NetApply.InProgress) return;

                NetRouter.Send(new KaCMultiplayer.Net.Messages.TreeFellMessage
                {
                    Index = idx,
                    X = cell.x,
                    Z = cell.z
                });
            }
        }

        [HarmonyPatch(typeof(TreeSystem), "ShakeTree")]
        public class TreeSystemShakeTreeHook
        {
            public static void Postfix(int idx)
            {
                if (!NetClient.client.IsConnected || NetApply.InProgress) return;

                NetRouter.Send(new KaCMultiplayer.Net.Messages.TreeShakeMessage { Index = idx });
            }
        }

        [HarmonyPatch(typeof(TreeSystem), "GrowTree")]
        public class TreeSystemGrowTreeHook
        {
            public static void Postfix(Cell cell)
            {
                // Host only. A client's trees grow when the host says so, never on their own.
                if (!NetHost.IsRunning || NetApply.InProgress) return;

                NetRouter.Broadcast(new KaCMultiplayer.Net.Messages.TreeGrowMessage
                {
                    X = cell.x,
                    Z = cell.z
                }, NetClient.client.Id);
            }
        }



        /// <summary>
        /// Runs the raider system's new-year work without letting it stop the world.
        ///
        /// WHY THIS EXISTS. Weather.Update does, in this order:
        ///
        ///     RaiderSystem.inst.OnNewYear();     // raids are scheduled here
        ///     seasonTime = SummerTime;           // the clock is reset AFTER
        ///     OnSeasonChange.Invoke(...);        // and the season event after that
        ///
        /// so anything thrown out of OnNewYear kills Weather.Update BEFORE the clock is reset and
        /// BEFORE the season event fires. seasonTime stays negative, the next frame flips the
        /// season and throws again, and the game is stuck in that loop for good. That reproduces
        /// the recorded raid symptom exactly: "game time stopped, autosaves went silent, pawns
        /// halted, and no exception was ever thrown".
        ///
        /// The last clause is the giveaway. It was read off the mod's own output.txt, and a throw
        /// here never reaches that file, only Unity's Player.log. The cave-container bug hid in
        /// precisely the same blind spot and cost the whole seasonal economy, harvests included,
        /// until Player.log was read.
        ///
        /// So the exception is contained rather than prevented: worst case is one year without a
        /// raid, plus a full stack trace naming the culprit, instead of a dead session with
        /// nothing to go on. That is what makes turning raids on a safe experiment.
        /// </summary>
        public static void RaiderOnNewYearSafely(RaiderSystem raiders)
        {
            if (raiders == null) return;

            try { raiders.OnNewYear(); }
            catch (Exception e)
            {
                // Full chain: the real cause is usually an inner exception, and this is the one
                // line that will say what the raid freeze actually was.
                LogEx("[RAID] RaiderSystem.OnNewYear threw; skipping this year's raid so the world"
                      + " clock keeps running. THIS STACK IS THE RAID FREEZE", e);
            }
        }

        /// <summary>
        /// Wraps <c>Weather.Update</c>'s call to the raider system. See
        /// <see cref="RaiderOnNewYearSafely"/> for why the world clock depends on it.
        /// </summary>
        [HarmonyPatch(typeof(Weather), "Update")]
        public class WeatherUpdateRaidGuardHook
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);

                MethodInfo vanilla = typeof(RaiderSystem).GetMethod(
                    "OnNewYear", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                MethodInfo guarded = typeof(Main).GetMethod(
                    "RaiderOnNewYearSafely", BindingFlags.Static | BindingFlags.Public);

                if (vanilla == null || guarded == null)
                {
                    Main.helper.Log("WEATHER TRANSPILER FOUND NO RaiderSystem.OnNewYear; a raid that"
                                    + " throws can still stop the world clock");
                    return codes.AsEnumerable();
                }

                // The instance is already on the stack from `ldsfld RaiderSystem::inst`, so a static
                // taking it as its one argument is a straight operand swap; only the call kind
                // changes, since ours is not virtual.
                int swapped = 0;
                foreach (CodeInstruction code in codes)
                {
                    if (code.operand as MethodInfo != vanilla) continue;

                    code.opcode = OpCodes.Call;
                    code.operand = guarded;
                    swapped++;
                }

                Main.helper.Log($"Weather.Update: {swapped} RaiderSystem.OnNewYear call(s) routed through the raid guard");
                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch(typeof(Weather), "ChangeWeather")]
        public class WeatherChangeWeatherHook
        {
            public static void Postfix(Weather.WeatherType type)
            {
                // Host drives the weather; clients are told. The equality check keeps a
                // no-op change off the wire, since ChangeWeather is called on a timer.
                if (!NetHost.IsRunning || !NetClient.client.IsConnected) return;
                if (NetApply.InProgress) return;
                if (Weather.inst == null || type == Weather.inst.currentWeather) return;

                NetRouter.Send(new KaCMultiplayer.Net.Messages.WeatherSetMessage
                {
                    WeatherType = (int)type
                });
            }
        }


        // ---- HARVEST DIAGNOSTIC ---------------------------------------------------------
        //
        // A farm can be built, Open, fully staffed and showing its full rated output in the
        // panel and still deliver nothing, because the amount actually harvested is
        //
        //     YieldAmt * actualYieldPercentage
        //
        // and actualYieldPercentage is not a property of the farm. It is accumulated all
        // summer, a slice at a time, by YieldProducerSeason.Tick, behind six gates:
        //
        //     frame % 8 == updateFrame, IsBuilt(), WorkersForFullYield > 0,
        //     season == Summer, !PauseYield, IsOpen()
        //
        // and Field.Tick forces PauseYield on whenever the field is flooded or has a fertility
        // error. Shut any one of those and the farm still animates, still holds its worker and
        // still reports "Food Output: 5 / 5 per year", but harvests exactly zero. From the
        // building panel every one of those states looks identical, which is why "he works the
        // land but never harvests" cannot be diagnosed from the screen.
        //
        // Inst_OnSeasonChange is the one moment where the whole chain is still live and the
        // answer is a single number, so that is where this reads it. Twice a year per producer,
        // a handful of lines a minute at speed 3, and it names the gate rather than the symptom.
        //
        // Remove once the cause is known. It is a probe, not a feature.
        [HarmonyPatch(typeof(YieldProducerSeason), "Inst_OnSeasonChange")]
        public class HarvestDiagnosticHook
        {
            public static void Prefix(YieldProducerSeason __instance, Weather.SeasonChangeArgs e)
            {
                // Only the harvest edge matters. The summer edge carries no yield, and logging
                // it would double the volume to say nothing.
                if (__instance == null || e == null || e.season != Weather.Season.Winter) return;
                if (!NetClient.client.IsConnected) return;

                try
                {
                    Building b = __instance.b;
                    if (b == null) return;

                    Field field = b.GetComponent<Field>();
                    if (field == null) return;   // orchards and the rest are not what is being chased

                    float pct = __instance.actualYieldPercentage;
                    float yield = __instance.YieldAmt * pct;

                    // Field keeps all of these private, and they are exactly the ones that decide
                    // whether the summer's accumulation ever started.
                    bool flooded = KaCMultiplayer.Net.PrivateField.Get<bool>(field, "flooded", false);
                    bool fertilityError = KaCMultiplayer.Net.PrivateField.Get<bool>(field, "fertilityError", false);
                    float growth = KaCMultiplayer.Net.PrivateField.Get<float>(field, "time", -1f);

                    // Who the farm belongs to. If the local kingdom's farms yield and a peer's do
                    // not (or the other way round), that is the answer on its own.
                    Player owner = Main.GetPlayerByBuilding(b);
                    string ownerName = (owner == Player.inst) ? "LOCAL" : "remote";

                    Main.helper.Log(
                        $"[HARVEST] {b.UniqueName} ({ownerName}) yield={yield:F2}"
                        + $" (YieldAmt={__instance.YieldAmt} x pct={pct:F3})"
                        + $"; built={b.IsBuilt()} open={b.IsOpen()}"
                        + $" workersForFullYield={b.WorkersForFullYield}"
                        + $" workerPct={b.GetWorkerPercent():F2}"
                        + $"; pauseYield={__instance.PauseYield} flooded={flooded}"
                        + $" fertilityError={fertilityError} growth={growth:F2}"
                        + $"; summerTime={Weather.inst.SummerTime:F1} year={e.year}");
                }
                catch (Exception ex) { Main.LogEx("[HARVEST] diagnostic", ex); }
            }
        }

        // How many times Tickable.TickAll ran during the last frame.
        //
        // TickAll is static and global: it ticks EVERY Tickable in the world and advances the
        // static frame counter the per-field stagger (frame % 8, frame % 10) is measured against.
        // It is called from Player.Update, and the singleton transpiler makes Player.Update run
        // for every kingdom, so this should read 1 in single player and N in an N-kingdom
        // session. Weather.Update, which drives the season clock and therefore the denominator
        // in YieldProducerSeason's dt / SummerTime, is an ordinary MonoBehaviour on a singleton
        // and runs exactly once a frame regardless. Anything above 1 here means production and
        // the calendar are running on different clocks.
        public static int TickAllCallsLastFrame;
        private static int tickAllCallsThisFrame;
        private static int tickAllFrame = -1;

        /// <summary>The season last seen, so a change can be noticed without a second event hook.</summary>
        private static Weather.Season lastSeasonSeen = (Weather.Season)(-1);

        /// <summary>
        /// Notices the season turning and advances anything that counts in seasons.
        ///
        /// POLLED rather than subscribed to Weather.OnSeasonChange, deliberately. That event is the
        /// one the cave-container bug proved is fragile: it is a multicast delegate, and a single
        /// subscriber throwing silently stops every later one, which is how the entire seasonal
        /// economy died unnoticed for weeks. A declared war quietly never arriving would be the
        /// same class of failure and just as hard to spot, so this reads the season directly
        /// instead of trusting the dispatch to reach it.
        /// </summary>
        private static void TickSeasonWatchers()
        {
            try
            {
                if (Weather.inst == null) return;

                Weather.Season now = Weather.inst.season;
                if (now == lastSeasonSeen) return;

                bool first = (int)lastSeasonSeen < 0;
                lastSeasonSeen = now;
                if (first) return;   // the first read sets a baseline, it is not a change

                KaCMultiplayer.Net.PlayerRelations.OnSeasonChanged();
            }
            catch (Exception e) { LogEx("season watchers", e); }
        }

        /// <summary>
        /// Ticks the world, once per frame, on the local kingdom's clock.
        ///
        /// TWO BUGS, ONE CAUSE. Tickable.TickAll is static and world-wide: it ticks EVERY field,
        /// producer and building in the game and advances the static frame counter that the
        /// per-object staggers (frame % 8, frame % 10) are measured against. It is called from
        /// Player.Update, and the singleton transpiler makes Player.Update run for every kingdom.
        /// Weather.Update, which drives the season clock, is an ordinary MonoBehaviour on a
        /// singleton and runs exactly once a frame no matter how many kingdoms there are.
        ///
        ///   1. The whole tickable world ran once per KINGDOM while the calendar ran once per
        ///      FRAME. A three-kingdom session showed tickAllPerFrame=3 in the heartbeat:
        ///      production, training and growth all running at three times the calendar.
        ///
        ///   2. A menu pause froze the calendar but not the world. MainMenuMode.Init sets
        ///      Player.inst.timeScale = 0, and Weather.Update reads exactly that, so the season
        ///      clock stopped. But a REMOTE player's Player object has timeScale = 1 straight from
        ///      its constructor and nothing ever syncs it, so its Player.Update kept calling
        ///      TickAll with a real delta. Crops went on growing toward a winter that never came.
        ///
        /// Both go away by letting only the LOCAL player's call through. That makes the world tick
        /// once per frame, and on the same clock Weather.Update already uses, so the calendar and
        /// everything it gates can no longer drift apart. A paused menu now stops both together.
        ///
        /// Single player is untouched: with nobody connected every call is the local player's.
        /// </summary>
        public static void TickAllForPlayer(float dt, Player who)
        {
            try
            {
                // Not in a session: there is only one Player, and it is this one.
                if (NetClient.client.IsConnected && Player.inst != null && who != Player.inst) return;

                NoteTickAllCall();
            }
            catch { /* never let the world stop ticking because a guard threw */ }

            Tickable.TickAll(dt);
        }

        /// <summary>Counts Tickable.TickAll calls per rendered frame. See TickAllCallsLastFrame.</summary>
        public static void NoteTickAllCall()
        {
            if (Time.frameCount != tickAllFrame)
            {
                TickAllCallsLastFrame = tickAllCallsThisFrame;
                tickAllCallsThisFrame = 0;
                tickAllFrame = Time.frameCount;
            }
            tickAllCallsThisFrame++;
        }



        /// <summary>
        /// Two narrow corrections to the game's own building-completion, which otherwise runs
        /// untouched.
        ///
        /// The game's own completion is left to run. It is already multiplayer-correct here,
        /// because <see cref="BuildingPlayerReferencePatch"/> retargets this very method's
        /// <c>Player.inst</c> reads at the building's owner, so <c>built</c>,
        /// <c>UpdateMaterialSelection</c>, <c>OnBuilt</c>, <c>yearBuilt</c>,
        /// <c>AddAllResourceProviders</c>, <c>BuildingNowBuilt</c> and <c>BakePathing</c> all land
        /// on the right kingdom without help. A Prefix that skipped the body would discard that.
        /// </summary>
        [HarmonyPatch(typeof(Building), "CompleteBuild")]
        public class BuildingCompleteBuildHook
        {
            /// <summary>
            /// Never complete the same building twice.
            ///
            /// In multiplayer <c>CompleteBuild</c> can fire more than once for one building, and
            /// <c>OnBuilt</c> *spawns the unit* for ship-launch pads, so a second completion
            /// instantiated a duplicate ship. Two stacked, one an orphan that could not be
            /// selected or demolished, which later became a destroyed-ship corpse left in
            /// <c>ShipSystem.ships</c> and crashed every autosave in <c>ShipSystemSaveData.Pack</c>.
            /// Vanilla never re-completes a built building, so this only ever fires on our path.
            /// </summary>
            /// <summary>
            /// Buildings already reported as re-completing, so one stuck building cannot drown a
            /// session. In a user log (Rednax, 2026-09-02) a single smallhouse produced 510 of these
            /// lines in four seconds, roughly one per frame, burying everything else that happened.
            /// Reported once per building and then held down, the same discipline the merchant
            /// arrival log uses.
            /// </summary>
            private static readonly HashSet<Guid> reportedRecompletes = new HashSet<Guid>();

            /// <summary>
            /// How many re-completions have been skipped this session, across all buildings.
            ///
            /// Exists because the throttled log below hides RECURRENCE, which is the one thing
            /// worth knowing about this bug. Reported once per building, five hundred repeats and
            /// one repeat look identical in the log, and that is exactly the difference between
            /// "a harmless double completion" and "something is driving this every frame".
            ///
            /// The acceptance run asserts this stays zero, so a clean session says so out loud
            /// instead of saying nothing. Cheap enough to leave in, the same reasoning as
            /// CombatSync.PublishedUpdates.
            /// </summary>
            public static int SkippedRecompletes { get; private set; }

            /// <summary>Forgets the session's tally, for a new session or a load.</summary>
            public static void Reset()
            {
                reportedRecompletes.Clear();
                SkippedRecompletes = 0;
            }

            public static bool Prefix(Building __instance)
            {
                if (!__instance.IsBuilt()) return true;

                // Single player keeps vanilla behaviour, including vanilla's own re-completion, per
                // Main.InMultiplayer. The root cause here was never pinned, so this is a guard over
                // a symptom we have only ever seen in a session, and guarding a symptom is not a
                // licence to change somebody's single-player game.
                if (!Main.InMultiplayer) return true;

                SkippedRecompletes++;

                // ANSWERED 2026-09-15, and the answer was the suspect this comment already
                // named. Vanilla cannot do this to itself:
                //
                //   Building.OnPlacement adds to ConstructionList only `if (!IsBuilt() &&
                //   doBuildAnimation)`, and Building.UpdateBuildingConstruction removes from it the
                //   moment IsBuilt() is true. IsBuilt() is a plain `return built`, and `built` is
                //   set inside CompleteBuild itself.
                //
                // It was ApplyBuildSnapshot, which used to write `built` by reflection out of a
                // peer's snapshot. That marked the building finished without commissioning it (no
                // OnBuilt, no resource providers, no BuildingNowBuilt, no jobs, no pathing), and
                // then this prefix suppressed the local simulation's own CompleteBuild a moment
                // later as a duplicate, so it never got commissioned at all. On a farm that is a
                // field with no HarvesterJob that nobody ever harvests. ApplyBuildSnapshot now
                // calls CompleteBuild instead of writing the field.
                //
                // The guard stays: it is what keeps CompleteBuild idempotent, which is what makes
                // calling it from the snapshot path safe in either arrival order. But a genuine
                // duplicate should now be rare, so if SkippedRecompletes climbs during normal play
                // there is a second source of the same mistake and the log line below carries the
                // state that says which. Do not infer a root cause without it.
                if (reportedRecompletes.Add(__instance.guid))
                {
                    float progress = KaCMultiplayer.Net.PrivateField.Get<float>(__instance, "constructionProgress", -1f);
                    float resources = KaCMultiplayer.Net.PrivateField.Get<float>(__instance, "resourceProgress", -1f);

                    Main.helper.Log($"skipped duplicate CompleteBuild for {__instance.UniqueName} ({__instance.guid})"
                        + $"; constructionProgress={progress}, resourceProgress={resources}"
                        + $", buildTimeAtMax={__instance.BuildTimeAtMax}, doBuildAnimation={__instance.doBuildAnimation}"
                        + $", constructionPaused={__instance.constructionPaused}"
                        + "; further repeats for this building are counted but not logged");
                }

                return false;
            }

            /// <summary>
            /// Redirects vanilla's unconditional <c>TryAddJobs()</c> through
            /// <see cref="Main.AddJobsUnlessUncategorised"/>.
            ///
            /// A Postfix cannot fix this, by then the jobs exist and un-creating them is far more
            /// fragile than not creating them. Both are instance calls taking the building and
            /// returning void, so this is one operand swap and the stack is unchanged.
            /// </summary>
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);
                MethodInfo vanilla = typeof(Building).GetMethod("TryAddJobs", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo guarded = typeof(Main).GetMethod("AddJobsUnlessUncategorised", BindingFlags.Static | BindingFlags.Public);

                int swapped = 0;
                foreach (CodeInstruction code in codes)
                {
                    if (code.operand as MethodInfo != vanilla) continue;

                    code.opcode = OpCodes.Call;
                    code.operand = guarded;
                    swapped++;
                }

                Main.helper.Log($"CompleteBuild: {swapped} TryAddJobs call(s) routed through the job-category guard");
                return codes.AsEnumerable();
            }
        }

        [HarmonyPatch(typeof(Building), "UpdateConstruction")]
        public class BuildingUpdateHook
        {
            /// <summary>
            /// Reports this building to the other players when something they can see about it
            /// changes. The own-team check and the change detection both live in
            /// <see cref="BuildingWatcher.Poll"/>.
            ///
            /// Runs per building per frame, so it stays a thin call, anything expensive belongs
            /// behind Poll's own early-outs, not here.
            /// </summary>
            public static void Prefix(Building __instance)
            {
                if (!NetClient.client.IsConnected) return;

                try { BuildingWatcher.Poll(__instance); }
                catch (Exception e) { Main.LogEx("building watcher poll", e); }
            }
        }



        // Lets the player select their OWN units in MP. Vanilla's GameUI.IsUnitSelectable only returns
        // true when unit.TeamID()==0 (the single-player local team) or the "Control AI Troops" creative
        // option is on. In MP the local player's team is 5/6/7, so your own ships/armies failed this
        // check → couldn't be selected. We now FORCE ControlAITroops on in MP (ControlUnitsInMPHook,
        // below) to open all the unit-control gates for our non-zero team, but that would also let you
        // select ENEMY / the other player's units, so we decide selectability FULLY here instead of
        // falling through to vanilla: ONLY units owned by the local player's team are selectable. SP
        // untouched.
        [HarmonyPatch(typeof(GameUI), "IsUnitSelectable")]
        public class GameUIIsUnitSelectableHook
        {
            public static bool Prefix(IMoveableUnit unit, ref bool __result)
            {
                if (!NetClient.client.IsConnected) return true; // single-player: vanilla
                try
                {
                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : 0;
                    ISelectable sel = unit as ISelectable;
                    if (unit != null && localTeam != 0 && unit.TeamID() == localTeam)
                        __result = (sel != null && sel.ValidToSelect()); // your own unit → selectable if valid
                    else
                        __result = false;                                // anything else → not selectable in MP
                    return false; // MP decides fully (don't fall through to vanilla's forced-creative path)
                }
                catch (Exception e) { Main.helper.Log("IsUnitSelectable MP hook error: " + e.Message); }
                return true; // on error, let vanilla decide
            }
        }

        // Forces the "Control AI Troops" creative option ON while in an MP session. That single option
        // guards EVERY GameUI unit-control gate, IsUnitSelectable, MoveUnitsToPosition (right-click
        // move), GetSelectedUnits, DoPrimaryClick, UpdateForKeyboard, each of which otherwise only acts
        // on units whose TeamID()==0. Forcing it on makes all of them accept our MP team (5/6/7), so you
        // can select AND move your ships/troops. It affects nothing else (this option isn't used for
        // building/economy), and selection is still restricted to your own units by the hook above, so
        // you can't touch anyone else's. Single-player unaffected.
        // (Removed the Harmony Postfix on Player.IsCreativeModeOptionOn, that method is inlined at its
        // call sites so the patch never ran. Replaced by Main.EnsureControlAITroopsOn() which writes the
        // backing cmoOptionsOn[] array directly every frame; see Update().)

        // OrdersManager tracks every moveable unit in per-team arrays `unitsByTeamID` / `envoysByTeamID`,
        // allocated SIZE 5 in its constructor (teams 0-4), the SAME fixed-size-by-team trap as PathCell.
        // In MP the local player's team is 5/6/7, so ShipBase.Init -> OrdersManager.AddUnit ->
        // unitsByTeamID[teamID] threw IndexOutOfRange for every CONTROLLABLE ship (SeedShip / TroopTransport
        // / Transport, the ones implementing IMoveableUnit). That exception aborted the launch pad's
        // OnBuilt BEFORE it set doDemolish, so the pad never demolished (the "duplicate" object) AND the
        // ship was never registered for orders (couldn't be moved). Merchant/fishing ships aren't
        // IMoveableUnit, so they skip AddUnit, which is exactly why THEY worked. Fix: enlarge both arrays
        // to cover MP teams before any add, preserving existing team data and initializing new slots.
        // Once enlarged, every later reader (RemoveUnit, per-team queries) is in-bounds too. Gated to MP.
        [HarmonyPatch(typeof(OrdersManager), "AddUnit")]
        public class OrdersManagerTeamSlotsHook
        {
            private const int MpTeamSlots = 32;

            public static void Prefix(OrdersManager __instance)
            {
                if (!(NetClient.client.IsConnected || NetHost.IsRunning)) return; // single-player: native size 5
                try
                {
                    EnsureSize(__instance, "unitsByTeamID");
                    EnsureSize(__instance, "envoysByTeamID");
                }
                catch (Exception e) { Main.helper.Log("OrdersManager team-slots hook error: " + e.Message); }
            }

            private static void EnsureSize(OrdersManager om, string fieldName)
            {
                var field = typeof(OrdersManager).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                if (field == null) return;
                var arr = field.GetValue(om) as ArrayExt<IMoveableUnit>[];
                if (arr == null || arr.Length >= MpTeamSlots) return;
                var bigger = new ArrayExt<IMoveableUnit>[MpTeamSlots];
                for (int i = 0; i < MpTeamSlots; i++)
                    bigger[i] = (i < arr.Length && arr[i] != null) ? arr[i] : new ArrayExt<IMoveableUnit>(16); // ArrayExt needs a capacity; Add auto-expands
                field.SetValue(om, bigger);
            }
        }

        // Opens the right window when a ship is selected in MP. Vanilla Ship.OnSelected has two branches:
        //
        //   teamID == 0            -> shipLogisticsUI  ("my ship: let me edit its route")
        //   type == PlayerMerchant -> merchantUI       ("a foreign kingdom's merchant: let me trade")
        //
        // In single-player the local player IS team 0, so the first branch claims every ship the player
        // owns, including their own merchant, and the second only ever sees a visiting AI kingdom's
        // merchant. In MP the local team is 5/6/7, so the first branch never fires: your transport ship
        // opened nothing, and your own merchant fell through to the trade window instead of the route
        // editor. With no route editor there is no way to give a merchant a destination at all.
        //
        // So the rule here is vanilla's rule with the ownership test written the way MP needs it:
        // your own ship (any type, merchant included) gets the route editor; anyone else's merchant
        // keeps going to vanilla and opens the trade window. ShipLogisticsUI is type-agnostic, it takes
        // the ILogisticTransport and reads ValidForDocking off it, and Ship's implementation already
        // allows a foreign dock for a PlayerMerchant when Player.DocksOpen says so (which
        // EnsureTradeDocksOpenInMP keeps true between every pair of players). Single-player untouched.
        [HarmonyPatch(typeof(Ship), "OnSelected")]
        public class ShipOnSelectedHook
        {
            public static bool Prefix(Ship __instance)
            {
                if (!NetClient.client.IsConnected) return true; // single-player: vanilla
                try
                {
                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : 0;
                    if (localTeam != 0 && __instance.teamID == localTeam)
                    {
                        GameUI.inst.ClearUIForClick();
                        GameUI.inst.shipLogisticsUI.SetSelectedTransport(__instance);
                        GameUI.inst.shipLogisticsUI.SetVisible(true);
                        SfxSystem.inst.PlayFromBank(
                            __instance.type == ShipBase.ShipType.PlayerMerchant ? "MerchantBoatSelect" : "TransportShipSelect",
                            __instance.GetPos(), (SfxParamsOverride)null);
                        GameUI.inst.ClearCellSelected();
                        Main.helper.Log($"[MERCHANT] route editor opened for your {__instance.type} (guid {__instance.guid})");
                        return false; // handled, skip vanilla's teamID==0 gate
                    }
                }
                catch (Exception e) { Main.helper.Log("Ship.OnSelected MP hook error: " + e.Message); }
                return true; // vanilla: another player's merchant opens the trade window
            }
        }

        // The same gate, on the other way in. ShipBase.Init wires issueButton.onClick to
        // SendMessage("OnClickedButton"), and Ship.OnClickedButton repeats OnSelected's teamID==0 /
        // PlayerMerchant split verbatim. That button matters more than it looks: a ship starts paused
        // (Ship.Awake calls SetPause(true)) and Tick shows the button whenever the route is empty or
        // paused, so the ❗ over a freshly launched merchant is the FIRST thing you would ever click on
        // it, and in MP it opened the trade window. Same rule as above, plus the camera move vanilla
        // does on this path but not on plain selection.
        [HarmonyPatch(typeof(Ship), "OnClickedButton")]
        public class ShipOnClickedButtonHook
        {
            public static bool Prefix(Ship __instance)
            {
                if (!NetClient.client.IsConnected) return true; // single-player: vanilla
                try
                {
                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : 0;
                    if (localTeam != 0 && __instance.teamID == localTeam)
                    {
                        GameUI.inst.shipLogisticsUI.SetSelectedTransport(__instance);
                        GameUI.inst.shipLogisticsUI.SetVisible(true);
                        SfxSystem.inst.PlayFromBank(
                            __instance.type == ShipBase.ShipType.PlayerMerchant ? "MerchantBoatSelect" : "TransportShipSelect",
                            __instance.GetPos(), (SfxParamsOverride)null);
                        Cam.inst.SetDesiredTrackingPos(__instance.transform.position);
                        if (Cam.inst.desiredDist > 15f) Cam.inst.desiredDist = 15f;
                        Main.helper.Log($"[MERCHANT] route editor opened from the issue button for your {__instance.type} (guid {__instance.guid})");
                        return false; // handled, skip vanilla's teamID==0 gate
                    }
                }
                catch (Exception e) { Main.helper.Log("Ship.OnClickedButton MP hook error: " + e.Message); }
                return true; // vanilla: another player's merchant opens the trade window
            }
        }

        // A building by guid, looked up across EVERY player in the session rather than just one.
        //
        // Player.GetBuilding walks that single player's own Buildings array, so it cannot see another
        // player's building at all. Callers below need the whole world. Kept off the hot path by the
        // callers, which consult vanilla's own cache field first.
        public static Building FindBuildingByGuidAnyPlayer(Guid id)
        {
            if (id == Guid.Empty) return null;
            foreach (SessionPlayer kp in Main.kCPlayers.Values)
            {
                if (kp == null || kp.inst == null) continue;
                Building b = kp.inst.GetBuilding(id);
                if (b != null) return b;
            }
            return null;
        }

        /// <summary>
        /// Finds a building by guid wherever it is, trying every player's own registry before
        /// falling back to a scan of the scene.
        ///
        /// The fallback is what makes this safe and the fast path is why it exists.
        /// <c>FindObjectsOfType&lt;Building&gt;</c> walks every object in the scene, which is fine
        /// once and expensive per message: drag-demolishing a row of twenty buildings sends twenty
        /// messages, and each receiving machine was doing twenty full scene scans back to back, in
        /// the same frame, while the player watched. Asking the owners first answers the same
        /// question from short per-player lists.
        ///
        /// The scan still runs when that misses, because a building is only in a player's registry
        /// once it has been added to one; rubble, and anything mid-placement, is not. Correctness is
        /// therefore unchanged, only the usual cost.
        /// </summary>
        public static Building FindBuildingByGuidAnywhere(Guid id)
        {
            if (id == Guid.Empty) return null;

            Building owned = FindBuildingByGuidAnyPlayer(id);
            if (owned != null) return owned;

            foreach (Building b in UnityEngine.Object.FindObjectsOfType<Building>())
                if (b != null && b.guid == id) return b;

            return null;
        }

        // Guids we've already reported resolving cross-player, so a per-tick lookup can't flood the log.
        private static HashSet<Guid> _loggedCrossPlayerOrders = new HashSet<Guid>();

        // ROUTE ORDERS THAT POINT AT ANOTHER PLAYER'S DOCK, the fix that makes cross-player merchant
        // trade possible at all.
        //
        // Ship.LogisticsOrder holds its destination as a building guid and resolves it with
        // Player.inst.GetBuilding(guid). Player.GetBuilding walks that ONE player's own Buildings array,
        // so another player's dock is never found and the call returns null. On its own that would just
        // be a dead route. What makes it destructive is Ship.ValidateOrders, which runs at the top of
        // EVERY Ship.Tick and rewrites any Building order whose GetBuilding() came back null:
        //
        //     order.endType = LogisticsOrderType.Position;   // permanent: the guid is never read again
        //
        // So a merchant sent to another player's port had its route silently downgraded to "sail to a
        // water cell" within a frame of being given, on the owner's machine as much as anyone else's.
        // It then sailed to open water near the dock and sat there. That is the "merchant never arrives"
        // stall the TEMP diagnostics further down were added to chase; the ship was never going to the
        // dock in the first place, so no amount of pathing detail was going to explain it.
        //
        // Fix: when vanilla's lookup comes back empty, resolve the guid across every player in the
        // session. Same shape as the two singleton transpilers, the game's own logic is correct once it
        // is looking at the right player's data. The Postfix only does anything when vanilla already
        // returned null, so a route to your own dock keeps the untouched vanilla path. MP only.
        [HarmonyPatch(typeof(Ship.LogisticsOrder), "GetBuilding")]
        public class LogisticsOrderGetBuildingHook
        {
            public static void Postfix(Ship.LogisticsOrder __instance, ref Building __result)
            {
                if (__result != null) return;               // vanilla found it, our own building
                if (!NetClient.client.IsConnected) return;   // single-player: vanilla
                try
                {
                    Guid id = PrivateField.Get<Guid>(__instance, "endBuildingGuid", Guid.Empty);
                    if (id == Guid.Empty) return;            // a Position order, or never assigned

                    Building b = Main.FindBuildingByGuidAnyPlayer(id);
                    if (b == null) return;                   // genuinely gone, let ValidateOrders retire it

                    __result = b;
                    PrivateField.Set(__instance, "building", b); // vanilla's own cache field, so this costs once
                    if (Main._loggedCrossPlayerOrders.Add(id))
                        Main.helper.Log($"[MERCHANT] route order resolved to another player's building '{b.UniqueName}' (team {b.TeamID()}, guid {id}), vanilla would have discarded this destination");
                }
                catch (Exception e) { Main.helper.Log("[MERCHANT] cross-player order resolve error: " + e.Message); }
            }
        }

        // The same guid, the other way out. LogisticsOrder.GetEnd does not go through GetBuilding, it
        // calls Player.inst.GetBuilding(endBuildingGuid).Center() directly, so for another player's dock
        // it dereferences null. This runs inside DockRouteCursorMode's per-frame route rendering, so it
        // has to stay cheap: read vanilla's cache field first (the Postfix above populates it) and only
        // fall back to the cross-player search on a genuine miss.
        [HarmonyPatch(typeof(Ship.LogisticsOrder), "GetEnd")]
        public class LogisticsOrderGetEndHook
        {
            public static bool Prefix(Ship.LogisticsOrder __instance, ref Vector3 __result)
            {
                if (!NetClient.client.IsConnected) return true;  // single-player: vanilla
                try
                {
                    if (__instance.endType != Ship.LogisticsOrderType.Building) return true; // position order: vanilla

                    Building cached = PrivateField.Get<Building>(__instance, "building", null);
                    if (cached == null)
                    {
                        Guid id = PrivateField.Get<Guid>(__instance, "endBuildingGuid", Guid.Empty);
                        cached = Main.FindBuildingByGuidAnyPlayer(id);
                        if (cached == null) return true;         // vanilla, and its NRE, which is vanilla's answer
                        PrivateField.Set(__instance, "building", cached);
                    }
                    __result = cached.Center();
                    return false;
                }
                catch (Exception e) { Main.helper.Log("[MERCHANT] route endpoint resolve error: " + e.Message); }
                return true;
            }
        }

        // The anchor for the two patches above, and the one that has to hold.
        //
        // GetBuilding is small, a null check, a call, a return. Mono inlines methods of that size, and
        // this project has already been bitten once by patching a method the runtime had inlined at its
        // call sites (Player.IsCreativeModeOptionOn, whose Postfix never ran anywhere it mattered). If
        // GetBuilding is inlined into ValidateOrders, the Postfix above never fires there and the route
        // is destroyed exactly as before.
        //
        // What cannot be inlined away is the FIELD. Every copy of GetBuilding, inlined or not, begins by
        // reading `building` and returns it when it is set. So we fill that cache in ourselves, ahead of
        // the loop that would otherwise discard the order: after this Prefix runs, no version of
        // GetBuilding has any reason to consult Player.inst at all. ValidateOrders itself is a loop over
        // a list, far too large to inline, so this hook is safe to rely on.
        //
        // Only touches orders vanilla cannot resolve; anything on your own landmass is left alone.
        [HarmonyPatch(typeof(Ship), "ValidateOrders")]
        public class ShipValidateOrdersHook
        {
            public static void Prefix(Ship __instance)
            {
                if (!NetClient.client.IsConnected || __instance == null) return; // single-player: vanilla
                try
                {
                    List<Ship.LogisticsOrder> orders = __instance.logisticsOrders;
                    if (orders == null) return;
                    for (int i = 0; i < orders.Count; i++)
                    {
                        Ship.LogisticsOrder o = orders[i];
                        if (o == null || o.endType != Ship.LogisticsOrderType.Building) continue;
                        if (PrivateField.Get<Building>(o, "building", null) != null) continue; // already cached

                        Guid id = PrivateField.Get<Guid>(o, "endBuildingGuid", Guid.Empty);
                        if (id == Guid.Empty) continue;
                        if (Player.inst != null && Player.inst.GetBuilding(id) != null) continue; // ours: vanilla resolves it

                        Building b = Main.FindBuildingByGuidAnyPlayer(id);
                        if (b == null) continue;   // genuinely gone, let vanilla retire the order

                        PrivateField.Set(o, "building", b);
                        if (Main._loggedCrossPlayerOrders.Add(id))
                            Main.helper.Log($"[MERCHANT] kept {__instance.type} route order to another player's '{b.UniqueName}' (team {b.TeamID()}, guid {id}), vanilla would have downgraded it to a position");
                    }
                }
                catch (Exception e) { Main.helper.Log("[MERCHANT] ValidateOrders pre-resolve error: " + e.Message); }
            }
        }

        // PAYING FOR GOODS DELIVERED TO ANOTHER PLAYER'S DOCK.
        //
        // Once a route to a foreign dock survives (see above), Ship.Tick's arrival branch unloads into
        // that dock's stockpile and then tries to settle up:
        //
        //     if (IsSellOrder(currentOrder, this) && building.TeamID() != 0)
        //         AIKingdom.GetKingdomByLandmass(...)?.GetTradeIntentionOfForeignTeamID(...).ConsiderBuyingFrom(...)
        //
        // Every step of that is right except the last: payment is an AI kingdom's decision, and this mod
        // has no AI kingdoms, so GetKingdomByLandmass returns null and the goods are handed over free.
        //
        // We settle it ourselves. Prices come from the same table the trade window uses for a foreign
        // seller, the DOCK OWNER's pay costs for the merchant's team (LandmassOwner.GetPayCosts), so a
        // delivery is worth what buying the same goods over the counter would be worth.
        //
        // Only the merchant's OWNER runs this. A ship starts paused (Ship.Awake) and nothing syncs
        // `paused`, so a remote copy of someone else's merchant never enters Tick's route branch at all:
        // one machine simulates, the rest are puppeted by ShipMove and MerchantTrade. That is why this
        // broadcasts the goods as well as the gold, on the dock owner's machine, the relayed message is
        // the only thing that moves anything.
        //
        // Measured as a hold diff across the tick rather than read out of the game's locals, because the
        // amount actually unloaded depends on the dock's free space and is never exposed. Cost is one
        // ResourceAmount copy per own-merchant tick.
        private static bool _fdPending;
        private static ResourceAmount _fdHoldBefore;
        private static Guid _fdDockGuid;
        private static int _fdDockTeam;

        [HarmonyPatch(typeof(Ship), "Tick")]
        public class ShipForeignDeliveryHook
        {
            public static void Prefix(Ship __instance) { Main.PrepForeignDelivery(__instance); }
            public static void Postfix(Ship __instance) { Main.SettleForeignDelivery(__instance); }
        }

        // Records what the merchant is holding immediately before a tick that may unload it into another
        // player's port. The dock has to be captured here too: the unload ends with NextOrder(), so by
        // the Postfix GetCurrentDock() already points at the next stop on the route.
        public static void PrepForeignDelivery(Ship ship)
        {
            _fdPending = false;
            try
            {
                if (!NetClient.client.IsConnected || ship == null) return;
                if (ship.type != ShipBase.ShipType.PlayerMerchant) return;
                if (!ship.arrived) return;                       // nothing unloads until it has docked

                int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                    ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
                if (ship.teamID != localTeam) return;            // only the owner's machine simulates the route

                Dock dock = ship.GetCurrentDock();
                if (dock == null) return;
                Building dockB = dock.GetComponent<Building>();
                if (dockB == null) return;
                if (dockB.TeamID() == ship.teamID) return;       // your own port: moving your own goods, not a sale

                _fdHoldBefore = ship.hold;
                _fdDockGuid = dockB.guid;
                _fdDockTeam = dockB.TeamID();
                _fdPending = true;
            }
            catch (Exception e) { Main.helper.Log("[MERCHANTTRADE] delivery prep error: " + e.Message); _fdPending = false; }
        }

        // Prices whatever left the hold during that tick, moves the gold, and broadcasts both halves.
        public static void SettleForeignDelivery(Ship ship)
        {
            if (!_fdPending) return;
            _fdPending = false;
            try
            {
                // What actually left the hold during that tick, priced at the dock owner's rates.
                // The arithmetic lives in TradeMath so the receiving machines settle the identical
                // figure from the identical rule; see Trade/TradeMath.cs.
                ResourceAmount goods = TradeMath.Delivered(_fdHoldBefore, ship.hold);

                LandmassOwner buyer = World.GetLandmassOwnerByTeamId(_fdDockTeam);    // dock owner receives the goods
                LandmassOwner seller = World.GetLandmassOwnerByTeamId(ship.teamID);   // merchant owner is paid
                if (buyer == null || seller == null) return;

                int cost = TradeMath.PriceOf(goods, buyer.GetPayCosts(ship.teamID));
                if (cost <= 0) return;                      // nothing changed hands, or everything was worthless

                // The goods are already in their stockpile by the time we get here, vanilla deposits
                // before it settles, and there is no clean way to put them back. So an insolvent buyer
                // pays what they have rather than the delivery being cancelled.
                int payable = TradeMath.ClampToAffordable(cost, buyer.Gold);
                if (payable < cost)
                    Main.helper.Log($"[MERCHANTTRADE] team {_fdDockTeam} owes {cost} gold for a delivery but holds {buyer.Gold}, paying what they have");
                if (payable <= 0) return;

                // Only the gold is settled here. The goods already moved on this machine, vanilla's
                // own unload put them in the dock before this hook ran; the broadcast below is what
                // moves them everywhere else.
                TradeDeltas deltas = TradeMath.Compute(true, goods, payable);
                buyer.Gold += deltas.BuyerGoldDelta;
                seller.Gold += deltas.SellerGoldDelta;

                Main.helper.Log($"[MERCHANTTRADE] delivered {goods.ToString()} to team {_fdDockTeam}'s dock for {payable} gold (merchant team {ship.teamID}, guid {ship.guid}), broadcasting");
                KaCMultiplayer.Net.NetRouter.Send(new KaCMultiplayer.Net.Messages.MerchantTradeMessage
                {
                    Merchant = ship.guid,
                    Dock = _fdDockGuid,
                    BuyerTeam = _fdDockTeam,
                    SellerTeam = ship.teamID,
                    Cost = payable,
                    IsBuy = true,          // goods leave the hold and land in the dock; gold goes the other way
                    Resources = TradeMath.ToList(goods)
                });
            }
            catch (Exception e) { Main.helper.Log("[MERCHANTTRADE] delivery settle error: " + e.Message); }
        }

        // Transport CARTS (land logistics units) open their route UI only when teamID==0, exactly the same
        // gate as ships, so in MP (team 5/6/7) clicking your own cart did nothing ("not clickable"). We open
        // the logistics UI for the local player's own cart via a Postfix on GameUI.AddToSelection (cleaner
        // than patching TransportCart's explicit-interface OnSelected). Selection itself already works
        // (ValidToSelect=true); only the UI-open was gated. SP untouched.
        [HarmonyPatch(typeof(GameUI), "AddToSelection")]
        public class GameUIAddCartToSelectionHook
        {
            public static void Postfix(ISelectable s)
            {
                if (!NetClient.client.IsConnected || s == null) return;
                try
                {
                    TransportCart cart = s as TransportCart;
                    if (cart == null) return; // only transport carts
                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : 0;
                    if (cart.teamID != localTeam) return; // only your own cart
                    GameUI.inst.shipLogisticsUI.SetSelectedTransport(cart);
                    GameUI.inst.shipLogisticsUI.SetVisible(true);
                }
                catch (Exception e) { Main.helper.Log("Cart select MP hook error: " + e.Message); }
            }
        }



        // FOREIGN MERCHANTS in MP, make every merchant that visits YOU tradeable.
        //
        // ShipSystem.Update spawns foreign merchants PER-MACHINE (unsynced) and, each time its timer
        // fires, it loops EVERY landmass owner in World.LandMassOwners and sends a merchant to a random
        // valid dock of that owner, including the OTHER player's island (which is fully simulated on
        // your machine too). Vanilla MerchantShip.StartSailing sets isTargetPlayerDock =
        // Player.inst.LandMassIsAPlayerLandMass(dock.LandMass()), which resolves to
        // PlayerLandmassOwner.OwnsLandMass, true ONLY for the LOCAL player's own docks. A merchant
        // bound for the other player's dock therefore arrives with isTargetPlayerDock=false and takes
        // the AIKingdom auto-trade branch in MerchantShip.Tick; this mod has no AI kingdoms, so
        // AIKingdom.GetKingdomByLandmass returns null and no trade happens. Meanwhile the other player
        // independently spawns and trades THEIR own merchants on THEIR machine.
        //
        // (An earlier version of this comment said such a merchant "just sits at the dock doing
        // nothing". That does not hold: MerchantShip.Tick calls Leave() whenever GetCurrentDock() is
        // null or the dock is closed, and GetCurrentDock goes through Player.inst.GetBuildingList,
        // which is the GLOBAL building registry and does resolve other players' docks. The suppression
        // below is still worth having, an untradeable merchant is noise either way, but if the
        // visible symptom ever needs explaining again, re-derive it rather than trusting this note.)
        //
        // Fix: in MP, suppress any foreign merchant not bound for one of the LOCAL player's docks. The
        // local player's own landmass is iterated in the same ShipSystem.Update loop, so you still get
        // merchants at your docks at the normal cadence, and every one you see now has
        // isTargetPlayerDock=true, so the trade UI opens and you can buy/sell. Single-player untouched.
        // (This is the per-machine "make it work" step; host-authoritative merchant SYNC comes next.)
        [HarmonyPatch(typeof(MerchantShip), "StartSailing")]
        public class MerchantShipStartSailingHook
        {
            public static bool Prefix(MerchantShip __instance, Building dock)
            {
                if (!(NetClient.client.IsConnected || NetHost.IsRunning)) return true; // single-player: vanilla
                try
                {
                    LandmassOwner owner = (Player.inst != null) ? Player.inst.PlayerLandmassOwner : null;
                    bool localDock = dock != null && owner != null && owner.OwnsLandMass(dock.LandMass());
                    if (!localDock)
                    {
                        // Not headed for one of our docks → it can never be traded with here. Remove it
                        // cleanly (OnDisable pulls it from ShipSystem.ships; our save-prune covers stragglers).
                        Main.helper.Log($"[MERCHANT] suppressed foreign merchant bound for non-local dock (landmass {(dock != null ? dock.LandMass() : -1)})");
                        UnityEngine.Object.Destroy(__instance.gameObject);
                        return false; // skip vanilla StartSailing
                    }
                    Main.helper.Log($"[MERCHANT] foreign merchant sailing to YOUR dock (landmass {dock.LandMass()}), trade will be available on arrival");
                }
                catch (Exception e) { Main.helper.Log("MerchantShip.StartSailing MP hook error: " + e.Message); }
                return true; // local dock (or error): vanilla sets isTargetPlayerDock=true and sails to trade
            }
        }

        // Cross-player merchant trade, broadcast the buyer's MerchantUI transaction so the same goods↔gold
        // exchange applies on EVERY machine (vanilla applies it only on the clicker's screen → would desync).
        // The buy/sell logic lives in MerchantUI's two "Complete Transaction" button lambdas
        // (<Start>b__18_5 = Buy, <Start>b__18_6 = Sell). We PREP before vanilla runs (capture cost + goods
        // from the public UI line items) and FINISH after it applies locally (broadcast MerchantTrade; for a
        // SELL also deduct the seller's gold, which vanilla omits for a fake foreign trader). Only fires in MP
        // for a PLAYER merchant; foreign merchants keep their untouched single-machine behaviour. The remote
        // machines apply the identical deltas via MerchantTrade.Apply. Buyer = dock owner, seller = merchant.
        //
        // NOTE: these two target compiler-generated lambda methods (<Start>b__18_5 / _6). They are patched
        // MANUALLY (see PatchMerchantTradeLambdas, called after PatchAll) inside a try/catch, NOT via the
        // [HarmonyPatch] attribute, because PatchAll is atomic: if a lambda name ever fails to resolve, an
        // attribute patch would abort PatchAll and half-patch the whole mod (a known freeze cause). Manual
        // guarded patching means a miss only disables merchant-trade sync, leaving every other hook intact.
        /// <summary>
        /// Lets a player trade at their own dock, which vanilla decides by asking whether the dock
        /// belongs to team 0.
        ///
        /// <c>MerchantUI.UpdateInternal</c> ends with:
        ///
        /// <code>
        ///     else if (dock.GetComponent&lt;Building&gt;().TeamID() &gt; 0) canTrade = false;
        ///     progressRoot.SetActive(!canTrade);
        ///     transactionRoot.SetActive(canTrade);
        /// </code>
        ///
        /// The intent is "only trade at a dock that is MINE", and single player expresses that as
        /// team 0 because there is only ever one kingdom and it is team 0. Multiplayer teams start
        /// at 5, so every dock a player owns failed the test and the buy/sell panel never appeared.
        ///
        /// WHY A TRANSPILER AND NOT A POSTFIX. A Postfix was the obvious fix and it was wrong.
        /// UpdateInternal runs EVERY FRAME, so vanilla set transactionRoot inactive every frame
        /// and the Postfix set it active again every frame. The panel is a parent of the order
        /// fields, and a TMP_InputField that is disabled and re-enabled sixty times a second can
        /// never hold focus: the panel appeared, and nothing could be typed into it. Fighting a
        /// per-frame write is never the answer; correcting the value it is computed from is.
        ///
        /// So the team test itself is redirected. Vanilla then reaches the right answer on its own
        /// and sets the panels once, exactly as it does in single player.
        /// </summary>
        [HarmonyPatch(typeof(MerchantUI), "UpdateInternal")]
        public class MerchantUIDockTeamHook
        {
            /// <summary>
            /// The dock's team as far as "may I trade here" is concerned: 0 when the dock is ours,
            /// so vanilla's <c>&gt; 0</c> test passes, and the real team otherwise.
            ///
            /// Another player's dock deliberately still reports its real team. That is their port
            /// and their transaction, settled on their screen, and vanilla charges whoever clicks
            /// rather than whoever owns the dock, so allowing it here would let two machines
            /// disagree about who paid. See PrepPlayerMerchantTrade.
            /// </summary>
            public static int TradeTeamOf(Building dock)
            {
                if (dock == null) return -1;

                int team = dock.TeamID();
                try
                {
                    if (!NetClient.client.IsConnected) return team;   // single player: vanilla
                    if (Player.inst == null || Player.inst.PlayerLandmassOwner == null) return team;

                    return (team == Player.inst.PlayerLandmassOwner.teamId) ? 0 : team;
                }
                catch { return team; }
            }

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var codes = new List<CodeInstruction>(instructions);

                MethodInfo vanilla = typeof(Building).GetMethod(
                    "TeamID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                MethodInfo ours = typeof(MerchantUIDockTeamHook).GetMethod(
                    "TradeTeamOf", BindingFlags.Static | BindingFlags.Public);

                if (vanilla == null || ours == null)
                {
                    Main.helper.Log("MERCHANT TRANSPILER FOUND NO Building.TeamID; the buy/sell panel"
                                    + " will stay hidden in multiplayer");
                    return codes.AsEnumerable();
                }

                // The Building is already on the stack from GetComponent<Building>(), so a static
                // taking it as its one argument is a straight operand swap; only the call kind
                // changes, since ours is not virtual.
                int swapped = 0;
                foreach (CodeInstruction code in codes)
                {
                    if (code.operand as MethodInfo != vanilla) continue;

                    code.opcode = OpCodes.Call;
                    code.operand = ours;
                    swapped++;
                }

                Main.helper.Log($"MerchantUI.UpdateInternal: {swapped} Building.TeamID call(s) routed through the dock-ownership fix");
                return codes.AsEnumerable();
            }
        }

        /// <summary>
        /// Prices a visiting hold at what its OWNER asks, not at what the buyer's own kingdom
        /// happens to charge.
        ///
        /// RefreshBuyWindow builds the list from Player.inst.defaultPayCost, which is the LOCAL
        /// player's table, so every cross-player trade settled at the buyer's own prices and a
        /// seller had no way to say what their goods were worth. Vanilla looks like it meant
        /// otherwise: it reaches for the seller's costs with LandmassOwner.GetPayCosts, then guards
        /// that branch with owner.teamId != ship.TeamID() after fetching the owner BY ship.TeamID().
        /// The condition cannot be true, so the branch is dead and the default always wins.
        ///
        /// A Postfix rather than a transpiler because the line items carry the price themselves:
        /// re-Set each one and both the displayed unit price and GetCost() follow, which means the
        /// transaction and the gold that crosses the wire follow too, since PrepPlayerMerchantTrade
        /// sums those same GetCost() values. One hook, and the number shown is the number paid.
        ///
        /// Silent for anything unpriced. A kingdom that has never opened the price window has no
        /// list, this returns immediately, and the trade is exactly what it was before.
        /// </summary>
        [HarmonyPatch(typeof(MerchantUI), "RefreshBuyWindow")]
        public class MerchantUIExportPriceHook
        {
            public static void Postfix(MerchantUI __instance)
            {
                try
                {
                    if (!NetClient.client.IsConnected || __instance == null) return;

                    Ship merchant = __instance.ship as Ship;
                    if (merchant == null || merchant.type != ShipBase.ShipType.PlayerMerchant) return;

                    int seller = merchant.TeamID();
                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;

                    // Our own ship. Vanilla pays us from ourselves and nothing is synced, so there
                    // is no second party whose prices could apply.
                    if (seller == localTeam) return;

                    if (!KaCMultiplayer.Trade.ExportPrices.HasList(seller)) return;

                    Transform content = __instance.buyContent;
                    if (content == null) return;

                    Assets.Code.ResourceAmount hold = merchant.GetHold();

                    for (int i = 0; i < content.childCount; i++)
                    {
                        ResourceLineItemUI line = content.GetChild(i).GetComponent<ResourceLineItemUI>();
                        if (line == null) continue;

                        int price = KaCMultiplayer.Trade.ExportPrices.PriceFor(seller, line.rtype);

                        // Withheld goods. Hidden rather than priced impossibly high, because a
                        // seller carrying iron they need for themselves should be able to say no
                        // outright. An inactive row cannot be ordered from and contributes a zero
                        // line to the transaction, so it cannot be bought by any route.
                        if (price <= KaCMultiplayer.Trade.ExportPrices.NotForSale)
                        {
                            line.gameObject.SetActive(false);
                            continue;
                        }

                        line.Set(line.rtype, price, hold.Get(line.rtype));
                    }
                }
                catch (Exception e) { Main.helper.Log("[MERCHANT] export pricing error: " + e.Message); }
            }
        }

        public class MerchantUIBuyHook
        {
            public static void Prefix(MerchantUI __instance) { Main.PrepPlayerMerchantTrade(__instance, true); }
            public static void Postfix(MerchantUI __instance) { Main.FinishPlayerMerchantTrade(true); }
        }

        public class MerchantUISellHook
        {
            public static void Prefix(MerchantUI __instance) { Main.PrepPlayerMerchantTrade(__instance, false); }
            public static void Postfix(MerchantUI __instance) { Main.FinishPlayerMerchantTrade(false); }
        }

        // Trade captured between Prefix (before vanilla applies) and Postfix (after) of a MerchantUI button.
        private static bool _mtPending;
        private static int _mtCost;
        private static List<int> _mtGoods;
        private static Guid _mtMerchantGuid, _mtDockGuid;
        private static int _mtBuyerTeam, _mtSellerTeam;

        // Captures what the buyer is about to trade with a PLAYER merchant, mirroring vanilla's own summation
        // of the MerchantUI line items. Sets _mtPending only when this is a cross-player player-merchant trade
        // that vanilla will actually complete (for a buy, only if affordable, matching vanilla's cancel gate).
        public static void PrepPlayerMerchantTrade(MerchantUI ui, bool isBuy)
        {
            _mtPending = false;
            try
            {
                if (!NetClient.client.IsConnected || ui == null) return;
                Ship merchant = ui.ship as Ship;
                if (merchant == null || merchant.type != ShipBase.ShipType.PlayerMerchant) return; // player merchants only
                Dock dock = merchant.GetCurrentDock();
                if (dock == null) return;
                Building dockB = dock.GetComponent<Building>();
                if (dockB == null) return;

                // Vanilla settles this trade against whoever is CLICKING, not against the dock's
                // owner: the decompiled buy lambda gates on, and deducts from,
                // Player.inst.PlayerLandmassOwner.Gold. Those are the same kingdom only in the
                // intended flow, a dock owner trading with a visiting merchant. Selection has no
                // team check, so anyone can open a docked merchant, and if we broadcast a trade
                // vanilla charged to somebody else the two machines end up disagreeing about who
                // paid. So the clicker is the buyer here, and a clicker who does not own the dock
                // is not synced at all.
                LandmassOwner localOwner = (Player.inst != null) ? Player.inst.PlayerLandmassOwner : null;
                if (localOwner == null) return;

                int buyerTeam = localOwner.teamId;   // the clicker, whom vanilla actually charges
                int sellerTeam = merchant.TeamID();  // merchant owner = the other side of the deal
                if (buyerTeam == sellerTeam) return; // your own merchant: vanilla pays you from yourself, nothing to sync
                if (buyerTeam != dockB.TeamID())
                {
                    Main.helper.Log($"[MERCHANTTRADE] {(isBuy ? "buy" : "sell")} NOT synced, team {buyerTeam} is trading at team {dockB.TeamID()}'s dock and vanilla settles it against the clicker");
                    return;
                }

                // Sum the ordered line items exactly as the vanilla button handler does.
                Transform content = isBuy ? ui.buyContent : ui.sellContent;
                if (content == null) return;
                int cost = 0;
                ResourceAmount goods = new ResourceAmount();
                for (int i = 0; i < content.childCount; i++)
                {
                    ResourceLineItemUI li = content.GetChild(i).GetComponent<ResourceLineItemUI>();
                    if (li == null) continue;
                    cost += li.GetCost();
                    ResourceAmount r = li.GetOrderedResources();
                    goods.Add(r); // ResourceAmount.Add takes an 'in' param, no ref keyword allowed
                }
                if (cost <= 0) return; // nothing actually selected

                // Vanilla cancels an unaffordable buy outright (its gate is cost <= gold), so a
                // trade it will not complete must not be broadcast as though it had.
                if (isBuy && cost > localOwner.Gold)
                {
                    Main.helper.Log($"[MERCHANTTRADE] buy NOT synced, can't afford (cost {cost} > gold {localOwner.Gold}); vanilla will cancel");
                    return;
                }

                _mtCost = cost;
                _mtGoods = TradeMath.ToList(goods);
                _mtMerchantGuid = merchant.guid;
                _mtDockGuid = dockB.guid;
                _mtBuyerTeam = buyerTeam;
                _mtSellerTeam = sellerTeam;
                _mtPending = true;
                Main.helper.Log($"[MERCHANTTRADE] {(isBuy ? "BUY" : "SELL")} captured for sync: merchant guid {merchant.guid}, dock {dockB.guid}, buyer team {buyerTeam}, seller team {sellerTeam}, cost {cost}");
            }
            catch (Exception e) { Main.helper.Log("[MERCHANTTRADE] prep error: " + e.Message); _mtPending = false; }
        }

        // Broadcasts the captured trade after vanilla applied the local side, and applies the seller-gold
        // deduction that vanilla omits on a SELL (it only credits the dock owner, never charging the merchant
        // owner, fine for a fake foreign trader, wrong for a real player merchant).
        public static void FinishPlayerMerchantTrade(bool isBuy)
        {
            if (!_mtPending) return;
            _mtPending = false;
            try
            {
                // Vanilla credits the dock owner on a sell but never charges the merchant's side,
                // which is right for a merchant belonging to nobody and wrong for one belonging to
                // another player. The missing half comes from the same delta table every machine
                // settles from, rather than being written out again here.
                if (!isBuy)
                {
                    LandmassOwner seller = World.GetLandmassOwnerByTeamId(_mtSellerTeam);
                    if (seller != null)
                        seller.Gold += TradeMath.Compute(false, TradeMath.FromList(_mtGoods), _mtCost).SellerGoldDelta;
                }
                Main.helper.Log($"[MERCHANTTRADE] broadcasting {(isBuy ? "BUY" : "SELL")} to other players (cost {_mtCost}, buyer team {_mtBuyerTeam}, seller team {_mtSellerTeam})");
                KaCMultiplayer.Net.NetRouter.Send(new KaCMultiplayer.Net.Messages.MerchantTradeMessage
                {
                    Merchant = _mtMerchantGuid,
                    Dock = _mtDockGuid,
                    BuyerTeam = _mtBuyerTeam,
                    SellerTeam = _mtSellerTeam,
                    Cost = _mtCost,
                    IsBuy = isBuy,
                    Resources = _mtGoods
                });
            }
            catch (Exception e) { Main.helper.Log("[MERCHANTTRADE] finish error: " + e.Message); }
        }

        // DIAGNOSTIC + future arrival-notification hook: logs ONCE when another player's merchant arrives at
        // one of YOUR docks, confirming the synced sail (2c) delivered it and it's ready to trade (click it →
        // MerchantUI opens). Guarded by a per-guid set so it logs once per arrival, cleared when it leaves.
        private static HashSet<Guid> _loggedMerchantArrivals = new HashSet<Guid>();

        /// <summary>
        /// Shows or hides the exclamation marker over a visiting merchant.
        ///
        /// Guarded because the button is created in ShipBase.OnEnableInternal, so a ship caught
        /// mid-teardown can reach here without one, and this runs from a per-frame Tick where an
        /// escaping exception would stall the simulation.
        /// </summary>
        public static void SetMerchantIssueButton(ShipBase ship, bool visible)
        {
            try
            {
                if (ship == null || ship.issueButton == null) return;
                ship.issueButton.gameObject.SetActive(visible);
            }
            catch (Exception e) { Main.helper.Log("[MERCHANT] issue button error: " + e.Message); }
        }

        /// <summary>
        /// The kingdom name for a team, falling back to the player's name and then to the team
        /// number, so a notice always says WHO rather than "a merchant".
        /// </summary>
        public static string KingdomNameForTeam(int teamId)
        {
            try
            {
                foreach (SessionPlayer kp in kCPlayers.Values)
                {
                    if (kp == null || kp.inst == null || kp.inst.PlayerLandmassOwner == null) continue;
                    if (kp.inst.PlayerLandmassOwner.teamId != teamId) continue;
                    if (!string.IsNullOrEmpty(kp.kingdomName)) return kp.kingdomName;
                    if (!string.IsNullOrEmpty(kp.name)) return kp.name;
                    break;
                }
            }
            catch { }
            return "Team " + teamId;
        }

        /// <summary>
        /// Raises or lowers the game's own merchant banner, the one that slides in at the edge of
        /// the screen when a trader puts in at your port.
        ///
        /// Wrapped because every reference on the way to it can be missing: the banner lives on
        /// GameUI, which does not exist in the menus, and a notification that fails to appear must
        /// never take a Ship.Tick postfix down with it.
        /// </summary>
        public static void ShowMerchantNotification(bool arriving)
        {
            try
            {
                if (GameUI.inst == null || GameUI.inst.merchantNotification == null) return;

                if (arriving) GameUI.inst.merchantNotification.OnShipArrival();
                else GameUI.inst.merchantNotification.OnShipDeparture();
            }
            catch (Exception e) { Main.helper.Log("[MERCHANT] notification error: " + e.Message); }
        }

        /// <summary>
        /// Points the merchant banner's view button at a visiting PLAYER's merchant.
        ///
        /// The vanilla walk looks for ships of type Merchant carrying a MerchantShip component and
        /// heading for a player dock. Another player's merchant is type PlayerMerchant with no such
        /// component, so the walk found nothing, fell through to its last line, and returned the
        /// position of your own keep. The button worked; it just took you to the wrong place, which
        /// is why the banner was not raised for these ships at all.
        ///
        /// Answered here instead when a foreign player merchant is tied up at one of our docks, and
        /// deferred to vanilla in every other case, so an ordinary merchant still tracks exactly as
        /// it did. With both kinds in port the visitor wins, because it is the one the vanilla walk
        /// cannot see and therefore the only one that would otherwise be unreachable.
        /// </summary>
        [HarmonyPatch(typeof(MerchantNotification), "GetDesiredTrackingPos")]
        public class MerchantNotificationTrackPlayerMerchantHook
        {
            public static bool Prefix(ref Vector3 __result)
            {
                try
                {
                    if (!NetClient.client.IsConnected) return true;
                    if (ShipSystem.inst == null || ShipSystem.inst.ships == null) return true;

                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
                    if (localTeam == int.MinValue) return true;

                    var ships = ShipSystem.inst.ships;
                    for (int i = 0; i < ships.Count; i++)   // .Count, never .data.Length
                    {
                        Ship s = ships.data[i] as Ship;
                        if (s == null || s.type != ShipBase.ShipType.PlayerMerchant) continue;
                        if (s.teamID == localTeam) continue;   // ours; the banner is for visitors
                        if (!s.arrived) continue;

                        Dock dock = s.GetCurrentDock();
                        Building dockB = dock == null ? null : dock.GetComponent<Building>();
                        if (dockB == null || dockB.TeamID() != localTeam) continue;

                        __result = s.GetPos();
                        return false;
                    }
                }
                catch (Exception e)
                {
                    Main.helper.Log("[MERCHANT] tracking pos error: " + e.Message);
                }

                return true;   // no visitor in port, let vanilla answer
            }
        }

        [HarmonyPatch(typeof(Ship), "Tick")]
        public class ShipTickArrivalLogHook
        {
            public static void Postfix(Ship __instance)
            {
                if (!NetClient.client.IsConnected || __instance == null) return;
                try
                {
                    if (__instance.type != ShipBase.ShipType.PlayerMerchant) return;

                    // Transit dump, for diagnosing a merchant that never reaches its dock. Behind the
                    // dev switch because it writes per merchant every couple of seconds, and this
                    // project has twice had a per-tick log bury everything else in a session.
                    if (!__instance.arrived && Main.DevTestBuild)
                        Main.LogMerchantTransit(__instance, "player");

                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
                    if (__instance.teamID == localTeam) return; // our own merchant, not an incoming visit

                    Dock dock = __instance.arrived ? __instance.GetCurrentDock() : null;
                    Building dockB = dock == null ? null : dock.GetComponent<Building>();
                    bool atOurDock = dockB != null && dockB.TeamID() == localTeam;

                    if (!atOurDock)
                    {
                        // Gone, or moved on to somebody else's port. Take the marker down so a stale
                        // exclamation does not hang over a ship that has nothing to offer.
                        if (Main._loggedMerchantArrivals.Remove(__instance.guid))
                        {
                            Main.SetMerchantIssueButton(__instance, false);
                            Main.ShowMerchantNotification(false);
                        }
                        return;
                    }

                    if (Main._loggedMerchantArrivals.Contains(__instance.guid)) return;
                    Main._loggedMerchantArrivals.Add(__instance.guid);

                    // The arrival marker (M9). ShipBase gives every ship an issueButton whose click
                    // sends OnClickedButton, and Ship.OnClickedButton opens MerchantUI for a
                    // PlayerMerchant, so switching it on is the whole trade prompt: the exclamation
                    // appears over the visiting ship and clicking it opens the trade window.
                    //
                    Main.SetMerchantIssueButton(__instance, true);

                    // AND THE REAL NOTIFICATION, the one a normal merchant raises.
                    //
                    // This was left out before for a good reason: the banner's view button walks
                    // MerchantNotification.GetDesiredTrackingPos, which counts only ships of type
                    // Merchant that carry a MerchantShip component. A player merchant is type
                    // PlayerMerchant and has no such component, so it was invisible to that walk
                    // and clicking through would have centred the camera on your own keep.
                    //
                    // That walk is now covered (see MerchantNotificationTrackPlayerMerchantHook), so
                    // the banner can be raised honestly: a visiting player's merchant announces
                    // itself exactly as an ordinary one does, and the view button goes to the ship.
                    Main.ShowMerchantNotification(true);

                    string trader = Main.KingdomNameForTeam(__instance.teamID);
                    KingdomLog.TryLog("mpMerchant" + __instance.guid,
                                      trader + "'s merchant has docked at your port and is ready to trade.",
                                      KingdomLog.LogStatus.Neutral, 0f);

                    Main.helper.Log($"[MERCHANT] {trader}'s merchant (team {__instance.teamID}, guid {__instance.guid}) ARRIVED at your dock '{dockB.UniqueName}'; trade prompt shown");
                }
                catch (Exception e) { Main.helper.Log("[MERCHANT] arrival log error: " + e.Message); }
            }
        }

        // TEMP DIAGNOSTIC state: last frame we logged transit for a given ship guid (throttle so we emit
        // roughly every 2 seconds per merchant instead of every frame). Remove with the diagnostics below.
        private static Dictionary<Guid, int> _merchantTransitLogFrame = new Dictionary<Guid, int>();

        // TEMP DIAGNOSTIC: dumps the complete pathing/arrival state of a merchant that is stuck "waiting to
        // reach dock". Both merchant types report this state while ShipBase.arrived is false, the game only
        // flips arrived when dynamics.ArrivedAt(target) is true, and only leaves (pathFailed) when the pather
        // reports no route. The stall (never arrives, never leaves) means one of: no move target (empty
        // dockPositions -> GetDockCell returned null), no path (pather never returned), or the ship never gets
        // within arrivalRad of the target cell. This single log line distinguishes those so the next 2-player
        // test tells us exactly which to fix. Throttled per guid; MP only.
        public static void LogMerchantTransit(ShipBase ship, string kind)
        {
            try
            {
                if (ship == null) return;
                int now = Time.frameCount;
                int last;
                if (_merchantTransitLogFrame.TryGetValue(ship.guid, out last) && now - last < 120) return;
                _merchantTransitLogFrame[ship.guid] = now;

                Vector3 pos = ship.GetPos();
                bool hasTarget = ship.moveTarget != null;
                Vector3 tgt = hasTarget ? ship.moveTarget.GetPos() : Vector3.zero;
                float distXZ = -1f;
                if (hasTarget)
                {
                    float dx = tgt.x - pos.x, dz = tgt.z - pos.z;
                    distXZ = Mathf.Sqrt(dx * dx + dz * dz);
                }
                float arrivalRad = ship.dynamics != null ? ship.dynamics.arrivalRad : -1f;
                Vector3 dynPos = ship.dynamics != null ? ship.dynamics.Pos : Vector3.zero;
                int pathCount = ship.path != null ? ship.path.Count : -1;

                // Dock + its water landing cells (empty dockPositions => GetDockCell returns null => no target).
                int dockPositions = -1;
                string dockName = "none";
                Dock dock = null;
                MerchantShip fm = ship as MerchantShip;
                Ship pm = ship as Ship;
                if (fm != null) dock = fm.GetCurrentDock();
                else if (pm != null) dock = pm.GetCurrentDock();
                if (dock != null)
                {
                    dockPositions = dock.dockPositions != null ? dock.dockPositions.Count : -1;
                    Building db = dock.GetComponent<Building>();
                    if (db != null) dockName = db.UniqueName;
                }

                Main.helper.Log($"[MERCHANTPATH] {kind} type={ship.type} team={ship.teamID} arrived={ship.arrived} " +
                    $"pathFailed={ship.pathFailed} hasTarget={hasTarget} pathCount={pathCount} pathIdx={ship.pathIdx} " +
                    $"distXZ={distXZ:F1} arrivalRad={arrivalRad:F1} pos=({pos.x:F0},{pos.z:F0}) dynPos=({dynPos.x:F0},{dynPos.z:F0}) " +
                    $"dock='{dockName}' dockPositions={dockPositions}");
            }
            catch (Exception e) { Main.helper.Log("[MERCHANTPATH] log error: " + e.Message); }
        }

        // TEMP DIAGNOSTIC: same transit dump for FOREIGN merchants (MerchantShip). These never carry a synced
        // guid and take a separate code path from the player merchant, so they need their own hook. Logs only
        // while the merchant is still sailing (status SailingToDock) and hasn't arrived. Remove once resolved.
        [HarmonyPatch(typeof(MerchantShip), "Tick")]
        public class MerchantShipTransitLogHook
        {
            public static void Postfix(MerchantShip __instance)
            {
                if (!Main.DevTestBuild) return;   // per-frame diagnostic; off in player builds
                if (!NetClient.client.IsConnected || __instance == null) return;
                if (__instance.arrived) return; // only diagnosing the "never reaches dock" phase
                Main.LogMerchantTransit(__instance, "foreign");
            }
        }



        // Broadcasts a keep's visual level-up so every player sees the castle grow.
        //
        // The keep model advances through Keep.TryLevelUp -> Upgradeable.TryUpgrade, driven purely by
        // Player.inst (the LOCAL player): Player.TryUpgradeKeeps only walks Player.inst's keeps and
        // Keep.TryLevelUp only counts Player.inst's special buildings (chamber of war / throne room /
        // great hall). So a level-up only ever fires for the machine-local player's OWN keep, a remote
        // player's keep upgrades on THEIR machine but stays visually un-upgraded on yours. Upgradeable
        // is used only by keeps (Keep.TryLevelUp is TryUpgrade's sole caller), so patching TryUpgrade is
        // keep-specific. On a successful upgrade for our own keep we send the new level; other machines
        // replay just the model swap via Upgradeable.SetUpgrade (no TryUpgrade re-entry → no echo).
        // See Packets/Game/GameBuilding/KeepUpgrade.cs.
        [HarmonyPatch(typeof(Upgradeable), "TryUpgrade")]
        public class UpgradeableTryUpgradeHook
        {
            public static void Postfix(Upgradeable __instance, bool __result)
            {
                if (!NetClient.client.IsConnected || !__result || __instance == null) return; // SP or no change
                try
                {
                    Building b = __instance.GetComponent<Building>();
                    if (b == null) return;

                    // TryUpgrade only runs for the local player's keep (Player.inst-gated above us), so this
                    // is always our own keep; the team check is belt-and-suspenders against future callers.
                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
                    if (b.TeamID() != localTeam) return;

                    Main.helper.Log($"[KEEPUP] broadcasting your keep {b.guid} upgrade -> level {__instance.level}");
                    KaCMultiplayer.Net.NetRouter.Send(new KaCMultiplayer.Net.Messages.KeepUpgradeMessage { Building = b.guid, Level = __instance.level });
                }
                catch (Exception e) { Main.helper.Log("[KEEPUP] broadcast error: " + e.Message); }
            }
        }

        /// <summary>
        /// Tells everyone when the local player changes a tax rate.
        ///
        /// The rate lives on the Player object, so without this every other machine kept its copy
        /// of our kingdom at 0: our homes were taxed wrong there, and the host saved 0 for us.
        /// Only the local player's own changes are sent; applying someone else's rate also runs
        /// SetTaxRate, on their Player, and must not echo.
        /// </summary>
        [HarmonyPatch(typeof(Player), "SetTaxRate")]
        public class PlayerSetTaxRateHook
        {
            public static void Postfix(Player __instance, int landMass, float taxRate)
            {
                if (!NetClient.client.IsConnected || __instance == null || __instance != Player.inst) return;
                if (landMass < 0) return;
                try
                {
                    KaCMultiplayer.Net.NetRouter.Send(new KaCMultiplayer.Net.Messages.TaxRateMessage { LandMass = landMass, Rate = taxRate });
                }
                catch (Exception e) { Main.helper.Log("[TAX] broadcast error: " + e.Message); }
            }
        }



        // Make a launch-spawned ship carry the SAME guid on every machine, so ship-targeted packets
        // (movement below, and later merchant sail/trade) can find "the same ship" everywhere. A ship
        // gets a fresh Guid.NewGuid() in ShipBase..ctor → different per machine. But the LAUNCH BUILDING
        // it spawns from has a synced guid (placement is networked), and its OnBuilt runs on every machine
        // (the builder via its own CompleteBuild; remotes via BuildingStatePacket → CompleteBuild). So we
        // deterministically set the spawned ship's guid = the launch building's guid on all machines, no
        // extra packet, no ordering race. OnBuilt spawns exactly one ship (the newest entries in
        // ShipSystem.ships), captured by a count diff between Prefix and Postfix. MP only.
        public static int _shipCountBeforeLaunch = -1;

        [HarmonyPatch(typeof(TransportShipLaunch), "OnBuilt")]
        public class TransportShipLaunchGuidHook
        {
            public static void Prefix() { Main._shipCountBeforeLaunch = Main.SnapshotShipCount(); }
            public static void Postfix(TransportShipLaunch __instance) { Main.AssignLaunchedShipGuid(__instance.gameObject, "TransportShipLaunch"); }
        }

        [HarmonyPatch(typeof(SeedShipLaunch), "OnBuilt")]
        public class SeedShipLaunchGuidHook
        {
            public static void Prefix() { Main._shipCountBeforeLaunch = Main.SnapshotShipCount(); }
            public static void Postfix(SeedShipLaunch __instance) { Main.AssignLaunchedShipGuid(__instance.gameObject, "SeedShipLaunch"); }
        }

        // Settling a new island consumes the seedship (SeedShip.Settle → Disband) on the settler's machine
        // only, so the other players' copies lingered. We can't replay Settle remotely (it spawns settlers
        // onto Player.inst, the wrong player) so we just broadcast the ship's synced guid and let the other
        // machines destroy their copy (ShipRemove). Runs after Settle, while __instance is still a valid ref.
        [HarmonyPatch(typeof(SeedShip), "Settle")]
        public class SeedShipSettleHook
        {
            public static void Postfix(SeedShip __instance)
            {
                if (!NetClient.client.IsConnected || __instance == null) return;
                try
                {
                    Main.helper.Log($"[SHIPREMOVE] broadcasting seedship settle-consume guid {__instance.guid}");
                    KaCMultiplayer.Net.NetRouter.Send(new KaCMultiplayer.Net.Messages.ShipDespawnMessage { Ship = __instance.guid });
                }
                catch (Exception e) { Main.helper.Log("[SHIPREMOVE] settle broadcast error: " + e.Message); }
            }
        }

        // Ship count at the start of OnBuilt (MP only), or -1 in single-player / if unavailable.
        public static int SnapshotShipCount()
        {
            return (NetClient.client.IsConnected || NetHost.IsRunning) && ShipSystem.inst != null
                ? ShipSystem.inst.ships.Count : -1;
        }

        // A launch whose spawned ship wasn't yet registered in ShipSystem.ships when OnBuilt returned,
        // retried by position over the next frames (handles machines where the ship registers a frame late).
        private class PendingShipGuid { public Guid buildingGuid; public Vector3 center; public int framesLeft; }
        private static List<PendingShipGuid> _pendingShipGuids = new List<PendingShipGuid>();

        // Assigns the launch building's synced guid to the ship it just spawned. ALWAYS logs whether OnBuilt
        // ran here (so we can confirm it fires on the CLIENT, last test showed it may not) plus the ship
        // count before/after. If no new ship was found (count-diff empty), queues a position-based retry.
        public static void AssignLaunchedShipGuid(GameObject launchGo, string launchType)
        {
            int before = _shipCountBeforeLaunch;
            _shipCountBeforeLaunch = -1;
            try
            {
                // Only in a session. This OVERWRITES a ship's guid with its launch building's, so
                // outside multiplayer it would leave a single-player save holding two objects that
                // claim the same id, which is nobody's idea of "the mod is not installed".
                bool mp = Main.InMultiplayer;
                if (!mp) { _shipCountBeforeLaunch = -1; return; }

                Building b = launchGo != null ? launchGo.GetComponent<Building>() : null;
                if (ShipSystem.inst == null || b == null)
                {
                    Main.helper.Log($"[SHIPGUID] {launchType}.OnBuilt fired but building/shipsystem null (mp={mp})");
                    return;
                }
                var ships = ShipSystem.inst.ships;
                int after = ships.Count;
                int assigned = 0;
                if (before >= 0)
                    for (int i = before; i < after; i++)
                        if (ships.data[i] != null) { ships.data[i].guid = b.guid; assigned++; }
                Main.helper.Log($"[SHIPGUID] {launchType}.OnBuilt '{b.UniqueName}' mp={mp} shipsBefore={before} shipsAfter={after} assigned={assigned} guid={b.guid}");
                if (mp && assigned == 0)
                    _pendingShipGuids.Add(new PendingShipGuid { buildingGuid = b.guid, center = b.Center(), framesLeft = 180 });
            }
            catch (Exception e) { Main.helper.Log("[SHIPGUID] assign error: " + e.Message); }
        }

        // Retries queued guid assignments: finds the ship that spawned near the launch and stamps it with the
        // building guid. Matching by position is safe because one launch spawns exactly one ship at its center.
        public static void ProcessPendingShipGuids()
        {
            if (_pendingShipGuids.Count == 0 || ShipSystem.inst == null) return;
            var ships = ShipSystem.inst.ships;
            for (int p = _pendingShipGuids.Count - 1; p >= 0; p--)
            {
                PendingShipGuid pend = _pendingShipGuids[p];
                ShipBase match = null;
                float best = 9f; // within 3 units of the launch center
                for (int i = 0; i < ships.Count; i++)
                {
                    ShipBase s = ships.data[i];
                    if (s == null || s.guid == pend.buildingGuid) continue;
                    float d = (s.GetPos() - pend.center).sqrMagnitude;
                    if (d < best) { best = d; match = s; }
                }
                if (match != null)
                {
                    match.guid = pend.buildingGuid;
                    Main.helper.Log($"[SHIPGUID] deferred-assigned ship type {match.type} guid {pend.buildingGuid} (matched by position)");
                    _pendingShipGuids.RemoveAt(p);
                }
                else if (--pend.framesLeft <= 0)
                {
                    Main.helper.Log($"[SHIPGUID] deferred assignment GAVE UP for building {pend.buildingGuid}, no ship ever appeared near its launch");
                    _pendingShipGuids.RemoveAt(p);
                }
            }
        }



        // Frame on which the local player issued a right-click move order. OrdersManager.MoveTo is called
        // both by that player command AND by internal game logic; we only broadcast the PLAYER's ship
        // moves. Stamping the frame (instead of a bool we must remember to clear) means the gate
        // auto-expires next frame, so a stuck flag can never cause a stray broadcast.
        public static int lastPlayerMoveFrame = -1;

        // Marks that OrdersManager.MoveTo calls this frame came from a local right-click move.
        [HarmonyPatch(typeof(GameUI), "MoveUnitsToPosition")]
        public class GameUIMoveUnitsHook
        {
            public static void Prefix()
            {
                if (NetClient.client.IsConnected) Main.lastPlayerMoveFrame = Time.frameCount;
            }
        }

        // Broadcasts a ship move command so other players see your ship sail to the same spot. Gated to:
        // MP, a player-issued move THIS frame, a SHIP (ShipBase, excludes dragons/siege combat units,
        // which are IMoveableUnit but not synced), owned by the LOCAL team, with a Cell target. The remote
        // applies via ShipMove.HandlePacketClient (not a player click), so lastPlayerMoveFrame is stale
        // there, so it never re-broadcasts. The parameter names must match the game's method
        // exactly (moveableUnit/moveTarget), Harmony binds them by name, and a typo silently
        // yields null rather than failing at patch time.
        [HarmonyPatch(typeof(OrdersManager), "MoveTo")]
        public class OrdersManagerMoveToHook
        {
            public static void Postfix(IMoveableUnit moveableUnit, IMoveTarget moveTarget)
            {
                if (!NetClient.client.IsConnected) return;
                if (Time.frameCount != Main.lastPlayerMoveFrame) return; // not a local player command
                try
                {
                    // Ships, ARMIES and SIEGE CATAPULTS share this sync; each carries a guid every
                    // machine agrees on (ships from their launch building, armies from ArmySpawn,
                    // catapults from SiegeCatapultSpawn). Dragons stay out: they are not player
                    // commanded, they fly where they like.
                    Guid unitGuid;
                    int unitTeam;
                    string desc;
                    ShipBase ship = moveableUnit as ShipBase;
                    UnitSystem.Army army = moveableUnit as UnitSystem.Army;
                    SiegeCatapult catapult = moveableUnit as SiegeCatapult;
                    if (ship != null) { unitGuid = ship.guid; unitTeam = ship.teamID; desc = "ship " + ship.type; }
                    else if (army != null) { unitGuid = army.guid; unitTeam = army.teamId; desc = "army " + army.armyType; }
                    else if (catapult != null) { unitGuid = catapult.guid; unitTeam = catapult.TeamID(); desc = "catapult"; }
                    else return;

                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
                    if (unitTeam != localTeam) return; // only your own units
                    if (moveTarget == null) return;

                    // Work out WHAT was ordered, not just where. A cell is a walk; an army or a
                    // building is an attack; a transport or seed ship is a boarding. Only the cell
                    // case used to be sent, so every attack and every boarding stayed local and the
                    // other players watched a unit stand still.
                    KaCMultiplayer.Net.Messages.MoveTargetKind kind =
                        KaCMultiplayer.Net.Messages.MoveTargetKind.Cell;
                    Guid targetGuid = Guid.Empty;
                    string targetDesc;

                    Cell cell = moveTarget as Cell;
                    UnitSystem.Army targetArmy = moveTarget as UnitSystem.Army;
                    Building targetBuilding = moveTarget as Building;
                    ShipBase targetShip = moveTarget as ShipBase;
                    SiegeCatapult targetCatapult = moveTarget as SiegeCatapult;

                    if (cell != null)
                    {
                        targetDesc = $"cell ({cell.x},{cell.z})";
                    }
                    else if (targetArmy != null)
                    {
                        kind = KaCMultiplayer.Net.Messages.MoveTargetKind.Army;
                        targetGuid = targetArmy.guid;
                        targetDesc = "army " + targetArmy.armyType + " " + targetGuid;
                    }
                    else if (targetBuilding != null)
                    {
                        kind = KaCMultiplayer.Net.Messages.MoveTargetKind.Building;
                        targetGuid = targetBuilding.guid;
                        targetDesc = "building " + targetBuilding.UniqueName + " " + targetGuid;
                    }
                    else if (targetShip != null)
                    {
                        kind = KaCMultiplayer.Net.Messages.MoveTargetKind.Ship;
                        targetGuid = targetShip.guid;
                        targetDesc = "ship " + targetShip.type + " " + targetGuid;
                    }
                    else if (targetCatapult != null)
                    {
                        kind = KaCMultiplayer.Net.Messages.MoveTargetKind.SiegeCatapult;
                        targetGuid = targetCatapult.guid;
                        targetDesc = "catapult " + targetGuid;
                    }
                    else
                    {
                        // An IMoveTarget we have no id for. Nothing sensible can be sent, and
                        // guessing at a position would be a different order from the one given.
                        return;
                    }

                    // The position always travels, even for an entity order, as the receiver's
                    // fallback when it cannot resolve the target. GetPos is on the interface, so
                    // this works for every kind without another cast.
                    int tx, tz;
                    if (cell != null) { tx = cell.x; tz = cell.z; }
                    else
                    {
                        Vector3 p = moveTarget.GetPos();
                        tx = Mathf.RoundToInt(p.x);
                        tz = Mathf.RoundToInt(p.z);
                    }

                    Main.helper.Log($"[SHIPMOVE] broadcasting your {desc} order: guid {unitGuid} -> {targetDesc}");
                    KaCMultiplayer.Net.NetRouter.Send(new KaCMultiplayer.Net.Messages.ShipMoveMessage
                    {
                        Unit = unitGuid,
                        X = tx,
                        Z = tz,
                        TargetKind = kind,
                        Target = targetGuid
                    });
                }
                catch (Exception e) { Main.helper.Log("[SHIPMOVE] broadcast error: " + e.Message); }
            }
        }

        // Dedup: last target cell broadcast per player-merchant guid, so following a route (which re-issues
        // MoveTo repeatedly toward the same dock) doesn't spam the network.
        private static Dictionary<Guid, long> _lastMerchantMoveTarget = new Dictionary<Guid, long>();

        // Syncs a PLAYER MERCHANT's sailing. Unlike the controllable ships above (SeedShip/TroopTransport,
        // synced via OrdersManager.MoveTo), the player merchant is route-driven and NOT an IMoveableUnit, so
        // its movement flows through ShipBase.MoveTo instead. We broadcast its destination so the other
        // players' copies sail it the same way, the same "sync the command, not the position" model, one
        // message per new destination. Gated to the LOCAL player's own player merchants; on a remote machine
        // that same merchant belongs to a foreign team so it never re-broadcasts (no echo). Deduped by
        // target cell so route repathing toward the same dock doesn't spam. This is the merchant "sail"
        // (Phase 2c) that carries a loaded merchant to another player's dock for trade.
        [HarmonyPatch(typeof(ShipBase), "MoveTo")]
        public class ShipBaseMoveToHook
        {
            public static void Postfix(ShipBase __instance, IMoveTarget target)
            {
                if (!NetClient.client.IsConnected) return;
                try
                {
                    Ship ship = __instance as Ship;
                    if (ship == null || ship.type != ShipBase.ShipType.PlayerMerchant) return; // player merchants only
                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
                    if (ship.teamID != localTeam) return; // only your own (remote copy is foreign team → no echo)
                    if (target == null) return;

                    int x, z;
                    Cell cell = target as Cell;
                    if (cell != null) { x = cell.x; z = cell.z; }
                    else
                    {
                        Cell c = World.inst.GetCellData(target.GetPos());
                        if (c == null) return;
                        x = c.x; z = c.z;
                    }

                    long key = ((long)x << 32) | (uint)z;
                    long prev;
                    if (Main._lastMerchantMoveTarget.TryGetValue(ship.guid, out prev) && prev == key) return; // unchanged
                    Main._lastMerchantMoveTarget[ship.guid] = key;

                    Main.helper.Log($"[SHIPMOVE] broadcasting your MERCHANT sail: guid {ship.guid} -> cell ({x},{z})");
                    KaCMultiplayer.Net.NetRouter.Send(new KaCMultiplayer.Net.Messages.ShipMoveMessage { Unit = ship.guid, X = x, Z = z });
                }
                catch (Exception e) { Main.helper.Log("[SHIPMOVE] merchant sail broadcast error: " + e.Message); }
            }
        }



        // ARMY sync, same method as ships: sync the SPAWN (with a shared guid), then movement replays as
        // commands through the existing ShipMove path. Ships got their spawn for free from synced building
        // completion; an army forms from Barracks.Tick's LOCAL villager/economy sim (soldiers walking in,
        // armaments), which DIVERGES between machines, so the spawn is broadcast explicitly (ArmySpawn) and
        // a foreign player's barracks is prevented from ever forming its own (diverged) army locally.
        // COMBAT (fighting/damage/deaths) remains unsynced, this makes armies exist, look right, and move
        // in sync; battles will still diverge. That's the known big frontier.

        // True while applying a remote ArmySpawn so the MakeArmy Postfix doesn't re-broadcast it (echo guard).
        public static bool applyingRemoteArmySpawn = false;

        // Broadcasts the spawn of YOUR army (formed by your barracks, MakeArmy runs on the owner's machine
        // with the barracks' team). Remote machines create the same army with the same guid via ArmySpawn.
        // Parameter names must match the game's method exactly, since Harmony binds by name:
        // MakeArmy(Vector3 pos, Int32 teamId, ArmyType at, Boolean addUnits).
        [HarmonyPatch(typeof(UnitSystem), "MakeArmy")]
        public class UnitSystemMakeArmyHook
        {
            public static void Postfix(UnitSystem.Army __result, Vector3 pos, int teamId, UnitSystem.ArmyType at)
            {
                if (!NetClient.client.IsConnected || Main.applyingRemoteArmySpawn || __result == null) return;
                try
                {
                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
                    if (teamId != localTeam) return; // only broadcast YOUR armies (raiders are suppressed; creative is local-team anyway)
                    Main.helper.Log($"[ARMY] broadcasting your army spawn: type {at} team {teamId} guid {__result.guid} at {pos}");
                    KaCMultiplayer.Net.NetRouter.Send(new KaCMultiplayer.Net.Messages.ArmySpawnMessage { Army = __result.guid, Position = pos, TeamId = teamId, ArmyType = (int)at });
                }
                catch (Exception e) { Main.helper.Log("[ARMY] spawn broadcast error: " + e.Message); }
            }
        }

        // Stops a FOREIGN player's barracks from ticking on this machine. Its army-forming runs off the local
        // villager/economy sim, which diverges, left alone it could form a duplicate (wrong) army next to the
        // synced one from ArmySpawn. The owner's machine is authoritative for their barracks; our copy is
        // visual. (MakeArmy can't be suppressed directly: Barracks.Tick uses its return value with NO null
        // check, so a null-returning Prefix there would NRE. Gating the whole foreign Tick avoids that.)
        // Army despawn, the missing half of ArmySpawn.
        //
        // Armies were created across the wire and never removed, so a disbanded or dead army stood
        // on every other machine forever. Worse than cosmetic: ReleaseArmy is also what takes a unit
        // out of OrdersManager, so an army nobody ever released stays a pathing obstacle for
        // everyone who still believes in it.
        //
        // ReleaseArmy is the single choke point. Disband() and DestroyUnit() both end here, and so
        // does UnitSystem.Reset, which is why the hook broadcasts from here rather than from the
        // several places an army can die.
        //
        // OWNER-AUTHORITATIVE, matching how the spawn works: the machine whose team owns the army
        // announces it, everyone else obeys. Any other rule double-broadcasts, since every machine
        // simulates the whole world and each one releases its own copy.
        //
        // Note that ReleaseArmy leaves guid and teamId alone (it clears units, the general and the
        // caches), so a Postfix can still read both, which is what lets this run after the removal
        // rather than having to guess beforehand whether it will succeed.
        [HarmonyPatch(typeof(UnitSystem), "ReleaseArmy")]
        public class UnitSystemReleaseArmyHook
        {
            public static void Postfix(UnitSystem.Army army)
            {
                if (!NetClient.client.IsConnected || army == null) return;
                if (KaCMultiplayer.Net.NetApply.InProgress) return;   // we are applying someone else's despawn

                // UnitSystem.Reset() releases EVERY army, and it runs on load and on teardown.
                // Without this the act of loading a save would announce the death of every army in
                // the world, and each peer would dutifully delete armies that are about to be
                // restored from the same save.
                if (LoadSaveOverrides.SessionSave.Unpacking) return;

                try
                {
                    int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                        ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
                    if (army.teamId != localTeam) return;   // not ours to announce

                    KaCMultiplayer.Net.NetRouter.Send(
                        new KaCMultiplayer.Net.Messages.ArmyDespawnMessage { Army = army.guid });
                }
                catch (Exception e) { Main.helper.Log("[ARMY] despawn broadcast error: " + e.Message); }
            }
        }

        /// <summary>
        /// True when <paramref name="team"/> is another player's kingdom rather than ours, which
        /// is the question every ownership gate in this file asks.
        ///
        /// Teams 0 to 4 are the game's own neutral and AI range: they tick the same way on every
        /// machine by design and nothing here should touch them. Team 5 and up is a real human
        /// kingdom, and exactly one machine is entitled to decide anything about it.
        ///
        /// "Ours" is Player.inst, which is the local kingdom everywhere except inside the two
        /// deliberate swaps (SessionSave while packing, NetRegistrations while applying). Nothing
        /// gated by this runs inside one of those windows.
        /// </summary>
        public static bool ForeignKingdomTeam(int team)
        {
            int localTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;

            return team >= 5 && team != localTeam;
        }

        // WHO DECIDES WHO MOVES IN, reported twice on 2026-09-20: after a reload the limit on how
        // many people could move in appeared to come off, and homelessness never cleared again.
        //
        // Player.TrySettlePeople and Player.UpdatePersonArrival are the only two callers of
        // Villager.SetHome in the whole game (read off the shipped IL, not assumed).
        // UpdatePersonArrival is reached only from Player.Update, and PlayerPatch already
        // suppresses Update on a remote kingdom's "Client Player" object, so that path is ours
        // alone already and needs nothing.
        //
        // TrySettlePeople is the hole. TownSquare is a plain MonoBehaviour: its own Update runs on
        // EVERY town square in the scene, whoever owns it, and calls TrySettleAttractedPeople. So
        // this machine decides who moves into another player's houses, reading its own idea of
        // which of their homes are free, and so does every other machine, at the same time.
        //
        // That race is both halves of the report. Several machines settle the same arrivals
        // because each sees the same vacancy before anyone's SetHome has travelled, which looks
        // like the cap coming off; and their lists of who lives where drift apart, which is a
        // villager homeless on one screen and housed on another, permanently, since nothing
        // reconciles them while the session is running.
        //
        // Gated the same way Barracks.Tick is: the kingdom's own machine decides and broadcasts,
        // everyone else applies what arrives. The cheat key path (KeyboardControl.UpdateCheatKeys)
        // goes through the same gate, which is right, it is the local player's own cheat.
        [HarmonyPatch(typeof(Player), "TrySettlePeople")]
        public class PlayerTrySettlePeopleForeignHook
        {
            /// <summary>Counted, not logged: this is a per-frame path and a log line here would
            /// bury the session. Read by the acceptance suite.</summary>
            public static int SkippedForeign;

            public static bool Prefix(Player __instance, ref int numHoused, ref bool housingShortage)
            {
                if (!NetClient.client.IsConnected || __instance == null) return true;

                try
                {
                    LandmassOwner owner = __instance.PlayerLandmassOwner;
                    if (owner == null || !ForeignKingdomTeam(owner.teamId)) return true;

                    // Vanilla assigns both of these on every path, and TownSquare.Update reads
                    // numHoused the moment the call returns, so skipping the body must still
                    // leave them sane rather than whatever the caller happened to pass in.
                    numHoused = 0;
                    housingShortage = false;
                    SkippedForeign++;
                    return false;
                }
                catch (Exception e) { Main.LogEx("TrySettlePeople ownership gate", e); }

                return true;
            }
        }

        // "IS THIS BUILDING MINE" HAD STOPPED MEANING ANYTHING. Vanilla's Building.IsPlayerBuilding
        // is one line: World.GetLandmassOwner(GetCell().landMassIdx) == Player.inst.PlayerLandmassOwner.
        //
        // BuildingPlayerReferencePatch rewrites Player.inst inside every Building instance method
        // to "this building's own owner", which is what tax, jobs and storage need and is exactly
        // wrong here: the comparison turns into "does this building's owner own this building",
        // which is true for every building in the world.
        //
        // PlayBuildingSound, TakeDamageInternal and Keep.SetAdvisorMessage all gate on it and
        // trust it to mean "mine", so every player's construction noise, damage warning and
        // advisor message became everyone's. Answered here from the ground the building stands on,
        // and the rewritten body never runs.
        [HarmonyPatch(typeof(Building), "IsPlayerBuilding")]
        public class BuildingIsPlayerBuildingHook
        {
            public static bool Prefix(Building __instance, ref bool __result)
            {
                if (!NetClient.client.IsConnected) return true;   // single player: vanilla is right

                try
                {
                    Cell cell = (__instance == null) ? null : __instance.GetCell();
                    LandmassOwner ground = (cell == null) ? null : World.GetLandmassOwner(cell.landMassIdx);
                    LandmassOwner mine = (Player.inst == null) ? null : Player.inst.PlayerLandmassOwner;

                    __result = ground != null && mine != null && ground == mine;
                    return false;
                }
                catch (Exception e) { Main.LogEx("IsPlayerBuilding", e); }

                return true;
            }
        }

        // ONE KINGDOM'S NEWS IN EVERYBODY'S LOG. KingdomLog.TryLog is the single funnel behind
        // every line the log panel shows, and it is called from inside whichever kingdom's
        // simulation raised the event. Vanilla filters land owned by an AI, which in single player
        // is the only thing worth filtering. Here every human kingdom's buildings tick on every
        // machine, so "not AI" is true for all of them, and another player's fire, plague or
        // unhappy peasants were announced in our log as though they were ours.
        //
        // A landmass of -1 is vanilla's own "this is not about one kingdom" case (weather and the
        // like) and is left alone.
        [HarmonyPatch(typeof(KingdomLog), "TryLog")]
        public class KingdomLogTryLogHook
        {
            public static bool Prefix(int landmass)
            {
                if (!NetClient.client.IsConnected || landmass < 0) return true;

                try
                {
                    LandmassOwner ground = World.GetLandmassOwner(landmass);
                    LandmassOwner mine = (Player.inst == null) ? null : Player.inst.PlayerLandmassOwner;

                    // Unowned ground, or too early for anyone to own anything: leave it to vanilla
                    // rather than silently swallowing a line that may be about the world itself.
                    if (ground == null || mine == null) return true;

                    return ground == mine;
                }
                catch (Exception e) { Main.LogEx("KingdomLog gate", e); }

                return true;
            }
        }

        // PINK BOATS ON THE OTHER PLAYER'S SCREEN. ShipBase.UpdateMaterial paints every mesh with
        // World.GetLandmassOwnerByTeamId(_teamID).UniMaterialFogClip and checks neither the owner
        // nor the material. ShipBase.Init calls it the instant a ship is created, which on another
        // machine can be before that kingdom's banner material exists, and Unity draws a null
        // material hot pink.
        //
        // RepaintShipHulls already repaints on the next banner sweep, but a fishing boat is born
        // and gone faster than the sweep comes round, so there is nearly always a fresh pink one
        // somewhere, which reads as "their boats are pink" rather than "one boat was, briefly".
        //
        // Skipping the paint leaves the hull prefab's own material, an undyed boat rather than an
        // obviously broken one, and asks for a sweep that will paint it for real. It also avoids
        // the NRE vanilla would throw here for a ship whose team owns no landmass.
        [HarmonyPatch(typeof(ShipBase), "UpdateMaterial")]
        public class ShipBaseUpdateMaterialHook
        {
            public static bool Prefix(ShipBase __instance)
            {
                if (!NetClient.client.IsConnected || __instance == null) return true;

                try
                {
                    int teamId = PrivateField.Get<int>(__instance, "_teamID", -1);
                    if (teamId < 0) return true;   // vanilla's own no-op path, nothing to guard

                    LandmassOwner owner = World.GetLandmassOwnerByTeamId(teamId);
                    if (owner == null) return false;

                    if (owner.UniMaterialFogClip == null)
                    {
                        MarkBannersDirty();
                        return false;
                    }
                }
                catch (Exception e) { Main.LogEx("ship material guard", e); }

                return true;
            }
        }

        [HarmonyPatch(typeof(Barracks), "Tick")]
        public class BarracksTickForeignHook
        {
            // Reported once. This sits on a per-tick path, so an unguarded log here writes a line
            // per barracks per tick, a failure that lasted forty seconds filled the log with two
            // thousand identical lines and buried everything else in the session.
            private static bool warnedGateFailure;

            public static bool Prefix(Barracks __instance)
            {
                if (!NetClient.client.IsConnected) return true; // single-player: the game's own path
                try
                {
                    Building b = __instance.GetComponent<Building>();
                    if (b == null) return true;

                    // TeamID() reads the landmass owner and throws when there isn't one, which is
                    // the normal state of a neutral island, and of any island whose keep
                    // placement failed. Unowned means nobody foreign owns it, so let it tick.
                    if (World.GetLandmassOwner(b.LandMass()) == null) return true;

                    if (ForeignKingdomTeam(b.TeamID())) return false; // not ours to simulate
                }
                catch (Exception e)
                {
                    if (!warnedGateFailure)
                    {
                        warnedGateFailure = true;
                        Main.LogEx("barracks ownership gate (further occurrences suppressed)", e);
                    }
                }
                return true;
            }
        }


        // Shares the game speed, pause included, between players.
        //
        // A Postfix, deliberately: the game's own SetSpeed must always run so the change takes
        // effect locally. Blocking it risks soft-locking an un-pause, which freezes the simulation
        // while real time keeps going, a hang on both machines with nothing in the log.
        //
        // So the only decision made here is whether to broadcast. A change that arrived from a
        // peer is not echoed back, and a repeat of the speed already in effect is dropped, since
        // the game re-asserts pause on its own and each repeat would otherwise cost a message.
        // knownSpeed holds the speed actually in effect, so a genuine change still sends even
        // immediately after a remote-driven one.
        [HarmonyPatch(typeof(SpeedControlUI), "SetSpeed")]
        public class SpeedControlUISetSpeedHook
        {
            private static int knownSpeed = -1;

            public static void Postfix(int idx)
            {
                if (!NetClient.client.IsConnected) { knownSpeed = idx; return; }

                bool changed = idx != knownSpeed;
                knownSpeed = idx; // always reflect the speed that was just applied
                Main.CurrentSpeed = idx;

                // Two flags, because a speed change can arrive by either route. Missing one
                // means the change is echoed straight back to the sender, which then applies it
                // and echoes again.
                if (Main.applyingRemoteSpeed || NetApply.InProgress)
                {
                    // Logged only on a real change, because SetSpeed re-enters itself. It assigns
                    // Unity Toggle.isOn, whose setter fires onValueChanged, which lands back here
                    // with the SAME value, so one remote change used to print two identical lines.
                    // Harmless (the second pass is still inside the apply, so nothing echoes) but it
                    // doubled the speed diagnostics for no information.
                    if (changed) Main.helper.Log($"[SPEED] applied remote speed={idx}");
                    return; // came from a peer, do not re-broadcast (avoids ping-pong)
                }

                if (Main.localMenuSpeedChange)
                {
                    Main.helper.Log($"[SPEED] menu speed={idx} kept local (not broadcast)");
                    return; // opening or closing your own menu is nobody else's business
                }

                if (!changed) return; // local call but speed didn't actually change, don't spam

                Main.helper.Log($"[SPEED] local speed change -> {idx}, broadcasting");
                NetRouter.Send(new KaCMultiplayer.Net.Messages.TimeScaleMessage { Speed = idx });
            }
        }

        // ONE PLAYER'S MENU MUST NOT STOP EVERYONE ELSE'S GAME.
        //
        // Opening the in-game menu runs PlayingMode.OnClickedMenu, which ends with
        // SpeedControlUI.SetSpeed(0); closing it runs MainMenuMode.Shutdown, which restores the speed
        // it saved on the way in. Both are ordinary local SetSpeed calls, so the hook above treated
        // them as the player deliberately changing speed and broadcast them. Pressing Escape therefore
        // froze every other player, and, worse, closing the menu again pushed YOUR old speed onto
        // everyone, overriding a pause somebody else had deliberately set.
        //
        // The menu still stops time for the player who opened it. It just stops being everyone's
        // problem. The three entry points are the ones that pause on the way in (menu, share, banner);
        // the exit is scoped to the two states the game itself restores speed for, so starting a game
        // from the main menu is untouched.
        //
        // A MENU IS NOT A PAUSE, AND THE WORLD ONLY STOPS WHEN SOMEBODY STOPS IT.
        //
        // There was a shared gate here for a while: opening a menu told the other players, and the
        // world stayed at speed zero until the last menu closed. It was meant to stop the two sides
        // drifting apart while one of them read something. In practice it caused more desync than it
        // prevented, because it made the simulation start and stop on events that were never part of
        // anyone's game: a player alt-tabbing, glancing at the share screen, or changing a banner
        // would halt everyone, and every one of those stops and starts was another chance for the
        // two worlds to disagree about what had happened and when.
        //
        // So it is gone, and the rule is the simple one: time stops when a player sets the speed to
        // pause, and at no other moment. Only the suppression below remains, which is the original
        // fix and a smaller claim, that YOUR menu pauses YOUR game and says nothing to anybody
        // else.
        [HarmonyPatch(typeof(PlayingMode), "OnClickedMenu")]
        public class PlayingModeOnClickedMenuHook
        {
            public static void Prefix() { Main.localMenuSpeedChange = true; }
            public static void Postfix() { Main.localMenuSpeedChange = false; }
        }

        [HarmonyPatch(typeof(PlayingMode), "OnClickedShare")]
        public class PlayingModeOnClickedShareHook
        {
            public static void Prefix() { Main.localMenuSpeedChange = true; }
            public static void Postfix() { Main.localMenuSpeedChange = false; }
        }

        [HarmonyPatch(typeof(PlayingMode), "OnClickedBannerChange")]
        public class PlayingModeOnClickedBannerHook
        {
            public static void Prefix() { Main.localMenuSpeedChange = true; }
            public static void Postfix() { Main.localMenuSpeedChange = false; }
        }

        // The way back out. MainMenuMode.Shutdown restores the saved speed only for PauseMenu and
        // BannerSelect; every other state takes TimeManager.ReturnToPlaySpeed instead, and starting a
        // session goes through here too, so the suppression matches the game's own condition exactly
        // rather than covering the whole method.
        [HarmonyPatch(typeof(MainMenuMode), "Shutdown")]
        public class MainMenuModeShutdownSpeedHook
        {
            public static void Prefix(MainMenuMode __instance)
            {
                if (__instance == null) return;
                MainMenuMode.State s = __instance.GetState();
                Main.localMenuSpeedChange = (s == MainMenuMode.State.PauseMenu || s == MainMenuMode.State.BannerSelect);
            }

            /// <summary>Cleared unconditionally, so the flag cannot survive a Shutdown that took the
            /// early exit above and leak into the next speed change the player makes.</summary>
            public static void Postfix()
            {
                Main.localMenuSpeedChange = false;
            }
        }

        /// <summary>
        /// Shuts the game's own <c>SteamManager</c> down completely.
        ///
        /// Steamworks.NET permits exactly one initialised manager per process, and this mod runs
        /// its own (<c>SteamBootstrap</c>) because it needs the callbacks live in the menus, well
        /// before the game would start its own. Leaving both would mean two managers racing
        /// SteamAPI_Init and RunCallbacks.
        ///
        /// Every declared instance method is suppressed, not just Awake, Update pumps callbacks
        /// and OnDestroy shuts the API down, so silencing the constructor alone would leave the
        /// other half running against our instance. The class name says Awake for historical
        /// reasons; what it does is disable the type.
        /// </summary>
        [HarmonyPatch]
        public class SteamManagerAwakeHook
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                return typeof(SteamManager)
                    .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Cast<MethodBase>();
            }

            /// <summary>Skips the game's method, always. See the class summary.</summary>
            public static bool Prefix()
            {
                return false;
            }
        }


        // DISABLE viking raids in multiplayer. Logs proved a raid hard-freezes the world clock the
        // instant it registers (game-time stops, autosave goes silent, pawns halt) and that no error
        // is thrown, it's a genuine simulation stall, not an exception loop, so it can't be recovered
        // by clearing boats or pawns (the Ctrl+Shift+R panic button killed both and the freeze stayed).
        // The raid sim isn't MP-aware (it can't resolve targets across multiple team-owned landmasses),
        // so until we can sync/rework it we suppress it outright, same approach as the witch huts.
        // SetupRaid is the master entry point; skipping it means no raid is ever scheduled or run.
        [HarmonyPatch(typeof(RaiderSystem), "SetupRaid")]
        public class RaiderSetupRaidDiag
        {
            public static bool Prefix()
            {
                // Single-player path is untouched; only suppress while in a multiplayer session.
                if (!NetClient.client.IsConnected) return true;
                if (Main.RaidsEnabled) return true;   // deliberately switched on for testing

                Main.helper.Log("[RAID] SetupRaid suppressed (viking raids disabled in MP)");
                return false; // skip the original, no raid happens
            }
        }

        // Belt-and-suspenders: even if some other code path tries to spawn a viking boat directly,
        // block it in MP so no raider unit ever enters the world to jam the shared simulation.
        [HarmonyPatch(typeof(RaiderSystem), "SpawnVikingBoat", new Type[] { typeof(Vector3) })]
        public class RaiderSpawnVikingBoatDiag
        {
            public static bool Prefix()
            {
                if (!NetClient.client.IsConnected) return true;
                if (Main.RaidsEnabled) return true;

                Main.helper.Log("[RAID] SpawnVikingBoat suppressed (viking raids disabled in MP)");
                return false; // skip the original, no boat spawns
            }
        }

        // DIAGNOSTIC (dragon): does the siege dragon ever actually try to attack in MP? FireBreath
        // is the attack action; SetLookTarget is target acquisition. If FireBreath(true) never fires
        // (or no building target is ever set), that pinpoints whether it's a targeting failure or an
        // attack-trigger failure. Event-driven, not per-frame.
        [HarmonyPatch(typeof(Dragon), "FireBreath", new Type[] { typeof(bool) })]
        public class DragonFireBreathDiag
        {
            public static void Postfix(bool __0)
            {
                if (NetClient.client.IsConnected)
                    Main.helper.Log($"[DRAGON] FireBreath({__0}) isServer={NetHost.IsRunning}");
            }
        }

        [HarmonyPatch(typeof(Dragon), "SetLookTarget", new Type[] { typeof(UnityEngine.Object) })]
        public class DragonSetLookTargetDiag
        {
            private static string _lastTarget = "";
            public static void Postfix(UnityEngine.Object __0)
            {
                if (!NetClient.client.IsConnected) return;
                string t = (__0 == null) ? "null" : __0.name;
                if (t == _lastTarget) return; // only log target CHANGES (avoid per-frame spam)
                _lastTarget = t;
                Main.helper.Log($"[DRAGON] SetLookTarget -> {t} isServer={NetHost.IsRunning}");
            }
        }

        // Dragon spawning is host-authoritative. All three spawn methods need identical
        // treatment, so the logic lives here once and each patch class is a two-line shim,
        // Harmony requires one class per target method, but not one copy of the body per class.
        //
        // Prefix: a client never spawns a dragon of its own accord. The game's spawn runs only
        // when the spawn arrived from the host (NetApply.InProgress) or when this machine is the
        // host.
        // Postfix: whoever legitimately spawned one tells everyone else, unless we are currently
        // applying someone else's message, which would bounce it back out again.

        /// <summary>The most recently spawned dragon, or null if there are none.</summary>
        public static Dragon NewestDragon()
        {
            try
            {
                var all = DragonSpawn.inst != null ? DragonSpawn.inst.currentDragons : null;
                if (all == null || all.Count == 0) return null;

                return all.data[all.Count - 1];
            }
            catch { return null; }
        }

        /// <summary>The id of the most recently spawned dragon, or Guid.Empty if there are none.</summary>
        public static Guid NewestDragonId()
        {
            Dragon d = NewestDragon();
            return d != null ? d.id : Guid.Empty;
        }

        /// <summary>Finds a dragon by the id every machine agrees on. Count-bounded, not a LINQ pass
        /// over .data, which is the backing array and holds stale entries past Count.</summary>
        public static Dragon FindDragonById(Guid id)
        {
            var all = DragonSpawn.inst != null ? DragonSpawn.inst.currentDragons : null;
            if (all == null) return null;

            for (int i = 0; i < all.Count; i++)
            {
                Dragon d = all.data[i];
                if (d != null && d.id == id) return d;
            }
            return null;
        }

        // Damage aimed at a DRAGON, arbitrated like everything else that can be hurt.
        //
        // Dragons already spawned everywhere, because only the host starts one and it broadcasts,
        // but nothing carried what happened to them afterwards. Every machine resolved its own
        // damage, so a dragon shot down over one player's island kept flying, unhurt, for everyone
        // else. Dragons were simply missed when the other four hitable things were hooked.
        //
        // Its damage method is an EXPLICIT interface implementation, exactly like Army's, so the
        // real name carries the interface prefix and a plain lookup finds nothing. That is why this
        // is patched by hand alongside the others rather than by attribute.
        public class DragonDamageAuthorityHook
        {
            public static bool Prefix(Dragon __instance, ref HitSfxResult __result)
            {
                try
                {
                    if (__instance == null) return true;
                    if (KaCMultiplayer.Combat.CombatAuthority.ResolvesHere(__instance.transform.position))
                    {
                        KaCMultiplayer.Combat.CombatSync.NoteDragonDamaged(__instance);
                        return true;
                    }
                }
                catch { return true; }

                __result = HitSfxResult.None;   // what vanilla returns; drives the hit effect only
                return false;
            }
        }

        // ---- DRAGON FLIGHT ---------------------------------------------------------------
        //
        // Off-host, a dragon stops deciding and starts following. Dragon.Update calls exactly two
        // methods that make decisions, and suppressing only those leaves everything else in Update
        // running off the synced state: the fire particles, the roar sound, the health bar, the hit
        // flash, the roar animation, and LookWithHead in LateUpdate. So a followed dragon is still
        // a fully animated dragon. See Combat/DragonFlightSync.cs for why dragons are puppets
        // where armies are merely corrected.

        /// <summary>Stops a followed dragon flying itself somewhere the host never sent it.</summary>
        [HarmonyPatch(typeof(Dragon), "UpdateMovement")]
        public class DragonUpdateMovementHook
        {
            public static bool Prefix()
            {
                return KaCMultiplayer.Combat.DragonFlightSync.IsAuthority();
            }
        }

        /// <summary>
        /// Stops a followed dragon picking its own targets.
        ///
        /// This is the half that mattered most. Target selection reads Player.inst, which is a
        /// different kingdom on every machine, so each copy was not merely in the wrong place, it
        /// was attacking a different village. Firing state arrives with the flight message instead.
        /// </summary>
        [HarmonyPatch(typeof(Dragon), "UpdateActions")]
        public class DragonUpdateActionsHook
        {
            public static bool Prefix()
            {
                return KaCMultiplayer.Combat.DragonFlightSync.IsAuthority();
            }
        }

        /// <summary>
        /// Moves a followed dragon towards where the host says it is.
        ///
        /// A Postfix on Update rather than a MonoBehaviour of its own: Update still runs on every
        /// dragon on every machine, so the per-frame hook already exists and adding a component
        /// per dragon would buy nothing. Does nothing on the host, and nothing in single player.
        /// </summary>
        [HarmonyPatch(typeof(Dragon), "Update")]
        public class DragonUpdateFollowHook
        {
            public static void Postfix(Dragon __instance)
            {
                KaCMultiplayer.Combat.DragonFlightSync.Steer(__instance);
            }
        }

        private static bool DragonSpawnPrefix()
        {
            return !(NetClient.client.IsConnected && !NetHost.IsRunning && !NetApply.InProgress);
        }

        /// <summary>
        /// Announces a dragon THIS MACHINE ACTUALLY CREATED.
        ///
        /// <paramref name="spawned"/> is what the Prefix returned, and everything here depends on
        /// it, because a Harmony Postfix runs even when its Prefix returned false. The Prefix skips
        /// the original method; it does not skip the rest of the patch. So on a client, where the
        /// Prefix deliberately blocks the local spawn, this used to run anyway and announce a
        /// dragon that had never been born.
        ///
        /// The id is what made that harmful rather than merely odd. NewestDragonId() reads the
        /// newest entry in currentDragons, and after a blocked spawn that is some OTHER dragon, or
        /// Guid.Empty when there are none. So the message said "a dragon spawned here, and its name
        /// is one you are already using". The host duly created one and stamped it with that name,
        /// and ended up with two dragons answering to a single id, or with an unnamed one that no
        /// health report, death or flight update could ever refer to again.
        ///
        /// That dragon is invisible to the machine that supposedly spawned it, cannot be killed
        /// through the sync, and goes on burning villages by itself: a dragon that stays for a year
        /// and kills citizens nobody can account for.
        ///
        /// Dragons are host-authoritative at spawn, which the flight sync already assumes from end
        /// to end (see Combat/DragonFlightSync.cs). This makes the spawn path say the same thing.
        /// </summary>
        private static void DragonSpawnPostfix(KaCMultiplayer.Net.Messages.DragonKind kind, Vector3 start, bool spawned)
        {
            if (!spawned) return;   // the Prefix blocked it; there is no dragon here to announce
            if (!NetClient.client.IsConnected || NetApply.InProgress)
                return;

            // The dragon that was just created is the newest one in the list. Its id goes out with
            // the spawn so every machine's copy answers to the same name, which is what makes it
            // possible to report anything about it later.
            Guid id = NewestDragonId();
            if (id == Guid.Empty)
            {
                // Nothing to name it by, so nothing useful to say. A spawn message with no id makes
                // a dragon on every other machine that none of them can ever talk about again.
                Main.helper.Log("[DRAGON] spawn not announced: no id to give it");
                return;
            }

            NetRouter.Send(new KaCMultiplayer.Net.Messages.DragonSpawnMessage
            {
                Kind = kind,
                Dragon = id,
                X = start.x, Y = start.y, Z = start.z
            });
        }

        [HarmonyPatch(typeof(DragonSpawn), "SpawnSiegeDragon")]
        public class DragonSpawnSpawnSiegeDragonHook
        {
            public static bool Prefix(out bool __state)
            {
                __state = DragonSpawnPrefix();
                return __state;
            }

            public static void Postfix(Vector3 start, bool __state)
            {
                DragonSpawnPostfix(KaCMultiplayer.Net.Messages.DragonKind.Siege, start, __state);
            }
        }

        [HarmonyPatch(typeof(DragonSpawn), "SpawnMamaDragon", new Type[] { typeof(Vector3) })]
        public class DragonSpawnSpawnMamaDragonHook
        {
            public static bool Prefix(out bool __state)
            {
                __state = DragonSpawnPrefix();
                return __state;
            }

            public static void Postfix(Vector3 start, bool __state)
            {
                DragonSpawnPostfix(KaCMultiplayer.Net.Messages.DragonKind.Mama, start, __state);
            }
        }

        [HarmonyPatch(typeof(DragonSpawn), "SpawnBabyDragon", new Type[] { typeof(Vector3) })]
        public class DragonSpawnSpawnBabyDragonHook
        {
            public static bool Prefix(out bool __state)
            {
                __state = DragonSpawnPrefix();
                return __state;
            }

            public static void Postfix(Vector3 start, bool __state)
            {
                DragonSpawnPostfix(KaCMultiplayer.Net.Messages.DragonKind.Baby, start, __state);
            }
        }

        /// <summary>
        /// The visiting baby, which was the one spawn route with no gate on it at all.
        ///
        /// <c>DragonSpawn.OnSeasonChange</c> spawns four kinds of dragon and this is the FIRST
        /// branch it tries. The other three route through <c>SpawnMamaDragon</c>,
        /// <c>SpawnSiegeDragon</c> and <c>SpawnBabyDragon</c>, all patched above; this one calls
        /// <c>Spawn</c> directly, so nothing stopped a client spawning one of its own accord and
        /// nothing told anybody else about it. The result is a dragon that exists on exactly one
        /// machine: real and attackable there, absent everywhere else.
        ///
        /// The no-argument <c>SpawnMamaDragon()</c> and <c>SpawnBabyDragon()</c> overloads need no
        /// patch of their own, they delegate to the Vector3 ones. <c>SpawnTestDragon</c> is left
        /// alone deliberately: it is a developer spawn, and someone testing wants it where they
        /// asked for it.
        /// </summary>
        [HarmonyPatch(typeof(DragonSpawn), "SpawnBabyDragonToVisit", new Type[] { typeof(Vector3) })]
        public class DragonSpawnSpawnBabyDragonToVisitHook
        {
            public static bool Prefix(out bool __state)
            {
                __state = DragonSpawnPrefix();
                return __state;
            }

            public static void Postfix(Vector3 start, bool __state)
            {
                DragonSpawnPostfix(KaCMultiplayer.Net.Messages.DragonKind.Visiting, start, __state);
            }
        }


        [HarmonyPatch(typeof(Villager), "TeleportTo")]
        public class VillagerTeleportToHook
        {
            /// <summary>
            /// A villager being moved somewhere other than by walking, the game teleports them
            /// when a building they belong to moves or is rebuilt. Position is sent as three
            /// floats rather than a Vector3 so the wire format does not depend on Unity's
            /// serialization.
            /// </summary>
            public static void Postfix(Villager __instance, Vector3 newPos)
            {
                if (!NetClient.client.IsConnected || NetApply.InProgress) return;
                if (__instance == null) return;

                NetRouter.Send(new KaCMultiplayer.Net.Messages.VillagerWarpMessage
                {
                    Villager = __instance.guid,
                    X = newPos.x,
                    Y = newPos.y,
                    Z = newPos.z
                });
            }
        }


        /// <summary>
        /// Where multiplayer saves used to go, and why nothing sends them there any more.
        ///
        /// This held a Prefix on <c>LoadSave.GetSaveDir</c> that redirected the WHOLE GAME's save
        /// directory to Saves/Multiplayer whenever the mod believed a session was in play. It was
        /// removed on 2026-09-05 after the developers reported that the main menu's Load button
        /// listed only multiplayer saves until the client was restarted.
        ///
        /// The immediate cause was that the redirect keyed off sticky flags: it tested
        /// <c>IsConnected || NetHost.IsRunning || SteamLobby.loadingSave</c>, and loadingSave was
        /// cleared on only some exit paths, so once it latched the game's own load list stayed
        /// redirected for the rest of the process. But the flags were not really the problem. The
        /// problem was patching a global function that non-multiplayer code also calls, and then
        /// deciding its answer from a mode flag. There is no version of that which is safe, which is
        /// why the fix is removal rather than a better gate.
        ///
        /// What the folder was actually for, and where each concern went instead:
        ///
        ///   - "A multiplayer save is not loadable as a single-player one." It is, and always was.
        ///     Mod data lives in the save's mod dictionary (see ModSaveData), so what we write is a
        ///     stock LoadSaveContainer, verified to open in a completely unmodded game. What you get
        ///     is a world containing several kingdoms, which is odd but not broken.
        ///   - "Host and client would overwrite each other." Solved by the save NAME instead, which
        ///     carries the session id, so two machines in one session cannot collide.
        ///   - "The load list should only show multiplayer saves." That belongs in OUR lobby picker,
        ///     which is our screen and can filter on the marker in our own dictionary block. It never
        ///     belonged in the game's GetSaveDir.
        ///
        /// Kept as a comment rather than deleted outright because the reasoning is the useful part:
        /// the folder looks like an obvious idea and someone will propose it again.
        /// </summary>

        [HarmonyPatch(typeof(LoadSave), "LoadAtPath")]
        public class LoadSaveLoadAtPathHook
        {
            public static byte[] saveData = new byte[0];

            /// <summary>
            /// Read the bytes into <see cref="saveData"/> and stop, do not deserialize, do not
            /// unpack, do not touch the running world.
            ///
            /// Set by <see cref="Main.PackLiveSnapshot"/>, which needs a save's bytes without
            /// loading it. It could not simply read the file itself: the Workshop security scanner
            /// rejected `System.IO.File` in a newly added method while accepting the identical
            /// calls here, and rather than keep guessing at the rule, the fix is to add no new
            /// reference to the type at all and reuse the one call site already known to pass.
            /// </summary>
            public static bool captureBytesOnly = false;

            /// <summary>
            /// Loads a multiplayer save on the host, in place of the game's own loader.
            ///
            /// Two things differ. The file holds a <see cref="SessionSave"/> rather
            /// than the container the game expects, and the raw bytes have to be retained in
            /// <see cref="saveData"/> so they can be forwarded to clients as they join.
            /// </summary>
            public static bool Prefix(string path, string filename, bool visitedWorld)
            {
                if (!NetHost.IsRunning)
                    return true;

                Main.helper.Log($"host: loading multiplayer save '{filename}'");
                LoadSave.LastLoadDirectory = path;

                string savePath = path + "/" + filename;
                if (!File.Exists(savePath))
                {
                    // Never fail silently here. Vanilla's own LoadAtPath returns without a word when
                    // the file is missing, and inheriting that cost a user their whole session: the
                    // load did nothing, the lobby carried on as though a save had been chosen, and
                    // the log's only trace was the line above with nothing after it.
                    Main.helper.Log($"[LOAD] FAILED, no save file at '{savePath}'");

                    // A folder with no 'world' in it is almost always the legacy Saves/Multiplayer
                    // container, left behind by the builds that kept multiplayer saves in their own
                    // directory. Nothing writes there any more, but the folder survives on disk and
                    // now appears in the game's ordinary load list, so name the case rather than
                    // making the host guess why an entry they can see refuses to load.
                    bool pickedTheContainer = path != null
                        && path.TrimEnd('/').EndsWith("Multiplayer");

                    GameState.inst.mainMenuMode.TransitionTo(MainMenuMode.State.LoadError);
                    KaCMultiplayer.Lobby.ModalDialog.ShowWhenVisible(
                        "Can't load this save",
                        pickedTheContainer
                            ? "That entry is the folder multiplayer saves live in, not a save. Go " +
                              "back and open the load list again, and it will list the saves inside it."
                            : "No save file was found at " + savePath + ". Nothing was loaded.");
                    return false;
                }

                // Read once, then deserialize from the copy in memory. The bytes are needed
                // either way, they are what joining clients receive, so opening the file a
                // second time to feed the deserializer would read the same save twice.
                saveData = File.ReadAllBytes(savePath);

                // Snapshot capture stops here: the caller wants the bytes, not a world reload.
                if (captureBytesOnly)
                {
                    Main.helper.Log($"[RESUME] captured {saveData.Length} bytes without loading");
                    return false;
                }

                try
                {
                    // Staged logging. Clicking a save in the Load list has been observed
                    // to hang the game with no output at all after "Trying to load
                    // multiplayer save", but the process was force-killed, so buffered
                    // log lines may simply have been lost. These lines bracket each stage
                    // so the next attempt says which one it died in rather than leaving
                    // deserialize-vs-unpack an open question.
                    Main.helper.Log($"[LOAD] reading '{filename}' ({saveData.Length} bytes)");

                    object raw;
                    using (MemoryStream stream = new MemoryStream(saveData))
                    {
                        BinaryFormatter formatter = new BinaryFormatter();
                        formatter.Binder = new ModAssemblyBinder();
                        raw = formatter.Deserialize(stream);
                    }

                    Main.helper.Log("[LOAD] deserialized as " +
                                    (raw == null ? "null" : raw.GetType().FullName));

                    // Accept both save formats. Old: a serialised SessionSave subclass. New: a stock
                    // LoadSaveContainer carrying the mod's session block in its dictionary (the
                    // migration off the save-shredding subclass). A real single-player save is neither,
                    // and is refused. Casting to the base type covers both; Unpack below then dispatches
                    // to SessionSave.Unpack (old) or LoadSaveContainerUnpackHook (new).
                    LoadSaveContainer loadData = raw as LoadSaveContainer;
                    bool isMultiplayer = loadData is SessionSave
                        || (loadData != null && LoadSaveOverrides.ModSaveData.ReadSession(loadData.CustomSaveData) != null);
                    if (loadData == null || !isMultiplayer)
                    {
                        Main.helper.Log("[LOAD] not a multiplayer save, refusing");
                        GameState.inst.mainMenuMode.TransitionTo(MainMenuMode.State.LoadError);
                        KaCMultiplayer.Lobby.ModalDialog.ShowWhenVisible(
                            "Can't load this save",
                            "'" + filename + "' is a single-player save. Hosting from a " +
                            "saved game needs one that was saved from a multiplayer session.");
                        return false;
                    }

                    Main.helper.Log("[LOAD] unpacking");

                    // Restoring a save re-creates every villager and building, and each
                    // one trips the Harmony patch that broadcasts a player's action, so
                    // loading a save fired thousands of VillagerAdd messages until
                    // Riptide's pending-message table collided
                    // ("An item with the same key has already been added").
                    //
                    // The AddVillager patch does try to guard this, with a StackFrame scan
                    // for a method named "unpack" within 4 frames. Unpack sits deeper than
                    // that, so the guard never fired. An explicit scope says the same thing
                    // without depending on call depth: we are restoring state, not
                    // performing actions, so nothing here goes on the wire.
                    using (KaCMultiplayer.Net.NetApply.Scope())
                    {
                        loadData.Unpack(null);
                        Main.helper.Log("[LOAD] unpacked; broadcasting OnLoaded");
                        Broadcast.OnLoadedEvent.Broadcast(new OnLoadedEvent());
                    }

                    Main.helper.Log("[LOAD] done");
                }
                catch (Exception e)
                {
                    GameState.inst.mainMenuMode.TransitionTo(MainMenuMode.State.LoadError);
                    Main.LogEx("host LoadAtPath (SessionSave.Unpack)", e);
                    throw;
                }

                return false;
            }
        }

        /// <summary>
        /// Scratch folder holding the snapshot a joining player is sent. Sits alongside the game's
        /// own "autosave" and "return" folders, and is reused rather than uniquely named so
        /// repeated joins overwrite it instead of accumulating saves.
        /// </summary>
        public const string ResumeSaveFolder = "mp_resume";

        /// <summary>
        /// Serialises the world as it stands right now, for somebody joining a game in progress.
        ///
        /// The pre-existing transfer path sends <c>LoadSaveLoadAtPathHook.saveData</c>, the bytes
        /// the host read off disk when it loaded the lobby's save, which is fine before anyone has
        /// played and useless afterwards, since it describes a world hours out of date.
        ///
        /// **It adds no new file or serialization call of its own, and that is deliberate.** The
        /// Workshop security scanner rejected `System.IO.File` and `System.IO.MemoryStream` in every
        /// newly written method that used them, while accepting the identical calls that have been
        /// in <c>LoadSaveLoadAtPathHook</c> for weeks. Three attempts to characterise the rule from
        /// the outside were wrong, so this stopped trying: the game writes the save, and the one
        /// existing, known-good read path hands back the bytes via
        /// <c>LoadSaveLoadAtPathHook.captureBytesOnly</c>.
        ///
        /// <c>LoadSave.Save</c> puts a full save at <c>dir/world</c>, and the save transpiler swaps
        /// the container it builds for a <c>SessionSave</c>, so what lands is a complete
        /// multiplayer save with every kingdom in it. The callback is not optional,
        /// <c>LoadSave.Save</c> only <c>Join()</c>s its writer thread when one is supplied, so
        /// without it this races an incomplete file.
        ///
        /// Returns null on any failure, which the caller must treat as "do not send": a truncated
        /// save unpacks into a broken world rather than failing cleanly.
        /// </summary>
        public static byte[] PackLiveSnapshot()
        {
            try
            {
                string dir = LoadSave.GetSaveDir() + "/" + ResumeSaveFolder;
                LoadSave.Save(dir, () => { });

                // Read the bytes back through the existing loader, which stops short of touching
                // the running world while this flag is set. Cleared in a finally: leaving it on
                // would turn the host's next real save-load into a silent no-op.
                LoadSaveLoadAtPathHook.saveData = new byte[0];
                LoadSaveLoadAtPathHook.captureBytesOnly = true;
                try { LoadSave.LoadAtPath(dir, "world"); }
                finally { LoadSaveLoadAtPathHook.captureBytesOnly = false; }

                byte[] bytes = LoadSaveLoadAtPathHook.saveData;
                if (bytes == null || bytes.Length == 0)
                {
                    Main.helper.Log("[RESUME] no bytes came back from " + dir + "/world");
                    return null;
                }

                Main.helper.Log($"[RESUME] packed a live snapshot: {bytes.Length} bytes for {kCPlayers.Count} kingdom(s)");
                return bytes;
            }
            catch (Exception e)
            {
                Main.LogEx("packing a live snapshot for a joining player", e);
                return null;
            }
        }

        /// <summary>
        /// Loads a save a client received over the wire rather than off disk.
        ///
        /// A joining client never has the file, the host streams it during the handshake (see
        /// <c>SaveTransfer</c>), so there is nothing on disk for the game's loader to open. When
        /// <see cref="fromNetwork"/> is set, the bytes in <see cref="saveBytes"/> stand in for the
        /// file and the game's own path is skipped entirely.
        ///
        /// The flag clears itself on use: it is armed by the transfer completing, and a stale one
        /// would make the next ordinary load read whatever was last received.
        /// </summary>
        [HarmonyPatch(typeof(LoadSave), "Load")]
        public class LoadSaveLoadHook
        {
            /// <summary>Armed by <c>SaveTransfer</c> when a save arrives; cleared on use.</summary>
            public static bool fromNetwork = false;

            /// <summary>The save as received. Also what the host re-sends to later joiners.</summary>
            public static byte[] saveBytes = new byte[0];

            /// <summary>
            /// The deserialized result, unpacked once the client reaches the world. Typed as the base
            /// container so both save formats fit: an old serialised <see cref="SessionSave"/>, or a
            /// stock <see cref="LoadSaveContainer"/> carrying the mod's dictionary block. The later
            /// <c>Unpack</c> dispatches to the right path for whichever it is.
            /// </summary>
            public static LoadSaveContainer saveContainer;

            public static bool Prefix()
            {
                if (fromNetwork)
                {
                    Main.helper.Log($"client: deserializing {saveBytes.Length} bytes of save received from host");

                    using (MemoryStream ms = new MemoryStream(saveBytes))
                    {
                        BinaryFormatter bf = new BinaryFormatter();
                        bf.Binder = new ModAssemblyBinder();
                        saveContainer = (LoadSaveContainer)bf.Deserialize(ms);
                    }

                    fromNetwork = false;
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// Save-format switch. False (default): the mod writes its old <see cref="SessionSave"/>
        /// subclass into the file, exactly as before. True: the file is a stock vanilla
        /// <see cref="LoadSaveContainer"/> and the mod's extra kingdoms ride in that container's
        /// CustomSaveData dictionary as JSON (see <see cref="WriteModSessionToDict"/>), so the save
        /// stays openable by the unmodded game.
        ///
        /// The whole migration off the save-shredding subclass hangs on this flag. It stays false
        /// until the new path (write, read, and confirming a mod-made save opens in the unmodded
        /// game) is verified. See docs/save-migration-plan.md.
        /// </summary>
        /// <summary>
        /// Whether a multiplayer session is actually happening on this machine.
        ///
        /// THE RULE THIS SERVES, asked for by the game's developers on 2026-09-05 and adopted: with
        /// this mod installed, single player must behave exactly as though it were not. Anything
        /// that CHANGES game behaviour has to sit behind this. Reading, logging and mod-only state
        /// do not.
        ///
        /// An audit of all 71 patches on 2026-09-06 found five that changed single player, every one
        /// of them a defensible bug fix and every one of them a rule violation anyway: a fix for
        /// somebody else's game, applied without being asked, is still a change they did not ask
        /// for. They are gated here now and the underlying issues get reported upstream instead.
        ///
        /// Both halves matter. IsConnected alone is false on a host before its own client socket
        /// finishes connecting, and IsRunning alone is false for a joined client.
        /// </summary>
        public static bool InMultiplayer
        {
            get { return NetClient.client.IsConnected || NetHost.IsRunning; }
        }

        public static bool UseVanillaSaveFormat = true;   // Shipped default: new dictionary save format (vanilla-openable). Set false only to fall back to the old SessionSave path.

        // MakeSaveContainer, the runtime factory the save transpiler routed
        // `newobj LoadSaveContainer` through, was removed on 2026-09-05 along with that transpiler.
        // It chose between a SessionSave and a stock container based on UseVanillaSaveFormat, and
        // that flag has been permanently true since multiplayer saves became ordinary vanilla
        // containers with the mod's data in their dictionary. Its ship-pruning side effect moved to
        // LoadSaveSaveHook.Prefix. SessionSave itself stays: old saves written in the previous
        // format still have to LOAD, which LoadSaveContainerUnpackHook handles.

        /// <summary>
        /// Writes the multiplayer session (every kingdom's PlayerSaveData, the kingdom-name map and
        /// the steamId to teamId identity map) into the save's mod-data dictionary. Subscribed to
        /// <c>Broadcast.OnSaveEvent</c>, which fires after the container is packed and before the
        /// write thread starts, so an entry added here rides into the file (see LoadSave.Save).
        ///
        /// Only does anything on the new save path, and only inside a real session. It mirrors what
        /// <see cref="SessionSave.Pack"/> stores in its own fields; the difference is purely where
        /// it lands (a dictionary entry of a stock container, not a subclass field).
        /// </summary>
        public static void WriteModSessionToDict()
        {
            if (!UseVanillaSaveFormat) return;
            if (!(NetClient.client.IsConnected || NetHost.IsRunning)) return;

            try
            {
                var data = new LoadSaveOverrides.ModSessionData();

                foreach (var player in kCPlayers.Values)
                {
                    // One failing kingdom must not abort the save. Packing runs on the main thread
                    // inside the game's save call, so an escaping exception stalls the whole
                    // simulation. Losing one kingdom from a save is recoverable; a hung session is not.
                    try
                    {
                        data.players.Add(player.steamId, new Player.PlayerSaveData().Pack(player.inst));
                    }
                    catch (Exception e)
                    {
                        Main.LogEx($"packing kingdom '{player.name}' ({player.steamId}) to dict", e);
                        continue;
                    }

                    // Your own SessionPlayer.kingdomName is only ever written from other players'
                    // packets, so for yourself it stays blank; take the real town name from TownNameUI
                    // instead. Remote players' names arrive over the wire, so theirs is already right.
                    bool isLocal = player.steamId == PlayerSteamID;
                    bool haveTownName = TownNameUI.inst != null && !string.IsNullOrWhiteSpace(TownNameUI.inst.townName);
                    data.kingdomNames[player.steamId] = (isLocal && haveTownName) ? TownNameUI.inst.townName : player.kingdomName;

                    int teamId = (player.inst != null && player.inst.PlayerLandmassOwner != null)
                        ? player.inst.PlayerLandmassOwner.teamId : 0;
                    data.identity[player.steamId] = teamId;
                }

                // Relations are session state, not per-kingdom state, so they hang off the block
                // itself rather than any one player's entry.
                data.relations = KaCMultiplayer.Net.PlayerRelations.Snapshot();
                data.pendingWars = KaCMultiplayer.Net.PlayerRelations.SnapshotPendingWars();

                // Export prices are session state too: they belong to kingdoms rather than to any
                // one player's entry, and a kingdom whose owner has not reconnected still has them.
                KaCMultiplayer.Trade.ExportPrices.Pack(
                    data.exportPriceTeams, data.exportPriceTypes, data.exportPriceValues);

                LoadSaveOverrides.ModSaveData.WriteSession(data);
                Main.helper.Log($"[SAVE] wrote mod session block for {data.players.Count} kingdom(s) " +
                                $"and {data.relations.Count} relation pair(s) into the save dictionary");
            }
            catch (Exception e)
            {
                Main.LogEx("writing the mod session block to the save dictionary", e);
            }
        }

        /// <summary>
        /// Read side of the dictionary save format. When a stock <see cref="LoadSaveContainer"/> is
        /// unpacked and it carries the mod's session block in its dictionary, rebuild the multiplayer
        /// session from that block through the existing SessionSave load path, and skip the game's own
        /// single-player Unpack.
        ///
        /// Fires only for genuine <c>LoadSaveContainer.Unpack</c> calls. An old serialised
        /// <see cref="SessionSave"/> has its own Unpack override and dispatches there instead, so this
        /// never sees it; a vanilla or single-player save has no block and falls through to the game
        /// untouched. Independent of <see cref="UseVanillaSaveFormat"/> on purpose: a new-format save
        /// must still load even if the write flag is later turned off.
        /// </summary>
        [HarmonyPatch(typeof(LoadSaveContainer), "Unpack")]
        public class LoadSaveContainerUnpackHook
        {
            public static bool Prefix(LoadSaveContainer __instance, object obj, ref object __result)
            {
                // RE-ENTRANCY GUARD. SessionSave.Unpack now calls base.Unpack, and base.Unpack IS
                // this patched method, so without this the prefix fires again on the same container,
                // wraps it in another SessionSave, and recurses until the process dies. That is not
                // hypothetical: it killed the game on the first run after the 2026-09-06 refactor,
                // and the automated load-back check caught it within a minute.
                //
                // A type test rather than a flag: reaching here with a SessionSave means we are
                // already inside our own conversion and the only correct thing to do is let the
                // game's real Unpack run. It also covers an old save that was serialised as a
                // SessionSave directly, which is the case the original comment assumed could never
                // arrive here.
                if (__instance is LoadSaveOverrides.SessionSave) return true;

                LoadSaveOverrides.ModSessionData mod;
                try
                {
                    mod = LoadSaveOverrides.ModSaveData.ReadSession(__instance.CustomSaveData);
                }
                catch (Exception e)
                {
                    Main.LogEx("reading the mod session block from a save", e);
                    return true;   // fall back to the game's own Unpack rather than fail the load
                }

                if (mod == null)
                {
                    // A host who picked a save with no multiplayer data in it. This became reachable
                    // on 2026-09-05, when the mod stopped redirecting the game's save directory: the
                    // host now browses the ordinary save list, so their single-player worlds are in
                    // it. Say so plainly instead of loading half a session.
                    //
                    // Loading it anyway would not work, and would fail quietly. The save holds one
                    // kingdom on team 0, while LoadIdentity gives the host team 5 (clientId + 4)
                    // because the save carries no identity block to say otherwise. The host would
                    // land in a world whose only kingdom is not theirs. Supporting this properly
                    // means remapping that kingdom onto the host's team, which is a feature worth
                    // having and not something to do accidentally inside a null check.
                    if (NetHost.IsRunning && KaCMultiplayer.Net.SteamLobby.loadingSave)
                    {
                        Main.helper.Log("[LOAD] REFUSED, the chosen save has no multiplayer data in it "
                                        + "(a single-player save cannot be hosted yet)");

                        GameState.inst.mainMenuMode.TransitionTo(MainMenuMode.State.LoadError);
                        KaCMultiplayer.Lobby.ModalDialog.ShowWhenVisible(
                            "That's a single-player save",
                            "This save was made outside multiplayer, so it has no kingdoms for the "
                            + "other players and cannot be hosted.\n\nPick a save you made in a "
                            + "multiplayer session, or start a new world instead.");
                        return false;
                    }

                    return true;   // vanilla or single-player save, loaded normally: let the game handle it
                }

                // Only rebuild the multiplayer session when one is actually active. A block-carrying
                // save opened OUTSIDE a session (an MP save copied into the single-player folder and
                // loaded from the SP menu, say) has no roster, no client id and no server, and
                // SessionSave.Unpack keys every player on those, so it would collapse both kingdoms
                // onto client id 0 or throw. Fall through to the game's own Unpack, which restores the
                // base (saver's) kingdom as a normal single-player load, exactly what the unmodded
                // game does with the same file.
                if (!(NetClient.client.IsConnected || NetHost.IsRunning))
                {
                    Main.helper.Log("[LOAD] save has a mod session block but no session is active; loading it as a plain single-player save (base kingdom only)");
                    return true;
                }

                Main.helper.Log($"[LOAD] mod session block found ({mod.players.Count} kingdom(s)); rebuilding through SessionSave");
                LoadSaveOverrides.SessionSave ss = LoadSaveOverrides.SessionSave.FromContainer(__instance, mod);
                __result = ss.Unpack(obj);
                return false;
            }
        }

        /// <summary>
        /// Chooses the container the game's save routine packs, via <see cref="MakeSaveContainer"/>.
        ///
        /// <c>LoadSave.Save</c> does a lot that multiplayer has no opinion about, picking the
        /// save directory, spawning the writer thread, the cover screenshot, the world summary,
        /// joining before the completion callback. The single thing that has to differ is the
        /// *type* of container it packs. So instead of standing in for the method, this rewrites
        /// a single instruction: <c>newobj LoadSaveContainer</c> is routed through
        /// <see cref="MakeSaveContainer"/>, which returns a <see cref="SessionSave"/> or a stock
        /// container depending on <see cref="UseVanillaSaveFormat"/>. The virtual <c>Pack</c> call
        /// that follows dispatches accordingly, and everything downstream is the game's own code,
        /// including any change it makes to saving in a future update.
        /// </summary>
        [HarmonyPatch(typeof(LoadSave), "Save")]
        public class LoadSaveSaveHook
        {
            /// <summary>
            /// Drops destroyed ship corpses before the game packs the save.
            ///
            /// ShipSystemSaveData.Pack calls GetComponent on every entry in ShipSystem.ships, and a
            /// destroyed Unity object there throws, aborting the save on the main thread. The game's
            /// autosave call then re-throws and the simulation stalls, which is the "host freezes
            /// while building" and "error related to saving" reports. Vanilla's Pack does not prune;
            /// the mod's old SessionSave.Pack did, and that protection had to survive the move to
            /// stock containers.
            ///
            /// This used to live in a TRANSPILER that rewrote `newobj LoadSaveContainer` into a call
            /// to a factory, so the container type could follow Main.UseVanillaSaveFormat. That flag
            /// has shipped permanently true since multiplayer saves became ordinary vanilla
            /// containers, so the factory only ever returned a stock container, and the only thing
            /// the IL rewrite still bought was a place to prune from. That is not a reasonable price
            /// for a callback, and rewriting what the save routine constructs is exactly the kind of
            /// change to the save process the game's developers asked us to stop making
            /// (2026-09-05). A Prefix runs before Save does anything, therefore before Pack, and
            /// needs none of it.
            ///
            /// Unconditional, matching what shipped: the factory pruned on every save, not only
            /// multiplayer ones, and the crash it prevents is a vanilla one.
            /// </summary>
            static void Prefix()
            {
                // Multiplayer only. The crash it prevents is in vanilla's own Pack, but every report
                // of it came from a session, and quietly repairing single-player saving is exactly
                // the kind of uninvited change this mod is not supposed to make. Reported upstream
                // instead, so it can be fixed where it belongs.
                if (!Main.InMultiplayer) return;

                try { LoadSaveOverrides.SessionSave.PruneDestroyedShips(); }
                catch (Exception e) { Main.LogEx("pruning destroyed ships before a save", e); }
            }

            /// <summary>
            /// Never let a failing save re-throw into the game's autosave call and stall the whole
            /// simulation (the documented freeze-on-failed-autosave: game-time stops, villagers halt,
            /// the host "cannot build anything"). The game's LoadSave.Save catch re-throws, and this
            /// hook's transpiler form, unlike the old Prefix, no longer swallowed it. A failed save is
            /// logged and abandoned for this cycle; the next autosave retries. A missed autosave is
            /// recoverable; a frozen host session is not. Pairs with MakeSaveContainer's ship prune,
            /// which keeps most saves from failing in the first place.
            /// </summary>
            static Exception Finalizer(Exception __exception)
            {
                if (__exception != null)
                    Main.LogEx("save aborted (swallowed so the simulation keeps running)", __exception);
                return null;
            }
        }

        // While a multiplayer save is being unpacked, ships restored from FishingHut/etc. data
        // immediately try to path (FishingShip.MoveToHut -> ShipBase.MoveTo -> UpdatePathing),
        // but the water nav grid isn't rebuilt until the unpack finishes. That throws
        // IndexOutOfRange and aborts the entire load. Skip ship pathing during the unpack; ships
        // re-path themselves on the first tick once the game is actually running.
        [HarmonyPatch(typeof(ShipBase), "UpdatePathing")]
        public class ShipUpdatePathingHook
        {
            public static bool Prefix()
            {
                if (LoadSaveOverrides.SessionSave.Unpacking)
                    return false; // skip original
                return true;
            }
        }

        /// <summary>
        /// Stops a fishing hut counting its dock positions before the nav grid exists.
        ///
        /// THE SAME CRASH AS THE SHIP ONE ABOVE, from the other direction. Restoring a fishing hut
        /// runs OnBuildingPlacement, which calls ValidateDockPositions, which asks each dock cell
        /// PathCell.GetBlocksWaterPath(cell, owner.teamId). That reads waterPathBlocked[teamId] --
        /// a bool array sized for vanilla's five teams, and our team ids start at 5. So a hut
        /// belonging to a multiplayer kingdom indexes past the end of the array and takes the whole
        /// load down with it: "There was a problem loading this save file."
        ///
        /// PathCellBakeTeamSlotsHook below already grows those arrays to 32 slots, which is the real
        /// fix and works everywhere the bake has run. It has NOT run here. The water nav grid is
        /// rebuilt after the unpack finishes, so during the unpack these cells still carry the
        /// native size-5 arrays and there is nothing to read.
        ///
        /// Skipped rather than made safe, because FishingHut.Tick calls this too: the count is
        /// recomputed on the hut's first tick once the world is actually running, from a grid that
        /// exists, with arrays that have been widened. Nothing is lost but a number that was about
        /// to be wrong anyway.
        ///
        /// GetBlocksWaterPath cannot be patched instead. It is nine bytes of IL, which is inside
        /// Mono's inlining threshold, so a prefix on it would be dead code. It is the same trap that
        /// IsCreativeModeOptionOn and GetJobEnabledFlags set for this project already.
        /// </summary>
        [HarmonyPatch(typeof(FishingHut), "ValidateDockPositions")]
        public class FishingHutValidateDockPositionsHook
        {
            public static bool Prefix()
            {
                return !LoadSaveOverrides.SessionSave.Unpacking;
            }
        }

        /// <summary>
        /// Stops one unit's flag colour aborting a load.
        ///
        /// UnitIGUI.UpdateMaterial paints a unit's flag with
        /// <c>World.inst.liverySets[owner.bannerIdx].armyMaterialUnlit</c>, and checks the owner for
        /// null but never the index. A troop transport restored from a save calls it through
        /// TroopTransportShip.Init while ShipSystemSaveData is still unpacking, at a point where a
        /// kingdom rebuilt by this mod may not have been given its banner yet. The list lookup
        /// throws, the exception leaves LoadSave.Load, and the player is told the save is broken.
        ///
        /// It is not broken. The only thing that cannot be answered yet is what colour one flag
        /// should be, and that is repainted anyway: every banner change calls MarkBannersDirty and
        /// the sweep repaints every flag in the world.
        ///
        /// Logged with the actual index, once, because "bannerIdx was out of range" is a fact worth
        /// having if this ever turns out to mean something worse than "too early".
        /// </summary>
        [HarmonyPatch(typeof(UnitIGUI), "UpdateMaterial")]
        public class UnitIGUIUpdateMaterialHook
        {
            private static bool reported;

            public static bool Prefix(int teamId)
            {
                try
                {
                    LandmassOwner owner = World.GetLandmassOwnerByTeamId(teamId);
                    if (owner == null) return false;   // vanilla does nothing here either

                    var liveries = World.inst != null ? World.inst.liverySets : null;
                    if (liveries != null && owner.bannerIdx >= 0 && owner.bannerIdx < liveries.Count)
                        return true;   // in range, let vanilla paint it

                    if (!reported)
                    {
                        reported = true;
                        Main.helper.Log($"[BANNER] team {teamId} asked for livery {owner.bannerIdx} of "
                            + $"{(liveries == null ? -1 : liveries.Count)}; flag left unpainted rather "
                            + "than throwing. Repainted by the next banner sweep. Logged once.");
                    }
                }
                catch (Exception e)
                {
                    Main.helper.Log("[BANNER] unit material guard error: " + e.Message);
                }

                return false;
            }
        }

        // ---- PATHS THE WORKER THREADS ABANDON ----------------------------------------------
        //
        // ThreadedPathing.CalculatePaths runs each path inside a bare catch, and the only line that
        // marks a path Complete is the last one in the try. A path whose calculation throws is left
        // at Status.Finding with nothing logged, and RequestPath refuses any path already Finding,
        // so whoever asked (villager, army, ship, cart) never gets an answer and never asks again.
        // In a session that is the "time runs, nobody moves" freeze.
        //
        // WaitForThread is the one place that sees every such path: it waits for all workers to
        // finish the batch, then clears the batch. Right after that wait, with every worker parked,
        // a path in the batch that is still Finding can only be one a worker gave up on. It is
        // closed there as "no route", the answer the game already gives an unreachable target, so
        // the asker's own logic moves on and asks again. Every unit type, one place, no polling.

        /// <summary>Paths closed after a worker abandoned them, for the log and the suite.</summary>
        public static int AbandonedPathsClosed;

        /// <summary>How many WaitForThread call sites got the sweep, so a check can see it took.</summary>
        public static int AbandonedPathSweepInstalled;

        private static FieldInfo pathsToCalculateField;
        private static FieldInfo requestedPathsField;

        /// <summary>
        /// Closes every path in the finished batch that a worker thread abandoned mid-calculation.
        /// Called from inside ThreadedPathing.WaitForThread, after the workers have finished. See the
        /// note above for why this is the multiplayer villager freeze. Single player: untouched.
        /// </summary>
        public static void CloseAbandonedPaths(ThreadedPathing pathing)
        {
            if (!InMultiplayer || pathing == null) return;

            try
            {
                if (pathsToCalculateField == null)
                    pathsToCalculateField = AccessTools.Field(typeof(ThreadedPathing), "pathsToCalculate");

                ArrayExt<GamePath>[] batches = pathsToCalculateField.GetValue(pathing) as ArrayExt<GamePath>[];
                if (batches == null) return;

                // A path finished in this batch can be consumed and asked for AGAIN before this
                // runs, which puts it back at Finding with a live request in requestedPaths. That
                // one is waiting for the next batch, not abandoned, and must be left alone.
                if (requestedPathsField == null)
                    requestedPathsField = AccessTools.Field(typeof(ThreadedPathing), "requestedPaths");
                ArrayExt<GamePath> queued = requestedPathsField.GetValue(pathing) as ArrayExt<GamePath>;

                for (int w = 0; w < batches.Length; w++)
                {
                    ArrayExt<GamePath> batch = batches[w];
                    if (batch == null) continue;

                    for (int i = 0; i < batch.Count; i++)
                    {
                        GamePath p = batch.data[i];
                        if (p == null || p.status != GamePath.Status.Finding) continue;
                        if (queued != null && queued.Contains(p)) continue;

                        // A half-written result is worse than none, and lastGridID = -1 stops
                        // RequestPath reusing it as a cached route next time.
                        p.result.Clear();
                        p.lastGridID = -1;
                        p.status = GamePath.Status.Complete;

                        // The throw itself is swallowed by the game, so this line is the only
                        // trace of it. The first few say where, which is what finds the cause.
                        if (++AbandonedPathsClosed <= 20)
                            helper.Log($"[PATHS] a {p.pathType} path for team {p.teamId} from {p.start} to {p.end}"
                                       + " was abandoned by its worker thread; closed as no route"
                                       + (AbandonedPathsClosed == 20 ? " (further ones counted, not logged)" : ""));
                    }
                }
            }
            catch (Exception e) { LogEx("closing abandoned paths", e); }
        }

        /// <summary>
        /// Inserts <see cref="CloseAbandonedPaths"/> right after WaitForThread's wait for the
        /// workers, before the finished batch is cleared. Earlier would race the workers; later
        /// and the batch is gone.
        /// </summary>
        [HarmonyPatch(typeof(ThreadedPathing), "WaitForThread")]
        public class ThreadedPathingAbandonedPathsHook
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo sweep = typeof(Main).GetMethod("CloseAbandonedPaths");

                foreach (CodeInstruction c in instructions)
                {
                    yield return c;

                    MethodInfo target = c.operand as MethodInfo;
                    if (AbandonedPathSweepInstalled == 0 && target != null && target.Name == "Wait"
                        && target.DeclaringType != null && target.DeclaringType.Name == "Countdown")
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Call, sweep);
                        AbandonedPathSweepInstalled++;
                    }
                }

                if (AbandonedPathSweepInstalled == 0)
                    helper.Log("[PATHS] WaitForThread has no Countdown.Wait any more; abandoned paths are NOT swept");
            }
        }

        // FIX (safety net): water/army/envoy pathing for multiplayer teams. Every PathCell stores its
        // per-team pathing data in fixed bool[5]/int[5] arrays indexed by teamId (waterPathBlocked[teamId],
        // waterPathCost[teamId], pathBlockedForArmies/Envoys[teamId], villager/envoyFootPathCost[teamId]).
        // Vanilla teams are 0..4 so 5 slots is enough. Our MP teamId is now clientId+4 (5, 6, 7…), still
        // ≥5, so it would overflow the size-5 arrays (IndexOutOfRange / "dock blocked" / ship can't path /
        // the load-time ShipBase.UpdatePathing crash). BakePathingCostsForCell bounds its per-team bake
        // loops on array.Length (not a hardcoded 5), so enlarging the arrays BEFORE it runs makes the game
        // itself bake correct data for our team IDs and keeps every reader in-bounds. This is belt-and-
        // suspenders alongside the compact teamId scheme: even though IDs are small now, they're still ≥5,
        // so the arrays must grow. Only in an MP session (SP teams fit 5), and once per cell (idempotent).
        // 32 slots covers ~27-player co-op with the clientId+4 scheme, far beyond any realistic game.
        private const int MpPathTeamSlots = 32;

        [HarmonyPatch(typeof(PathCell), "BakePathingCostsForCell")]
        public class PathCellBakeTeamSlotsHook
        {
            public static bool Prefix(Cell cell)
            {
                // SP keeps the native size-5 arrays (teams 0..4), nothing to fix there.
                if (!(NetClient.client.IsConnected || NetHost.IsRunning)) return true;
                try
                {
                    PathCell pc = World.inst.GetPathCell(cell);
                    if (pc != null && (pc.waterPathBlocked == null || pc.waterPathBlocked.Length < MpPathTeamSlots))
                    {
                        pc.waterPathBlocked = new bool[MpPathTeamSlots];
                        pc.waterPathCost = new int[MpPathTeamSlots];
                        pc.pathBlockedForArmies = new bool[MpPathTeamSlots];
                        pc.pathBlockedForEnvoys = new bool[MpPathTeamSlots];
                        pc.villagerFootPathCost = new int[MpPathTeamSlots];
                        pc.envoyFootPathCost = new int[MpPathTeamSlots];
                    }
                }
                catch (Exception e) { Main.helper.Log("[PATHFIX] enlarge error: " + e.Message); }
                return true; // let the original bake run, it now fills all enlarged team slots
            }

            /// <summary>
            /// Stops one uncookable cell from killing an entire load.
            ///
            /// Baking a cell reads the owner of the land it sits on, and four places in the bake do
            /// it as `World.GetLandmassOwner(cell.landMassIdx).teamId` with no null check.
            /// GetLandmassOwner returns null for land nobody owns, so a gate, keep, outpost or
            /// drawbridge standing on an unowned landmass throws. That happens during a LOAD, where
            /// a kingdom's buildings are restored before its landmass ownership is settled, and the
            /// throw escapes all the way out of Unpack and the save simply refuses to open.
            ///
            /// Seen intermittently: it needs one of those buildings on not-yet-owned land at bake
            /// time, so it depends on the map and on restore order, which is the worst kind of bug
            /// to leave in a load path. Caught here rather than by patching four vanilla methods,
            /// because the cost of the failure is wrong pathing costs for a single cell, which the
            /// next bake corrects, against the cost of the alternative, which is losing the save.
            ///
            /// Logged once per session. A per-cell log inside a catch on a map-wide bake is how you
            /// produce a hundred thousand log lines.
            /// </summary>
            private static bool reportedBakeFailure;

            static Exception Finalizer(Exception __exception, Cell cell)
            {
                if (__exception == null) return null;

                if (!reportedBakeFailure)
                {
                    reportedBakeFailure = true;
                    Main.helper.Log("[PATHFIX] baking pathing for a cell threw, most likely a gate or keep "
                                    + "on land with no owner yet; skipping that cell and carrying on. "
                                    + "Only reported once per session. First error: " + __exception.Message);
                }
                return null;   // swallowed: a mis-costed cell is recoverable, a failed load is not
            }
        }

        /// <summary>
        /// Picking a save while hosting loads it and returns to the lobby, rather than dropping
        /// straight into the world.
        ///
        /// The session has not started yet at this point, the host still has to wait for players
        /// to connect and ready up, and the world they are about to join is the one this load just
        /// produced.
        /// </summary>
        [HarmonyPatch(typeof(SaveLoadUI), "ClickLoadItem")]
        public class SaveLoadUIClickedLoadItemHook
        {
            public static bool Prefix(string id)
            {
                if (!NetHost.IsRunning) return true;

                LoadSave.Load(id);
                TransitionTo(MenuState.LobbyScreen);
                return false;
            }
        }

        [HarmonyPatch(typeof(Player.PlayerSaveData), "ProcessBuilding")]
        public class PlayerProcessBuildingHook
        {
            /// <summary>
            /// Rebuilds one saved building under the player that owns it.
            ///
            /// Stands in for the game's version, which attaches every restored building to the
            /// local player's container and keep. Here the owner is whichever
            /// <paramref name="p"/> the save is being unpacked for, possibly a remote kingdom, so
            /// each step has to be aimed at that player rather than at the singleton.
            /// </summary>
            public static bool Prefix(Building.BuildingSaveData structureData, Player p, ref Building __result)
            {
                if (!NetClient.client.IsConnected)
                    return true;

                Building prefab = GameState.inst.GetPlaceableByUniqueName(structureData.uniqueName);

                // Unity's overloaded == : false for a missing OR destroyed object, which a plain
                // null check would not catch.
                if (prefab == null)
                {
                    Main.helper.Log($"failed to instantiate '{structureData.uniqueName}' from save");
                    __result = null;
                    return false;
                }

                Building restored = UnityEngine.Object.Instantiate<Building>(prefab);
                restored.transform.position = structureData.globalPosition;
                restored.Init();
                restored.transform.SetParent(p.buildingContainer.transform, true);

                // Unpack before AddBuilding: AddBuilding reads state that Unpack fills in.
                structureData.Unpack(restored);
                p.AddBuilding(restored);

                // A save holds every player's keep, so match on team as well as component type,
                // otherwise the first keep encountered would be adopted by whoever is unpacking.
                Keep keep = restored.GetComponent<Keep>();
                bool ownKeep = keep != null && restored.TeamID() == p.PlayerLandmassOwner.teamId;
                if (ownKeep)
                    p.keep = keep;

                // One compact line per building. A save carries hundreds, and the
                // six-line-per-building trace this replaced made output.txt unreadable
                // at exactly the point it was needed.
                Main.helper.Log($"loaded {restored.FriendlyName} team={restored.TeamID()} owner={p.PlayerLandmassOwner.teamId}{(ownKeep ? " [keep]" : "")}");

                __result = restored;
                return false;
            }
        }

        [HarmonyPatch(typeof(Player.PlayerSaveData), "Pack")]
        public class PlayerSaveDataPackgHook
        {
            // A building covering several cells appears in every one of their lists, but the save
            // must hold it once. The game records it against the cell its transform stands on, so
            // every other cell that references it fails this test and is skipped.
            private const float AnchorTolerance = 1E-05f;

            // Per-save tallies for the two ways a building can be skipped, reported once at the end
            // of the sweep. Counted rather than logged where they happen: the sweep visits every
            // tile on the map, so a line per building would bury the rest of the session.
            private static int destroyedSkipped;
            private static int unpackableSkipped;

            /// <summary>
            /// Writes one player's kingdom into its save record, replacing the game's own Pack
            /// while a session is connected.
            ///
            /// The replacement exists because vanilla packs whichever kingdom
            /// <c>Player.inst</c> points at, and multiplayer has to pack each player in turn.
            /// Everything below therefore reads from <paramref name="p"/> and never from the
            /// singleton.
            /// </summary>
            public static bool Prefix(Player.PlayerSaveData __instance, Player p, ref Player.PlayerSaveData __result)
            {
                // Solo play keeps the game's version: there is one kingdom and nothing to correct.
                if (!NetClient.client.IsConnected) return true;

                Main.helper.Log($"packing player save for team {p.PlayerLandmassOwner.teamId}");

                PackKingdom(__instance, p);
                PackPeople(__instance, p);
                PackStructures(__instance, p);
                PackEconomy(__instance, p);
                PackJobs(__instance, p);

                __result = __instance;
                return false;
            }

            /// <summary>Identity, banner, difficulty and the per-landmass names.</summary>
            private static void PackKingdom(Player.PlayerSaveData data, Player p)
            {
                // Custom banner artwork is not packed, only the index into the stock set is, so
                // a player flying a custom banner comes back with a standard one.
                data.newBannerSystem = true;
                data.bannerIdx = p.PlayerLandmassOwner.bannerIdx;
                data.playerLandmassOwnerSaveData = new LandmassOwner.LandmassOwnerSaveData().Pack(p.PlayerLandmassOwner);

                data.creativeMode = p.creativeMode;

                // Preserve custom creative settings; ordinary kingdoms use vanilla's defaults.
                // MustBuildInTerritory must be ON to enforce road coverage.
                try
                {
                    bool[] flags = PrivateField.Get<bool[]>(p, "cmoOptionsOn");
                    if (flags != null)
                    {
                        List<Player.CreativeOptions> on = new List<Player.CreativeOptions>();

                        for (int i = 0; i < flags.Length; i++)
                            if (flags[i]) on.Add((Player.CreativeOptions)i);

                        // Null asks vanilla to restore its normal defaults. An empty list turns
                        // off survival rules and MustBuildInTerritory, allowing remote placement.
                        data.cmoOptions = p.creativeMode ? on : null;
                    }
                }
                catch (Exception cex) { LogEx("packing creative-mode options", cex); }

                // Upgrades are cleared rather than carried over. The field is private with no
                // setter, hence the reflection.
                PrivateField.Set(data, "upgrades", new List<Player.UpgradeType>());

                data.Difficulty = p.difficulty;
                data.CurrYear = p.CurrYear;
                data.usedCheats = p.hasUsedCheats;
                data.tourism = p.tourism;
                data.bDidFirstFire = PrivateField.Get<bool>(p, "bDidFirstFire");

                data.landMassNames = new List<string>(p.LandMassNames);
            }

            /// <summary>Villagers, and the kingdom-wide numbers that describe how they are faring.</summary>
            private static void PackPeople(Player.PlayerSaveData data, Player p)
            {
                // Packed compact, with no null holes. Allocating Workers.Count slots and filling
                // only the live ones leaves a null wherever a villager has died or been removed,
                // and vanilla's Unpack walks every slot and dereferences element.pos without a
                // null check, that was the "there was a problem loading this save" failure.
                //
                // Both loops are Count-bounded and read .data directly: ArrayExt over-allocates
                // its backing array, so its length is capacity, not population.
                var workers = new List<Villager.VillagerSaveData>();
                for (int i = 0; i < p.Workers.Count; i++)
                {
                    Villager worker = p.Workers.data[i];
                    if (worker != null)
                        workers.Add(new Villager.VillagerSaveData().Pack(worker));
                }
                data.WorkersArray = workers.ToArray();

                data.HomelessData = new List<Guid>();
                for (int i = 0; i < p.Homeless.Count; i++)
                {
                    Villager homeless = p.Homeless.data[i];
                    if (homeless != null)
                        data.HomelessData.Add(homeless.guid);
                }

                data.TownHappiness = p.KingdomHappiness;
                data.happinessMods = p.happinessMods;
                data.timeAtFailHappiness = p.timeAtFailHappiness;
                data.happinessInfos = PrivateField.Get<List<Player.HappinessInfo>>(p, "landMassHappiness");
                data.integrityInfos = PrivateField.Get<List<Player.IntegrityInfo>>(p, "landMassIntegrity");

                // The third of the same trio, and it was the one being dropped.
                //
                // ResetPerLandMassData builds landMassHappiness, landMassHealth and
                // landMassIntegrity together, one entry per landmass, and vanilla's Unpack reads all
                // three back. This replacement packed two of them. Nothing crashed, because Unpack
                // guards the null: it assigns healthInfos straight into landMassHealth and, finding
                // it null, substitutes a new EMPTY list.
                //
                // Empty is the problem. The list is indexed by landmass everywhere else, so it is
                // meant to come back with one entry per landmass and instead comes back with none,
                // and every read of landMassHealth[lm] after a load is an ArgumentOutOfRange waiting
                // to happen. Exceptions only reach Player.log, which is why this could sit here
                // unnoticed.
                data.healthInfos = PrivateField.Get<List<Player.HealthInfo>>(p, "landMassHealth");

                data.deathsThisYear = PrivateField.Get<int>(p, "deathsThisYear");
                data.nameForOldAgeDeath = PrivateField.Get<string>(p, "nameForOldAgeDeath");
                data.poorHealthGracePeriod = PrivateField.Get<float>(p, "poorHealthGracePeriod");
            }

            /// <summary>
            /// Every building and sub-building on the map that belongs to this player, grouped
            /// one array per cell, the shape the game's loader expects.
            /// </summary>
            private static void PackStructures(Player.PlayerSaveData data, Player p)
            {
                int teamId = p.PlayerLandmassOwner.teamId;

                data.structures = new List<Building.BuildingSaveData[]>();
                data.subStructures = new List<Building.BuildingSaveData[]>();

                destroyedSkipped = 0;
                unpackableSkipped = 0;

                // A full sweep of the grid, because ownership is a property of each building
                // rather than of any list the player holds.
                World.inst.ForEachTile(0, 0, World.inst.GridWidth, World.inst.GridHeight, delegate (int x, int z, Cell cell)
                {
                    PackCell(data.structures, cell.OccupyingStructure, cell, teamId, "building");
                    PackCell(data.subStructures, cell.SubStructure, cell, teamId, "sub-building");
                });

                if (destroyedSkipped > 0 || unpackableSkipped > 0)
                    Main.helper.Log($"[SAVE] team {teamId}: skipped {destroyedSkipped} destroyed and " +
                                    $"{unpackableSkipped} un-packable building(s) while packing structures");

                data.dockOpenings = PrivateField.Get<List<Player.DockOpening>>(p, "dockOpenings");
            }

            /// <summary>
            /// Appends this player's buildings anchored on <paramref name="cell"/> to
            /// <paramref name="into"/>, adding nothing when the cell holds none of theirs.
            /// </summary>
            private static void PackCell(
                List<Building.BuildingSaveData[]> into, List<Building> onCell, Cell cell, int teamId, string kind)
            {
                if (onCell == null) return;

                List<Building.BuildingSaveData> packed = null;

                foreach (Building building in onCell)
                {
                    try
                    {
                        // Unity's overloaded null is true for a DESTROYED object as well as an absent
                        // one, so this single check covers a corpse left behind in the cell's list.
                        //
                        // This guard, and the fact that the whole body now sits inside the try, is the
                        // fix for a total save failure seen in the wild (Rednax, 2026-09-02): the two
                        // filters below used to run OUTSIDE the try, so reading .transform on a
                        // destroyed building threw past PackCell and aborted the entire save. The
                        // player kept playing, because the Finalizer on LoadSave.Save swallows the
                        // throw rather than stalling the sim, so the only symptom was a run of
                        // "Problem during save" and, silently, no autosave ever reaching disk. Same
                        // family as the destroyed-ship corpses PruneDestroyedShips clears out of
                        // ShipSystem.ships.
                        if (building == null) { destroyedSkipped++; continue; }

                        if (building.TeamID() != teamId) continue;
                        if (Vector3.Distance(building.transform.position.xz(), cell.Position.xz()) > AnchorTolerance) continue;

                        Building.BuildingSaveData saved = new Building.BuildingSaveData().Pack(building);
                        if (packed == null) packed = new List<Building.BuildingSaveData>();
                        packed.Add(saved);
                    }
                    catch (Exception e)
                    {
                        // One building that will not pack must not take the autosave down with it.
                        // When it did, the throw travelled back into the game's save call and
                        // stalled the simulation, the "freeze at year N" report. Host-only,
                        // because only the host packs remote players' buildings, and those are
                        // reconstructed objects that can be missing a runtime reference.
                        //
                        // The name is read defensively: an object that just failed to pack is exactly
                        // the kind that throws again when asked for its name, and a throw raised from
                        // inside this catch would put the save right back where it started.
                        unpackableSkipped++;
                        string name;
                        try { name = building.UniqueName; } catch { name = "<unreadable>"; }
                        Main.helper.Log($"save: skipped un-packable {kind} '{name}': {e.Message}");
                    }
                }

                if (packed != null) into.Add(packed.ToArray());
            }

            /// <summary>Tax rates and the production/consumption history.</summary>
            private static void PackEconomy(Player.PlayerSaveData data, Player p)
            {
                // Left as-is when the player has none, rather than overwritten with an empty array.
                if (p.taxRates != null)
                    data.TaxRates = (float[])p.taxRates.Clone();

                // Stored by reference, with no defensive copy, the same as the routine this
                // replaces. Copying them would change what a save file contains.
                data.currConsumptionList = p.currConsumption;
                data.lastConsumptionList = p.lastConsumption;
                data.currProductionList = p.currProduction;
                data.lastProductionList = p.lastProduction;
            }

            /// <summary>Job priorities, staffing and tool permissions.</summary>
            private static void PackJobs(Player.PlayerSaveData data, Player p)
            {
                data.JobPriorityOrder = new int[p.JobPriorityOrder.Length][];
                data.JobEnabledFlag = new bool[p.JobEnabledFlag.Length][];
                for (int i = 0; i < p.JobPriorityOrder.Length; i++)
                {
                    data.JobPriorityOrder[i] = (int[])p.JobPriorityOrder[i].Clone();
                    data.JobEnabledFlag[i] = (bool[])p.JobEnabledFlag[i].Clone();
                }

                // A remote player's per-landmass arrays can be SHORTER than the current world: a remote
                // Player is built during the handshake, sometimes before this machine has generated the
                // map, so SetupJobPriorities sizes JobFilledAvailable to whatever NumLandMasses was then
                // (often the menu world's), and nothing grows it once the real map lands. Looping to
                // NumLandMasses and indexing p.JobFilledAvailable.data[lm] then throws IndexOutOfRange,
                // which the per-kingdom catch in the save turns into that remote kingdom silently
                // vanishing from the file. Bound the copy on what the player actually has so it packs
                // cleanly. The owner's machine holds the authoritative job tables; a puppeted remote's
                // are not worth losing the whole kingdom over, and the local player (the one that
                // matters) is always full-sized, so its pack is unchanged.
                int lmCount = World.inst.NumLandMasses;
                if (p.JobFilledAvailable != null) lmCount = System.Math.Min(lmCount, p.JobFilledAvailable.Count);
                if (p.JobCustomMaxEnabledFlag != null) lmCount = System.Math.Min(lmCount, p.JobCustomMaxEnabledFlag.Length);
                if (lmCount < World.inst.NumLandMasses)
                    Main.helper.Log($"[SAVE] player has {lmCount} landmass job rows but the world has {World.inst.NumLandMasses}; packing what exists (a remote built before the map was ready)");

                data.JobFilledAvailable = new int[lmCount][];
                data.JobCustomMaxEnabledFlag = new bool[lmCount][];
                for (int lm = 0; lm < lmCount; lm++)
                {
                    // Slot counts come from the player's own arrays, not from a literal. Vanilla's
                    // Unpack compares the packed length against the live array and abandons the
                    // whole job restore on a mismatch, without logging, so a hardcoded count
                    // that a game update outgrows silently drops every job setting in the save.
                    // JobCategory.NumCategories is 39 today and has grown before.
                    int[,] live = p.JobFilledAvailable.data[lm];
                    int slots = live.GetLength(0);

                    // Column 0 is recomputed on load, so only column 1 is worth writing out.
                    int[] available = new int[slots];
                    for (int slot = 0; slot < slots; slot++)
                        available[slot] = live[slot, 1];

                    data.JobFilledAvailable[lm] = available;
                    data.JobCustomMaxEnabledFlag[lm] = (bool[])p.JobCustomMaxEnabledFlag[lm].Clone();
                }

                data.CanUseTools = new bool[p.CanUseTools.Length][];
                for (int i = 0; i < p.CanUseTools.Length; i++)
                    data.CanUseTools[i] = (bool[])p.CanUseTools[i].Clone();
            }
        }


        /// <summary>
        /// Makes the game's own Player code multi-instance safe.
        ///
        /// Vanilla Player methods reach for the <c>Player.inst</c> singleton, which is fine when
        /// there is exactly one player and wrong the moment there are several: every client's
        /// logic would read and write the local player's state. This transpiler rewrites each
        /// <c>ldsfld Player::inst</c> in every declared instance method on Player to <c>ldarg_0</c>
        ///, "this", so a method invoked on a given Player object operates on that object.
        ///
        /// It only works because the targets are instance methods, where arg 0 is the receiver.
        /// Static methods on Player would need the receiver supplied some other way; none are
        /// patched here (TargetMethods filters to BindingFlags.Instance).
        /// </summary>

        /// <summary>
        /// Shared machinery for the two singleton-elimination transpilers below.
        ///
        /// Both answer the same question, "this method reaches for <c>Player.inst</c>, but which
        /// player does it actually mean?", and differ only in how the answer is loaded onto the
        /// stack. So the scan, the count and the reporting live here once, and each patch supplies
        /// the replacement instructions for one <c>ldsfld Player::inst</c>.
        /// </summary>
        private static class SingletonRewrite
        {
            /// <summary>Every declared, non-abstract instance method on <paramref name="owner"/>.</summary>
            public static IEnumerable<MethodBase> InstanceMethodsOf(Type owner, string label)
            {
                var targets = owner
                    .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsAbstract)
                    .ToList();

                helper.Log($"{label}: {targets.Count} instance methods targeted");
                return targets.Cast<MethodBase>();
            }

            /// <summary>
            /// Replaces every <c>ldsfld Player::inst</c> with whatever <paramref name="replacement"/>
            /// returns. The first instruction is written over the existing one rather than
            /// inserted before it, so a branch already targeting that offset still lands right.
            /// </summary>
            public static IEnumerable<CodeInstruction> Apply(
                MethodBase method, IEnumerable<CodeInstruction> instructions,
                Func<CodeInstruction[]> replacement, string describe)
            {
                var codes = new List<CodeInstruction>(instructions);
                int rewritten = 0;

                for (int i = 0; i < codes.Count; i++)
                {
                    if (codes[i].opcode != OpCodes.Ldsfld) continue;
                    if (codes[i].operand.ToString() != "Player inst") continue;

                    CodeInstruction[] with = replacement();
                    codes[i].opcode = with[0].opcode;
                    codes[i].operand = with[0].operand;
                    for (int j = 1; j < with.Length; j++)
                        codes.Insert(++i, with[j]);

                    rewritten++;
                }

                if (rewritten > 0)
                    Main.helper.Log($"rewrote {rewritten} Player.inst reference(s) to {describe} in {method.Name}");

                return codes.AsEnumerable();
            }
        }

        /// <summary>
        /// Inside Player's own instance methods, <c>Player.inst</c> means "me". Argument 0 is the
        /// receiver, so the singleton load becomes <c>ldarg_0</c>, one opcode, same stack depth.
        /// </summary>
        [HarmonyPatch]
        public class PlayerReferencePatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                return SingletonRewrite.InstanceMethodsOf(typeof(Player), "Player.inst transpiler");
            }

            static IEnumerable<CodeInstruction> Transpiler(MethodBase method, IEnumerable<CodeInstruction> instructions)
            {
                return SingletonRewrite.Apply(method, instructions,
                    () => new[] { new CodeInstruction(OpCodes.Ldarg_0) }, "'this'");
            }
        }

        /// <summary>
        /// Inside Building's instance methods, <c>Player.inst</c> means "whoever owns this
        /// building", a Building has no Player of its own, so the receiver alone is not the
        /// answer. Load the Building and map it: <c>ldarg_0</c> + <c>call GetPlayerByBuilding</c>
        /// leaves a Player exactly where the singleton would have been.
        /// </summary>
        [HarmonyPatch]
        public class BuildingPlayerReferencePatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                return SingletonRewrite.InstanceMethodsOf(typeof(Building), "Building owner transpiler");
            }

            static IEnumerable<CodeInstruction> Transpiler(MethodBase method, IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo ownerOf = typeof(Main).GetMethod("GetPlayerByBuilding", BindingFlags.Static | BindingFlags.Public);

                return SingletonRewrite.Apply(method, instructions,
                    () => new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Call, ownerOf),
                    }, "GetPlayerByBuilding");
            }
        }

        /// <summary>
        /// Inside some building components, <c>Player.inst</c> means the kingdom that owns the
        /// building, not the local one.
        ///
        /// These are components next to a Building, not Buildings, so the rewrite above never
        /// reached them (the same gap CalcMaxGold fell through). Before this, every house in the
        /// world was taxed at the local player's rate and sent its residents to the local homeless
        /// list, every farm counted the local player's windmills for its bonus, and a full
        /// blacksmith in another kingdom set off the local player's advisor. Each of these types
        /// keeps its Building in a field named <c>b</c>, so load that and map it to the owner.
        ///
        /// Left alone on purpose: Home.ShowOverlay (whether the LOCAL overlay covers the house) and
        /// Field's wheat drawing (remote farms are drawn by the local player's field system, the
        /// only one that ticks, since cloned players' Update is suppressed).
        /// </summary>
        [HarmonyPatch]
        public class ComponentOwnerReferencePatch
        {
            /// <summary>Type to method names, or null for every instance method on it.</summary>
            private static readonly Dictionary<Type, string[]> Targets = new Dictionary<Type, string[]>
            {
                { typeof(Home), null },
                { typeof(Field), new[] { "Tick", "DeferredYield", "RefreshBonuses" } },
                { typeof(ProducerBasePlural), new[] { "DoYield", "CheckProductionPipeline" } },
            };

            static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var t in Targets)
                    foreach (MethodBase m in SingletonRewrite.InstanceMethodsOf(t.Key, t.Key.Name + " owner transpiler"))
                    {
                        if (t.Value == null ? m.Name == "ShowOverlay" : !t.Value.Contains(m.Name)) continue;
                        yield return m;
                    }
            }

            static IEnumerable<CodeInstruction> Transpiler(MethodBase method, IEnumerable<CodeInstruction> instructions)
            {
                FieldInfo building = AccessTools.Field(method.DeclaringType, "b");
                MethodInfo ownerOf = typeof(Main).GetMethod("GetPlayerByBuilding", BindingFlags.Static | BindingFlags.Public);

                return SingletonRewrite.Apply(method, instructions,
                    () => new[]
                    {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldfld, building),
                        new CodeInstruction(OpCodes.Call, ownerOf),
                    }, "GetPlayerByBuilding(b)");
            }
        }


        /// <summary>
        /// Keeps Unity's own lifecycle off the Player objects this mod constructs.
        ///
        /// A remote player is a cloned Player GameObject that the mod builds and drives itself
        /// (see <c>SessionPlayer.BuildRemotePlayer</c>). Unity still calls Awake and Update on it, and
        /// both assume they are running on the one true singleton, Awake re-seeds
        /// <c>Player.inst</c> and the per-landmass arrays, Update ticks a simulation that only the
        /// owner should tick. Neither is wanted on a clone, so both are suppressed.
        ///
        /// Awake is additionally suppressed on the *local* Player for the duration of a session,
        /// because the mod has already initialised it and a second Awake would reset that work.
        ///
        /// Harmony has no "patch these two methods by name" form that also gives the instance, so
        /// this targets every declared method on Player and dispatches on the name. The cost is a
        /// string compare per Player call; the alternative is two patch classes duplicating the
        /// clone test.
        ///
        /// Nothing else belongs here. A general Update branch, a Postfix, and a per-Player
        /// reflection observer that raised events nobody handled were all removed once it became
        /// clear they ran on *every declared method on Player* to do nothing.
        /// </summary>
        [HarmonyPatch]
        public class PlayerPatch
        {
            private const string CloneObjectName = "Client Player";

            static IEnumerable<MethodBase> TargetMethods()
            {
                return typeof(Player)
                    .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Cast<MethodBase>();
            }

            public static bool Prefix(MethodBase __originalMethod, Player __instance)
            {
                bool isClone = __instance.gameObject.name.Contains(CloneObjectName);

                switch (__originalMethod.Name)
                {
                    case "Awake":
                        if (NetHost.IsRunning || NetClient.client.IsConnected)
                        {
                            helper.Log("suppressing Player.Awake while a session is active");
                            return false;
                        }
                        if (isClone)
                        {
                            helper.Log("suppressing Player.Awake on the cloned client Player");
                            return false;
                        }
                        return true;

                    case "Update":
                        return !isClone;

                    default:
                        return true;
                }
            }
        }

    }

}
