using System;
using Riptide;
using UnityEngine;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A player's army formed and must exist on every machine.
    ///
    /// Ships get their spawn for free from synced building completion, but an army forms out
    /// of Barracks.Tick's local villager and economy simulation, which diverges between
    /// machines, the remote copy of your barracks never fills, so the army has to be
    /// spawned there explicitly.
    ///
    /// The guid replaces whatever the receiver would have generated, which is what lets
    /// later move commands find the same army on every machine.
    /// </summary>
    public class ArmySpawnMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.ArmySpawn; } }

        public ushort Origin { get; set; }

        /// <summary>Shared army id, overriding the receiver's random one.</summary>
        public Guid Army;

        /// <summary>The barracks' spawn point.</summary>
        public Vector3 Position;

        /// <summary>Owner's multiplayer team id.</summary>
        public int TeamId;

        /// <summary>UnitSystem.ArmyType as an int.</summary>
        public int ArmyType;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin).AddGuid(Army).AddVector3(Position);
            m.AddInt(TeamId);
            m.AddInt(ArmyType);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Army = m.GetGuid();
            Position = m.GetVector3();
            TeamId = m.GetInt();
            ArmyType = m.GetInt();
        }
    }
}
