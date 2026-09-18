using System;
using UnityEngine;

using KaCMultiplayer.Net;

namespace KaCMultiplayer
{
    // Password prompt shown when joining a server published as "locked". Built with IMGUI (OnGUI) so it
    // needs no prefab/Canvas wiring and always renders (it can never end up as invisible UI). Themed to
    // match the mod UI: dark blue-grey panel, a header bar, a muted subtitle, an inset masked field,
    // an accent "Join" button and a neutral "Cancel". (IMGUI can't use the game's TextMeshPro font, so
    // the typeface is the system one, colours/layout match; a pixel-perfect uGUI version is a later
    // option.) Drawn from Main.OnGUI; triggered from SteamLobby.HandleLobbyEntered when "locked" is set.
    public static class PasswordPrompt
    {
        private static bool active;
        private static string entered = "";
        private static Action<string> onSubmit;
        private static Action onCancel;
        private static bool focusRequested;

        public static bool IsActive { get { return active; } }

        public static void Request(Action<string> onSubmit, Action onCancel)
        {
            PasswordPrompt.onSubmit = onSubmit;
            PasswordPrompt.onCancel = onCancel;
            entered = "";
            active = true;
            focusRequested = true;
        }

        private static void Close() { active = false; entered = ""; }

        private static void Submit()
        {
            Action<string> cb = onSubmit;
            string pw = entered;
            Close();
            try { if (cb != null) cb(pw); } catch (Exception e) { Main.helper.Log("Password submit error: " + e.Message); }
        }

        private static void Cancel()
        {
            Action cb = onCancel;
            Close();
            try { if (cb != null) cb(); } catch (Exception e) { Main.helper.Log("Password cancel error: " + e.Message); }
        }

        // ---- Theme ----------------------------------------------------------------------------------
        private static readonly Color cOverlay = new Color(0f, 0f, 0f, 0.78f);
        private static readonly Color cBorder  = new Color(0.27f, 0.35f, 0.46f, 1f);
        private static readonly Color cPanel   = new Color(0.10f, 0.14f, 0.20f, 1f);
        private static readonly Color cHeader  = new Color(0.07f, 0.10f, 0.15f, 1f);
        private static readonly Color cField   = new Color(0.05f, 0.08f, 0.12f, 1f);
        private static readonly Color cText    = new Color(0.88f, 0.92f, 0.96f, 1f);
        private static readonly Color cMuted   = new Color(0.60f, 0.67f, 0.76f, 1f);
        private static readonly Color cAccent     = new Color(0.20f, 0.46f, 0.69f, 1f);
        private static readonly Color cAccentHi   = new Color(0.27f, 0.56f, 0.82f, 1f);
        private static readonly Color cNeutral    = new Color(0.18f, 0.23f, 0.31f, 1f);
        private static readonly Color cNeutralHi  = new Color(0.25f, 0.31f, 0.41f, 1f);

        private static bool ready;
        private static Texture2D txOverlay, txBorder, txPanel, txHeader, txField;
        private static GUIStyle stHeader, stLabel, stField, stAccent, stNeutral;

        private static Texture2D Tex(Color c)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        private static void EnsureStyles()
        {
            if (ready) return;
            ready = true;

            txOverlay = Tex(cOverlay);
            txBorder  = Tex(cBorder);
            txPanel   = Tex(cPanel);
            txHeader  = Tex(cHeader);
            txField   = Tex(cField);

            stHeader = new GUIStyle();
            stHeader.fontSize = 16;
            stHeader.fontStyle = FontStyle.Bold;
            stHeader.alignment = TextAnchor.MiddleLeft;
            stHeader.normal.textColor = cText;
            stHeader.padding = new RectOffset(16, 16, 0, 0);

            stLabel = new GUIStyle();
            stLabel.fontSize = 13;
            stLabel.alignment = TextAnchor.MiddleLeft;
            stLabel.normal.textColor = cMuted;

            stField = new GUIStyle();
            stField.fontSize = 15;
            stField.alignment = TextAnchor.MiddleLeft;
            stField.normal.textColor = cText;
            stField.normal.background = txField;
            stField.focused.textColor = cText;
            stField.focused.background = txField;
            stField.padding = new RectOffset(10, 10, 0, 0);
            stField.clipping = TextClipping.Clip;

            stAccent = MakeButton(cAccent, cAccentHi);
            stNeutral = MakeButton(cNeutral, cNeutralHi);
        }

        private static GUIStyle MakeButton(Color normal, Color hover)
        {
            GUIStyle s = new GUIStyle();
            s.fontSize = 14;
            s.fontStyle = FontStyle.Bold;
            s.alignment = TextAnchor.MiddleCenter;
            s.normal.textColor = cText;
            s.normal.background = Tex(normal);
            s.hover.textColor = Color.white;
            s.hover.background = Tex(hover);
            s.active.textColor = Color.white;
            s.active.background = Tex(hover);
            return s;
        }

        // Called every frame from Main.OnGUI; no-ops unless a prompt is active.
        public static void Draw()
        {
            if (!active) return;
            EnsureStyles();

            const float w = 400f, h = 200f;
            float x = (Screen.width - w) / 2f;
            float y = (Screen.height - h) / 2f;

            // Dim the whole screen so the dialog reads as modal.
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), txOverlay);

            // 1px border, then the panel on top.
            GUI.DrawTexture(new Rect(x - 1, y - 1, w + 2, h + 2), txBorder);
            GUI.DrawTexture(new Rect(x, y, w, h), txPanel);

            // Header bar + title.
            const float headerH = 44f;
            GUI.DrawTexture(new Rect(x, y, w, headerH), txHeader);
            GUI.Label(new Rect(x, y, w, headerH), "Server is Locked", stHeader);

            // Keyboard: Enter joins, Escape cancels.
            Event ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown)
            {
                if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter) { Submit(); return; }
                if (ev.keyCode == KeyCode.Escape) { Cancel(); return; }
            }

            float pad = 18f;
            float innerX = x + pad;
            float innerW = w - pad * 2f;

            GUI.Label(new Rect(innerX, y + headerH + 12, innerW, 20), "Enter the server password to join:", stLabel);

            GUI.SetNextControlName("kcm_pw_field");
            entered = GUI.PasswordField(new Rect(innerX, y + headerH + 38, innerW, 32), entered ?? "", '*', 64, stField);
            if (focusRequested) { GUI.FocusControl("kcm_pw_field"); focusRequested = false; }

            float btnY = y + h - 48f;
            float btnW = (innerW - 12f) / 2f;
            if (GUI.Button(new Rect(innerX, btnY, btnW, 34), "Join", stAccent)) Submit();
            if (GUI.Button(new Rect(innerX + btnW + 12f, btnY, btnW, 34), "Cancel", stNeutral)) Cancel();
        }
    }
}
