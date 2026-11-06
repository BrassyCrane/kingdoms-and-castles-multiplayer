// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Host to guests: where the host's calendar is, and where its dragon countdown is. Sent every
    /// few seconds. See Net/ClockSync.cs and Net/DragonAlerts.cs.
    /// </summary>
    public class ClockSyncMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.ClockSync; } }

        public int Year;

        /// <summary>Weather.Season, as an int: 0 summer, 1 winter.</summary>
        public int Season;

        /// <summary>Game-seconds left in the current season (Weather.seasonTime).</summary>
        public float SeasonTime;

        /// <summary>DragonSpawn.yearsUntilNextAttack on the host.</summary>
        public int DragonYears;

        /// <summary>DragonSpawn.totalYearsUntilNextAttack on the host (the countdown bar's length).</summary>
        public int DragonTotalYears;

        /// <summary>DragonSpawn.totalDragonAttacks on the host.</summary>
        public int DragonAttacks;

        /// <summary>DragonSpawn.AllowSpawning() on the host: whether dragons can come at all yet.</summary>
        public bool DragonsAllowed;

        public int VikingYears;
        public int VikingTotalYears;
        public int VikingSkirmishYears;

        public void Serialize(Message m)
        {
            m.AddInt(Year);
            m.AddInt(Season);
            m.AddFloat(SeasonTime);
            m.AddInt(DragonYears);
            m.AddInt(DragonTotalYears);
            m.AddInt(DragonAttacks);
            m.AddBool(DragonsAllowed);
            m.AddInt(VikingYears);
            m.AddInt(VikingTotalYears);
            m.AddInt(VikingSkirmishYears);
        }

        public void Deserialize(Message m)
        {
            Year = m.GetInt();
            Season = m.GetInt();
            SeasonTime = m.GetFloat();
            DragonYears = m.GetInt();
            DragonTotalYears = m.GetInt();
            DragonAttacks = m.GetInt();
            DragonsAllowed = m.GetBool();
            VikingYears = m.GetInt();
            VikingTotalYears = m.GetInt();
            VikingSkirmishYears = m.GetInt();
        }
    }
}
