using System;
using System.Collections.Generic;
using KaCMultiplayer.Net;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Combat
{
    /// <summary>
    /// Publishes the outcome of every fight this machine is the arbiter of, and applies the
    /// outcomes other machines publish.
    ///
    /// Combat cannot be replayed from orders. Damage is distributed by a random draw, pathing runs
    /// on another thread, and attacks resolve against live positions, so two machines replaying the
    /// same orders reach different battles. Instead exactly one machine resolves each fight (see
    /// CombatRule) and states the result; everyone else suppresses their own resolution and listens.
    ///
    /// A periodic sweep rather than a hook on the damage itself. Damage lands many times a second
    /// per army, and a message per blow would be both a flood and pointless, since only the running
    /// total is observable. Sweeping means one message per army per change, and it costs nothing
    /// when nobody is fighting, because an army whose state has not moved is not sent at all.
    /// </summary>
    public static class CombatSync
    {
        /// <summary>
        /// Fixed ticks between sweeps. Fast enough that a death shows up promptly on the other
        /// machines, slow enough that a long battle is a trickle of small messages rather than a
        /// stream. Only CHANGED armies are sent, so this is an upper bound on rate, not a cost.
        /// </summary>
        private const int TicksBetweenSweeps = 10;

        private static int tickCounter;

        /// <summary>
        /// Sweeps between prunes of the published-state tables. Roughly a minute at the default
        /// sweep rate, which is often enough that a long siege cannot pile up much, and rare enough
        /// that the cost of building the live-guid sets is nothing.
        /// </summary>
        private const int SweepsBetweenPrunes = 300;

        private static int sweepsSincePrune;

        /// <summary>
        /// How many army and ship updates this machine has published since the session began.
        ///
        /// Exists so the outcome of a fight can be asserted rather than assumed. The sweep is
        /// deliberately silent, which is right for a player's log and wrong for a test: a battle
        /// that resolves correctly and a battle whose results are never published look identical
        /// from the outside. Dev/AutoTest reads this after its battle. Cheap enough to leave in.
        /// </summary>
        public static int PublishedUpdates { get; private set; }

        /// <summary>What we last told everyone about each army, so unchanged armies stay silent.</summary>
        private struct Published
        {
            public int LivingUnits;
            public float GeneralLife;
        }

        private static readonly Dictionary<Guid, Published> lastSent = new Dictionary<Guid, Published>();

        /// <summary>Ship life last published, kept apart because a ship is one number, not a squad.</summary>
        private static readonly Dictionary<Guid, float> lastShipLife = new Dictionary<Guid, float>();

        /// <summary>Building life last published, same shape as ships.</summary>
        private static readonly Dictionary<Guid, float> lastBuildingLife = new Dictionary<Guid, float>();

        /// <summary>
        /// Buildings hit since the last sweep, so buildings can be published without SCANNING them.
        ///
        /// Armies and ships are swept by walking their systems, which is affordable because a
        /// session has tens of them. It has hundreds or thousands of buildings, and almost none of
        /// them are ever in a fight, so walking that list ten times a second to find the two that
        /// changed would be pure waste. The damage hook already knows precisely which building was
        /// hit, so it says so and the sweep only looks at those.
        /// </summary>
        private static readonly HashSet<Building> damagedSinceSweep = new HashSet<Building>();

        /// <summary>
        /// Called from the building damage hook, on the arbiter only, naming a building that was
        /// just hit. Cheap and safe to call as often as damage lands: it is a set add, and the
        /// sweep decides whether anything is actually worth sending.
        /// </summary>
        /// <summary>Dragon hp last published.</summary>
        private static readonly Dictionary<Guid, float> lastDragonHp = new Dictionary<Guid, float>();

        /// <summary>Dragons hit since the last sweep. Same dirty-set reasoning as buildings, and
        /// even more so: a session usually has none at all, so sweeping the list on a timer would be
        /// work done for nothing on almost every tick.</summary>
        private static readonly HashSet<Dragon> damagedDragons = new HashSet<Dragon>();

        /// <summary>Called from the dragon damage hook, on the arbiter only.</summary>
        public static void NoteDragonDamaged(Dragon d)
        {
            if (d == null) return;
            if (!Main.CombatAuthorityEnabled) return;
            if (!NetClient.client.IsConnected) return;

            damagedDragons.Add(d);
        }

        /// <summary>What we last told everyone about each wolf den's pack.</summary>
        private static readonly Dictionary<Guid, List<float>> lastWolfLives =
            new Dictionary<Guid, List<float>>();

        /// <summary>Siege catapult life last published, keyed by the id the spawn established.</summary>
        private static readonly Dictionary<Guid, float> lastCatapultLife = new Dictionary<Guid, float>();

        /// <summary>
        /// Catapults hit since the last sweep, held as id AND life rather than as the object.
        ///
        /// The other dirty sets keep the thing itself and read its health at sweep time, which
        /// works because a wrecked building and a sunk ship both leave something behind to read. A
        /// catapult does not: the last blow destroys its GameObject inside the damage call, so by
        /// the next sweep the reference is a destroyed Unity object and the sweep would skip it.
        /// That is precisely the update that matters most, the one saying it is dead, so the value
        /// is captured at the moment of the hit instead. Reading the plain guid and life fields off
        /// a destroyed object is safe; they are managed fields, not Unity properties.
        /// </summary>
        private static readonly Dictionary<Guid, float> damagedCatapults = new Dictionary<Guid, float>();

        /// <summary>
        /// True while this machine is dealing a catapult the damage its arbiter reported.
        ///
        /// A catapult has no public TakeDamage of its own; the ONLY way in is the explicit
        /// interface implementation that the authority hook suppresses, so applying a remote
        /// verdict would be blocked by our own suppression. Buildings and dragons never needed
        /// this because each has a separate public damage method the hook does not sit on.
        ///
        /// Set and cleared around one synchronous call, so it cannot stay stuck: the game is
        /// single-threaded here and the clear is in a finally.
        /// </summary>
        public static bool ApplyingCatapultDamage { get; private set; }

        /// <summary>
        /// Called from the catapult damage hook's Postfix, on the arbiter only, with the life the
        /// catapult has AFTER the blow. A Postfix rather than a Prefix because the whole point is
        /// to record the outcome, including the zero that means it just died.
        /// </summary>
        public static void NoteCatapultDamaged(SiegeCatapult c)
        {
            if (c == null) return;
            if (!Main.CombatAuthorityEnabled) return;
            if (!NetClient.client.IsConnected) return;

            damagedCatapults[c.guid] = c.life;
        }

        public static void NoteBuildingDamaged(Building b)
        {
            if (b == null) return;
            if (!Main.CombatAuthorityEnabled) return;
            if (!NetClient.client.IsConnected) return;

            damagedSinceSweep.Add(b);
        }

        /// <summary>Forgets published state, for a new session or a load.</summary>
        public static void Reset()
        {
            lastSent.Clear();
            lastShipLife.Clear();
            lastBuildingLife.Clear();
            damagedSinceSweep.Clear();
            lastDragonHp.Clear();
            damagedDragons.Clear();
            lastCatapultLife.Clear();
            damagedCatapults.Clear();
            lastWolfLives.Clear();
            tickCounter = 0;
            sweepsSincePrune = 0;
            PublishedUpdates = 0;
        }

        /// <summary>
        /// Called every fixed tick. Broadcasts any army whose visible state has changed since last
        /// time, but only for armies this machine arbitrates.
        /// </summary>
        public static void Tick()
        {
            if (!Main.CombatAuthorityEnabled) return;   // feature dark, nothing to publish
            if (!NetClient.client.IsConnected) return;
            if (UnitSystem.inst == null) return;

            if (++tickCounter < TicksBetweenSweeps) return;
            tickCounter = 0;

            try
            {
                var armies = UnitSystem.inst.armies;
                for (int i = 0; i < armies.Count; i++)
                {
                    UnitSystem.Army army = armies.data[i];
                    if (army == null) continue;

                    // The arbiter is decided by where the army IS, so it can change mid-campaign as
                    // an army marches onto someone else's island. That is intended: the fight moves
                    // to whoever owns the ground it is being fought on.
                    if (!CombatAuthority.ResolvesHere(army.generalPos)) continue;

                    Published now;
                    now.LivingUnits = army.units.Count;
                    now.GeneralLife = army.generalLife;

                    Published before;
                    if (lastSent.TryGetValue(army.guid, out before)
                        && before.LivingUnits == now.LivingUnits
                        && before.GeneralLife == now.GeneralLife)
                        continue;   // nothing has happened to this army

                    lastSent[army.guid] = now;
                    PublishedUpdates++;

                    NetRouter.Send(new ArmyHealthMessage
                    {
                        Army = army.guid,
                        LivingUnits = now.LivingUnits,
                        GeneralLife = now.GeneralLife
                    });
                }

                SweepShips();
                SweepDamagedBuildings();
                SweepDamagedDragons();
                SweepDamagedCatapults();
                SweepWolves();

                if (++sweepsSincePrune >= SweepsBetweenPrunes)
                {
                    sweepsSincePrune = 0;
                    PruneStaleTracking();
                }
            }
            catch (Exception ex) { NetLog.Error("combat sweep", ex); }
        }

        /// <summary>
        /// Drops published state for armies and ships that no longer exist.
        ///
        /// Without this the tables only ever grow: every army that dies and every ship that sinks
        /// leaves its last published state behind for the rest of the session. That is a slow leak
        /// in a long war, and worse than a leak if an id is ever reused, since a stale entry would
        /// match the new owner's first report and suppress it, leaving that unit's state unpublished
        /// until something else about it changed.
        ///
        /// Done on a timer rather than on every removal because there is no single place a ship
        /// stops existing, and because being a minute out of date costs nothing here.
        /// </summary>
        private static void PruneStaleTracking()
        {
            HashSet<Guid> live = new HashSet<Guid>();

            if (UnitSystem.inst != null)
            {
                var armies = UnitSystem.inst.armies;
                for (int i = 0; i < armies.Count; i++)
                    if (armies.data[i] != null) live.Add(armies.data[i].guid);
            }

            Prune(lastSent, live);

            live.Clear();
            if (ShipSystem.inst != null)
            {
                var ships = ShipSystem.inst.ships;
                for (int i = 0; i < ships.Count; i++)
                    if (ships.data[i] != null) live.Add(ships.data[i].guid);
            }

            Prune(lastShipLife, live);

            live.Clear();
            var catapults = SiegeCatapultSystem.siegeCatapults;
            if (catapults != null)
                for (int i = 0; i < catapults.Count; i++)
                    if (catapults.data[i] != null) live.Add(catapults.data[i].guid);

            Prune(lastCatapultLife, live);

            // Buildings are not held in one system list the way armies and ships are, so the live
            // set is built from what we have actually published about.
            List<Guid> goneBuildings = null;
            foreach (Guid id in lastBuildingLife.Keys)
            {
                if (Main.FindBuildingByGuidAnywhere(id) != null) continue;
                if (goneBuildings == null) goneBuildings = new List<Guid>();
                goneBuildings.Add(id);
            }
            if (goneBuildings != null)
                for (int i = 0; i < goneBuildings.Count; i++) lastBuildingLife.Remove(goneBuildings[i]);
        }

        /// <summary>Removes every key of <paramref name="table"/> that is not in <paramref name="live"/>.</summary>
        private static void Prune<T>(Dictionary<Guid, T> table, HashSet<Guid> live)
        {
            if (table.Count == 0) return;

            List<Guid> stale = null;
            foreach (Guid key in table.Keys)
            {
                if (live.Contains(key)) continue;
                if (stale == null) stale = new List<Guid>();
                stale.Add(key);
            }

            if (stale == null) return;
            for (int i = 0; i < stale.Count; i++) table.Remove(stale[i]);
        }

        /// <summary>
        /// Publishes the life of any ship this machine arbitrates whose life has moved.
        ///
        /// Which machine that is follows the same landmass rule as everything else, and the answer
        /// is less often "the host" than it first looks. **Coastal water carries the neighbouring
        /// island's landMassIdx**, measured 2026-09-05: a dock's own water cells report the island's
        /// index and therefore its owner's team, even though the cell type is Water. So a fight in
        /// somebody's harbour is arbitrated by the owner of that harbour, and only genuinely open
        /// water, which owns to nobody, falls to the host.
        ///
        /// That is the behaviour we want and it needs no special case: both machines read the same
        /// index from the same position and reach the same verdict, and authority hands over on its
        /// own as a ship sails from one player's coast to another's. Worth stating because the
        /// obvious assumption, "ships are on water, water is unowned, so the host decides", is
        /// wrong and would send someone looking for a bug that is not there.
        ///
        /// Ships that are not fighting never send anything, since an unchanged life is not reported.
        /// </summary>
        private static void SweepShips()
        {
            if (ShipSystem.inst == null) return;

            var ships = ShipSystem.inst.ships;
            for (int i = 0; i < ships.Count; i++)
            {
                ShipBase ship = ships.data[i];
                if (ship == null) continue;

                if (!CombatAuthority.ResolvesHere(ship.GetPos())) continue;

                float life = ship.life;

                float before;
                if (lastShipLife.TryGetValue(ship.guid, out before) && before == life) continue;

                lastShipLife[ship.guid] = life;
                PublishedUpdates++;

                NetRouter.Send(new ShipHealthMessage { Ship = ship.guid, Life = life });
            }
        }

        /// <summary>
        /// Publishes the life of every building that was hit since the last sweep.
        ///
        /// Only the arbiter reaches here, because only the arbiter's damage hook records anything.
        /// The set is cleared each sweep whatever happens, so a building that is destroyed and
        /// removed between being hit and being swept cannot be held forever.
        /// </summary>
        private static void SweepDamagedBuildings()
        {
            if (damagedSinceSweep.Count == 0) return;

            try
            {
                foreach (Building b in damagedSinceSweep)
                {
                    if (b == null) continue;   // destroyed between the hit and this sweep

                    float life = b.Life;

                    float before;
                    if (lastBuildingLife.TryGetValue(b.guid, out before) && before == life) continue;

                    lastBuildingLife[b.guid] = life;
                    PublishedUpdates++;

                    NetRouter.Send(new BuildingHealthMessage { Building = b.guid, Life = life });
                }
            }
            finally
            {
                damagedSinceSweep.Clear();
            }
        }

        /// <summary>
        /// A dragon's current health.
        ///
        /// Read by reflection because Dragon.hp is private and the class exposes no getter for it,
        /// unlike Building.Life or ShipBase.life. Falls back to zero, which is harmless here: an
        /// unreadable dragon simply looks unchanged and nothing is published about it.
        /// </summary>
        private static float DragonHp(Dragon d)
        {
            return KaCMultiplayer.Net.PrivateField.Get<float>(d, "hp", 0f);
        }

        /// <summary>Publishes the hp of every dragon hit since the last sweep.</summary>
        private static void SweepDamagedDragons()
        {
            if (damagedDragons.Count == 0) return;

            try
            {
                foreach (Dragon d in damagedDragons)
                {
                    if (d == null) continue;

                    float hp = DragonHp(d);

                    float before;
                    if (lastDragonHp.TryGetValue(d.id, out before) && before == hp) continue;

                    lastDragonHp[d.id] = hp;
                    PublishedUpdates++;

                    NetRouter.Send(new DragonHealthMessage { Dragon = d.id, Hp = hp });
                }
            }
            finally
            {
                damagedDragons.Clear();
            }
        }

        /// <summary>
        /// Publishes the life of every siege catapult hit since the last sweep.
        ///
        /// Unlike the other sweeps this one has nothing to look up: the hit itself recorded both
        /// the id and the resulting life, so a catapult that was destroyed by the blow is still
        /// reported, which is the update the other machines most need.
        /// </summary>
        private static void SweepDamagedCatapults()
        {
            if (damagedCatapults.Count == 0) return;

            try
            {
                foreach (KeyValuePair<Guid, float> hit in damagedCatapults)
                {
                    float before;
                    if (lastCatapultLife.TryGetValue(hit.Key, out before) && before == hit.Value) continue;

                    lastCatapultLife[hit.Key] = hit.Value;
                    PublishedUpdates++;

                    NetRouter.Send(new SiegeCatapultHealthMessage { Catapult = hit.Key, Life = hit.Value });
                }
            }
            finally
            {
                damagedCatapults.Clear();
            }
        }

        /// <summary>
        /// Publishes the pack at every wolf den this machine arbitrates whose wolves have changed.
        ///
        /// Swept rather than driven from a dirty set, and that is the cheap choice here rather than
        /// the lazy one. A wolf carries no back-reference to its den, so a damage hook would have to
        /// search every den to say which pack was hit, and a map holds a handful of dens with a
        /// handful of wolves each. Walking them outright is less work than the lookup would be, and
        /// a map with no dens costs one list check.
        ///
        /// Arbitrated by where the DEN stands rather than where each wolf is standing. Wolves
        /// cannot swim, so a pack and its den always share an island and the two answers agree;
        /// asking about the den keeps one verdict for the whole message instead of a wolf at the
        /// water's edge being decided differently from its neighbour.
        /// </summary>
        private static void SweepWolves()
        {
            var dens = WolfDen.wolfDens;
            if (dens == null || dens.Count == 0) return;

            for (int d = 0; d < dens.Count; d++)
            {
                WolfDen den = dens[d];
                if (den == null) continue;

                if (!CombatAuthority.ResolvesHere(den.GetPos())) continue;

                var pack = den.wolfData;
                if (pack == null) continue;

                // Compared against the pack in place, without building anything. A sweep runs ten
                // times a second and almost every den is quiet, so allocating a list per den per
                // sweep just to find out nothing had changed would be a steady drip of garbage on
                // the same per-tick path that has starved this simulation before. The list is only
                // built when there is something to say.
                List<float> before;
                if (lastWolfLives.TryGetValue(den.guid, out before) && Unchanged(before, pack)) continue;

                List<float> now = new List<float>(pack.Count);
                for (int i = 0; i < pack.Count; i++)   // .Count, never .data.Length
                {
                    WolfDen.WolfData w = pack.data[i];
                    now.Add(w != null ? w.life : 0f);
                }

                lastWolfLives[den.guid] = now;
                PublishedUpdates++;

                NetRouter.Send(new WolfPackHealthMessage { Den = den.guid, Lives = now });
            }
        }

        /// <summary>
        /// Whether a pack still matches what we last published about it, answered by reading the
        /// wolves directly so an untouched den costs a few float comparisons and no allocation.
        /// </summary>
        private static bool Unchanged(List<float> published, ArrayExt<WolfDen.WolfData> pack)
        {
            if (published == null || published.Count != pack.Count) return false;

            for (int i = 0; i < pack.Count; i++)
            {
                WolfDen.WolfData w = pack.data[i];
                float life = w != null ? w.life : 0f;
                if (published[i] != life) return false;
            }
            return true;
        }

        /// <summary>
        /// Brings a local wolf pack into line with what its arbiter reported.
        ///
        /// Assigns life rather than dealing damage, which is the ship rule rather than the building
        /// rule, and it is right here for the same reason: WolfDen.Tick turns a wolf whose life has
        /// run out into a dying one by itself, so the number is enough and every machine reaches the
        /// same end through its own local code. Dealing the difference instead would mean calling
        /// the damage method our own authority hook is busy suppressing.
        ///
        /// Only as many wolves as both sides agree exist are touched. A pack that is somehow
        /// shorter here is left short rather than being handed wolves whose position and target
        /// would have to be invented, the same restraint the army repair shows.
        /// </summary>
        public static void ApplyWolfPackHealth(WolfPackHealthMessage m)
        {
            try
            {
                WolfDen den = Main.FindWolfDenByGuid(m.Den);
                if (den == null || den.wolfData == null || m.Lives == null) return;

                var pack = den.wolfData;
                int shared = pack.Count < m.Lives.Count ? pack.Count : m.Lives.Count;

                for (int i = 0; i < shared; i++)
                {
                    WolfDen.WolfData w = pack.data[i];
                    if (w == null) continue;

                    // Damage only, never healing. There is no un-wounding in the game and a wolf we
                    // already believe is nearly dead must not be handed its health back.
                    if (m.Lives[i] < w.life) w.life = m.Lives[i];
                }

                // Deliberately quiet on success. This message can arrive many times per second for
                // an unchanged pack, and the spam buries the load diagnostics we need after a
                // resume or saved-game join. Exceptions are still logged below.
            }
            catch (Exception ex) { NetLog.Error("wolf pack health", ex); }
        }

        /// <summary>
        /// Brings a local siege catapult into line with what its arbiter reported.
        ///
        /// Deals the DIFFERENCE through the catapult's own damage method, for the same reason
        /// buildings and dragons do: dying is something the unit does to itself as a consequence of
        /// being hurt. SiegeCatapult sets its status to Dead, tells OrdersManager the unit was
        /// killed, plays the wreck effects and calls Release, all inside that one method. Writing
        /// life alone would leave a catapult sitting at zero, intact and manned, on every machine
        /// but the arbiter's, and permanently undamageable there because its own damage is
        /// suppressed. That is the exact regression buildings shipped with once.
        ///
        /// The call has to get past our own suppression hook, hence ApplyingCatapultDamage.
        ///
        /// The attacker is reported as team -1, which resolves to no landmass owner. That is not a
        /// lie about who fired: the arbiter has already decided the outcome, and the attacking team
        /// is read in here only to apply that team's hazard-pay multiplier, which would scale
        /// damage that has already been scaled once and overshoot the number we are aiming at. The
        /// one other thing it reaches, RaiderSystem's kill handler, ignores it.
        ///
        /// Damage only, never healing. A copy that is somehow already weaker is left alone rather
        /// than being handed life back.
        /// </summary>
        public static void ApplyCatapultHealth(SiegeCatapultHealthMessage m)
        {
            try
            {
                SiegeCatapult c = Main.FindSiegeCatapultByGuid(m.Catapult);
                if (c == null) return;   // never arrived here, or already destroyed

                float delta = c.life - m.Life;
                if (delta <= 0f) return;

                ApplyingCatapultDamage = true;
                try
                {
                    ((IProjectileHitable)c).TakeProjectileDamage(
                        delta, DamageType.Standard, DamageSource.Ranged,
                        c.transform.position, UnityEngine.Vector3.zero, -1);
                }
                finally { ApplyingCatapultDamage = false; }

                NetLog.Info("catapult health: " + m.Catapult + " -> " + m.Life);
            }
            catch (Exception ex) { NetLog.Error("catapult health", ex); }
        }

        /// <summary>
        /// Brings a local dragon into line with what its arbiter reported.
        ///
        /// Applies the DIFFERENCE through the dragon's own TakeDamage rather than assigning hp, for
        /// the same reason buildings do: dying is something the dragon does to itself as a result of
        /// taking damage, and writing the number alone would leave it at zero hp, still flying.
        ///
        /// Damage only, never healing. A dragon that is somehow already weaker here is left alone
        /// rather than being handed health back.
        /// </summary>
        public static void ApplyDragonHealth(DragonHealthMessage m)
        {
            try
            {
                Dragon d = Main.FindDragonById(m.Dragon);
                if (d == null) return;

                float delta = DragonHp(d) - m.Hp;
                if (delta <= 0f) return;

                d.TakeDamage(delta);
                NetLog.Info("dragon health: " + m.Dragon + " -> " + m.Hp);
            }
            catch (Exception ex) { NetLog.Error("dragon health", ex); }
        }

        /// <summary>
        /// Brings a local building into line with what its arbiter reported.
        ///
        /// Applies the DIFFERENCE through the game's own <c>Building.TakeDamage</c> rather than
        /// assigning Life directly, because assigning it would never destroy anything. A building
        /// is wrecked inside TakeDamageInternal, which sets its private `destroyed` flag, drops it
        /// from the damaged list and leaves rubble. Writing the number alone would leave a building
        /// sitting at zero life, intact and standing, on every machine but the arbiter's.
        ///
        /// Damage only, never healing. A local copy that somehow has LESS life than the arbiter
        /// reports is left alone: there is no "un-damage" in the game and inventing one would mean
        /// resurrecting a building the arbiter may have already wrecked.
        /// </summary>
        public static void ApplyBuildingHealth(BuildingHealthMessage m)
        {
            try
            {
                Building b = Main.FindBuildingByGuidAnywhere(m.Building);
                if (b == null) return;   // never arrived here, or already gone

                float delta = b.Life - m.Life;
                if (delta <= 0f) return;

                // Position and attacker name only drive the hit effect and the log line; the
                // arbiter has already decided the outcome that matters.
                b.TakeDamage(delta, b.transform.position, string.Empty);

                NetLog.Info("building health: " + m.Building + " -> " + m.Life);
            }
            catch (Exception ex) { NetLog.Error("building health", ex); }
        }

        /// <summary>
        /// Applies a ship's arbitrated life.
        ///
        /// Nothing here sinks the ship. ShipBase.Tick does that by itself once life reaches zero, so
        /// setting the number is enough and every machine arrives at the same end through its own
        /// local rule. Sending a separate death event as well would give two things the chance to
        /// disagree about whether the ship is gone.
        /// </summary>
        public static void ApplyShipHealth(ShipHealthMessage m)
        {
            try
            {
                var ships = ShipSystem.inst.ships;
                for (int i = 0; i < ships.Count; i++)
                {
                    ShipBase ship = ships.data[i];
                    if (ship == null || ship.guid != m.Ship) continue;

                    ship.life = m.Life;
                    NetLog.Info("ship health: " + m.Ship + " -> " + m.Life);
                    return;
                }
            }
            catch (Exception ex) { NetLog.Error("ship health", ex); }
        }

        /// <summary>
        /// Brings a local army into line with what its arbiter reported.
        ///
        /// Releases surplus soldiers rather than re-running damage on them. The point of the model
        /// is that this machine does NOT decide who died, so re-deriving the casualties here would
        /// reintroduce exactly the disagreement the arbiter exists to prevent.
        ///
        /// Units are never ADDED back. A count higher than ours means our copy lost men it should
        /// not have, and the honest repair is to leave it short rather than conjure soldiers whose
        /// position, animation state and job would all be invented.
        /// </summary>
        public static void ApplyHealth(ArmyHealthMessage m)
        {
            try
            {
                UnitSystem.Army army = UnitSystem.inst.FindArmyByGuid(m.Army);
                if (army == null) return;   // already gone here, or never arrived; ArmyDespawn covers removal

                army.generalLife = m.GeneralLife;

                int surplus = army.units.Count - m.LivingUnits;
                if (surplus <= 0) return;

                for (int i = 0; i < surplus && army.units.Count > 0; i++)
                {
                    UnitSystem.Unit doomed = army.units.data[army.units.Count - 1];
                    if (doomed == null) break;
                    UnitSystem.inst.ReleaseUnit(doomed);
                }

                NetLog.Info("army health: " + m.Army + " -> " + m.LivingUnits +
                            " standing, general " + m.GeneralLife);
            }
            catch (Exception ex) { NetLog.Error("army health", ex); }
        }
    }
}
