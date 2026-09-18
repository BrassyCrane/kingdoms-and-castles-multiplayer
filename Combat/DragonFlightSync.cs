using System;
using System.Collections.Generic;
using UnityEngine;
using KaCMultiplayer.Net;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Combat
{
    /// <summary>
    /// Makes every machine's dragons fly the same flight.
    ///
    /// THE GAP THIS CLOSES. Dragons already spawned on every machine and already agreed on their
    /// health, so both players saw a dragon and both saw it die. What nobody shared was what it
    /// DID in between. <c>Dragon</c> is an ordinary MonoBehaviour whose <c>Update</c> runs
    /// <c>UpdateMovement</c> and <c>UpdateActions</c> locally, and target selection reads
    /// <c>Player.inst</c>, which is a different kingdom on every machine. So the copies were not
    /// drifting apart, they were making genuinely different decisions: two dragons, same name,
    /// same hit points, burning two different villages.
    ///
    /// WHY THIS IS NOT THE ARMY FIX AGAIN. <see cref="ArmyPositionSync"/> is a CORRECTION: both
    /// machines were given the same order and are walking to the same place, so they agree except
    /// for drift, and an occasional nudge is enough. A dragon has no order to agree on. Correcting
    /// its position every so often while a local AI actively flies it somewhere else produces a
    /// dragon that snaps back and forth between two destinations, which is worse than the
    /// divergence it was meant to fix.
    ///
    /// So dragons are PUPPETS, not corrected peers. The host flies them; everyone else stops
    /// deciding and follows. The suppression is deliberately narrow: only
    /// <c>Dragon.UpdateMovement</c> and <c>Dragon.UpdateActions</c> are skipped, so the fire
    /// particles, the roar, the health bar, the hit flash, the wing animation and
    /// <c>LookWithHead</c> all still run locally off the synced state. A followed dragon is still
    /// a fully animated dragon, it just no longer has opinions.
    ///
    /// WHY THE HOST. Dragons are already host-authoritative at spawn (see the DragonSpawn hooks in
    /// Main), so this changes no ownership, it just extends an existing decision to the rest of
    /// the dragon's life. It also means exactly one machine ever publishes, with no election.
    ///
    /// AFFORDABLE. A session has a handful of dragons against tens of armies and thousands of
    /// villagers, which is why this can send far more often than <see cref="ArmyPositionSync"/>
    /// does and still cost less. A dragon crosses the map in seconds; the army rate would show it
    /// teleporting.
    /// </summary>
    public static class DragonFlightSync
    {
        /// <summary>
        /// Fixed ticks between broadcasts. Much faster than the army rate on purpose: this is a
        /// stream, not a correction, because the receiver is no longer flying the thing itself and
        /// has nothing of its own to fall back on between updates.
        /// </summary>
        private const int TicksBetweenSends = 5;

        /// <summary>How far a dragon must move before it is worth re-announcing, in world units.</summary>
        private const float ReportDistance = 0.25f;

        /// <summary>How far its facing must turn before it is worth re-announcing, in degrees.</summary>
        private const float ReportAngle = 2f;

        /// <summary>
        /// Beyond this gap we stop easing and just put the dragon where it belongs.
        ///
        /// Smoothing a large correction looks like the dragon flying there, which is a lie: it did
        /// not fly there, it was somewhere else. That matters at exactly the moments a big gap
        /// happens, a join, a stall, a dragon that was off doing its own thing before this feature
        /// caught it.
        /// </summary>
        private const float SnapDistance = 25f;

        /// <summary>
        /// Easing rate for ordinary following, per second. Applied as
        /// <c>1 - exp(-Smoothing * dt)</c> so the result does not depend on frame rate, which a
        /// plain <c>Lerp(a, b, k)</c> would.
        /// </summary>
        private const float Smoothing = 12f;

        /// <summary>What the host last told us about one dragon.</summary>
        private struct Target
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public bool Firing;
            public bool HasFired;   // whether Firing has ever been applied, so the first one always is
        }

        private static readonly Dictionary<Guid, Target> targets = new Dictionary<Guid, Target>();

        /// <summary>What we last sent, so a dragon holding still sends nothing.</summary>
        private static readonly Dictionary<Guid, Target> lastSent = new Dictionary<Guid, Target>();

        private static int tickCounter;

        /// <summary>Batches reused between sends so a quiet tick allocates nothing.</summary>
        private static readonly List<Guid> batchIds = new List<Guid>();
        private static readonly List<Vector3> batchPositions = new List<Vector3>();
        private static readonly List<Quaternion> batchRotations = new List<Quaternion>();
        private static readonly List<bool> batchFiring = new List<bool>();

        /// <summary>Flight updates published and applied since the session began. Counted so a
        /// silent mechanism can be asserted rather than assumed, as CombatSync does.</summary>
        public static int Published { get; private set; }

        public static int Applied { get; private set; }

        /// <summary>Forgets published state, for a new session or a load.</summary>
        public static void Reset()
        {
            targets.Clear();
            lastSent.Clear();
            batchIds.Clear();
            batchPositions.Clear();
            batchRotations.Clear();
            batchFiring.Clear();
            tickCounter = 0;
            sincePrune = 0;
            Published = 0;
            Applied = 0;
        }

        /// <summary>
        /// True when this machine decides what the dragons do. The host, or anyone not in a
        /// session at all, because single player must be left exactly as it shipped.
        /// </summary>
        public static bool IsAuthority()
        {
            try
            {
                if (!Main.DragonFlightSyncEnabled) return true;
                if (!NetClient.client.IsConnected) return true;   // single player
                return NetHost.IsRunning;
            }
            catch { return true; }   // never take a dragon's brain away because a check threw
        }

        /// <summary>Called every fixed tick. On the host, publishes dragons that have moved.</summary>
        public static void Tick()
        {
            if (!Main.DragonFlightSyncEnabled) return;
            if (!NetClient.client.IsConnected || !NetHost.IsRunning) return;
            if (DragonSpawn.inst == null) return;

            if (++tickCounter < TicksBetweenSends) return;
            tickCounter = 0;

            try
            {
                var all = DragonSpawn.inst.currentDragons;
                if (all == null || all.Count == 0)
                {
                    if (lastSent.Count > 0) lastSent.Clear();   // they are all gone
                    return;
                }

                batchIds.Clear();
                batchPositions.Clear();
                batchRotations.Clear();
                batchFiring.Clear();

                for (int i = 0; i < all.Count; i++)   // .Count, never .data.Length
                {
                    Dragon d = all.data[i];
                    if (d == null || d.id == Guid.Empty) continue;

                    Vector3 pos = d.transform.position;
                    Quaternion rot = d.transform.rotation;
                    bool firing = FireEnabled(d);

                    Target before;
                    if (lastSent.TryGetValue(d.id, out before)
                        && before.Firing == firing
                        && (before.Position - pos).sqrMagnitude < ReportDistance * ReportDistance
                        && Quaternion.Angle(before.Rotation, rot) < ReportAngle)
                        continue;   // nothing anyone would notice has changed

                    lastSent[d.id] = new Target { Position = pos, Rotation = rot, Firing = firing };

                    batchIds.Add(d.id);
                    batchPositions.Add(pos);
                    batchRotations.Add(rot);
                    batchFiring.Add(firing);
                }

                PruneDead();

                if (batchIds.Count == 0) return;

                Published++;
                NetRouter.Send(new DragonFlightMessage
                {
                    Dragons = new List<Guid>(batchIds),
                    Positions = new List<Vector3>(batchPositions),
                    Rotations = new List<Quaternion>(batchRotations),
                    Firing = new List<bool>(batchFiring)
                });
            }
            catch (Exception e) { Main.LogEx("dragon flight publish", e); }
        }

        /// <summary>Records what the host says, for <see cref="Steer"/> to move towards.</summary>
        public static void Apply(DragonFlightMessage m)
        {
            if (m == null || m.Dragons == null) return;

            // Parallel lists: a short one means a truncated or mismatched message, and reading past
            // it would pair a dragon with someone else's position.
            int count = m.Dragons.Count;
            if (m.Positions == null || m.Positions.Count < count
                || m.Rotations == null || m.Rotations.Count < count
                || m.Firing == null || m.Firing.Count < count)
            {
                NetLog.Warn("dragon flight: ragged message, " + count + " dragons but "
                            + (m.Positions == null ? 0 : m.Positions.Count) + " positions; ignored");
                return;
            }

            for (int i = 0; i < count; i++)
            {
                Guid id = m.Dragons[i];
                if (id == Guid.Empty) continue;

                // HasFired is carried over from what we already had. Rebuilding the struct without it
                // reset it to false on every message, and a flying dragon is in nearly every message,
                // so Steer re-sent FireBreath every few ticks: exactly the "permanently about to roar"
                // behaviour Steer's own comment says it avoids.
                Target before;
                bool hadFired = targets.TryGetValue(id, out before) && before.HasFired;

                targets[id] = new Target
                {
                    Position = m.Positions[i],
                    Rotation = m.Rotations[i],
                    Firing = m.Firing[i],
                    HasFired = hadFired
                };
            }

            Applied++;
            PruneDead();
        }

        /// <summary>
        /// Moves one followed dragon towards where the host says it is. Called every frame from a
        /// postfix on <c>Dragon.Update</c>, which still runs, only its two decision-making calls
        /// are suppressed.
        /// </summary>
        public static void Steer(Dragon d)
        {
            if (d == null || IsAuthority()) return;

            try
            {
                Target target;
                if (d.id == Guid.Empty || !targets.TryGetValue(d.id, out target)) return;   // nothing heard yet

                Transform t = d.transform;
                Vector3 pos = t.position;

                if ((target.Position - pos).sqrMagnitude > SnapDistance * SnapDistance)
                {
                    t.position = target.Position;
                    t.rotation = target.Rotation;
                }
                else
                {
                    // Frame-rate independent easing: the same wall-clock catch-up whether the
                    // machine is running at 30fps or 200.
                    float k = 1f - Mathf.Exp(-Smoothing * Time.deltaTime);
                    t.position = Vector3.Lerp(pos, target.Position, k);
                    t.rotation = Quaternion.Slerp(t.rotation, target.Rotation, k);
                }

                // Fire is a state, not an event, so it is driven off the difference rather than
                // called every frame: FireBreath re-rolls the roar timer each time it is told to
                // start, so calling it repeatedly would keep the dragon permanently about to roar.
                if (!target.HasFired || FireEnabled(d) != target.Firing)
                {
                    d.FireBreath(target.Firing);
                    target.HasFired = true;
                    targets[d.id] = target;
                }
            }
            catch (Exception e) { Main.LogEx("dragon flight steer", e); }
        }

        /// <summary>How many publishes or applies between sweeps for dragons that have died.</summary>
        private const int PruneEvery = 64;

        private static int sincePrune;

        private static readonly HashSet<Guid> liveIds = new HashSet<Guid>();
        private static readonly List<Guid> staleIds = new List<Guid>();

        /// <summary>
        /// Drops dragons that are no longer in the world.
        ///
        /// Dragons die, and the game removes them from <c>currentDragons</c> without telling
        /// anyone, so neither table can be trimmed on an event. Swept instead of hooked because
        /// there is no seam to hook: a dragon dies deep inside TakeDamage, and patching that to
        /// maintain a lookup table would be a lot of blast radius for a handful of dictionary
        /// entries. A session produces few dragons, so this is untidiness rather than a leak, but
        /// the sweep is cheap enough that leaving it to grow is not worth the argument.
        /// </summary>
        private static void PruneDead()
        {
            if (++sincePrune < PruneEvery) return;
            sincePrune = 0;

            if (DragonSpawn.inst == null) return;

            liveIds.Clear();
            var all = DragonSpawn.inst.currentDragons;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    Dragon d = all.data[i];
                    if (d != null) liveIds.Add(d.id);
                }
            }

            Sweep(targets);
            Sweep(lastSent);
        }

        /// <summary>Removes every entry whose dragon is gone. Collected first, because a
        /// dictionary cannot be modified while it is being enumerated.</summary>
        private static void Sweep(Dictionary<Guid, Target> table)
        {
            if (table.Count == 0) return;

            staleIds.Clear();
            foreach (Guid id in table.Keys)
                if (!liveIds.Contains(id)) staleIds.Add(id);

            for (int i = 0; i < staleIds.Count; i++) table.Remove(staleIds[i]);
        }

        /// <summary><c>fireEnabled</c> is private and has no accessor, so it needs reflection.</summary>
        private static bool FireEnabled(Dragon d)
        {
            return PrivateField.Get<bool>(d, "fireEnabled", false);
        }
    }
}
