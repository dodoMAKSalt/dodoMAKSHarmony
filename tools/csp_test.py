# Проверка словаря csp_ru.txt той же логикой, что CspTranslator.cs (точные → шаблоны → строки → куски).
import os, re, sys
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
def unesc(s): return re.sub(r'\\(.)', lambda m: {'n': '\n', 't': '\t'}.get(m.group(1), m.group(1)), s)
exact, tpls, frags = {}, [], []
PH = re.compile(r'\{(\d+)\}')
for line in open(os.path.join(ROOT, 'CastleStoryOptimizer', 'Lang', 'csp_ru.txt'), encoding='utf-8'):
    line = line.rstrip('\n')
    if '\t' not in line: continue
    en, ru = map(unesc, line.split('\t', 1))
    if PH.search(en):
        parts = PH.split(en); rx = '^'; anchor = ''
        for i, p in enumerate(parts):
            if i % 2 == 0: rx += re.escape(p); anchor = p if len(p) > len(anchor) else anchor
            else: rx += '(?P<p%s>.+?)' % p
        if len(anchor.strip()) >= 2: tpls.append((anchor, re.compile(rx + '$', re.S), ru))
    else:
        exact[en] = ru; t = en.strip()
        if t != en: exact.setdefault(t, ru.strip())
        if len(t) >= 4 and (en != t or not en[0].isalnum() or not en[-1].isalnum()) and len(re.findall('[A-Za-z]', t)) >= 3:
            frags.append((en, ru))
frags.sort(key=lambda x: -len(x[0])); tpls.sort(key=lambda x: -len(x[0]))
def tr(s, d=0):
    if s in exact: return exact[s]
    t = s.strip()
    if t != s and t in exact: return s.replace(t, exact[t], 1)
    if d > 3: return None
    for a, rx, ru in tpls:
        if a not in s: continue
        m = rx.match(s)
        if m: return PH.sub(lambda pm: (tr(m.group('p' + pm.group(1)), d + 1) or m.group('p' + pm.group(1))) if ('p' + pm.group(1)) in m.groupdict() else pm.group(0), ru)
    if '\n' in s:
        ls = s.split('\n'); r = [tr(l, d + 1) or l for l in ls]
        if r != ls: return '\n'.join(r)
    cur = s
    for k, v in frags:
        if k in cur: cur = cur.replace(k, v)
    if cur != s: return cur
    if len(s) <= 48 and ' ' in s:
        ws = s.split(' '); r = [exact.get(w, w) if len(w) > 1 else w for w in ws]
        if r != ws: return ' '.join(r)
    return None
tests = sys.argv[1:] or ["Only 3 of 5 got Archer: not enough sets.", "Next wave in 2:31", "Amount: 7", "LOAD GAME", "Coal",
         "Fog of war: on", "Tier 2 Steel", "Cost: 50 dark crystals (12 free in the stockpiles) + the sacrificed worker",
         "Bob set to Knight.", "Waves every 10 min.", "In stock 25 (+3)", "Healed 4 bricktrons.", "Treasure", "Ranged: set",
         "Loop until stock: 20 (7 now)", "QUEUE (2/20)", "Building the interface (12 menus left)", "Привет мир", "Archer 3/5"]
for t in tests: print(repr(t), '->', repr(tr(t)))
print(len(exact), 'точных,', len(tpls), 'шаблонов,', len(frags), 'кусков')
