// Kingdoms and Castles Multiplayer. Copyright (c) BrassyCrane and contributors.
// All rights reserved, not open source: see LICENSE.md for what you may do with this code.

using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using KaCMultiplayer.Net;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// The lobby's AI kingdom controls: a "- AI Kingdoms: n/max + Add AI" strip at the bottom of
    /// the Players column, and one row per AI kingdom in the player list itself, its real name
    /// with "(AI)", its banner, and its own difficulty stepper.
    ///
    /// Built in code rather than bound from a prefab node: the lobby prefab has never had AI
    /// controls (AI kingdoms have never worked in multiplayer before now). The player list's
    /// ScrollRect fills the whole column down to the Back button, so the list is shortened by the
    /// strip's height and the strip takes the freed space, the same arrangement the Chat column
    /// uses for its input box. AI rows are clones of the same PlayerEntry prefab human rows use,
    /// kept after the human rows so they read as "under the players".
    ///
    /// Art and font are borrowed from the lobby's own Start button so everything reads as part of
    /// the same screen.
    ///
    /// HOST CAN TOUCH IT, A GUEST CAN ONLY WATCH IT. Only the host's copy picks rival codes and
    /// clamps the count; a guest displays exactly what the host last sent.
    ///
    /// Only edits LobbySettings.Current. Turning that into actual AI kingdoms at world generation
    /// is Main.ApplyAiKingdomConfig's job, not this file's.
    /// </summary>
    public static class AiKingdomControl
    {
        private const float RowHeight = 34f;
        private const float Gap = 8f;

        internal static readonly string[] DifficultyNames = { "Low", "Medium", "High", "Very High" };
        internal static readonly Color[] DifficultyColours =
        {
            new Color(0.55f, 0.85f, 0.55f, 1f),
            new Color(0.95f, 0.85f, 0.40f, 1f),
            new Color(0.95f, 0.60f, 0.30f, 1f),
            new Color(0.95f, 0.40f, 0.35f, 1f),
        };

        private static GameObject root;

        /// <summary>The lobby panel the controls live in; the AI editor opens over it.</summary>
        internal static Transform LobbyContainer;

        private static TextMeshProUGUI countLabel;
        private static Button addButton, removeButton;
        private static readonly List<AiRow> rows = new List<AiRow>();

        private static Image styleImage;
        private static Button styleButton;
        private static TextMeshProUGUI styleText;

        internal static readonly Color cText = new Color(0.88f, 0.92f, 0.96f, 1f);
        private static readonly Color cLabel = new Color(0.95f, 0.75f, 0.35f, 1f);
        private static readonly Color cButton = new Color(0.16f, 0.22f, 0.30f, 1f);

        /// <summary>
        /// Builds the AI kingdom strip under the lobby's player list, once, styled like the Start
        /// button it is given. Built in code because the lobby prefab has no AI controls.
        /// </summary>
        public static void EnsureBuilt(Button styleSource)
        {
            if (root != null) return;

            try
            {
                if (LobbyScreen.RosterContent == null) return;

                ScrollRect scrollRect = LobbyScreen.RosterContent.GetComponentInParent<ScrollRect>();
                Transform playerList = scrollRect != null
                    ? scrollRect.transform
                    : (LobbyScreen.RosterContent.parent != null ? LobbyScreen.RosterContent.parent.parent : null);
                if (playerList == null || playerList.parent == null) return;

                RectTransform listRect = playerList.GetComponent<RectTransform>();
                if (listRect == null) return;

                if (styleSource != null)
                {
                    styleButton = styleSource;
                    styleImage = styleSource.GetComponent<Image>();
                    styleText = styleSource.GetComponentInChildren<TextMeshProUGUI>(true);
                }

                LobbyContainer = playerList.parent;

                root = new GameObject("AiKingdomControl");
                root.transform.SetParent(playerList.parent, false);

                // Occupy the bottom strip of where the list is now: same horizontal anchors and
                // offsets, vertical anchors pinned to the list's bottom anchor.
                RectTransform rootRect = root.AddComponent<RectTransform>();
                rootRect.anchorMin = new Vector2(listRect.anchorMin.x, listRect.anchorMin.y);
                rootRect.anchorMax = new Vector2(listRect.anchorMax.x, listRect.anchorMin.y);
                rootRect.pivot = new Vector2(0.5f, 0f);
                rootRect.offsetMin = new Vector2(listRect.offsetMin.x, listRect.offsetMin.y);
                rootRect.offsetMax = new Vector2(listRect.offsetMax.x, listRect.offsetMin.y + RowHeight);

                // Then lift the list's bottom edge clear of it.
                listRect.offsetMin = new Vector2(listRect.offsetMin.x, listRect.offsetMin.y + RowHeight + Gap);

                HorizontalLayoutGroup hlg = root.AddComponent<HorizontalLayoutGroup>();
                hlg.spacing = 6f;
                hlg.childControlWidth = true;
                hlg.childControlHeight = true;
                hlg.childForceExpandWidth = false;
                hlg.childForceExpandHeight = true;

                removeButton = NewButton("Remove", root.transform, "-", RowHeight);
                countLabel = NewLabel("Count", root.transform, "AI Kingdoms: 0", cLabel);
                addButton = NewButton("Add", root.transform, "+ Add AI", 110f);

                addButton.onClick.AddListener(() =>
                {
                    LobbySettings s = LobbySettings.Current;
                    if (s.AiKingdomCount >= MaxForMap()) return;
                    s.AiKingdomCount++;
                    s.AiCodes[s.AiKingdomCount - 1] = -1;   // pick a fresh code for the new slot
                    s.AiNames[s.AiKingdomCount - 1] = "";   // and the game's own name for it
                    Refresh(true);
                });
                removeButton.onClick.AddListener(() =>
                {
                    LobbySettings s = LobbySettings.Current;
                    if (s.AiKingdomCount <= 0) return;
                    s.AiKingdomCount--;
                    AiKingdomEditor.CloseIfSlotGone(s.AiKingdomCount);
                    Refresh(true);
                });

                Refresh(false);
                Main.helper.Log("AiKingdomControl built under " + playerList.parent.name);
            }
            catch (Exception e) { Main.helper.Log("AiKingdomControl build error: " + e.Message); }
        }

        /// <summary>
        /// Most AI kingdoms this map can seat: one kingdom per island, and the player seats come
        /// first. Before a map exists, the full AI range.
        /// </summary>
        public static int MaxForMap()
        {
            try
            {
                int islands = World.inst == null ? 0 : World.inst.NumLandMasses;
                if (islands < LobbySettings.MinPlayers) return LobbySettings.MaxAiKingdoms;
                return Mathf.Clamp(islands - LobbySettings.Current.MaxPlayers, 0, LobbySettings.MaxAiKingdoms);
            }
            catch { return LobbySettings.MaxAiKingdoms; }
        }

        /// <summary>
        /// Reflects LobbySettings.Current onto the controls. <paramref name="interactable"/> is
        /// true only for the host while the world is editable; only then does this also clamp the
        /// count to the map and pick rival codes, so a guest never rewrites what the host sent.
        /// </summary>
        public static void Refresh(bool interactable)
        {
            if (root == null) return;

            try
            {
                LobbySettings s = LobbySettings.Current;
                int max = MaxForMap();

                if (interactable)
                {
                    if (s.AiKingdomCount > max) s.AiKingdomCount = max;
                    if (s.AiKingdomCount < 0) s.AiKingdomCount = 0;
                    AssignCodes(s);
                }

                int count = Mathf.Clamp(s.AiKingdomCount, 0, LobbySettings.MaxAiKingdoms);

                // A loaded save can hold more AI kingdoms than a new game on this map would seat;
                // then the count stands alone rather than reading "3/1".
                if (countLabel != null)
                    countLabel.text = "AI Kingdoms: " + count + (count > max ? "" : "/" + max);
                if (addButton != null) addButton.interactable = interactable && count < max;
                if (removeButton != null) removeButton.interactable = interactable && count > 0;

                SyncRows(count, interactable);

                if (!interactable) AiKingdomEditor.Close();
                else AiKingdomEditor.RefreshIfOpen();
            }
            catch (Exception e) { Main.helper.Log("AiKingdomControl refresh error: " + e.Message); }
        }

        /// <summary>
        /// Host, in a load-a-save lobby: shows the saved world's own AI kingdoms in the player
        /// list. The save has already been unpacked by the time the lobby is up (the same moment
        /// its difficulty is adopted, see SessionSave.Unpack), so the kingdoms are in memory with
        /// their banner, skill and island name; this copies them into the lobby's AI slots, which
        /// the lobby then broadcasts, so guests see the same rows.
        ///
        /// Read-only: the controls stay disabled while a save is loaded (the save decides its AI
        /// kingdoms, the same way it decides the map), and nothing here changes the kingdoms
        /// themselves, a loaded world never places AI again (AIKingdomPlacementHook sees them
        /// already there).
        /// </summary>
        public static void AdoptLoadedWorld(LobbySettings s)
        {
            try
            {
                List<AIKingdom> kingdoms = AiDiplomacy.Kingdoms();
                int count = Mathf.Min(kingdoms.Count, LobbySettings.MaxAiKingdoms);

                for (int i = 0; i < count; i++)
                {
                    AIKingdom k = kingdoms[i];
                    s.AiCodes[i] = k.LandmassOwner.bannerIdx;
                    s.AiDifficulties[i] = Mathf.Clamp((int)k.skillLevel, 0, DifficultyNames.Length - 1);

                    // The island carries "Name (AI)"; the row adds " (AI)" itself.
                    string name = AiDiplomacy.NameFor(k.LandmassOwner.teamId) ?? "";
                    if (name.EndsWith(" (AI)")) name = name.Substring(0, name.Length - 5);
                    if (name.Length > LobbySettings.MaxAiNameLength) name = name.Substring(0, LobbySettings.MaxAiNameLength);
                    s.AiNames[i] = name;
                }

                s.AiKingdomCount = count;
            }
            catch (Exception e) { Main.helper.Log("AiKingdomControl: reading the saved world's AI kingdoms failed: " + e.Message); }
        }

        /// <summary>
        /// Gives every active AI slot a rival code no human banner or other AI is using, the way
        /// vanilla's own RivalKingdomSettingsUI.FindUnusedRivalCode does. A slot keeps its code
        /// unless a human has since picked that banner.
        /// </summary>
        private static void AssignCodes(LobbySettings s)
        {
            int limit = CodeLimit();
            if (limit <= 0) return;

            var humanBanners = new HashSet<int>();
            foreach (SessionPlayer p in Main.kCPlayers.Values)
                if (p != null && !p.isAI && p.banner >= 0) humanBanners.Add(p.banner);

            var taken = new HashSet<int>(humanBanners);
            for (int i = 0; i < s.AiKingdomCount; i++)
            {
                int code = s.AiCodes[i];
                bool keep = code >= 0 && code < limit && !taken.Contains(code);
                if (!keep)
                {
                    code = -1;
                    for (int c = 0; c < limit; c++)
                        if (!taken.Contains(c)) { code = c; break; }
                    if (code < 0) code = i % limit;   // more kingdoms than banners: share one
                    s.AiCodes[i] = code;
                }
                taken.Add(code);
            }
        }

        /// <summary>
        /// The next rival code after <paramref name="slot"/>'s current one, stepping by
        /// <paramref name="direction"/>, skipping every code a human banner or another AI slot
        /// holds. The slot's current code if nothing else is free.
        /// </summary>
        internal static int NextFreeCode(int slot, int direction)
        {
            LobbySettings s = LobbySettings.Current;
            int limit = CodeLimit();
            int current = s.AiCodes[slot];
            if (limit <= 0) return current;

            var taken = new HashSet<int>();
            foreach (SessionPlayer p in Main.kCPlayers.Values)
                if (p != null && !p.isAI && p.banner >= 0) taken.Add(p.banner);
            for (int i = 0; i < s.AiKingdomCount; i++)
                if (i != slot && s.AiCodes[i] >= 0) taken.Add(s.AiCodes[i]);

            int start = current >= 0 ? current : 0;
            for (int step = 1; step <= limit; step++)
            {
                int c = ((start + direction * step) % limit + limit) % limit;
                if (!taken.Contains(c)) return c;
            }
            return current;
        }

        /// <summary>
        /// How many rival codes this game can use: the smaller of its livery sets and its AI name pool,
        /// since one code picks both a banner and a name.
        /// </summary>
        internal static int CodeLimit()
        {
            int limit = (World.inst != null && World.inst.liverySets != null) ? World.inst.liverySets.Count : 0;
            string[] pool = AIBrainsContainer.inst != null ? AIBrainsContainer.inst.AIKingdomNamePool : null;
            if (pool != null && pool.Length < limit) limit = pool.Length;
            return limit;
        }

        /// <summary>The name an AI slot shows: the one the host typed, or the game's own for its rival code.</summary>
        internal static string NameFor(int slot, int code)
        {
            string custom = LobbySettings.Current.AiNames[slot];
            if (!string.IsNullOrEmpty(custom) && custom.Trim().Length > 0) return custom.Trim();
            return DefaultNameFor(slot, code);
        }

        /// <summary>The game's own name for a rival code, the one vanilla gives that kingdom.</summary>
        internal static string DefaultNameFor(int slot, int code)
        {
            string[] pool = AIBrainsContainer.inst != null ? AIBrainsContainer.inst.AIKingdomNamePool : null;
            if (pool != null && code >= 0 && code < pool.Length && !string.IsNullOrEmpty(pool[code]))
                return pool[code];
            return "AI Kingdom " + (slot + 1);
        }

        /// <summary>The banner for a rival code, or null if this game has no such livery.</summary>
        internal static Texture BannerFor(int code)
        {
            var sets = World.inst == null ? null : World.inst.liverySets;
            if (sets == null || code < 0 || code >= sets.Count) return null;
            return sets[code].banners;
        }

        /// <summary>
        /// Keeps exactly <paramref name="count"/> AI rows in the player list, after every human
        /// row. Re-sorted on each tick because a human who joins is appended at the end.
        /// </summary>
        private static void SyncRows(int count, bool interactable)
        {
            Transform content = LobbyScreen.RosterContent;

            for (int i = rows.Count - 1; i >= 0; i--)
            {
                if (rows[i] == null || rows[i].transform.parent != content)
                {
                    if (rows[i] != null) UnityEngine.Object.Destroy(rows[i].gameObject);
                    rows.RemoveAt(i);
                }
            }

            if (content == null) return;

            while (rows.Count > count)
            {
                AiRow last = rows[rows.Count - 1];
                rows.RemoveAt(rows.Count - 1);
                if (last != null) UnityEngine.Object.Destroy(last.gameObject);
            }

            while (rows.Count < count)
            {
                GameObject row = UnityEngine.Object.Instantiate(LobbyPrefabs.PlayerEntry, content);
                row.name = "AiRow" + rows.Count;
                row.SetActive(true);
                AiRow script = row.AddComponent<AiRow>();
                script.Build(rows.Count);
                rows.Add(script);
            }

            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].transform.SetAsLastSibling();
                rows[i].Refresh(interactable);
            }
        }

        /// <summary>An empty horizontal row for the lobby controls.</summary>
        internal static GameObject NewRow(string name, Transform parent)
        {
            GameObject row = new GameObject(name);
            row.transform.SetParent(parent, false);
            row.AddComponent<RectTransform>();

            HorizontalLayoutGroup hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 2f;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;

            return row;
        }

        /// <summary>A text label in the lobby's own font and size.</summary>
        internal static TextMeshProUGUI NewLabel(string name, Transform parent, string text, Color color)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            obj.AddComponent<RectTransform>();
            LayoutElement le = obj.AddComponent<LayoutElement>();
            le.flexibleWidth = 1f;

            TextMeshProUGUI tmp = obj.AddComponent<TextMeshProUGUI>();
            ApplyFont(tmp);
            tmp.text = text;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Left;
            tmp.raycastTarget = false;
            return tmp;
        }

        /// <summary>A button with the lobby's own art, copied from the Start button when there is one.</summary>
        internal static Button NewButton(string name, Transform parent, string label, float width)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            obj.AddComponent<RectTransform>();
            LayoutElement le = obj.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minWidth = width;

            Image img = obj.AddComponent<Image>();
            if (styleImage != null && styleImage.sprite != null)
            {
                img.sprite = styleImage.sprite;
                img.type = styleImage.type;
                img.color = styleImage.color;
            }
            else
            {
                img.color = cButton;
            }

            Button btn = obj.AddComponent<Button>();
            btn.targetGraphic = img;
            if (styleButton != null)
            {
                btn.transition = styleButton.transition;
                btn.colors = styleButton.colors;
            }

            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(obj.transform, false);
            RectTransform textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(2f, 0f);
            textRect.offsetMax = new Vector2(-2f, 0f);

            TextMeshProUGUI tmp = textObj.AddComponent<TextMeshProUGUI>();
            ApplyFont(tmp);
            tmp.text = label;
            tmp.color = styleText != null ? styleText.color : cText;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;

            return btn;
        }

        /// <summary>The Start button's own TMP font, auto-sized to fit. Falls back to TMP's
        /// default font asset if the lobby's button has no TMP label.</summary>
        internal static void ApplyFont(TextMeshProUGUI tmp)
        {
            float maxSize = 20f;
            if (styleText != null)
            {
                if (styleText.font != null) tmp.font = styleText.font;
                maxSize = styleText.enableAutoSizing ? styleText.fontSizeMax : styleText.fontSize;
            }

            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 10f;
            tmp.fontSizeMax = maxSize;
            tmp.enableWordWrapping = false;
        }
    }

    /// <summary>
    /// One AI kingdom's row in the lobby player list: its banner, "Name (AI)", and a difficulty
    /// stepper. A clone of the human PlayerEntry prefab with the ready tick hidden. Clicking the
    /// banner opens AiKingdomEditor for the host, the way clicking your own banner opens the
    /// name-and-banner screen for a human.
    /// </summary>
    public class AiRow : MonoBehaviour
    {
        private const float StepperWidth = 124f;

        private int slot;
        private bool canEdit;
        private RawImage bannerImage;
        private TextMeshProUGUI nameLabel;
        private TextMeshProUGUI difficultyLabel;
        private Button prevButton, nextButton;

        /// <summary>
        /// Lays the row out itself rather than squeezing a stepper into the human row's own
        /// layout. Doing that first time round crushed the prefab's name label to nothing, the
        /// AI rows showed a flag and a difficulty with a blank gap where the name belonged. So the
        /// prefab now supplies only the row's background and height: its own children are hidden,
        /// and one content strip (excluded from the prefab's layout, stretched across the row)
        /// holds banner, name and stepper with a fixed share of the width each. The name copies
        /// the human name label's font, size and colour so the two rows still match.
        /// </summary>
        public void Build(int slotIndex)
        {
            slot = slotIndex;

            try
            {
                RectTransform rowRect = (RectTransform)transform;

                // Measured before anything changes: the height every human row has.
                float height = rowRect.rect.height;
                LayoutElement rowLe = GetComponent<LayoutElement>();
                if (height < 20f) height = (rowLe != null && rowLe.preferredHeight > 20f) ? rowLe.preferredHeight : 56f;

                TextMeshProUGUI protoName = null;
                Transform nameNode = transform.Find("PlayerName");
                if (nameNode != null) protoName = nameNode.GetComponent<TextMeshProUGUI>();

                for (int i = 0; i < transform.childCount; i++)
                    transform.GetChild(i).gameObject.SetActive(false);

                if (rowLe == null) rowLe = gameObject.AddComponent<LayoutElement>();
                rowLe.preferredHeight = height;
                rowLe.minHeight = height;

                GameObject content = new GameObject("AiContent");
                content.transform.SetParent(transform, false);
                RectTransform cr = content.AddComponent<RectTransform>();
                cr.anchorMin = Vector2.zero;
                cr.anchorMax = Vector2.one;
                cr.offsetMin = new Vector2(10f, 5f);
                cr.offsetMax = new Vector2(-6f, -5f);
                content.AddComponent<LayoutElement>().ignoreLayout = true;

                HorizontalLayoutGroup h = content.AddComponent<HorizontalLayoutGroup>();
                h.spacing = 8f;
                h.childAlignment = TextAnchor.MiddleLeft;
                h.childControlWidth = true;
                h.childControlHeight = true;
                h.childForceExpandWidth = false;
                h.childForceExpandHeight = true;

                // Banner: click to rename and re-flag (host).
                GameObject bannerObj = new GameObject("AiBanner");
                bannerObj.transform.SetParent(content.transform, false);
                bannerObj.AddComponent<RectTransform>();
                LayoutElement ble = bannerObj.AddComponent<LayoutElement>();
                ble.preferredWidth = 26f;
                ble.minWidth = 26f;
                bannerImage = bannerObj.AddComponent<RawImage>();
                Button pick = bannerObj.AddComponent<Button>();
                pick.targetGraphic = bannerImage;
                pick.onClick.AddListener(() => { if (canEdit) AiKingdomEditor.Open(slot); });

                // Name, styled like the human rows' name.
                nameLabel = AiKingdomControl.NewLabel("AiName", content.transform, "", AiKingdomControl.cText);
                LayoutElement nle = nameLabel.GetComponent<LayoutElement>();
                nle.flexibleWidth = 1f;
                nle.minWidth = 40f;
                if (protoName != null)
                {
                    if (protoName.font != null) nameLabel.font = protoName.font;
                    nameLabel.color = protoName.color;
                    nameLabel.fontSizeMax = protoName.enableAutoSizing ? protoName.fontSizeMax : protoName.fontSize;
                }
                nameLabel.overflowMode = TextOverflowModes.Ellipsis;

                // Difficulty stepper.
                GameObject stepper = AiKingdomControl.NewRow("AiDifficulty", content.transform);
                LayoutElement sle = stepper.AddComponent<LayoutElement>();
                sle.preferredWidth = StepperWidth;
                sle.minWidth = StepperWidth;

                prevButton = AiKingdomControl.NewButton("Prev", stepper.transform, "◄", 24f);
                difficultyLabel = AiKingdomControl.NewLabel("Difficulty", stepper.transform, "Medium", AiKingdomControl.cText);
                difficultyLabel.alignment = TextAlignmentOptions.Center;
                nextButton = AiKingdomControl.NewButton("Next", stepper.transform, "►", 24f);

                prevButton.onClick.AddListener(() => Step(-1));
                nextButton.onClick.AddListener(() => Step(1));
            }
            catch (Exception e) { Main.helper.Log("AI row build error: " + e.Message); }
        }

        /// <summary>Host: moves this AI's difficulty up or down one level, wrapping round.</summary>
        private void Step(int delta)
        {
            LobbySettings s = LobbySettings.Current;
            int n = AiKingdomControl.DifficultyNames.Length;
            s.AiDifficulties[slot] = ((s.AiDifficulties[slot] + delta) % n + n) % n;
            Refresh(true);
        }

        /// <summary>
        /// Shows this AI slot's name, banner and difficulty as LobbySettings has them. Only the host
        /// can click; a guest sees what the host sent.
        /// </summary>
        public void Refresh(bool interactable)
        {
            try
            {
                LobbySettings s = LobbySettings.Current;
                int code = s.AiCodes[slot];
                int diff = Mathf.Clamp(s.AiDifficulties[slot], 0, AiKingdomControl.DifficultyNames.Length - 1);

                if (nameLabel != null) nameLabel.text = AiKingdomControl.NameFor(slot, code) + " (AI)";

                Texture banner = AiKingdomControl.BannerFor(code);
                if (bannerImage != null && banner != null) bannerImage.texture = banner;

                if (difficultyLabel != null)
                {
                    difficultyLabel.text = AiKingdomControl.DifficultyNames[diff];
                    difficultyLabel.color = AiKingdomControl.DifficultyColours[diff];
                }

                if (prevButton != null) prevButton.interactable = interactable;
                if (nextButton != null) nextButton.interactable = interactable;
                canEdit = interactable;
            }
            catch (Exception e) { Main.helper.Log("AI row refresh error: " + e.Message); }
        }
    }

    /// <summary>
    /// Host-only popup for one AI kingdom: its name and its flag. Opened by clicking that AI's
    /// banner in the player list.
    ///
    /// The name box is a clone of the lobby's own server-name field, so it looks and types like
    /// every other text box on the screen. An empty name means "use the game's own name for this
    /// flag", the placeholder shows what that is. The flag steps through the game's livery sets,
    /// skipping any banner a human or another AI already has, same rule as the lobby uses when an
    /// AI is first added.
    ///
    /// Everything writes straight into LobbySettings.Current, which the host already sends to
    /// every guest, so their AI rows follow along.
    /// </summary>
    internal static class AiKingdomEditor
    {
        private static GameObject root;
        private static TMP_InputField nameField;
        private static RawImage bannerImage;
        private static TextMeshProUGUI titleLabel;
        private static int editSlot = -1;

        private static readonly Color cDim = new Color(0f, 0f, 0f, 0.45f);
        private static readonly Color cPanel = new Color(0.10f, 0.14f, 0.20f, 0.97f);
        private static readonly Color cTitle = new Color(0.95f, 0.75f, 0.35f, 1f);

        /// <summary>Host: opens the editor for one AI slot's name and flag.</summary>
        public static void Open(int slot)
        {
            if (!NetRouter.IsServer) return;
            if (slot < 0 || slot >= LobbySettings.Current.AiKingdomCount) return;

            EnsureBuilt();
            if (root == null) return;

            editSlot = slot;
            if (nameField != null)
                nameField.SetTextWithoutNotify(LobbySettings.Current.AiNames[slot] ?? "");
            Refresh();

            root.SetActive(true);
            root.transform.SetAsLastSibling();
        }

        /// <summary>Closes the editor.</summary>
        public static void Close()
        {
            editSlot = -1;
            if (root != null) root.SetActive(false);
        }

        /// <summary>Closes the editor if the slot it was editing has just been removed.</summary>
        public static void CloseIfSlotGone(int count)
        {
            if (editSlot >= count) Close();
        }

        /// <summary>Redraws the editor if it is open, after the lobby settings changed.</summary>
        public static void RefreshIfOpen()
        {
            if (root != null && root.activeSelf && editSlot >= 0) Refresh();
        }

        /// <summary>Shows the edited slot's current name and flag.</summary>
        private static void Refresh()
        {
            if (editSlot < 0 || editSlot >= LobbySettings.Current.AiKingdomCount) { Close(); return; }

            int code = LobbySettings.Current.AiCodes[editSlot];

            Texture banner = AiKingdomControl.BannerFor(code);
            if (bannerImage != null) bannerImage.texture = banner;

            if (titleLabel != null)
                titleLabel.text = AiKingdomControl.NameFor(editSlot, code) + " (AI)";

            // What an empty box means: the game's own name for the current flag.
            TMP_Text placeholder = nameField != null ? nameField.placeholder as TMP_Text : null;
            if (placeholder != null) placeholder.text = AiKingdomControl.DefaultNameFor(editSlot, code);
        }

        /// <summary>Steps the edited slot to the next flag no other AI is using.</summary>
        private static void StepFlag(int direction)
        {
            if (editSlot < 0) return;
            LobbySettings.Current.AiCodes[editSlot] = AiKingdomControl.NextFreeCode(editSlot, direction);
            AiKingdomControl.Refresh(true);
        }

        /// <summary>Stores the name the host typed, on one line and no longer than the lobby allows.</summary>
        private static void OnNameChanged(string text)
        {
            if (editSlot < 0) return;
            string clean = (text ?? "").Replace("\n", " ").Replace("\r", " ");
            if (clean.Length > LobbySettings.MaxAiNameLength) clean = clean.Substring(0, LobbySettings.MaxAiNameLength);
            LobbySettings.Current.AiNames[editSlot] = clean;
            AiKingdomControl.Refresh(true);
        }

        /// <summary>Builds the editor window over the lobby, once.</summary>
        private static void EnsureBuilt()
        {
            if (root != null) return;

            try
            {
                Transform container = AiKingdomControl.LobbyContainer;
                if (container == null) return;

                // Full-panel dimmer: makes the editor modal over the lobby.
                root = new GameObject("AiKingdomEditor");
                root.transform.SetParent(container, false);
                RectTransform rootRect = root.AddComponent<RectTransform>();
                rootRect.anchorMin = Vector2.zero;
                rootRect.anchorMax = Vector2.one;
                rootRect.offsetMin = Vector2.zero;
                rootRect.offsetMax = Vector2.zero;
                Image dim = root.AddComponent<Image>();
                dim.color = cDim;

                GameObject panel = new GameObject("Panel");
                panel.transform.SetParent(root.transform, false);
                RectTransform panelRect = panel.AddComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(0.5f, 0.5f);
                panelRect.anchorMax = new Vector2(0.5f, 0.5f);
                panelRect.pivot = new Vector2(0.5f, 0.5f);
                panelRect.sizeDelta = new Vector2(460f, 250f);
                Image bg = panel.AddComponent<Image>();
                bg.color = cPanel;

                VerticalLayoutGroup vlg = panel.AddComponent<VerticalLayoutGroup>();
                vlg.padding = new RectOffset(16, 16, 14, 14);
                vlg.spacing = 10f;
                vlg.childControlWidth = true;
                vlg.childControlHeight = true;
                vlg.childForceExpandWidth = true;
                vlg.childForceExpandHeight = false;

                titleLabel = AiKingdomControl.NewLabel("Title", panel.transform, "AI Kingdom", cTitle);
                titleLabel.alignment = TextAlignmentOptions.Center;
                SetHeight(titleLabel.gameObject, 32f);

                // Name
                GameObject nameRow = EditorRow("NameRow", panel.transform, 40f);
                FixedLabel(nameRow.transform, "Name");
                nameField = CloneNameBox(nameRow.transform);
                if (nameField == null)
                    AiKingdomControl.NewLabel("NoNameBox", nameRow.transform, "(renaming unavailable)", AiKingdomControl.cText);

                // Flag
                GameObject flagRow = EditorRow("FlagRow", panel.transform, 64f);
                FixedLabel(flagRow.transform, "Flag");
                Button prev = AiKingdomControl.NewButton("FlagPrev", flagRow.transform, "◄", 34f);
                GameObject bannerObj = new GameObject("Banner");
                bannerObj.transform.SetParent(flagRow.transform, false);
                bannerObj.AddComponent<RectTransform>();
                LayoutElement ble = bannerObj.AddComponent<LayoutElement>();
                ble.preferredWidth = 46f;
                ble.minWidth = 46f;
                bannerImage = bannerObj.AddComponent<RawImage>();
                bannerImage.raycastTarget = false;
                Button next = AiKingdomControl.NewButton("FlagNext", flagRow.transform, "►", 34f);
                Spacer(flagRow.transform);
                prev.onClick.AddListener(() => StepFlag(-1));
                next.onClick.AddListener(() => StepFlag(1));

                // Done
                GameObject doneRow = EditorRow("DoneRow", panel.transform, 38f);
                Spacer(doneRow.transform);
                Button done = AiKingdomControl.NewButton("Done", doneRow.transform, "Done", 130f);
                done.onClick.AddListener(Close);

                root.SetActive(false);
            }
            catch (Exception e)
            {
                Main.helper.Log("AI kingdom editor build error: " + e.Message);
                if (root != null) UnityEngine.Object.Destroy(root);
                root = null;
            }
        }

        /// <summary>A copy of the lobby's server-name box, with its lobby wiring stripped.</summary>
        private static TMP_InputField CloneNameBox(Transform parent)
        {
            try
            {
                TMP_InputField source = LobbyScreen.NameBox;
                if (source == null) return null;

                GameObject copy = UnityEngine.Object.Instantiate(source.gameObject, parent, false);
                copy.name = "AiName";
                LayoutElement le = copy.GetComponent<LayoutElement>();
                if (le == null) le = copy.AddComponent<LayoutElement>();
                le.flexibleWidth = 1f;
                le.minHeight = 36f;
                le.preferredHeight = 36f;

                TMP_InputField field = copy.GetComponent<TMP_InputField>();
                if (field == null) { UnityEngine.Object.Destroy(copy); return null; }

                field.onValueChanged = new TMP_InputField.OnChangeEvent();
                field.onEndEdit = new TMP_InputField.SubmitEvent();
                field.onSubmit = new TMP_InputField.SubmitEvent();
                field.interactable = true;
                field.contentType = TMP_InputField.ContentType.Standard;
                field.lineType = TMP_InputField.LineType.SingleLine;
                field.characterLimit = LobbySettings.MaxAiNameLength;
                field.onValueChanged.AddListener(OnNameChanged);
                return field;
            }
            catch (Exception e)
            {
                Main.helper.Log("AI kingdom editor: could not copy the name box: " + e.Message);
                return null;
            }
        }

        /// <summary>One row of the editor, a fixed height.</summary>
        private static GameObject EditorRow(string name, Transform parent, float height)
        {
            GameObject row = AiKingdomControl.NewRow(name, parent);
            row.GetComponent<HorizontalLayoutGroup>().spacing = 8f;
            SetHeight(row, height);
            return row;
        }

        /// <summary>A fixed-width caption at the start of an editor row.</summary>
        private static void FixedLabel(Transform parent, string text)
        {
            TextMeshProUGUI label = AiKingdomControl.NewLabel(text + "Label", parent, text, AiKingdomControl.cText);
            LayoutElement le = label.GetComponent<LayoutElement>();
            le.flexibleWidth = 0f;
            le.preferredWidth = 70f;
            le.minWidth = 70f;
        }

        /// <summary>Empty space that pushes the rest of a row to the right.</summary>
        private static void Spacer(Transform parent)
        {
            GameObject spacer = new GameObject("Spacer");
            spacer.transform.SetParent(parent, false);
            spacer.AddComponent<RectTransform>();
            spacer.AddComponent<LayoutElement>().flexibleWidth = 1f;
        }

        /// <summary>Fixes a layout element's height.</summary>
        private static void SetHeight(GameObject obj, float height)
        {
            LayoutElement le = obj.GetComponent<LayoutElement>();
            if (le == null) le = obj.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            le.minHeight = height;
        }
    }
}
