"""
Собирает русский перевод для мода dodoMAKSharmony.

Основа: перевод из Мастерской Steam (id 1925721026, сообщество) — покрывает все ключи,
но ~200 строк (обучение, справка, ивент) в нём остались на английском.
Поверх кладём наши переводы из ru_extra.py. Ключи, которых нет в основе, берём из английского.

Запуск:  python -I tools/build_russian.py
Результат: CastleStoryOptimizer/Lang/language.csv и meta.json (вшиваются в DLL мода).
"""
import csv, io, json, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "tools"))
from ru_extra import RU, MOD  # noqa: E402

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Castle Story"
WORKSHOP = r"C:\Program Files (x86)\Steam\steamapps\workshop\content\227860\1925721026\language.csv"
ENGLISH = os.path.join(GAME, r"Info\Languages\English\language.csv")
OUT = os.path.join(ROOT, "CastleStoryOptimizer", "Lang")

LANGUAGE_NAME = "Русский (dodoMAKSharmony)"


def read_rows(path):
    with open(path, encoding="utf-8-sig", newline="") as f:
        return list(csv.reader(f))


def main():
    en_rows = read_rows(ENGLISH)
    base = {r[0]: (r[1] if len(r) > 1 else "") for r in read_rows(WORKSHOP) if r}

    out_rows, from_base, from_extra, from_en = [], 0, 0, 0
    for r in en_rows:
        if not r:
            continue
        key = r[0]
        if key == "Keys":
            out_rows.append(["Keys", "Russian", ""])
            continue
        if not key.startswith("##"):           # заголовки разделов
            out_rows.append([key, "", ""])
            continue
        if key in RU:
            val, from_extra = RU[key], from_extra + 1
        elif base.get(key, "").strip():
            val, from_base = base[key], from_base + 1
        else:
            val, from_en = (r[1] if len(r) > 1 else ""), from_en + 1
        out_rows.append([key, val, ""])

    out_rows.append(["DODOMAKSHARMONY", "", ""])
    for k, v in MOD.items():
        out_rows.append([k, v, ""])

    unknown = [k for k in RU if k not in {r[0] for r in en_rows if r}]
    if unknown:
        print("ВНИМАНИЕ: ключей нет в игре:", unknown)

    os.makedirs(OUT, exist_ok=True)
    buf = io.StringIO()
    csv.writer(buf, lineterminator="\r\n").writerows(out_rows)
    with open(os.path.join(OUT, "language.csv"), "w", encoding="utf-8", newline="") as f:
        f.write(buf.getvalue())

    meta = {
        "__CODENAME": "loc_ru_dodomaksharmony",
        "__DISPLAYNAME": "Russian (dodoMAKSharmony)",
        "__ASSETVERSION": 1,
        "__PLAYABLECONTEXT": 7,
        "__LANGUAGE_CODE": "ru",
        "__LANGUAGE_NAME": LANGUAGE_NAME,
    }
    with open(os.path.join(OUT, "meta.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False, indent=2)

    print(f"Готово: из Мастерской {from_base}, наших {from_extra}, английских {from_en}")


if __name__ == "__main__":
    main()
