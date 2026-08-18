# M3 — секция схем в `.scadapkg`: вопросы к обсуждению

**Дата:** 2026-08-17
**Автор:** B (графика/UI), для обсуждения с A (ядро и данные)
**Относится к:** ТЗ §14.2, `docs/M1-report.md` (`code.bin`), `docs/M5-plan.md` §6 (`alarms.bin` — образец)
**Состояние:** M3 фазы 1–5 (модель схемы, рендерер, динамизация, SVG-символы, пан/зум, декларативные действия) готовы и работают на схеме, собранной вручную в коде. Осталось последнее: схема должна грузиться из `.scadapkg`, а не строиться руками — это единственная часть M3, которую нельзя закрыть в одиночку.

---

## 1. Что уже есть на стороне графики

Всё в `SCADA.Graphics`:

- **`Scheme`/`SchemeElement`** — модель схемы: `Guid Id`, `ZOrder`, `ShapeKind` (Rectangle/Ellipse/Symbol), геометрия (`X`/`Y`/`Width`/`Height`), до восьми независимых текстовых каналов выражений (`ValueExpression`, `VisibleExpression`, `BlinkWhenExpression`, `RotationExpression`, `FillLevelExpression`, `TextExpression`, `PositionXExpression`, `PositionYExpression`), пороги (`WarnThreshold`/`CritThreshold`), формат/единицы текста, `QualityTagName`, `SymbolPath`, список декларативных действий `OnClick` (`WriteTag`/`ToggleTag`/`OpenScheme`/`ShowDialog`/`Confirm`).
- **`SchemeLoader.Compile(Scheme, ITagCatalog)`** — компилирует все текстовые выражения через `ExpressionCompiler` в `CompiledSchemeElement` (имена тегов → `TagId`, выражения → байткод). Сейчас это единственный путь загрузки — компиляция происходит в рантайме, при каждом запуске приложения, из схемы, собранной вручную в C#-коде.
- **`SchemeCanvas`/`SchemeDrawOperation`** — исполнение: честный пересчёт по эпохам через `TagIndices` каждого канала (как в `TagTable.GetChangedSince`), отрисовка через `ICustomDrawOperation`, пан/зум, клик → действие.
- Нагрузочно проверено: 500 синтетических элементов, пересчёт+отрисовка укладываются в бюджет 30 FPS с большим запасом (на dev-машине; на целевом железе не проверялось).

Чего не хватает: реального пути `.scadapkg → Scheme → CompiledSchemeElement`, минуя ручную сборку в коде.

## 2. Предложение — по образцу `alarms.bin`

Секция `alarms.bin` (`AlarmsSectionWriter`/`Reader` + `PackageBuilder.CompileAlarmRules`) уже решает практически ту же задачу: у правила сигнализации есть текстовое условие (`Condition`), оно компилируется в общий пул `code.bin`, правило хранит только индекс. Предлагаю для схем сделать то же самое:

- `SchemeSectionWriter`/`SchemeSectionReader` (в `SCADA.Package.Builder`/`SCADA.Package`) — запись с префиксом длины на элемент, неизвестный хвост при чтении пропускается — тот же принцип, что везде в проекте.
- В `PackageBuilder.Build`, рядом с `CompileAlarmRules` — новый шаг `CompileSchemeExpressions`: компилирует все восемь каналов каждого элемента схемы через `ExpressionCompiler`, кладёт в тот же общий `pool`, что и алармы (один пул на весь проект — уже зафиксированное решение §14.2), запоминает индексы. После `CodeSectionWriter.Write` — перемотка индексов после дедупликации, как в `RemapAlarmExpressionIndices`.
- Каждый канал хранит и текст выражения (для обратной распаковки, §11.9), и индекс в пуле — как `AlarmRule.Condition` + `CompiledExpressionIndex`.

## 3. Вопросы, требующие решения

### 3.1 Где жить модели данных

`Scheme`/`SchemeElement`/`SchemeAction` сейчас в `SCADA.Graphics` — вместе с рендерером на Avalonia/Skia. `AlarmRule`/`AlarmConfiguration` лежат в `SCADA.Core.Alarms`, отдельно от движка сигнализации — чтобы `SCADA.Package.Builder` мог их сериализовать, не таща Avalonia. Предлагаю по тому же принципу вынести чистые данные схемы (без ссылок на Avalonia/Skia) в `SCADA.Core`, оставив рендерер (`SchemeCanvas`, `SchemeDrawOperation`, `SymbolCache`) в `SCADA.Graphics`. Нужно подтверждение — есть `SCADA.Architecture.Tests`, следящий за правилами зависимостей §5.3, и мы не знаем их точно со стороны графики.

### 3.2 Индексы тегов при чтении

`CodePool.ToExpression(index)` отдаёт только байткод и константы, без `TagIndices` — они лежат отдельно, в `CodePool.Expressions[index].TagIndices`. Честный пересчёт по эпохам (уже реализован в `SchemeCanvas`, Фаза 1) зависит именно от `TagIndices` каждого канала выражения. Значит, загрузчик схемы на стороне рантайма должен доставать `TagIndices` из пула отдельно, не только через `ToExpression`. Прошу подтвердить, что это ожидаемое использование `CodePool`, а не обход API.

### 3.3 Символы

`SymbolPath` сейчас — абсолютный путь на диске, наш временный dev-костыль через `AppContext.BaseDirectory`. В структуре пакета уже заложена папка `symbols/` (§14.2). Предлагается: элемент схемы ссылается на символ по имени файла внутри `symbols/`, `SymbolCache` при работе с пакетом читает не с диска, а через `PackageReader.ReadEntry("symbols/<имя>.svg")`.

### 3.4 Одна схема или несколько

В §14.2 путь выглядит как `schemes/overview.bin` — единственная схема. В §14.1 (исходная форма) уже заложено несколько файлов (`overview.scheme`, `boiler-1.scheme`). Предполагаем, что в манифесте пакета будет несколько записей `schemes/<имя>.bin`, и рантайм перечисляет их через `PackageManifest.Entries`, фильтруя по префиксу — но это стоит подтвердить явно, не додумывать.

### 3.5 Декларативные действия (`OnClick`)

У `alarms.bin` прецедента нет — действия появились только в схемах. Нужен свой бинарный формат: список действий на элемент, каждое — байт-тип + поля:

| Действие | Поля |
|---|---|
| `WriteTag` | `TagId` (int), `Value` (double) |
| `ToggleTag` | `TagId` (int) |
| `OpenScheme` | имя схемы (string) |
| `ShowDialog` | сообщение (string) |
| `Confirm` | сообщение (string) |

Имена тегов у `WriteTag`/`ToggleTag` резолвятся в `TagId` при сборке пакета — то же раннее связывание, что и везде (§11.6).

---

## Что дальше

Ответы на 3.1–3.5 определяют, кто и что пишет: модель данных и бинарные секции логично делать вместе (или тому, у кого сейчас в руках `SCADA.Package.Builder`/`SCADA.Package`), загрузчик на стороне рантайма (адаптер `CodePool` → `CompiledSchemeElement`) — со стороны графики.
