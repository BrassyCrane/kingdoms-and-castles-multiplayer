using System;
using System.Collections.Generic;
using Riptide;
using UnityEngine;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Where the sender's armies are standing, all of them in one message.
    ///
    /// Batched rather than one message per army, because the whole point of sending positions at
    /// all is that it must stay cheap: a session has tens of armies, and a packet each would turn a
    /// correction into a stream. Two parallel lists rather than a list of pairs, so the existing
    /// guid and vector list codecs can be reused instead of inventing a third.
    ///
    /// Only the owner sends, so a receiver never has to decide between two claims about the same
    /// army. See Combat/ArmyPositionSync.cs for why this is owner-authoritative rather than
    /// arbitrated by landmass like damage is.
    /// </summary>
    public class ArmyPositionsMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.ArmyPositions; } }

        public ushort Origin { get; set; }

        /// <summary>Army ids, matching the ones ArmySpawnMessage established.</summary>
        public List<Guid> Armies = new List<Guid>();

        /// <summary>Position per army, in the same order.</summary>
        public List<Vector3> Positions = new List<Vector3>();

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddGuidList(Armies);
            m.AddVector3List(Positions);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Armies = m.GetGuidList();
            Positions = m.GetVector3List();
        }
    }
}
