# -*- coding: utf-8 -*-
"""Report two types declared with the same fully-qualified name.

The game compiles this mod at load, so a duplicate declaration surfaces as
"The type 'Main' already contains a definition for 'X'" on the workshop upload screen —
after a publish, a launch and a wait. This finds it in a second instead.

It is easy to hit here because most Harmony patches are nested classes inside `Main`,
which is 3,000+ lines: adding a hook for a method that already has one is not obvious
from the code around where you are working. `membercheck.py` cannot see this — it checks
that referenced members exist, not that declarations are unique.

Names are qualified by their enclosing types, so `PeerRosterMessage.Entry` and
`NetRegistry.Entry` are correctly treated as different types.

Usage: python dupes.py
"""
import pathlib, re, sys, collections

ROOT = pathlib.Path(__file__).resolve().parent.parent
SKIP_DIRS = {'.git', 'docs', 'Riptide'}

TYPEDECL = re.compile(
    r'\b(?:class|struct|enum|interface)\s+(\w+)')


def sources():
    for p in sorted(ROOT.rglob('*.cs')):
        if any(part in SKIP_DIRS for part in p.relative_to(ROOT).parts):
            continue
        yield p


def strip(text):
    """Remove comments and string bodies so neither can look like a declaration."""
    text = re.sub(r'/\*.*?\*/', '', text, flags=re.S)
    text = re.sub(r'//[^\n]*', '', text)
    text = re.sub(r'@"(?:[^"]|"")*"', '""', text)
    text = re.sub(r'"(?:\\.|[^"\\])*"', '""', text)
    return text


def qualified_names(text):
    """Yield (qualified name, line) for every type declared in the file.

    Walks the text tracking brace depth, keeping a stack of the types currently open.
    A `namespace` block is deliberately not part of the qualified name: everything here
    lives under KaCMultiplayer.* and including it would hide a real clash between two
    files that declare the same nested type in the same outer class.
    """
    stack = []          # (name, depth at which it was opened)
    depth = 0
    i = 0
    line = 1
    while i < len(text):
        c = text[i]
        if c == '\n':
            line += 1
            i += 1
            continue
        if c == '{':
            depth += 1
            i += 1
            continue
        if c == '}':
            depth -= 1
            # Strictly greater: a type's body sits AT its recorded depth, so a method or
            # `if` block inside it closing back to that depth must not pop the type. Only
            # the type's own closing brace, which drops below it, does.
            while stack and stack[-1][1] > depth:
                stack.pop()
            i += 1
            continue
        m = TYPEDECL.match(text, i)
        if m:
            # Only a declaration if a brace opens before the next semicolon.
            rest = text[m.end():m.end() + 400]
            brace = rest.find('{')
            semi = rest.find(';')
            if brace != -1 and (semi == -1 or brace < semi):
                stack.append((m.group(1), depth + 1))
                yield '.'.join(n for n, _ in stack), line
            i = m.end()
            continue
        i += 1


def main():
    seen = collections.defaultdict(list)
    for p in sources():
        text = strip(p.read_text(encoding='utf-8-sig', errors='replace'))
        for name, line in qualified_names(text):
            seen[name].append('%s:%d' % (p.relative_to(ROOT), line))

    dupes = {k: v for k, v in seen.items() if len(v) > 1}
    if not dupes:
        print('no duplicate type declarations')
        return 0

    print(len(dupes), 'duplicate type declaration(s):\n')
    for name, locations in sorted(dupes.items()):
        print(' ', name)
        for loc in locations:
            print('     ', loc)
    return 1


if __name__ == '__main__':
    sys.exit(main())
