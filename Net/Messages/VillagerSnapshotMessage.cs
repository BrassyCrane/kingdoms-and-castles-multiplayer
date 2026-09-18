using System;
using System.Collections.Generic;
using Riptide;
using UnityEngine;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Periodic position correction for villagers. Two parallel lists matched by index; the
    /// receiver walks the shorter of the two, so a truncated payload degrades rather than
    /// throwing part-way through moving villagers around.
    ///
    /// High-frequency, and each message supersedes the last, which makes this the natural
    /// first candidate for <see cref="NetDelivery.Unreliable"/>. Left reliable until it is
    /// known to work, a dropped correction is harmless, but debugging two variables at once
    /// is not.
    /// </summary>
    public class VillagerSnapshotMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.VillagerSnapshot; } }

        public List<Guid> Villagers = new List<Guid>();
        public List<Vector3> Positions = new List<Vector3>();

        public void Serialize(Message m)
        {
            m.AddGuidList(Villagers);
            m.AddVector3List(Positions);
        }

        public void Deserialize(Message m)
        {
            Villagers = m.GetGuidList();
            Positions = m.GetVector3List();
        }
    }
}
