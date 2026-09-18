# -*- coding: utf-8 -*-
"""Catch `OurType.Member` references where Member is not declared on OurType.

The game compiles this mod at load, so there is no compiler here and a bad rename surfaces
as a wall of red text in-game. This is a crude substitute: it only understands the mod's own
types, only checks statically-qualified access, and knows nothing about inheritance beyond
the mod's own hierarchy — but that is exactly the shape of mistake a rename makes.

Usage: python membercheck.py
"""
import pathlib, re, sys, collections

# The repo root, derived from this file rather than hardcoded.
#
# It USED to be an absolute path on a previous machine, which meant the check silently found
# zero .cs files and reported "no unresolved Type.Member references" on every run. A checker
# that cannot fail is worse than no checker, because it gets quoted as evidence. dupes.py
# already derived its root this way; this now matches it.
ROOT = pathlib.Path(__file__).resolve().parent.parent
SKIP_DIRS = {'.git', 'docs'}

# Members that come from Unity / the BCL rather than from the mod. A reference to one of
# these on a mod type is fine and must not be reported.
INHERITED = {
    'inst', 'instance', 'transform', 'gameObject', 'name', 'enabled', 'tag', 'Equals',
    'GetHashCode', 'ToString', 'GetType', 'Instantiate', 'Destroy', 'DestroyImmediate',
    'FindObjectOfType', 'StartCoroutine', 'StopCoroutine', 'InvokeRepeating', 'CancelInvoke',
    'GetComponent', 'GetComponents', 'GetComponentInChildren', 'GetComponentsInChildren',
    'AddComponent', 'SendMessage', 'Invoke', 'ReferenceEquals',
}

DECL = re.compile(
    r'\b(?:public|private|protected|internal|static|readonly|const|override|virtual|new|'
    r'abstract|sealed|partial|extern|unsafe|async|volatile|event)\s+'
    # return type, then the member name, then optional generic parameter list
    r'(?:[\w<>\[\],\.\?]+\s+)+?(\w+)\s*(?:<[\w\s,]+>)?\s*(?:[=;({]|=>)')
TYPEDECL = re.compile(r'\b(?:class|struct|enum|interface)\s+(\w+)\s*(?::\s*([\w\s,<>\.]+?))?\s*\{', re.S)
ENUMMEMBER = re.compile(r'^\s*(\w+)\s*(?:=\s*[^,]+)?,\s*$', re.M)


def sources():
    for p in sorted(ROOT.rglob('*.cs')):
        if any(part in SKIP_DIRS for part in p.relative_to(ROOT).parts):
            continue
        yield p


def strip_comments(t):
    t = re.sub(r'/\*.*?\*/', '', t, flags=re.S)
    t = re.sub(r'//[^\n]*', '', t)
    t = re.sub(r'"(?:\\.|[^"\\])*"', '""', t)
    return t


def main():
    bodies = {}       # type -> source text of its declaration onward
    bases = {}        # type -> list of base names
    members = collections.defaultdict(set)

    texts = {p: strip_comments(p.read_text(encoding='utf-8-sig', errors='replace')) for p in sources()}

    for p, t in texts.items():
        for m in TYPEDECL.finditer(t):
            tname = m.group(1)
            bases[tname] = [b.strip().split('<')[0].split('.')[-1]
                            for b in (m.group(2) or '').split(',') if b.strip()]
            # Body: from the opening brace to a brace-balanced close.
            i = m.end() - 1
            depth, j = 0, i
            while j < len(t):
                if t[j] == '{': depth += 1
                elif t[j] == '}':
                    depth -= 1
                    if depth == 0: break
                j += 1
            body = t[i:j]
            bodies[tname] = body
            for d in DECL.finditer(body):
                members[tname].add(d.group(1))
            for e in ENUMMEMBER.finditer(body):
                members[tname].add(e.group(1))
            # Nested types count as members of the outer type.
            for n in TYPEDECL.finditer(body):
                members[tname].add(n.group(1))

    def visible(tname, seen=None):
        seen = seen or set()
        if tname in seen or tname not in members:
            return set()
        seen.add(tname)
        out = set(members[tname])
        for b in bases.get(tname, []):
            out |= visible(b, seen)
        return out

    known = set(members)
    problems = []
    ref = re.compile(r'\b([A-Z]\w*)\.(\w+)\b')

    for p, t in texts.items():
        for line_no, line in enumerate(t.splitlines(), 1):
            for m in ref.finditer(line):
                tname, member = m.group(1), m.group(2)
                if tname not in known:
                    continue
                # Unknown base somewhere in the chain -> cannot judge.
                chain_unknown = any(b not in known for b in bases.get(tname, []))
                if chain_unknown:
                    continue
                if member in INHERITED or member in visible(tname):
                    continue
                problems.append((str(p.relative_to(ROOT)), line_no, tname, member, line.strip()[:90]))

    if not problems:
        print('no unresolved Type.Member references')
        return 0

    print(len(problems), 'unresolved references:\n')
    for f, n, tname, member, line in problems:
        print('%s:%d  %s.%s' % (f, n, tname, member))
        print('    ', line)
    return 1


if __name__ == '__main__':
    sys.exit(main())
