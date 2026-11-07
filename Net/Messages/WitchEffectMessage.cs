// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A witch spell or curse just started on one player's machine, sent only when the lobby
    /// setting "Witch spells hit everyone" is on, so every machine runs the same effect on its
    /// own kingdom. The effect is named by its game class, checked on arrival to be a streamer
    /// effect before anything is made from it.
    /// </summary>
    public class WitchEffectMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.WitchEffect; } }

        public ushort Origin { get; set; }

        /// <summary>Full name of the Assets.StreamerEffect subclass, e.g. Assets.StreamerEffect_SuperPlague.</summary>
        public string Effect;

        /// <summary>The banner's title term, as the witch passed it.</summary>
        public string TitleTerm;

        public float R, G, B, A;

        /// <summary>Writes the effect name, title and colour. Multiplayer reason: see the class.</summary>
        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddString(Effect ?? string.Empty);
            m.AddString(TitleTerm ?? string.Empty);
            m.AddFloat(R); m.AddFloat(G); m.AddFloat(B); m.AddFloat(A);
        }

        /// <summary>Reads what Serialize wrote, in the same order.</summary>
        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Effect = m.GetString();
            TitleTerm = m.GetString();
            R = m.GetFloat(); G = m.GetFloat(); B = m.GetFloat(); A = m.GetFloat();
        }
    }
}
