using System.Globalization;
using System.Text;

namespace SCADA.Historian;

/// <summary>
/// Исключительный захват каталога архива (docs/archive-format.md §16.1).
/// Два экземпляра службы на один каталог — типичная ошибка развёртывания
/// (служба уже зарегистрирована, инженер запускает консольный вариант),
/// дающая перемешанный журнал и порванные блоки. Повреждение неремонтируемое,
/// поэтому отказ при старте.
/// </summary>
/// <remarks>
/// Читатели каталог не блокируют: файл открывается с <see cref="FileShare.Read"/>,
/// поэтому разбор архива сторонним экземпляром возможен во время записи.
/// </remarks>
public sealed class ArchiveDirectoryLock : IDisposable
{
    private const string LockFileName = ".lock";

    private readonly FileStream _stream;
    private bool _disposed;

    private ArchiveDirectoryLock(FileStream stream) => _stream = stream;

    public static ArchiveDirectoryLock Acquire(string archiveRoot)
    {
        Directory.CreateDirectory(archiveRoot);
        string path = Path.Combine(archiveRoot, LockFileName);

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Каталог архива \"{archiveRoot}\" уже занят другим процессом. " +
                "Одновременная запись двумя экземплярами повредит архив: " +
                "остановите работающую службу либо укажите другой каталог.", ex);
        }

        string owner = string.Create(CultureInfo.InvariantCulture,
            $"pid={Environment.ProcessId} host={Environment.MachineName} " +
            $"since={DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");

        stream.Write(Encoding.UTF8.GetBytes(owner));
        stream.Flush(flushToDisk: true);

        return new ArchiveDirectoryLock(stream);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        string? path = (_stream as FileStream)?.Name;
        _stream.Dispose();

        // Файл удаляем, чтобы не оставлять мусор; если не вышло — не беда,
        // следующий запуск перезапишет его через FileMode.Create.
        try
        {
            if (path is not null)
                File.Delete(path);
        }
        catch (IOException)
        {
            // каталог мог быть удалён вместе с архивом
        }
    }
}
