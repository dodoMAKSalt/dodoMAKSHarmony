# Медики — заметки по устройству игры (для реализации)

## Решения пользователя
- Модели: свои простые (tools/make_medic_models.py → Medic/*.obj + medic_palette.png). Сумка — белый кейс, красная ручка, крест в круге.
- Медик не дерётся, держится сзади; лечит БЛИЖАЙШЕГО раненого ниже порога.
- Сохранения с медиками без мода не грузятся — ок; в мультиплеере мод у всех.
- Свои решения (можно поменять): медик лечит и себя; в ближнем бою лечит, только если рядом нет врагов.
- Этапы 1+2 объединены (предметы нельзя скрафтить без стола).

## Цифры
- Стол: 10 досок (plankBlock) + 1 железо (iron).
- Шапка: 50 растений (plant). Сумка: 2 верёвки (rope) + 30 растений. Бинт: 10 растений.
- Медик носит до 10 бинтов, перевязка 7 с, 1 бинт, +50% макс. HP. Пороги: <99/<75/<50/<25%.

## Как устроена игра (найдено)
- **Lua-моды**: всё из `Info/Lua/Mods/<папка>/clientside.lua` (+`serverside.lua` у хоста) грузится в
  `GameLoader.StartLua()` — ПОСЛЕ загрузки объектов мира (LoadGameObjects). Хэллоуин-моды отключены `.off`.
  `Factory.CreateItemFrom(fy_Helmet, "fy_X")` клонирует префаб в фабрику "ModsFactory", снимает
  WorkerTool/KnightTool/ArcherTool/Protection; `X.__Factory.SetMesh(Mesh.LoadOBJ(path, rot))`, `SetMaterial`,
  `SetGripMatrix(Matrix4x4.TRS(...))`. Шейдер вещей: "Shader Forge/FalloffBumpDiffuseEmissive" (_Diffuse,_Normals,...).
  ⚠ Сохранения ссылаются на AssetKey "ModsFactory:..." — предметы надо создавать ДО LoadGameObjects (делать из C#).
- **Рецепты**: `Brix.Game.Components.CookBook` — статические `RecipeInfo` через `RecipeMaker{Work,Heat,Creates(AssetKey),
  Icon,Drawing}.With(Adjectif.X, qty).Make()` (Make регистрирует id в CookBook). Сеть: `CallCmdcraftingQueueRecipe(station, id)`;
  `CraftingState.RecipesToLong` упаковывает id в ulong — проверить лимит id.
- **Lua UI рецептов**: `LuaCrafting.LuaRecipe` (enum) ↔ List<RecipeInfo> (`_luaRecipeToRecipeInfo`), `ToRecipeInfo(LuaRecipe)`.
  Списки рецептов станций — `Info/Lua/LUI/Meta/Meta_Operable.lua` (`_t:Add(fy_Workbench, {Recipes = {Recipe.X,...}})`),
  описания — `Meta_Recipe.lua`, ресурсы — `Meta_Resource.lua`. Новые значения enum можно передавать числом (C# enum принимает любое int),
  а Lua-таблицы дописывать через перехват чтения файла (как PieMenuPriority делает с Livre).
- **Постройки**: меню строительства — `Meta_Structure.lua`: `AssetKey.New("Blueprints", "Workbench")`, `Picking.SetHeldBlueprint(...)`
  в `GameMenu.lua` (RegisterAction "project_Workbench"). Стол = клон чертежа "Workbench" со своей ценой и рецептами.
- **Ресурсы/типы**: каждый тип — C#-класс-наследник Adjectif (`AlchemistPouch : Outil`, ключ `new AdjectiveKey("AlchemistPouch")`),
  экземпляр в статическом поле `Adjectif.alchemistPouch` и в словаре тип→экземпляр (~Adjectif.cs:800).
  `Adjectif.StringToAdjectif(name)` ищет СТАТИЧЕСКОЕ ПОЛЕ Adjectif по имени (через RearViewMirror) — для своих типов
  нужен патч. `AdjectiveInfo.GetInfo(codeName)` — паспорт (иконка/имя), GameComponent.
- **Профессии**: `Occupation.Type`/`Occupation.Job` — enum'ы; профессия по инструменту:
  `OccupationsPerTools` / `JobPerTools` (CategorieOutil → Type/Job). Бомбер = `Adjectif.alchemistTool` → Archer/Alchemist.
  Оружейная стойка принимает категории из `ToolRack.cs:~185`.
- **Лечение**: `IDamageReceiver.Heal(float)`, `BricktronDamageReceiver.Heal`.
- В игре уже есть HealingWard (лечащий тотем) — можно подсмотреть эффекты/иконки (`IconKeys._Healing_Ward`).

## План
1. (C#) Типы MedicHat/MedicBag/Bandage (+MedicTool), регистрация в Adjectif, StringToAdjectif, AdjectiveInfo.
2. (C#) Префабы предметов клонами (шлем/сумка алхимика/бомба) в фабрику до LoadGameObjects, свои меши/текстура.
3. Рецепты в CookBook + LuaRecipe + Meta_*.lua; стол = клон Workbench (цена 10 досок + 1 железо, свои рецепты, модель).
4. Профессия медика (стойка, надевание, запас бинтов), ИИ лечения, приказы (авто/вручную, порог).
