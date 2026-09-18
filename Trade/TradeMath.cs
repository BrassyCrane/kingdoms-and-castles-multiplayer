using System.Collections.Generic;
using Assets.Code;

namespace KaCMultiplayer.Trade
{
    /// <summary>
    /// The four state changes one merchant trade makes, as signed amounts to be added to
    /// whatever currently holds them.
    ///
    /// Signed deltas rather than "new values" on purpose. The sending machine and every
    /// receiving machine hold different objects (one simulates the route, the rest are
    /// puppeted) and they do not agree on absolute stockpile contents. They must agree on
    /// the CHANGE, which is what actually travels on the wire.
    ///
    /// Roles are fixed by position, not by direction of goods: the buyer is always the dock
    /// owner and the seller is always the merchant owner, for a sell as much as for a buy.
    /// That matches the field names on MerchantTradeMessage, so the wire message and this
    /// struct never need a mental translation between them.
    /// </summary>
    public struct TradeDeltas
    {
        /// <summary>Added to the merchant's hold. Negative on a buy, goods are leaving it.</summary>
        public ResourceAmount HoldDelta;

        /// <summary>Added to the dock's stockpile. The exact opposite of HoldDelta.</summary>
        public ResourceAmount DockDelta;

        /// <summary>Added to the dock owner's gold. Negative on a buy, they are paying.</summary>
        public int BuyerGoldDelta;

        /// <summary>Added to the merchant owner's gold. Positive on a buy, they are being paid.</summary>
        public int SellerGoldDelta;
    }

    /// <summary>
    /// The economics of a cross-player merchant trade, with no dependency on the game's
    /// singletons, Unity, or the mod's logger.
    ///
    /// Why this exists as its own file. The two halves of a trade used to compute the same
    /// arithmetic in two different places: the owner's machine worked out what a delivery was
    /// worth before broadcasting it, and every receiving machine worked out how to apply it.
    /// Two copies of one economic rule is a desync waiting to happen, the machines would
    /// quietly stop agreeing the moment either copy was edited alone. Both sides now call
    /// these functions, so there is only one rule to get right.
    ///
    /// The second reason is that this project has no compiler and no second tester. Everything
    /// here is pure arithmetic over ResourceAmount (a plain struct of ints) and FreeResourceType,
    /// so the headless test project can compile this exact file and assert against it. Keep it
    /// that way: no Main.helper.Log, no Player.inst, no UnityEngine. Anything needing those
    /// belongs at the call site.
    /// </summary>
    public static class TradeMath
    {
        /// <summary>
        /// Number of resource slots a ResourceAmount carries. Note that slot 8 (DeadVillager)
        /// is a hole: ResourceAmount stores nothing there and always reads back zero. It is
        /// still counted so list indices line up with FreeResourceType on both machines.
        /// </summary>
        public const int TypeCount = (int)FreeResourceType.NumTypes;

        /// <summary>
        /// Flattens a ResourceAmount into the fixed-length int list the wire message carries.
        /// Indexed by FreeResourceType so the receiver can read it back without a schema.
        /// </summary>
        public static List<int> ToList(ResourceAmount amount)
        {
            List<int> list = new List<int>(TypeCount);
            for (int i = 0; i < TypeCount; i++)
                list.Add(amount.Get((FreeResourceType)i));
            return list;
        }

        /// <summary>
        /// Rebuilds a ResourceAmount from the wire list, tolerating a list that is short or
        /// over-long rather than throwing. A malformed message should cost a wrong trade at
        /// worst, never an exception on a receive path that is handling someone else's action.
        /// </summary>
        public static ResourceAmount FromList(List<int> list)
        {
            ResourceAmount amount = new ResourceAmount();
            if (list == null) return amount;

            int limit = list.Count < TypeCount ? list.Count : TypeCount;
            for (int i = 0; i < limit; i++)
                if (list[i] != 0) amount.Set((FreeResourceType)i, list[i]);

            return amount;
        }

        /// <summary>
        /// What a parcel of goods is worth at the given per-unit prices.
        ///
        /// Gold is skipped: it is the thing being paid, so pricing it would let a hold full of
        /// gold be sold for more gold. Negative quantities are skipped too, so this can be
        /// handed a raw hold difference without a caller first cleaning it up.
        /// </summary>
        public static int PriceOf(ResourceAmount goods, ResourceAmount unitPrices)
        {
            int cost = 0;
            for (int i = 0; i < TypeCount; i++)
            {
                FreeResourceType type = (FreeResourceType)i;
                if (type == FreeResourceType.Gold) continue;

                int quantity = goods.Get(type);
                if (quantity <= 0) continue;

                cost += quantity * unitPrices.Get(type);
            }
            return cost;
        }

        /// <summary>
        /// What actually left a merchant's hold between two observations of it.
        ///
        /// Measured as a difference because the amount a route unloads depends on the
        /// receiving dock's free space, and the game never exposes the figure it settled on.
        /// Only decreases count: a tick that loaded goods rather than unloading them is not a
        /// delivery, and must not be billed as one. Gold is excluded for the same reason as
        /// in PriceOf.
        /// </summary>
        public static ResourceAmount Delivered(ResourceAmount before, ResourceAmount after)
        {
            ResourceAmount delivered = new ResourceAmount();
            for (int i = 0; i < TypeCount; i++)
            {
                FreeResourceType type = (FreeResourceType)i;
                if (type == FreeResourceType.Gold) continue;

                int gone = before.Get(type) - after.Get(type);
                if (gone > 0) delivered.Set(type, gone);
            }
            return delivered;
        }

        /// <summary>
        /// Caps a bill at what the payer actually has.
        ///
        /// Used only on the route-delivery path, where the goods are already sitting in the
        /// buyer's stockpile by the time anyone works out the price (vanilla deposits before
        /// it settles) and there is no clean way to put them back. Paying what they have beats
        /// both alternatives: letting gold go negative, or handing over the goods for free.
        /// A trade the buyer opens themselves is checked for affordability up front instead,
        /// which is why that path does not come through here.
        /// </summary>
        public static int ClampToAffordable(int cost, int buyerGold)
        {
            if (cost <= 0) return 0;
            if (buyerGold <= 0) return 0;
            return cost < buyerGold ? cost : buyerGold;
        }

        /// <summary>
        /// Turns a priced parcel of goods into the four deltas every machine applies.
        ///
        /// A buy moves goods out of the hold and into the dock, with gold going the other way;
        /// a sell is the exact mirror. Because it is a mirror rather than a separate rule, the
        /// two conservation properties hold by construction and are asserted by the tests:
        /// the goods one side gains the other loses, and the gold one kingdom gains the other
        /// pays. Nothing is created or destroyed by a trade.
        /// </summary>
        public static TradeDeltas Compute(bool isBuy, ResourceAmount goods, int cost)
        {
            ResourceAmount positive = goods;
            ResourceAmount negative = new ResourceAmount() - goods;

            TradeDeltas deltas = new TradeDeltas();
            if (isBuy)
            {
                deltas.HoldDelta = negative;     // goods leave the merchant
                deltas.DockDelta = positive;     // and land in the dock
                deltas.BuyerGoldDelta = -cost;   // the dock owner pays
                deltas.SellerGoldDelta = cost;   // the merchant owner is paid
            }
            else
            {
                deltas.HoldDelta = positive;     // goods are loaded onto the merchant
                deltas.DockDelta = negative;     // out of the dock's stockpile
                deltas.BuyerGoldDelta = cost;    // the dock owner is paid
                deltas.SellerGoldDelta = -cost;  // the merchant owner pays
            }
            return deltas;
        }
    }
}
