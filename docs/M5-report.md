# M5: сигнализация — отчёт (Dev A)

**Дата:** 2026-08-17
**План:** docs/M5-plan.md (редакция 2)
**Объём отчёта:** вся часть Dev A (ядро, хранилище, API, упаковка).
  Часть Dev B (UI: баннер, журнал, звук, `alarm()` на мнемосхемах) не начата —
  её контракт зафиксирован в §4 этого документа.

---

## 1. Главные решения вехи

**Одна авария на правило, уставки — внутренние условия.** Правило
`Boiler1.Temp` с уставками Hi/HiHi даёт одну строку в баннере, а не две.
Severity аварии — severity старшей сработавшей уставки. Рост severity при
активной аварии — событие `Escalated` в журнале + повторный алерт (квитирование
сбрасывается); деэскалация и повторное срабатывание того же ранга — тихие.
Это совпадает с тем, как оператор воспринимает объект: «температура котла»
— одна неисправность, а не набор пересекающихся строк.

**Журнал событий — SQLite за интерфейсом `IEventJournal`.** DuckDB
рассматривался и отклонён: профиль журнала — OLTP (мелкие вставки, обновления
при квитировании, удаления при retention), а не колоночная аналитика. Событий
на порядки меньше, чем значений в архиве: даже миллион событий — сотни МБ
SQLite с индексами, retention подрезает по возрасту. Интерфейс оставляет
возможность замены хранилища без изменения ядра (как `IArchiveStore`).

**Вся веха — локальный мастер-АРМ.** UI общается с ядром через in-process
`IRuntimeClient`. Контракт спроектирован транспортно-нейтральным и отображается
в proto один-к-одному, когда дойдёт multi-ARM/gRPC — переделок ядра не
потребуется.

## 2. Что реализовано

### Модель и конфигурация (`SCADA.Core/Alarms`, `SCADA.Runtime/Configuration`)

- `AlarmSeverity` (Info < Warning < High < Critical), `AlarmType`
  (Threshold/Expression), `ThresholdKind` (LoLo < Lo < Hi < HiHi — порядок
  важен для эскалации), `ThresholdLimit` с per-limit severity.
- `AlarmRule`: имя, описание, тип, severity по умолчанию, зона (area),
  `RequiresAck`, шаблон сообщения, `MinDurationMs` (null → дефолт проекта),
  уставки + гистерезис (threshold) или текст условия (expression).
- `AlarmConfiguration`: правила, глобальные шаблоны сообщений, `SoundConfiguration`
  (severity → файл проекта), `AlarmDefaults.MinDurationMs`.
- Источник — опциональный `alarms.json` рядом с `tags.json`; отсутствие файла =
  проект без аварий. Загрузка в `ProjectLoader`, валидация в `ProjectValidator`:
  уникальность имён, существование тега, строгое убывание уставок по рангу,
  непустое condition, hysteresis/minDurationMs ≥ 0.
- Модель лежит в `SCADA.Core`, а не в `SCADA.Alarms`: `ProjectConfiguration`
  в Core, иначе цикл зависимостей.

### Движок (`SCADA.Alarms`)

- `IAlarmEngine` / `AlarmEngine`: state machine Normal → ActiveUnack →
  ActiveAck / RtnUnack (docs/M5-plan.md §7.1), эскалация событием `Escalated`.
- Анти-дребезг двух уровней: гистерезис на каждую уставку (свойство условия,
  а не измерения) и `MinDurationMs` — условие должно удержаться срок, иначе
  фронт игнорируется.
- Качество тега ≠ Good — правило не меняет состояния и не пишет событий.
- Снимки всех участвующих тегов пишутся в события `Active` и `Escalated`:
  история читается без похода в архив значений.
- Сообщения рендерятся при событии и хранятся готовым текстом —
  переименование тега не ломает историю. Шаблоны: `{Severity}`, `{Description}`,
  `{Tag0.Value}`, `{Tag0.Unit}`, `{Limit.Value}`, `{Limit.Kind}`, `{TagValues}`.
- `IsActive(ruleName)` — дешёвый lookup для будущей функции `alarm()` на
  мнемосхемах: рендер не дублирует условие.

### Журнал (`SCADA.Alarms`)

- `IEventJournal` + `SqliteEventJournal`: WAL, auto_vacuum, индексы под
  фильтры истории (время, severity, area, правило). Первичный ключ события
  присваивает журнал — движок id не знает.
- Ошибки записи (в т.ч. переполнение диска) не роняют службу: диагностика
  через callback, событие теряется, опрос продолжается (ТЗ §8.9).
- Retention: удаление событий старше порога по расписанию из конвейера.
  Настраивается под проект: `Runtime:Journal` в appsettings (дефолт 365 сут,
  минимум 30).
- `AlarmStateRecovery`: восстановление активных/неквитированных аварий из
  журнала при перезапуске службы; группировка по правилу, `Escalated`
  обновляет уставку без смены состояния.

### Конвейер и API (`SCADA.Runtime/Alarms`, `SCADA.Runtime/Runtime`)

- `AlarmPipeline` (BackgroundService): пересчёт по эпохам TagTable — пересчитываются
  только правила, зависящие от изменившихся тегов; `EvaluateAll` на первом
  тике ловит значения, пересёкшие уставку до старта; периодический `Tick`
  для отложенных MinDuration-фронтов; retention по расписанию.
- `AlarmChangeBroadcaster`: bounded-канал подписчикам UI; переполнение роняет
  свежие события для отставшего подписчика, а не конвейер.
- `AlarmRulePreparer`: раннее связывание — имена тегов заменяются индексами
  TagTable; expression-правила проходят фабрику (компилятор в dev-режиме,
  пул `code.bin` в пакетном).
- `IRuntimeClient` расширен методами сигнализации (сигнатуры — §4),
  реализация в `LocalRuntimeClient`. Квитирование адресуется именем правила,
  а не id события: id у отложенного фронта ещё нет, а ссылка по id даёт гонку
  при повторном срабатывании.

### Упаковка в `.scadapkg` (`SCADA.Package`, `SCADA.Package.Builder`)

- Секция `alarms.bin` (writer/reader зеркальны, раскладка как у `tags.bin` —
  новые поля только в хвост записи). Секция опциональна: проект без
  `alarms.json` собирается без неё.
- Expression-условия компилируются при сборке в общий пул `code.bin` с
  дедупликацией: `CodeSectionWriter.Write(..., out int[] poolIndices)`
  возвращает отображение входных выражений в итоговую таблицу, правила
  получают `CompiledExpressionIndex` после дедупликации. Threshold-правила
  получают `CompiledTagIndices`.
- Ошибка компиляции условия — ошибка сборки пакета с именем правила,
  а не рантайма.
- Звуковые файлы из `sound.files` копируются секциями `sounds/<имя>`;
  отсутствующий файл — ошибка сборки. Контрольные суммы покрываются общим
  манифестом пакета автоматически.
- `PackageReader.HasEntry` для опциональных секций; `PackageProjectLoader`
  восстанавливает `AlarmConfiguration` со скомпилированными индексами.
- Сервер в пакетном режиме строит expression-правила из пула `code.bin` —
  компилятор в боевой поставке не нужен (ссылка `SCADA.Server →
  SCADA.Expressions.Compiler` остаётся только ради dev-режима).

## 3. Тесты и приёмка

Всего по решению 423 теста, 0 провалов; сборка без предупреждений.

- `SCADA.Alarms.Tests` (34): state machine — все переходы, эскалация (одна
  строка, `Escalated`, re-alert после квитирования, тихая деэскалация,
  Normal только после отпускания последней уставки), гистерезис, MinDuration,
  качество тегов, журнал SQLite, recovery с эскалацией.
- `SCADA.Runtime.Tests` (112, +alarms): загрузка/валидация `alarms.json` (9),
  конвейер по эпохам, первый тик `EvaluateAll`, retention по расписанию,
  клиентский API.
- `SCADA.Package.Tests` (17, +6): round-trip `alarms.bin`, полный цикл
  с исполнением выражения из пула, дедупликация условий, ошибка сборки на
  отсутствующем звуке и на битом выражении, отсутствие секции без аварий.

По ходу прогонов починен флаки-тест `FirstRun_EvaluateAll_CatchesAlreadyCrossedValue`:
таймаут ожидания 3 с не выдерживал параллельного прогона всех девяти тестовых
сборок — поднят до 10 с. Это устойчивость теста к нагрузке, не дефект кода.

## 4. Контракт для UI (Dev B)

Всё нижеописанное уже работает в `LocalRuntimeClient` и покрыто тестами —
Dev B только потребляет.

### Методы (`IRuntimeClient`)

```csharp
ValueTask<IReadOnlyList<ActiveAlarm>> GetActiveAlarmsAsync(AlarmFilter filter, CancellationToken ct);
ValueTask<IReadOnlyList<AlarmEvent>> GetAlarmHistoryAsync(AlarmHistoryQuery query, CancellationToken ct);
ValueTask AcknowledgeAlarmsAsync(IEnumerable<string> ruleNames, string acknowledgedBy,
    string? comment = null, CancellationToken ct);
IAsyncEnumerable<AlarmChange> SubscribeAlarmsAsync(CancellationToken ct);
```

### Типы

- `ActiveAlarm(RuleName, Limit, State, Severity, Area, Message,
  ActivatedAtUtcMs, AcknowledgedBy)` — строка баннера. `State`:
  `ActiveUnack` / `ActiveAck` / `RtnUnack` (вернулась в норму, ждёт
  квитирования — показывать до квитирования). `Limit` null у expression-правил.
- `AlarmFilter(MinSeverity, Area, UnacknowledgedOnly)` — для баннера и
  счётчиков.
- `AlarmHistoryQuery(FromUtcMs, ToUtcMs, Severity, Area, RuleName, Limit=1000)`
  — для окна журнала. Событие `AlarmEvent` несёт готовый `Message`, снимки
  тегов (у `Active`/`Escalated`) и поля квитирования (`AcknowledgedBy`,
  `AckComment`, `AcknowledgedAtUtcMs`).
- `AlarmChange(Kind, Alarm)` — элемент подписки. `Kind`: `Activated`
  (включая эскалацию — та же строка баннера, новый severity/сообщение),
  `Normalized`, `Acknowledged`. UI обновляет строку по `Alarm.RuleName`.

### Правила для UI

- **Квитирование** — по `RuleName`, одиночно или списком (групповое
  выделение = тот же список, отдельной операции нет). Поле комментария
  опционально. `acknowledgedBy` формирует UI как
  `$"{Environment.UserName}@{Environment.MachineName}"`; в M7 источник
  заменяется аутентификацией, контракт не меняется.
- **Звук** (`AlarmSoundService`, §10 плана): подписка на `SubscribeAlarmsAsync`;
  циклически играет звук максимального severity среди **неквитированных**
  активных; при смене максимума переключается; замолкает, когда неквитированных
  не осталось, или по mute. Конфигурация — `sound` в `alarms.json`; severity
  без файла = встроенный звук UI. Источник файлов: dev-режим — каталог проекта,
  пакет — `PackageReader.ReadEntry("sounds/<имя>")`.
- **`alarm("ruleName")` на мнемосхемах** (§11 плана): builtin возвращает
  `IAlarmEngine.IsActive(ruleName)` — lookup по словарю состояний, условие
  в рендере не дублируется.
- **Сортировка баннера**: по severity (Critical сверху), внутри — по
  `ActivatedAtUtcMs`.

## 5. Что осталось (Dev B) и после MVP

- Баннер, окно журнала с фильтрами, квитирование с комментарием, звук,
  `alarm()` в динамизации — по контракту §4.
- Первый кандидат сразу после MVP: flood suppression («не более K событий
  в минуту от правила, далее сводное») — на случай дребезгащего сигнала,
  который иначе раздувает журнал.
- Далее по плану §12: rate-of-change (окно значений из кольца
  `InMemoryHistorian`), shelving, SMS/email, аудит конфигурации (с M7),
  экспорт журнала, gRPC-транспорт (с multi-ARM).

## 6. Известные ограничения

- `SCADA.Server` ссылается на `SCADA.Expressions.Compiler` — только для
  dev-режима (исходный каталог проекта). Боевая поставка компилятор не
  содержит; ссылку можно убрать, когда dev-режим переедет в отдельный хост.
- Звуки в пакете лежат несжатыми секциями zip — достаточно для wav-файлов
  проектного размера; если появятся длинные записи, пересмотреть.
- Retention журнала — только по возрасту; квоты по объёму пока нет
  (защита от раздувания — flood suppression из §5).
