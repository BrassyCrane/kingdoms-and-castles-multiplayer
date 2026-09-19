using System.Collections.Generic;
using Riptide;

namespace KaCMultiplayer.Net.Messages
{
    /// <summary>
    /// The host's full picture of who is in the lobby. Sent whenever the roster changes,
    /// and treated by receivers as the truth, they reconcile against it rather than
    /// applying a delta.
    ///
    /// One list of records, not six parallel lists keyed by index. The parallel form it
    /// replaces had no structural guarantee the lists were the same length; anything that
    /// dropped an entry from one of them turned into an index-out-of-range in the middle
    /// of the receiver's loop, halfway through mutating the player registry.
    /// </summary>
    public class PeerRosterMessage : INetMessage
    {
        public NetMessageId Id { get { return NetMessageId.PeerRoster; } }

        /// <summary>One entry per player the host knows about, ordered by client id.</summary>
        public List<Entry> Players = new List<Entry>();

        /// <summary>A single player's lobby-visible state.</summary>
        public class Entry
        {
            public ushort ClientId;
            public string SteamId;
            public string Name;
            public string KingdomName;
            public int Banner;
            public bool Ready;

            /// <summary>
            /// The team the host has this player on, so nobody has to re-derive it.
            ///
            /// The fresh-game formula is clientId + 4, and Riptide recycles client ids as players
            /// leave, so two machines that joined at different times could otherwise disagree
            /// about who owns which team, which means disagreeing about who owns which buildings.
            /// The host's answer is the one that counts. 0 means "not assigned yet".
            /// </summary>
            public int TeamId;

            /// <summary>
            /// A kingdom from the loaded save whose player is not connected. Shown in the lobby as
            /// "not joined yet" and never counted for Start.
            /// </summary>
            public bool Ghost;
        }

        public void Serialize(Message m)
        {
            int count = Players == null ? 0 : Players.Count;
            m.AddInt(count);

            for (int i = 0; i < count; i++)
            {
                Entry e = Players[i];
                m.AddUShort(e.ClientId);
                m.AddString(e.SteamId ?? string.Empty);
                m.AddString(e.Name ?? string.Empty);
                m.AddString(e.KingdomName ?? string.Empty);
                m.AddInt(e.Banner);
                m.AddBool(e.Ready);
                m.AddInt(e.TeamId);
                m.AddBool(e.Ghost);
            }
        }

        public void Deserialize(Message m)
        {
            int count = m.GetInt();
            Players = new List<Entry>(count < 0 ? 0 : count);

            for (int i = 0; i < count; i++)
            {
                Entry e = new Entry();
                e.ClientId = m.GetUShort();
                e.SteamId = m.GetString();
                e.Name = m.GetString();
                e.KingdomName = m.GetString();
                e.Banner = m.GetInt();
                e.Ready = m.GetBool();
                e.TeamId = m.GetInt();
                e.Ghost = m.GetBool();
                Players.Add(e);
            }
        }
    }
}
