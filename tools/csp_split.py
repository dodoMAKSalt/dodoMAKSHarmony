# Отбор строк Castle Story Plus для перевода и деление на порции tools/csp_tr/src_NN.txt
# (одна строка = одна фраза; \n и \t экранированы). Переводы — tools/csp_tr/ru_NN.txt, строка в строку.
import json, os, re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
d = json.load(open(os.path.join(ROOT, 'tools', 'csp_strings.json'), encoding='utf-8'))
NS_SKIP = re.compile(r'^(LuaCompileCache|UpdateCheck|UpdateInstaller|SceneDump|SpikeLog|AssemblyReload|UiAssembly|BlockDump|'
                     r'LoadWatch|SystemReport|ModLoader|Installer|PortLauncher|Compat\w*)\.cs$')
keep = []
for k, v in d.items():
    if all(NS_SKIP.match(f) for f in v): continue
    t = k.strip()
    if '{"' in t or '":' in t or t.startswith('Assembly') or 'yyyy' in t: continue
    if re.match(r'^[a-z][\w ]*: ', t) and not re.search(r'[.!]$', t): continue  # "jobboard: memo..." — лог
    if re.fullmatch(r'(BUTTON|MENU) \w+', t): continue
    keep.append(k)
keep.sort()

def esc(s):
    return s.replace('\\', '\\\\').replace('\n', '\\n').replace('\t', '\\t')

out = os.path.join(ROOT, 'tools', 'csp_tr')
os.makedirs(out, exist_ok=True)
batches, cur, n = [], [], 0
for k in keep:
    cur.append(k); n += len(k)
    if n > 9000: batches.append(cur); cur, n = [], 0
if cur: batches.append(cur)
for i, c in enumerate(batches):
    with open(os.path.join(out, 'src_%02d.txt' % i), 'w', encoding='utf-8', newline='\n') as f:
        for k in c: f.write(esc(k) + '\n')
print(len(keep), 'строк,', sum(len(k) for k in keep), 'символов,', len(batches), 'порций')
