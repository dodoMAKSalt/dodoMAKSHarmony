# Проверка переводов tools/csp_tr/ru_NN.txt против src_NN.txt: число строк, плейсхолдеры {N}, теги <...>.
import os, re, sys
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
D = os.path.join(ROOT, 'tools', 'csp_tr')
bad = 0
for fn in sorted(os.listdir(D)):
    if not fn.startswith('ru_'): continue
    src = open(os.path.join(D, fn.replace('ru_', 'src_')), encoding='utf-8').read().split('\n')[:-1]
    ru = open(os.path.join(D, fn), encoding='utf-8').read().split('\n')
    if ru and ru[-1] == '': ru = ru[:-1]
    if len(src) != len(ru):
        print(fn, 'строк', len(ru), 'а надо', len(src)); bad += 1
        for i, (a, b) in enumerate(zip(src, ru)):
            pass
        continue
    for i, (a, b) in enumerate(zip(src, ru)):
        if b == '=': continue
        if sorted(re.findall(r'\{\d+\}', a)) != sorted(re.findall(r'\{\d+\}', b)):
            print(fn, i + 1, 'плейсхолдеры:', a[:60], '|', b[:60]); bad += 1
        if a.count('<') != b.count('<'):
            print(fn, i + 1, 'теги:', a[:60], '|', b[:60]); bad += 1
print('ошибок:', bad)
