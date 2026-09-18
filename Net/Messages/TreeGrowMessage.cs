using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// A tree grew. Host-authoritative: growth is driven by the host's simulation and
    /// pushed out, so this is never sent upward and has no server handler.
    ///
    /// The host excludes its own client from the broadcast, it grew the tree itself and
    /// does not need telling.
    /// </summary>
    public class TreeGrowMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.TreeGrow; } }

        public int X;
        public int Z;

        public void Serialize(Message m)
        {
            m.AddInt(X);
            m.AddInt(Z);
        }

        public void Deserialize(Message m)
        {
            X = m.GetInt();
            Z = m.GetInt();
        }
    }
}
