using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

using KaCMultiplayer.Net;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// Opens one of the bundle's in-game popups (requestui, pickerui, exportpricesui) on its own
    /// overlay canvas.
    ///
    /// Its own canvas because the game's menu UI, where ModalDialog lives, is switched off during
    /// play, so anything parented there is built and never drawn. Each popup gets a fixed sorting
    /// order so two arriving together always stack the same way.
    /// </summary>
    internal static class Popup
    {
        /// <summary>
        /// Builds the canvas and the prefab under it, hidden. Null, logged, if the prefab is
        /// missing. The canvas is the popup's one on/off switch.
        /// </summary>
        public static GameObject Create(string name, GameObject prefab, int sortingOrder)
        {
            if (prefab == null)
            {
                NetLog.Warn(name + ": the bundle has no prefab for this popup");
                return null;
            }

            GameObject canvas = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas c = canvas.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = sortingOrder;

            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            // Buttons need an EventSystem, and the game does not always have one up.
            if (Object.FindObjectOfType<EventSystem>() == null)
            {
                GameObject es = new GameObject("KcmEventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                es.transform.SetParent(canvas.transform, false);
            }

            Object.Instantiate(prefab, canvas.transform);

            // Kingdom and player names are typed by players and reach these labels, so a name
            // that looks like a tag must show as itself rather than as markup.
            foreach (TextMeshProUGUI t in canvas.GetComponentsInChildren<TextMeshProUGUI>(true))
                t.richText = false;

            canvas.SetActive(false);
            return canvas;
        }

        /// <summary>A component at a path under the popup's prefab, or null.</summary>
        public static T Find<T>(GameObject canvas, string path) where T : Component
        {
            Transform t = canvas.transform.GetChild(0).Find(path);
            return t == null ? null : t.GetComponent<T>();
        }

        public static void OnClick(Button b, UnityAction action)
        {
            if (b == null) return;
            b.onClick.RemoveAllListeners();
            b.onClick.AddListener(action);
        }

        public static void SetLabel(Button b, string text)
        {
            if (b == null) return;
            TextMeshProUGUI label = b.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = text;
        }

        /// <summary>Clones a template option (hidden in the prefab) and shows the copy.</summary>
        public static Button AddOption(GameObject template, string label, UnityAction onClick)
        {
            GameObject o = Object.Instantiate(template, template.transform.parent);
            o.SetActive(true);
            Button b = o.GetComponent<Button>();
            SetLabel(b, label);
            OnClick(b, onClick);
            return b;
        }

        /// <summary>Shows or hides a picker option's selected outline.</summary>
        public static void Select(Button option, bool selected)
        {
            if (option == null) return;
            Transform mark = option.transform.Find("Selected");
            if (mark != null) mark.gameObject.SetActive(selected);
        }
    }
}
