# Собирает строки интерфейса Castle Story Plus из декомпиляции (decompiled/csp, decompiled/cspui; в git не кладём).
# Склейки "Only " + n + " of " + m  превращаются в шаблоны "Only {0} of {1}".
# Также: строки внутри Lua-вставок, надписи кругового меню (Name : / Tooltip :), описания функций [Feature].
# Логи отбрасываются. Результат: tools/csp_strings.json  {шаблон: [файлы]}
import json, os, re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIRS = [os.path.join(ROOT, 'decompiled', 'csp'), os.path.join(ROOT, 'decompiled', 'cspui')]
LIT = re.compile(r'(\$?)"((?:[^"\\\n]|\\.)*)"')
LOG = re.compile(r'Log\.Log|LogInfo|LogWarning|LogError|LogDebug|LogMessage|Debug\.Log|\[HarmonyPatch|AccessTools|TypeByName|'
                 r'GetMethod|GetField\(|GetProperty\(|Shader\.Find|SetFloat|SetColor|SetTexture|GetTexture|HasProperty|'
                 r'Path\.Combine|PlayerPrefs|GitHub|http|\bnew AssetKey|LuaInjection\.AddPatch\("[^"]*", "[^"]*", "[^"]*"|\.name = |'
                 r'GameObject\("|Find\("|Exception\(|throw ', re.I)

def unesc(s):
    return re.sub(r'\\(.)', lambda m: {'n': '\n', 't': '\t', 'r': ''}.get(m.group(1), m.group(1)), s)

def interesting(s):
    t = s.strip()
    if len(t) < 2: return False
    if not re.search(r'[A-Za-z]{2}', t): return False
    if t.startswith('##') or t.startswith('_'): return False
    if re.search(r'\.(lua|png|json|dll|txt|cs|shader|cfg)\b', t, re.I): return False
    if re.fullmatch(r'[\w.\-/:]+', t) and not re.fullmatch(r'[A-Z][a-z]+([ \-][A-Za-z]+)*:?', t):
        return False  # ключи, идентификаторы
    if re.fullmatch(r'[A-Z][a-z]+(?:[A-Z][a-z0-9]+)+', t): return False  # CamelCase
    if 'function' in t and ('end' in t or '(' in t) or '\nlocal ' in t or t.startswith('---lyt'): return False  # Lua-код
    return True

def balanced(x):
    d = 0
    for ch in x:
        if ch in '([{': d += 1
        elif ch in ')]}':
            d -= 1
            if d < 0: return False
    return d == 0

def operand_ok(x):
    x = x.strip()
    if not x or not balanced(x): return False
    depth = 0
    for i, ch in enumerate(x):  # на верхнем уровне не должно быть ? : , = ; &&
        if ch in '([{': depth += 1
        elif ch in ')]}': depth -= 1
        elif depth == 0 and ch in '?,;=': return False
    return True

out = {}
def add(s, f):
    s = s.replace('\r', '')
    if interesting(s): out.setdefault(s, set()).add(f)

def from_lua(code, f):
    # строки внутри Lua: "…" и [[…]]; отбрасываем ключи/имена
    for m in re.finditer(r'"((?:[^"\\\n]|\\.)*)"', code):
        s = unesc(m.group(1))
        if ' ' in s.strip() or re.fullmatch(r'[A-Z][a-z]+', s): add(s, f)

def from_wheel(s, f):
    for m in re.finditer(r'^\s*(Name|Tooltip|Title|Description)\s*:\s*(.+)$', s, re.M):
        add(m.group(2).strip(), f)

for d in DIRS:
    for dp, _, fs in os.walk(d):
        for fn in fs:
            if not fn.endswith('.cs'): continue
            for line in open(os.path.join(dp, fn), encoding='utf-8', errors='replace'):
                lits = list(LIT.finditer(line))
                if not lits: continue
                log = bool(LOG.search(line))
                # Lua-код и меню колеса — даже в строках с AddPatch
                for m in lits:
                    raw = unesc(m.group(2))
                    if 'function' in raw or '\nlocal ' in raw or '_t:Add' in raw or 'Name = ||' in raw: from_lua(raw, fn)
                    if 'BUTTON ' in raw or '\nName : ' in raw or 'Tooltip : ' in raw: from_wheel(raw, fn)
                if log: continue
                # склейки
                i = 0
                while i < len(lits):
                    m = lits[i]
                    if m.group(1) == '$':  # $"Now {x}" → Now {0}
                        k = [0]
                        def ph(_):
                            k[0] += 1; return '{%d}' % (k[0] - 1)
                        add(re.sub(r'\{[^{}]*\}', ph, unesc(m.group(2))), fn); i += 1; continue
                    parts = []; n = 0
                    before = line[:m.start()]
                    pm = re.search(r'([\w.\)\]]+(?:\([^()]*\))?)\s*\+\s*$', before)
                    if pm and not before.rstrip().endswith('"+'):
                        parts.append('{0}'); n = 1
                    parts.append(unesc(m.group(2)))
                    j = i
                    while j + 1 < len(lits):
                        between = line[lits[j].end():lits[j + 1].start()]
                        bm = re.fullmatch(r'\s*\+\s*(.*?)\s*\+\s*', between)
                        if between.strip() == '+':
                            parts.append(unesc(lits[j + 1].group(2))); j += 1; continue
                        if bm and operand_ok(bm.group(1)) and '"' not in bm.group(1):
                            parts.append('{%d}' % n); n += 1; parts.append(unesc(lits[j + 1].group(2))); j += 1; continue
                        break
                    after = line[lits[j].end():]
                    am = re.match(r'\s*\+\s*([^;,"?:]+?)\s*(\)|;|,|$)', after)
                    if am and operand_ok(am.group(1)) and not after.lstrip().startswith('+ "'):
                        parts.append('{%d}' % n)
                    s = ''.join(parts)
                    add(s, fn)
                    # отдельные куски тоже (могут выводиться без склейки)
                    if j > i or n:
                        for q in range(i, j + 1): add(unesc(lits[q].group(2)), fn)
                    i = j + 1

res = {k: sorted(v) for k, v in sorted(out.items())}
json.dump(res, open(os.path.join(ROOT, 'tools', 'csp_strings.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
print(len(res), 'строк,', sum(len(k) for k in res), 'символов')
