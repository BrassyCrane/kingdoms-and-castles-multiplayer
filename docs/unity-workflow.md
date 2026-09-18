# Unity workflow, version control and working with a second person

## Current machine, 2026-09-05 (read this first)

The Unity project was moved across from the old PC and now lives at **`E:\Games\KCM`**. The mod is at
**`E:\Games\multiplayer mod for kingdomds and castles`**. Every `C:\Users\User\...` path further down
is from the OLD machine; translate as you read.

Unity **2019.4.40f1** is installed at
`C:\Program Files\Unity\Hub\Editor\2019.4.40f1\Editor\Unity.exe`.

**Two things went wrong on the move, both now understood:**

1. **Hub said "No valid Unity projects found in E:\Games\KCM".** That is Hub's *scan a folder for
   projects* action, which looks for project directories INSIDE the folder you give it. `KCM` **is**
   the project (its `ProjectSettings` sits directly inside), so the scan correctly found no children.
   Use **Add project from disk** and select `E:\Games\KCM` itself. Nothing was wrong with the project:
   `ProjectSettings/ProjectVersion.txt` reads 2019.4.40f1 and every generator script survived.

2. **The real blocker: Unity is not licensed on this machine.** A batchmode open fails with
   `BatchMode: Unity has not been activated with a valid License` and
   `Failed to activate/update license`, and `C:\ProgramData\Unity\` is empty. Sign in to Unity Hub
   and activate a licence (Personal is free) before anything else here will run. Nothing in this
   document works until that is done.

**`Library/` from the old PC was moved aside to `Library_from_old_pc`.** It is a derived cache with
machine-specific state and a stale `ArtifactDB-lock`, so it should be rebuilt rather than carried
over. Unity recreates it on the first successful open; delete `Library_from_old_pc` once that has
happened and the project looks right.

**Hub is only a launcher, and is not required.** Once a licence is active, the project can be driven
entirely from the command line, which is how the bundle build below already works.

### Verifying a built bundle, and two traps in doing so

**Do not grep a bundle for asset names.** Bundles are compressed, so a raw string search reports
every prefab missing, including ones you know are there. Read
`AssetBundles/<platform>/serverbrowserpkg.manifest` instead: its `Assets:` list is authoritative and
plain text.

**`AppendHashToAssetBundleName` accumulates.** A content change writes a bundle under a NEW filename
and leaves the previous one beside it, so `AssetBundles/win64/` ends up holding every bundle ever
built there. Copying the folder wholesale ships several, and the loader takes the first
`serverbrowserpkg_*` it finds, which is a coin toss between the new UI and one from months ago.
`ship_bundles.py` now copies only the newest and names the stale ones it ignored.

First proven run on this machine, 2026-09-05: 9 prefabs, bundle
`serverbrowserpkg_ec81b6ade52024d0295d5f0e1bc38ca1` (win64, 391,569 bytes), all four platforms.

### Batchmode cannot get a licence on this machine (2026-09-05)

**The GUI editor is licensed and works; batchmode is not.** A headless run dies with:

```
[LicensingClient] ERROR Error 10 while verifying Licensing Client signature
[Licensing::Module] Failed to connect to channel: LicenseClient-<user>-2019.4.40
BatchMode: Unity has not been activated with a valid License.
```

Unity Hub and `Unity.Licensing.Client.exe` ARE running when this happens, so it is not a
"Hub is closed" problem. It is Unity 2019.4's older licensing module failing to validate a much
newer Hub's licensing client. The GUI path works because Hub hands the editor a licence directly at
launch; batchmode has to negotiate for one, and cannot.

**So the build runs from the GUI for now:** open the project, **Ctrl+R and wait for the compile
spinner** (§4a, non-negotiable), then `Build > Everything (sprites, prefabs, tags, bundles)`. Then
run `docs/ship_bundles.py` from the mod side.

**To make headless work permanently** (worth doing, it removes a human from every UI change):
`Unity_v2019.4.40f1.alf` has been generated with
`Unity.exe -batchmode -quit -nographics -createManualActivationFile` and left at `E:\Games\`.
Upload it at <https://license.unity3d.com/manual>, download the `.ulf` it returns, then:

```bash
"C:/Program Files/Unity/Hub/Editor/2019.4.40f1/Editor/Unity.exe" -batchmode -quit -nographics -manualLicenseFile "E:/Games/Unity_v2019.4.40f1.ulf" -logFile -
```

That writes a real licence file, which 2019.4 reads without going near the Hub client, and the
two-command pipeline below then works unattended.

### The whole pipeline, two commands

Added 2026-09-05. `BuildBundles.GenerateTagAndBuild` runs sprites, prefabs, bundle tags and the four
bundles in the one order that works, so there is nothing to click and nothing to get out of order.

```bash
"C:/Program Files/Unity/Hub/Editor/2019.4.40f1/Editor/Unity.exe" -batchmode -quit -projectPath "E:/Games/KCM" -executeMethod BuildBundles.GenerateTagAndBuild -logFile -
```

```bash
python "E:/Games/multiplayer mod for kingdomds and castles/docs/ship_bundles.py"
```

**Close the Unity editor first.** Batchmode cannot take the project lock while an editor instance
holds it, and that is the only reason this would fail.

Running it in batchmode also sidesteps §4a's stale-compile trap entirely: batchmode always compiles
before it runs, so a menu item can no longer execute the *previous* compile of a generator you just
edited. The same steps are still on the Build menu (`Build > Everything`) if you prefer clicking, and
there §4a still applies, refresh with Ctrl+R and wait for the spinner first.

`ship_bundles.py` drops the `.manifest` files (build metadata the game never reads), clears each
destination first so a changed bundle hash cannot leave the old bundle sitting beside the new one,
and refuses to copy a platform folder with no `serverbrowserpkg_*` in it, since a half-failed build
would otherwise replace working UI with nothing.

---

Nothing below this line has been executed. It's the plan, written down so either of you
can run it when you're ready.

Two projects are involved:

| Path | What | Size without `Library/` |
| --- | --- | --- |
| `C:\Users\User\KCM` | Unity project that builds the bundle | ~5 MB |
| `C:\Users\User\Labs\kcm-multiplayer` | the mod itself (C#, shipped to Steam) | ~1 MB |

---

## 1. The hazard to name first

**Unity 6000.1.4f1 is also installed on this machine.** Opening `KCM` in it upgrades the
project in place, and a bundle built by a newer editor will not load in a game built on
2019.4.40f1, asset bundles are backward compatible, never forward. Unity Hub will offer
to open with whatever version it feels like.

Both people, every time: **2019.4.40f1**. If someone opens it in 6000 by accident, discard
the working tree and re-clone rather than trying to unpick it.

## 2. Why git works here at all

Unity 2019 has no built-in version control. It doesn't need one, the project is already
serialized in a form git handles, and both of the settings that decide this are already
correct in `ProjectSettings/EditorSettings.asset`:

- `m_SerializationMode: 2`, **Force Text**. Prefabs and scenes are YAML, so they diff.
- `m_ExternalVersionControlSupport: Visible Meta Files`, `.meta` files exist on disk.

Do not change either.

## 3. Setup

```bash
cd /c/Users/User/KCM
git init
```

`.gitignore` for the Unity project:

```gitignore
[Ll]ibrary/
[Tt]emp/
[Oo]bj/
[Bb]uild/
[Bb]uilds/
[Ll]ogs/
[Uu]serSettings/
[Mm]emoryCaptures/
AssetBundles/
*.csproj
*.sln
*.user
*.pidb
*.booproj
*.svd
*.suo
.vs/
.vscode/
*.apk
*.unitypackage
```

`Library/` is 123 MB of the project's 128 MB, and it is a derived cache, never commit it.
`AssetBundles/` is build output; it's shipped by copying into the mod, not by committing.

**`.meta` files must be committed.** This is the one that bites people. Every sprite and
font reference in a prefab is a GUID, and the GUID lives in the asset's `.meta`. If the
second person's checkout is missing `Assets/Art/ui_ctrl.png.meta`, Unity mints a fresh
GUID on import and every button in every prefab loses its sprite. The `.gitignore` above
does not exclude them; keep it that way.

### Prefab merge driver

The smart-merge tool ships with the editor:

```bash
git config merge.unityyamlmerge.name "Unity SmartMerge"
git config merge.unityyamlmerge.driver '"C:/Program Files/Unity/Hub/Editor/2019.4.40f1/Editor/Data/Tools/UnityYAMLMerge.exe" merge -p "$BASE" "$REMOTE" "$LOCAL" "$MERGED"'
```

`.gitattributes`:

```gitattributes
*.prefab merge=unityyamlmerge eol=lf
*.unity  merge=unityyamlmerge eol=lf
*.asset  merge=unityyamlmerge eol=lf
*.mat    merge=unityyamlmerge eol=lf
```

### The mod repo

Same idea, much simpler:

```bash
cd /c/Users/User/Labs/kcm-multiplayer
git init
```

`.gitignore`: `output.txt`, it's the runtime log, rewritten every launch. Add `../kcm-bundles-*`
too if you ever move the backups inside the repo; they are large binaries with no useful diff.

This is worth more here than in most projects. The game compiles the sources at load, so there
is no build to fall back on and no compiler to catch a bad edit, `git diff` and `git checkout`
are the only undo that exists.

## 4a. Always force a refresh before running the generator

**Unity only reimports externally-edited scripts when the editor regains focus.** If a script
is written while Unity has focus, which is exactly what happens when someone edits the
generator for you while you sit in the editor, then clicking *Build → Generate Prefabs*
runs the **previously compiled** version. It succeeds, it logs success, and it writes prefabs
that silently predate the edit.

This has already cost one launch cycle: `ConfigureUiSprites.cs` was picked up and
`GeneratePrefabs.cs` / `UiKit.cs`, saved 60–140 seconds later, were not. Half the fixes
appeared to have been ignored.

So, every time, before running any Build menu item:

1. **Assets → Refresh (Ctrl+R)**
2. Wait for the compile spinner to finish
3. Then run the menu items

*Build → Generate Prefabs* now echoes the layout numbers it actually used, panel size, map
slot size, settings height, column widths. If those don't match the source, the compile was
stale; refresh and re-run.

## 5. Two people, no collisions

The generator changes the shape of this problem. `Assets/Workspace/*.prefab` are now
**build output**, produced by `Build > Generate Prefabs (all 9)` from
`Assets/Editor/GeneratePrefabs.cs`. So:

- **Edit the generator, not the prefabs.** A hand-edit to a prefab is destroyed on the next
  run. If a prefab needs to change, change the number in `UiKit.cs` or `GeneratePrefabs.cs`.
- **Prefab conflicts don't need merging.** `git checkout --theirs Assets/Workspace/`, then
  re-run the generator. The thing you actually merge is C#, which merges fine.
- Commit the generated prefabs anyway, the bundle build reads them off disk, and it's
  useful to see in a diff that a tweak did what you expected.

Suggested split, chosen because the two halves touch different files:

| Person | Owns |
| --- | --- |
| A | `ServerLobby()` in `GeneratePrefabs.cs`, the 25-node screen, the settings grid |
| B | the six smaller builders, `UiKit.cs` palette and metrics, the sprite generation |

`UiKit.cs` is shared, so it's the one file worth a heads-up before editing. Keep changes
there additive.

## 6. Build and ship

Bundles can be built without opening the editor, as long as no editor instance is holding
the project lock:

```bash
"C:/Program Files/Unity/Hub/Editor/2019.4.40f1/Editor/Unity.exe" -batchmode -quit -projectPath "C:/Users/User/KCM" -executeMethod BuildBundles.BuildAll -logFile -
```

Then per `unity-build-guide.md` section 4: delete the `.manifest` files, copy the four
platform folders into the mod directory, and verify the shipped build matches the working
copy before launching:

```bash
diff -rq "C:/Users/User/Labs/kcm-multiplayer" "C:/Program Files (x86)/Steam/steamapps/workshop/content/569480/3775124962" | grep -v "output.txt\|docs"
```

Silence means they match.
