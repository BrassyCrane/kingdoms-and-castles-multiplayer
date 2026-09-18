using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// The host's map: its seed AND the world type, size and rivers it was built with. Receiving
    /// this regenerates the world from scratch, so it is only ever sent from the host and only
    /// before play begins.
    ///
    /// WHY THE SETTINGS TRAVEL WITH THE SEED. A seed alone does not reproduce a map; generation
    /// also depends on type, size and rivers. This used to rely on the lobby-settings message
    /// arriving first, and nothing guarantees that order, so a guest could build the new seed with
    /// the old rivers setting. Worse, a "Random" size or rivers setting is rolled inside
    /// World.Generate BEFORE the seed is applied, so every machine rolled its own answer from the
    /// same lobby. Both showed up as host and guest looking at different islands.
    ///
    /// So this carries the values the host's world was ACTUALLY generated with (World's
    /// generated* fields, never "Random"), and the receiver builds exactly that.
    /// </summary>
    public class WorldSeedMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.WorldSeed; } }

        public int Seed;

        /// <summary>World.MapBias the host's map was built with.</summary>
        public int MapBias;

        /// <summary>World.MapSize the host's map was built with, already resolved if it was Random.</summary>
        public int MapSize;

        /// <summary>World.MapRiverLakes the host's map was built with, already resolved if it was Random.</summary>
        public int RiverLakes;

        /// <summary>The world as it stands on this machine, which is the host whenever this is sent.</summary>
        public static WorldSeedMessage ForCurrentWorld()
        {
            return new WorldSeedMessage
            {
                Seed = World.inst.seed,
                MapBias = (int)World.inst.generatedMapsBias,
                MapSize = (int)World.inst.generatedMapSize,
                RiverLakes = (int)World.inst.generatedRiverLakes
            };
        }

        public void Serialize(Message m)
        {
            m.AddInt(Seed);
            m.AddInt(MapBias);
            m.AddInt(MapSize);
            m.AddInt(RiverLakes);
        }

        public void Deserialize(Message m)
        {
            Seed = m.GetInt();
            MapBias = m.GetInt();
            MapSize = m.GetInt();
            RiverLakes = m.GetInt();
        }
    }
}
