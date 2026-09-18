#!/bin/bash
# Compiles the whole mod against the game's own assemblies, in about three seconds, without
# launching the game.
#
# WHY THIS EXISTS. The game compiles this mod itself, at load, with Roslyn. Until this script
# existed the only way to learn that a change did not compile was to launch Kingdoms and Castles,
# wait for the mod loader, and read a wall of red text, or worse, to publish and have players find
# it. Every change to this mod can now be compile-checked before anyone launches anything.
#
# It uses the C# compiler Unity ships (any Unity install with MonoBleedingEdge will do; the project
# uses 2019.4.40f1), at language version 7.2, the newest that compiler knows. The game's own Roslyn
# may accept newer syntax than this, so a pass here is the stricter of the two.
#
# WHAT IT CANNOT CATCH. The game also runs its code-security pass (Trivial.CodeSecurity) over the
# mod, and Harmony patches only fail when PatchAll runs at load. A clean compile here is necessary,
# not sufficient: the game run is still the real test.
#
# usage:   bash docs/compile_check.sh [mod-folder]
# env:     KC_GAME   the Kingdoms and Castles install folder
#          UNITY_MONO the MonoBleedingEdge folder of a Unity install

MOD="${1:-$(cd "$(dirname "$0")/.." && pwd)}"
KC_GAME="${KC_GAME:-/c/Program Files (x86)/Steam/steamapps/common/Kingdoms and Castles}"
UNITY_MONO="${UNITY_MONO:-/c/Program Files/Unity/Hub/Editor/2019.4.40f1/Editor/Data/MonoBleedingEdge}"

MANAGED="$KC_GAME/KingdomsAndCastles_Data/Managed"
MONO="$UNITY_MONO/bin/mono.exe"
CSC="$UNITY_MONO/lib/mono/4.5/csc.exe"

for need in "$MANAGED/Assembly-CSharp.dll" "$MONO" "$CSC"; do
    if [ ! -e "$need" ]; then echo "missing: $need (set KC_GAME / UNITY_MONO)"; exit 2; fi
done

OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT

# Every .cs the game would compile. .claude is local tooling, never shipped.
(cd "$MOD" && find . -name '*.cs' -not -path './.claude/*' | sed 's|^\./||') > "$OUT/sources.txt"

# Every game assembly as a reference, mscorlib explicitly because -nostdlib keeps the compiler's
# own framework out of it: the mod has to compile against what the GAME ships, not against a newer
# framework that would hide a missing API.
REFS=()
for dll in "$MANAGED"/*.dll; do REFS+=("-r:$dll"); done

# Warnings about unused fields, obsolete Unity APIs and the like are the codebase's normal noise;
# errors are the only thing this is for.
cd "$MOD"
"$MONO" "$CSC" -nologo -target:library -nostdlib -noconfig -langversion:7.2 \
    -nowarn:0618,0414,0169,0649,0219,0162,0168,0067,1998,4014 \
    -out:"$OUT/check.dll" "${REFS[@]}" @"$OUT/sources.txt" > "$OUT/log.txt" 2>&1

ERRORS=$(grep -c 'error CS' "$OUT/log.txt")
if [ "$ERRORS" -gt 0 ]; then
    grep 'error CS' "$OUT/log.txt"
    echo "FAILED: $ERRORS compile error(s) across $(wc -l < "$OUT/sources.txt") file(s)"
    exit 1
fi

echo "OK: $(wc -l < "$OUT/sources.txt") file(s) compile cleanly against the game's assemblies"
