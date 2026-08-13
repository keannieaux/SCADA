# Отчёт по вехам M0–M1 (состояние на 2026-08-12)

Документ для восстановления контекста: что реализовано, какие решения приняты
и почему, что осознанно отложено. Детали требований — в `TZ.md`,
здесь — фактическое состояние кода.

## M0 — Фундамент

Выполнено до начала этой сессии (по состоянию репозитория):

- Структура решения `SCADA.sln`, `Directory.Build.props` (net10.0, Nullable,
  ImplicitUsings, TreatWarningsAsErrors), `Directory.Packages.props` (CPM).
- Приложение `SCADA` (Avalonia): shell, навигация, DI через
  `Microsoft.Extensions.Hosting` (`Program.cs` регистрирует ViewModel/окна,
  `Program.Services` — точка доступа к DI).
- `SCADA.Core` — доменные примитивы: `TagId/DeviceId/ChannelId` (record struct
  над int), `TagValue(double Value, long TimeStampUtc, Quality)`,
  `Quality { Bad=0, Uncertain=64, Good=192 }`, `TagDataType`,
  `TagDefinition`, `DeviceDefinition`, `ChannelDefinition`,
  `ProjectConfiguration`, `TagLoggingConfiguration` (LogOnChange / Interval /
  Schedule).

## M1 — Ядро реального времени

### 1. TagTable (`SCADA.Runtime/TagTable/`)

- Seqlock на слот: `TagSlot { int Version; TagValue Value; long LastChangedEpoch }`.
  Писатель: `Version++` → запись → `Version++`; читатель крутится, пока версия
  нечётная или изменилась за чтение.
- Эпохи: `_epoch` инкрементится **внутри Write** (каждая запись = новая эпоха,
  `LastChangedEpoch` у слота). `GetChangedSince(epoch, Span<TagId>)` — скан,
  возвращает полное число изменённых (может быть > размера буфера — вызывающий
  решает).
- Тесты: конкурентный стресс (8 писателей × читатели, нет «рваных» значений).
  Бенчмарки: `SCADA.Benchmarks/TagTableBenchmarks`, `ContentionBenchmarks`.

### 2. Конфигурация (`SCADA.Runtime/Configuration/`)

- Исходная форма (§14.1): каталог `project.json` / `devices.json` / `tags.json`,
  в каждом файле `formatVersion` (сейчас везде 1, до релиза совместимость не
  поддерживаем — поменял формат, пересобрал пакет).
- Source-gen `ProjectJsonContext` (без рефлексии). Конвертеры:
  `TagId/DeviceId/ChannelId` пишутся числом; `JsonStringEnumConverter<TagDataType>`.
- `ProjectLoader` — чтение + проверка версий + валидация; ошибки накапливаются
  списком и кидаются одним `ProjectConfigurationException`.
- `ProjectValidator` — публичный (нужен редактору Dev B до сохранения):
  ссылки тег→устройство→канал, дубликаты Id/имён, **TagId плотные 0..n-1**
  (TagTable индексируется напрямую).
- `ProjectWriter` — симметричный Save, валидирует перед записью, атомарная
  запись (temp + Move).
- **Важный подводный камень**: STJ source-gen десериализует `init`-свойства
  через псевдо-конструктор со всеми параметрами и затирает значения по
  умолчанию (`= ""`) null'ами для отсутствующих полей. Поэтому во всех
  конфигурационных моделях — `{ get; set; }`, не `init`. Проверено экспериментально.

### 3. Драйверы (`SCADA.Drivers.Abstractions`, `SCADA.Drivers.Simulator`)

- `IDeviceDriver`: `ProtocolName`, `ConnectAsync(device, tags, ct)`,
  `ValueTask<bool> PollAsync(Span<TagValue> results, ct)`, `DisconnectAsync`.
  - `bool` = «есть свежие данные» (false → движок не трогает таблицу;
    задел под pub/sub-драйверы OPC UA/MQTT: Poll отдаёт слепок буфера подписки).
  - Семантика Poll — «отдай текущие значения», а не «сделай сетевой запрос».
- `SimulatorDriver`: поведение кодируется строкой Address: `sin:10`,
  `square:5`, `ramp:60`, `const:42` (пустой адрес — дефолт по типу тега).
  Значения — чистая функция от времени (детерминизм для тестов), фаза от Id.
  Часы инжектируются (`Func<DateTimeOffset>`).
- `InternalDriver` (в `SCADA.Runtime/Polling`) — внутренние теги: Poll ничего
  не делает, значения пишутся снаружи; `InitValue` записывается движком при
  старте. Модель данных не менялась (никаких nullable DeviceId).
- `DriverFactory`: `"simulator"` / `"internal"`, неизвестное — исключение.

### 4. PollingEngine (`SCADA.Runtime/Polling/`)

- Группировка устройств по `ChannelId`: **один канал = один Task** (§7.3),
  внутри канала опрос последовательный.
- `PeriodicTimer` (не `Task.Delay` — дрейф). Буферы `TagValue[]` выделяются
  один раз — цикл без аллокаций.
- Исключение драйвера → теги устройства помечаются `Quality.Bad`, движок живёт.
- Отложено: частота опроса на устройство (нужно поле в DeviceDefinition),
  диагностические теги канала (§7.4, к M2), логирование.

### 5. IRuntimeClient (`SCADA.Runtime/Runtime/`)

- Контракт: `Read` (одиночный и пакетный Span), `CurrentEpoch`,
  `GetChangedSince`, `WriteLocal` (запись во внутренние теги; защита прав — M7).
- `LocalRuntimeClient` — обёртка над `ITagTable`. Remote (gRPC) — позже;
  UI зависит только от интерфейса (мастер-АРМ = local, операторские = remote).
- **`ITagTable` перенесён в `SCADA.Core/Tags/`** — иначе SCADA.Expressions
  не смог бы читать таблицу без циклической ссылки на Runtime.

### 6. InMemoryHistorian (`SCADA.Runtime/Historian/`)

- `IHistorian`: `Append(id, value)`, `Read(id, from, to, Span<TagValue>) → int`
  (при переполнении буфера — самые поздние).
- Кольцевой буфер на тег, **ленивое выделение** (полный объём 20k×3600 точек ≈
  1.7 ГБ — поэтому ленивость обязательна; боевой архив M4 — дисковый, §8).
- Подаватель данных: свой `PeriodicTimer`, читает изменения по эпохам из
  таблицы (подписчик, как UI) — ловит записи любого источника.

### 7. Подсистема выражений (`SCADA.Expressions` + `SCADA.Expressions.Compiler`)

- **ВМ** (`SCADA.Expressions`, исполнительная поставка): стековая машина,
  `stackalloc Span<double>` (глубина 32), ноль аллокаций. Опкоды: LoadConst,
  LoadTag, арифметика, сравнения (результат 1.0/0.0), Not, JumpIfFalse/Jump
  (только вперёд — циклов не существует, завершение гарантировано), CallBuiltin,
  Return. **Все операнды-индексы — 4-байтные int** (потолок 2 млрд тегов).
- `EvaluationContext { ITagTable Tags }` — точка роста (Historian для
  RateOfChange в M5). `BuiltinFunctions` — реестр с метаданными
  (`BuiltinInfo`: Name, Id, ArgCount, **TagRefArgs** — какие аргументы ссылки
  на теги), id append-only. Функции: IsGood, ValueOr, Abs, Min, Max, Clamp.
  Семантика качества — явная (§11.2): `IsGood(t) && t > 80`.
- **Компилятор** (`SCADA.Expressions.Compiler`, только инженерная поставка):
  Lexer → Parser (Пратт, приоритеты таблицей) → Binder (имена→индексы через
  `ITagCatalog`, арность, сбор `TagIndices`, все ошибки списком с позициями)
  → Emitter (дедупликация констант, backpatching переходов, constant folding
  унарного минуса). Фасад `ExpressionCompiler.Compile(text, catalog)`.
- Бенчмарк: эталонное выражение (11 инструкций) — **19.7 нс, 0 аллокаций**
  (критерий ≤ 100 нс).

### 8. Пакет (`SCADA.Package` + `SCADA.Package.Builder`)

- `.scadapkg` = zip-контейнер: `manifest.json` (formatVersion, проект,
  SHA-256 каждой секции) + `tags.bin`, `devices.bin`, `code.bin`.
- `PackageReader` — при Open проверяет версию формата (§14.5: из будущего —
  внятный отказ) и контрольные суммы всех секций до применения (§14.4).
- `PackageWriter` (только Builder) — суммы считает сам, запись атомарная.
- Секции: бинарные записи с **префиксом длины** (читатель пропускает хвост
  с неизвестными полями). tags.bin — весь TagDefinition включая
  TagLoggingConfiguration; devices.bin — каналы затем устройства;
  code.bin — **общий пул констант + дедупликация выражений** (§14.2),
  LoadConst-индексы переписываются в глобальный пул при сборке.
- Фасады: `PackageBuilder.Build(projectDir, out)` (конвейер) и
  `PackageProjectLoader.Load(path)` → `ProjectConfiguration` (+ `LoadCodePool`).

### 9. Приёмочные замеры M1 (Ryzen 5600G, Release, 20 000 тегов)

| Замер | Результат | Бюджет §4.1 |
|---|---|---|
| PollCycle (драйвер + запись 20k) | 0.58 мс, 0 аллокаций | ~0.6% ядра при норме 25% CPU |
| EpochScan (все 20k изменились) | 11.9 мкс | — |
| ReadFrame500 (кадр мнемосхемы) | 0.36 мкс | 30 FPS — запас огромный |
| Выражение (11 инструкций) | 19.7 нс, 0 аллокаций | ≤ 100 нс |
| Память: TagTable + конфиг | ~4.6 МБ | 500 МБ |

Историк-заглушка при полном заполнении — 1.65 ГБ (ожидаемо: сырые точки в RAM;
боевой архив M4 дисковый).

### Тесты

~95 зелёных: `SCADA.Runtime.Tests` (32), `SCADA.Expressions.Tests` (12),
`SCADA.Expressions.Compiler.Tests` (40), `SCADA.Package.Tests` (11).

## Что дальше (по дорожной карте)

- **M2 — Modbus TCP/RTU**: новый проект `SCADA.Drivers.Modbus`, группировка
  запросов, переподключение, диагностические теги канала (§7.4). Симулятор
  остаётся для разработки UI.
- Далее M3 (мнемосхемы), M4 (архив + тренды), M5 (сигнализация — условия на
  нашей ВМ, автомат состояний отдельным слоем; RateOfChange через IHistorian).
