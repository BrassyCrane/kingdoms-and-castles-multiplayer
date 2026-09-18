using System;
using System.Collections.Generic;
using System.Reflection;

using KaCMultiplayer;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Notices when one of the local player's buildings changes and sends a snapshot.
    ///
    /// Deliberately specific rather than a general observer. A framework that watches an
    /// arbitrary field list on an arbitrary object costs a GameObject and a MonoBehaviour per
    /// building, a reflected read of every watched member every hundred milliseconds, and deep
    /// array comparison for a field set containing no arrays, to do exactly what this does.
    ///
    /// The change test here is the payload itself. We capture what we would send and compare
    /// it against what we last sent, so a message goes out exactly when the thing on the wire
    /// would differ, no separate list of watched field names to drift out of step with the
    /// message. Add a field to <see cref="Messages.BuildSnapshotMessage"/> and it is watched;
    /// there is no second place to update.
    /// </summary>
    public static class BuildingWatcher
    {
        /// <summary>
        /// Floor on how often one building may report. Construction progress changes every
        /// tick, so without this a building under construction sends continuously.
        /// </summary>
        private const long MinIntervalMs = 300;

        /// <summary>
        /// <c>resourceProgress</c> is private with no accessor, so it needs reflection. Looked
        /// up once, the previous code did this lookup on every single send.
        /// </summary>
        private static readonly FieldInfo ResourceProgressField =
            typeof(Building).GetField("resourceProgress", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>What we last sent for a building, so we can tell whether anything moved.</summary>
        private class LastSent
        {
            public long AtMs;

            public string UniqueName;
            public string CustomName;
            public bool Built;
            public bool Placed;
            public bool Open;
            public bool DoBuildAnimation;
            public bool ConstructionPaused;
            public float ConstructionProgress;
            public float ResourceProgress;
            public float Life;
            public float ModifiedMaxLife;
            public int YearBuilt;
            public float DecayProtection;
            public bool SeenByPlayer;
        }

        private static readonly Dictionary<Guid, LastSent> tracked = new Dictionary<Guid, LastSent>();

        /// <summary>Forgets everything. Called when networking is torn down.</summary>
        public static void Reset()
        {
            tracked.Clear();
        }

        private static long NowMs
        {
            get { return DateTimeOffset.Now.ToUnixTimeMilliseconds(); }
        }

        /// <summary>
        /// Checks one building and sends a snapshot if it has changed since the last one.
        /// Called from the building's own update, so there is no polling loop and no observer
        /// object, a building that is not ticking cannot have changed.
        /// </summary>
        public static void Poll(Building building)
        {
            if (building == null || !NetRouter.IsConnected) return;

            // Roads are watched like everything else, and the exclusion that used to sit here has
            // been removed.
            //
            // It read: "Roads are placed in full by BuildPlaceMessage and then become complete
            // terrain-like objects." They are not. BuildPlaceMessage carries
            // Built = PendingObj.IsBuilt(), captured at PLACEMENT, and a road at that moment is an
            // unstarted construction site: built false, constructionProgress 0. Skipping the poll
            // meant the other machine was told a road existed and then never told it had been
            // finished, so every remote kingdom's roads sat at zero progress for the life of the
            // session. Saves show it plainly, and it was true of every multiplayer save on this
            // machine: the local kingdom's roads all built, the other kingdom's all unbuilt, while
            // its farms, houses, keep and quarries were fine. Only roads were excluded, so only
            // roads were wrong.
            //
            // The duplicate-CompleteBuild problem the exclusion was written for was real, and has
            // since been fixed at its source rather than by hiding roads from the watcher:
            // ApplyBuildSnapshot now calls CompleteBuild instead of writing `built` by reflection,
            // and BuildingCompleteBuildHook makes CompleteBuild idempotent, which is what makes a
            // second completion safe in either arrival order.
            //
            // Cost is bounded: a snapshot only goes out when the payload would actually differ, no
            // building may report more often than MinIntervalMs, and a finished road stops changing
            // and therefore stops sending.

            // Only report our own buildings. Everyone else's arrive as snapshots from them.
            try
            {
                if (building.TeamID() != Player.inst.PlayerLandmassOwner.teamId) return;
            }
            catch { return; }   // no team yet, nothing worth reporting

            try
            {
                Guid guid = building.guid;
                float resourceProgress = ReadResourceProgress(building);

                LastSent last;
                if (!tracked.TryGetValue(guid, out last))
                {
                    // First sighting. Record and send, so a late joiner's view converges.
                    last = new LastSent();
                    tracked.Add(guid, last);
                    Capture(building, resourceProgress, last);
                    Send(building, resourceProgress);
                    last.AtMs = NowMs;
                    return;
                }

                if (!HasChanged(building, resourceProgress, last)) return;

                long now = NowMs;
                if (now - last.AtMs < MinIntervalMs) return;   // throttled; will send next time

                Capture(building, resourceProgress, last);
                Send(building, resourceProgress);
                last.AtMs = now;
            }
            catch (Exception ex)
            {
                NetLog.Error("building watcher", ex);
            }
        }

        private static float ReadResourceProgress(Building building)
        {
            if (ResourceProgressField == null) return 0f;
            try { return (float)ResourceProgressField.GetValue(building); }
            catch { return 0f; }
        }

        private static bool HasChanged(Building b, float resourceProgress, LastSent last)
        {
            return last.UniqueName != b.UniqueName
                || last.CustomName != b.customName
                || last.Built != b.IsBuilt()
                || last.Placed != b.IsPlaced()
                || last.Open != b.Open
                || last.DoBuildAnimation != b.doBuildAnimation
                || last.ConstructionPaused != b.constructionPaused
                || last.ConstructionProgress != b.constructionProgress
                || last.ResourceProgress != resourceProgress
                || last.Life != b.Life
                || last.ModifiedMaxLife != b.ModifiedMaxLife
                || last.YearBuilt != b.YearBuilt
                || last.DecayProtection != b.decayProtection
                || last.SeenByPlayer != b.seenByPlayer;
        }

        private static void Capture(Building b, float resourceProgress, LastSent into)
        {
            into.UniqueName = b.UniqueName;
            into.CustomName = b.customName;
            into.Built = b.IsBuilt();
            into.Placed = b.IsPlaced();
            into.Open = b.Open;
            into.DoBuildAnimation = b.doBuildAnimation;
            into.ConstructionPaused = b.constructionPaused;
            into.ConstructionProgress = b.constructionProgress;
            into.ResourceProgress = resourceProgress;
            into.Life = b.Life;
            into.ModifiedMaxLife = b.ModifiedMaxLife;
            into.YearBuilt = b.YearBuilt;
            into.DecayProtection = b.decayProtection;
            into.SeenByPlayer = b.seenByPlayer;
        }

        private static void Send(Building b, float resourceProgress)
        {
            NetRouter.Send(new Messages.BuildSnapshotMessage
            {
                State = new Messages.BuildingState
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
                },
                ResourceProgress = resourceProgress
            });
        }
    }
}

