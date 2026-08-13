using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SCADA.Package;

namespace SCADA.Package.Builder;

/// <summary>
/// Писатель пакета .scadapkg. Секции копятся в памяти, манифест
/// с контрольными суммами строится при Save автоматически.
/// Запись атомарная: temp-файл + замена — пакет ездит по сети на объект,
/// половинчатый файл недопустим.
/// </summary>
public sealed class PackageWriter
{
    private readonly Dictionary<string, byte[]> _entries = new();

    public void AddEntry(string name, byte[] content)
    {
        if (name == PackageReader.ManifestFileName)
            throw new ArgumentException($"'{name}' — служебное имя, оно пишется автоматически");
        if (!_entries.TryAdd(name, content))
            throw new ArgumentException($"Секция '{name}' уже добавлена");
    }

    public void Save(string path, string projectName, string projectVersion)
    {
        var manifest = new PackageManifest
        {
            FormatVersion = PackageManifest.CurrentFormatVersion,
            ProjectName = projectName,
            ProjectVersion = projectVersion,
            CreatedUtc = DateTimeOffset.UtcNow,
            Entries = _entries
                .Select(e => new PackageEntryInfo(e.Key, Convert.ToHexString(SHA256.HashData(e.Value))))
                .ToArray()
        };

        var manifestJson = JsonSerializer.SerializeToUtf8Bytes(
            manifest, PackageJsonContext.Default.PackageManifest);

        var tempPath = path + ".tmp";
        using (var stream = File.Create(tempPath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(archive, PackageReader.ManifestFileName, manifestJson);
            foreach (var (name, content) in _entries)
                WriteEntry(archive, name, content);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }
}
