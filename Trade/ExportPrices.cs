using System;
using System.Collections.Generic;
using KaCMultiplayer.Net;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Trade
{
    /// <summary>
    /// What each kingdom charges for the goods it ships out.
    ///
    /// WHY THIS EXISTS. Every cross-player trade in this mod settled at one table:
    /// <c>Player.inst.defaultPayCost</c>, read by <c>MerchantUI.RefreshBuyWindow</c>. That is the
    /// LOCAL player's price list, so the buyer was quoting themselves for somebody else's cargo,
    /// and nobody could charge what their goods were actually worth to them. A kingdom sitting on a
    /// mountain of stone and no wheat had no way to say so.
    ///
    /// Vanilla looks like it meant to do better. RefreshBuyWindow reaches for the seller's own
    /// costs with <c>LandmassOwner.GetPayCosts</c>, but guards it with
    /// <c>owner.teamId != ship.TeamID()</c> after fetching that very owner BY ship.TeamID(). The
    /// condition can never be true, so the branch is dead and the default table always wins. This
    /// class is that dead branch, made to work and put in the seller's hands.
    ///
    /// THE SELLER SETS THE PRICE, which is the only arrangement that is not exploitable. Goods
    /// travel in a ship the seller loaded and sent; the buyer opens the hold and pays what the
    /// owner of the hold asks. A buyer who set prices would be naming their own discount.
    ///
    /// UNSET MEANS VANILLA. A kingdom that has never opened the price window behaves exactly as
    /// before, because <see cref="PriceFor"/> falls through to the game's own default for anything
    /// not named. So this changes nothing until somebody decides it should.
    /// </summary>
    public static class ExportPrices
    {
        /// <summary>A price of zero means the goods are not offered, not that they are free.</summary>
        public const int NotForSale = 0;

        /// <summary>
        /// Above what any sane trade would be, and enforced everywhere a price is set.
        ///
        /// Prices arrive over the wire, and a number large enough to overflow the cost arithmetic
        /// in the transaction would be a way to make a purchase cost a negative amount of gold.
        /// </summary>
        public const int MaxPrice = 9999;

        /// <summary>team -> what that team charges, per resource. Missing means "vanilla".</summary>
        private static readonly Dictionary<int, Dictionary<FreeResourceType, int>> byTeam =
            new Dictionary<int, Dictionary<FreeResourceType, int>>();

        /// <summary>Forgets every kingdom's list, for the end of a session.</summary>
        public static void Reset()
        {
            byTeam.Clear();
        }

        /// <summary>
        /// The game's own price for one unit, which is what an unset resource still costs.
        ///
        /// Read live rather than cached at startup: defaultPayCost is a field on the local Player
        /// and does not exist before a world does.
        /// </summary>
        public static int DefaultPrice(FreeResourceType type)
        {
            try
            {
                if (Player.inst == null) return 1;
                int price = Player.inst.defaultPayCost.Get(type);
                return price > 0 ? price : 1;
            }
            catch { return 1; }
        }

        /// <summary>True when this kingdom has named a price for anything at all.</summary>
        public static bool HasList(int team)
        {
            return byTeam.ContainsKey(team);
        }

        /// <summary>
        /// What <paramref name="team"/> charges for one unit of <paramref name="type"/>, falling
        /// through to the game's own price when they have not said.
        /// </summary>
        public static int PriceFor(int team, FreeResourceType type)
        {
            Dictionary<FreeResourceType, int> list;
            int price;

            if (byTeam.TryGetValue(team, out list) && list.TryGetValue(type, out price))
                return price;

            return DefaultPrice(type);
        }

        /// <summary>
        /// Whether this kingdom is willing to part with these goods at all.
        ///
        /// Withholding is a real position in a trade, distinct from pricing high: a kingdom that
        /// needs its own iron should be able to carry it in a hold without a visitor being able to
        /// buy it out from under them.
        /// </summary>
        public static bool ForSale(int team, FreeResourceType type)
        {
            return PriceFor(team, type) > NotForSale;
        }

        /// <summary>
        /// Records a price this player has chosen and tells everyone else.
        ///
        /// Broadcast rather than answered on demand, because the price has to be known on the
        /// BUYER's machine at the moment they open the hold, and a request at that moment would
        /// show them a window priced wrongly until the reply landed.
        /// </summary>
        public static void SetLocal(int team, FreeResourceType type, int price)
        {
            Set(team, type, price);
            Publish(team);
        }

        /// <summary>Records a price without announcing it. For applying somebody else's list.</summary>
        public static void Set(int team, FreeResourceType type, int price)
        {
            if (price < 0) price = 0;
            if (price > MaxPrice) price = MaxPrice;

            Dictionary<FreeResourceType, int> list;
            if (!byTeam.TryGetValue(team, out list))
            {
                list = new Dictionary<FreeResourceType, int>();
                byTeam[team] = list;
            }

            list[type] = price;
        }

        /// <summary>
        /// Sends one kingdom's whole list.
        ///
        /// Whole, because a list is cheap and a per-resource message would let two edits cross on
        /// the wire and leave the two machines holding different halves of the same list.
        /// </summary>
        public static void Publish(int team)
        {
            try
            {
                Dictionary<FreeResourceType, int> list;
                if (!byTeam.TryGetValue(team, out list)) return;

                ExportPricesMessage m = new ExportPricesMessage { Team = team };
                foreach (KeyValuePair<FreeResourceType, int> entry in list)
                {
                    m.Types.Add((int)entry.Key);
                    m.Prices.Add(entry.Value);
                }

                NetRouter.Send(m);
            }
            catch (Exception e) { NetLog.Error("publishing export prices", e); }
        }

        /// <summary>Adopts a kingdom's list as they sent it, replacing whatever we held for them.</summary>
        public static void ApplyRemote(ExportPricesMessage m)
        {
            try
            {
                if (m == null || m.Types == null || m.Prices == null) return;

                // Parallel lists: a short one means a truncated message, and reading past it would
                // price a resource from its neighbour's entry.
                int count = m.Types.Count;
                if (m.Prices.Count < count)
                {
                    NetLog.Warn("export prices: ragged message for team " + m.Team + ", "
                                + count + " resources but " + m.Prices.Count + " prices; ignored");
                    return;
                }

                byTeam.Remove(m.Team);
                for (int i = 0; i < count; i++)
                    Set(m.Team, (FreeResourceType)m.Types[i], m.Prices[i]);
            }
            catch (Exception e) { NetLog.Error("applying export prices", e); }
        }

        /// <summary>
        /// Sends every list we hold to a joiner.
        ///
        /// The host answers for everyone rather than each player announcing themselves, because a
        /// kingdom whose owner has not reconnected yet still has prices, restored from the save,
        /// and there is nobody on their side to speak for them.
        /// </summary>
        public static void SendAllTo(ushort clientId)
        {
            try
            {
                foreach (KeyValuePair<int, Dictionary<FreeResourceType, int>> team in byTeam)
                {
                    ExportPricesMessage m = new ExportPricesMessage { Team = team.Key };
                    foreach (KeyValuePair<FreeResourceType, int> entry in team.Value)
                    {
                        m.Types.Add((int)entry.Key);
                        m.Prices.Add(entry.Value);
                    }

                    NetRouter.SendTo(m, clientId);
                }
            }
            catch (Exception e) { NetLog.Error("sending export prices to a joiner", e); }
        }

        // ---- saving ---------------------------------------------------------
        //
        // Flat parallel lists rather than a nested structure, because this goes through the same
        // JSON block as the rest of the session and Unity's serialiser will not write a dictionary.

        public static void Pack(List<int> teams, List<int> types, List<int> prices)
        {
            foreach (KeyValuePair<int, Dictionary<FreeResourceType, int>> team in byTeam)
            {
                foreach (KeyValuePair<FreeResourceType, int> entry in team.Value)
                {
                    teams.Add(team.Key);
                    types.Add((int)entry.Key);
                    prices.Add(entry.Value);
                }
            }
        }

        public static void Unpack(List<int> teams, List<int> types, List<int> prices)
        {
            byTeam.Clear();
            if (teams == null || types == null || prices == null) return;

            int count = teams.Count;
            if (types.Count < count || prices.Count < count)
            {
                NetLog.Warn("export prices: ragged save block, " + count + " teams but "
                            + types.Count + " resources and " + prices.Count + " prices; skipped");
                return;
            }

            for (int i = 0; i < count; i++)
                Set(teams[i], (FreeResourceType)types[i], prices[i]);
        }
    }
}
