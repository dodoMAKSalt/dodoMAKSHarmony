# Мои настройки dodoMAKSharmony / Castle Story Plus (снимок)

Как применить: правь значения в `Castle Story\CastleStoryPlus-Unity6\BepInEx\config\` (после первого запуска игры там появятся файлы) или импортируй `my_preset_for_launcher.json` как свой пресет (см. конец файла).

## dodoMAKSharmony (наш мод) — `castlestory.optimizer.cfg`
Включено 27 из 29 функций.

### Включено

**[AI]**
- FastDecisions
- FasterGoalCounting

**[Compat]**
- FastExit

**[Fixes]**
- DisableAnalytics
- FastLoading
- CollectGarbageAfterLoad
- FastLoadWaits
- FixCspAutoCleanup

**[Gc]**
- SliceGc

**[Language]**
- ForceRussian
- TranslateCastleStoryPlus

**[Launcher]**
- Install

**[Performance]**
- LessGarbage
- OverheadSortOnlyOnChange
- CapPhysicsSteps
- FixColliderScaler

**[Profiler]**
- Enabled
- ShowOverlayOnStart
- TrackGarbage
- DetailedHooks
- DetailedOnPort

**[Workshops]**
- Enabled
- ShowLabels
- PieMenuButton
- MaxButton
- CrewMenu

**[World]**
- Enabled

### Выключено

**[AI]**
- ThinkInTurns2

**[Performance]**
- KinematicMovingColliders2

### Небулевы значения, отличающиеся от умолчания
(нет)

## Castle Story Plus — `com.tricky12321.castlestoryplus.cfg`
Включено 107 из 183 функций.

### Включено

**[BaseRanges]**
- ShowRings

**[Crystal]**
- AutoSpawn

**[Features]**
- RandomWorld
- DropGearOnDeath
- AssignNow
- AutoCleanup
- AutoRepair
- BuildJobs
- StuckReset
- DirectTask
- WorkerAI
- TaskReservation
- JobBoard
- LargerTasks
- LoadRelief
- PauseTasks
- PickUpAround
- FasterTaskSearch
- PriorityLevels
- QuarrySpeed
- QuickNextTask
- ResourceReservation
- StockpileChoice
- StockpileConsolidation
- StockpilePickup
- TaskFlow
- TaskPrediction
- UnreachableTasks
- UnreachableThought
- UpdateCheck
- CameraZoomOut
- EnergyHint
- FastStartup
- ItemHint
- MenuTextFields
- MinimapZoom
- ResourceList
- StockpileTooltip
- WardRangeFade
- FastSave
- RenderTuning
- FrameSavers
- Pathfinding
- Ladders
- PassThroughAllies
- TreeStumps
- WildPlants
- WornPaths
- AllyWorkerIcons
- DesyncProtection
- GameChat
- HiddenDeliveries
- LobbyStart
- MaxPlayers
- SavedTeams
- SteamRelay
- FastQuit
- GameLeakFixes
- LuaHeapShrink
- LuaStackShrink
- CsMenus
- FasterLoading
- CraftFinishFix
- WorkshopSetup
- DebugMenu
- Performance
- IdleLog
- SystemLog
- PlayerOrdersFirst
- Retreat
- BuildBlocked
- BuildNeeds
- BuildShortage
- CopyPaste
- CustomBlocks
- DoorRepair
- EmptyStockpile
- LargeStockpile
- MixedStockpiles
- StockpileSpill
- Warehouse

**[FogOfWar]**
- AlwaysOn

**[Loading]**
- LuaChunkReuse
- LuaCompileCache

**[LuaUi]**
- CsBars
- CsBlueprintLibrary
- CsBuildMenus
- CsTooltips
- CsMainMenu
- CsScoreboard
- CsGameMenu
- CsTaskList
- CsModals
- CsSettings
- CsSmokeTest
- CsTaskPopup
- LazyArticles
- CsWorldEditor

**[Multiplayer]**
- Chat

**[Pathfinding]**
- NavMeshExperiment
- NavMeshRoutes
- StraightPaths

**[Performance]**
- ProgressionOnWorkers
- MeasureAllocations
- PlayerJobs

**[RenderTuning]**
- OffscreenShadows

**[ResourceList]**
- Shown

### Выключено

**[BuildShortage]**
- Minified

**[Debug]**
- FastSpeedKeys
- ErrorLog
- FpsCounter
- SceneDump
- WorkerTrace
- DebugMode

**[Features]**
- BaseJobs
- StuckEscape
- WorkLimit
- Upgrades
- MinimapTerrain
- PlusLogo
- WaveDirection
- WaveWarning
- WorldSearch
- Treasure
- AutoSave
- RespawnCost
- RespawnStatus
- EnemyTreeClearing
- Saplings
- WiderVision
- BlueCrystalDeposits
- Metallurgy
- LeanCoal
- MineShafts
- OreMine
- RichIron
- ContinueButton
- SaveAndLeave
- LauncherOptions
- SpeedKeys
- WaveInterval
- Market
- SpawnDistance
- GiantBricktron
- Experience
- CraftLoopLimit
- CraftRoundRobin
- DarkCrystals
- Economy
- QuarryDepth
- QuarryLimit
- WorkshopFetchRange
- WorkshopPriority
- FocusInputGuard
- ArcherAccuracy
- ArtificerHealing
- CriticalHits
- Healtron
- RangedReposition
- UnarmedWorkers
- WallLean
- BlockAnchor
- BlueprintsVisible
- BuildPriority
- DeconstructColour
- DragRelease
- Eyedropper
- GlobalBuildJob
- MixedRacks
- MoveStructure
- RackFilter
- SidePlanks
- WoodOverhang
- WorkSpotReserve
- CallToArms
- KitBundles
- KitReservation
- RangedGuard
- SoldierPosts

**[Performance]**
- MeasureAtStart
- MeasureModPatches
- SpikeLog

**[Startup]**
- FastStartup

### Небулевы значения, отличающиеся от умолчания
- [LuaUi] DefaultsVersion = 1   (по умолчанию 0)
- [RandomWorld] Seed = 3b01c86e-b2f3-478f-a0e3-b41f52ee4c3b   (по умолчанию )

## Настройки мира (`dodoMAKSharmony-world.txt`, формат `id|включено|значение`)
- cap = 100
- tasks = 200
- ubase = 10
- ustep = 0

## Как это передать другу и его Claude
1. Другу поставить мод по инструкции в README (Castle Story Plus -> один запуск игры -> `dodoMAKSharmony-Settings.exe`).
2. Положить `my_preset_for_launcher.json` в `Castle Story\CastleStoryPlus-Unity6\BepInEx\config\` под именем `dodoMAKSharmony-presets.json`
   и в лаунчере (кнопка «Моды») нажать пресет «Дима» — выставятся все галочки как у меня. Если файл уже есть, добавить содержимое как ещё один ключ.
3. Числа и настройки мира (юниты, цены энергии, лимит задач…) — в окне «Подробные настройки» в лобби; значения из раздела выше.
