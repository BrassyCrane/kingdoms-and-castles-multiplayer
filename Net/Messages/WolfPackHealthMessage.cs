using System;
using System.Collections.Generic;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// The health of every wolf living at one den, as decided by the machine arbitrating the ground
    /// the den stands on.
    ///
    /// Wolves were the last unarbitrated thing that can fight. WolfDen.WolfData implements
    /// IProjectileHitable, so a tower or a soldier hits it exactly like anything else, and its
    /// damage method is the whole of the story: it records the attacker and subtracts from life.
    /// Nothing published the result, so every machine ran its own wolf fight and one player could
    /// be watching a pack that another player had already cleared.
    ///
    /// A den at a time, not a wolf at a time, because a wolf has no id of its own. It is an entry
    /// in its den's list, and the den has a Guid that already travels with the world. Sending the
    /// pack as one list also keeps the two halves that must agree, how many wolves and how hurt
    /// each one is, in a single message rather than in two that could arrive out of step.
    ///
    /// Nothing here kills a wolf. WolfDen.Tick does that itself once life runs out, so every
    /// machine reaches the same end from the same numbers, which is the same reasoning that lets a
    /// ship sink itself from a synced life.
    /// </summary>
    public class WolfPackHealthMessage : IOriginated
    {
        public NetMessageId Id { get { return NetMessageId.WolfPackHealth; } }

        public ushort Origin { get; set; }

        /// <summary>The den, by the id every machine already shares for it.</summary>
        public Guid Den;

        /// <summary>
        /// One life per wolf, in the den's own list order.
        ///
        /// Order is not an identity and is not claimed to be. Wolves are interchangeable: they have
        /// no name, no id and no job, and the den's list is reshuffled by RemoveSwap whenever one
        /// dies. If two machines happen to hold the same pack in a different order, a life lands on
        /// a sibling instead, and the pack still ends up the same size with the same wounds. What
        /// this must never do is invent or destroy wolves, and assigning by index into the shorter
        /// of the two lists cannot.
        /// </summary>
        public List<float> Lives = new List<float>();

        public void Serialize(Message m)
        {
            m.AddUShort(Origin);
            m.AddGuid(Den);
            m.AddFloatList(Lives);
        }

        public void Deserialize(Message m)
        {
            Origin = m.GetUShort();
            Den = m.GetGuid();
            Lives = m.GetFloatList();
        }
    }
}
