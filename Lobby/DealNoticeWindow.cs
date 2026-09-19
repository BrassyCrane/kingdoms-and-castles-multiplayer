using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// "Demand sent to Firereach: 100 Wood." One line, one button, gone when you click it.
    ///
    /// WHY THIS EXISTS. Sending a demand or a gift went out silently: click Demand, the picker
    /// closes, and the only sign anything happened was a line in the kingdom log easy to miss
    /// mid-game. The other side gets a full popup asking them to answer; the sender got nothing
    /// telling them their own click had landed. This is that acknowledgement, and the same one
    /// speaks again when the deal is resolved, for whichever side is not already looking at a
    /// popup that just told them the answer (the side that clicked Accept or Decline knows what
    /// they did; the side that sent the original request does not, until now).
    ///
    /// Built from the same requestui prefab as <see cref="AllianceRequestWindow"/> and
    /// <see cref="DealRequestWindow"/>, with the parts a notice has no use for hidden.
    /// </summary>
    internal static class DealNoticeWindow
    {
        private const int SortingOrder = 5080;

        private static GameObject canvas;
        private static TextMeshProUGUI titleText, bodyText;

        /// <summary>Shown when the local player's own demand or gift goes out.</summary>
        public static void ShowSent(string title, string body)
        {
            Show(title, body);
        }

        /// <summary>Shown to the proposer once the other side answers.</summary>
        public static void ShowResolved(string title, string body)
        {
            Show(title, body);
        }

        private static void Show(string title, string body)
        {
            try
            {
                EnsureBuilt();
                if (canvas == null) return;

                titleText.text = title ?? "";
                bodyText.text = body ?? "";
                canvas.SetActive(true);
            }
            catch (System.Exception e) { Net.NetLog.Error("showing a deal notice", e); }
        }

        public static void Close()
        {
            if (canvas != null) canvas.SetActive(false);
        }

        /// <summary>Drops the window entirely, for the end of a session.</summary>
        public static void Reset()
        {
            Close();
            if (canvas != null)
            {
                Object.Destroy(canvas);
                canvas = null;
                titleText = null;
                bodyText = null;
            }
        }

        /// <summary>
        /// Builds the notice from requestui: a title, one line, and Okay. The request's other
        /// parts are hidden, and the title and line move up into the space they leave.
        /// </summary>
        private static void EnsureBuilt()
        {
            if (canvas != null) return;

            canvas = Popup.Create("DealNotice", LobbyPrefabs.Request, SortingOrder);
            if (canvas == null) return;

            foreach (string part in new[] { "Window/Kind", "Window/Banner", "Window/Player", "Window/Note", "Window/Decline" })
            {
                RectTransform r = Popup.Find<RectTransform>(canvas, part);
                if (r != null) r.gameObject.SetActive(false);
            }

            titleText = Popup.Find<TextMeshProUGUI>(canvas, "Window/Kingdom");
            bodyText = Popup.Find<TextMeshProUGUI>(canvas, "Window/Body");

            titleText.rectTransform.anchoredPosition = new Vector2(28f, -24f);
            titleText.rectTransform.sizeDelta = new Vector2(504f, titleText.rectTransform.sizeDelta.y);
            bodyText.rectTransform.anchoredPosition = new Vector2(28f, -70f);
            Popup.Find<RectTransform>(canvas, "Window").sizeDelta = new Vector2(560f, 200f);

            Button ok = Popup.Find<Button>(canvas, "Window/Accept");
            Popup.SetLabel(ok, "Okay");
            Popup.OnClick(ok, Close);
        }
    }
}
