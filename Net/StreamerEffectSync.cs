using System;
using System.Collections.Generic;
using System.Reflection;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Keeps the game's streamer effects the same on every machine in a session.
    ///
    /// Streamer mode lets a Twitch audience vote for effects that change the simulation outright:
    /// Assets.StreamerEffects holds a public static bool per effect and the game reads them in the
    /// places you would least want a disagreement. StrongerEnemies halves damage inside
    /// SiegeCatapult's damage method, BetterBarracksTraining doubles training speed inside
    /// Barracks.Tick, FreeBuilding and BuildingCostsReduced change what things cost.
    ///
    /// They are per-machine statics set by ONE player's audience, so in multiplayer they diverge by
    /// construction: the streamer's world gets faster villagers and everyone else's does not, and
    /// with combat arbitrated by whoever owns the ground, the same fight resolves under different
    /// rules depending on which island it happens on. Nobody has to be cheating for that to be
    /// wrong; it is simply two machines simulating different games.
    ///
    /// So the flags travel. Whoever's effects change says so and everyone adopts them, which is
    /// last-writer-wins and needs to be: the flags are a single global set, and two audiences
    /// voting at once is a situation the game itself has no answer for either.
    ///
    /// DARK unless somebody is actually in streamer mode. With no effects active every flag is
    /// false on every machine, the poll finds nothing changed, and nothing is ever sent.
    ///
    /// Read by REFLECTION over the whole class rather than naming nineteen fields. Naming them
    /// would be nineteen chances to typo one and a silent gap the day the game adds a twentieth;
    /// this picks up whatever the installed game has. Sorted by name so both ends agree which bit
    /// is which, since GetFields makes no ordering promise, and every machine in a session runs the
    /// same assembly so both arrive at the same list.
    /// </summary>
    public static class StreamerEffectSync
    {
        /// <summary>Fixed ticks between polls. The flags change at human speed, on a vote.</summary>
        private const int TicksBetweenPolls = 30;

        private static int tickCounter;

        /// <summary>The flag fields, in a fixed order. Null until the first successful lookup.</summary>
        private static FieldInfo[] flags;

        /// <summary>True once we have tried to find them, so a game without the class is asked once.</summary>
        private static bool looked;

        /// <summary>The mask we last sent or received, so an unchanged set stays silent.</summary>
        private static int lastKnown;

        /// <summary>True while writing the flags from a peer, so applying does not re-broadcast.</summary>
        private static bool applying;

        /// <summary>How many effect changes this machine has published. Lets a test assert a silent
        /// path actually ran, the same reason CombatSync counts its own.</summary>
        public static int Published { get; private set; }

        /// <summary>Forgets what we knew, for a new session or a load.</summary>
        public static void Reset()
        {
            lastKnown = 0;
            tickCounter = 0;
            Published = 0;
        }

        /// <summary>
        /// The effect flags, looked up once and cached.
        ///
        /// Returns an empty array rather than null if the class is missing, so every caller can
        /// loop without a null check and a game that has dropped streamer effects simply syncs
        /// nothing.
        /// </summary>
        private static FieldInfo[] Flags()
        {
            if (looked) return flags;
            looked = true;

            try
            {
                Type t = typeof(Assets.StreamerEffects);
                FieldInfo[] all = t.GetFields(BindingFlags.Public | BindingFlags.Static);

                List<FieldInfo> bools = new List<FieldInfo>();
                for (int i = 0; i < all.Length; i++)
                    if (all[i].FieldType == typeof(bool)) bools.Add(all[i]);

                bools.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

                // More effects than a mask can carry would silently drop the tail, so say so rather
                // than sync a subset that looks complete.
                if (bools.Count > 32)
                {
                    NetLog.Warn("streamer effects: " + bools.Count +
                                " flags is more than one mask can carry; syncing the first 32");
                    bools.RemoveRange(32, bools.Count - 32);
                }

                flags = bools.ToArray();
                NetLog.Info("streamer effects: tracking " + flags.Length + " flag(s)");
            }
            catch (Exception ex)
            {
                flags = new FieldInfo[0];
                NetLog.Warn("streamer effects: could not be read, they will not sync: " + ex.Message);
            }

            return flags;
        }

        /// <summary>The current flags packed into one bit per effect.</summary>
        private static int CurrentMask()
        {
            FieldInfo[] f = Flags();
            int mask = 0;

            for (int i = 0; i < f.Length; i++)
            {
                try { if ((bool)f[i].GetValue(null)) mask |= (1 << i); }
                catch { }   // one unreadable flag must not cost the rest
            }
            return mask;
        }

        /// <summary>
        /// Called every fixed tick. Broadcasts the effect set when it changes, and nothing at all
        /// when it does not, which is every tick of every session where nobody is streaming.
        /// </summary>
        public static void Tick()
        {
            if (!NetClient.client.IsConnected) return;
            if (applying) return;

            if (++tickCounter < TicksBetweenPolls) return;
            tickCounter = 0;

            try
            {
                if (Flags().Length == 0) return;

                int now = CurrentMask();
                if (now == lastKnown) return;

                lastKnown = now;
                Published++;

                NetLog.Info("streamer effects changed here, telling the session: mask " + now);
                NetRouter.Send(new StreamerEffectsMessage { Effects = now });
            }
            catch (Exception ex) { NetLog.Error("streamer effect poll", ex); }
        }

        /// <summary>
        /// The set as it stands, for telling somebody who has just joined.
        ///
        /// Needed because the poll only speaks when something CHANGES, which is right for a running
        /// session and wrong for an arrival: a player joining after the vote would run with every
        /// effect off while everyone else had them on, and nothing would correct it until the next
        /// vote. Returns zero when there is nothing to say, so the caller can skip the message.
        /// </summary>
        public static int CurrentEffects()
        {
            try { return Flags().Length == 0 ? 0 : CurrentMask(); }
            catch { return 0; }
        }

        /// <summary>
        /// Adopts the effect set another player's audience chose.
        ///
        /// Records the mask as ours BEFORE writing the fields, so the next poll sees no change and
        /// the adoption is not announced straight back out. Without that the two machines would
        /// tell each other about the same vote forever.
        /// </summary>
        public static void Apply(StreamerEffectsMessage m)
        {
            try
            {
                FieldInfo[] f = Flags();
                if (f.Length == 0) return;

                lastKnown = m.Effects;
                applying = true;
                try
                {
                    for (int i = 0; i < f.Length; i++)
                    {
                        try { f[i].SetValue(null, (m.Effects & (1 << i)) != 0); }
                        catch { }   // guarded per flag, for the usual reason
                    }
                }
                finally { applying = false; }

                NetLog.Info("streamer effects adopted from the session: mask " + m.Effects);
            }
            catch (Exception ex) { NetLog.Error("streamer effects", ex); }
        }
    }
}
