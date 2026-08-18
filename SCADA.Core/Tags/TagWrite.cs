namespace SCADA.Core.Tags;

/// <summary>Один элемент команды записи: тег + значение в инженерных единицах.</summary>
public readonly record struct TagWriteItem(TagId TagId, double Value);

/// <summary>Статус записи одного тега (M7).</summary>
public enum TagWriteStatus : byte
{
    Ok = 0,
    /// <summary>Тег не помечен IsWritable или не существует.</summary>
    NotWritable = 1,
    /// <summary>Драйвер устройства не реализует запись.</summary>
    WriteNotSupported = 2,
    /// <summary>Устройство отключено. Команда не ждёт переподключения —
    /// отложенное срабатывание для оператора хуже отказа.</summary>
    DeviceOffline = 3,
    /// <summary>Устройство отвергло запись (exception response, недопустимый адрес).</summary>
    RejectedByDevice = 4,
    /// <summary>Истёк таймаут ожидания исполнения в канале.</summary>
    Timeout = 5,
    /// <summary>Значение не кодируется в тип устройства (вне диапазона и т.п.).
    /// Пакет с таким элементом отклоняется целиком, до записи (§13).</summary>
    ValidationFailed = 6,
    /// <summary>Прочая ошибка исполнения.</summary>
    Failed = 7
}

/// <summary>Результат записи одного тега.</summary>
public readonly record struct TagWriteResult(TagWriteStatus Status, string? Error = null)
{
    public static TagWriteResult Success { get; } = new(TagWriteStatus.Ok);
}
