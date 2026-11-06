// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Host to guests: the host's "DRAGON SIGHTED!" banner just went up, so put it up here too.
    /// See Net/DragonAlerts.cs.
    /// </summary>
    public class DragonSightedMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.DragonSighted; } }

        public void Serialize(Message m) { m.AddByte(0); }

        public void Deserialize(Message m) { m.GetByte(); }
    }
}
