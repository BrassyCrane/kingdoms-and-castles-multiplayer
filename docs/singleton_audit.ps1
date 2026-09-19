# Every place the GAME reads Player.inst that neither of our transpilers covers.
#
# WHY THIS EXISTS. This mod makes one large bet: that rewriting `Player.inst` inside Player's own
# instance methods (to `this`) and inside Building's instance methods (to the building's owner) is
# enough to make a singleton-shaped game work for several kingdoms at once. Everywhere else, the
# game still reads the singleton, which is always the LOCAL kingdom, and answers a question about
# somebody else's kingdom with our own data.
#
# That bet has now been wrong twice in ways that reached players:
#
#   LandmassOwner.CalcMaxGold   asked Player.inst for the owner's throne rooms, so every remote
#                               kingdom had zero gold capacity, could never hold gold, and every
#                               number typed into a merchant order snapped back to 0.
#   JobSystem.Update            asked Player.inst for each landmass's job settings sixteen times,
#                               so every island in the world was staffed by the local player's
#                               decrees, other players' islands included.
#
# Both were found by a player noticing a symptom, months apart, and both were sitting in plain
# sight in the IL. This script enumerates the rest of them in a few seconds, so the third one is
# found by reading a list rather than by somebody's save being wrong.
#
# WHAT IT CANNOT TELL YOU. It finds the reads; it cannot know which ones MATTER. Plenty are
# legitimately about the local player: UI that draws the local kingdom's panel, input handling, the
# main menu. Judgement is still required per row, which is why the output is a ranked list to read
# rather than a pass or fail. The question to ask of each row is the same one every time:
#
#   "If two kingdoms existed, would this method still be right?"
#
# Usage:  powershell -ExecutionPolicy Bypass -File docs\singleton_audit.ps1
#         powershell -ExecutionPolicy Bypass -File docs\singleton_audit.ps1 -All   (include claimed)

param(
    [string] $Game = "C:\Program Files (x86)\Steam\steamapps\common\Kingdoms and Castles",
    [string] $Mod  = (Split-Path -Parent $PSScriptRoot),
    [switch] $All
)

$managed = Join-Path $Game "KingdomsAndCastles_Data\Managed"
$cecil   = Join-Path $managed "Trivial.Mono.Cecil.dll"

if (-not (Test-Path $cecil)) {
    Write-Error "no Cecil at $cecil. Point -Game at the game folder."
    exit 1
}

Add-Type -Path $cecil

# ---- what the mod already claims -------------------------------------------------------------
#
# Parsed out of our own source rather than maintained by hand here, because a list of patched
# methods in a second file is a list that goes stale. Best effort by design: it reads the
# [HarmonyPatch(typeof(X), "Y")] attribute forms and the manual harmony.Patch calls, and anything
# it misses shows up as a false "unclaimed" row, which is the safe direction to be wrong in.

$claimed = New-Object 'System.Collections.Generic.HashSet[string]'

Get-ChildItem -Path $Mod -Filter *.cs -Recurse |
    Where-Object { $_.FullName -notmatch '\\(Riptide|RiptideSteamTransport|docs)\\' } |
    ForEach-Object {
        $text = Get-Content $_.FullName -Raw

        # [HarmonyPatch(typeof(Weather), "Update")] and the new Type[] { ... } overloads
        [regex]::Matches($text, 'HarmonyPatch\(\s*typeof\(\s*([A-Za-z0-9_.]+)\s*\)\s*,\s*"([A-Za-z0-9_]+)"') |
            ForEach-Object { [void] $claimed.Add(($_.Groups[1].Value -split '\.')[-1] + "." + $_.Groups[2].Value) }

        # Two-attribute form: [HarmonyPatch(typeof(World))] / [HarmonyPatch("Place")]
        [regex]::Matches($text, 'HarmonyPatch\(\s*typeof\(\s*([A-Za-z0-9_.]+)\s*\)\s*\)\s*\]\s*\[\s*HarmonyPatch\(\s*"([A-Za-z0-9_]+)"') |
            ForEach-Object { [void] $claimed.Add(($_.Groups[1].Value -split '\.')[-1] + "." + $_.Groups[2].Value) }

        # Manual guarded patching: GetMethod("Name") on a typeof(X) nearby. Coarse on purpose.
        [regex]::Matches($text, 'typeof\(\s*([A-Za-z0-9_.]+)\s*\)\s*\.\s*GetMethod\(\s*"([A-Za-z0-9_]+)"') |
            ForEach-Object { [void] $claimed.Add(($_.Groups[1].Value -split '\.')[-1] + "." + $_.Groups[2].Value) }
    }

Write-Host "the mod claims $($claimed.Count) game method(s) by name" -ForegroundColor DarkGray

# ---- every read of Player.inst in the game ----------------------------------------------------

$rows = New-Object System.Collections.ArrayList

foreach ($dll in @("Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll")) {
    $path = Join-Path $managed $dll
    if (-not (Test-Path $path)) { continue }

    $module = [Trivial.Mono.Cecil.AssemblyDefinition]::ReadAssembly($path).MainModule

    # Nested types are separate entries in module.Types, so this walk reaches them without
    # recursing. It is the same reason the technical notes warn about looking for a nested type
    # and finding nothing.
    foreach ($type in $module.Types) {
        foreach ($method in $type.Methods) {
            if (-not $method.HasBody) { continue }

            $reads = 0
            foreach ($i in $method.Body.Instructions) {
                if ($i.OpCode.Name -eq 'ldsfld' -and "$($i.Operand)" -match 'Player::inst') { $reads++ }
            }
            if ($reads -eq 0) { continue }

            # The transpilers cover INSTANCE methods on these types only (Home minus ShowOverlay,
            # which is meant to read the local player). A static method has no receiver to rewrite
            # the singleton into, so it is just as exposed as anything else and is deliberately not
            # treated as covered.
            $coveredByTranspiler = (-not $method.IsStatic) -and
                                   ($type.Name -eq 'Player' -or $type.Name -eq 'Building' -or
                                    ($type.Name -eq 'Home' -and $method.Name -ne 'ShowOverlay'))

            $key = "$($type.Name).$($method.Name)"

            [void] $rows.Add([pscustomobject]@{
                Type      = $type.Name
                Method    = $method.Name
                Reads     = $reads
                Covered   = $coveredByTranspiler
                Claimed   = $claimed.Contains($key)
                Assembly  = $dll
            })
        }
    }
}

$total = ($rows | Measure-Object -Property Reads -Sum).Sum
Write-Host "the game reads Player.inst $total time(s) across $($rows.Count) method(s)`n" -ForegroundColor DarkGray

$exposed = $rows | Where-Object { -not $_.Covered }
if (-not $All) { $exposed = $exposed | Where-Object { -not $_.Claimed } }

if ($exposed.Count -eq 0) {
    Write-Host "nothing unclaimed. Every Player.inst read is inside a transpiled instance method or a method the mod patches." -ForegroundColor Green
    exit 0
}

# Grouped by type, because that is how the fixes come: CalcMaxGold was on LandmassOwner and the
# note it produced was "it is worth checking the rest of LandmassOwner for the same shape". A type
# with several exposed methods is a type to read in one sitting.
$exposed |
    Group-Object Type |
    Sort-Object { ($_.Group | Measure-Object -Property Reads -Sum).Sum } -Descending |
    ForEach-Object {
        $sum = ($_.Group | Measure-Object -Property Reads -Sum).Sum
        Write-Host ("{0}  ({1} read(s) across {2} method(s))" -f $_.Name, $sum, $_.Count) -ForegroundColor Yellow

        $_.Group | Sort-Object Reads -Descending | ForEach-Object {
            $mark = if ($_.Claimed) { " [claimed]" } else { "" }
            Write-Host ("    {0,3}x  {1}{2}" -f $_.Reads, $_.Method, $mark)
        }
        Write-Host ""
    }

Write-Host "Ask of each row: if two kingdoms existed, would this method still be right?" -ForegroundColor Cyan
Write-Host "A row that answers no is the next CalcMaxGold. A row about the local player's own UI is fine." -ForegroundColor Cyan
