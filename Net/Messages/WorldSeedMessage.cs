using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// The host's map seed. Receiving this regenerates the world from scratch, so it is
    /// only ever sent from the host and only before play begins.
    ///
    /// The seed alone is not enough to reproduce a map, generation also depends on world
    /// type, size and rivers. The receiver applies the lobby settings first and then
    /// generates, which is why the settings message has to arrive before this one.
    /// </summary>
    public class WorldSeedMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.WorldSeed; } }

        public int Seed;

        public void Serialize(Message m)
        {
            m.AddInt(Seed);
        }

        public void Deserialize(Message m)
        {
            Seed = m.GetInt();
        }
    }
}
