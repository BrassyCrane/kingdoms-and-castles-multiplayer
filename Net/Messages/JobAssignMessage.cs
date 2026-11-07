// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A kingdom's own machine put a villager into a job, or took one out of it. The job is named
    /// by its building and its place in that building's job list, which is the same on every
    /// machine because every copy of a building makes its jobs in the same order. Villager empty
    /// means the job is now vacant.
    /// </summary>
    public class JobAssignMessage : IOriginated
    {
        private const int GuidBytes = 16;

        public NetMessageId Id { get { return NetMessageId.JobAssign; } }

        public ushort Origin { get; set; }

        public Guid Building;

        public int JobIndex;

        public Guid Villager;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddBytes(Building.ToByteArray(), false);
            m.AddInt(JobIndex);
            m.AddBytes(Villager.ToByteArray(), false);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Building = new Guid(m.GetBytes(GuidBytes));
            JobIndex = m.GetInt();
            Villager = new Guid(m.GetBytes(GuidBytes));
        }
    }
}
