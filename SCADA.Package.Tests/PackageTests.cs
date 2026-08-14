using System.IO.Compression;
using System.Text;
using SCADA.Package.Builder;

namespace SCADA.Package.Tests;

public class PackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private string PackagePath => Path.Combine(_dir, "project.scadapkg");

    public PackageTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteSamplePackage()
    {
        var writer = new PackageWriter();
        writer.AddEntry("tags.bin", [1, 2, 3, 4]);
        writer.AddEntry("devices.bin", "Байты устройств"u8.ToArray());
        writer.Save(PackagePath, "TestProject", "1.2");
    }

    [Fact]
    public void RoundTrip_EntriesAndManifest_Preserved()
    {
        WriteSamplePackage();

        using var reader = PackageReader.Open(PackagePath);

        Assert.Equal("TestProject", reader.Manifest.ProjectName);
        Assert.Equal("1.2", reader.Manifest.ProjectVersion);
        Assert.Equal(PackageManifest.CurrentFormatVersion, reader.Manifest.FormatVersion);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, reader.ReadEntry("tags.bin"));
        Assert.Equal("Байты устройств", Encoding.UTF8.GetString(reader.ReadEntry("devices.bin")));
    }

    [Fact]
    public void Open_CorruptedEntry_ThrowsAboutChecksum()
    {
        WriteSamplePackage();

        // портим содержимое tags.bin, манифест не трогаем
        var tempPath = PackagePath + ".broken";
        using (var archive = ZipFile.Open(PackagePath, ZipArchiveMode.Read))
        {
            var entries = archive.Entries
                .Select(e => (e.FullName, Content: ReadBytes(e)))
                .ToArray();

            using var stream = File.Create(tempPath);
            using var broken = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                if (name == "tags.bin")
                    content[0] ^= 0xFF; // битая секция
                var entry = broken.CreateEntry(name);
                using var s = entry.Open();
                s.Write(content);
            }
        }
        File.Move(tempPath, PackagePath, overwrite: true);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(PackagePath));
        Assert.Contains(ex.Errors, e => e.Contains("tags.bin") && e.Contains("повреждён"));
    }

    [Fact]
    public void Open_FutureFormatVersion_RefusesClearly()
    {
        WriteSamplePackage();

        // переписываем манифест с версией из будущего
        var tempPath = PackagePath + ".future";
        using (var archive = ZipFile.Open(PackagePath, ZipArchiveMode.Read))
        {
            var entries = archive.Entries
                .Select(e => (e.FullName, Content: ReadBytes(e)))
                .ToArray();

            using var stream = File.Create(tempPath);
            using var future = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var (name, originalContent) in entries)
            {
                var content = originalContent;
                if (name == "manifest.json")
                {
                    var json = Encoding.UTF8.GetString(content)
                        .Replace("\"formatVersion\": 1", "\"formatVersion\": 99");
                    content = Encoding.UTF8.GetBytes(json);
                }
                var entry = future.CreateEntry(name);
                using var s = entry.Open();
                s.Write(content);
            }
        }
        File.Move(tempPath, PackagePath, overwrite: true);

        var ex = Assert.Throws<PackageFormatException>(() => PackageReader.Open(PackagePath));
        Assert.Contains("99", ex.Message);
        Assert.Contains("более новой", ex.Message);
    }

    [Fact]
    public void ReadEntry_MissingSection_Throws()
    {
        WriteSamplePackage();

        using var reader = PackageReader.Open(PackagePath);

        Assert.Throws<PackageFormatException>(() => reader.ReadEntry("schemes/overview.bin"));
    }

    [Fact]
    public void AddEntry_Duplicate_Throws()
    {
        var writer = new PackageWriter();
        writer.AddEntry("tags.bin", [1]);

        Assert.Throws<ArgumentException>(() => writer.AddEntry("tags.bin", [2]));
    }

    [Fact]
    public void Open_NotAZip_Throws()
    {
        File.WriteAllText(PackagePath, "это не пакет, а текстовый файл");

        Assert.Throws<PackageFormatException>(() => PackageReader.Open(PackagePath));
    }

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
