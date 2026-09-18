using TMPro;
using UnityEngine;
using UnityEngine.UI;

using KaCMultiplayer.Net;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// Turns each settings dropdown into a two-arrow stepper.
    ///
    /// TMP_Dropdown's popup does not render under this game's canvas. It is built correctly in
    /// every measurable respect, active in hierarchy, alpha 1, localScale (1,1,1), rect 320x130,
    /// items instantiated with the right text, viewport mask valid, own Canvas enabled with
    /// overrideSorting and sortingOrder 30000 on a matching sorting layer, screen corners
    /// comfortably inside 2048x1280, and it is never visible. Forcing alpha, matching the
    /// sorting layer, and reparenting it to the canvas root as the last sibling all failed. The
    /// host canvas is ScreenSpaceCamera inside the game's own multi-camera stack, and something
    /// there swallows a nested override-sorted canvas.
    ///
    /// So the popup is abandoned rather than fixed. The TMP_Dropdown component stays exactly
    /// where it was, because it is the contract: LobbyScreen resolves these paths with
    /// GetComponentInChildren&lt;TMP_Dropdown&gt;() and reads <c>.value</c>. The dropdown remains
    /// the model and the source of truth; only its popup is replaced by Prev/Next buttons that
    /// step the value. No settings logic changes, and the wire format is untouched.
    ///
    /// For two to four options this is the better control anyway: one click per change, nothing
    /// to mis-render, and it matches the game's own arrow steppers.
    ///
    /// The prefab cannot hold a listener pointing at mod code, so the buttons are wired here.
    /// </summary>
    public class DropdownCycler : MonoBehaviour
    {
        private TMP_Dropdown dropdown;
        private bool wired;

        private void Awake()
        {
            dropdown = GetComponent<TMP_Dropdown>();
            Wire();
        }

        private void Wire()
        {
            if (wired || dropdown == null) return;

            Button prev = FindButton("Prev");
            Button next = FindButton("Next");
            if (prev == null || next == null)
            {
                NetLog.Warn("[dd] " + name + ": no Prev/Next buttons, rebuild the prefabs " +
                            "(the bundle predates the stepper controls)");
                return;
            }

            wired = true;

            prev.onClick.RemoveAllListeners();
            next.onClick.RemoveAllListeners();
            prev.onClick.AddListener(delegate { Step(-1); });
            next.onClick.AddListener(delegate { Step(1); });
        }

        private Button FindButton(string childName)
        {
            Transform t = transform.Find(childName);
            return t == null ? null : t.GetComponent<Button>();
        }

        private void Step(int delta)
        {
            if (dropdown == null) return;

            int count = dropdown.options.Count;
            if (count <= 0) return;
            if (!dropdown.IsInteractable()) return;   // host-only settings on a client

            // Wrap in both directions. The double modulo keeps -1 landing on the last option
            // instead of a negative index.
            int next = ((dropdown.value + delta) % count + count) % count;

            // Assigning value fires onValueChanged, which is what the lobby already listens to.
            dropdown.value = next;
            dropdown.RefreshShownValue();
        }

        /// <summary>Attaches a cycler to every dropdown under a screen.</summary>
        public static void AttachAll(Transform screen)
        {
            if (screen == null) return;
            foreach (TMP_Dropdown d in screen.GetComponentsInChildren<TMP_Dropdown>(true))
                if (d.GetComponent<DropdownCycler>() == null)
                    d.gameObject.AddComponent<DropdownCycler>();
        }
    }
}
