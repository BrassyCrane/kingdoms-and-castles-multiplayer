using System;
using I2.Loc;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

using KaCMultiplayer.Net;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// Adds a button to the game's main menu that looks like the game's own.
    ///
    /// There is no styling API to borrow, so the only way to match the menu is to clone an
    /// existing button and re-label it. "New Game" is the template.
    ///
    /// A factory method rather than a wrapper type: there is one call site, it needs the button
    /// once, and the two non-obvious steps below are easier to see stated plainly than buried in
    /// properties.
    /// </summary>
    public static class MenuButton
    {
        /// <summary>Path to the button whose styling we clone.</summary>
        private const string TemplatePath = "TopLevelUICanvas/TopLevel/Body/ButtonContainer/New";

        /// <summary>
        /// Clones the menu's New Game button, re-labels it, and wires up a click handler.
        /// Returns null if the template isn't there, which happens if a game update moves the
        /// menu, and is logged by <see cref="MenuUi.Find"/>.
        /// </summary>
        /// <param name="label">Visible text, also used as the object name.</param>
        /// <param name="onClick">Click handler.</param>
        /// <param name="siblingIndex">Position among its siblings; negative leaves it last.</param>
        public static Button Create(string label, UnityAction onClick, int siblingIndex = -1)
        {
            Transform template = MenuUi.Find(TemplatePath);
            if (template == null) return null;

            Button source = template.GetComponent<Button>();
            if (source == null)
            {
                NetLog.Warn("menu template at '" + TemplatePath + "' has no Button component");
                return null;
            }

            try
            {
                Button button = UnityEngine.Object.Instantiate(source, template.parent);
                button.name = label;

                // Strip the localisation components. They rewrite the label from a string
                // table on enable, so without this the button silently reverts to "New Game"
                // the next time the menu is shown.
                foreach (Localize loc in button.GetComponentsInChildren<Localize>())
                    UnityEngine.Object.Destroy(loc);

                TextMeshProUGUI text = button.GetComponentInChildren<TextMeshProUGUI>();
                if (text != null) text = SetText(text, label);

                // Replace the event rather than clearing listeners: the clone inherits the
                // template's handler, which would start a new single-player game.
                button.onClick = new Button.ButtonClickedEvent();
                if (onClick != null) button.onClick.AddListener(onClick);

                if (siblingIndex >= 0) button.transform.SetSiblingIndex(siblingIndex);

                return button;
            }
            catch (Exception ex)
            {
                NetLog.Error("creating menu button '" + label + "'", ex);
                return null;
            }
        }

        private static TextMeshProUGUI SetText(TextMeshProUGUI target, string value)
        {
            target.text = value;
            return target;
        }

        /// <summary>
        /// Removes one of the game's own menu buttons by path, for entries that make no sense
        /// in a multiplayer session.
        /// </summary>
        public static bool Remove(string path)
        {
            Transform found = MenuUi.Find(path);
            if (found == null) return false;

            UnityEngine.Object.Destroy(found.gameObject);
            return true;
        }
    }
}
