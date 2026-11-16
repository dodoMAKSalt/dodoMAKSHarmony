# Промт для нового чата (скопируй блок ниже в первое сообщение)

```
Продолжаем мод dodoMAKSharmony для Castle Story (папка проекта C:\Users\dodom\Desktop\clod). Сначала прочитай CLAUDE.md
(устройство мода, пути, правила работы, раздел «Состояние на 16.11.2026» и «Правила работы с пользователем»), затем docs/HANDOFF.md,
docs/game_code_notes.md и CHANGELOG.md. Память проекта подгрузится сама.

Сейчас: версия 0.19.4 на порте Castle Story Plus (Unity 6). Есть меню настроек (Settings/, dodoMAKSharmony-Settings.exe) с двумя блоками
функций и пресетами, GC кусками (GcSlicer), исправление AutoCleanup, перевод обучения через game_ru, release/ + README.md для друга-тестера.

Правила: общаюсь по-русски; игру сам не запускай без моей просьбы; мои конфиги не меняй без спроса; большие логи и код игры читай через агента
на Haiku (model haiku), а сам делай выводы; после каждой рабочей сборки — версия +1, dotnet build, коммит по-русски и push;
поддержку пиратских копий игры не делаем.

Задачи на эту сессию: <впиши, что делаем: новые фичи / что проверили в игре / что сломалось>.
```

## Что уже сделано и как устроено (кратко)

- Репозиторий: https://github.com/dodoMAKSalt/dodoMAKSHarmony (remote `origin`, ветка `main`, публичный). Ссылка для тестера: `release/dodoMAKSharmony-Settings.exe`
  (raw-ссылка в `README.md`).
- Сборка: `dotnet build Settings/CastleStoryPlusSettings.csproj -c Release` (собирает и мод net35, и меню net48; DLL мода вшивается в exe и копируется
  в плагины игры), затем `cp dodoMAKSharmony-Settings.exe release/`, коммит, push. Версия — `Plugin.Version` + `<Version>` в `CastleStoryOptimizer.csproj`,
  `Installer/CastleStoryModInstaller.csproj`, `Settings/CastleStoryPlusSettings.csproj`.
- Отчёты мода (в `…\CastleStoryPlus-Unity6\BepInEx\`, перезаписываются при каждом запуске игры): `CastleStoryOptimizer_report.txt`, `_spikes.txt`,
  `_timeline.csv`, `_slowlua.txt`, `_untranslated.txt`, `LogOutput.log`; лог игры — `%TEMP%\Castle Story\logs\brix.log`.
  Клавиши замерщика на порте: Shift+F8 оверлей, Shift+F9 отчёт (с Ctrl — полный подсчёт сцены), Shift+F7 подробный замер.
- Подробный замер на порте включается в `castlestory.optimizer.cfg`: `[Profiler] DetailedOnPort = true`, `TrackGarbage = true` (стоит 1–2 мс на кадр).

## Открытые задачи (по приоритету)

1. Проверить в игре: перевод обучения, исправление AutoCleanup (`[Fixes] FixCspAutoCleanup`), установку меню на чистой системе у друга.
2. Сессия с подробным замером на версии ≥0.19: прочитать строки «[Карта]» (LodUpdater, Jardinier) и мусор по методам → решить про оптимизацию
   `LodUpdater.GetPaddedLowerMvs`/`RenderLod` и бюджет времени для `Jardinier.Travailler` (описание кода — `docs/game_code_notes.md`).
3. Длинная партия (несколько часов): рост минимума кучи после сборок, поведение `GcSlicer` (`TriggerMB`, `PanicMB`).
4. Остаточный Lua-пик в `Picking.Update` (~360 мс один раз после загрузки) — смотреть `_slowlua.txt`.
5. Непереведённые строки из `_untranslated.txt`; при выходе новой версии Castle Story Plus — обновить `Lang/csp_ru.txt` (инструкция в `CLAUDE.md`).
6. Идеи для новых фич: пресеты в меню по сценариям, проверка совместимости версий Castle Story Plus, страница Releases на GitHub (нужен токен).

## Выводы по замерам (для контекста)

| | кадр | FPS | фризы >100 мс | GC-пауза |
|---|---|---|---|---|
| Ванила, Unity 5.6, большой замок | 20 мс | ~50 | ~3 в минуту | 0,7–1 с раз в ~30 с |
| + BetterThanCastle + наш мод (0.16.x) | — | 84–93 (было 51) | в логах нет | 0,7–1 с |
| Порт Unity 6, новые карты | 4,5–6,9 мс | 147–226 | 1 за 8 мин (0.19) | ~67 мс раз в 2–4 мин |
| Порт, сохранение «Тест 2» | ~7 мс | 130–150 (по оверлею) | — | — |

Сравнение неточное: разные сохранения. Для честного сравнения прогнать «Тест 2» на порте 10 минут и приложить отчёт.
