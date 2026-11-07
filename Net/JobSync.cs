// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using Harmony;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Shows who works where in another player's kingdom.
    ///
    /// Only a kingdom's own machine decides job assignments (JobUpdateAssignmentForeignHook), and
    /// nothing sent them, so on everyone else's screen that kingdom's workplaces stood empty and
    /// its villagers idle. The owner now announces each assignment and each vacancy, and the
    /// others apply it with the game's own AssignEmployee / QuitJob, the same calls the game's
    /// save uses to restore jobs on load.
    /// </summary>
    public static class JobSync
    {
        /// <summary>
        /// Announces a change to one of our own kingdom's jobs. Says nothing in single player,
        /// while applying a peer's change, or for a job on someone else's land.
        /// </summary>
        public static void Announce(Job job, Villager villager)
        {
            try
            {
                if (!NetClient.client.IsConnected || NetApply.InProgress || job == null) return;

                Building b = job.employer as Building;
                if (b == null) return;
                int lm = b.LandMass();
                if (lm < 0 || World.GetLandmassOwner(lm) == null || Main.IsForeignLandmass(lm)) return;

                int idx = b.jobs.IndexOf(job);
                if (idx < 0) return;

                NetRouter.Send(new JobAssignMessage
                {
                    Building = b.guid,
                    JobIndex = idx,
                    Villager = villager != null ? villager.guid : Guid.Empty
                });
            }
            catch (Exception e) { Main.helper.Log("[JOBS] announce failed: " + e.Message); }
        }

        /// <summary>
        /// Applies another kingdom's job change to our copy. A building or villager this machine
        /// does not have (yet, or any more) is skipped quietly: the next change, or the next save
        /// load, sets it right.
        /// </summary>
        public static void Apply(JobAssignMessage m)
        {
            if (m.Origin == NetRouter.LocalClientId) return;   // our own, relayed back

            try
            {
                Building b = Main.FindBuildingByGuidAnywhere(m.Building);
                if (b == null || m.JobIndex < 0 || m.JobIndex >= b.jobs.Count) return;
                Job job = b.jobs[m.JobIndex];

                if (m.Villager == Guid.Empty)
                {
                    if (job.Employee != null) job.Employee.QuitJob(false);
                    return;
                }

                Villager v = FindVillager(m.Villager);
                if (v == null || job.Employee == v) return;
                if (v.landMass != b.LandMass()) return;   // same check the game's save makes

                if (job.Employee != null) job.Employee.QuitJob(false);
                job.AssignEmployee(v);
            }
            catch (Exception e) { NetLog.Error("job assign", e); }
        }

        /// <summary>A villager by the id every machine agrees on, bounded by Count (see ArrayExt).</summary>
        private static Villager FindVillager(Guid id)
        {
            var all = Villager.villagers;
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                Villager v = all.data[i];
                if (v != null && v.guid == id) return v;
            }
            return null;
        }

        /// <summary>Our kingdom filled a job: tell the others who took it.</summary>
        [HarmonyPatch(typeof(Job), "AssignEmployee")]
        public class JobAssignEmployeeSyncHook
        {
            public static void Postfix(Job __instance, Villager bestWorker)
            {
                Announce(__instance, bestWorker);
            }
        }

        /// <summary>
        /// Our kingdom's villager left a job: tell the others it is vacant. Read before the game
        /// clears villager.job. AssignEmployee also calls this when moving a villager between
        /// jobs, and the vacancy it sends is followed at once by the new assignment.
        /// </summary>
        [HarmonyPatch(typeof(Villager), "QuitJob")]
        public class VillagerQuitJobSyncHook
        {
            public static void Prefix(Villager __instance)
            {
                if (__instance != null && __instance.job != null) Announce(__instance.job, null);
            }
        }
    }
}
