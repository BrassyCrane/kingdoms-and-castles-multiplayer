import sys

# Balance checker that understands C# comments, char literals, and verbatim/interpolated
# strings, so it does not trip on braces or quotes inside them.
def scan(path):
    s = open(path, encoding='utf-8-sig').read()
    i, n = 0, len(s)
    curly = paren = square = 0
    line = 1
    while i < n:
        c = s[i]
        if c == '\n':
            line += 1; i += 1; continue
        if c == '/' and i + 1 < n:
            if s[i+1] == '/':
                while i < n and s[i] != '\n': i += 1
                continue
            if s[i+1] == '*':
                i += 2
                while i + 1 < n and not (s[i] == '*' and s[i+1] == '/'):
                    if s[i] == '\n': line += 1
                    i += 1
                i += 2; continue
        if c == "'":
            i += 1
            while i < n and s[i] != "'":
                if s[i] == '\\': i += 1
                i += 1
            i += 1; continue
        # verbatim string @"..." (and $@" / @$")
        j = i
        prefix = ''
        while j < n and s[j] in '@$':
            prefix += s[j]; j += 1
        if '@' in prefix and j < n and s[j] == '"':
            j += 1
            while j < n:
                if s[j] == '"':
                    if j + 1 < n and s[j+1] == '"': j += 2; continue
                    j += 1; break
                if s[j] == '\n': line += 1
                j += 1
            i = j; continue
        if c == '"' or (prefix == '$' and j < n and s[j] == '"'):
            j = j + 1 if prefix == '$' else i + 1
            depth = 0
            while j < n:
                ch = s[j]
                if ch == '\\': j += 2; continue
                if ch == '{' and prefix == '$':
                    if j + 1 < n and s[j+1] == '{': j += 2; continue
                    depth += 1; j += 1; continue
                if ch == '}' and prefix == '$' and depth > 0:
                    depth -= 1; j += 1; continue
                if ch == '"' and depth == 0:
                    j += 1; break
                if ch == '\n': line += 1
                j += 1
            i = j; continue
        if c == '{': curly += 1
        elif c == '}':
            curly -= 1
            if curly < 0: print(f'{path}: curly went negative at line {line}')
        elif c == '(': paren += 1
        elif c == ')': paren -= 1
        elif c == '[': square += 1
        elif c == ']': square -= 1
        i += 1
    print(f'{path}: curly={curly} paren={paren} square={square}')

for p in sys.argv[1:]:
    scan(p)
