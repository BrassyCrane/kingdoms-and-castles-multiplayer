using System;
using System.Collections.Generic;
using System.Linq;
using Steamworks;

namespace KaCMultiplayer.Net
{
    /// <summary>
    /// Makes sure host and guest run the same Workshop mods. Two machines with different mods
    /// simulate different games, and the drift that follows looks exactly like a bug of ours.
    ///
    /// The guest sends its list in the connect message, the host compares it with its own and
    /// refuses a guest whose list differs, sending its own list back so the guest can fix itself.
    ///
    /// The game has no on/off switch for mods: every subscribed Workshop mod loads at startup and
    /// nothing reloads until a restart. So the fix is subscribing to what is missing, unsubscribing
    /// from what is extra, and restarting.
    ///
    /// Only Workshop mods are compared. Mods dropped into the local mods folder are listed by the
    /// game's ModLoader, which the game's code-security scan forbids mods to touch.
    /// </summary>
    public static class ModList
    {
        /// <summary>This mod's own Workshop item. Left out: the version check already covers it.</summary>
        private const ulong OwnWorkshopId = 3751021307UL;

        /// <summary>
        /// The Workshop ids of every mod the game actually loaded on this machine, sorted, comma
        /// separated. Sent by the guest, and back by the host when it refuses one. Read from the
        /// game's own record, filled at startup; a mod that failed to compile is not counted, since
        /// it changes nothing in play.
        /// </summary>
        public static string Mine()
        {
            try
            {
                return string.Join(",", GameWorkshopUI.loadedMods
                    .Where(m => m != null && m.loaded && m.fileId.m_PublishedFileId != OwnWorkshopId)
                    .Select(m => m.fileId.m_PublishedFileId)
                    .Distinct().OrderBy(id => id)
                    .Select(id => id.ToString()).ToArray());
            }
            catch (Exception e)
            {
                // An empty list would refuse a guest who has mods; better to log and let the
                // comparison treat this machine as unknown.
                Main.helper.Log("[MODS] could not read the loaded mod list: " + e.Message);
                return null;
            }
        }

        /// <summary>Parses a list written by <see cref="Mine"/>. Null or "" is an empty list.</summary>
        public static HashSet<ulong> Parse(string list)
        {
            HashSet<ulong> ids = new HashSet<ulong>();
            if (string.IsNullOrEmpty(list)) return ids;
            foreach (string part in list.Split(','))
            {
                ulong id;
                if (ulong.TryParse(part.Trim(), out id)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// What the guest must change to match the host: <paramref name="missing"/> are the host's
        /// mods the guest lacks, <paramref name="extra"/> the guest's mods the host lacks. Both
        /// empty means the lists match.
        /// </summary>
        public static void Compare(string host, string guest, out List<ulong> missing, out List<ulong> extra)
        {
            HashSet<ulong> h = Parse(host), g = Parse(guest);
            missing = h.Where(id => !g.Contains(id)).OrderBy(id => id).ToList();
            extra = g.Where(id => !h.Contains(id)).OrderBy(id => id).ToList();
        }

        /// <summary>The plain-language explanation shown to the refused guest.</summary>
        public static string Describe(List<ulong> missing, List<ulong> extra)
        {
            string text = "The host is running different mods, and everyone in a game needs the same ones.";
            if (missing.Count > 0)
                text += "\n\nMissing (the host has them): Workshop item " + string.Join(", ", missing.Select(id => id.ToString()).ToArray());
            if (extra.Count > 0)
                text += "\n\nExtra (the host does not have them): Workshop item " + string.Join(", ", extra.Select(id => id.ToString()).ToArray());
            text += "\n\nFix my mods subscribes to the missing ones and unsubscribes from the extra ones, then the game closes. Start it again and rejoin.";
            return text;
        }

        /// <summary>
        /// The one-click fix: subscribe to what is missing, unsubscribe from what is extra. Steam
        /// does the downloading; the change only takes effect after a restart, because the game
        /// loads mods once at startup.
        /// </summary>
        public static void Fix(List<ulong> missing, List<ulong> extra)
        {
            foreach (ulong id in missing)
            {
                Main.helper.Log("[MODS] subscribing to " + id);
                SteamUGC.SubscribeItem(new PublishedFileId_t(id));
            }
            foreach (ulong id in extra)
            {
                Main.helper.Log("[MODS] unsubscribing from " + id);
                SteamUGC.UnsubscribeItem(new PublishedFileId_t(id));
            }
        }
    }
}
