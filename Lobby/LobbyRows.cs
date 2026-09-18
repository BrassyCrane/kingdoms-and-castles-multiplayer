using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using KaCMultiplayer.Net;
using KaCMultiplayer;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// Shared bits for the lobby's list rows. All three row types resolve a player's banner
    /// texture the same way, so the lookup and its guards live here once.
    /// </summary>
    internal static class LobbyRowVisuals
    {
        /// <summary>
        /// Banner texture for a player, or null if it can't be resolved yet.
        ///
        /// Two guards matter here. A player can disconnect between refreshes, so the lookup
        /// must not index straight into the registries, a row polling four times a second will
        /// hit that. And a player whose banner has not been chosen yet holds -1, so the livery
        /// set has to be bounds-checked before it is indexed.
        /// </summary>
        public static Texture Resolve(ushort clientId, out SessionPlayer player)
        {
            player = NetPlayers.ById(clientId);
            if (player == null) return null;

            var sets = World.inst == null ? null : World.inst.liverySets;
            if (sets == null) return null;

            // liverySets is a List, not an array, Count, not Length.
            int idx = player.banner;
            if (idx < 0 || idx >= sets.Count) return null;

            return sets[idx].banners;
        }
    }

    /// <summary>
    /// One row in the lobby's player list: name, banner, ready tick.
    ///
    /// Refreshes on a timer because there is no change notification for the underlying player
    /// record, readiness and banner are mutated directly by message handlers.
    /// </summary>
    public class PlayerRow : MonoBehaviour
    {
        /// <summary>Which player this row is for.</summary>
        public ushort Client { get; set; }

        private const float RefreshSeconds = 0.25f;

        private RawImage bannerImage;
        private TextMeshProUGUI nameLabel;
        private GameObject readyMark;

        private void Start()
        {
            // Bind before the first refresh, not after. Refreshing first dereferences a banner
            // field that has not been assigned yet, which throws on the very first tick of every
            // row. Harmless in effect, the next tick 250ms later works, but it fills the log
            // with exceptions and buries the real ones.
            Bind();

            if (bannerImage != null)
            {
                Button pick = bannerImage.GetComponent<Button>();
                if (pick != null)
                {
                    // Clicking your own banner reopens the name-and-banner screen.
                    pick.onClick.AddListener(() => Main.TransitionTo(MenuState.NameAndBanner));
                }
            }

            Refresh();
            InvokeRepeating(nameof(Refresh), RefreshSeconds, RefreshSeconds);
        }

        private void Bind()
        {
            try
            {
                Transform bannerNode = transform.Find("PlayerBanner");
                if (bannerNode != null) bannerImage = bannerNode.GetComponent<RawImage>();

                Transform nameNode = transform.Find("PlayerName");
                if (nameNode != null) nameLabel = nameNode.GetComponent<TextMeshProUGUI>();

                Transform readyNode = transform.Find("Ready");
                if (readyNode != null) readyMark = readyNode.gameObject;

                if (bannerImage == null || nameLabel == null || readyMark == null)
                    NetLog.Warn("player row prefab is missing PlayerBanner, PlayerName or Ready");
            }
            catch (Exception ex) { NetLog.Error("binding player row", ex); }
        }

        /// <summary>Public because InvokeRepeating resolves it by name.</summary>
        public void Refresh()
        {
            try
            {
                SessionPlayer player;
                Texture banner = LobbyRowVisuals.Resolve(Client, out player);

                // The player has gone, stop polling rather than logging every 250ms until
                // the row is destroyed.
                if (player == null)
                {
                    CancelInvoke(nameof(Refresh));
                    return;
                }

                if (nameLabel != null) nameLabel.text = player.name;
                if (readyMark != null) readyMark.SetActive(player.ready);
                if (banner != null && bannerImage != null) bannerImage.texture = banner;
            }
            catch (Exception ex)
            {
                NetLog.Error("refreshing player row for client " + Client, ex);
                CancelInvoke(nameof(Refresh));
            }
        }
    }

    /// <summary>One line of player chat: who said it, what they said, their banner.</summary>
    public class ChatRow : MonoBehaviour
    {
        public ushort Client { get; set; }
        public string PlayerName { get; set; }
        public string Message { get; set; }

        private const float RetrySeconds = 0.25f;

        private RawImage bannerImage;

        private void Start()
        {
            try
            {
                SetText("PlayerName", PlayerName);
                SetText("PlayerMessage", Message);

                Transform bannerNode = transform.Find("PlayerBanner");
                if (bannerNode != null) bannerImage = bannerNode.GetComponent<RawImage>();
            }
            catch (Exception ex) { NetLog.Error("building chat row", ex); }

            // Retry until the banner resolves, then stop. Polling forever costs a busy lobby one
            // permanent 4Hz timer per chat line ever sent, and a chat line is a historical record
            //, once its banner is drawn it never changes again.
            Refresh();
            if (bannerImage != null && bannerImage.texture == null)
                InvokeRepeating(nameof(Refresh), RetrySeconds, RetrySeconds);
        }

        private void SetText(string node, string value)
        {
            Transform t = transform.Find(node);
            if (t == null) { NetLog.Warn("chat row prefab has no '" + node + "'"); return; }

            TextMeshProUGUI label = t.GetComponent<TextMeshProUGUI>();
            if (label != null) label.text = value ?? string.Empty;
        }

        /// <summary>Public because InvokeRepeating resolves it by name.</summary>
        public void Refresh()
        {
            try
            {
                SessionPlayer player;
                Texture banner = LobbyRowVisuals.Resolve(Client, out player);

                if (banner == null)
                {
                    // Author gone and never resolved, give up rather than retry forever.
                    if (player == null) CancelInvoke(nameof(Refresh));
                    return;
                }

                if (bannerImage != null) bannerImage.texture = banner;
                CancelInvoke(nameof(Refresh));
            }
            catch (Exception ex)
            {
                NetLog.Error("refreshing chat row banner", ex);
                CancelInvoke(nameof(Refresh));
            }
        }
    }

    /// <summary>
    /// A system notice in chat, joins, leaves. One line of text, set once; nothing about it
    /// changes afterwards, so there is nothing to refresh.
    /// </summary>
    public class NoticeRow : MonoBehaviour
    {
        public string Message { get; set; }

        private void Start()
        {
            try
            {
                Transform node = transform.Find("PlayerMessage");
                if (node == null)
                {
                    NetLog.Warn("notice row prefab has no 'PlayerMessage'");
                    return;
                }

                TextMeshProUGUI label = node.GetComponent<TextMeshProUGUI>();
                if (label != null) label.text = Message ?? string.Empty;
            }
            catch (Exception ex) { NetLog.Error("building notice row", ex); }
        }
    }
}
