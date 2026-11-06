// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using System.Collections.Generic;
using Riptide;
using UnityEngine;

namespace KaCMultiplayer.Net.Messages
{
    public class RaiderBoatsMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.RaiderBoats; } }
        public List<Guid> Boats = new List<Guid>();
        public List<Vector3> Positions = new List<Vector3>();
        public List<Quaternion> Rotations = new List<Quaternion>();
        public void Serialize(Message m) { m.AddGuidList(Boats); m.AddVector3List(Positions); m.AddQuaternionList(Rotations); }
        public void Deserialize(Message m) { Boats=m.GetGuidList(); Positions=m.GetVector3List(); Rotations=m.GetQuaternionList(); }
    }
}
