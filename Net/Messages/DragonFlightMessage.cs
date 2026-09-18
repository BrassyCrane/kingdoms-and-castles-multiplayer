using System;
using System.Collections.Generic;
using Riptide;
using UnityEngine;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Where the host's dragons are, where they are facing, and which of them are breathing fire.
    /// All of them in one message.
    ///
    /// HOST-AUTHORITATIVE, unlike <see cref="ArmyPositionsMessage"/>, which the owner sends.
    /// An army belongs to a player and follows that player's orders, so the owner is the only
    /// machine with an opinion worth having. A dragon belongs to nobody: it is spawned by the host
    /// (see the DragonSpawn hooks) and flown by AI, so the host is the only machine whose opinion
    /// can be the shared one. That also means exactly one machine ever sends this, so a receiver
    /// never has to choose between two claims about the same dragon.
    ///
    /// ROTATION TRAVELS, where the army message sends position alone. An army is a cluster of
    /// pawns whose facing is a detail; a dragon is one large model that banks and turns, and a
    /// dragon flying sideways down its own flight path reads as broken even when it is in exactly
    /// the right place.
    ///
    /// FIRING TRAVELS because the receiving machines are not deciding anything for themselves any
    /// more. With <c>Dragon.UpdateActions</c> suppressed off-host, nothing there will ever call
    /// <c>FireBreath</c>, so without this flag a dragon would strafe a burning village in total
    /// silence on every screen but the host's.
    ///
    /// Four parallel lists rather than a list of records, matching the army message, so the
    /// existing list codecs are reused rather than inventing a per-dragon struct codec.
    /// </summary>
    public class DragonFlightMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.DragonFlight; } }

        public ushort Origin { get; set; }

        /// <summary>Dragon ids, matching the ones <see cref="DragonSpawnMessage"/> established.</summary>
        public List<Guid> Dragons = new List<Guid>();

        /// <summary>Position per dragon, in the same order.</summary>
        public List<Vector3> Positions = new List<Vector3>();

        /// <summary>Rotation per dragon, in the same order.</summary>
        public List<Quaternion> Rotations = new List<Quaternion>();

        /// <summary>Whether each dragon is currently breathing fire, in the same order.</summary>
        public List<bool> Firing = new List<bool>();

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddGuidList(Dragons);
            m.AddVector3List(Positions);
            m.AddQuaternionList(Rotations);
            m.AddBoolList(Firing);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Dragons = m.GetGuidList();
            Positions = m.GetVector3List();
            Rotations = m.GetQuaternionList();
            Firing = m.GetBoolList();
        }
    }
}
