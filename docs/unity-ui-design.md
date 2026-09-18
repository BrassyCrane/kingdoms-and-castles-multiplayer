# UI design system, `serverbrowserpkg`

Companion to `unity-build-guide.md`. That document says which nodes must exist and what
they must be named; **this** one says what they look like. Where the build guide says
"yours to design", this is the answer.

Derived by measuring the bundle being replaced (`Screenshot 2026-07-30 112623.png`,
2047×1279, 16:10 → canvas units are screenshot px ÷ 1.0661). The palette is taken from
the original; the *geometry* is normalised, because the original's control heights ran
33–47 units for controls of the same kind and that raggedness is most of why it reads as
unfinished.

Unity project: `C:\Users\User\KCM`. Sprites in `Assets/Art`, prefabs in
`Assets/Workspace`.

---

## 1. Palette

Sampled from the original. Alpha is given where it isn't 255.

| Role | Hex | Alpha | Where |
| --- | --- | --- | --- |
| Screen dim | `#05080B` | 60 | prefab root, behind the panel, blocks click-through |
| Modal dim | `#000000` | 150 | `modalui` root only |
| Panel fill | `#16222B` | 235 | `Container`, `Modal`, `LoadingSave/Window` |
| Panel border | `#46586C` | 255 | 1px, baked into `ui_panel` |
| List row fill | `#26323C` | 220 | `serverentryitem`, `serverlobbyplayerentry`, chat entries |
| Button fill (resting) | `#2A577A` → `#2C5F86` | 255 | gradient, baked into `btn_normal`, see §4 |
| Button fill (hover) | `#31AEDE` → `#1076C5` | 255 | baked into `btn_hover` |
| Button border | `#0D2033` | 255 | 2px, square corners, the game's own button edge |
| Button bevel / bottom band | `#18344D` | 255 | 6px along the bottom only: the 3D read |
| Input fill | `#040B10` | 255 | every `TMP_InputField` |
| Input border | `#2A3945` | 255 | 1px, baked into `ui_input` |
| Text primary | `#FFFFFF` | 255 | names, button labels, values |
| Text label | `#C9D2DA` | 255 | field captions ("Max Players", "World Size") |
| Text muted | `#7C8894` | 255 | placeholders ("Enter text…"), `2/25` counts |
| Chat system text | `#CED375` | 255 | `serverchatsystementry`, "X has joined" |
| Accent (positive) | `#57C64A` | 255 | the `+` on Create, `Ready` checkmark |
| Banner backing | `#1A2433` | 255 | behind a `RawImage` before its texture arrives |

## 2. Type scale

LiberationSans SDF (ships with TMP Essential Resources; SIL OFL, so redistributable
inside the bundle, the build guide's "original or licensed" rule is satisfied).

To move to a rounded face closer to the game's own later: import the TTF, generate a TMP
font asset, then replace the font GUID across all seven `.prefab` files in one pass. It
does not require re-touching any node.

| Use | Size | Colour | Align |
| --- | --- | --- | --- |
| Screen title ("Server Lobby") | 32 | primary | top-left |
| Column header ("Players", "Chat") | 30 | primary | centre |
| List row primary ("Xshoy's Server") | 28 | primary | left |
| List row secondary (difficulty, `2/25`) | 22 | muted | centre |
| Field caption ("Max Players") | 19 | label | left |
| Input / dropdown value | 22 | primary | left, 12 padding |
| Button label | 22 | primary | centre |
| Chat message | 21 | primary | left |
| Modal title | 28 | primary | centre |
| Modal body | 21 | label | centre |

## 3. Geometry

### The canvas is not 1920 units wide

The build guide says to author at 1920×1080 and let the anchors do the rest. **That is
wrong**, and it cost a launch cycle. These prefabs are instantiated under a copy of the
*game's* top-level canvas, so the game's Canvas Scaler decides how many units wide the
screen is. Measured in-game it is about **1150**, not 1920. Absolute positions authored
against 1920 land at roughly half scale, which reads as a layout bug: `ServerSettings` is
anchored top-right and 740 wide, so in an ~830-unit `Container` it covers nearly the whole
panel and drops its two sub-columns on top of Players and Chat.

The fix is **not** to author against 1150. Three approaches were tried and rejected:

| Approach | Why not |
| --- | --- |
| Author against the measured width | Build-time constant, correct at exactly one resolution. If the game scales by constant pixel size, canvas units *are* screen pixels and it breaks the moment anyone changes resolution. |
| Put our own `CanvasScaler` on the canvas copy | Unity ignores `CanvasScaler` on a **nested** canvas, and ours is parented under the main-menu UI, so we can't rely on being a root canvas. |
| Fractional anchors throughout | Fixes column positions but not font sizes or control heights, which are absolute. |

So: `Container` is a **fixed 1600×860 design surface**, centred, `localScale` left at 1 in
the prefab. `Lobby/CanvasFit.cs` sets that scale at runtime from the canvas's actual rect,
and recomputes whenever the window changes size:

```
scale = Min(canvasWidth / 1920, canvasHeight / 1080)
```

`Min` of both ratios, not width alone, so the design area fits on both axes and nothing
overflows on ultrawide or 4:3. It scales three surfaces by path, `Container`, `Modal`,
`LoadingSave/Window`, and is attached in `ServerBrowser` (both screens) and
`ModalDialog`.

**Consequence for this document:** every number below is a *design unit* inside that fixed
surface. They do not change with resolution and never need scaling by hand. Do not
"optimise" the runtime scale into a constant.

### Numbers

**One control height: 44.** Inputs, buttons, dropdowns, all 44. This is the single
biggest change from the original.

| Thing | Size |
| --- | --- |
| Control height (input / button / dropdown) | 44 |
| Standard button width | 160 |
| Narrow button ("New Map") | 150 |
| Toggle box | 28 × 28 |
| Caption → its control | 6 gap |
| Control → next caption | 18 gap |
| Panel padding | 40 |
| Column gutter | 20 |
| Corners | square, the game's blocky style |

### Screen panel

Root: stretch-stretch, full screen, `Image` = screen dim, no sprite. It blocks
click-through to the game behind.

`Container`: a **fixed 1600 × 860 box, centred** (`UiKit.DesignBox`), *not* stretched with
insets, see the canvas note above. `Image` = `ui_panel`, *Sliced*, `#FFFFFF` alpha 235.
`localScale` stays 1 in the prefab; `CanvasFit` sets it at runtime.

Measured against the original: panel was 1600 wide, centred. Same.

### Lobby grid

Inside `Container` (1600 wide), padding 40. The settings block is a single node
(`ServerSettings`, a contract path) but reads as two columns: **who may join** on the left,
**what map to generate** on the right, with the preview at the top of the map column.

| Column | x range | Width |
| --- | --- | --- |
| Players | 40 – 320 | 280 |
| Chat | 340 – 780 | 440 |
| `ServerSettings` | 820 – 1560 | 740 |
| ↳ server sub-column | local 0 – 320 | 320 |
| ↳ map sub-column | local 340 – 740 | 400 |

Server sub-column, top down: Name, Max Players, Password, Difficulty.
Map sub-column: `MapPreviewSlot` (340×340, centred in the 400 column), then Seed + New Map,
World Size | World Rivers,
Placement.

**Fixed heights derived from the canvas are a trap.** An earlier pass computed pane heights
from a presumed 1080-tall canvas; the panel is shorter than that on wide aspects, so the
player list ran through the Back button and the settings column through Start. The canvas
note above removes the cause, `Container` is now a fixed box, but two habits stay:

- Content panes (`PlayerList`, `PlayerChat`, the browser scroll view) use `UiKit.VStretch`,
  anchored to both top and bottom, so they track the panel rather than assume its height.
- `ServerSettings` has a fixed height because its content is a fixed stack, but that height is
  **derived from the content**: `MapSlotH + MapSlotGap + 3 × BlockPitch` = 340 + 20 + 234 =
  594. Never write the number in by hand.

For the record, measured from the game: the scaler is *Scale With Screen Size*, reference
1280×720, **match height**. So canvas height is always 720 units and width varies with aspect
(1152 at 16:10, 1280 at 16:9). Canvas units do not change with resolution at all.

`MapPreviewSlot` must stay a **direct child of `Container`**, `LobbyScreen` finds it
by simple name, which does not search grandchildren. It is positioned over the map
sub-column but not parented into `ServerSettings`.

The slot draws the frame; `EnsurePreviewImage()` skips its own border, background and
caption when a slot exists, so there is one definition of how the box looks rather than two.

**The slot is square, and the image insets a uniform 8.** The runtime `AspectRatioFitter`
letterboxes a square map inside whatever slot it gets, so a landscape slot shows dark bars
down the sides and a reserved caption strip shows one across the top. Both were visible in
testing. There is no "Map Preview" caption, the column already has a *World & Map* heading
directly above it.

**Controls deliberately removed:**

- **Lock toggle**, a server is locked iff `Password` is non-empty. One field, one meaning.
- **`WorldType` entirely**, node and dropdown both gone. Land means one landmass means one
  kingdom, and Random can roll Land, so Islands was the only legal value. Hard-set in C#.
- **Fog of War caption**, setting is unimplemented; the node ships inactive to satisfy the
  `Find`, with no caption taking up layout space.

**World Rivers stays.** It looks decorative but it isn't: `World.inst.mapRiverLakes` is
assigned from it in `LobbySettings.cs:149`, `NetRegistrations.cs:327` and
`LobbyScreen.cs`, and it's synced in `LobbySettingsMessage`. It changes generated
terrain.

Vertical, from `Container`'s top edge:

| Band | Y (from top) | Height |
| --- | --- | --- |
| Screen title | −40 | 40 |
| Column headers | −96 | 36 |
| Content top | −144 |, |
| Content bottom | 104 (from bottom) |, |
| Bottom button row | 40 (from bottom) | 44 |

**Reserve the bottom-left corner.** `LobbyScreen.EnsurePreviewImage()` parents a
map preview into `Container` at runtime, currently 175×175 at `(38, 74)` from
bottom-left, being changed to 320×320 at `(40, 104)` to match this grid. The player list
must stop above it.

## 4. Buttons use sprite swap, not colour tint

Buttons follow the **game's own blocky main-menu style**, sampled pixel by pixel from a
screenshot of it:

| Part | Value |
| --- | --- |
| Border | `#0D2033`, 2px, square corners |
| Bevel | `#18344D`, 1px inside the border |
| Fill (normal) | vertical gradient `#2A577A` → `#2B5E85` |
| Fill (hover) | vertical gradient `#31AEDE` → `#1076C5` |
| Hover top line | `#9CE7FF`, 1px |
| Border (hover) | `#004184` |

**The hover state is a different hue, and that rules colour tint out.** Unity's tint
multiplies into the sprite, and multiply can only darken, no tint of a slate-blue sprite
will ever produce bright cyan. So buttons, dropdowns and toggles use
`Selectable.Transition.SpriteSwap` with four real sprites: `btn_normal`, `btn_hover`,
`btn_pressed`, `btn_disabled`. `UiKit.StyleSelectable` wires them up.

Two things stay on colour tint:

- **Scrollbar handles** (`UiKit.StyleTinted`), the handle has its own narrower sprite, and
  swapping in a button sprite would change its proportions.
- **`PlayerBanner` in `serverlobbyplayerentry`**, its target graphic is a `RawImage`, which
  cannot swap sprites. Its normal tint must stay pure white or it darkens the banner texture
  the mod assigns at runtime.

Everything not a button (`ui_panel`, `ui_input`, `ui_row`, `ui_track`, `ui_fill`) is baked
at its final colour and used with tint `#FFFFFF`, set only the alpha.

The button sprites are 32×48 with a 6px 9-slice border: that keeps the 2px edge and 1px
bevel crisp while the gradient in the centre band stretches with the control.

## 5. Sprite inventory

All generated programmatically (`scratchpad/gen-art.ps1`), so every pixel is original
work, no extraction from the old bundle or the base game, per the build guide's rule.

| File | Size | 9-slice border | Image Type | Use |
| --- | --- | --- | --- | --- |
| `btn_normal` | 32×48 | 6 | Sliced | buttons, dropdowns, toggle boxes, resting |
| `btn_hover` | 32×48 | 6 | Sliced | highlighted sprite |
| `btn_pressed` | 32×48 | 6 | Sliced | pressed sprite |
| `btn_disabled` | 32×48 | 6 | Sliced | disabled sprite |
| `ui_panel` | 32² | 6 | Sliced | panel backgrounds |
| `ui_input` | 32² | 6 | Sliced | input field backgrounds |
| `ui_row` | 32² | 6 | Sliced | list rows, chat bubbles |
| `ui_handle` | 24×32 | 6 | Sliced | scrollbar handles |
| `ui_track` | 24² | 6 | Sliced | scrollbar tracks |
| `ui_arrow_down` | 24² |, | Simple | dropdown arrow |
| `ui_check` | 32² |, | Simple | toggle checkmark, `Ready` |
| `ui_plus` | 24² |, | Simple | Create button, tinted accent |
| `ui_padlock` | 32² |, | Simple | `ServerLocked` |
| `ui_sprout` | 32² |, | Simple | seed field icon (baked green) |

Import settings are applied by *Build → Configure UI Sprites*
(`Assets/Editor/ConfigureUiSprites.cs`), do not set them by hand, the borders are the
part that matters and they're recorded in that file.

`Build → Tag Workspace Prefabs Into Bundle` tags all seven into `serverbrowserpkg`.

## 6. Renames, applied

Section 0 of the build guide lists seven node renames. **Both halves are now done**: the
generator builds the new names and the C# calls them.

| File | Old | New |
| --- | --- | --- |
| `LobbyScreen.cs` | `Container/TextMeshPro - InputField` | `Container/ChatInput` |
| `LobbyScreen.cs` | `…/ServerAccess/Toggle` | `…/ServerAccess/LockToggle` |
| `LobbyScreen.cs` | `LoadingSave/Window/Progress Bar/Mask` | `…/ProgressBar/Fill` |
| `LobbyScreen.cs` | `LoadingSave/Window/Progress Bar` | `…/ProgressBar` |
| `LobbyScreen.cs` | `LoadingSave/Window/Information` | `…/StatusText` |
| `ServerRow.cs` | `Panel/ServerName` | `ServerName` |
| `ServerRow.cs` | `Panel/ServerHost` | `ServerHost` |

**Consequence: bundle and code have to move together.** A bundle built before this rename
uses the old node names, and the lobby null-refs on the first binding that misses. That
failure is at least legible, the `Bind<T>` helpers name the missing path in the exception,
but there is no way to run half of this change. Rebuild all four bundles in the same pass.

Also changed in the same pass, so the prefab grid and the runtime code agree:

- `LobbyScreen.EnsurePreviewImage()`, map preview 175×175 at `(38, 74)` →
  **280×280 at `(50, 100)`**, matching the slot the Players column now leaves open.

Two blocks in `LobbyScreen` are now inert and their comments say so: the `"Locked"`
label relabel (the prefab ships the right caption) and the `Password` width fix-up
(`ServerAccess` fills `ServerSettings`, so the rects already agree). Both are guarded and
harmless; they cover the case of an older bundle being loaded.

One piece of genuine cruft remains, in `ServerRow.Start()`: a loop that tints any
sprite-less white `Image`/`RawImage` to dark, added to hide "an unused server-cover image
that renders as a glaring white box" in the old bundle. Every Image in the rebuilt
`serverentryitem` has a sprite, so the loop matches nothing. Safe to delete once the new
bundle ships.

### Dropdowns: two traps

**The option lists live in the prefab.** Nothing in the mod calls `AddOptions`, so an empty
list renders as a blank control. `UiKit.MakeDropdown` populates them; the generator is the
only source.

**`alphaFadeSpeed` must be 0.** `TMP_Dropdown.Show()` fades its popup in with a `CanvasGroup`
tween that uGUI builds with `ignoreTimeScale = false`. The menu runs at `timeScale 0`, so with
any non-zero fade speed the tween never advances and the list sits at **alpha 0**, fully
instantiated, correctly sized and positioned, sorting order 30000, and completely invisible.
It cost three test cycles, and it looks exactly like "the dropdown doesn't react".

Belt and braces: the generator sets `alphaFadeSpeed = 0`, and `Lobby/DropdownListFix.cs`
forces the `CanvasGroup` to alpha 1 in `LateUpdate` as well.

Worth knowing for diagnosis: a dropdown list's `lossyScale` in this hierarchy is about
**0.0083**, and that is normal, it is what the game's canvas chain scales to. It looks
alarming and it is not the problem.

