# M2 — Драйвер Modbus: текущее состояние (2026-08-12)

Продолжение `M1-report.md`. Веха M2 в работе, не закрыта.

## Сделано

**Проекты:** `SCADA.Drivers.Modbus` (+ FluentModbus 5.3.2), `SCADA.Drivers.Modbus.Tests`.
21 тест зелёный.

**Адрес тега** (`ModbusAddress.cs`): грамматика `таблица:смещение[:тип]`,
например `hr:100:f32`, `coil:3`. Таблицы: coil/di/ir/hr; типы u16(дефолт)/i16/u32/i32/f32.
Тип запрещён для битовых таблиц. `RegisterCount` учитывает 2-регистровые типы.
Отложено: классическая нотация 4x/3x (пользователь решил — не сейчас), порядок байт.

**Группировщик** (`RequestGrouper.cs`): адреса → блоки запросов.
Слияние только внутри одной таблицы; лимиты 125 регистров / 2000 бит;
`maxGap` — макс. зазор слияния. `BlockItem` хранит ResultIndex + OffsetWithinBlock.

**Драйвер** (`ModbusTcpDriver.cs`): ConnectAsync парсит конфиг + адреса + группирует;
PollAsync выполняет блоки и раскладывает по результатам.
- `ModbusSettings` — парсер `DeviceDefinition.Configuration`:
  `"host:port;unit=..;timeout=..;maxregs=..;maxgap=.."`. Unit по умолчанию 0
  (стандарт для прямого TCP; FluentModbus-сервер обслуживает только unit 0 по умолчанию).
- `RegisterDecoder` — big-endian байты → double; шов под порядок байт/слов
  (word swap) — добавить, когда будет реальное железо LicOS.
- Биты в ответах УПАКОВАНЫ: `data[bit/8] >> (bit%8) & 1`; серверный буфер тоже
  packed (в тестах `coils.Set(address, true)`, не индекс).

**Масштабирование — в движке** (`PollingEngine.PollDeviceAsync`): драйверы отдают
СЫРЫЕ значения, движок применяет `ScaleFactor/ScaleOffset`. Инвариант: в TagTable
всегда инженерные единицы. Задокументировано в `IDeviceDriver`. Тест
`Poll_AppliesScaleFromTagDefinition`.

**Интерфейс драйвера изменён:** `PollAsync(Memory<TagValue>, ct)` вместо Span —
Span нельзя через await (ограничение C#). Затронуты: Abstractions, Simulator,
Internal. bool-семантика («есть свежие данные») сохранена.

**Интеграционный тест:** виртуальный ПЛК (`ModbusTcpServer` из FluentModbus)
в процессе — чтение u16/i16/f32/coil, раскладка по тегам; обрыв → исключение
(движок помечает Bad).

## Осталось по M2

1. **Переподключение**: при ошибке соединения — reconnect с backoff; при
   восстановлении теги снова Good. Критерий приёмки M2.
2. **Диагностика канала (§7.4)**: счётчики запросов, время отклика,
   переподключения — как диагностические теги.
3. **Регистрация в DriverFactory**: `"modbus-tcp"` → `new ModbusTcpDriver()`.
   Внимание: SCADA.Runtime не ссылается на Modbus (правильно); фабрику
   расширять аккуратно — возможно, регистрация драйверов извне.
4. **RTU** — отдельно, вместе с `IChannel` (общий COM-порт на несколько ПЛК).

## Напоминания для новой сессии

- Тесты: `dotnet test SCADA.Drivers.Modbus.Tests` (21), Runtime (33),
  Expressions (12), Compiler (40), Package (11).
- Приёмочный бенчмарк: `SCADA.Benchmarks/AcceptanceBenchmarks`
  (`dotnet run -c Release -- --filter '*AcceptanceBenchmarks*' --job short`).
  После добавления масштабирования в движок — перегнать, проверить дельту.
