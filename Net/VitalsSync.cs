// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using System.Collections.Generic;
using Riptide;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// One batch of a kingdom's villagers' hunger, health and sickness, from the kingdom's own
    /// machine. Parallel lists matched by index; the receiver walks the shortest, so a bad
    /// payload degrades rather than throwing.
    /// </summary>
    public class VillagerVitalsMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.VillagerVitals; } }

        public ushort Origin { get; set; }

        public List<Guid> Villagers = new List<Guid>();
        public List<float> Hunger = new List<float>();
        public List<float> Health = new List<float>();
        public List<float> SickTime = new List<float>();
        /// <summary>Sickness strength, or -1 for a villager who is not sick.</summary>
        public List<int> Sickness = new List<int>();

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddGuidList(Villagers);
            m.AddFloats(Hunger.ToArray());
            m.AddFloats(Health.ToArray());
            m.AddFloats(SickTime.ToArray());
            m.AddInts(Sickness.ToArray());
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Villagers = m.GetGuidList();
            Hunger = new List<float>(m.GetFloats());
            Health = new List<float>(m.GetFloats());
            SickTime = new List<float>(m.GetFloats());
            Sickness = new List<int>(m.GetInts());
        }
    }

    /// <summary>
    /// Keeps other players' villagers' hunger, health and sickness the same as on their owner's
    /// machine.
    ///
    /// Every machine simulates every villager, and hunger and sickness depend on food and
    /// doctors this machine only half knows about for another kingdom, so each copy drifted on
    /// its own: another player's people could look starving or sick on our screen while fine on
    /// theirs. The owner sends its own villagers' numbers every few seconds and everyone else
    /// overwrites their copy. The same fields the game's own save restores.
    /// </summary>
    public static class VitalsSync
    {
        /// <summary>Villagers per message: 30 x 32 bytes stays under Riptide's 1225-byte payload.</summary>
        private const int Batch = 30;

        /// <summary>Every 250 fixed ticks, about five seconds.</summary>
        private const int IntervalTicks = 250;

        /// <summary>Called every fixed tick; sends our own kingdom's villagers on the interval.</summary>
        public static void Tick(int fixedTick)
        {
            if (fixedTick % IntervalTicks != 0) return;
            try
            {
                if (!NetClient.client.IsConnected || !Main.PlayHasBegun || Player.inst == null || Player.inst.Workers == null) return;

                var workers = Player.inst.Workers;
                VillagerVitalsMessage m = new VillagerVitalsMessage();
                for (int i = 0; i < workers.Count; i++)
                {
                    Villager v = workers.data[i];
                    if (v == null) continue;
                    m.Villagers.Add(v.guid);
                    m.Hunger.Add(v.hunger);
                    m.Health.Add(v.health);
                    m.SickTime.Add(v.sickTime);
                    m.Sickness.Add(v.sick ? v.sicknessStrength : -1);
                    if (m.Villagers.Count >= Batch)
                    {
                        NetRouter.Send(m);
                        m = new VillagerVitalsMessage();
                    }
                }
                if (m.Villagers.Count > 0) NetRouter.Send(m);
            }
            catch (Exception e) { Main.helper.Log("[VITALS] send failed: " + e.Message); }
        }

        /// <summary>
        /// Overwrites our copy of the sender's villagers. Only villagers in the sender's own
        /// kingdom are touched, so a stale or misrouted batch cannot change ours.
        /// </summary>
        public static void Apply(VillagerVitalsMessage m)
        {
            if (m.Origin == NetRouter.LocalClientId) return;   // our own, relayed back

            SessionPlayer player;
            if (!NetPlayers.TryGet(m.Origin, "villager vitals", out player)) return;
            if (player.inst == null || player.inst == Player.inst || player.inst.Workers == null) return;

            try
            {
                Dictionary<Guid, Villager> theirs = new Dictionary<Guid, Villager>();
                var workers = player.inst.Workers;
                for (int i = 0; i < workers.Count; i++)
                {
                    Villager v = workers.data[i];
                    if (v != null) theirs[v.guid] = v;
                }

                int n = Math.Min(Math.Min(m.Villagers.Count, m.Hunger.Count),
                                 Math.Min(Math.Min(m.Health.Count, m.SickTime.Count), m.Sickness.Count));
                for (int i = 0; i < n; i++)
                {
                    Villager v;
                    if (!theirs.TryGetValue(m.Villagers[i], out v)) continue;
                    v.hunger = m.Hunger[i];
                    v.health = m.Health[i];
                    bool sick = m.Sickness[i] >= 0;
                    v.sick = sick;
                    v.sickTime = m.SickTime[i];
                    if (sick) v.sicknessStrength = m.Sickness[i];
                }
            }
            catch (Exception e) { NetLog.Error("villager vitals", e); }
        }
    }
}
