# dodoMAKSharmony — мод-оптимизатор для Castle Story

BepInEx 5 плагин (`CastleStoryOptimizer.dll`, GUID `castlestory.optimizer`, имя `dodoMAKSharmony`) для Castle Story
(Steam, Unity 5.6.5f1, Mono 2.x / Boehm GC, MoonSharp Lua). Цели: убрать фризы, поднять FPS (цель — стабильные 120),
ускорить загрузку, починить ИИ бриктронов, полный русский перевод. Плюс установщик для друга.
Общение с пользователем — **на русском**; все строки интерфейса/логов мода — на русском.

## Главные правила
- **Файлы игры не меняем.** Всё — Harmony-патчами в памяти (prefix/postfix/transpiler) и подменой текста при чтении
  (Lua — `BrixScriptLoader.LoadFile`, круговое меню — `Livre.Lire`).
- Каждая функция — отдельная опция в `BepInEx/config/castlestory.optimizer.cfg` (можно выключить).
- Перед правкой — **замер** (свой профайлер), после — снова замер. Не гадать.
- Пользователь всегда тестирует **одно и то же сохранение, не пересохраняя его**; логи сравнимы между сессиями.
- Инструменты пользователь ставит сам («дай ссылку — скачаю»).

## Порт Castle Story Plus (Unity 6) — основная версия с 0.17.0
- github.com/Tricky12321/CastleStoryPlus (**GPL v3 — код не копировать**). Порт в `<Игра>\CastleStoryPlus-Unity6\`
  (свой BepInEx 5.4.23.5: плагины/конфиг/`LogOutput.log` там). Doorstop игры → `CastleStoryPlus.PortLauncher.dll`.
  Оригинальный `doorstop_config.ini` — в `backup_before_port/`.
- `PortCompat` (обнаружение, `On(cfg, keepOnPort)`, клавиши замерщика с Shift). Всё, что дублирует CSP, на порте выключено.
- Клавиши CSP: F6 рентген, F7 производительность, F8 отладка, F9 системный лог. Наши — Shift+F7/F8/F9.
- Перевод CSP: `CspTranslator` (перехват `UI.Text.text`, словарь `Lang/csp_ru.txt`: точные строки, шаблоны {0}, куски склеек,
  по словам). Обновить под новую версию CSP: декомпилировать их DLL в `decompiled/csp`, `decompiled/cspui` →
  `tools/csp_strings.py` → `tools/csp_split.py` → перевести новые `tools/csp_tr/ru_NN.txt` (строка в строку, `=` — оставить)
  → `tools/csp_check.py` → `tools/csp_build.py`; проверка — `tools/csp_test.py "строка"`.
- Сундуки/звери CSP (Treasure) — обычная геймплейная функция, отключается в меню Settings/ (dodoMAKSharmony-Settings.exe) или `[Features] Treasure` в их конфиге.

- Меню настроек `Settings/` (`dodoMAKSharmony-Settings.exe`, ярлык на рабочем столе): два блока — функции Castle Story Plus (`[Features]` их конфига) и наши (`castlestory.optimizer.cfg`), в каждом подгруппы; пресеты «Как в оригинале» / «Включить оптимизацию». Новая опция нашего мода — добавить строку в таблицу `Ours` в `SettingsForm.cs`.

- Выпуск для друга: после `dotnet build Settings/CastleStoryPlusSettings.csproj -c Release` скопировать `dodoMAKSharmony-Settings.exe` в `release/` и закоммитить (ссылка в `README.md`; DLL мода вшита в exe, меню само ставит/обновляет её в папку плагинов порта). Версия: `Plugin.Version` + `<Version>` в трёх csproj (мод, установщик, Settings).

## Пути
- Игра: `C:\Program Files (x86)\Steam\steamapps\common\Castle Story`
  - мод: `BepInEx\plugins\CastleStoryOptimizer\`, конфиг: `BepInEx\config\castlestory.optimizer.cfg`
  - отчёты: `BepInEx\CastleStoryOptimizer_report.txt` (раз в минуту + F9), `_spikes.txt` (кадры >50 мс), `_timeline.csv`
  - лог BepInEx: `BepInEx\LogOutput.log`; лог самой игры (log4net, ошибки загрузки!): `%TEMP%\Castle Story\logs\brix.log`
  - Lua/данные игры: `Info\Lua\...`, `Info\Livre\...`
- Декомпиляция игры: `decompiled\` (ilspycmd, в git НЕ кладём). Повторить:
  `DOTNET_DefaultStackSize=10000000 ilspycmd -p -o decompiled\<asm> "<Игра>\Castle Story_Data\Managed\<asm>.dll"`

## Сборка
- `dotnet build Installer/CastleStoryModInstaller.csproj -c Release` — собирает мод (net35) и установщик (net48, WinForms),
  мод автоматически копируется в папку плагинов игры (если игра запущена — DLL заменится, подхватится при перезапуске),
  установщик — в корень (`CastleStoryModInstaller.exe`).
- Для установщика нужен `BepInEx_win_x64_5.4.23.6.zip` в корне (не в git): https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.6
- Версия: `Plugin.Version` в `Plugin.cs` + `<Version>` в обоих csproj — повышать при каждой сборке для пользователя.
- Русский перевод: `tools/build_russian.py` (+`tools/ru_extra.py`) → `CastleStoryOptimizer/Lang/` (вшивается в DLL).

## Устройство мода (CastleStoryOptimizer/)
- `Plugin.cs` — Awake: конфиг и применение модулей; Update/OnGUI мода (с замером «[Мод] …»).
- Профайлер: `FrameProfiler` (оверлей F8, отчёт F9, мусор по методам), `FrameSplit` (этапы кадра),
  `ProfilerPatches` (перехват Update/корутин/Lua/путей игры; `DetailedHooks` в конфиге — дорого, ~1–2 мс), `PhysicsWatch`, `RenderTiming`.
- Загрузка: `LoadingPatches` (фазы GameLoader + запасной путь через `LoadNewGameFromGameParams`/сигнал `LevelReady`),
  `FastLoadPatches`, `LuaCompileCache`, `LoadingDiagnostics` (Lua-меню несколькими шагами за кадр), `GcControl` (пауза Boehm GC на загрузке).
- ИИ: `AiFixes` (один носильщик на блок, быстрее решения, бюджеты путей, `StuckWatcher` — застрявшие, авто-копание),
  `AiPerf` (подсчёт целей добычи без HasFlag/Slinq), `StorageReservations` (бронь места в складах).
- Фризы/FPS: `TerrainColliderPatches` (коллайдеры рельефа живут 20 с), `AnimatorCulling`, `PhysicsPatches`, `SoundThrottle`,
  `GcFixes` (TimerTracker без мусора), `MovingColliderFix` (эксперимент, выключен).
- Прочее: `RussianLanguage`, `MenuBranding`, `StationPriorities` + `PieMenuPriority` (приоритеты мастерских, кнопка в круговом меню),
  `NetworkPatches` (фриз 11 с на DNS при хосте), `AnalyticsPatches`, `Compat` (таймер 1 мс, частота экрана, быстрый выход на Alt+F4).
- Совместимость с **BetterThanCastle** (github.com/NNLEKKK/BetterThanCastle, **GPL v3 — их код не копировать**, только идеи):
  `BtcCompat` (обнаружение, общий `Livre.Lire`, отключение наших дублей, PageUp/PageDown отданы им),
  `BtcTuning` (ускорение их мода: статистика в реплей 1/с вместо 10/с, File.Exists не каждый кадр, без GUILayout и т.п.).
  Наш мод грузится после них (`BepInDependency` soft). Их мод НЕ обязателен.
- `archive/MedicMod/` — отложенный мод «Медики» (предметы/стол/рецепты работают, бриктроны не берут чертёж стола в работу).
  Заметки об устройстве предметов/рецептов/фабрик игры — `archive/MedicMod/src/DESIGN.md`.

## Что важно знать об игре (грабли)
- CPU-bound: видеокарта простаивает; кадр ~16–20 мс на большом замке, ~8 мс в новом мире.
- `FindObjectsOfType` очень дорогой (до ~50 мс при ~11 тыс. объектов) — не вызывать периодически, вести свои списки.
- `Enum.HasFlag` в Mono 2.x упаковывает и медленный; LINQ/Slinq/замыкания в частых местах — мусор.
- Boehm GC: куча ~1.4–2 ГБ, каждая сборка ~0.7–1 с. Главный оставшийся фриз — сборка мусора; бороться уменьшением мусора.
- `transform.hasChanged` игра использует как флаг кэша — не сбрасывать.
- Шаблоны фабрик: `PeekTemplate` нельзя `Factory.Instantiate`; у клонов надо поправить `FactoryImprint._assetKey`
  и вызвать `Initialize()` (иначе пустые `_gcPoco`/`Apparence._principale` → падения загрузки). Добавлять шаблоны — до `ObjectPoolSingleton.GeneratePools`.
- Сигналы `GameSignals` очищаются при смене сцены — подписываться заново для каждой загрузки.
- Мелкие методы (геттеры, `GameSignals.Invoke`) Mono может инлайнить — Harmony-патч на них ненадёжен.
- В игре есть свои логические ошибки Lua при загрузке («attempt to index a nil value») — они были и без мода.

## Процесс работы
1. Пользователь играет → читаем отчёты/спайки/лог (см. пути), ищем по цифрам.
2. Правка → `dotnet build ...` → версия +1 → пользователь перезапускает игру и проверяет.
3. Коммит в git после каждой рабочей версии (сообщение по-русски, что и зачем).
