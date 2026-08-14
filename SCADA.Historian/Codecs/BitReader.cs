namespace SCADA.Historian;

/// <summary>
/// Читатель битового потока, зеркален <see cref="BitWriter"/>:
/// старший бит байта — первый. Выход за границу — ошибка формата,
/// а не тихий мусор (файл мог быть повреждён).
/// </summary>
/// <remarks>
/// <c>ref struct</c> над <see cref="ReadOnlySpan{T}"/>, а не класс над массивом:
/// иначе каждый разбор блока копировал бы полезную нагрузку по разу на секцию,
/// и чтение года по десяти тегам давало бы сотни мегабайт мусора при бюджете
/// RAM 500 МБ (ТЗ §4.1, §15.2). Декодирование нигде не пересекает await,
/// поэтому ограничение ref struct ничего не стоит.
/// </remarks>
public ref struct BitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPosition;

    public BitReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _bitPosition = 0;
    }

    public readonly int BitPosition => _bitPosition;

    public readonly int BitsRemaining => _data.Length * 8 - _bitPosition;

    public int ReadBit()
    {
        if (BitsRemaining < 1)
            throw new InvalidDataException("Битовый поток закончился неожиданно");

        int byteIndex = _bitPosition >> 3;
        int bitIndex = _bitPosition & 7;
        _bitPosition++;

        return (_data[byteIndex] >> (7 - bitIndex)) & 1;
    }

    /// <summary>Читает count бит как беззнаковое число, старший бит первый.</summary>
    public ulong ReadBits(int count)
    {
        if (BitsRemaining < count)
            throw new InvalidDataException("Битовый поток закончился неожиданно");

        ulong value = 0;
        for (int i = 0; i < count; i++)
            value = (value << 1) | (uint)ReadBit();
        return value;
    }
}
