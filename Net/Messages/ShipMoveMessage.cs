using System;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// What a move order is aimed at.
    ///
    /// Vanilla's IMoveTarget is implemented by a cell, a building, an army, and the two
    /// controllable ships, and the difference matters: ordering a unit AT a cell is a walk, while
    /// ordering it at an army or a building is an attack, and ordering it at a transport is
    /// boarding. Only the cell case used to travel, so every attack and every boarding stayed on
    /// the machine that issued it.
    /// </summary>
    public enum MoveTargetKind
    {
        /// <summary>A position on the map. X and Z are the order.</summary>
        Cell = 0,

        /// <summary>Another army, so an attack. Resolved by guid.</summary>
        Army = 1,

        /// <summary>A building, so an attack. Resolved by guid across all players.</summary>
        Building = 2,

        /// <summary>A transport or seed ship, so a boarding. Resolved by guid.</summary>
        Ship = 3,

        /// <summary>An enemy siege catapult, so an attack. Resolved by guid.</summary>
        SiegeCatapult = 4,
    }

    /// <summary>
    /// A move or attack order for a ship or an army. The guid is looked up in both collections,
    /// since both are commanded the same way and share an id space here.
    ///
    /// Only the order travels, movement replays locally from it rather than being streamed as
    /// positions, which is why one small message per command suffices.
    ///
    /// X and Z are always filled in, even when the order names an entity. They are the target's
    /// position at the moment the order was given, and they are what the receiver falls back to
    /// when it cannot resolve the entity, which happens legitimately: combat is not synced, so a
    /// unit that is already dead here would otherwise drop the order entirely. Walking to where the
    /// target was is closer to right than standing still.
    /// </summary>
    public class ShipMoveMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.ShipMove; } }

        public ushort Origin { get; set; }

        /// <summary>Ship or army id. In multiplayer a ship's guid is its launch building's.</summary>
        public Guid Unit;

        public int X;
        public int Z;

        /// <summary>What kind of thing the order names.</summary>
        public MoveTargetKind TargetKind;

        /// <summary>The target's id, or Guid.Empty for a plain position order.</summary>
        public Guid Target;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin).AddGuid(Unit);
            m.AddInt(X);
            m.AddInt(Z);
            m.AddInt((int)TargetKind);
            m.AddGuid(Target);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Unit = m.GetGuid();
            X = m.GetInt();
            Z = m.GetInt();
            TargetKind = (MoveTargetKind)m.GetInt();
            Target = m.GetGuid();
        }
    }
}
