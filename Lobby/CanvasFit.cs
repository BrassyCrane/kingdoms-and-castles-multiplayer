using UnityEngine;

using KaCMultiplayer.Net;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// Scales a screen's fixed-size design surfaces to fit whatever canvas the game gives us.
    ///
    /// The UI prefabs are authored against a notional 1920x1080 screen, with the panel a fixed
    /// 1600x860 box inside it. But they are instantiated under a copy of the game's own
    /// top-level canvas, so the game's Canvas Scaler decides how many *units* wide the screen
    /// is, and that number is not 1920. Measured in-game it came out around 1150.
    ///
    /// Three approaches were rejected before this one:
    ///
    /// - Authoring against the real number. It is a build-time constant, so it would be correct
    ///   at exactly one resolution. If the game scales by constant pixel size, canvas units are
    ///   screen pixels and the constant is wrong the moment anyone changes resolution.
    /// - Putting our own CanvasScaler on the canvas copy. Unity ignores CanvasScaler on a
    ///   nested canvas, and this canvas is parented under the main menu UI, so we cannot rely
    ///   on being a root canvas.
    /// - Fractional anchors everywhere. Fixes column positions but not font sizes or control
    ///   heights, which are absolute.
    ///
    /// So it is computed here, from the canvas's actual rect, and recomputed whenever the
    /// window changes size. Min() of the two ratios rather than width alone, so the design
    /// area fits on both axes and nothing overflows on ultrawide or 4:3.
    /// </summary>
    public class CanvasFit : MonoBehaviour
    {
        /// <summary>Paths, from the screen prefab's root, of the fixed-size surfaces.</summary>
        private static readonly string[] Surfaces =
        {
            "Container",
            "Modal",
            "LoadingSave/Window",
        };

        private int lastWidth;
        private int lastHeight;
        private bool logged;

        private void OnEnable()
        {
            Apply();
        }

        private void Update()
        {
            // Cheap guard: two int compares per frame, and resolution changes are rare.
            if (Screen.width == lastWidth && Screen.height == lastHeight) return;
            Apply();
        }

        public void Apply()
        {
            lastWidth = Screen.width;
            lastHeight = Screen.height;

            try
            {
                Canvas canvas = GetComponentInParent<Canvas>();
                if (canvas == null) return;

                RectTransform canvasRect = canvas.transform as RectTransform;
                if (canvasRect == null) return;

                float cw = canvasRect.rect.width;
                float ch = canvasRect.rect.height;
                if (cw <= 1f || ch <= 1f) return;   // not laid out yet

                float scale = Mathf.Min(cw / UiScale.DesignWidth, ch / UiScale.DesignHeight);
                if (scale <= 0f || float.IsNaN(scale) || float.IsInfinity(scale)) return;

                int applied = 0;
                for (int i = 0; i < Surfaces.Length; i++)
                {
                    Transform t = transform.Find(Surfaces[i]);
                    if (t == null) continue;
                    t.localScale = new Vector3(scale, scale, 1f);
                    applied++;
                }

                // Once per screen, not every resize, enough to diagnose a scaling problem
                // without filling the log.
                if (!logged)
                {
                    logged = true;
                    NetLog.Info(string.Format(
                        "[canvas] {0}: canvas {1:0.#}x{2:0.#} units, screen {3}x{4}, scale {5:0.####}, {6} surface(s)",
                        name, cw, ch, Screen.width, Screen.height, scale, applied));
                }
            }
            catch (System.Exception e)
            {
                NetLog.Warn("CanvasFit failed on " + name + ": " + e.Message);
            }
        }

        /// <summary>
        /// Attaches a fitter to a freshly instantiated screen and applies it immediately, so
        /// the first frame is already the right size.
        /// </summary>
        public static void Attach(GameObject screen)
        {
            if (screen == null) return;
            CanvasFit fit = screen.GetComponent<CanvasFit>();
            if (fit == null) fit = screen.AddComponent<CanvasFit>();
            fit.Apply();
        }
    }

    /// <summary>The design area the UI prefabs are authored against.</summary>
    public static class UiScale
    {
        public const float DesignWidth = 1920f;
        public const float DesignHeight = 1080f;
    }
}
