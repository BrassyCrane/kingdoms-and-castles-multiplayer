using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

using KaCMultiplayer.Net;
using KaCMultiplayer;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// Owns the two scrolling lists in the lobby: who is here, and what has been said.
    ///
    /// Each entry is a prefab clone with a script attached that renders one row. This class
    /// exists to keep track of those clones so they can be destroyed again, Unity will not
    /// do it for us, and a lobby that is opened and closed repeatedly would otherwise leak a
    /// row per player per visit.
    /// </summary>
    public static class LobbyView
    {
        private static readonly List<GameObject> playerRows = new List<GameObject>();
        private static readonly List<GameObject> chatRows = new List<GameObject>();

        /// <summary>Player rows currently on screen. Read by the disconnect handler.</summary>
        public static IList<GameObject> PlayerRows { get { return playerRows; } }

        // ---- player list ------------------------------------------------------

        /// <summary>
        /// Adds a row for a player. The row script reads everything else it needs from the
        /// player registry using this id, so only the id is passed in.
        /// </summary>
        public static void AddPlayer(ushort clientId)
        {
            try
            {
                // Idempotent. A join is applied twice on the host, once synchronously so the
                // roster it broadcasts includes the joiner, then again when its own local client
                // receives the relayed echo, and a roster message can also re-add a player who
                // is already listed. Without this guard each of those produces a duplicate row.
                if (HasRow(clientId)) return;

                GameObject row = UnityEngine.Object.Instantiate(
                    LobbyPrefabs.PlayerEntry, LobbyScreen.RosterContent);
                row.SetActive(true);

                PlayerRow script = row.AddComponent<PlayerRow>();
                script.Client = clientId;

                playerRows.Add(row);
            }
            catch (Exception ex) { NetLog.Error("adding lobby player row for client " + clientId, ex); }
        }

        /// <summary>
        /// Clears the player list. On a client this also empties the player registry, because
        /// the host's next roster message is the authority and stale entries would survive it.
        /// The host keeps its registry, it *is* the authority.
        /// </summary>
        public static void ClearPlayers()
        {
            try
            {
                DestroyAll(playerRows);

                if (!NetHost.IsRunning)
                    Main.kCPlayers.Clear();
            }
            catch (Exception ex) { NetLog.Error("clearing the lobby player list", ex); }
        }

        /// <summary>True if this client already has a row on screen.</summary>
        public static bool HasRow(ushort clientId)
        {
            for (int i = 0; i < playerRows.Count; i++)
            {
                if (playerRows[i] == null) continue;
                PlayerRow script = playerRows[i].GetComponent<PlayerRow>();
                if (script != null && script.Client == clientId) return true;
            }
            return false;
        }

        /// <summary>Removes the row belonging to one player, if it is present.</summary>
        public static bool RemovePlayer(ushort clientId)
        {
            try
            {
                for (int i = 0; i < playerRows.Count; i++)
                {
                    PlayerRow script = playerRows[i].GetComponent<PlayerRow>();
                    if (script == null || script.Client != clientId) continue;

                    UnityEngine.Object.Destroy(playerRows[i]);
                    playerRows.RemoveAt(i);
                    return true;
                }
            }
            catch (Exception ex) { NetLog.Error("removing lobby player row for client " + clientId, ex); }
            return false;
        }

        // ---- chat -------------------------------------------------------------

        /// <summary>Adds a line of player chat.</summary>
        public static void AddChatLine(ushort clientId, string playerName, string text)
        {
            try
            {
                GameObject row = UnityEngine.Object.Instantiate(
                    LobbyPrefabs.ChatEntry, LobbyScreen.ChatContent);
                row.SetActive(true);
                chatRows.Add(row);

                ChatRow script = row.AddComponent<ChatRow>();
                script.Client = clientId;
                script.PlayerName = playerName;
                script.Message = text;

                ScrollToLatest(row);
            }
            catch (Exception ex) { NetLog.Error("adding chat line", ex); }
        }

        /// <summary>Adds a system notice, joins, leaves, and similar.</summary>
        public static void AddChatNotice(string text)
        {
            try
            {
                GameObject row = UnityEngine.Object.Instantiate(
                    LobbyPrefabs.ChatSystemEntry, LobbyScreen.ChatContent);
                row.SetActive(true);
                chatRows.Add(row);

                NoticeRow script = row.AddComponent<NoticeRow>();
                script.Message = text;

                ScrollToLatest(row);
            }
            catch (Exception ex) { NetLog.Error("adding chat notice", ex); }
        }

        public static void ClearChat()
        {
            try { DestroyAll(chatRows); }
            catch (Exception ex) { NetLog.Error("clearing lobby chat", ex); }
        }

        // ---- shared -----------------------------------------------------------

        private static void DestroyAll(List<GameObject> rows)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i] != null) UnityEngine.Object.Destroy(rows[i]);

            rows.Clear();
        }

        /// <summary>
        /// Scrolls the chat pane to the newest entry.
        ///
        /// Found via <c>GetComponentInParent</c> rather than by walking up a fixed number of
        /// parents. The previous version did <c>parent.parent.parent</c>, which silently
        /// assumed the exact Content → Viewport → ScrollView depth Unity happens to generate,
        /// wrap the content in one extra object and it would either grab the wrong component
        /// or throw. Searching upward for the ScrollRect works at any depth.
        /// </summary>
        private static void ScrollToLatest(GameObject row)
        {
            try
            {
                Canvas.ForceUpdateCanvases();

                ScrollRect scroll = row.GetComponentInParent<ScrollRect>();
                if (scroll == null) return;

                // (0,0) is the bottom for a vertical scroll rect, newest entry.
                scroll.normalizedPosition = Vector2.zero;
            }
            catch
            {
                // Cosmetic. Never worth failing a chat message over.
            }
        }
    }
}
