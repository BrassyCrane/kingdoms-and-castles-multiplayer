using System;
using System.Collections.Generic;
using UnityEngine;
using KaCMultiplayer.Net;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Combat
{
    /// <summary>
    /// Corrects where other players' armies are standing, occasionally and only when they have
    /// drifted far enough to matter.
    ///
    /// THE GAP THIS CLOSES. Only ORDERS travel between machines, never positions, so each machine
    /// walks its copy of an army there by itself. That is usually fine and is why the traffic was
    /// never spent: two machines given the same destination arrive at the same place. It stops
    /// being fine during a chase, where the destination keeps changing and pathing is threaded, so
    /// the copies wander apart. With combat now arbitrated, the health of an army agrees everywhere
    /// while its POSITION does not, and the visible result is one player watching a battle happen
    /// somewhere the other player sees nothing at all.
    ///
    /// OWNER-AUTHORITATIVE, and deliberately not landmass-arbitrated like damage. Where an army is
    /// standing is not the outcome of a fight that a defender should get to judge; it is the result
    /// of the owner's own orders and their own pathing. The owner says where their army is and
    /// everyone else agrees. This also means each army is announced by exactly one machine, with no
    /// election and no chance of two machines correcting each other in a loop.
    ///
    /// WHY THIS IS AFFORDABLE when villager position sync was not. A session has tens of armies and
    /// thousands of villagers, and the villager version broadcast every frame. This sends one
    /// batched message every 30 fixed ticks, holds nothing when nothing has moved, and puts every
    /// army in a single message so the cost is one packet rather than one per army.
    ///
    /// SHIPS DARK behind Main.ArmyPositionSyncEnabled. The user had already decided to leave army
    /// positions alone, and this does not overturn that: it makes the option exist and testable.
    /// </summary>
    public static class ArmyPositionSync
    {
        /// <summary>Fixed ticks between broadcasts. Slow on purpose: this is a correction, not a
        /// stream, and an army that is where everyone thinks it is sends nothing at all.</summary>
        private const int TicksBetweenSends = 30;

        /// <summary>
        /// How far our copy may sit from the owner's before we move it, in world units.
        ///
        /// The single most important number here. Too small and every correction fights the local
        /// pathing that is already walking the army to the same place, producing a permanent
        /// tug-of-war and a stuttering army. Too large and the correction never fires when it is
        /// needed. Two units is roughly two tiles, which is wider than the formation itself, so
        /// ordinary marching never trips it and a genuine divergence does.
        /// </summary>
        private const float CorrectionDistance = 2f;

        /// <summary>How far an army must move before we bother re-announcing it.</summary>
        private const float ReportDistance = 0.5f;

        private static int tickCounter;

        private static readonly Dictionary<Guid, Vector3> lastSent = new Dictionary<Guid, Vector3>();

        /// <summary>Batches reused between sends so a quiet tick allocates nothing.</summary>
        private static readonly List<Guid> batchIds = new List<Guid>();
        private static readonly List<Vector3> batchPositions = new List<Vector3>();

        /// <summary>Position corrections published, and applied, since the session began. Counted so
        /// a silent mechanism can be asserted rather than assumed.</summary>
        public static int Published { get; private set; }

        public static int Corrected { get; private set; }

        /// <summary>Forgets published state, for a new session or a load.</summary>
        public static void Reset()
        {
            lastSent.Clear();
            batchIds.Clear();
            batchPositions.Clear();
            tickCounter = 0;
            Published = 0;
            Corrected = 0;
        }

        /// <summary>Called every fixed tick. Announces our own armies that have moved.</summary>
        public static void Tick()
        {
            if (!Main.ArmyPositionSyncEnabled) return;   // feature dark
            if (!NetClient.client.IsConnected) return;
            if (UnitSystem.inst == null) return;

            if (++tickCounter < TicksBetweenSends) return;
            tickCounter = 0;

            try
            {
                int localTeam = LocalTeam();
                if (localTeam == int.MinValue) return;

                batchIds.Clear();
                batchPositions.Clear();

                var armies = UnitSystem.inst.armies;
                for (int i = 0; i < armies.Count; i++)   // .Count, never .data.Length
                {
                    UnitSystem.Army army = armies.data[i];
                    if (army == null || army.teamId != localTeam) continue;   // only ours to announce

                    Vector3 now = army.generalPos;

                    Vector3 before;
                    if (lastSent.TryGetValue(army.guid, out before)
                        && (before - now).sqrMagnitude < ReportDistance * ReportDistance)
                        continue;   // has not meaningfully moved

                    lastSent[army.guid] = now;
                    batchIds.Add(army.guid);
                    batchPositions.Add(now);
                }

                if (batchIds.Count == 0) return;

                Published++;
                NetRouter.Send(new ArmyPositionsMessage
                {
                    Armies = new List<Guid>(batchIds),
                    Positions = new List<Vector3>(batchPositions)
                });
            }
            catch (Exception ex) { NetLog.Error("army position sweep", ex); }
        }

        /// <summary>The local player's team, or int.MinValue when there is not one yet.</summary>
        private static int LocalTeam()
        {
            try
            {
                return (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                    ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;
            }
            catch { return int.MinValue; }
        }

        /// <summary>
        /// Moves our copies of another player's armies to where their owner says they are.
        ///
        /// Through the army's own SetPos, which is what the game itself calls after raising one.
        /// That matters: SetPos moves the general and then gives each soldier a new TARGET position
        /// in formation, so the squad WALKS into place instead of teleporting. A correction is
        /// therefore something the player sees as the army adjusting its line, not as it flickering.
        ///
        /// Only past the threshold, and never for our own armies. Ours are authoritative here, and
        /// applying a stale report of our own position would fight the pathing that is currently
        /// moving them. That cannot normally arrive, since the sender skips armies it does not own,
        /// but a message crossing a team change should not be able to do it either.
        ///
        /// Note SetPos makes an SRand draw for its formation jitter, so a receiver takes draws a
        /// non-receiving machine does not. Post-worldgen SRand divergence is already normal and
        /// tolerated in this mod, for exactly the same reason suppressed damage is.
        /// </summary>
        public static void Apply(ArmyPositionsMessage m)
        {
            // Deliberately NOT gated on Main.ArmyPositionSyncEnabled, unlike the sweep. With the
            // flag off nowhere sends, so this never runs anyway; but in a mixed session, where one
            // player is testing the feature and the others are not, accepting their corrections is
            // strictly better than ignoring them. Only the machine that SPEAKS needs the flag.
            try
            {
                if (m.Armies == null || m.Positions == null) return;
                if (UnitSystem.inst == null) return;

                int localTeam = LocalTeam();
                int shared = m.Armies.Count < m.Positions.Count ? m.Armies.Count : m.Positions.Count;
                int moved = 0;

                for (int i = 0; i < shared; i++)
                {
                    // Guarded PER ARMY. One army that has already died here must not cost the rest
                    // of the batch their correction.
                    try
                    {
                        UnitSystem.Army army = UnitSystem.inst.FindArmyByGuid(m.Armies[i]);
                        if (army == null) continue;          // not here, or already gone
                        if (army.teamId == localTeam) continue;   // ours; we are the authority

                        Vector3 theirs = m.Positions[i];
                        if ((army.generalPos - theirs).sqrMagnitude
                            < CorrectionDistance * CorrectionDistance) continue;

                        army.SetPos(theirs);
                        moved++;
                    }
                    catch { }
                }

                if (moved == 0) return;

                Corrected += moved;
                NetLog.Info("army positions: corrected " + moved + " of " + shared);
            }
            catch (Exception ex) { NetLog.Error("army positions", ex); }
        }
    }
}
