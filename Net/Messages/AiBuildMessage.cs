// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Host to guests: one of an AI kingdom's buildings, as it now stands. AI kingdoms run on the
    /// host only, so without this a guest saw their islands empty. The same message both creates
    /// the building on a guest that does not have it yet and updates one that does, so a guest
    /// that missed the first sighting still ends up with it. See Net/AiMirror.cs.
    ///
    /// Carries the kingdom's team and banner as well, so a guest can set up the kingdom itself
    /// (who owns the island, what colours it flies) if this arrives before the AI roster does.
    /// </summary>
    public class AiBuildMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.AiBuild; } }

        public int AiTeam;
        public int Banner;

        public BuildingState State = new BuildingState();

        /// <summary>Progress toward the building's next resource output.</summary>
        public float ResourceProgress;

        public void Serialize(Message m)
        {
            m.AddInt(AiTeam);
            m.AddInt(Banner);
            State.Write(m);
            m.AddFloat(ResourceProgress);
        }

        public void Deserialize(Message m)
        {
            AiTeam = m.GetInt();
            Banner = m.GetInt();
            State = new BuildingState();
            State.Read(m);
            ResourceProgress = m.GetFloat();
        }
    }
}
