using SCADA.Core.Devices;
using SCADA.Core.Tags;

namespace SCADA.Drivers.Abstractions;

public interface IDeviceDriver
{
    string ProtocolName{get;}
    Task ConnectAsync(DeviceDefinition device, IReadOnlyList<TagDefinition> tags, CancellationToken ct);
    // Отдать ТЕКУЩИЕ значения тегов (в порядке списка из ConnectAsync).
    // Драйвер сам выбирает: сетевой запрос (Modbus) или буфер подписки (OPC UA).
    // true  — буфер содержит свежие значения, движок запишет их в таблицу;
    // false — новых данных нет (internal-теги, подписка без обновлений).
    // Значения — СЫРЫЕ: масштабирование (ScaleFactor/ScaleOffset) применяет
    // движок централизованно; в TagTable всегда инженерные единицы.
    ValueTask<bool> PollAsync(Memory<TagValue> results, CancellationToken ct);
    Task DisconnectAsync();
}
