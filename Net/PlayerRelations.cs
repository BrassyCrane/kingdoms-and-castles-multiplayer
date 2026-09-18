using System;
using System.Collections.Generic;
using UnityEngine;

using KaCMultiplayer;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Who is at war with whom, for multiplayer teams.
    ///
    /// The game already models this, <c>World.Relations</c> is Neutral / Allies / Enemy, and
    /// <c>World.RelationBetween</c> answers for any pair, but it cannot answer for us:
    ///
    /// <code>
    ///     private Relations[,] hostility = new Relations[5, 5];
    ///     ...
    ///     if (teamIDA >= 0 &amp;&amp; teamIDA &lt; 5 &amp;&amp; teamIDB >= 0 &amp;&amp; teamIDB &lt; 5)
    ///         return hostility[teamIDA, teamIDB];
    ///     Debug.Log(teamIDA + " " + teamIDB);
    ///     return Relations.Neutral;
    /// </code>
    ///
    /// Multiplayer teams are 5, 6, 7, outside that range, so every player pair fell through to
    /// the last line and came back Neutral, permanently and unchangeably. This is the third
    /// fixed-size-by-team array to bite this project, after <c>OrdersManager.unitsByTeamID</c> and
    /// <c>PathCell</c>.
    ///
    /// We keep our own table rather than enlarging the game's, for two reasons. The bounds check
    /// above is a hardcoded literal, not <c>hostility.GetLength(0)</c>, so a bigger array alone
    /// would change nothing. And <c>hostility</c> is packed into <c>WorldSaveData</c>, resizing it
    /// would change the shape of every save this mod writes, to fix a method we have to patch
    /// anyway.
    ///
    /// <c>World.SetRelations</c> is likewise unusable here: past the array write it does
    /// <c>kingdomFromTeamId.enemyPoints = 0</c> and <c>kingdomFromTeamId.ignoreCount = 0</c> with no
    /// null check, and <c>kingdomFromTeamId</c> is always null in this mod because there are no AI
    /// kingdoms. <see cref="Set"/> does the parts that actually apply to us instead.
    /// </summary>
    public static class PlayerRelations
    {
        /// <summary>
        /// Lowest team id the mod hands out. Below this sits the single-player team (0), the AI
        /// kingdom range (2-4) and the raider/neutral sentinels, all of which vanilla answers for
        /// correctly, so anything below this is left entirely alone.
        /// </summary>
        public const int MpTeamBase = 5;

        /// <summary>Relation per unordered team pair. Absent means <see cref="Default"/>.</summary>
        private static readonly Dictionary<long, World.Relations> relations =
            new Dictionary<long, World.Relations>();

        /// <summary>
        /// What two players are to each other until somebody says otherwise.
        ///
        /// Neutral, matching both the game's own zero-initialised <c>hostility</c> array and the
        /// behaviour players have had until now. Allies would be friendlier for co-op but it would
        /// also mean a session silently changing meaning the moment relations became settable.
        /// </summary>
        public const World.Relations Default = World.Relations.Neutral;

        /// <summary>True when this pair is two multiplayer players rather than anything vanilla owns.</summary>
        public static bool IsPlayerPair(int teamA, int teamB)
        {
            return teamA >= MpTeamBase && teamB >= MpTeamBase;
        }

        /// <summary>Order-independent key, so (5,6) and (6,5) are the same entry. See TeamPair.</summary>
        private static long Key(int teamA, int teamB)
        {
            return TeamPair.Key(teamA, teamB);
        }

        /// <summary>
        /// The relation between two multiplayer teams. A team is always allied with itself, which
        /// is what vanilla answers first and what a great deal of code downstream assumes.
        /// </summary>
        public static World.Relations Get(int teamA, int teamB)
        {
            if (teamA == teamB) return World.Relations.Allies;

            World.Relations r;
            return relations.TryGetValue(Key(teamA, teamB), out r) ? r : Default;
        }

        /// <summary>
        /// Records a relation and applies the consequences vanilla would have applied.
        ///
        /// Local only, it does not broadcast. Callers that represent a player's decision send a
        /// PlayerRelation message; the handler calls this on every machine. That keeps the "one
        /// decision, everyone applies it" shape the rest of the mod uses, and stops an applied
        /// message echoing back out.
        /// </summary>
        public static void Set(int teamA, int teamB, World.Relations r)
        {
            if (teamA == teamB) return;                       // a kingdom cannot declare war on itself
            if (!IsPlayerPair(teamA, teamB)) return;          // vanilla's table owns this pair

            World.Relations was = Get(teamA, teamB);
            relations[Key(teamA, teamB)] = r;

            NetLog.Info("relations: team " + teamA + " and team " + teamB + " are now " + r +
                        (was == r ? " (unchanged)" : " (was " + was + ")"));

            if (was == r) return;

            RebakeGates();
            ApplyDockPolicy(teamA, teamB, r);
        }

        /// <summary>
        /// Re-bakes pathing on every gate.
        ///
        /// Vanilla's SetRelations does exactly this, and it is not cosmetic: a gate decides whether
        /// to let a unit through by the relation between its owner and that unit's team, and it
        /// caches that decision in its baked pathing. Skip it and a freshly declared enemy walks
        /// through your gates until something else happens to re-bake them.
        ///
        /// GetBuildingList reads the global registry, so this covers every player's gates, which
        /// is what we want, since the relation changed for both sides.
        /// </summary>
        private static void RebakeGates()
        {
            try
            {
                if (Player.inst == null) return;

                Rebake(World.gateHash);
                Rebake(World.woodengateHash);
                Rebake(World.seagateHash);
            }
            catch (Exception ex) { NetLog.Error("re-baking gates after a relation change", ex); }
        }

        private static void Rebake(int uniqueNameHash)
        {
            ArrayExt<Building> gates = Player.inst.GetBuildingList(uniqueNameHash);
            for (int i = 0; i < gates.Count; i++)
                if (gates.data[i] != null) gates.data[i].BakePathing();
        }

        /// <summary>
        /// Opens or closes the two kingdoms' docks to match the new relation.
        ///
        /// Vanilla closes docks on a declaration of war, and the same should hold here, but this
        /// mod also re-opens every player pair's docks on a timer (Main.EnsureTradeDocksOpenInMP),
        /// so closing them here alone would last about a second. That method skips enemies now;
        /// this is the other half.
        /// </summary>
        private static void ApplyDockPolicy(int teamA, int teamB, World.Relations r)
        {
            try
            {
                if (Player.inst == null) return;

                LandmassOwner a = World.GetLandmassOwnerByTeamId(teamA);
                LandmassOwner b = World.GetLandmassOwnerByTeamId(teamB);
                if (a == null || b == null) return;

                // Both OpenDocks and CloseDocks Invoke() OnDocksOpenForTrade with no null guard, so
                // either NREs when nothing is subscribed, and only AI trade intentions subscribe,
                // of which there are none here. Main.EnsureTradeDocksOpenInMP seeds a no-op for the
                // same reason, but a relation can change before its first run.
                if (Player.inst.OnDocksOpenForTrade == null)
                    Player.inst.OnDocksOpenForTrade += new Player.OnDockUpdate((x, y, open) => { });

                if (r == World.Relations.Enemy)
                {
                    Player.inst.CloseDocks(a, b);
                    NetLog.Info("relations: closed docks between team " + teamA + " and team " + teamB);
                }
                else
                {
                    Player.inst.OpenDocks(a, b);
                }
            }
            catch (Exception ex) { NetLog.Error("applying dock policy after a relation change", ex); }
        }

        /// <summary>Every pair currently on record, for saving and for sending to a joiner.</summary>
        public static Dictionary<long, World.Relations> Snapshot()
        {
            return new Dictionary<long, World.Relations>(relations);
        }

        /// <summary>
        /// Reinstates a whole set of relations at once, from a save or from the host on join.
        ///
        /// Replaces rather than merges: the snapshot is the complete truth about who is at war with
        /// whom, and merging would let a stale local entry survive a load and leave two machines
        /// disagreeing about whether there is a war on.
        ///
        /// Gates are rebaked ONCE at the end rather than per pair. Rebaking walks the map, so doing
        /// it inside the loop would repeat that work for every pair for no benefit. Dock policy is
        /// applied per pair because it is cheap and pair-specific.
        ///
        /// Deliberately does not broadcast. Restoring is not a decision anybody made; the host sends
        /// the same snapshot to every machine, and each applies it locally. Broadcasting here would
        /// turn one load into a storm of declarations.
        /// </summary>
        public static void Restore(Dictionary<long, World.Relations> saved)
        {
            relations.Clear();

            if (saved == null || saved.Count == 0)
            {
                NetLog.Info("relations: nothing to restore, everyone starts " + Default);
                return;
            }

            foreach (KeyValuePair<long, World.Relations> entry in saved)
                relations[entry.Key] = entry.Value;

            NetLog.Info("relations: restored " + relations.Count + " pair(s)");

            try
            {
                RebakeGates();

                foreach (KeyValuePair<long, World.Relations> entry in relations)
                    ApplyDockPolicy(TeamPair.Low(entry.Key), TeamPair.High(entry.Key), entry.Value);
            }
            catch (Exception ex) { NetLog.Error("applying restored relations", ex); }
        }

        /// <summary>Drops every recorded relation. Called when a session ends.</summary>
        public static void Reset()
        {
            relations.Clear();
        }
    }
}
