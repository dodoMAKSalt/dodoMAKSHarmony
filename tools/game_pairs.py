# Пары «английский текст игры -> наш русский» из language.csv (для CspTranslator как страховка:
# на порте часть текстов (обучение, справка) приходит в UI.Text на английском, хотя русский перевод в языке есть).
# Запуск: python -I tools/game_pairs.py   Результат: CastleStoryOptimizer/Lang/game_ru.txt
import csv, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Castle Story"
EN = os.path.join(GAME, "Info", "Languages", "English", "language.csv")
RU = os.path.join(ROOT, "CastleStoryOptimizer", "Lang", "language.csv")
OUT = os.path.join(ROOT, "CastleStoryOptimizer", "Lang", "game_ru.txt")

BS = chr(92)   # обратная косая
NL = chr(10)   # перевод строки
CR = chr(13)
TAB = chr(9)


def rows(path):
    with open(path, encoding="utf-8-sig", newline="") as f:
        return {r[0]: (r[1] if len(r) > 1 else "") for r in csv.reader(f) if r and r[0].startswith("##")}


def esc(s):
    # \n (два символа) из csv и настоящие переводы строк -> один настоящий перевод строки -> пишем как \n
    s = s.replace(CR + NL, NL).replace(BS + "n", NL)
    return s.replace(BS, BS + BS).replace(NL, BS + "n").replace(TAB, BS + "t")


def main():
    en, ru = rows(EN), rows(RU)
    pairs = {}
    for k, e in en.items():
        r = ru.get(k, "")
        e, r = e.strip(), r.strip()
        if not e or not r or e == r:
            continue
        if not any(c.isalpha() for c in e) or len(e) < 3:
            continue
        pairs[esc(e)] = esc(r)
    with open(OUT, "w", encoding="utf-8", newline=NL) as f:
        for e in sorted(pairs):
            f.write(e + TAB + pairs[e] + NL)
    print(len(pairs), "pairs ->", OUT)


main()
