# Заметки по коду игры (декомпилированный Brix/Assembly-CSharp-firstpass, Unity 5.6)

Источник: разбор кода по замерам порта (16.11.2026). Код оригинальной игры; на порте (Unity 6) возможны отличия — перед правкой
проверять методы рефлексией/логом. Пути относительно `decompiled\` (в git не лежит).

## Уже известно из замеров
- `GameClock.Update/FixedUpdate` — просто диспетчер: вызывает `BrixUpdate` всех компонентов; его время и мусор — сумма вложенных.
- Lua-фризы ~100 мс при выборе: `CraftingStation.lua_UpdateSelection` / `Project.OnSelect` → `LuaHookBase.InvokeFromCLR` → Lua
  `GameMenu.lua` (`ConnectWhen(CraftingStation.hk_OnSetSelected, …)`). Лечится `CsMenus` Castle Story Plus (C#-меню).
- Куча растёт 130–260 КБ/с после сборок; выделение ~1 МБ/с (без подробного замера; с ним ~2,9 МБ/с — сами перехваты мусорят).

## Кандидаты на оптимизацию (не сделано)
1. **LodUpdater** (`Brix.Core\Brix.Engine.FlatWorld\LodUpdater.cs`) — разовые пики до 280 мс. `Update` (661–676) раз в 5 с при dirty создаёт
   корутину; `QuickDrawLods_Coroutine` (211–217) НЕ итератор → `GetPaddedLowerMvs` (172–204; цикл `Calcule.VolumeBetween(-XYZ.one, XYZ.one << lodDiff)`,
   HashSet, ExceptWith) целиком в первом MoveNext на главном потоке; `RenderLod` (304–312) рисует все low-MV за один кадр без лимита времени
   (`Test.RenderMegaVoxelLod` → CreateLocalizedMesh/GetPooledMesh/meshInfo.Rendre). Новый `Thread` на каждый LOD (112–113), Abort в Dispose (95).
   Патчи: `GetPaddedLowerMvs` — public метод `LodBatchDrawer`, prefix с целочисленными ключами/bounding box (низкий риск);
   `RenderLod`/`QuickDraw_Coroutine` приватные, разбивка по времени = свой итератор (средний риск).
   Замеры уже поставлены (0.18.7): строки «[Карта] …» в отчёте при `DetailedOnPort = true` — данных ещё нет.
2. **Jardinier.Travailler** (`Brix.Engine\Jardinier.cs` 18–24): `CalculateNatureLod` до min(count,100) MegaVoxel каждый кадр без лимита времени —
   prefix с бюджетом по времени (низкий риск). Вызывается из `TerrainLoop.Update`.
3. **LayoutElementWidgetComponent** (`Brix.UI.Builder.Widget`): `OnPosTrack` 619–678 — строка 625 `_hasPrevPos = false;` перед проверкой делает guard
   всегда истинным (позиция RectTransform пишется каждый вызов, пересчёт канваса); `_posTrackOnce = false` (626) ломает «once»; `Enum.HasFlag`
   (872, 877, 1023, 1027, 1032) → битовые операции; `Camera.main` (638, 682, 780, 783) — кэшировать на кадр. До 5 вызовов `TrimAndClamp` за кадр на виджет.
4. **ScreenSignaler** (`Brix.Game`): `BrixUpdate` каждый кадр обходит все рендереры детей (`r.bounds`, `activeInHierarchy`), `BrixSlowUpdate` дублирует.
   Поле `Visible` в firstpass никто не читает (Lua/CSP не проверяли) — можно отключить prefix-патчем.
5. **VoxelRequestBatcher** `AddTerrainResources_Post` (156–198): `hits.AddToList(ref list)` (`ArrayExtensions.cs` 29–35) добавляет все 1000 элементов массива,
   а не `length` — исправление низкий-средний риск. `AddTerrainResources_Multi` — замыкание на каждый вызов.
6. **MaterialCombineHarvester.ApplyCombine** (`Brix.Engine` 58–97): полная пересборка вершин при каждом вызове, `mesh.uv` копия, назначение
   `MeshCollider.sharedMesh` (cook на главном потоке). Частота вызова не проверена. Вызывается из `MaterialCombineDispatcher.ApplyCombines` ← `MegaVoxel.AfficherSpecial`.
7. **LuaRegistry.Update** (`Brix.Lua` 209–235): каждый кадр для каждой спящей корутины вызывает Lua-замыкание; все `hk_Update`. Троттлинг — меняет семантику.
8. **Behavior.BrixFixedUpdate** → `TreeRun.Run()` на каждый агент каждый шаг физики (Motus, не разбирали). **Locomotion4.BrixUpdate** — аниматор на агента, лёгких побед нет.
9. **PersistentCoroutineRunner**: `Signal.Invoke` создаёт `SignalInvokeListEnumerator` на каждый вызов (класс или структура — не проверено).
10. **RecepteurDisplay.relatedAdjective**: кэш не заполняется при промахе. Низкий приоритет.

## Не проверено
Motus `NodeRun.Run`, `IsNullOrReleased`, `CalculateNatureLod`, потоки `Test.InitLayers`, типы `ITexColors`, частота `ApplyCombine`/`Refresh`,
число `ScreenSignaler` на карте, `lodMax` (`LayerVoxelEngine.cs:907`).

## Ошибки самой игры/порта, которые видно в логах (не наши)
`Material … doesn't have a texture property '_MainTex'` (Pie-меню, шейдеры Castle Story/Rock), `FurnaceRecipeDisplay.SwitchMats` ArgumentOutOfRange,
`NetworkLobbyPlayer.OnSceneLoaded` NRE, `Neo.Relais called outside of ScenePrincipale`, `Signal.Connect: connection overflow (max=100)`,
`Destroying Mesh assets immediately`, `ObjectPoolSingleton … already released`.
