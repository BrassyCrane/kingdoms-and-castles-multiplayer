// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Harmony;
using UnityEngine;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Witch huts in multiplayer.
    ///
    /// Every machine has the same huts: world generation places them from the shared seed, a save
    /// restores them from its terrain, and a late joiner is sent the host's list. Each machine's
    /// copy of a hut holds that machine's own dealings with the witch (relationship, missions,
    /// cooldowns), because every witch window acts for the local player only. So each player has
    /// their own witch.
    ///
    /// What her spells and curses reach is the host's lobby choice, LobbySettings.WitchShared:
    ///   off (default): only the kingdom of the player who dealt with her. Her three effects that
    ///     are world-wide switches (tree yield, slow villagers, lightning) are kept off the
    ///     streamer-effect sync so they stay on that player's machine.
    ///   on: every spell or curse banner is sent to everyone, and each machine runs the same
    ///     effect on its own kingdom.
    /// </summary>
    public static class WitchSync
    {
        /// <summary>The witch effects that work by flipping a global Assets.StreamerEffects flag.</summary>
        private static readonly HashSet<string> WitchFlags = new HashSet<string>
        {
            "Flag_TreeDoubleYield", "Flag_SlowVillagers", "Flag_LightningStorm"
        };

        /// <summary>True while showing a banner a peer sent, so it is not sent back out.</summary>
        private static bool applying;

        /// <summary>True when witch spells reach every kingdom in this session.</summary>
        public static bool Shared
        {
            get { return Main.InMultiplayer && LobbySettings.Current.WitchShared; }
        }

        /// <summary>
        /// Whether StreamerEffectSync must leave this flag alone. True for the witch's own flags
        /// when her spells are kept to one kingdom, so one player's spell or curse does not reach
        /// the others through the streamer sync.
        /// ponytail: a real Twitch vote for these three effects is also kept local in that mode;
        /// split by source if streamers in multiplayer ever ask for it.
        /// </summary>
        public static bool KeepFlagLocal(string flagName)
        {
            return !LobbySettings.Current.WitchShared && WitchFlags.Contains(flagName);
        }

        /// <summary>
        /// Whose gold pays for a witch spell. Vanilla charges the kingdom owning the hut's island,
        /// which in multiplayer can be a different player (or nobody, and a crash). The player
        /// casting the spell is always the local one, so they pay.
        /// </summary>
        public static LandmassOwner SpellPayer(int landMass)
        {
            if (Main.InMultiplayer && Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                return Player.inst.PlayerLandmassOwner;
            return World.GetLandmassOwner(landMass);
        }

        /// <summary>
        /// Called before a banner is shown. If it is the witch's and her spells are shared, tells
        /// every other player to show and run the same effect.
        /// </summary>
        public static void OnBanner(Votable votable, string voterName, string titleTerm)
        {
            try
            {
                if (applying || !Shared || votable == null || votable.Implementation == null) return;
                if (voterName != I2.Loc.ScriptLocalization.TheWitch) return;

                Color c = votable.color;
                NetRouter.Send(new WitchEffectMessage
                {
                    Effect = votable.Implementation.FullName,
                    TitleTerm = titleTerm,
                    R = c.r, G = c.g, B = c.b, A = c.a
                });
                NetLog.Info("witch effect sent to the session: " + votable.Implementation.Name);
            }
            catch (Exception ex) { NetLog.Error("witch effect send", ex); }
        }

        /// <summary>
        /// Shows and runs a witch effect another player's witch started. Only effect classes the
        /// game itself defines are accepted, since the name came off the network.
        /// </summary>
        public static void Apply(WitchEffectMessage m)
        {
            try
            {
                if (!Shared || EffectBanner.inst == null) return;

                Type t = typeof(WitchHut).Assembly.GetType(m.Effect ?? string.Empty);
                if (t == null || !typeof(Assets.StreamerEffect).IsAssignableFrom(t))
                {
                    NetLog.Warn("witch effect: unknown effect '" + m.Effect + "', ignored");
                    return;
                }

                var v = new Votable { color = new Color(m.R, m.G, m.B, m.A) };
                v.Implementation = t;

                Sprite icon = null;
                try { icon = GameUI.inst.witchUI.witchSpr; } catch { }

                applying = true;
                try { EffectBanner.inst.ShowBannerGeneric(v, I2.Loc.ScriptLocalization.TheWitch, icon, m.TitleTerm); }
                finally { applying = false; }

                NetLog.Info("witch effect from the session: " + t.Name);
            }
            catch (Exception ex) { NetLog.Error("witch effect apply", ex); }
        }

        /// <summary>Sends witch spell and curse banners on, when shared. See WitchSync.OnBanner.</summary>
        [HarmonyPatch(typeof(EffectBanner), "ShowBannerGeneric")]
        public class WitchBannerHook
        {
            /// <summary>Only reads its arguments; the banner always shows.</summary>
            public static void Prefix(Votable votable, string voterName, string bannerTitleTerm)
            {
                OnBanner(votable, voterName, bannerTitleTerm);
            }
        }

        /// <summary>
        /// Makes the caster pay for a spell: rewrites WitchHut.TryActivate's two
        /// World.GetLandmassOwner calls into SpellPayer. A transpiler because both the gold check
        /// and the deduction read the owner inline, with no seam a Prefix could use.
        /// </summary>
        [HarmonyPatch(typeof(WitchHut), "TryActivate")]
        public class WitchSpellPayerHook
        {
            /// <summary>Calls rewritten, for the log.</summary>
            public static int Rewritten;

            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo vanilla = typeof(World).GetMethod("GetLandmassOwner", new[] { typeof(int) });
                MethodInfo ours = typeof(WitchSync).GetMethod("SpellPayer");
                foreach (CodeInstruction c in instructions)
                {
                    if ((c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt) && Equals(c.operand, vanilla))
                    {
                        c.opcode = OpCodes.Call;
                        c.operand = ours;
                        Rewritten++;
                    }
                    yield return c;
                }
            }
        }

        /// <summary>
        /// Puts the hut back after a curse. Vanilla destroys the hut once she has cursed you, but
        /// that runs on the cursed player's machine only, which would leave the hut gone for them
        /// and standing for everyone else. Re-placing it keeps the map the same everywhere; the
        /// cursed player's dealings with her start over.
        /// </summary>
        [HarmonyPatch(typeof(WitchHut), "Curse")]
        public class WitchCurseKeepsHutHook
        {
            /// <summary>Remembers the hut's cell before Curse clears it.</summary>
            public static void Prefix(WitchHut __instance, out Cell __state)
            {
                __state = null;
                try { if (Main.InMultiplayer) __state = World.inst.GetCellData(__instance.transform.position); }
                catch { }
            }

            /// <summary>Places a fresh hut on that cell. The old one is removed at the end of the frame.</summary>
            public static void Postfix(Cell __state)
            {
                if (__state == null) return;
                try
                {
                    World.inst.AddWitchHut(__state.x, __state.z);
                    NetLog.Info("witch curse: hut kept at " + __state.x + "," + __state.z);
                }
                catch (Exception ex) { NetLog.Error("witch curse hut", ex); }
            }
        }
    }
}
