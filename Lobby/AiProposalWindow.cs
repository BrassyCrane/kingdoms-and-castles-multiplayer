// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using KaCMultiplayer.Net;
using KaCMultiplayer.Net.Messages;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// An AI kingdom's envoy, on a guest's screen: "Aberdale demands 120 gold" / "Aberdale offers
    /// peace", with two answers. The game's own envoys only ever walk to the host's keep, so for a
    /// guest the host decides what the AI proposes (AiDiplomacy.OnSeasonChanged) and this is where
    /// the guest answers it.
    ///
    /// Paying happens HERE, on the guest's own machine, before the answer is sent: only this
    /// machine holds this kingdom's treasury for real. The host is told how much actually left.
    ///
    /// One at a time; any others wait their turn. Built from the same real dialog art as the other
    /// diplomacy popups (KacModalStyle), with a plain fallback.
    /// </summary>
    internal static class AiProposalWindow
    {
        private const int SortingOrder = 5085;

        private static GameObject canvas;
        private static TextMeshProUGUI titleText, bodyText;
        private static Button acceptButton, refuseButton;

        private static readonly Queue<AiProposalMessage> waiting = new Queue<AiProposalMessage>();
        private static AiProposalMessage current;

        /// <summary>
        /// Guest: an AI kingdom has proposed something (a demand, a gift, peace). Queued, so two
        /// arriving together are shown one after the other instead of one hiding the other.
        /// </summary>
        public static void Enqueue(AiProposalMessage m)
        {
            if (m == null) return;
            waiting.Enqueue(m);
            if (current == null) ShowNext();
        }

        /// <summary>Shows the next queued proposal, or hides the window when there are none.</summary>
        private static void ShowNext()
        {
            current = waiting.Count > 0 ? waiting.Dequeue() : null;

            if (current == null)
            {
                if (canvas != null) canvas.SetActive(false);
                return;
            }

            try
            {
                EnsureBuilt();
                if (canvas == null) return;

                titleText.text = current.Title ?? "";
                bodyText.text = current.Body ?? "";

                bool gold = current.Kind == (int)AiProposalKind.GoldDemand;
                KacModalStyle.SetLabel(acceptButton, gold ? ("Pay " + current.Amount + " gold") : "Accept peace");
                KacModalStyle.SetLabel(refuseButton, "Refuse");

                canvas.SetActive(true);
            }
            catch (System.Exception e) { NetLog.Error("showing an AI proposal", e); }
        }

        /// <summary>
        /// Sends this player's answer to the host, which runs the AI. Gold the AI demanded leaves this
        /// kingdom's treasury here first, because only this machine holds it for real.
        /// </summary>
        private static void Answer(bool accept)
        {
            AiProposalMessage p = current;
            if (p == null) return;

            try
            {
                int paid = 0;
                if (accept && p.Kind == (int)AiProposalKind.GoldDemand)
                {
                    LandmassOwner mine = Player.inst != null ? Player.inst.PlayerLandmassOwner : null;
                    paid = PlayerRelations.TakeFromStores(mine, FreeResourceType.Gold, p.Amount);
                }

                NetRouter.Send(new AiProposalAnswerMessage { ProposalId = p.ProposalId, Accept = accept, Paid = paid });
            }
            catch (System.Exception e) { NetLog.Error("answering an AI proposal", e); }

            ShowNext();
        }

        /// <summary>Drops the window and anything waiting, for the end of a session.</summary>
        public static void Reset()
        {
            waiting.Clear();
            current = null;
            if (canvas != null) Object.Destroy(canvas);
            canvas = null;
            titleText = bodyText = null;
            acceptButton = refuseButton = null;
        }

        /// <summary>Builds the proposal window once, on the game's own dialog art when it can be found.</summary>
        private static void EnsureBuilt()
        {
            if (canvas != null) return;

            canvas = new GameObject("AiProposal", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            Canvas c = canvas.GetComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = SortingOrder;

            CanvasScaler scaler = canvas.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = .5f;

            KacModalStyle.Panel panel = KacModalStyle.Build(canvas.transform);
            if (panel != null)
            {
                panel.Root.SetActive(true);
                titleText = panel.Title;
                bodyText = panel.Description;

                acceptButton = panel.Button;
                refuseButton = KacModalStyle.CloneButton(panel, "Refuse");
                if (refuseButton != null) KacModalStyle.SplitHorizontally(panel, acceptButton, refuseButton);
            }

            if (titleText == null || bodyText == null || acceptButton == null || refuseButton == null)
            {
                if (panel != null) Object.Destroy(panel.Root);
                BuildFallback();
            }

            KacModalStyle.SetClick(acceptButton, delegate { Answer(true); });
            KacModalStyle.SetClick(refuseButton, delegate { Answer(false); });

            canvas.SetActive(false);
        }

        /// <summary>A plain version of the window, for when the game's dialog art cannot be found.</summary>
        private static void BuildFallback()
        {
            GameObject panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(canvas.transform, false);
            RectTransform pr = panel.GetComponent<RectTransform>();
            pr.sizeDelta = new Vector2(600, 280);
            panel.GetComponent<Image>().color = new Color(.055f, .11f, .14f, .96f);

            titleText = Label(panel.transform, new Vector2(0, 90), new Vector2(560, 50), 30);
            titleText.color = new Color(0.95f, 0.75f, 0.35f);
            bodyText = Label(panel.transform, new Vector2(0, 10), new Vector2(560, 90), 22);

            acceptButton = Button(panel.transform, new Vector2(-130, -95));
            refuseButton = Button(panel.transform, new Vector2(130, -95));
        }

        /// <summary>A text label placed in the fallback window.</summary>
        private static TextMeshProUGUI Label(Transform parent, Vector2 pos, Vector2 size, float fontSize)
        {
            GameObject go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            TextMeshProUGUI t = go.AddComponent<TextMeshProUGUI>();
            t.fontSize = fontSize;
            t.alignment = TextAlignmentOptions.Center;
            t.richText = false;
            return t;
        }

        /// <summary>A button placed in the fallback window.</summary>
        private static Button Button(Transform parent, Vector2 pos)
        {
            GameObject go = new GameObject("Button", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(220, 46);
            Image img = go.GetComponent<Image>();
            img.color = new Color(.16f, .35f, .48f, 1);
            Button b = go.AddComponent<Button>();
            b.targetGraphic = img;
            TextMeshProUGUI label = Label(go.transform, Vector2.zero, new Vector2(210, 40), 20);
            label.text = "";
            return b;
        }
    }
}
