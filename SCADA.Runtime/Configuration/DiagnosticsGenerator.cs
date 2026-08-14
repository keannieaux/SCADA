using SCADA.Core.Channels;
using SCADA.Core.Devices;
using SCADA.Core.Tags;

namespace SCADA.Runtime.Configuration;

/// <summary>
/// Генератор диагностических тегов каналов (ТЗ §7.4). На каждый канал
/// добавляет псевдоустройство "@<канал>" и фиксированный набор тегов
/// "@<канал>.<метрика>".
///
/// Инварианты, от которых зависят другие подсистемы:
/// - детерминизм: один и тот же проект даёт одни и те же Id — иначе сломается
///   раннее связывание (пакет собирается тем же ProjectLoader, индексы совпадают);
/// - TagId продолжают плотный ряд после тегов проекта (TagTable индексируется напрямую);
/// - сгенерированное помечается Origin=Diagnostics и именами с префиксом '@'
///   (валидатор запрещает их в исходных файлах, ProjectWriter не сохраняет).
/// </summary>
public static class DiagnosticsGenerator
{
    /// <summary>Префикс системных имён. Зарезервирован: инженеру запрещён валидатором.</summary>
    public const char SystemPrefix = '@';

    // состав метрик канала — фиксированный и append-only, от порядка зависят Id
    public const string ConnectedSuffix = "Connected";               // 0/1
    public const string LastOkTimeSuffix = "LastOkTime";             // unix ms последнего успеха
    public const string RequestsOkSuffix = "RequestsOk";             // счётчик
    public const string RequestsFailedSuffix = "RequestsFailed";     // счётчик
    public const string ReconnectCountSuffix = "ReconnectCount";     // счётчик
    public const string ResponseTimeAvgSuffix = "ResponseTimeAvg";   // мс
    public const string ResponseTimeMaxSuffix = "ResponseTimeMax";   // мс

    private static readonly (string Suffix, TagDataType DataType)[] Metrics =
    [
        (ConnectedSuffix, TagDataType.Discrete),
        (LastOkTimeSuffix, TagDataType.Analog),
        (RequestsOkSuffix, TagDataType.Analog),
        (RequestsFailedSuffix, TagDataType.Analog),
        (ReconnectCountSuffix, TagDataType.Analog),
        (ResponseTimeAvgSuffix, TagDataType.Analog),
        (ResponseTimeMaxSuffix, TagDataType.Analog)
    ];

    public static string DeviceName(string channelName) => $"{SystemPrefix}{channelName}";

    public static bool IsSystemDevice(DeviceDefinition device) => device.Name.StartsWith(SystemPrefix);
    public static bool IsSystemTag(TagDefinition tag) => tag.Origin != TagOrigin.Process;

    /// <summary>
    /// Добавить диагностические устройства и теги в загруженную конфигурацию.
    /// Вызывается после валидации исходной формы: существующие Id не трогаем,
    /// новые назначаем продолжением ряда.
    /// </summary>
    public static void AppendDiagnostics(ProjectConfiguration config)
    {
        if (config.Channels.Count == 0)
            return;

        int nextDeviceId = config.Devices.Count == 0 ? 0 : config.Devices.Max(d => d.Id.Value) + 1;
        int nextTagId = config.Tags.Count == 0 ? 0 : config.Tags.Max(t => t.Id.Value) + 1;

        var devices = config.Devices.ToList();
        var tags = config.Tags.ToList();

        foreach (var channel in config.Channels)
        {
            var device = new DeviceDefinition
            {
                Id = new DeviceId(nextDeviceId++),
                Name = DeviceName(channel.Name),
                Description = $"Диагностика канала '{channel.Name}'",
                DriverName = "internal", // значения пишет движок, опрос не нужен
                ChannelId = channel.Id
            };
            devices.Add(device);

            string prefix = $"{device.Name}.";
            foreach (var (suffix, dataType) in Metrics)
            {
                tags.Add(new TagDefinition
                {
                    Id = new TagId(nextTagId++),
                    Name = prefix + suffix,
                    DataType = dataType,
                    DeviceId = device.Id,
                    Origin = TagOrigin.Diagnostics,
                    InitValue = 0
                });
            }
        }

        config.Devices = devices;
        config.Tags = tags;
    }
}
