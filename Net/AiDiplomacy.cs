// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

using KaCMultiplayer.Lobby;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Diplomacy between human kingdoms and AI kingdoms: alliance, breaking one, war, peace,
    /// tribute and gifts, answered by the AI the way the game's own AI answers, and the AI's
    /// own initiatives toward players (warnings, war, gold demands, gifts, peace offers).
    ///
    /// WHERE THE GAME'S RULES ACTUALLY LIVE. Vanilla has no C# "should I accept" method. Every
    /// exchange with an AI is a dialogue-system conversation, and the accept/refuse conditions are
    /// script lines in the game's dialogue data. What C# owns is the AI's OPINION: per team, on the
    /// AI's own LandmassOwner (standings, 0 VeryUnfavorable .. 4 VeryFavorable, plus points toward
    /// the next level), moved by LandmassOwner.ModifyStandingFor, per team already, so every
    /// human has their own standing with every AI. This file reads and moves it through the game's
    /// own private methods (by reflection, as the mod already calls the game's private StartGame)
    /// and applies the thresholds read out of the dialogue data:
    ///
    ///   alliance      only at standing 4, then +10
    ///   peace         at standing >= 1, or if the AI should surrender (AIKingdom.ShouldSurrender);
    ///                 +50 if agreed, -25 if refused
    ///   tribute       standing >= 2: pays if it can (-55), cannot afford (-30).
    ///                 standing <= 1: pays if you are militarily stronger (-55); otherwise at
    ///                 standing 0 it declares war, above that it refuses (-105)
    ///   gift          points = resources given x 2.5 (x 0.1 after year 125), x 0.25..1 by years
    ///                 since your last gift (full after 7), x 0.7 at war, capped at 100. An
    ///                 Aggressive ruler at war refuses gifts.
    ///   break alliance / declare war from you   -100 (the game's island panel does the same)
    ///
    /// WHO RUNS IT. AI kingdoms exist only on the host. The host's own clicks are decided right
    /// there. A guest's click becomes an AiRequestMessage; the host decides with the SAME code,
    /// using that guest's own standing, and answers with an AiResultMessage. Relations go through
    /// Main.HostPresetRelation, which every machine applies. Resources move on the machine that
    /// really holds them: a guest's gift leaves the guest's stores on the guest's machine before
    /// the request is sent, and tribute an AI pays arrives in the guest's stores on the guest's
    /// machine when the answer does. Guests learn who the AI kingdoms are, and what each thinks of
    /// them, from AiRosterMessage.
    ///
    /// THE AI'S OWN INITIATIVES. Toward the host, the game's own envoys already walk to the host's
    /// keep and run the game's own conversations (World.SetRelations and the standing methods are
    /// patched in Main.cs so what they decide lands on the host's real team). Envoys only ever go
    /// to Player.inst's keep, so toward GUESTS the host runs the same intentions itself, each
    /// season, from the game's own Intention_ManageDiplomacy numbers: nothing until the AI's island
    /// has 75 villagers; warnings after 15/20/30 seasons at the lowest opinion (Aggressive/Playful/
    /// Anxious), war on the second; each ruler's own objective cycle (Aggressive demands gold,
    /// Anxious and Playful send gifts when they like you); peace offers while at war.
    /// </summary>
    public static class AiDiplomacy
    {
        public const int FirstAiTeam = 2;
        public const int LastAiTeam = 4;

        /// <summary>The game's gate for any AI diplomacy (Intention_ManageDiplomacy.Tick).</summary>
        private const int VillagersBeforeDiplomacy = 75;

        /// <summary>Seasons between one AI objective and the next, per guest.</summary>
        private const int SeasonsPerObjective = 4;

        /// <summary>Seasons a guest has to answer a proposal before it counts as ignored.</summary>
        private const int SeasonsToAnswer = 8;

        private static readonly string[] StandingNames =
            { "Very Unfavorable", "Unfavorable", "Neutral", "Favorable", "Very Favorable" };

        // Each ruler's objective cycle, from Intention_ManageDiplomacy: 1 Chat, 2 TradeRoute,
        // 4 MissionHelp, 8 GiveGift, 10 DemandGold. Only 8 and 10 are carried here.
        private static readonly int[] AnxiousObjectives = { 4, 1, 4, 8, 2 };
        private static readonly int[] PlayfulObjectives = { 1, 4, 1, 8, 2 };
        private static readonly int[] AggressiveObjectives = { 1, 10, 4, 10, 2, 10 };

        // ---- reflection into the game's standing methods ---------------------------

        private static bool reflected;
        private static MethodInfo initStanding, getStanding, getPoints, modifyStanding, setStanding, shouldSurrender;

        // ---- host state, per (ai, human) pair ---------------------------------------

        private static readonly Dictionary<long, int> lastGiftYear = new Dictionary<long, int>();     // human -> AI gifts
        private static readonly Dictionary<long, int> lastAiGiftYear = new Dictionary<long, int>();   // AI -> human gifts
        private static readonly Dictionary<long, int> enemySeasons = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> warningBonus = new Dictionary<long, int>();
        private static readonly HashSet<long> warned = new HashSet<long>();
        private static readonly Dictionary<long, int> seasonsSinceObjective = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> objectiveIndex = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> denyPeace = new Dictionary<long, int>();
        private static readonly Dictionary<long, int> ignores = new Dictionary<long, int>();

        private class Proposal
        {
            public int Id, Ai, Human, Amount, SeasonsLeft;
            public AiProposalKind Kind;
        }

        private static readonly Dictionary<int, Proposal> proposals = new Dictionary<int, Proposal>();
        private static int nextProposalId = 1;

        // ---- guest state: the host's last roster ------------------------------------

        private class RemoteAi
        {
            public int Team, Banner;
            public string Name;
            public readonly Dictionary<int, int> Standing = new Dictionary<int, int>();
        }

        private static readonly Dictionary<int, RemoteAi> remote = new Dictionary<int, RemoteAi>();

        private static float rosterTimer;
        private static float lastRosterSendTime = -999f;
        private static string lastRosterSignature;

        /// <summary>What the diplomacy window shows for one AI kingdom, on host or guest alike.</summary>
        public struct AiView
        {
            public int Team;
            public string Name;
            public int Standing;
        }

        // ---- who --------------------------------------------------------------------

        public static bool IsAiTeam(int team)
        {
            if (team < FirstAiTeam || team > LastAiTeam) return false;
            if (KingdomFor(team) != null) return true;
            return !NetRouter.IsServer && remote.ContainsKey(team);
        }

        /// <summary>The AI kingdom with this team, on the host, or null.</summary>
        public static AIKingdom KingdomFor(int team)
        {
            AIBrainsContainer brains = AIBrainsContainer.inst;
            if (brains == null || brains.kingdoms == null) return null;
            foreach (AIKingdom k in brains.kingdoms)
                if (k != null && k.LandmassOwner != null && k.LandmassOwner.teamId == team) return k;
            return null;
        }

        /// <summary>Every AI kingdom this machine is running, in team order.</summary>
        public static List<AIKingdom> Kingdoms()
        {
            var list = new List<AIKingdom>();
            AIBrainsContainer brains = AIBrainsContainer.inst;
            if (brains == null || brains.kingdoms == null) return list;
            foreach (AIKingdom k in brains.kingdoms)
                if (k != null && k.LandmassOwner != null) list.Add(k);
            list.Sort((a, b) => a.LandmassOwner.teamId.CompareTo(b.LandmassOwner.teamId));
            return list;
        }

        /// <summary>The AI kingdoms for the diplomacy window, with their opinion of <paramref name="me"/>.</summary>
        public static List<AiView> ViewsFor(int me)
        {
            var views = new List<AiView>();

            if (NetRouter.IsServer)
            {
                foreach (AIKingdom k in Kingdoms())
                {
                    int team = k.LandmassOwner.teamId;
                    views.Add(new AiView { Team = team, Name = NameFor(team), Standing = Standing(k, me) });
                }
                return views;
            }

            var teams = new List<int>(remote.Keys);
            teams.Sort();
            foreach (int team in teams)
            {
                RemoteAi r = remote[team];
                int s;
                views.Add(new AiView
                {
                    Team = team,
                    Name = r.Name,
                    Standing = r.Standing.TryGetValue(me, out s) ? s : 2
                });
            }
            return views;
        }

        /// <summary>
        /// The AI kingdom's display name: its island's name, which AI placement set to the
        /// lobby's name for it plus " (AI)". On a guest, the name the host sent.
        /// </summary>
        public static string NameFor(int team)
        {
            AIKingdom k = KingdomFor(team);
            if (k != null)
            {
                try
                {
                    LandmassOwner lo = k.LandmassOwner;
                    if (lo.ownedLandMasses != null && lo.ownedLandMasses.Count > 0 && Player.inst != null)
                    {
                        int lm = lo.ownedLandMasses.data[0];
                        if (Player.inst.LandMassNames != null && lm >= 0 && lm < Player.inst.LandMassNames.Count
                            && !string.IsNullOrEmpty(Player.inst.LandMassNames[lm]))
                            return Player.inst.LandMassNames[lm];
                    }
                }
                catch { }
            }

            RemoteAi r;
            if (remote.TryGetValue(team, out r) && !string.IsNullOrEmpty(r.Name)) return r.Name;
            return "AI Kingdom (AI)";
        }

        /// <summary>The AI kingdom's banner, from its own LandmassOwner or the flag the host sent.</summary>
        public static Texture BannerFor(int team)
        {
            AIKingdom k = KingdomFor(team);
            if (k != null && k.LandmassOwner != null && k.LandmassOwner.BannerTexture != null)
                return k.LandmassOwner.BannerTexture;

            RemoteAi r;
            if (!remote.TryGetValue(team, out r)) return null;
            var sets = World.inst == null ? null : World.inst.liverySets;
            if (sets == null || r.Banner < 0 || r.Banner >= sets.Count) return null;
            return sets[r.Banner].banners;
        }

        // ---- opinion ----------------------------------------------------------------

        /// <summary>The AI's standing toward <paramref name="team"/>, 0..4.</summary>
        public static int Standing(AIKingdom k, int team)
        {
            if (k == null || k.LandmassOwner == null) return 2;
            LandmassOwner lo = k.LandmassOwner;
            try
            {
                if (Reflect())
                {
                    initStanding.Invoke(lo, new object[] { team });
                    return (int)(LandmassOwner.Standing)getStanding.Invoke(lo, new object[] { team });
                }
            }
            catch (Exception e) { NetLog.Error("reading an AI standing", e); }

            LandmassOwner.Standing s;
            return lo.standings != null && lo.standings.TryGetValue(team, out s) ? (int)s : 2;
        }

        /// <summary>An AI's opinion level as the words the game uses for it.</summary>
        public static string StandingName(int standing)
        {
            return StandingNames[Mathf.Clamp(standing, 0, StandingNames.Length - 1)];
        }

        /// <summary>Moves an AI's opinion of one kingdom by some points, through the game's own method.</summary>
        private static void Modify(AIKingdom k, int team, int points)
        {
            try
            {
                if (k == null || k.LandmassOwner == null || !Reflect()) return;
                initStanding.Invoke(k.LandmassOwner, new object[] { team });
                modifyStanding.Invoke(k.LandmassOwner, new object[] { team, points });
            }
            catch (Exception e) { NetLog.Error("changing an AI standing", e); }
        }

        /// <summary>Vanilla's SetRelations drops any standing of Neutral or better to Unfavorable on war.</summary>
        internal static void CapStandingOnWar(AIKingdom k, int team)
        {
            try
            {
                if (k == null || setStanding == null || Standing(k, team) <= 1) return;
                setStanding.Invoke(k.LandmassOwner, new object[] { team, LandmassOwner.Standing.Unfavorable });
            }
            catch (Exception e) { NetLog.Error("capping an AI standing", e); }
        }

        /// <summary>Whether the game would have this AI give in to peace now.</summary>
        private static bool ShouldSurrender(AIKingdom k)
        {
            try { return Reflect() && shouldSurrender != null && (bool)shouldSurrender.Invoke(k, null); }
            catch { return false; }
        }

        /// <summary>Finds the game's private opinion methods, once. False if any is missing.</summary>
        private static bool Reflect()
        {
            if (!reflected)
            {
                reflected = true;
                const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                Type lo = typeof(LandmassOwner);
                initStanding = lo.GetMethod("InitStandingIfNecessary", bf, null, new[] { typeof(int) }, null);
                getStanding = lo.GetMethod("GetStandingFor", bf, null, new[] { typeof(int) }, null);
                getPoints = lo.GetMethod("GetPointsStandingFor", bf, null, new[] { typeof(int) }, null);
                modifyStanding = lo.GetMethod("ModifyStandingFor", bf, null, new[] { typeof(int), typeof(int) }, null);
                setStanding = lo.GetMethod("SetStandingFor", bf, null, new[] { typeof(int), typeof(LandmassOwner.Standing) }, null);
                shouldSurrender = typeof(AIKingdom).GetMethod("ShouldSurrender", bf, null, Type.EmptyTypes, null);

                if (initStanding == null || getStanding == null || modifyStanding == null)
                    NetLog.Warn("AI diplomacy: the game's standing methods were not found; opinions will not change");
            }
            return initStanding != null && getStanding != null && modifyStanding != null;
        }

        /// <summary>
        /// The war side effects vanilla's World.SetRelations runs when an AI kingdom goes to war
        /// with "the player": its diplomacy intention's OnDeclaredWar, its current mission cleared,
        /// and its opinion dropped to Unfavorable at best.
        /// </summary>
        internal static void ApplyWarSideEffects(AIKingdom k, int human)
        {
            if (k == null) return;
            try { if (k.manageDiplomacyIntention != null) k.manageDiplomacyIntention.OnDeclaredWar(); } catch { }
            try { k.ClearMission(); } catch { }
            CapStandingOnWar(k, human);
        }

        // ---- a human's actions ------------------------------------------------------

        /// <summary>
        /// A human (this machine's own kingdom, <paramref name="me"/>) asks an AI kingdom for
        /// something. The host decides on the spot; a guest sends the request to the host.
        /// </summary>
        public static void Execute(int me, int aiTeam, AiAction action,
                                   FreeResourceType res = FreeResourceType.Gold, int amount = 0)
        {
            try
            {
                LandmassOwner mine = Player.inst != null ? Player.inst.PlayerLandmassOwner : null;

                // A gift leaves the giver's stores on the giver's own machine, first.
                if (action == AiAction.Gift)
                {
                    int taken = PlayerRelations.TakeFromStores(mine, res, amount);
                    if (taken <= 0)
                    {
                        Tell(NameFor(aiTeam), "You have no " + PlayerRelations.ResourceLabel(res) + " in your public stores to give"
                             + (res == FreeResourceType.Gold ? "." : " (goods still in a woodcutter's or quarry's own yard are not counted until they reach a stockpile)."));
                        return;
                    }
                    amount = taken;
                }

                if (NetRouter.IsServer)
                {
                    Outcome o = Decide(me, aiTeam, action, res, amount, null);
                    foreach (KeyValuePair<FreeResourceType, int> d in o.Deposits)
                        PlayerRelations.DeliverTo(mine, d.Key, d.Value);
                    Tell(o.Title, o.Body);
                    return;
                }

                NetRouter.Send(new AiRequestMessage
                {
                    FromTeam = me,
                    AiTeam = aiTeam,
                    Action = (int)action,
                    Resource = (int)res,
                    Amount = amount
                });
                DealNoticeWindow.ShowSent(NameFor(aiTeam), "Your envoy is on the way to " + NameFor(aiTeam) + "...");
            }
            catch (Exception e) { NetLog.Error("AI diplomacy action " + action, e); }
        }

        /// <summary>
        /// An envoy from this machine's Hall of Diplomacy hands its gift to an AI kingdom. Used on
        /// a guest; the host's own envoys talk to the AI through the game's own conversation. The
        /// goods already left this kingdom when the hall gathered them, so nothing is taken here.
        /// Multiplayer reason: AI kingdoms run on the host only, so the host decides.
        /// </summary>
        public static void GiveEnvoyGift(int me, int aiTeam, Assets.Code.ResourceAmount gift)
        {
            try
            {
                var res = new List<int>();
                var amt = new List<int>();
                foreach (FreeResourceType t in PlayerRelations.Demandable)
                {
                    if (gift.Get(t) <= 0) continue;
                    res.Add((int)t);
                    amt.Add(gift.Get(t));
                }
                if (res.Count == 0) return;

                if (NetRouter.IsServer)
                {
                    var list = new List<KeyValuePair<FreeResourceType, int>>();
                    for (int i = 0; i < res.Count; i++) list.Add(new KeyValuePair<FreeResourceType, int>((FreeResourceType)res[i], amt[i]));
                    Outcome o = Decide(me, aiTeam, AiAction.Gift, FreeResourceType.Gold, 0, list);
                    LandmassOwner mine = Player.inst != null ? Player.inst.PlayerLandmassOwner : null;
                    foreach (KeyValuePair<FreeResourceType, int> d in o.Deposits)
                        PlayerRelations.DeliverTo(mine, d.Key, d.Value);
                    Tell(o.Title, o.Body);
                    return;
                }

                NetRouter.Send(new AiRequestMessage
                {
                    FromTeam = me,
                    AiTeam = aiTeam,
                    Action = (int)AiAction.Gift,
                    GiftResources = res.ToArray(),
                    GiftAmounts = amt.ToArray()
                });
                DealNoticeWindow.ShowSent(NameFor(aiTeam), "Your envoy hands your gift to " + NameFor(aiTeam) + "...");
            }
            catch (Exception e) { NetLog.Error("envoy gift to an AI kingdom", e); }
        }

        /// <summary>A Demand or Offer from the resource picker, aimed at an AI kingdom.</summary>
        public static void HandleDeal(int me, int aiTeam, DealKind kind, int amount, FreeResourceType res)
        {
            if (kind == DealKind.Offer) Execute(me, aiTeam, AiAction.Gift, res, amount);
            else if (kind == DealKind.Demand) Execute(me, aiTeam, AiAction.Demand, res, amount);
        }

        /// <summary>Host: a guest's request. The team is taken from the connection, not the message.</summary>
        public static void HandleRemoteRequest(AiRequestMessage m, ushort sender)
        {
            if (!NetRouter.IsServer) return;
            try
            {
                SessionPlayer sp = NetPlayers.ById(sender);
                if (sp == null || sp.inst == null || sp.inst.PlayerLandmassOwner == null) return;
                int team = sp.inst.PlayerLandmassOwner.teamId;

                AiAction action = (AiAction)m.Action;
                FreeResourceType res = (FreeResourceType)m.Resource;

                var gift = new List<KeyValuePair<FreeResourceType, int>>();
                if (action == AiAction.Gift)
                {
                    if (m.GiftResources.Length == 0) gift.Add(new KeyValuePair<FreeResourceType, int>(res, m.Amount));
                    for (int i = 0; i < m.GiftResources.Length && i < m.GiftAmounts.Length; i++)
                        gift.Add(new KeyValuePair<FreeResourceType, int>((FreeResourceType)m.GiftResources[i], m.GiftAmounts[i]));
                    gift.RemoveAll(g => g.Value <= 0);
                }

                Outcome o;
                if (KingdomFor(m.AiTeam) == null)
                {
                    o = new Outcome { Title = "AI kingdoms", Body = "That AI kingdom no longer exists." };
                    o.Deposits.AddRange(gift);
                }
                else
                {
                    o = Decide(team, m.AiTeam, action, res, m.Amount, gift);
                }

                SendResult(sender, m.AiTeam, o);
            }
            catch (Exception e) { NetLog.Error("handling a guest's AI diplomacy request", e); }
        }

        /// <summary>Guest: the AI's answer, and anything it sends into this kingdom's stores.</summary>
        public static void ApplyResult(AiResultMessage m)
        {
            try
            {
                LandmassOwner mine = Player.inst != null ? Player.inst.PlayerLandmassOwner : null;
                for (int i = 0; i < m.DepositResource.Length && i < m.DepositAmount.Length; i++)
                    PlayerRelations.DeliverTo(mine, (FreeResourceType)m.DepositResource[i], m.DepositAmount[i]);

                Tell(m.Title, m.Body);
            }
            catch (Exception e) { NetLog.Error("applying an AI diplomacy result", e); }
        }

        private class Outcome
        {
            public string Title = "";
            public string Body = "";
            public readonly List<KeyValuePair<FreeResourceType, int>> Deposits = new List<KeyValuePair<FreeResourceType, int>>();
        }

        /// <summary>
        /// The AI's decision, on the host, for human team <paramref name="me"/>. Applies relations
        /// and standing; resources that must reach the human are returned as Deposits for the
        /// human's own machine to put away.
        /// </summary>
        private static Outcome Decide(int me, int aiTeam, AiAction action, FreeResourceType res, int amount,
                                      List<KeyValuePair<FreeResourceType, int>> gift)
        {
            AIKingdom k = KingdomFor(aiTeam);
            string name = NameFor(aiTeam);
            var o = new Outcome { Title = name };
            World.Relations now = PlayerRelations.Get(me, aiTeam);

            switch (action)
            {
                case AiAction.Ally:
                    if (now == World.Relations.Enemy)
                        o.Body = name + " will not ally with a kingdom it is at war with. Make peace first.";
                    else if (Standing(k, me) >= 4)
                    {
                        Main.HostPresetRelation(me, aiTeam, World.Relations.Allies);
                        Modify(k, me, 10);
                        o.Body = name + " gladly accepts your alliance.";
                    }
                    else
                        o.Body = name + " is not ready for an alliance yet. Its opinion of you is "
                                 + StandingName(Standing(k, me)) + "; it needs to be Very Favorable.";
                    break;

                case AiAction.BreakAlliance:
                    Modify(k, me, -100);
                    Main.HostPresetRelation(me, aiTeam, World.Relations.Neutral);
                    o.Body = "You have broken your alliance with " + name + ". It will remember.";
                    break;

                case AiAction.War:
                    Modify(k, me, -100);
                    Main.HostPresetRelation(me, aiTeam, World.Relations.Enemy);
                    ApplyWarSideEffects(k, me);
                    o.Body = "You have declared war on " + name + ".";
                    break;

                case AiAction.Peace:
                    if (Standing(k, me) >= 1 || ShouldSurrender(k))
                    {
                        Main.HostPresetRelation(me, aiTeam, World.Relations.Neutral);
                        Modify(k, me, 50);
                        o.Body = name + " accepts peace.";
                    }
                    else
                    {
                        Modify(k, me, -25);
                        o.Body = name + ": \"Never.\" It refuses to make peace while it thinks so little of you.";
                    }
                    break;

                case AiAction.Gift:
                    if (gift == null || gift.Count == 0)
                        gift = new List<KeyValuePair<FreeResourceType, int>> { new KeyValuePair<FreeResourceType, int>(res, amount) };
                    DecideGift(k, me, aiTeam, name, now, gift, o);
                    break;

                case AiAction.Demand:
                    DecideTribute(k, me, aiTeam, name, res, amount, o);
                    break;
            }

            NetLog.Info("AI diplomacy: team " + me + " -> " + name + " " + action + ": " + o.Body);
            return o;
        }

        /// <summary>
        /// <paramref name="gift"/> has already left the giver's stores. One gift, however many
        /// resources it holds (an envoy's gift from the Hall of Diplomacy can hold several), as
        /// the game scores a gift on its total.
        /// </summary>
        private static void DecideGift(AIKingdom k, int me, int aiTeam, string name, World.Relations now,
                                       List<KeyValuePair<FreeResourceType, int>> gift, Outcome o)
        {
            bool atWar = now == World.Relations.Enemy;

            if (atWar && k.rulerPersonality == AIKingdom.RulerPersonality.Aggressive)
            {
                o.Body = name + " sends your gift back. It wants victory, not presents.";
                o.Deposits.AddRange(gift);
                return;
            }

            int given = 0;
            var parts = new List<string>();
            foreach (KeyValuePair<FreeResourceType, int> g in gift)
            {
                PlayerRelations.DeliverTo(k.LandmassOwner, g.Key, g.Value);
                given += g.Value;
                parts.Add(g.Value + " " + PlayerRelations.ResourceLabel(g.Key));
            }

            int year = Player.inst != null ? Player.inst.CurrYear : 0;
            long key = TeamPair.Key(aiTeam, me);
            int last;
            int sinceLast = lastGiftYear.TryGetValue(key, out last) ? year - last : 999;

            // GiftPts, as the game computes it.
            float points = given * (year < 125 ? 2.5f : 0.1f);
            points *= Mathf.Lerp(0.25f, 1f, Mathf.Clamp01(sinceLast / 7f));
            if (atWar) points *= 0.7f;
            points = Mathf.Min(points, 100f);

            lastGiftYear[key] = year;
            Modify(k, me, (int)points);

            string reaction;
            if (sinceLast >= 5) reaction = points > 50 ? "is delighted with your generous gift" : "thanks you for your gift";
            else if (sinceLast > 1) reaction = points > 75 ? "thanks you for another generous gift" : "accepts your gift politely";
            else reaction = "accepts your gift, though you gave one only recently";

            o.Body = name + " " + reaction + " (" + string.Join(", ", parts.ToArray())
                     + "). Its opinion of you is now " + StandingName(Standing(k, me)) + ".";
        }

        /// <summary>
        /// Whether an AI pays a tribute demand, by the game's own rules: it pays when it likes you or
        /// fears you, and at the lowest opinion it answers a demand with war.
        /// </summary>
        private static void DecideTribute(AIKingdom k, int me, int aiTeam, string name,
                                          FreeResourceType res, int amount, Outcome o)
        {
            int standing = Standing(k, me);

            // Below Neutral it only pays under threat.
            if (standing < 2 && !PlayerMilitaryStronger(me, aiTeam))
            {
                if (standing == 0)
                {
                    Modify(k, me, -100);
                    Main.HostPresetRelation(me, aiTeam, World.Relations.Enemy);
                    ApplyWarSideEffects(k, me);
                    o.Body = name + " answers your demand with war!";
                }
                else
                {
                    Modify(k, me, -105);
                    o.Body = name + " refuses your demand, and thinks less of you for making it.";
                }
                return;
            }

            float share = standing >= 4 ? 0.3f : (standing == 3 ? 0.15f : 0.05f);
            int stock = PlayerRelations.StockOf(k.LandmassOwner, res);
            int most = Mathf.Min(200, Mathf.FloorToInt(stock * share));
            int want = Mathf.Min(Mathf.Max(0, amount), most);

            if (want <= 0)
            {
                Modify(k, me, -30);
                o.Body = name + " cannot spare any " + PlayerRelations.ResourceLabel(res) + ".";
                return;
            }

            int paid = PlayerRelations.TakeFromStores(k.LandmassOwner, res, want);
            Modify(k, me, -55);
            if (paid > 0) o.Deposits.Add(new KeyValuePair<FreeResourceType, int>(res, paid));

            o.Body = name + " pays you " + paid + " " + PlayerRelations.ResourceLabel(res)
                     + (paid < amount ? (" (you asked for " + amount + ")") : "")
                     + ". Its opinion of you is now " + StandingName(Standing(k, me)) + ".";
        }

        /// <summary>
        /// The game's IsPlayerMilitaryStronger, per team: your armies outnumber the AI's by more
        /// than three.
        /// </summary>
        private static bool PlayerMilitaryStronger(int me, int aiTeam)
        {
            int mine = 0, theirs = 0;
            var armies = UnitSystem.inst != null ? UnitSystem.inst.armies : null;
            if (armies != null)
            {
                for (int i = 0; i < armies.Count; i++)
                {
                    UnitSystem.Army a = armies.data[i];
                    if (a == null) continue;
                    if (a.teamId == me) mine++;
                    else if (a.teamId == aiTeam) theirs++;
                }
            }
            return mine - 3 > theirs;
        }

        // ---- roster: host tells every guest who the AI kingdoms are ------------------

        /// <summary>Called every frame from Main. Host only; sends at most every two seconds.</summary>
        public static void Tick()
        {
            // Not from the lobby of a loaded game, whose AI kingdoms already exist here: a guest
            // there has not loaded the world yet. The first roster goes out as the game starts.
            if (!NetRouter.IsServer || !ClockSync.InGame) return;

            rosterTimer += Time.unscaledDeltaTime;
            if (rosterTimer < 2f) return;
            rosterTimer = 0f;

            try
            {
                List<AIKingdom> kingdoms = Kingdoms();
                if (kingdoms.Count == 0 && lastRosterSignature == null) return;

                var humans = HumanTeams(false);

                var teams = new List<int>(); var banners = new List<int>(); var names = new List<string>();
                var islands = new List<int>();
                var sa = new List<int>(); var sh = new List<int>(); var sv = new List<int>();
                var sig = new System.Text.StringBuilder();

                foreach (AIKingdom k in kingdoms)
                {
                    int team = k.LandmassOwner.teamId;
                    string name = NameFor(team);
                    ArrayExt<int> owned = k.LandmassOwner.ownedLandMasses;
                    int island = (owned != null && owned.Count > 0) ? owned.data[0] : -1;
                    teams.Add(team); banners.Add(k.LandmassOwner.bannerIdx); names.Add(name); islands.Add(island);
                    sig.Append(team).Append(':').Append(k.LandmassOwner.bannerIdx).Append(':').Append(name)
                       .Append(':').Append(island);

                    foreach (int h in humans)
                    {
                        int s = Standing(k, h);
                        sa.Add(team); sh.Add(h); sv.Add(s);
                        sig.Append(',').Append(h).Append('=').Append(s);
                    }
                    sig.Append('|');
                }

                string signature = sig.ToString();
                bool due = Time.unscaledTime - lastRosterSendTime > 30f;   // also covers late joiners
                if (signature == lastRosterSignature && !due) return;

                lastRosterSignature = signature;
                lastRosterSendTime = Time.unscaledTime;

                NetRouter.Broadcast(new AiRosterMessage
                {
                    Teams = teams.ToArray(),
                    Banners = banners.ToArray(),
                    Names = names.ToArray(),
                    Landmasses = islands.ToArray(),
                    StandingAi = sa.ToArray(),
                    StandingHuman = sh.ToArray(),
                    StandingValue = sv.ToArray()
                });
            }
            catch (Exception e) { NetLog.Error("sending the AI roster", e); }
        }

        /// <summary>Host: immediately restates AI identities and opinions to one guest.</summary>
        public static void SendRosterTo(ushort clientId)
        {
            if (!NetRouter.IsServer || clientId == 0) return;

            try
            {
                List<AIKingdom> kingdoms = Kingdoms();
                List<int> humans = HumanTeams(false);
                var teams = new List<int>(); var banners = new List<int>(); var names = new List<string>();
                var islands = new List<int>();
                var sa = new List<int>(); var sh = new List<int>(); var sv = new List<int>();

                foreach (AIKingdom k in kingdoms)
                {
                    int team = k.LandmassOwner.teamId;
                    ArrayExt<int> owned = k.LandmassOwner.ownedLandMasses;
                    teams.Add(team);
                    banners.Add(k.LandmassOwner.bannerIdx);
                    names.Add(NameFor(team));
                    islands.Add(owned != null && owned.Count > 0 ? owned.data[0] : -1);

                    foreach (int human in humans)
                    {
                        sa.Add(team);
                        sh.Add(human);
                        sv.Add(Standing(k, human));
                    }
                }

                NetRouter.SendTo(new AiRosterMessage
                {
                    Teams = teams.ToArray(), Banners = banners.ToArray(), Names = names.ToArray(),
                    Landmasses = islands.ToArray(), StandingAi = sa.ToArray(),
                    StandingHuman = sh.ToArray(), StandingValue = sv.ToArray()
                }, clientId);
                NetLog.Info("diplomacy refresh: sent " + kingdoms.Count + " AI kingdom(s) to client " + clientId);
            }
            catch (Exception e) { NetLog.Error("sending an on-demand AI roster", e); }
        }

        /// <summary>Guest: adopt the host's roster.</summary>
        public static void ApplyRoster(AiRosterMessage m)
        {
            if (NetRouter.IsServer) return;   // the host's own copy of its broadcast

            try
            {
                remote.Clear();
                for (int i = 0; i < m.Teams.Length; i++)
                {
                    remote[m.Teams[i]] = new RemoteAi { Team = m.Teams[i], Banner = m.Banners[i], Name = m.Names[i] };

                    // The kingdom itself, so its island is its own here before its buildings arrive.
                    int island = i < m.Landmasses.Length ? m.Landmasses[i] : -1;
                    AiMirror.EnsureOwner(m.Teams[i], m.Banners[i], m.Names[i], island);
                }

                for (int i = 0; i < m.StandingAi.Length; i++)
                {
                    RemoteAi r;
                    if (remote.TryGetValue(m.StandingAi[i], out r)) r.Standing[m.StandingHuman[i]] = m.StandingValue[i];
                }
            }
            catch (Exception e) { NetLog.Error("applying the AI roster", e); }
        }

        /// <summary>Every human kingdom in the session, optionally without the host's own.</summary>
        private static List<int> HumanTeams(bool guestsOnly)
        {
            var teams = new List<int>();
            int hostTeam = (Player.inst != null && Player.inst.PlayerLandmassOwner != null)
                ? Player.inst.PlayerLandmassOwner.teamId : int.MinValue;

            foreach (SessionPlayer p in Main.kCPlayers.Values)
            {
                if (p == null || p.isAI || p.inst == null || p.inst.PlayerLandmassOwner == null) continue;
                int team = p.inst.PlayerLandmassOwner.teamId;
                if (team < PlayerRelations.MpTeamBase) continue;
                if (guestsOnly && (team == hostTeam || p.isGhost)) continue;
                if (!teams.Contains(team)) teams.Add(team);
            }
            return teams;
        }

        // ---- the AI's own initiatives toward guests (host) --------------------------

        /// <summary>Once per in-game season, from Main's season watcher. Host only.</summary>
        public static void OnSeasonChanged()
        {
            if (!NetRouter.IsServer) return;

            try
            {
                ExpireProposals();

                List<int> guests = HumanTeams(true);
                if (guests.Count == 0) return;

                foreach (AIKingdom k in Kingdoms())
                {
                    int ai = k.LandmassOwner.teamId;
                    bool active = VillagersOf(k) >= VillagersBeforeDiplomacy;

                    foreach (int h in guests)
                        SeasonFor(k, ai, h, active);
                }
            }
            catch (Exception e) { NetLog.Error("AI initiatives", e); }
        }

        /// <summary>
        /// Host, each season, for one AI and one guest: counts seasons at the lowest opinion, and runs
        /// the warnings, war, demands and gifts the game's envoys would bring to the host.
        /// </summary>
        private static void SeasonFor(AIKingdom k, int ai, int h, bool active)
        {
            long key = TeamPair.Key(ai, h);
            int standing = Standing(k, h);

            // Vanilla's seasonal counter: seasons spent at the lowest opinion.
            enemySeasons[key] = standing == 0 ? Get(enemySeasons, key) + 1 : 0;

            if (!active || HasOpenProposal(ai, h)) return;

            seasonsSinceObjective[key] = Get(seasonsSinceObjective, key) + 1;
            bool atWar = PlayerRelations.Get(ai, h) == World.Relations.Enemy;
            string name = NameFor(ai);

            if (atWar)
            {
                if (Get(seasonsSinceObjective, key) < SeasonsPerObjective) return;
                seasonsSinceObjective[key] = 0;

                int limit = k.rulerPersonality == AIKingdom.RulerPersonality.Aggressive ? 1 : 2;
                if (ShouldSurrender(k) || Get(denyPeace, key) <= limit)
                    Propose(ai, h, AiProposalKind.PeaceOffer, 0, name + " seeks peace",
                            name + " sends an envoy: it is tired of this war and offers peace. Do you accept?");
                return;
            }

            // Bad blood: a warning, then war on the second time the patience runs out.
            int threshold = k.GetEnemyPointsForWarning() + Get(warningBonus, key);
            if (standing == 0 && Get(enemySeasons, key) >= threshold)
            {
                seasonsSinceObjective[key] = 0;
                enemySeasons[key] = 0;

                if (!warned.Contains(key))
                {
                    warned.Add(key);
                    warningBonus[key] = UnityEngine.Random.Range(10, 21);
                    Notify(h, ai, name + " warns you", name + "'s envoy warns you: its patience with your kingdom is running out.");
                }
                else
                {
                    warned.Remove(key);
                    warningBonus.Remove(key);
                    DeclareWarOn(k, ai, h, name + " has declared war on you!");
                }
                return;
            }

            if (Get(seasonsSinceObjective, key) < SeasonsPerObjective) return;
            seasonsSinceObjective[key] = 0;

            int[] cycle = k.rulerPersonality == AIKingdom.RulerPersonality.Aggressive ? AggressiveObjectives
                        : (k.rulerPersonality == AIKingdom.RulerPersonality.Playful ? PlayfulObjectives : AnxiousObjectives);
            int idx = Get(objectiveIndex, key);
            objectiveIndex[key] = (idx + 1) % cycle.Length;

            switch (cycle[idx % cycle.Length])
            {
                case 10: DemandGoldFrom(k, ai, h, name); break;
                case 8: GiveGiftTo(k, ai, h, name, standing); break;
                default: break;   // chat, missions and trade routes are conversations this does not carry
            }
        }

        /// <summary>The game's GetGoldDemandAmount outside a war: the year, x2 after 50, x3 after 100, in tens.</summary>
        private static void DemandGoldFrom(AIKingdom k, int ai, int h, string name)
        {
            int year = Player.inst != null ? Player.inst.CurrYear : 0;
            int amount = year;
            if (year > 50) amount = year * 2;
            if (year > 100) amount = year * 3;
            amount = amount / 10 * 10;
            if (amount < 10) return;   // the game asks for nothing this early

            Propose(ai, h, AiProposalKind.GoldDemand, amount, name + " demands tribute",
                    name + "'s envoy demands " + amount + " gold. Refusing will anger it.");
        }

        /// <summary>
        /// The game's gift objective (standing Neutral or better, 15 years since its last gift),
        /// sized by GetPotentialGiftAmount: half of what it has spare of each resource, capped at
        /// 50, given at 25-50% (more if it likes you), at least 5 of each.
        /// </summary>
        private static void GiveGiftTo(AIKingdom k, int ai, int h, string name, int standing)
        {
            if (standing < 2) return;

            int year = Player.inst != null ? Player.inst.CurrYear : 0;
            long key = TeamPair.Key(ai, h);
            int last;
            if (lastAiGiftYear.TryGetValue(key, out last) && year - last <= 15) return;

            var outcome = new Outcome { Title = "A gift from " + name };
            var parts = new List<string>();

            Assets.Code.ResourceAmount spare = k.CurrentResources - k.lastFrameRequestedResources;
            for (int i = 0; i < 12; i++)
            {
                FreeResourceType t = (FreeResourceType)i;
                int avail = Mathf.Min(50, (int)(spare.Get(t) * 0.5f));
                if (avail <= 5) continue;

                float share = UnityEngine.Random.Range(0.25f, 0.5f);
                if (standing == 3) share += 0.25f;
                else if (standing == 4) share += 0.5f;
                int want = Mathf.Max(5, (int)(avail * Mathf.Clamp01(share)));

                int taken = PlayerRelations.TakeFromStores(k.LandmassOwner, t, want);
                if (taken <= 0) continue;
                outcome.Deposits.Add(new KeyValuePair<FreeResourceType, int>(t, taken));
                parts.Add(taken + " " + PlayerRelations.ResourceLabel(t));
            }

            if (outcome.Deposits.Count == 0) return;

            lastAiGiftYear[key] = year;
            outcome.Body = name + " sends you a gift: " + string.Join(", ", parts.ToArray()) + ".";
            SendToHuman(h, ai, outcome);
        }

        /// <summary>The AI declares war on a player, everywhere, and tells them.</summary>
        private static void DeclareWarOn(AIKingdom k, int ai, int h, string message)
        {
            Main.HostPresetRelation(ai, h, World.Relations.Enemy);
            ApplyWarSideEffects(k, h);
            Notify(h, ai, NameFor(ai), message);
        }

        /// <summary>How many villagers live on an AI kingdom's islands; the game waits for 75 before diplomacy.</summary>
        private static int VillagersOf(AIKingdom k)
        {
            int n = 0;
            try
            {
                LandmassOwner lo = k.LandmassOwner;
                if (lo.ownedLandMasses == null || World.inst == null) return 0;
                for (int i = 0; i < lo.ownedLandMasses.Count; i++)
                {
                    var list = World.inst.GetVillagersForLandMass(lo.ownedLandMasses.data[i]);
                    if (list != null) n += list.Count;
                }
            }
            catch { }
            return n;
        }

        // ---- proposals ------------------------------------------------------------------

        private static void Propose(int ai, int h, AiProposalKind kind, int amount, string title, string body)
        {
            ushort client;
            if (!ClientOf(h, out client)) return;

            var p = new Proposal { Id = nextProposalId++, Ai = ai, Human = h, Kind = kind, Amount = amount, SeasonsLeft = SeasonsToAnswer };
            proposals[p.Id] = p;

            NetRouter.SendTo(new AiProposalMessage
            {
                ProposalId = p.Id,
                AiTeam = ai,
                Kind = (int)kind,
                Amount = amount,
                Title = title,
                Body = body
            }, client);

            NetLog.Info("AI initiative: " + NameFor(ai) + " -> team " + h + ": " + kind + " " + amount);
        }

        /// <summary>Whether this AI already has a proposal waiting on this player.</summary>
        private static bool HasOpenProposal(int ai, int h)
        {
            foreach (Proposal p in proposals.Values)
                if (p.Ai == ai && p.Human == h) return true;
            return false;
        }

        /// <summary>Host: a guest answered one of the AI's proposals.</summary>
        public static void HandleProposalAnswer(AiProposalAnswerMessage m, ushort sender)
        {
            if (!NetRouter.IsServer) return;

            try
            {
                Proposal p;
                if (!proposals.TryGetValue(m.ProposalId, out p)) return;

                SessionPlayer sp = NetPlayers.ById(sender);
                if (sp == null || sp.inst == null || sp.inst.PlayerLandmassOwner == null
                    || sp.inst.PlayerLandmassOwner.teamId != p.Human) return;   // only the one asked may answer

                proposals.Remove(p.Id);

                AIKingdom k = KingdomFor(p.Ai);
                string name = NameFor(p.Ai);
                long key = TeamPair.Key(p.Ai, p.Human);
                var o = new Outcome { Title = name };

                if (k == null)
                {
                    o.Body = name + " no longer exists.";
                    if (m.Accept && m.Paid > 0) o.Deposits.Add(new KeyValuePair<FreeResourceType, int>(FreeResourceType.Gold, m.Paid));
                    SendToHuman(p.Human, p.Ai, o);
                    return;
                }

                ignores[key] = 0;

                if (p.Kind == AiProposalKind.GoldDemand)
                {
                    if (m.Accept)
                    {
                        PlayerRelations.DeliverTo(k.LandmassOwner, FreeResourceType.Gold, m.Paid);
                        o.Body = name + " accepts your payment of " + m.Paid + " gold.";
                    }
                    else
                    {
                        int before = Standing(k, p.Human);
                        Modify(k, p.Human, -100);
                        if (before == 0)
                        {
                            DeclareWarOn(k, p.Ai, p.Human, name + " answers your refusal with war!");
                            return;
                        }
                        o.Body = name + " is angered by your refusal. Its opinion of you is now " + StandingName(Standing(k, p.Human)) + ".";
                    }
                }
                else if (p.Kind == AiProposalKind.PeaceOffer)
                {
                    if (m.Accept)
                    {
                        Main.HostPresetRelation(p.Ai, p.Human, World.Relations.Neutral);
                        Modify(k, p.Human, 50);
                        o.Body = "You have made peace with " + name + ".";
                    }
                    else
                    {
                        denyPeace[key] = Get(denyPeace, key) + 1;
                        o.Body = "You refused " + name + "'s offer of peace. The war goes on.";
                    }
                }

                SendToHuman(p.Human, p.Ai, o);
            }
            catch (Exception e) { NetLog.Error("handling an answer to an AI proposal", e); }
        }

        /// <summary>
        /// A proposal left unanswered: the game's own penalty for ignoring an envoy. Aggressive:
        /// -40, and war after three ignored while it thinks poorly of you. Others: -25 while it
        /// thinks well of you, and a 50% chance of war after five at the lowest opinion.
        /// </summary>
        private static void ExpireProposals()
        {
            var expired = new List<Proposal>();
            foreach (Proposal p in proposals.Values)
                if (--p.SeasonsLeft <= 0) expired.Add(p);

            foreach (Proposal p in expired)
            {
                proposals.Remove(p.Id);
                AIKingdom k = KingdomFor(p.Ai);
                if (k == null) continue;

                long key = TeamPair.Key(p.Ai, p.Human);
                int count = Get(ignores, key) + 1;
                ignores[key] = count;
                int standing = Standing(k, p.Human);
                string name = NameFor(p.Ai);
                bool atWar = PlayerRelations.Get(p.Ai, p.Human) == World.Relations.Enemy;

                if (k.rulerPersonality == AIKingdom.RulerPersonality.Aggressive)
                {
                    Modify(k, p.Human, -40);
                    if (!atWar && count >= 3 && Standing(k, p.Human) <= 1)
                    {
                        DeclareWarOn(k, p.Ai, p.Human, name + " will not be ignored any longer. It declares war!");
                        continue;
                    }
                }
                else
                {
                    if (standing >= 2) Modify(k, p.Human, -25);
                    if (!atWar && count >= 5 && standing == 0 && UnityEngine.Random.value < 0.5f)
                    {
                        DeclareWarOn(k, p.Ai, p.Human, name + " has run out of patience with your silence. It declares war!");
                        continue;
                    }
                }

                Notify(p.Human, p.Ai, name, name + "'s envoy went home unanswered. It thinks less of you for it.");
            }
        }

        // ---- plumbing -----------------------------------------------------------------

        private static void Notify(int human, int ai, string title, string body)
        {
            SendToHuman(human, ai, new Outcome { Title = title, Body = body });
        }

        /// <summary>Sends an outcome to the machine that plays this team, if anyone does.</summary>
        private static void SendToHuman(int human, int ai, Outcome o)
        {
            ushort client;
            if (!ClientOf(human, out client)) return;
            SendResult(client, ai, o);
        }

        /// <summary>Sends an outcome to one guest, with anything to go into that guest's stores.</summary>
        private static void SendResult(ushort client, int ai, Outcome o)
        {
            var res = new List<int>();
            var amt = new List<int>();
            foreach (KeyValuePair<FreeResourceType, int> d in o.Deposits)
            {
                res.Add((int)d.Key);
                amt.Add(d.Value);
            }

            NetRouter.SendTo(new AiResultMessage
            {
                AiTeam = ai,
                Title = o.Title,
                Body = o.Body,
                DepositResource = res.ToArray(),
                DepositAmount = amt.ToArray()
            }, client);
        }

        /// <summary>The connection that plays this team, if they are here.</summary>
        private static bool ClientOf(int team, out ushort client)
        {
            client = 0;
            SessionPlayer sp = NetPlayers.ByTeam(team);
            if (sp == null || sp.isGhost) return false;
            client = sp.id;
            return client != 0;
        }

        /// <summary>A counter's value, 0 if it has none yet.</summary>
        private static int Get(Dictionary<long, int> d, long key)
        {
            int v;
            return d.TryGetValue(key, out v) ? v : 0;
        }

        /// <summary>Tells this machine's player an AI's answer, in a popup and the log.</summary>
        private static void Tell(string title, string body)
        {
            NetLog.Info("AI diplomacy: " + body);
            DealNoticeWindow.ShowResolved(title, body);
        }

        /// <summary>For a session ending.</summary>
        public static void Reset()
        {
            lastGiftYear.Clear();
            lastAiGiftYear.Clear();
            enemySeasons.Clear();
            warningBonus.Clear();
            warned.Clear();
            seasonsSinceObjective.Clear();
            objectiveIndex.Clear();
            denyPeace.Clear();
            ignores.Clear();
            proposals.Clear();
            remote.Clear();
            lastRosterSignature = null;
            lastRosterSendTime = -999f;
            AiMirror.Reset();
        }
    }
}
