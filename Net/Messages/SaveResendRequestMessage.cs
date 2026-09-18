using System.Collections.Generic;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// "I am missing these pieces of the world. Send them again."
    ///
    /// WHY THE TRANSPORT CANNOT DO THIS FOR US. Riptide resends a reliable message until it is
    /// acked, which normally makes loss invisible. It does not work at the rate a save is sent.
    /// Its receiver keeps a sliding window of sequence ids it has seen, and
    /// <c>Bitfield.IsSet</c> answers TRUE for anything older than the window:
    ///
    ///     if (bit &gt; count) return true;     // treated as already received
    ///     ...
    ///     doHandle = !receivedSeqIds.IsSet(sequenceGap);
    ///
    /// So a resend that arrives after the window has moved on is discarded as a duplicate. At five
    /// hundred chunks a second the window moves on in a fraction of a second, and every attempt to
    /// recover that chunk is thrown away for the same reason the first one was. Nothing is logged
    /// at either end, because as far as both are concerned the message was delivered.
    ///
    /// What that looks like is a loading bar that climbs to the high eighties or low nineties and
    /// stops there forever, at a slightly different number each time.
    ///
    /// The receiver knows exactly which chunks it lacks, so it asks for them by number. This
    /// converges: each round is smaller than the last, and a round that loses nothing ends it.
    /// </summary>
    public class SaveResendRequestMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.SaveResend; } }

        /// <summary>
        /// The chunk ids still missing, newest request wins.
        ///
        /// Capped by the sender rather than here, because a request for every chunk of a large save
        /// would be a big message and the point is to ask for the gaps, not the world.
        /// </summary>
        public List<int> ChunkIds = new List<int>();

        public void Serialize(Message m)
        {
            m.AddIntList(ChunkIds);
        }

        public void Deserialize(Message m)
        {
            ChunkIds = m.GetIntList();
        }
    }
}
