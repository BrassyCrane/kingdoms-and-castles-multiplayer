using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using KaCMultiplayer.Net;
using KaCMultiplayer;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// The mod's one message box, "Failed to connect", "Disconnected from the host", and
    /// similar. A single instance is built on first use and reused.
    ///
    /// Built lazily, and retried on failure. Doing the Unity instantiation in a <b>static
    /// constructor</b> would tie it to whichever code path first touches the type: if the asset
    /// bundle has not finished loading by then the constructor throws, and a failed static
    /// constructor poisons the type permanently, every later access throws
    /// <c>TypeInitializationException</c>, so no message can ever be shown again, including the
    /// "couldn't connect" one explaining why. A failed build here is logged and retried.
    ///
    /// It also carried an <c>instantiated</c> flag and threw "may only be instantiated once"
    /// in the else branch, unreachable, since a static constructor runs once by definition.
    /// </summary>
    public static class ModalDialog
    {
        private static GameObject root;
        private static TextMeshProUGUI title;
        private static TextMeshProUGUI body;
        private static Button button;

        // A message raised while the dialog cannot actually be seen, held until it can.
        private static string queuedTitle;
        private static string queuedBody;
        private static Action queuedDismiss;

        /// <summary>
        /// Shows a message as soon as it can actually be seen, queueing it until then.
        ///
        /// Needed because the dialog is parented under the main-menu UI, which is **inactive
        /// while a game is in progress**. <see cref="Show"/> would happily set it active and
        /// report success, an active object inside an inactive parent renders nothing, and
        /// nothing in the log says so. That is how a mid-game disconnect left the player sitting
        /// in a dead session with no explanation at all.
        ///
        /// Use this for anything raised from gameplay. <see cref="Show"/> is still right for
        /// messages raised from the menus, where the parent is up.
        /// </summary>
        public static void ShowWhenVisible(string titleText, string bodyText, Action onDismiss = null)
        {
            queuedTitle = titleText;
            queuedBody = bodyText;
            queuedDismiss = onDismiss;
            PumpQueued();
        }

        /// <summary>
        /// Delivers a queued message once the menu UI is actually up. Called every frame from
        /// Main.Update; cheap, and does nothing unless something is waiting.
        /// </summary>
        public static void PumpQueued()
        {
            if (queuedTitle == null) return;

            Transform parent = MenuUi.Root;
            if (parent == null || !parent.gameObject.activeInHierarchy) return;

            string t = queuedTitle, b = queuedBody;
            Action d = queuedDismiss;
            queuedTitle = null; queuedBody = null; queuedDismiss = null;

            Show(t, b, "Okay", true, d);
        }

        /// <summary>
        /// Creates the dialog if it isn't there yet. Returns false when the prefab or the menu
        /// UI isn't available, having said which.
        /// </summary>
        private static bool EnsureBuilt()
        {
            if (root != null) return true;

            if (LobbyPrefabs.Modal == null)
            {
                NetLog.Warn("modal prefab not loaded yet, dialog suppressed");
                return false;
            }

            Transform parent = MenuUi.Root;
            if (parent == null)
            {
                NetLog.Warn("menu UI not available yet, dialog suppressed");
                return false;
            }

            try
            {
                GameObject instance = UnityEngine.Object.Instantiate(LobbyPrefabs.Modal, parent);
                CanvasFit.Attach(instance);
                instance.SetActive(false);

                Transform t = instance.transform;
                title = FindText(t, "Modal/Container/Title");
                body = FindText(t, "Modal/Container/Description");

                Transform buttonNode = t.Find("Modal/Container/Button");
                button = buttonNode == null ? null : buttonNode.GetComponent<Button>();

                if (title == null || body == null || button == null)
                {
                    NetLog.Warn("modal prefab is missing expected nodes, dialog unavailable");
                    UnityEngine.Object.Destroy(instance);
                    return false;
                }

                root = instance;
                return true;
            }
            catch (Exception ex)
            {
                NetLog.Error("building the modal dialog", ex);
                return false;
            }
        }

        private static TextMeshProUGUI FindText(Transform under, string path)
        {
            Transform node = under.Find(path);
            if (node == null)
            {
                NetLog.Warn("modal prefab has no node at '" + path + "'");
                return null;
            }
            return node.GetComponent<TextMeshProUGUI>();
        }

        /// <summary>
        /// Shows a message. <paramref name="onDismiss"/> runs after the dialog closes.
        ///
        /// Suppressed silently, bar a log line, if the dialog can't be built. A caller
        /// reporting a connection failure should not itself fail.
        /// </summary>
        public static void Show(string titleText, string bodyText,
                                string buttonText = "Okay", bool withButton = true,
                                Action onDismiss = null)
        {
            if (!EnsureBuilt())
            {
                NetLog.Info("modal (not shown): " + titleText + ", " + bodyText);
                return;
            }

            try
            {
                title.text = titleText ?? string.Empty;
                body.text = bodyText ?? string.Empty;

                button.gameObject.SetActive(withButton);

                TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.text = buttonText;

                // Cleared each time: the listener captures this call's callback, and leaving
                // the previous one attached would fire every earlier dialog's action too.
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() =>
                {
                    root.SetActive(false);
                    if (onDismiss != null) onDismiss();
                });

                root.SetActive(true);
            }
            catch (Exception ex) { NetLog.Error("showing modal '" + titleText + "'", ex); }
        }

        /// <summary>Hides the dialog. Safe to call when it was never built.</summary>
        public static void Hide()
        {
            if (root != null) root.SetActive(false);
        }
    }
}
