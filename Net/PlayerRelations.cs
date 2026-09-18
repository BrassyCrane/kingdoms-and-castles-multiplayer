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
            RevealAllyLandsOnce(teamA, teamB, r);
        }

        /// <summary>Alliances whose map has already been revealed, so the reveal happens once.</summary>
        private static readonly HashSet<long> revealedFor = new HashSet<long>();

        /// <summary>
        /// Lifts the fog over the map when the LOCAL player enters an alliance.
        ///
        /// Until now an alliance changed exactly two things, gates and docks, so two allied players
        /// still could not see each other's island. That is not much of an alliance.
        ///
        /// ONE-TIME, and that is the whole design. Shared vision used to be rebuilt every tick and
        /// was switched off for it: forcing visibility on the local player's OWN landmasses fights
        /// the game's per-frame fog management, `changed` never settles, and RefreshVisibility plus
        /// UpdateShaders run forever, which is the year-16 freeze recorded in Main's FixedUpdate
        /// note. RevealAllForSharedVision was written for this case and touches only landmasses the
        /// local player does not own, which have no local units to re-fog them, so one call
        /// converges and then costs nothing. Main's note says as much: "stays for a possible
        /// one-time reveal, which would cost nothing per tick".
        ///
        /// NOT UNDONE when an alliance breaks. There is no un-seeing a map, and re-fogging ground
        /// the player has already studied would be a bigger change than this is: the fog arrays are
        /// global rather than per-relationship, so "hide it again" means deciding what every OTHER
        /// standing should now see. An ex-ally knowing the shape of your coastline is the cheaper
        /// and more believable answer.
        /// </summary>
        private static void RevealAllyLandsOnce(int teamA, int teamB, World.Relations r)
        {
            try
            {
                if (r != World.Relations.Allies) return;

                // Only when WE are in this alliance. Two other players allying with each other
                // tells us nothing and must not lift our fog.
                if (Player.inst == null || Player.inst.PlayerLandmassOwner == null) return;
                int localTeam = Player.inst.PlayerLandmassOwner.teamId;
                if (teamA != localTeam && teamB != localTeam) return;

                if (!revealedFor.Add(Key(teamA, teamB))) return;   // already lifted for this pair

                NetLog.Info("relations: allied with team " +
                            (teamA == localTeam ? teamB : teamA) + ", lifting the fog");

                Main.RevealAllForSharedVision();
            }
            catch (Exception ex) { NetLog.Error("revealing an ally's land", ex); }
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

        // ---- CONSENT AND NOTICE -----------------------------------------------------------
        //
        // Two rules sit on top of the relation table, and they exist because the two directions of
        // diplomacy are not symmetrical:
        //
        //   An ALLIANCE binds both kingdoms, so it needs both to agree. Clicking Allies is an
        //   OFFER; the alliance forms when the other player clicks Allies too.
        //
        //   A WAR is one kingdom's decision and nobody should need permission to attack. But
        //   arriving with no warning gives the defender nothing to do about it, so a declaration
        //   now takes effect after a delay, and both sides are told the moment it is made.
        //
        // Leaving an alliance, back to Neutral, stays instant and one-sided. You do not need
        // consent to stop being someone's ally, and a notice period on leaving would mean being
        // held in an alliance you have already withdrawn from.

        /// <summary>
        /// How many seasons pass between declaring war and the war starting.
        ///
        /// Seasons, because the game has no smaller unit: Kingdoms and Castles counts Summer,
        /// Winter and years, and there is no day anywhere in it. Two seasons is one full year,
        /// long enough to raise troops and build walls, short enough to still feel like a threat.
        /// Drop to 1 for half a year.
        /// </summary>
        public const int WarNoticeSeasons = 2;

        /// <summary>Distinct id per announcement: KingdomLog suppresses repeats of the same id.</summary>
        private static int noticeSeq;

        /// <summary>Outstanding alliance offers, keyed by pair, holding the team that offered.</summary>
        private static readonly Dictionary<long, int> allianceOffers = new Dictionary<long, int>();

        /// <summary>Declared wars not yet begun, keyed by pair, holding seasons still to run.</summary>
        private static readonly Dictionary<long, int> pendingWars = new Dictionary<long, int>();

        /// <summary>Seasons remaining before this pair goes to war, or 0 if no war is pending.</summary>
        public static int WarCountdown(int teamA, int teamB)
        {
            int seasons;
            return pendingWars.TryGetValue(Key(teamA, teamB), out seasons) ? seasons : 0;
        }

        /// <summary>The team that has offered this pair an alliance, or 0 if nobody has.</summary>
        public static int AllianceOfferFrom(int teamA, int teamB)
        {
            int from;
            return allianceOffers.TryGetValue(Key(teamA, teamB), out from) ? from : 0;
        }

        /// <summary>
        /// Handles one player asking for a change, applying consent and notice.
        ///
        /// Returns the relation to apply RIGHT NOW, or null when the request has been recorded but
        /// nothing changes yet, which is the normal case for a first alliance offer and for every
        /// declaration of war.
        /// </summary>
        public static World.Relations? RequestFrom(int fromTeam, int toTeam, World.Relations wanted)
        {
            if (!IsPlayerPair(fromTeam, toTeam)) return null;
            long key = Key(fromTeam, toTeam);

            if (wanted == World.Relations.Allies)
            {
                if (Get(fromTeam, toTeam) == World.Relations.Allies) return null;   // already allied

                int offeredBy;
                if (allianceOffers.TryGetValue(key, out offeredBy) && offeredBy != fromTeam)
                {
                    // They offered first and we have now agreed. That is consent.
                    allianceOffers.Remove(key);
                    pendingWars.Remove(key);   // making peace calls off an approaching war
                    NetLog.Info("relations: team " + fromTeam + " accepted an alliance with team " + toTeam);
                    return World.Relations.Allies;
                }

                allianceOffers[key] = fromTeam;
                NetLog.Info("relations: team " + fromTeam + " offered team " + toTeam + " an alliance, waiting on them");
                Announce(fromTeam, toTeam, "has offered an alliance to", "click Allies to accept");
                return null;
            }

            if (wanted == World.Relations.Enemy)
            {
                if (Get(fromTeam, toTeam) == World.Relations.Enemy) return null;   // already fighting
                if (pendingWars.ContainsKey(key)) return null;                     // already declared

                allianceOffers.Remove(key);   // an offer is plainly withdrawn
                pendingWars[key] = WarNoticeSeasons;

                NetLog.Info("relations: team " + fromTeam + " declared war on team " + toTeam +
                            ", beginning in " + WarNoticeSeasons + " season(s)");
                Announce(fromTeam, toTeam, "has DECLARED WAR on",
                         "fighting begins in " + WarNoticeSeasons + " season(s)");
                return null;
            }

            // Neutral: leaving an alliance, or calling off a war that has not started.
            allianceOffers.Remove(key);
            pendingWars.Remove(key);
            return World.Relations.Neutral;
        }

        /// <summary>
        /// Counts every pending war down by one season, starting any that have run out.
        ///
        /// Driven by the season changing rather than by a wall-clock timer, so it advances at
        /// whatever speed the game is running and stops dead when the game is paused, which is what
        /// a player watching the seasons go by would expect.
        /// </summary>
        public static void OnSeasonChanged()
        {
            if (pendingWars.Count == 0) return;

            List<long> starting = null;
            List<long> keys = new List<long>(pendingWars.Keys);

            foreach (long key in keys)
            {
                int left = pendingWars[key] - 1;
                if (left > 0) { pendingWars[key] = left; continue; }

                pendingWars.Remove(key);
                if (starting == null) starting = new List<long>();
                starting.Add(key);
            }

            if (starting == null) return;

            foreach (long key in starting)
            {
                int a = TeamPair.Low(key), b = TeamPair.High(key);
                NetLog.Info("relations: the declared war between team " + a + " and team " + b + " begins now");
                Announce(a, b, "is now AT WAR with", "the notice period has run out");
                Set(a, b, World.Relations.Enemy);
            }
        }

        /// <summary>Tells the local player about a change involving them, by name where possible.</summary>
        private static void Announce(int actingTeam, int otherTeam, string what, string detail)
        {
            try
            {
                if (Player.inst == null || Player.inst.PlayerLandmassOwner == null) return;
                int localTeam = Player.inst.PlayerLandmassOwner.teamId;
                if (localTeam != actingTeam && localTeam != otherTeam) return;   // not our business

                string actor = NameOf(actingTeam);
                string target = (localTeam == otherTeam) ? "you" : NameOf(otherTeam);

                string line = actor + " " + what + " " + target + " (" + detail + ")";
                NetLog.Info("notice: " + line);

                // Same surface RenderChatNotice uses: the game's own event feed once play has
                // begun, the lobby chat list before it. A diplomacy change that nobody sees is
                // worse than no diplomacy, so this deliberately reuses the path already proven
                // to put text where a player is looking.
                if (GameState.inst != null && GameState.inst.IsPlayMode())
                    KingdomLog.TryLog("mpDiplo" + (noticeSeq++), line, KingdomLog.LogStatus.Important, 0f);
                else
                    KaCMultiplayer.Lobby.LobbyView.AddChatNotice(line);
            }
            catch (Exception ex) { NetLog.Error("announcing a diplomacy change", ex); }
        }

        private static string NameOf(int team)
        {
            SessionPlayer p = NetPlayers.ByTeam(team);
            return (p != null && !string.IsNullOrEmpty(p.name)) ? p.name : ("team " + team);
        }

        // ---- TRIBUTE AND RANSOM -----------------------------------------------------------
        //
        // A war that can only end when one side is destroyed is not much of a war. These let it be
        // bought off from either end, which is what most wars actually end in:
        //
        //   /demand 500   the attacker names a price for stopping
        //   /offer  500   the defender offers one
        //   /accept       the other side agrees: the gold moves and the war ends at once
        //   /refuse       they do not
        //
        // Demand and offer are the SAME transaction seen from opposite ends, so they share one
        // message and differ only in who pays. Outside a war the same commands are a plain tribute
        // demand, which is a threat precisely because war is one click away.

        /// <summary>The deal currently on the table for a pair, or absent if there is none.</summary>
        private struct Deal
        {
            public int Proposer;    // who sent it
            public int Payer;       // who would pay if it is accepted
            public int Amount;
            public FreeResourceType Resource;
        }

        /// <summary>
        /// Everything a kingdom can be asked for.
        ///
        /// Taken from the live <c>FreeResourceType</c> rather than written out, so a game update
        /// that adds a resource offers it here without anyone remembering to. Two are excluded on
        /// purpose: <c>DeadVillager</c> is not a commodity, and the enum's own bookkeeping values
        /// are not resources at all.
        /// </summary>
        public static readonly FreeResourceType[] Demandable = new FreeResourceType[]
        {
            FreeResourceType.Gold,
            FreeResourceType.Wheat,
            FreeResourceType.Tree,
            FreeResourceType.Stone,
            FreeResourceType.Charcoal,
            FreeResourceType.IronOre,
            FreeResourceType.Tools,
            FreeResourceType.Armament,
            FreeResourceType.Fish,
            FreeResourceType.Apples,
            FreeResourceType.Pork,
        };

        /// <summary>A resource's name as a player would say it. "Tree" is wood to everyone but the enum.</summary>
        public static string ResourceLabel(FreeResourceType t)
        {
            switch (t)
            {
                case FreeResourceType.Tree: return "Wood";
                case FreeResourceType.IronOre: return "Iron";
                case FreeResourceType.Armament: return "Weapons";
                case FreeResourceType.Pork: return "Pork";
                case FreeResourceType.Wheat: return "Wheat";
                default: return t.ToString();
            }
        }

        /// <summary>Turns a typed word into a resource. Returns false for anything unrecognised.</summary>
        public static bool TryParseResource(string word, out FreeResourceType type)
        {
            type = FreeResourceType.Gold;
            if (string.IsNullOrEmpty(word)) return false;

            string w = word.Trim().ToLowerInvariant();
            if (w == "wood" || w == "timber" || w == "tree") { type = FreeResourceType.Tree; return true; }
            if (w == "iron" || w == "ore" || w == "ironore") { type = FreeResourceType.IronOre; return true; }
            if (w == "weapons" || w == "armament" || w == "armaments") { type = FreeResourceType.Armament; return true; }
            if (w == "meat") { type = FreeResourceType.Pork; return true; }
            if (w == "bread" || w == "grain") { type = FreeResourceType.Wheat; return true; }

            for (int i = 0; i < Demandable.Length; i++)
                if (Demandable[i].ToString().ToLowerInvariant() == w) { type = Demandable[i]; return true; }

            return false;
        }

        private static readonly Dictionary<long, Deal> openDeals = new Dictionary<long, Deal>();

        /// <summary>Applies a proposal or an answer. Runs identically on every machine.</summary>
        public static void ApplyDeal(Messages.DiplomacyDealMessage m)
        {
            if (m == null || !IsPlayerPair(m.FromTeam, m.ToTeam)) return;
            long key = Key(m.FromTeam, m.ToTeam);

            try
            {
                switch ((Messages.DealKind)m.Kind)
                {
                    case Messages.DealKind.Demand:
                    case Messages.DealKind.Offer:
                    {
                        bool demand = (Messages.DealKind)m.Kind == Messages.DealKind.Demand;
                        int payer = demand ? m.ToTeam : m.FromTeam;
                        int amount = System.Math.Max(0, m.Amount);

                        FreeResourceType res;
                        if (!ValidResource(m.Resource, out res))
                        {
                            NetLog.Warn("deal: unknown resource " + m.Resource + ", ignored");
                            return;
                        }

                        openDeals[key] = new Deal
                        {
                            Proposer = m.FromTeam, Payer = payer, Amount = amount, Resource = res
                        };

                        string what = amount + " " + ResourceLabel(res);
                        Announce(m.FromTeam, m.ToTeam,
                                 demand ? ("demands " + what + " from") : ("offers " + what + " to"),
                                 AtWar(m.FromTeam, m.ToTeam)
                                    ? "type /accept to end the war, /refuse to fight on"
                                    : "type /accept to pay, /refuse to decline");
                        return;
                    }

                    case Messages.DealKind.Refuse:
                    {
                        if (!openDeals.ContainsKey(key)) return;
                        openDeals.Remove(key);
                        Announce(m.FromTeam, m.ToTeam, "refused the deal from", "nothing changes");
                        return;
                    }

                    case Messages.DealKind.Accept:
                    {
                        Deal deal;
                        if (!openDeals.TryGetValue(key, out deal)) return;

                        // Only the OTHER side can accept. Otherwise a proposer could accept their
                        // own demand and simply take the money.
                        if (m.FromTeam == deal.Proposer)
                        {
                            NetLog.Warn("deal: team " + m.FromTeam + " tried to accept its own proposal, ignored");
                            return;
                        }

                        openDeals.Remove(key);

                        int paid = MoveResource(deal.Payer, Other(deal.Payer, key), deal.Resource, deal.Amount);

                        // Peace is immediate, and that is the point: a ransom paid after the next
                        // season would buy nothing worth having.
                        bool wasFighting = AtWar(m.FromTeam, m.ToTeam) || pendingWars.ContainsKey(key);
                        pendingWars.Remove(key);
                        if (wasFighting) Set(m.FromTeam, m.ToTeam, World.Relations.Neutral);

                        Announce(m.FromTeam, m.ToTeam, "accepted the deal with",
                                 paid + " " + ResourceLabel(deal.Resource) + " paid"
                                 + (wasFighting ? ", the war is over" : ""));
                        return;
                    }
                }
            }
            catch (Exception ex) { NetLog.Error("applying a diplomacy deal", ex); }
        }

        private static bool AtWar(int a, int b) { return Get(a, b) == World.Relations.Enemy; }

        private static int Other(int team, long key)
        {
            int lo = TeamPair.Low(key), hi = TeamPair.High(key);
            return team == lo ? hi : lo;
        }

        /// <summary>
        /// Moves gold between two kingdoms, and returns what actually moved.
        ///
        /// Clamped to what the payer holds rather than refused outright, matching how the mod
        /// already settles a merchant delivery somebody cannot fully afford. A ransom nobody can
        /// quite pay should still end the war for everything they have, not fail and leave both
        /// sides confused about whether the deal went through.
        /// </summary>
        /// <summary>True when an int off the wire names a resource we are willing to move.</summary>
        private static bool ValidResource(int raw, out FreeResourceType type)
        {
            type = FreeResourceType.Gold;
            for (int i = 0; i < Demandable.Length; i++)
                if ((int)Demandable[i] == raw) { type = Demandable[i]; return true; }
            return false;
        }

        /// <summary>
        /// Moves one resource between two kingdoms, and returns how much actually moved.
        ///
        /// Gold is a special case and deliberately kept one: it lives as a plain int on
        /// LandmassOwner rather than in any building, so it needs none of the storage walking
        /// below and cannot be limited by warehouse space.
        ///
        /// Everything else lives in BUILDINGS, not in a kingdom-wide pool, so a transfer means
        /// taking from the payer's stores and putting it into the payee's. Both halves are
        /// clamped: you cannot take what is not there, and you cannot deposit into a kingdom with
        /// nowhere to put it. Whatever could not be delivered is returned to the payer rather than
        /// vanishing, because a tribute that quietly destroys goods is worse than one that fails.
        /// </summary>
        private static int MoveResource(int payerTeam, int payeeTeam, FreeResourceType type, int amount)
        {
            if (amount <= 0) return 0;

            LandmassOwner payer = World.GetLandmassOwnerByTeamId(payerTeam);
            LandmassOwner payee = World.GetLandmassOwnerByTeamId(payeeTeam);
            if (payer == null || payee == null) return 0;

            if (type == FreeResourceType.Gold)
            {
                int paid = System.Math.Min(amount, System.Math.Max(0, payer.Gold));
                if (paid <= 0) return 0;

                payer.Gold -= paid;
                payee.Gold += paid;

                NetLog.Info("deal: team " + payerTeam + " paid team " + payeeTeam + " " + paid + " gold"
                            + (paid < amount ? (" (asked " + amount + ", that is all they had)") : ""));
                return paid;
            }

            int taken = TakeFrom(payer, type, amount);
            if (taken <= 0)
            {
                NetLog.Info("deal: team " + payerTeam + " has no " + ResourceLabel(type) + " to give");
                return 0;
            }

            int given = GiveTo(payee, type, taken);
            if (given < taken)
            {
                // No room at the other end. Put the remainder back where it came from rather than
                // destroying it.
                int returned = GiveTo(payer, type, taken - given);
                NetLog.Warn("deal: team " + payeeTeam + " had room for only " + given + " "
                            + ResourceLabel(type) + "; " + returned + " returned to team " + payerTeam);
            }

            NetLog.Info("deal: team " + payerTeam + " paid team " + payeeTeam + " " + given + " "
                        + ResourceLabel(type)
                        + (given < amount ? (" (asked " + amount + ")") : ""));
            return given;
        }

        /// <summary>Removes up to <paramref name="want"/> of a resource from a kingdom's stores.</summary>
        private static int TakeFrom(LandmassOwner owner, FreeResourceType type, int want)
        {
            int taken = 0;

            foreach (int lm in Landmasses(owner))
            {
                var stores = FreeResourceManager.inst.GetResourceStorageListFor(type, lm);
                if (stores == null) continue;

                for (int i = 0; i < stores.Count && taken < want; i++)
                {
                    var store = stores.data[i];
                    if (store == null || store.IsPrivate()) continue;

                    int here = store.StoredPublicResources().Get(type);
                    if (here <= 0) continue;

                    int grab = System.Math.Min(here, want - taken);
                    Assets.Code.ResourceAmount amt = new Assets.Code.ResourceAmount();
                    amt.Set(type, grab);

                    store.RemoveResources(amt);
                    taken += grab;
                }

                if (taken >= want) break;
            }

            return taken;
        }

        /// <summary>Deposits up to <paramref name="have"/> of a resource into a kingdom's stores.</summary>
        private static int GiveTo(LandmassOwner owner, FreeResourceType type, int have)
        {
            int left = have;

            foreach (int lm in Landmasses(owner))
            {
                var stores = FreeResourceManager.inst.GetResourceStorageListFor(type, lm);
                if (stores == null) continue;

                for (int i = 0; i < stores.Count && left > 0; i++)
                {
                    var store = stores.data[i];
                    if (store == null || store.IsPrivate() || !store.AcceptsType(type)) continue;

                    int room = store.SpaceAvailable(type);
                    if (room <= 0) continue;

                    int put = System.Math.Min(room, left);
                    Assets.Code.ResourceAmount amt = new Assets.Code.ResourceAmount();
                    amt.Set(type, put);

                    // Deposit takes the amount by reference and writes back what it could NOT
                    // take, so the leftover is read from the struct rather than assumed to be nil.
                    store.Deposit(ref amt);
                    left -= (put - amt.Get(type));
                }

                if (left <= 0) break;
            }

            return have - left;
        }

        /// <summary>The landmasses a kingdom owns, or nothing if it owns none.</summary>
        private static IEnumerable<int> Landmasses(LandmassOwner owner)
        {
            if (owner == null || owner.ownedLandMasses == null) yield break;
            for (int i = 0; i < owner.ownedLandMasses.Count; i++)
                yield return owner.ownedLandMasses.data[i];
        }

        /// <summary>
        /// Handles a typed diplomacy command. Returns true when the text was a command, so the
        /// chat box knows not to also broadcast it as a message.
        /// </summary>
        public static bool HandleChatCommand(string text)
        {
            try
            {
                string[] parts = text.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return false;

                string cmd = parts[0].ToLowerInvariant();
                if (cmd != "/demand" && cmd != "/offer" && cmd != "/accept"
                    && cmd != "/refuse" && cmd != "/diplo") return false;

                if (Player.inst == null || Player.inst.PlayerLandmassOwner == null) return true;
                int me = Player.inst.PlayerLandmassOwner.teamId;

                if (cmd == "/diplo")
                {
                    Tell("/demand <amount> [resource] - name your price to stop fighting");
                    Tell("/offer <amount> [resource] - offer to pay them to stop");
                    Tell("/accept or /refuse - answer what is on the table");
                    Tell("resources: gold, wheat, wood, stone, charcoal, iron, tools, weapons, fish, apples, pork");
                    return true;
                }

                int them = SoleOpponent(me);
                if (them == 0)
                {
                    Tell("no one to negotiate with (these commands need exactly one other kingdom)");
                    return true;
                }

                if (cmd == "/accept" || cmd == "/refuse")
                {
                    if (!openDeals.ContainsKey(Key(me, them))) { Tell("there is no deal on the table"); return true; }

                    Send(me, them, cmd == "/accept" ? Messages.DealKind.Accept : Messages.DealKind.Refuse,
                         0, FreeResourceType.Gold);
                    return true;
                }

                int amount;
                if (parts.Length < 2 || !int.TryParse(parts[1], out amount) || amount <= 0)
                {
                    Tell("how much? e.g. " + cmd + " 500, or " + cmd + " 100 wood");
                    return true;
                }

                // Gold when nothing else is named, because that is what a ransom usually means.
                FreeResourceType res = FreeResourceType.Gold;
                if (parts.Length >= 3 && !TryParseResource(parts[2], out res))
                {
                    Tell("no resource called '" + parts[2] + "'. Try /diplo for the list.");
                    return true;
                }

                Send(me, them, cmd == "/demand" ? Messages.DealKind.Demand : Messages.DealKind.Offer,
                     amount, res);
                return true;
            }
            catch (Exception ex) { NetLog.Error("handling a diplomacy command", ex); return true; }
        }

        /// <summary>Sends a proposal or an answer. Public so the diplomacy window can use it too.</summary>
        public static void Send(int from, int to, Messages.DealKind kind, int amount, FreeResourceType res)
        {
            NetRouter.Send(new Messages.DiplomacyDealMessage
            {
                FromTeam = from,
                ToTeam = to,
                Kind = (int)kind,
                Amount = amount,
                Resource = (int)res
            });
        }

        /// <summary>The one other kingdom, for callers outside this class. 0 when not a single answer.</summary>
        public static int SoleOpponentOf(int me) { return SoleOpponent(me); }

        /// <summary>
        /// The one other kingdom in the session, or 0 when that is not a single obvious answer.
        ///
        /// Two players is the shape this mod is played in, and naming a target would mean typing
        /// kingdom names with spaces into a chat parser. With three or more the commands simply
        /// say so rather than guessing whom you meant to threaten.
        /// </summary>
        private static int SoleOpponent(int me)
        {
            int found = 0;
            foreach (SessionPlayer kp in Main.kCPlayers.Values)
            {
                if (kp == null || kp.inst == null || kp.inst.PlayerLandmassOwner == null) continue;

                int team = kp.inst.PlayerLandmassOwner.teamId;
                if (team == me || team < MpTeamBase) continue;

                if (found != 0) return 0;   // more than one, ambiguous
                found = team;
            }
            return found;
        }

        /// <summary>Puts a line in front of the local player only. Nothing is sent.</summary>
        private static void Tell(string line)
        {
            NetLog.Info("diplo: " + line);
            try
            {
                if (GameState.inst != null && GameState.inst.IsPlayMode())
                    KingdomLog.TryLog("mpDiplo" + (noticeSeq++), line, KingdomLog.LogStatus.Neutral, 0f);
                else
                    KaCMultiplayer.Lobby.LobbyView.AddChatNotice(line);
            }
            catch { }
        }

        /// <summary>Pending wars, for the save. See ModSaveData.pendingWars.</summary>
        public static Dictionary<long, int> SnapshotPendingWars()
        {
            return new Dictionary<long, int>(pendingWars);
        }

        /// <summary>Reinstates declared-but-unstarted wars from a save.</summary>
        public static void RestorePendingWars(Dictionary<long, int> saved)
        {
            pendingWars.Clear();
            allianceOffers.Clear();   // an offer is a live conversation, not saved state

            if (saved == null || saved.Count == 0) return;

            foreach (KeyValuePair<long, int> entry in saved)
                if (entry.Value > 0) pendingWars[entry.Key] = entry.Value;

            NetLog.Info("relations: restored " + pendingWars.Count + " pending war(s)");
        }

        /// <summary>Drops every recorded relation. Called when a session ends.</summary>
        public static void Reset()
        {
            relations.Clear();
        }
    }
}
