// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A message from a companion mod, carried for it without this mod knowing what it means.
    /// A companion is compiled on its own and cannot define a message type of ours, so it names
    /// a channel and hands over a string; see <see cref="AddonChannel"/>.
    /// </summary>
    public class AddonMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.Addon; } }

        /// <summary>Who sent it. Stamped by the host on relay; ignored on send.</summary>
        public ushort Origin { get; set; }

        /// <summary>True when the host sent it. Set by the host on relay; ignored on send.</summary>
        public bool FromHost;

        /// <summary>Which companion, and which of its conversations, this belongs to.</summary>
        public string Channel;

        /// <summary>The companion's own content. Opaque here.</summary>
        public string Payload;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddBool(FromHost);
            m.AddString(Channel ?? string.Empty);
            m.AddString(Payload ?? string.Empty);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            FromHost = m.GetBool();
            Channel = m.GetString();
            Payload = m.GetString();
        }
    }
}
