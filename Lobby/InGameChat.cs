using System;
using System.Collections.Generic;
using UnityEngine;

using KaCMultiplayer.Net;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer
{
    /// <summary>
    /// Player chat during play, as opposed to in the lobby.
    ///
    /// Chat rows are instantiated into the lobby prefab's ChatContent, which is gone once the
    /// session starts, so a message sent mid-game reached the handler and then landed nowhere any
    /// player could see. System notices were moved to KingdomLog for that reason; player chat needs
    /// somewhere it can be typed as well as read, which KingdomLog cannot provide.
    ///
    /// Built with IMGUI for the same reason PasswordPrompt is: it needs no prefab, no Canvas wiring
    /// and no asset bundle, so it cannot end up as invisible UI, and it ships without rebuilding the
    /// bundle in Unity. A uGUI version using the game's own typeface is a later refinement.
    ///
    /// Quiet by default. With nothing said recently and the box closed it draws nothing at all, so a
    /// player who never uses it pays exactly one hidden key.
    /// </summary>
    public static class InGameChat
    {
        /// <summary>Opens the input box. Return is unbound during play; GameUI never reads it.</summary>
        private const KeyCode OpenKey = KeyCode.Return;
        private const KeyCode AltOpenKey = KeyCode.KeypadEnter;

        /// <summary>How long a message stays on screen once the box is closed.</summary>
        private const float FadeAfterSeconds = 14f;

        /// <summary>Kept in the backlog, which is only fully visible while the box is open.</summary>
        private const int MaxHistory = 60;

        /// <summary>Cap on a typed message, so nothing can be typed that will not send.</summary>
        private const int MaxLength = 240;

        private struct Line
        {
            public string Text;
            public float At;
            public bool Mine;
        }

        private static readonly List<Line> lines = new List<Line>();
        private static string typing = "";
        private static bool open;
        private static bool focusRequested;

        /// <summary>
        /// The frame the box was opened on, so the keystroke that OPENED it cannot also send it.
        ///
        /// Update and OnGUI both run within one frame: Tick sees the Return press and opens the box,
        /// then OnGUI runs for that same frame with the same Return still in Event.current, reads it
        /// as "send", and closes the box on an empty message. The visible result is that Enter does
        /// nothing at all, which reads exactly like chat being broken.
        /// </summary>
        private static int openedOnFrame = -1;

        /// <summary>
        /// True while the player is typing. Read by the hook that suppresses the game's own keyboard
        /// handling, so typing a letter into chat does not also trigger a building hotkey.
        /// </summary>
        public static bool Capturing { get { return open; } }

        /// <summary>Drops the backlog, for a new session.</summary>
        public static void Reset()
        {
            lines.Clear();
            typing = "";
            open = false;
        }

        /// <summary>
        /// Records a line for display. Called for every chat message including our own, because the
        /// sender receives its own relayed echo rather than adding locally; adding here as well
        /// would print everything the player says twice.
        /// </summary>
        public static void Add(string playerName, string text, bool mine)
        {
            if (string.IsNullOrEmpty(text)) return;

            lines.Add(new Line
            {
                Text = string.IsNullOrEmpty(playerName) ? text : playerName + ": " + text,
                At = Time.unscaledTime,
                Mine = mine
            });

            while (lines.Count > MaxHistory) lines.RemoveAt(0);
        }

        /// <summary>
        /// Handles the open and cancel keys. Called from Main.Update.
        ///
        /// Deliberately inactive in the lobby: the lobby has its own chat box, and two of them
        /// competing for Return would be worse than either alone.
        /// </summary>
        public static void Tick()
        {
            if (!InPlaySession())
            {
                if (open) { open = false; typing = ""; }
                return;
            }

            if (open)
            {
                if (Input.GetKeyDown(KeyCode.Escape)) { open = false; typing = ""; }
                return;   // Return while open is handled in Draw, where the field text is current
            }

            if (Input.GetKeyDown(OpenKey) || Input.GetKeyDown(AltOpenKey))
            {
                open = true;
                focusRequested = true;
                typing = "";
                openedOnFrame = Time.frameCount;
            }
        }

        /// <summary>Whether we are in a multiplayer session that has actually started.</summary>
        private static bool InPlaySession()
        {
            try
            {
                if (!NetClient.client.IsConnected) return false;
                return GameState.inst != null && GameState.inst.IsPlayMode();
            }
            catch { return false; }
        }

        // ---- Theme, matching PasswordPrompt so the mod surfaces look related ----------------------
        private static readonly Color cPanel = new Color(0.10f, 0.14f, 0.20f, 0.92f);
        private static readonly Color cField = new Color(0.05f, 0.08f, 0.12f, 1f);
        private static readonly Color cBorder = new Color(0.27f, 0.35f, 0.46f, 1f);
        private static readonly Color cText = new Color(0.88f, 0.92f, 0.96f, 1f);
        private static readonly Color cMine = new Color(0.62f, 0.82f, 0.98f, 1f);
        private static readonly Color cHint = new Color(0.60f, 0.67f, 0.76f, 1f);

        private static Texture2D solid;

        private static Texture2D Solid()
        {
            if (solid == null)
            {
                solid = new Texture2D(1, 1);
                solid.SetPixel(0, 0, Color.white);
                solid.Apply();
            }
            return solid;
        }

        private static void Box(Rect r, Color c)
        {
            Color was = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Solid());
            GUI.color = was;
        }

        /// <summary>Draws the backlog and, when open, the input box. Called from Main.OnGUI.</summary>
        public static void Draw()
        {
            if (!InPlaySession()) return;

            try
            {
                float now = Time.unscaledTime;

                const float width = 460f;
                const float lineH = 20f;
                float left = 16f;
                float inputY = Screen.height - 132f;

                // Everything while typing, only the recent while not.
                List<Line> shown = new List<Line>();
                for (int i = 0; i < lines.Count; i++)
                    if (open || now - lines[i].At < FadeAfterSeconds) shown.Add(lines[i]);

                int maxRows = open ? 12 : 6;
                if (shown.Count > maxRows) shown.RemoveRange(0, shown.Count - maxRows);

                if (shown.Count > 0)
                {
                    float h = shown.Count * lineH + 8f;
                    Rect back = new Rect(left, inputY - h - 4f, width, h);
                    if (open) Box(back, cPanel);

                    GUIStyle row = new GUIStyle(GUI.skin.label);
                    row.fontSize = 13;
                    row.wordWrap = false;

                    for (int i = 0; i < shown.Count; i++)
                    {
                        row.normal.textColor = shown[i].Mine ? cMine : cText;
                        GUI.Label(new Rect(left + 6f, back.y + 4f + i * lineH, width - 12f, lineH),
                                  shown[i].Text, row);
                    }
                }

                if (!open) return;

                Rect field = new Rect(left, inputY, width, 26f);
                Box(new Rect(field.x - 1f, field.y - 1f, field.width + 2f, field.height + 2f), cBorder);
                Box(field, cField);

                // Read Return BEFORE the text field consumes the event. A field swallows the
                // keystroke, so checking afterwards means the message is never sent.
                // Never on the frame the box opened: that Return is the one that opened it.
                bool send = Time.frameCount != openedOnFrame
                            && Event.current.type == EventType.KeyDown
                            && (Event.current.keyCode == KeyCode.Return
                                || Event.current.keyCode == KeyCode.KeypadEnter);

                GUI.SetNextControlName("kcmChatInput");

                GUIStyle entry = new GUIStyle(GUI.skin.textField);
                entry.normal.textColor = cText;
                entry.focused.textColor = cText;
                entry.fontSize = 13;

                typing = GUI.TextField(
                    new Rect(field.x + 4f, field.y + 3f, field.width - 8f, field.height - 6f),
                    typing, MaxLength, entry);

                GUIStyle hint = new GUIStyle(GUI.skin.label);
                hint.fontSize = 11;
                hint.normal.textColor = cHint;
                GUI.Label(new Rect(left, inputY + 28f, width, 16f), "Enter to send, Esc to cancel", hint);

                if (focusRequested)
                {
                    GUI.FocusControl("kcmChatInput");
                    focusRequested = false;
                }

                if (send)
                {
                    Event.current.Use();
                    Send();
                }
            }
            catch (Exception e)
            {
                // Never let a UI error take the frame down, and never leave the player stuck with
                // the game keyboard suppressed: close on the way out.
                open = false;
                Main.helper.Log("[CHAT] draw error: " + e.Message);
            }
        }

        /// <summary>
        /// Sends what was typed and closes the box.
        ///
        /// Nothing is added locally. ChatSay is relayed back to the sender as well, which is how the
        /// lobby box behaves, so adding here would print every message the player sends twice.
        /// </summary>
        private static void Send()
        {
            string text = (typing ?? "").Trim();
            open = false;
            typing = "";

            if (text.Length == 0) return;

            try
            {
                NetRouter.Send(new ChatSayMessage
                {
                    PlayerName = NetClient.inst != null ? NetClient.inst.Name : "",
                    Text = text
                });
            }
            catch (Exception e) { Main.helper.Log("[CHAT] send error: " + e.Message); }
        }
    }
}
