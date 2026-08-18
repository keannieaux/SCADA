using SCADA.Core.Tags;

namespace SCADA.Drivers.Abstractions;

/// <summary>Один элемент пакета записи на уровне драйвера: тег + СЫРОЕ значение
/// (обратное масштабирование применяет движок, как и на чтении).</summary>
public readonly record struct DriverWriteItem(TagDefinition Tag, double RawValue);

/// <summary>
/// Capability-интерфейс записи (M7). Драйвер, умеющий писать в устройство,
/// реализует его дополнительно к IDeviceDriver; драйвер без записи —
/// не реализует, и запрос записи получает внятный отказ.
///
/// Интерфейс batch-native: одиночная запись — пакет из одного элемента,
/// двух путей кода нет. Семантика двухфазная: драйвер сначала кодирует ВСЕ
/// элементы (ошибка кодирования — весь пакет отклоняется с ValidationFailed,
/// устройство не трогается), потом передаёт. Поэлементный результат —
/// драйвер знает, какие теги ехали в каком запросе протокола.
/// Исключение = потеря связи: обрабатывает движок (теги Bad, переподключение),
/// как ошибку опроса.
///
/// Вызывается только из цикла канала опроса — конкурентно с PollAsync
/// не бывает, драйверу не нужна своя синхронизация сокета.
/// </summary>
public interface IWritableDeviceDriver : IDeviceDriver
{
    Task<TagWriteResult[]> WriteAsync(IReadOnlyList<DriverWriteItem> items, CancellationToken ct);
}
