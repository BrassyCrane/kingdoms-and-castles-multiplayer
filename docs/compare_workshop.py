"""Compare the original mod against ours, whole-mod, across two folders.

Defaults to the Labs pair (reference copy vs working copy); pass two paths to point it
anywhere, e.g. the Steam workshop download folders for a shipped-build answer.

Third-party code is excluded, because overlap there is not evidence of anything - both mods
vendor Tom Weiland's Riptide and RiptideSteamTransport unmodified, and those files are
byte-identical for that reason alone. Detection is by MIT header first, directory name second,
so a renamed or relocated vendor folder still gets excluded.

Two measures:

  * line-set overlap - his distinct significant lines that appear anywhere in ours. Scattered
    survivors show up here, but so do incidental collisions.
  * contiguous runs  - matching blocks of MIN_RUN+ consecutive lines between paired files.
    This is the one that means copying. Files are paired by content, not by path, since the
    layouts diverged (his Packets/, ours Net/Messages/).

Both percentages are reported against HIS significant line count, since the question is how
much of his expression is present in ours - growing our own code cannot dilute that.

    python docs/compare_workshop.py                 summary + per-file table
    python docs/compare_workshop.py --runs          every surviving block, largest first
    python docs/compare_workshop.py --assets        also checksum the non-.cs files
    python docs/compare_workshop.py <his> <ours>    override the two folders
"""
import difflib
import hashlib
import re
import sys
from pathlib import Path

HIS_ROOT = Path(r"C:\Users\User\Labs\kcm-original-reference")
OUR_ROOT = Path(r"C:\Users\User\Labs\kcm-multiplayer")

MIN_RUN = 4               # a block shorter than this is not meaningfully "copied"
PAIR_FLOOR = 0.05         # below this shared-line fraction, treat as unrelated files
VENDOR_DIRS = {"riptide", "riptidesteamtransport"}
VENDOR_HEADER = re.compile(r"provided under The MIT License as part of", re.I)
TRIVIAL = re.compile(r"^[\s{}();]*$")


def read(p):
    return p.read_text(encoding="utf-8", errors="replace").splitlines()


def is_vendor(path, root):
    """Third-party if it carries the MIT banner, or lives in a known vendor directory."""
    rel = path.relative_to(root)
    if any(part.lower() in VENDOR_DIRS for part in rel.parts[:-1]):
        return True
    try:
        head = "\n".join(read(path)[:6])
    except OSError:
        return False
    return bool(VENDOR_HEADER.search(head))


def sources(root):
    """Every non-vendor .cs file under root, as {relative path: [lines]}."""
    out = {}
    for path in sorted(root.rglob("*.cs")):
        if is_vendor(path, root):
            continue
        out[path.relative_to(root)] = [l.rstrip() for l in read(path)]
    return out


def is_trivial(line):
    s = line.strip()
    if not s or TRIVIAL.match(s):
        return True
    if s.startswith("//") or s.startswith("*") or s.startswith("/*"):
        return True
    if s in ("else", "try", "break;", "continue;", "return;", "}", "{", "});", "}));"):
        return True
    if s.startswith("using ") and s.endswith(";"):
        return True
    return False


def significant(lines):
    return [l for l in lines if not is_trivial(l)]


def pair_files(his, ours):
    """Match each of his files to whichever of ours shares the most significant lines.

    Cheap set intersection for the scoring pass - SequenceMatcher only runs on the winner.
    Greedy and one-to-one: our file, once claimed, is out of the running, so a single big
    file of ours cannot absorb credit for several of his.
    """
    our_sets = {rel: set(significant(lines)) for rel, lines in ours.items()}
    scored = []
    for rel, lines in his.items():
        his_set = set(significant(lines))
        if not his_set:
            continue
        for our_rel, our_set in our_sets.items():
            shared = len(his_set & our_set)
            if shared:
                scored.append((shared / len(his_set), shared, str(rel), str(our_rel)))

    scored.sort(reverse=True)
    taken_his, taken_ours, pairs = set(), set(), []
    for frac, _shared, rel, our_rel in scored:
        if frac < PAIR_FLOOR or rel in taken_his or our_rel in taken_ours:
            continue
        taken_his.add(rel)
        taken_ours.add(our_rel)
        pairs.append((Path(rel), Path(our_rel)))
    unmatched = [rel for rel in his if str(rel) not in taken_his]
    return pairs, unmatched


def runs_between(his_lines, our_lines):
    """Contiguous matching blocks carrying at least one significant line."""
    sm = difflib.SequenceMatcher(None, his_lines, our_lines, autojunk=False)
    out = []
    for b in sm.get_matching_blocks():
        if b.size < MIN_RUN:
            continue
        chunk = his_lines[b.a:b.a + b.size]
        if any(not is_trivial(l) for l in chunk):
            out.append((b.size, b.a, b.b, chunk))
    return out


def compare_assets(his_root, our_root):
    """Non-.cs files that are byte-identical, matched on content rather than name."""
    def digest(root):
        out = {}
        for path in root.rglob("*"):
            if not path.is_file() or path.suffix in (".cs", ".txt", ".py", ".md"):
                continue
            out.setdefault(hashlib.md5(path.read_bytes()).hexdigest(), []).append(
                path.relative_to(root))
        return out

    his, ours = digest(his_root), digest(our_root)
    shared = sorted(set(his) & set(ours))
    print(f"non-source files : {sum(len(v) for v in his.values())} his, "
          f"{sum(len(v) for v in ours.values())} ours, "
          f"{sum(len(his[d]) for d in shared)} byte-identical")
    for d in shared:
        print(f"    {his[d][0]}  ==  {ours[d][0]}")
    print()


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    his_root = Path(args[0]) if args else HIS_ROOT
    our_root = Path(args[1]) if len(args) > 1 else OUR_ROOT

    for root, label in ((his_root, "his"), (our_root, "ours")):
        if not root.is_dir():
            print(f"No {label} mod at {root}")
            return 1

    his, ours = sources(his_root), sources(our_root)
    his_sig = [l for lines in his.values() for l in significant(lines)]
    our_sig = [l for lines in ours.values() for l in significant(lines)]
    our_set = set(our_sig)
    if not his_sig:
        print("Nothing to compare - no non-vendor sources found.")
        return 1

    print(f"his  {his_root.name}   {len(his):>3} files, {len(his_sig):>6} significant lines")
    print(f"ours {our_root.name}   {len(ours):>3} files, {len(our_sig):>6} significant lines")
    print("(Riptide / RiptideSteamTransport excluded - vendored MIT, identical in both)")
    print()

    survivors = [l for l in his_sig if l in our_set]
    print(f"line-set overlap : {len(survivors)}/{len(his_sig)} of his significant lines "
          f"present in ours ({100.0 * len(survivors) / len(his_sig):.1f}%)")

    pairs, unmatched = pair_files(his, ours)
    per_file, all_runs = [], []
    for rel, our_rel in pairs:
        runs = runs_between(his[rel], ours[our_rel])
        copied = sum(len(significant(chunk)) for _, _, _, chunk in runs)
        n = len(significant(his[rel]))
        if n:
            per_file.append((copied / n, copied, n, str(rel), str(our_rel)))
        all_runs.extend((size, a, b, chunk, rel, our_rel) for size, a, b, chunk in runs)

    in_runs = sum(c for _, c, _, _, _ in per_file)
    print(f"contiguous runs  : {len(all_runs)} blocks of >={MIN_RUN} lines, carrying "
          f"{in_runs}/{len(his_sig)} of his significant lines "
          f"({100.0 * in_runs / len(his_sig):.1f}%)")
    print()

    if "--assets" in sys.argv:
        compare_assets(his_root, our_root)

    if "--runs" in sys.argv:
        all_runs.sort(key=lambda x: -x[0])
        print(f"{'lines':>6}  {'his':>6}  {'ours':>6}  file / first significant line")
        print("-" * 96)
        for size, a, b, chunk, rel, our_rel in all_runs:
            first = next((l.strip() for l in chunk if not is_trivial(l)), "")
            print(f"{size:>6}  {a+1:>6}  {b+1:>6}  {rel}  ->  {our_rel}")
            print(f"{'':>22}  {first[:70]}")
        return 0

    per_file.sort(reverse=True)
    print("per-file, worst first (share of his significant lines inside a run):")
    print(f"{'':>7}  {'in runs':>13}  his file  ->  our file")
    print("-" * 96)
    for frac, copied, n, rel, our_rel in per_file:
        if copied == 0:
            continue
        print(f"{100 * frac:>6.1f}%  {copied:>6}/{n:<6}  {rel}  ->  {our_rel}")

    if unmatched:
        gone = sum(len(significant(his[rel])) for rel in unmatched)
        print()
        print(f"{len(unmatched)} of his files have no counterpart in ours "
              f"({gone} significant lines, dropped or fully rewritten)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
