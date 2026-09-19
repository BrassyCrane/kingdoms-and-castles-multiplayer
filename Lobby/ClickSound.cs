using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// Plays the game's own select sound when a button is clicked, the way its menu buttons do.
    ///
    /// Added to every button in the mod's prefabs when the bundle loads (see LobbyPrefabs), so a
    /// button wired anywhere in the code sounds like the game's without each handler having to
    /// remember to. The game has no hover sound, so there is none here either.
    /// </summary>
    internal class ClickSound : MonoBehaviour, IPointerClickHandler
    {
        public void OnPointerClick(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left) return;

            Button b = GetComponent<Button>();
            if (b != null && b.IsInteractable()) SfxSystem.PlayUiSelect();
        }

        /// <summary>Adds the sound to every button under a prefab, once.</summary>
        public static void AddTo(GameObject prefab)
        {
            if (prefab == null) return;
            foreach (Button b in prefab.GetComponentsInChildren<Button>(true))
                if (b.GetComponent<ClickSound>() == null) b.gameObject.AddComponent<ClickSound>();
        }
    }
}
