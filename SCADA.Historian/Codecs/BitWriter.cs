namespace SCADA.Historian;

/// <summary>
/// Потоковый писатель бит: байты заполняются от старшего бита к младшему
/// (docs/archive-format.md §8). Хвост последнего байта добивается нулями.
/// Используется кодеками меток времени и значений — сам про их смысл не знает.
/// </summary>
public sealed class BitWriter
{
    private readonly List<byte> _bytes = new();
    private int _current;   // заполняемый байт
    private int _filled;    // занятых бит в нём (0..7)

    /// <summary>Число записанных бит (для диагностики и замеров сжатия).</summary>
    public long BitLength { get; private set; }

    public void WriteBit(int bit)
    {
        _current = (_current << 1) | (bit & 1);
        _filled++;
        BitLength++;

        if (_filled == 8)
        {
            _bytes.Add((byte)_current);
            _current = 0;
            _filled = 0;
        }
    }

    /// <summary>Пишет count младших бит value, старшим вперёд.</summary>
    public void WriteBits(ulong value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
            WriteBit((int)(value >> i) & 1);
    }

    public byte[] ToArray()
    {
        if (_filled == 0)
            return _bytes.ToArray();

        // хвост добит нулями: нулевые биты уже стоят, осталось выровнять
        var result = _bytes.ToArray();
        Array.Resize(ref result, result.Length + 1);
        result[^1] = (byte)(_current << (8 - _filled));
        return result;
    }
}
