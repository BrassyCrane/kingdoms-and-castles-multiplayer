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

        // ---- player list ------------------------------------------------------

        /// <summary>
        /// Adds a row for a connected player. Rows are keyed by Steam id rather than client id,
        /// because a saved kingdom whose player has not joined has no client id of its own, and
        /// when that player does join, their row should stay the same row.
        /// </summary>
        public static void AddPlayer(ushort clientId)
        {
            string steamId;
            if (Main.clientSteamIds.TryGetValue(clientId, out steamId)) AddRow(steamId);
            else
            {
                SessionPlayer p = NetPlayers.ById(clientId);
                if (p != null) AddRow(p.steamId);
            }
        }

        /// <summary>
        /// Makes the list match the player registry: a row for everyone in it, including saved
        /// kingdoms nobody has joined as yet, and no row for anyone who has gone. Cheap enough to
        /// run every second, and it means no join, leave or roster path can leave the list stale.
        /// </summary>
        public static void SyncRows()
        {
            try
            {
                if (LobbyScreen.RosterContent == null) return;

                foreach (string steamId in Main.kCPlayers.Keys)
                    AddRow(steamId);

                for (int i = playerRows.Count - 1; i >= 0; i--)
                {
                    PlayerRow script = playerRows[i] == null ? null : playerRows[i].GetComponent<PlayerRow>();
                    if (script != null && Main.kCPlayers.ContainsKey(script.SteamId)) continue;

                    if (playerRows[i] != null) UnityEngine.Object.Destroy(playerRows[i]);
                    playerRows.RemoveAt(i);
                }
            }
            catch (Exception ex) { NetLog.Error("syncing lobby player rows", ex); }
        }

        private static void AddRow(string steamId)
        {
            try
            {
                if (string.IsNullOrEmpty(steamId) || HasRow(steamId)) return;

                GameObject row = UnityEngine.Object.Instantiate(
                    LobbyPrefabs.PlayerEntry, LobbyScreen.RosterContent);
                row.SetActive(true);

                PlayerRow script = row.AddComponent<PlayerRow>();
                script.SteamId = steamId;

                playerRows.Add(row);
            }
            catch (Exception ex) { NetLog.Error("adding lobby player row for " + steamId, ex); }
        }

        /// <summary>
        /// Clears the player list. On a client this also empties the player registry, because
        /// the next lobby starts from nothing. The host keeps its registry, it *is* the authority.
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

        private static bool HasRow(string steamId)
        {
            for (int i = 0; i < playerRows.Count; i++)
            {
                if (playerRows[i] == null) continue;
                PlayerRow script = playerRows[i].GetComponent<PlayerRow>();
                if (script != null && script.SteamId == steamId) return true;
            }
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
