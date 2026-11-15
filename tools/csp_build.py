# Собирает словарь перевода Castle Story Plus: tools/csp_tr/src_NN + ru_NN → CastleStoryOptimizer/Lang/csp_ru.txt
# Формат: строка "оригинал<TAB>перевод", \n \t \\ экранированы. Пробелы по краям берутся из оригинала.
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
D = os.path.join(ROOT, 'tools', 'csp_tr')

def unesc(s):
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == '\\' and i + 1 < len(s):
            n = s[i + 1]; out.append({'n': '\n', 't': '\t', '\\': '\\'}.get(n, '\\' + n)); i += 2
        else:
            out.append(c); i += 1
    return ''.join(out)

def esc(s):
    return s.replace('\\', '\\\\').replace('\n', '\\n').replace('\t', '\\t')

pairs = {}
for fn in sorted(os.listdir(D)):
    if not fn.startswith('ru_'): continue
    src = open(os.path.join(D, fn.replace('ru_', 'src_')), encoding='utf-8').read().split('\n')[:-1]
    ru = open(os.path.join(D, fn), encoding='utf-8').read().split('\n')
    if ru and ru[-1] == '': ru = ru[:-1]
    assert len(src) == len(ru), fn
    for a, b in zip(src, ru):
        if b.strip() == '=': continue
        a, b = unesc(a), unesc(b)
        lead = a[:len(a) - len(a.lstrip(' '))]
        trail = a[len(a.rstrip(' ')):]
        v = lead + b.strip(' ') + trail
        if v == a: continue
        pairs[a] = v
        if a.startswith('&&') and v.startswith('&&'):
            pairs.setdefault(a[2:], v[2:])

out = os.path.join(ROOT, 'CastleStoryOptimizer', 'Lang', 'csp_ru.txt')
with open(out, 'w', encoding='utf-8', newline='\n') as f:
    for k in sorted(pairs):
        f.write(esc(k) + '\t' + esc(pairs[k]) + '\n')
print(len(pairs), 'переводов ->', out)
