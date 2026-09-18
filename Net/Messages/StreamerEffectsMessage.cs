using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// Which streamer effects are running, so everyone simulates the same game.
    ///
    /// One bit per effect rather than a list of names. The set is small, fixed for a given build of
    /// the game, and both ends derive the same bit order from the same assembly, so a mask says
    /// everything a list would and cannot half-arrive.
    ///
    /// The whole set travels every time, not just what changed. An absolute value is
    /// self-correcting: a machine that missed a message is put right by the next one, where a
    /// stream of "this one turned on" deltas would stay wrong until the session ended.
    /// </summary>
    public class StreamerEffectsMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.StreamerEffects; } }

        public ushort Origin { get; set; }

        /// <summary>Bit i is the i-th effect flag, in the order StreamerEffectSync fixes.</summary>
        public int Effects;

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddInt(Effects);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Effects = m.GetInt();
        }
    }
}
