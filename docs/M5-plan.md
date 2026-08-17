# M5: Сигнализация — план

**Дата:** 2026-08-17 (редакция 2)
**Исполнители:** A (ядро и данные) + B (UI и журнал)
**Относится к:** ТЗ §16, §5.3, §5.4, §11, §13, §8.7, §8.9

**Статус:** этапы 1–7 из §12 реализованы (модель, загрузка `alarms.json`,
движок с эскалацией, SQLite-журнал, конвейер, API в `IRuntimeClient`,
компиляция в `.scadapkg` — секция `alarms.bin` + звуки). Осталось: UI
(баннер, журнал, квитирование, звук) и `alarm()` на мнемосхемах (§12, п. 8–9).

---

## 1. Цель вехи

Реализовать движок правил сигнализации, журнал событий/алармов и доступ к ним
для UI. Dev A отвечает за движок, хранилище и API; Dev B — за баннер, журнал,
квитирование, звук и визуализацию на мнемосхемах.

В M5 вся система работает на **локальном мастер-АРМе**: UI общается с ядром
через in-process `IRuntimeClient` (существующий паттерн). Сетевой транспорт
(gRPC) — вне этой вехи, см. §2.7.

---

## 2. Принятые решения

### 2.1 Хранилище событий — SQLite с интерфейсом

- Первичная реализация `IEventJournal` — SQLite (`Microsoft.Data.Sqlite`).
- Интерфейс позволяет в будущем заменить хранилище без изменения ядра
  (так же, как `IArchiveStore` для истории).
- SQLite оправдан: событий на порядки меньше, чем значений; нужны реляционные
  запросы с фильтрами и индексами (ТЗ §8.7).
- DuckDB рассматривался и **отклонён**: это колоночный OLAP-движок, а профиль
  журнала — OLTP (мелкие вставки, обновления при квитировании, удаления при
  retention, индексные выборки). Зрелость .NET-биндингов и durability-история
  также на стороне SQLite.

### 2.2 Конфигурация аварий — отдельный `alarms.json`

- Аварийные правила и шаблоны сообщений хранятся в файле `alarms.json` рядом с
  `tags.json` и `devices.json` (предусмотрено ТЗ §14.1).
- `ProjectLoader` загружает его и помещает в `ProjectConfiguration.Alarms`.
- Отсутствие `alarms.json` означает, что в проекте нет аварий — валидный случай.

### 2.3 Условия срабатывания — пороговые и expression-правила

- **Threshold** — набор уставок `HiHi`, `Hi`, `Lo`, `LoLo` в одном правиле,
  с гистерезисом (ТЗ M5: гистерезис задаётся в правиле, не на теге).
- **Expression** — произвольное условие на выражениях §11.
- Дискретные аварии и отклонение от уставки отдельными типами правил **не**
  выделяются — они выражаются через Expression
  (`DI1.Status == true`, `abs(Temp - Setpoint) > 5`).
- Правила обоих типов порождают одно и то же: boolean-состояние и события в
  журнале. Expression-правила могут зависеть от нескольких тегов; в журнале
  сохраняются снимки всех участвующих тегов.

### 2.4 Сообщения — глобальные шаблоны + переопределение в правиле

- Глобальные шаблоны задаются в `alarms.json` по ключам (`ThresholdActive`,
  `ExpressionActive`, `ThresholdNormal` и т.д.).
- Правило может переопределить шаблон через поле `messageTemplate`;
  если не задано — шаблон по умолчанию.
- В журнале сохраняется **готовый текст сообщения** + снимки тегов. История
  отображается без шаблонов и не теряет данные при переименовании тегов.

### 2.5 Severity и Area

- **Severity** — порядковый (ordinal) приоритет из четырёх уровней:
  `Info < Warning < High < Critical`.
  - `Info` — к сведению, действий не требует;
  - `Warning` — отклонение, следить;
  - `High` — требуется вмешательство в ближайшее время;
  - `Critical` — требуется немедленное вмешательство.
- Порядок уровней используется баннером (сортировка) и звуком («максимальный
  severity среди активных неквитированных»), см. §10.
- Для Threshold-правил severity задаётся **per-limit**: у каждой уставки
  (`HiHi`/`Hi`/`Lo`/`LoLo`) свой уровень (типично HiHi/LoLo → Critical,
  Hi → High, Lo → Warning). Severity на уровне правила — дефолт для
  Expression-правил и fallback, если у уставки уровень не задан.
- **Одна авария на правило (эскалация).** Уставки — внутренние условия со
  своими гистерезисами; наружу правило показывает одно состояние с severity
  старшей сработавшей уставки. Две строки на одно физическое отклонение
  (Hi + HiHi) — это alarm flood по ISA-18.2, поэтому рост severity при
  активной аварии оформляется событием `Escalated` для той же аварии,
  а не новой строкой. Подробности — §7.1.
- **Area** — логическая зона/объект: фильтрация журнала, группировка, в
  будущем права доступа. На логику не влияет.
- Отображаемые строки severity отделены от enum-имён (мультязычность — вне MVP).

### 2.6 Разделение по сборкам (§5.4)

- `SCADA.Expressions.Compiler` и `SCADA.Package.Builder` — только инженерная
  поставка.
- `SCADA.Alarms` — движок и абстракция журнала; входит в исполнительную
  поставку.
- SQLite-реализация `IEventJournal` — в исполнительной поставке.
- Рантайм не содержит компилятора выражений, только байткод-ВМ
  `SCADA.Expressions`.

### 2.7 Транспорт — локальный `IRuntimeClient`, gRPC позже

- В M5 сетевого слоя нет: `SCADA.Server` — plain `Host` без Kestrel, UI работает
  in-process. API сигнализации добавляется **расширением `IRuntimeClient`**
  и реализацией в `LocalRuntimeClient` — по существующему паттерну истории.
- Контракт `IAlarmService` проектируется **транспортно-нейтральным**: в
  сигнатурах только сериализуемые типы, никаких in-process-only ссылок. Это
  позволит позже обернуть тот же контракт в gRPC (multi-ARM, ТЗ §12) без
  переделки ядра и UI.
- gRPC-сервис `AlarmService` — вне MVP, вместе с остальным сетевым слоем.

### 2.8 Звук — функция UI-слоя

- Движок про звук не знает. Звуком занимается `AlarmSoundService` на стороне
  UI (Dev B), подписанный на изменения аварий.
- Правило: есть хотя бы одна **активная неквитированная** авария → играется
  звук, соответствующий **максимальному severity** среди таких аварий. Одна
  петля на систему, не звук на каждое событие (защита от какофонии при флуде).
- Квитирование или возврат в норму гасит звук, если других неквитированных
  не осталось. Кнопка «тишина» (mute) глушит звук, но не квитирует.
- Конфигурация: секция `sound` в `alarms.json` — `enabled` и таблица
  `severity → звуковой файл`. Дефолтный формат — WAV (PCM): не нужен декодер,
  нулевая задержка старта, бесшовная петля. Другие форматы допустимы, если их
  читает выбранный плеер, — формат определяется по расширению файла.
  Per-rule переопределение звука — вне MVP.
- Файлы звуков — **ресурсы проекта**: лежат в исходном проекте
  (`MyProject.scada/sounds/`) и при сборке упаковываются в `.scadapkg`
  (§6) — иначе на объекте звука не будет. Существование файлов проверяется
  при сборке, не в рантайме.
- В UI встроены стандартные звуки по умолчанию на каждый severity: проекту
  не обязательно иметь свои файлы, секция `sound.files` — переопределение.

### 2.9 `AcknowledgedBy` до появления пользователей (M7)

- Поле `AcknowledgedBy` обязательно в модели и схеме журнала уже сейчас —
  переделывать схему после накопления данных дорого.
- Реальных пользователей до M7 нет, поэтому в M5 поле заполняется строкой
  `<os-user>@<station-name>`: пользователь ОС + имя станции из конфигурации
  (`Runtime:StationName`). Имя станции останется полезным и после M7 — в
  журнале видно, с какого АРМа квитировали.
- В M7 источник строки заменяется на аутентифицированного пользователя;
  схема журнала и контракты не меняются.
- Журнал аудита по ТЗ §13 — M7; в M5 квитирование фиксируется событием
  `Acknowledged` в журнале аварий, этого достаточно.

### 2.10 Качество тегов

- Если качество любого тега, участвующего в правиле, отлично от Good —
  пересчёт правила **пропускается, состояние замораживается** (последнее
  известное). Это исключает ложные фронты и ложные «возвраты в норму» при
  обрыве связи.
- В снимке тега (`AlarmTagSnapshot`) качество фиксируется всегда — по журналу
  видно, на каких данных сработало правило.
- Отдельные аварии «потеря связи с устройством» — не движок правил, а
  диагностика канала (ТЗ §7.4), вне M5.

### 2.11 Журнал: объём, retention, надёжность

- Прикидка объёма: ~400–500 Б на событие с сообщением и снимками + ~40 % на
  индексы → миллион событий ≈ 0.5–0.7 ГБ. SQLite это штатно выдерживает;
  риск — не размер, а неограниченный рост и флуд.
- **Retention обязателен сразу**: фоновая задача удаляет события старше порога,
  в БД включён `auto_vacuum = INCREMENTAL`, чтобы файл реально ужимался.
- Настройки журнала — **по аналогии с архивом значений**: POCO
  `JournalOptions`, биндинг из секции `Runtime:Journal` в appsettings сервера
  (как `ArchiveOptions` из `Runtime:Archive`): `RetentionDays` (дефолт 365),
  `MinRetentionDays` (нижний предел при нехватке диска). `DiskSpaceSupervisor`
  при просадке диска может принудительно сокращать и retention журнала — тот
  же принцип «потерять старое лучше, чем новое» (ТЗ §8.9).
- `MinDuration` — настраивается **на уровне проекта**: дефолт в `alarms.json`
  (`defaults.minDurationMs`) + переопределение в каждом правиле
  (`minDurationMs`). Дефолт 0 = без задержки.
- **Снимки тегов пишутся только для событий `Active`** (для `Normal` и
  `Acknowledged` они почти никогда не нужны) — экономия 40–60 % объёма.
- Файл журнала `events.db` лежит в папке проекта рядом с архивом (ТЗ §14.6).
- Переполнение диска — по принципу ТЗ §8.9: ошибка записи в журнал не роняет
  опрос и HMI; пишется диагностика, новые события теряются до освобождения
  места.
- Флуд-контроль первой линии — гистерезис и `MinDuration` (уже в модели).
  Ограничение частоты событий одного правила («не более K в минуту, далее
  сводное событие») — первый пункт сразу после MVP, см. §12.

---

## 3. Архитектура

```text
Исходный проект
  project.json
  devices.json
  tags.json
  alarms.json
        ↓
SCADA.Package.Builder
  → валидация
  → компиляция expression-правил в code.bin
  → запись alarms.bin
        ↓
.scadapkg
  tags.bin
  devices.bin
  code.bin
  alarms.bin
        ↓
SCADA.Server
  SCADA.Package
  SCADA.Runtime.AlarmPipeline
  SCADA.Alarms.AlarmEngine
  SQLite IEventJournal (events.db)
  IAlarmService → расширение IRuntimeClient / LocalRuntimeClient
        ↓
UI (Dev B, in-process на мастер-АРМе)
  баннер, журнал, квитирование, звук, мнемосхема
```

### Проекты и интерфейсы

- `SCADA.Core` (пакет `SCADA.Core.Alarms`)
  - Конфигурационная модель: `AlarmRule`, `ThresholdLimit`, `AlarmConfiguration`,
    `AlarmSeverity`, `SoundConfiguration`, `AlarmDefaults`.
  - Модель живёт в Core, а не в `SCADA.Alarms`: `ProjectConfiguration` (Core)
    ссылается на неё, а `SCADA.Alarms` ссылается на Core (`TagId`, `Quality`) —
    обратная зависимость дала бы цикл.
- `SCADA.Alarms`
  - Модель событий: `AlarmEvent`, `ActiveAlarm`, `AlarmId`, `AlarmState`,
    `AlarmFilter`, `AlarmHistoryQuery`, `AlarmChange`.
  - `IAlarmEngine` — вычисление состояний по изменениям тегов.
  - `IEventJournal` — запись и чтение событий.
  - `IAlarmStateStore` — активные аварии в памяти + восстановление из журнала
    при старте.
- `SCADA.Alarms.Sqlite` (или внутри `SCADA.Alarms`)
  - Реализация `IEventJournal` на SQLite.
- `SCADA.Runtime`
  - `AlarmPipeline` — `BackgroundService`, подписанный на `TagTable`.
  - Расширение `IRuntimeClient`/`LocalRuntimeClient` методами `IAlarmService`.
- `SCADA.Server`
  - DI-регистрация.
- `SCADA.Alarms.Tests`
  - Unit-тесты движка и state machine.
- `SCADA.Runtime.Tests`
  - Интеграция с `TagTable`, восстановление состояния при рестарте.

---

## 4. Модель данных

### 4.1 AlarmRule

```csharp
public class AlarmRule
{
    public string Name { get; set; }                 // уникальное имя правила
    public string Description { get; set; }          // "Температура масла превышена"
    public AlarmType Type { get; set; }              // Threshold | Expression
    public AlarmSeverity Severity { get; set; }      // дефолт (Expression и fallback)
    public string Area { get; set; }
    public bool RequiresAck { get; set; }
    public string? MessageTemplate { get; set; }     // переопределение глобального шаблона
    public int? MinDurationMs { get; set; }          // анти-дребезг; null → Defaults.MinDurationMs

    // Threshold: уставки с индивидуальным severity (§2.5)
    public string? TagName { get; set; }
    public IReadOnlyList<ThresholdLimit>? Limits { get; set; }
    public double Hysteresis { get; set; }

    // Expression
    public string? Condition { get; set; }           // исходный текст выражения
    public int[]? CompiledTagIndices { get; set; }   // заполняется при сборке
    public int? CompiledExpressionIndex { get; set; } // индекс в code.bin
}

public class ThresholdLimit
{
    public ThresholdKind Kind { get; set; }          // HiHi | Hi | Lo | LoLo
    public double Value { get; set; }
    public AlarmSeverity? Severity { get; set; }     // null → дефолт правила
}
```

State machine ведётся **по правилу целиком**: уставки — внутренние условия
со своими гистерезисами; наружу (баннер, квитирование, `alarm()`) правило —
одна авария с severity старшей сработавшей уставки (§2.5, §7.1). В журнале
события хранят per-limit деталь (`Limit`), чтобы разбор инцидента не терял
точности.

### 4.2 AlarmEvent, ActiveAlarm, идентификаторы

```csharp
public record struct AlarmId(long Value);            // = PK строки журнала

public record AlarmEvent(
    AlarmId Id,
    long TimestampUtcMs,                  // время фронта по часам сервера (UTC)
    string RuleName,
    ThresholdKind? Limit,                 // null для Expression-правил
    AlarmEventType Type,                  // Active | Normal | Acknowledged
    string Message,                       // готовый текст
    AlarmSeverity Severity,
    string Area,
    IReadOnlyList<AlarmTagSnapshot> TagSnapshots,  // только для Active (§2.11)
    string? AcknowledgedBy = null,
    string? AckComment = null,
    long? AcknowledgedAtUtcMs = null);

public record AlarmTagSnapshot(
    TagId TagId,
    string TagName,
    double? Value,
    Quality Quality);

public record ActiveAlarm(
    string RuleName,
    ThresholdKind? Limit,
    AlarmState State,                     // ActiveUnack | ActiveAck | RtnUnack
    AlarmSeverity Severity,
    string Area,
    string Message,
    long ActivatedAtUtcMs,
    string? AcknowledgedBy);

public record AlarmFilter(
    AlarmSeverity? MinSeverity = null,
    string? Area = null,
    bool? UnacknowledgedOnly = null);

public record AlarmHistoryQuery(
    long FromUtcMs, long ToUtcMs,
    AlarmSeverity? Severity = null,
    string? Area = null,
    string? RuleName = null,
    int Limit = 1000);

public record AlarmChange(                  // элемент подписки UI
    AlarmChangeKind Kind,                   // Activated | Normalized | Acknowledged
    ActiveAlarm Alarm);
```

### 4.3 AlarmConfiguration

```csharp
public class AlarmConfiguration
{
    public IReadOnlyList<AlarmRule> Rules { get; set; } = Array.Empty<AlarmRule>();
    public IReadOnlyDictionary<string, string> Templates { get; set; }
        = new Dictionary<string, string>();
    public SoundConfiguration Sound { get; set; } = new();
    public AlarmDefaults Defaults { get; set; } = new();
}

public class AlarmDefaults
{
    public int MinDurationMs { get; set; }          // дефолт анти-дребезга; правило может переопределить
}

public class SoundConfiguration
{
    public bool Enabled { get; set; } = true;
    public IReadOnlyDictionary<AlarmSeverity, string> Files { get; set; }
        = new Dictionary<AlarmSeverity, string>();   // severity → путь к .wav
}
```

---

## 5. Пример `alarms.json`

```json
{
  "formatVersion": 1,
  "templates": {
    "thresholdActive": "{Severity}: {Description}. Значение {Tag0.Value} {Tag0.Unit} пересекло уставку {Limit.Value} ({Limit.Kind}).",
    "thresholdNormal": "{Description} вернулось в норму. Текущее значение {Tag0.Value} {Tag0.Unit}.",
    "expressionActive": "{Severity}: {Description}. Участвующие значения: {TagValues}."
  },
  "sound": {
    "enabled": true,
    "files": {
      "Warning": "sounds/warning.wav",
      "High": "sounds/high.wav",
      "Critical": "sounds/critical.wav"
    }
  },
  "defaults": {
    "minDurationMs": 0
  },
  "rules": [
    {
      "name": "Boiler1.Temp",
      "type": "Threshold",
      "tagName": "Boiler1.Temp",
      "limits": [
        { "kind": "HiHi", "value": 95, "severity": "Critical" },
        { "kind": "Hi",   "value": 80, "severity": "High" },
        { "kind": "Lo",   "value": 20, "severity": "Warning" },
        { "kind": "LoLo", "value": 10, "severity": "Critical" }
      ],
      "hysteresis": 2,
      "minDurationMs": 1000,
      "area": "Котельная",
      "requiresAck": true,
      "description": "Температура котла вне уставок"
    },
    {
      "name": "PUMP_FAILURE",
      "type": "Expression",
      "condition": "Pump1.Running && !Pump2.Running && Tank1.Level < 10",
      "severity": "Critical",
      "area": "Насосная",
      "requiresAck": true,
      "minDurationMs": 0,
      "description": "Аварийная ситуация насосов"
    }
  ]
}
```

---

## 6. Компиляция в `.scadapkg`

`PackageBuilder` компилирует правила и добавляет секцию `alarms.bin`:

```csharp
// внутри PackageBuilder.Build, после загрузки и валидации проекта:
CompileAlarmRules(config, allExpressions); // expression → общий пул, threshold → CompiledTagIndices
writer.AddEntry("code.bin", CodeSectionWriter.Write(allExpressions, out var poolIndices));
RemapAlarmExpressionIndices(config, poolIndices); // индексы правил с учётом дедупликации
writer.AddEntry("alarms.bin", AlarmsSectionWriter.Write(config.Alarms));
```

- Expression-условия компилируются через `SCADA.Expressions.Compiler` в общий
  пул `code.bin` (с дедупликацией, ТЗ §14.2).
- Имена тегов в правилах заменяются на `TagId`/`TagIndex` из `tags.bin`.
- В `alarms.bin` хранятся уже скомпилированные правила.

**Звуковые файлы** из `sound.files` копируются в пакет как обычные секции
(`sounds/<имя>.wav`, по аналогии с `symbols/` и `schemes/` из ТЗ §14.2):

```csharp
foreach (var soundFile in config.Alarms.Sound.Files.Values.Distinct())
    writer.AddEntry($"sounds/{Path.GetFileName(soundFile)}", File.ReadAllBytes(projectDir + soundFile));
```

- Существование каждого файла проверяется **при сборке**: отсутствующий звук —
  ошибка сборки пакета, а не рантайма.
- Контрольные суммы секций `sounds/` покрываются общим механизмом манифеста
  (§14.4) автоматически.
- В рантайме UI читает звуки через `PackageReader.ReadEntry("sounds/...")`;
  в dev-режиме (исходный каталог проекта) — прямо с диска. Отсутствующий в
  конфиге severity берёт встроенный звук UI (§2.8).

`PackageProjectLoader` читает `alarms.bin` и `code.bin`, восстанавливает
`AlarmConfiguration` с готовыми `Expression` для ВМ.

---

## 7. Движок (AlarmEngine)

### 7.1 State machine

Одна авария на правило (§2.5). Полный набор состояний по правилу:

```text
                 ┌───────────────┐
   false→true    │               │  ack
  ┌──────────► ActiveUnack ──────┼──────► ActiveAck ──┐
  │            │               │                      │ true→false
  │            └──────┬────────┘                      ▼
  │                   │ true→false                 Normal
  │                   ▼                              ▲
  │              RtnUnack ──────ack──────────────────┘
  │            (в норме, но не квитировано)
  │
  └── новый фронт false→true возможен только из Normal/RtnUnack

  Escalated: в состояниях Active* сработала уставка с более высоким
  severity → событие Escalated, severity аварии растёт, строка в баннере
  та же. Если авария была квитирована — снова ActiveUnack (re-alert).
  Деэскалация (старшая уставка отпустила, младшая держится) — тихая.
```

- Условие правила — агрегат уставок: истинно, пока сработала хотя бы одна
  (для Threshold) или истинно выражение (Expression). У каждой уставки свой
  гистерезис и своё прошлое состояние — внутри правила.
- `false → true` — вход в аварию, событие `Active` (со снимками тегов).
- `true → false` — возврат в норму, когда отпустила **последняя** уставка:
  - из `ActiveAck` → `Normal`;
  - из `ActiveUnack` → `RtnUnack` (авария ушла, но квитирование обязательно);
  - если `RequiresAck = false` — сразу в `Normal` минуя квитирование.
- Квитирование — событие `Acknowledged`: `ActiveUnack → ActiveAck`,
  `RtnUnack → Normal`. Адресуется именем правила.
- События пишутся **только на фронтах**: пока условие остаётся true, новых
  событий `Active` нет. «Повторное срабатывание» возможно только после
  возврата в норму.
- `MinDuration`: условие должно удерживаться true не меньше заданного времени,
  иначе фронт игнорируется (анти-дребезг поверх гистерезиса).
- Timestamp события — время фронта по часам сервера (UTC), не timestamp тега.

### 7.2 Интеграция с TagTable

`AlarmPipeline` — `BackgroundService`, который:

- отслеживает изменения `TagTable` через эпохи (`GetChangedSince`);
- пересчитывает только затронутые правила (обратный индекс тег → правила
  строится при загрузке конфигурации);
- пропускает пересчёт правила при качестве любого участвующего тега ≠ Good
  (§2.10);
- пишет события в `IEventJournal`;
- обновляет активные аварии в `IAlarmStateStore`.

### 7.3 Восстановление состояния при рестарте

Активные аварии живут в памяти (`ConcurrentDictionary`) ради быстрого доступа
баннера, но при старте `IAlarmStateStore` **восстанавливается из журнала**:
читаются последние события по каждому ключу `(RuleName, Limit?)`, находятся
незавершённые (есть `Active`, нет завершающего `Normal`/квитирования). Затем
движок однократно пересчитывает все правила по текущим значениям тегов, чтобы
свести восстановленное состояние с фактическим. Неквитированные аварии переживают
перезапуск службы.

---

## 8. Журнал на SQLite

Файл `events.db` в папке проекта (ТЗ §14.6). Таблица `AlarmEvents`:

```sql
PRAGMA journal_mode = WAL;
PRAGMA auto_vacuum = INCREMENTAL;

CREATE TABLE AlarmEvents (
    Id INTEGER PRIMARY KEY,
    TimestampUtcMs INTEGER NOT NULL,
    RuleName TEXT NOT NULL,
    LimitKind INTEGER,             -- NULL для Expression-правил
    EventType INTEGER NOT NULL,
    Severity INTEGER NOT NULL,
    Area TEXT,
    Message TEXT NOT NULL,
    TagSnapshots TEXT,             -- JSON, только для EventType = Active
    AcknowledgedBy TEXT,
    AckComment TEXT,
    AcknowledgedAtUtcMs INTEGER
);

CREATE INDEX idx_alarms_time ON AlarmEvents(TimestampUtcMs);
CREATE INDEX idx_alarms_rule ON AlarmEvents(RuleName, TimestampUtcMs);
CREATE INDEX idx_alarms_severity ON AlarmEvents(Severity, TimestampUtcMs);
CREATE INDEX idx_alarms_area ON AlarmEvents(Area, TimestampUtcMs);
```

- Индекс по квитированию не нужен: активные/неквитированные обслуживаются
  in-memory `IAlarmStateStore`, журнал читается историческими запросами.
- **Retention**: фоновая задача удаляет события старше порога +
  `PRAGMA incremental_vacuum`. Настройки — `JournalOptions` из секции
  `Runtime:Journal` в appsettings (§2.11), по образцу `ArchiveOptions`:

```json
"Runtime": {
  "Journal": {
    "RetentionDays": 365,
    "MinRetentionDays": 30
  }
}
```
- Ошибки записи (в т.ч. переполнение диска) не роняют службу: диагностика,
  событие теряется, опрос и HMI продолжают работу (ТЗ §8.9).

---

## 9. API

### Интерфейс для UI (транспортно-нейтральный, §2.7)

```csharp
public interface IAlarmService
{
    ValueTask<IReadOnlyList<ActiveAlarm>> GetActiveAsync(AlarmFilter filter, CancellationToken ct);
    ValueTask<IReadOnlyList<AlarmEvent>> GetHistoryAsync(AlarmHistoryQuery query, CancellationToken ct);
    ValueTask AcknowledgeAsync(IEnumerable<string> ruleNames, string acknowledgedBy, string? comment, CancellationToken ct);
    IAsyncEnumerable<AlarmChange> SubscribeAsync(CancellationToken ct);
}
```

- Квитирование адресуется **именем правила** (одна авария на правило, §7.1),
  а не id события журнала: у активной аварии может ещё не быть события
  (отложенный фронт MinDuration), а ссылка по id события даёт гонку при
  повторном срабатывании между отображением и нажатием «квитировать».
- В M5 методы добавляются в `IRuntimeClient` и реализуются в
  `LocalRuntimeClient` (in-process), по аналогии с методами истории.
- `acknowledgedBy` в M5 формируется как `<os-user>@<station-name>` (§2.9);
  в M7 источник заменяется аутентификацией, контракт не меняется.
- Комментарий при квитировании — опциональный, UI предоставляет поле ввода.
- Квитирование — по одиночной аварии или списком имён; выделение группы в UI —
  это тот же список, отдельной операции не требуется.

### Будущий gRPC (вне MVP)

Контракт выше отображается в proto один-к-одному (`GetActiveAlarms`,
`GetAlarmHistory`, `Acknowledge`, `Subscribe`) вместе с остальным сетевым
слоем multi-ARM (ТЗ §12).

---

## 10. Звук (Dev B)

`AlarmSoundService` на стороне UI (§2.8):

- подписан на `IAlarmService.SubscribeAsync`;
- играет циклически звук максимального severity среди активных
  неквитированных аварий; при смене максимума переключает звук;
- замолкает, когда неквитированных не осталось, или по кнопке mute;
- конфигурация — секция `sound` в `alarms.json` (§5); отсутствие файла для
  severity = нет звука для этого уровня, без ошибок.

---

## 11. Переиспользование на мнемосхемах

- В выражениях динамизации добавляется функция `alarm("ruleName")` → `bool`.
- Рендер не дублирует условие, а читает уже вычисленное состояние правила —
  дешёвый lookup по словарю состояний.
- На мастер-АРМе состояния доступны in-process через тот же клиент; при
  появлении сетевого транспорта источник состояний подменяется подпиской,
  функция и рендер не меняются.

---

## 12. Порядок работы

### MVP (M5)

1. Модель: `AlarmRule`, `ThresholdLimit`, `AlarmEvent`, `ActiveAlarm`,
   `AlarmConfiguration` (§4).
2. `alarms.json` + загрузка в `ProjectConfiguration` (валидация: уникальность
   имён, существование тегов, корректность уставок).
3. State machine и `IAlarmEngine` (§7.1), включая качество тегов (§2.10).
4. `IEventJournal` + SQLite-реализация + retention (§8).
5. Интеграция с `TagTable` (`AlarmPipeline`) + восстановление при рестарте (§7).
6. `IAlarmService` как расширение `IRuntimeClient`/`LocalRuntimeClient` (§9).
7. Тесты: state machine (все переходы §7.1), интеграция, журнал, рестарт.
8. UI (Dev B): баннер, журнал с фильтрами, квитирование, звук (§10).
9. Функция `alarm()` в выражениях/рендере (§11).

### Вне MVP (позже)

- Ограничение частоты событий одного правила (flood suppression: «не более K
  в минуту, далее сводное событие») — **первый кандидат сразу после MVP**.
- Rate-of-change правила. При реализации использовать окно значений кольца
  `InMemoryHistorian` (ТЗ §16.4) — оно штатно предназначено для правил
  сигнализации и диска не касается.
- Shelving / подавление аварий.
- Escalation / SMS / email.
- Аудит изменений конфигурации алармов (важно для регуляторов, вместе с M7).
- Мультиязычность сообщений.
- Экспорт журнала.
- gRPC-транспорт `AlarmService` (вместе с multi-ARM).

---

## 13. Открытые вопросы

Открытых вопросов нет. Оба подстройочных параметра конфигурируемы
(§2.11): retention — через `Runtime:Journal` в appsettings (дефолт 365 сут),
`MinDuration` — через `defaults.minDurationMs` в `alarms.json` (дефолт 0) с
переопределением в каждом правиле. Конкретные значения подбираются под
проект без изменения кода.

---

## 14. Критерии приёмки M5

- Сборка без предупреждений.
- Тесты state machine покрывают все переходы §7.1, включая `RtnUnack`
  (возврат в норму без квитирования) и эскалацию: одна строка в баннере на
  правило, рост severity событием `Escalated`, re-alert после квитирования,
  тихая деэскалация.
- При качестве тега ≠ Good правило не меняет состояния и не пишет событий.
- SQLite-журнал читается с фильтрами по времени, severity и area; retention
  удаляет события старше порога.
- После перезапуска службы активные и неквитированные аварии восстанавливаются
  из журнала и видны в `GetActiveAsync`.
- Квитирование фиксируется в журнале с `AcknowledgedBy` вида
  `<os-user>@<station>` и комментарием.
- Expression-правила с несколькими тегами сохраняют снимки всех участвующих
  тегов (только для событий `Active`/`Escalated`).
- Severity аварии определяется старшей сработавшей уставкой (per-limit, §2.5).
- UI: баннер показывает активные аварии с сортировкой по severity; журнал с
  фильтрами; квитирование с комментарием; звук по максимальному severity
  среди неквитированных + mute.
- Пакет `.scadapkg` содержит звуковые файлы из конфигурации; отсутствующий
  файл — ошибка сборки пакета.
- `alarm("name")` доступно в выражениях динамизации мнемосхемы.
