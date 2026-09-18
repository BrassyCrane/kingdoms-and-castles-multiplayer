using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using KaCMultiplayer.Net;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// Builds a popup from the mod's REAL dialog art instead of a hand-drawn rectangle.
    ///
    /// WHY THIS EXISTS. AllianceRequestWindow, DealRequestWindow and ResourcePicker each drew
    /// their own panel from flat <c>Image</c> rectangles with hand-picked colours, because
    /// <see cref="ModalDialog"/>'s real art is parented under <c>MenuUi.Root</c>, which is the
    /// main-menu UI and is switched off for the whole of play, the same reason each of those
    /// windows already builds its own overlay canvas. What actually ties ModalDialog's panel to
    /// that inactive parent is only WHERE it happens to be instantiated, not the prefab itself.
    /// This instantiates the identical prefab (<see cref="LobbyPrefabs.Modal"/>) under whichever
    /// canvas a caller has that IS visible during play, so the panel, its border and its button
    /// all come from the game's own art rather than a guess at its colours.
    ///
    /// EVERY LOOKUP IS OPTIONAL AND LOGGED. Nothing outside ModalDialog has ever walked this
    /// prefab's node paths before, so a bundle that does not match them exactly is a real
    /// possibility, not a hypothetical. A caller gets null back rather than an exception, and is
    /// expected to fall back to its own plain build when it does, exactly as before this existed.
    /// Nothing here can leave a window worse than it already was.
    /// </summary>
    internal static class KacModalStyle
    {
        /// <summary>A built instance and the pieces a caller customises.</summary>
        public class Panel
        {
            public GameObject Root;
            public RectTransform Container;
            public TextMeshProUGUI Title;
            public TextMeshProUGUI Description;

            /// <summary>
            /// The prefab's one real button, left in place as the first of however many a caller
            /// needs. Repurposing it (renaming, relabelling, rewiring its own onClick) is cheaper
            /// than cloning it and hiding the original, and leaves no orphan button behind.
            /// </summary>
            public Button Button;
        }

        /// <summary>
        /// Instantiates the real modal prefab under <paramref name="parent"/>. Null, having
        /// logged why, if the prefab is not loaded or any expected node is missing.
        /// </summary>
        public static Panel Build(Transform parent)
        {
            try
            {
                if (LobbyPrefabs.Modal == null) return null;

                GameObject instance = UnityEngine.Object.Instantiate(LobbyPrefabs.Modal, parent);

                Transform t = instance.transform;
                Transform containerT = t.Find("Modal/Container");
                Transform titleT = t.Find("Modal/Container/Title");
                Transform descT = t.Find("Modal/Container/Description");
                Transform buttonT = t.Find("Modal/Container/Button");

                RectTransform container = containerT == null ? null : containerT.GetComponent<RectTransform>();
                TextMeshProUGUI title = titleT == null ? null : titleT.GetComponent<TextMeshProUGUI>();
                TextMeshProUGUI desc = descT == null ? null : descT.GetComponent<TextMeshProUGUI>();
                Button button = buttonT == null ? null : buttonT.GetComponent<Button>();

                if (container == null || title == null || desc == null || button == null)
                {
                    NetLog.Warn("K&C-styled popup: modal prefab is missing an expected node "
                                + "(container=" + (container != null) + " title=" + (title != null)
                                + " desc=" + (desc != null) + " button=" + (button != null)
                                + "); falling back to the plain style");
                    UnityEngine.Object.Destroy(instance);
                    return null;
                }

                // Player-typed strings (a kingdom name, a Steam persona) end up in these fields,
                // and TextMeshPro's rich text is on by default on an authored prefab. Off here,
                // once, so every caller gets the same protection the old hand-built Label()
                // helpers gave for the same reason: a name containing something that looks like a
                // tag must appear as itself, not be interpreted as markup.
                title.richText = false;
                desc.richText = false;

                instance.SetActive(false);

                return new Panel
                {
                    Root = instance,
                    Container = container,
                    Title = title,
                    Description = desc,
                    Button = button
                };
            }
            catch (Exception e)
            {
                NetLog.Error("building a K&C-styled popup", e);
                return null;
            }
        }

        /// <summary>
        /// Clones the panel's button so a second action can share its real art. The original stays
        /// where it is; use <see cref="SplitHorizontally"/> afterwards to lay the two out side by
        /// side.
        ///
        /// <paramref name="reparentTo"/> is for a caller that only wants the button's ART, not the
        /// rest of the panel it came from (see ResourcePicker): the clone is built under the
        /// template's own parent, same as ever, then moved. Left null it stays where a normal
        /// second button belongs, alongside the first.
        /// </summary>
        public static Button CloneButton(Panel panel, string name, Transform reparentTo = null)
        {
            try
            {
                GameObject clone = UnityEngine.Object.Instantiate(
                    panel.Button.gameObject, panel.Button.transform.parent);
                clone.name = name;
                if (reparentTo != null) clone.transform.SetParent(reparentTo, false);

                // Explicitly: Instantiate copies the template's own active state, and cloning a
                // button that has ever been hidden gives a hidden clone that exists, is wired up,
                // and cannot be seen. The same trap this mod hit once already, in
                // DiplomacyWindow.CloneRowButton.
                clone.SetActive(true);

                return clone.GetComponent<Button>();
            }
            catch (Exception e)
            {
                NetLog.Error("cloning a K&C-styled popup button", e);
                return null;
            }
        }

        /// <summary>
        /// Splits two buttons left and right around the template button's own position, by a
        /// fraction of the panel's actual width rather than a guessed pixel value, so the layout
        /// scales with whatever size the real prefab turns out to be.
        /// </summary>
        public static void SplitHorizontally(Panel panel, Button left, Button right)
        {
            try
            {
                float width = panel.Container != null ? panel.Container.rect.width : 0f;
                float halfGap = Mathf.Clamp(width * 0.27f, 90f, 220f);

                RectTransform lr = left.GetComponent<RectTransform>();
                RectTransform rr = right.GetComponent<RectTransform>();
                Vector2 basePos = lr.anchoredPosition;

                lr.anchoredPosition = basePos + new Vector2(-halfGap, 0f);
                rr.anchoredPosition = basePos + new Vector2(halfGap, 0f);
            }
            catch (Exception e) { NetLog.Error("laying out K&C-styled popup buttons", e); }
        }

        /// <summary>Sets a button's label, if it has one.</summary>
        public static void SetLabel(Button b, string text)
        {
            if (b == null) return;
            TextMeshProUGUI label = b.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = text;
        }

        /// <summary>Wires a button's click, replacing whatever it was already wired to.</summary>
        public static void SetClick(Button b, UnityEngine.Events.UnityAction onClick)
        {
            if (b == null) return;
            b.onClick.RemoveAllListeners();
            b.onClick.AddListener(onClick);
        }
    }
}
