namespace SCADA.Drivers.Modbus;

/// <summary>Тег, привязанный к адресу: куда класть значение + откуда читать.</summary>
public readonly record struct ModbusTagMapping(int ResultIndex, ModbusAddress Address);

/// <summary>Позиция тега внутри блока запроса.</summary>
public readonly record struct BlockItem(int ResultIndex, int OffsetWithinBlock);

/// <summary>
/// Сгруппированный запрос: чтение Count регистров (или бит) таблицы Table
/// начиная со Start. Items — какие теги на каких смещениях лежат в ответе.
/// </summary>
public sealed record RequestBlock(
    ModbusTable Table, int Start, int Count, IReadOnlyList<BlockItem> Items);

/// <summary>
/// Оптимизатор запросов (ТЗ §7.3): сливает соседние адреса в блоки,
/// чтобы 50 тегов в соседних регистрах читались одним запросом.
/// Чистая логика без сети — тестируется отдельно.
/// </summary>
public static class RequestGrouper
{
    // лимиты протокола Modbus на один запрос
    private const int MaxRegistersPerRequest = 125;
    private const int MaxBitsPerRequest = 2000;

    /// <summary>
    /// maxGap — максимальный зазор (в регистрах/битах), через который слияние
    /// выгодно: читать 3 лишних регистра дешевле, чем делать второй запрос.
    /// </summary>
    public static IReadOnlyList<RequestBlock> Group(
        IReadOnlyList<ModbusTagMapping> tags, int maxGap = 8)
    {
        var blocks = new List<RequestBlock>();

        foreach (var tableGroup in tags.GroupBy(t => t.Address.Table))
        {
            var sorted = tableGroup.OrderBy(t => t.Address.Offset).ToArray();
            int maxPerRequest = tableGroup.Key is ModbusTable.Coil or ModbusTable.DiscreteInput
                ? MaxBitsPerRequest
                : MaxRegistersPerRequest;

            int blockStart = -1, blockEnd = -1; // [blockStart, blockEnd)
            var items = new List<BlockItem>();

            foreach (var tag in sorted)
            {
                int tagStart = tag.Address.Offset;
                int tagEnd = tagStart + Math.Max(1, tag.Address.RegisterCount);

                bool fits = blockStart >= 0
                    && tagStart - blockEnd <= maxGap          // зазор приемлемый
                    && tagEnd - blockStart <= maxPerRequest;  // и блок влезает в лимит

                if (!fits)
                {
                    Flush();
                    blockStart = tagStart;
                    blockEnd = tagStart; // новый блок — сброс, не наследуем конец предыдущего
                    items = new List<BlockItem>();
                }

                items.Add(new BlockItem(tag.ResultIndex, tagStart - blockStart));
                blockEnd = Math.Max(blockEnd, tagEnd);
            }

            Flush();

            void Flush()
            {
                if (blockStart < 0)
                    return;
                blocks.Add(new RequestBlock(
                    tableGroup.Key, blockStart, blockEnd - blockStart, items.ToArray()));
            }
        }

        return blocks;
    }
}
