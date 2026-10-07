#!/usr/bin/env python3
"""Move inline PT-BR/EN literal pairs in src/RHI.Desktop into the UiStrings key table (#79).

Idempotent: re-run after a merge to migrate pairs that arrived from other branches. Pairs whose two arguments are
string literals (or literal concatenations) become T("Key") / TF("Key", args...) (or UiLocalizationService.Text /
Format for the qualified Choose/ChooseFormat calls). Calls with computed arguments are reported and left alone.
Usage: tools/l10n-migrate.py <repo-root> [--dry-run]
An inline pair is any L("pt", "en") / LF("pt", "en", args) call (define the helper locally while migrating).
"""
import re, sys, os, glob

ROOT = sys.argv[1]
DRY = '--dry-run' in sys.argv
TABLE = os.path.join(ROOT, 'src/RHI.Desktop/Localization/UiStrings.cs')
SERVICE = os.path.join(ROOT, 'src/RHI.Desktop/Localization/UiLocalizationService.cs')

def parse_literal(src, i):
    """Parse one C# string literal at src[i]; return (value, end) or None. Interpolated strings are not literals."""
    if src.startswith('@"', i):
        j = i + 2; out = []
        while True:
            k = src.index('"', j)
            if src.startswith('""', k):
                out.append(src[j:k] + '"'); j = k + 2; continue
            out.append(src[j:k]); return ''.join(out), k + 1
    if src[i] != '"':
        return None
    j = i + 1; out = []
    esc = {'n': '\n', 't': '\t', 'r': '\r', '"': '"', '\\': '\\', '0': '\0', "'": "'"}
    while True:
        c = src[j]
        if c == '\\':
            n = src[j + 1]
            if n == 'u':
                out.append(chr(int(src[j + 2:j + 6], 16))); j += 6; continue
            out.append(esc[n]); j += 2; continue
        if c == '"':
            return ''.join(out), j + 1
        if c == '\n':
            return None
        out.append(c); j += 1

def skip_ws(src, i):
    while i < len(src) and src[i] in ' \t\r\n':
        i += 1
    return i

def parse_literal_expr(src, i):
    """literal ( + literal )* followed by ',' or ')'."""
    i = skip_ws(src, i)
    r = parse_literal(src, i)
    if not r:
        return None
    parts = [r[0]]; i = r[1]
    while True:
        k = skip_ws(src, i)
        if k < len(src) and src[k] == '+':
            k = skip_ws(src, k + 1)
            r = parse_literal(src, k)
            if not r:
                return None
            parts.append(r[0]); i = r[1]; continue
        if k < len(src) and src[k] in ',)':
            return ''.join(parts), k
        return None

def find_close(src, i):
    """i points just after '('; return index of the matching ')' (strings/chars aware)."""
    depth = 1; j = i
    while j < len(src):
        c = src[j]
        if c == '"' or src.startswith('@"', j) or src.startswith('$"', j) or src.startswith('$@"', j):
            # skip any string (interpolated holes may contain parens but rarely strings with quotes)
            k = j
            if src[k] == '$': k += 1
            verb = src[k] == '@'
            if verb: k += 1
            k += 1
            while True:
                if verb:
                    if src[k] == '"' and src[k + 1:k + 2] == '"': k += 2; continue
                    if src[k] == '"': break
                else:
                    if src[k] == '\\': k += 2; continue
                    if src[k] == '"': break
                k += 1
            j = k + 1; continue
        if c == "'":
            j = src.index("'", j + 3) + 1 if src[j + 1] == '\\' else j + 3
            continue
        if c == '(':
            depth += 1
        elif c == ')':
            depth -= 1
            if depth == 0:
                return j
        j += 1
    raise ValueError('unbalanced')

def cs_literal(value):
    s = value.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n').replace('\t', '\\t').replace('\r', '\\r')
    return '"' + s + '"'

STOP = {'a', 'an', 'the', 'to', 'of', 'and', 'or', 'is', 'are', 'be', 'for', 'in', 'on', 'it', 'this', 'that', 'with'}

def key_name(prefix, en, taken):
    text = re.sub(r'\{\d+[^}]*\}', ' ', en)
    words = [w for w in re.findall(r'[A-Za-z0-9]+', text)]
    sig = [w for w in words if w.lower() not in STOP] or words
    name = ''.join(w[:1].upper() + w[1:] for w in sig[:6])[:48] or 'Symbol'
    if name[0].isdigit():
        name = 'N' + name
    key = f'{prefix}.{name}'; n = 2
    while key in taken:
        key = f'{prefix}.{name}{n}'; n += 1
    return key

def load_table():
    entries = {}
    if os.path.exists(TABLE):
        src = open(TABLE, encoding='utf-8').read()
        for m in re.finditer(r'\("([^"]+)",\s*', src):
            i = m.end()
            pt = parse_literal_expr(src, i)
            if not pt: continue
            en = parse_literal_expr(src, pt[1] + 1)
            if not en: continue
            entries[m.group(1)] = (pt[0], en[0])
    return entries

def existing_service_keys():
    src = open(SERVICE, encoding='utf-8').read()
    return set(re.findall(r'\["([A-Za-z0-9_.]+)"\]\s*=', src))

PREFIX = {
    'MainWindowViewModel.cs': 'Vm',
    'ComponentUpdateHistoryPresenter.cs': 'Hist',
    'ReShadeHotkeyEditor.cs': 'Key',
}

CALL = re.compile(r'(?<![A-Za-z0-9_.])(L|LF)\(|UiLocalizationService\.(Choose|ChooseFormat)\(')

def migrate():
    table = load_table()
    by_pair = {v: k for k, v in table.items()}
    taken = set(table) | existing_service_keys()
    skipped = []
    changed_files = {}
    files = sorted(glob.glob(os.path.join(ROOT, 'src/RHI.Desktop/**/*.cs'), recursive=True))
    for f in files:
        if f.endswith('UiLocalizationService.cs') or f.endswith('UiStrings.cs') or '/obj/' in f or '/bin/' in f:
            continue
        src = open(f, encoding='utf-8').read()
        prefix = PREFIX.get(os.path.basename(f), 'Win')
        out = []; pos = 0; n = 0
        for m in CALL.finditer(src):
            if m.start() < pos:
                continue
            # skip the helper definitions themselves
            line_start = src.rfind('\n', 0, m.start()) + 1
            line = src[line_start:src.find('\n', m.start())]
            if re.search(r'static string (L|LF)\(string', line):
                continue
            kind = m.group(1) or m.group(2)
            fmt = kind in ('LF', 'ChooseFormat')
            a = parse_literal_expr(src, m.end())
            if not a or src[a[1]] != ',':
                skipped.append((f, src.count('\n', 0, m.start()) + 1, line.strip()[:100])); continue
            b = parse_literal_expr(src, a[1] + 1)
            if not b:
                skipped.append((f, src.count('\n', 0, m.start()) + 1, line.strip()[:100])); continue
            close = find_close(src, m.end())
            pair = (a[0], b[0])
            key = by_pair.get(pair)
            if key is None:
                key = key_name(prefix, b[0], taken)
                taken.add(key); table[key] = pair; by_pair[pair] = key
            rest = src[b[1]:close]  # ')' or ', args...'
            qualified = m.group(2) is not None
            if fmt:
                head = 'UiLocalizationService.Format(' if qualified else 'TF('
            else:
                head = 'UiLocalizationService.Text(' if qualified else 'T('
            out.append(src[pos:m.start()]); out.append(f'{head}"{key}"{rest})')
            pos = close + 1; n += 1
        if n:
            out.append(src[pos:])
            changed_files[f] = (''.join(out), n)
    return table, changed_files, skipped

def write_table(table):
    lines = ['// Seeded by tools/l10n-migrate.py; edit entries by hand freely (the script keeps existing keys).',
             '// One row per UI string: key, PT-BR, EN. Keys are looked up through UiLocalizationService.Text/Format.',
             'namespace RHI.Desktop.Localization;', '',
             'internal static class UiStrings', '{',
             '    internal static readonly (string Key, string PtBr, string EnUs)[] Entries =', '    [']
    rows = [f'        ("{k}", {cs_literal(pt)}, {cs_literal(en)})' for k, (pt, en) in table.items()]
    lines.append(',\n'.join(rows)); lines += ['    ];', '}', '']
    open(TABLE, 'w', encoding='utf-8').write('\n'.join(lines))

if __name__ == '__main__':
    table, changed, skipped = migrate()
    print(f'table entries: {len(table)}; files changed: {len(changed)}; calls: {sum(n for _, n in changed.values())}')
    for f, line, text in skipped:
        print(f'SKIPPED (computed argument) {os.path.relpath(f, ROOT)}:{line}: {text}')
    if not DRY:
        write_table(table)
        for f, (text, _) in changed.items():
            open(f, 'w', encoding='utf-8').write(text)
