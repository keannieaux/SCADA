using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SCADA.Package;

/// <summary>
/// Читатель пакета .scadapkg (zip-контейнер).
/// При открытии: читает манифест, проверяет версию формата (§14.5)
/// и контрольные суммы всех секций (§14.4) — ДО любого применения.
/// Неизвестные структуры не «угадываются» — только внятный отказ.
/// </summary>
public sealed class PackageReader : IDisposable
{
    public const string ManifestFileName = "manifest.json";

    private readonly ZipArchive _archive;

    private PackageReader(ZipArchive archive, PackageManifest manifest)
    {
        _archive = archive;
        Manifest = manifest;
    }

    public PackageManifest Manifest { get; }

    public static PackageReader Open(string path)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (InvalidDataException)
        {
            throw new PackageFormatException($"Файл '{path}' не является пакетом .scadapkg");
        }

        try
        {
            var manifestEntry = archive.GetEntry(ManifestFileName)
                ?? throw new PackageFormatException("В пакете отсутствует manifest.json");

            PackageManifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize(
                    ReadAll(manifestEntry), PackageJsonContext.Default.PackageManifest)
                    ?? throw new PackageFormatException("manifest.json пуст");
            }
            catch (JsonException ex)
            {
                throw new PackageFormatException($"manifest.json повреждён: {ex.Message}");
            }

            // §14.5: версия из будущего — внятный отказ, не попытка угадать
            if (manifest.FormatVersion > PackageManifest.CurrentFormatVersion)
                throw new PackageFormatException(
                    $"Пакет собран более новой версией инженерной поставки " +
                    $"(формат {manifest.FormatVersion}, поддерживается {PackageManifest.CurrentFormatVersion}). " +
                    $"Обновите исполнительную поставку.");

            // §14.4: контрольные суммы всех секций — до применения
            var errors = new List<string>();
            foreach (var entryInfo in manifest.Entries)
            {
                var entry = archive.GetEntry(entryInfo.Name);
                if (entry is null)
                {
                    errors.Add($"В пакете отсутствует файл '{entryInfo.Name}'");
                    continue;
                }

                var hash = Convert.ToHexString(SHA256.HashData(ReadAll(entry)));
                if (!hash.Equals(entryInfo.Sha256, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Файл '{entryInfo.Name}' повреждён (контрольная сумма не совпадает)");
            }

            if (errors.Count > 0)
                throw new PackageFormatException(errors);

            return new PackageReader(archive, manifest);
        }
        catch
        {
            archive.Dispose(); // не оставляем файл открытым при любой ошибке
            throw;
        }
    }

    /// <summary>Содержимое секции. Контрольная сумма уже проверена при Open.</summary>
    public byte[] ReadEntry(string name)
    {
        var entry = _archive.GetEntry(name)
            ?? throw new PackageFormatException($"В пакете нет секции '{name}'");
        return ReadAll(entry);
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public void Dispose() => _archive.Dispose();
}
