using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using KaCMultiplayer.Net;

namespace KaCMultiplayer.Lobby
{
    /// <summary>
    /// The game's OWN diplomacy art, borrowed from its Hall of Diplomacy screen, for re-skinning
    /// the mod's Ctrl+Shift+D diplomacy window so it reads as part of Kingdoms and Castles.
    ///
    /// Source: GameUI.inst.diplomacyUI (the vanilla DiplomacyUI, which the game only opens when AI
    /// kingdoms exist, so it sits inactive in every multiplayer session) and
    /// GameUI.inst.diplomacyNotificationUI's kingdom rows. Both are public fields on a public
    /// singleton, no scene search needed, nothing the mod compiler's security scan could object
    /// to. Taken: the main panel's sprite, the game's button art and colour transitions, and the
    /// fonts and text colours of the kingdom name and standing lines.
    ///
    /// Text colours travel WITH the panel art on purpose. If the game's panel is light parchment
    /// its text is dark, and painting that panel behind the mod's white labels would make them
    /// unreadable; taking both keeps whatever contrast the game's own artist chose.
    ///
    /// Every piece is optional and the whole search is logged once. Anything not found is simply
    /// left as the mod's prefab drew it, so this can only make the window look more like the game,
    /// never break it.
    /// </summary>
    internal static class KacGameArt
    {
        private static bool searched;
        private static bool found;

        private static Sprite panelSprite, rowSprite, buttonSprite;
        private static Image.Type panelType, rowType, buttonType;
        private static Color panelColor = Color.white, rowColor = Color.white, buttonColor = Color.white;
        private static ColorBlock buttonColors;
        private static bool haveButtonColors;

        private static TMP_FontAsset titleFont, bodyFont, buttonFont;
        private static Color titleColor = Color.white, bodyColor = Color.white, buttonTextColor = Color.white;
        private static bool haveTitleColor, haveBodyColor, haveButtonTextColor;

        /// <summary>True when the body text the game uses is dark (a light panel behind it).</summary>
        public static bool DarkText
        {
            get { return haveBodyColor && Luminance(bodyColor) < 0.5f; }
        }

        /// <summary>The game's body-text colour, or <paramref name="fallback"/>.</summary>
        public static Color BodyText(Color fallback)
        {
            EnsureFound();
            return haveBodyColor ? bodyColor : fallback;
        }

        /// <summary>
        /// A standing's colour, kept readable on the game's panel: the mod's own light reds and
        /// greens are for a dark panel, so on a light one they are darkened to the same hue.
        /// </summary>
        public static Color Relation(Color onDark)
        {
            EnsureFound();
            if (!DarkText) return onDark;
            return new Color(onDark.r * 0.55f, onDark.g * 0.55f, onDark.b * 0.55f, onDark.a);
        }

        /// <summary>Re-skins the whole diplomacy window: its frame, buttons and text.</summary>
        public static void SkinWindow(GameObject root)
        {
            EnsureFound();
            if (!found || root == null) return;

            try
            {
                Transform window = root.transform.Find("Window");
                Image frame = window != null ? window.GetComponent<Image>() : null;
                if (frame == null)
                {
                    Transform container = root.transform.Find("Window/Container");
                    frame = container != null ? container.GetComponent<Image>() : null;
                }
                if (frame != null && panelSprite != null) Paint(frame, panelSprite, panelType, panelColor);

                SkinButtonsAndText(root, true);
            }
            catch (Exception e) { NetLog.Error("skinning the diplomacy window", e); }
        }

        /// <summary>True once the game's button art was found, so callers can rely on it.</summary>
        public static bool Skinned
        {
            get { EnsureFound(); return found && buttonSprite != null; }
        }

        /// <summary>
        /// Re-skins a panel the mod builds itself (the resource picker): the panel's own Image
        /// becomes the game's panel art, then its buttons and text follow, the same way as the
        /// diplomacy window.
        /// </summary>
        public static void SkinPanel(GameObject root)
        {
            EnsureFound();
            if (!found || root == null) return;

            try
            {
                Image bg = root.GetComponent<Image>();
                if (bg != null && panelSprite != null)
                {
                    Paint(bg, panelSprite, panelType, panelColor);
                    // The game's panel art carries its own border; a flat outline on top of it
                    // would only look wrong.
                    Outline outline = root.GetComponent<Outline>();
                    if (outline != null) UnityEngine.Object.Destroy(outline);
                }

                SkinButtonsAndText(root, true);
            }
            catch (Exception e) { NetLog.Error("skinning a panel", e); }
        }

        /// <summary>Re-skins one kingdom row: its background, buttons and fonts. Colours the
        /// row's caller already set on its labels are left alone.</summary>
        public static void SkinRow(GameObject row)
        {
            EnsureFound();
            if (!found || row == null) return;

            try
            {
                Image bg = row.GetComponent<Image>();
                if (bg != null && rowSprite != null) Paint(bg, rowSprite, rowType, rowColor);

                SkinButtonsAndText(row, false);
            }
            catch (Exception e) { NetLog.Error("skinning a diplomacy row", e); }
        }

        /// <summary>Applies the game's own art to one named action button in a row.</summary>
        public static void SkinActionButton(GameObject row, string name)
        {
            EnsureFound();
            if (!found || row == null) return;

            Transform node = row.transform.Find(name);
            if (node == null) node = row.transform.Find("Actions/" + name);
            if (node != null) SkinButton(node.GetComponent<Button>());
        }

        /// <summary>
        /// Puts the game's button art on every button under a window, and its fonts on the text, the
        /// biggest text getting the title font.
        /// </summary>
        private static void SkinButtonsAndText(GameObject root, bool recolourText)
        {
            foreach (Button b in root.GetComponentsInChildren<Button>(true))
                SkinButton(b);

            TextMeshProUGUI[] texts = root.GetComponentsInChildren<TextMeshProUGUI>(true);

            // The window's biggest text is its heading; that gets the game's title font.
            float biggest = 0f;
            foreach (TextMeshProUGUI t in texts)
                if (t.GetComponentInParent<Button>() == null) biggest = Mathf.Max(biggest, t.fontSize);

            foreach (TextMeshProUGUI t in texts)
            {
                if (t.GetComponentInParent<Button>() != null) continue;   // done by SkinButton

                bool heading = recolourText && biggest > 0f && t.fontSize >= biggest - 0.01f;
                TMP_FontAsset font = heading && titleFont != null ? titleFont : bodyFont;
                if (font != null) t.font = font;

                if (recolourText)
                {
                    if (heading && haveTitleColor) t.color = titleColor;
                    else if (!heading && haveBodyColor) t.color = bodyColor;
                }
            }
        }

        /// <summary>Puts the game's button art and font on one button.</summary>
        private static void SkinButton(Button b)
        {
            if (b == null) return;

            Image img = b.targetGraphic as Image;
            if (img == null) img = b.GetComponent<Image>();
            if (img != null && buttonSprite != null)
            {
                Paint(img, buttonSprite, buttonType, buttonColor);
                b.targetGraphic = img;
            }

            if (haveButtonColors) b.colors = buttonColors;

            foreach (TextMeshProUGUI label in b.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (buttonFont != null) label.font = buttonFont;
                if (haveButtonTextColor) label.color = buttonTextColor;
            }
        }

        /// <summary>Sets an image's sprite, how it is drawn, and its colour.</summary>
        private static void Paint(Image img, Sprite sprite, Image.Type type, Color color)
        {
            img.sprite = sprite;
            img.type = type;
            img.color = color;
        }

        /// <summary>How bright a colour looks, 0 to 1, to tell light text from dark.</summary>
        private static float Luminance(Color c)
        {
            return 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
        }

        // ---- resource icons ----------------------------------------------------------------
        //
        // The game draws its resource icons INSIDE text, as TextMeshPro sprite tags
        // ("<sprite name=icon_wood>", see ResourceAmount.FriendlyName), from its own sprite sheet.
        // A label the mod creates only draws them if it is pointed at that same sheet, so it is
        // found once: the TextMeshPro default if that is the game's sheet, otherwise the sheet one
        // of the game's own labels uses.

        private static bool iconsSearched;
        private static TMP_SpriteAsset iconSprites;

        /// <summary>The game's icon sheet, or null if it could not be found.</summary>
        public static TMP_SpriteAsset IconSprites
        {
            get
            {
                if (iconsSearched) return iconSprites;
                iconsSearched = true;

                try
                {
                    if (HasIcon(TMP_Settings.defaultSpriteAsset, "icon_gold"))
                        iconSprites = TMP_Settings.defaultSpriteAsset;
                    else if (GameUI.inst != null)
                    {
                        foreach (TMP_Text t in GameUI.inst.GetComponentsInChildren<TMP_Text>(true))
                            if (HasIcon(t.spriteAsset, "icon_gold")) { iconSprites = t.spriteAsset; break; }
                    }

                    NetLog.Info("game art: resource icons " + (iconSprites != null ? "from " + iconSprites.name : "not found"));
                }
                catch (Exception e) { NetLog.Error("finding the game's resource icons", e); }

                return iconSprites;
            }
        }

        /// <summary>
        /// The game's icon for a resource as a sprite tag, or "" when it cannot be drawn. A label
        /// showing it needs rich text on and <see cref="IconSprites"/> as its sprite asset.
        /// </summary>
        public static string ResourceIcon(FreeResourceType type)
        {
            string name = IconName(type);
            if (name == null || !HasIcon(IconSprites, name)) return "";
            return "<sprite name=" + name + ">";
        }

        /// <summary>The name of the game's own sprite for a resource icon.</summary>
        private static string IconName(FreeResourceType type)
        {
            switch (type)
            {
                case FreeResourceType.Wheat: return "icon_food";
                case FreeResourceType.Tree: return "icon_wood";
                case FreeResourceType.Stone: return "icon_stone";
                case FreeResourceType.Charcoal: return "icon_charcoal";
                case FreeResourceType.Gold: return "icon_gold";
                case FreeResourceType.IronOre: return "icon_iron";
                case FreeResourceType.Tools: return "icon_tools";
                case FreeResourceType.Armament: return "icon_armaments";
                case FreeResourceType.Fish: return "icon_fish";
                case FreeResourceType.Apples: return "icon_apple";
                case FreeResourceType.Pork: return "icon_pork";
                default: return null;
            }
        }

        /// <summary>Whether the game's sprite sheet has an icon of this name.</summary>
        private static bool HasIcon(TMP_SpriteAsset sheet, string name)
        {
            try { return sheet != null && sheet.GetSpriteIndexFromName(name) >= 0; }
            catch { return false; }
        }

        /// <summary>Finds the game's own Hall of Diplomacy art and fonts once, from its diplomacy window.</summary>
        private static void EnsureFound()
        {
            if (searched) return;
            searched = true;

            try
            {
                GameUI gameUi = GameUI.inst;
                DiplomacyUI ui = gameUi != null ? gameUi.diplomacyUI : null;
                if (ui == null)
                {
                    NetLog.Info("game art: GameUI.diplomacyUI not available; diplomacy window keeps its own look");
                    return;
                }

                if (ui.nameTxt != null)
                {
                    titleFont = ui.nameTxt.font;
                    titleColor = ui.nameTxt.color;
                    haveTitleColor = true;
                }
                if (ui.standingTxt != null)
                {
                    bodyFont = ui.standingTxt.font;
                    bodyColor = ui.standingTxt.color;
                    haveBodyColor = true;
                }
                if (bodyFont == null) bodyFont = titleFont;

                Button donor = ui.inquireButton != null ? ui.inquireButton
                             : (ui.missionInfoToggle != null ? ui.missionInfoToggle : ui.resourceInfoToggle);
                if (donor != null)
                {
                    Image bi = donor.targetGraphic as Image;
                    if (bi == null) bi = donor.GetComponent<Image>();
                    if (bi != null && bi.sprite != null)
                    {
                        buttonSprite = bi.sprite;
                        buttonType = bi.type;
                        buttonColor = bi.color;
                    }
                    buttonColors = donor.colors;
                    haveButtonColors = true;

                    TextMeshProUGUI label = donor.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (label != null)
                    {
                        buttonFont = label.font;
                        buttonTextColor = label.color;
                        haveButtonTextColor = true;
                    }
                }
                if (buttonFont == null) buttonFont = bodyFont;

                Image panel = PanelImage(ui);
                if (panel != null)
                {
                    panelSprite = panel.sprite;
                    panelType = panel.type;
                    panelColor = panel.color;
                }

                DiplomacyNotificationUI notes = gameUi.diplomacyNotificationUI;
                DiplomacyNotificationItem item = notes != null
                    ? notes.GetComponentInChildren<DiplomacyNotificationItem>(true) : null;
                if (item != null && item.notificationBackground != null && item.notificationBackground.sprite != null)
                {
                    rowSprite = item.notificationBackground.sprite;
                    rowType = item.notificationBackground.type;
                    rowColor = item.notificationBackground.color;
                }

                found = panelSprite != null || buttonSprite != null || bodyFont != null;

                NetLog.Info("game art for diplomacy: panel=" + Name(panelSprite) + " row=" + Name(rowSprite)
                            + " button=" + Name(buttonSprite)
                            + " titleFont=" + (titleFont != null ? titleFont.name : "-")
                            + " bodyFont=" + (bodyFont != null ? bodyFont.name : "-")
                            + " bodyText=" + (haveBodyColor ? ColorUtility.ToHtmlStringRGBA(bodyColor) : "-"));
            }
            catch (Exception e) { NetLog.Error("reading the game's diplomacy art", e); }
        }

        /// <summary>
        /// The Hall of Diplomacy's main panel: its dialogue panel if that carries a nine-sliced
        /// background, otherwise the largest nine-sliced image anywhere in the screen that is not
        /// part of a button or a scroll mask.
        /// </summary>
        private static Image PanelImage(DiplomacyUI ui)
        {
            if (ui.dialoguePanel != null)
            {
                Image own = ui.dialoguePanel.GetComponent<Image>();
                if (own != null && own.sprite != null && own.type == Image.Type.Sliced) return own;
            }

            Image best = null;
            float bestArea = 0f;
            foreach (Image img in ui.GetComponentsInChildren<Image>(true))
            {
                if (img.sprite == null || img.type != Image.Type.Sliced) continue;
                if (img.GetComponentInParent<Button>() != null) continue;
                if (img.GetComponent<Mask>() != null) continue;

                Rect r = img.rectTransform.rect;
                float area = Mathf.Abs(r.width * r.height);
                if (area > bestArea) { bestArea = area; best = img; }
            }
            return best;
        }

        /// <summary>A sprite's name for the log, or a dash for none.</summary>
        private static string Name(Sprite s)
        {
            return s != null ? s.name : "-";
        }
    }
}
