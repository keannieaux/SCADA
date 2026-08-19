namespace SCADA.Package;

public sealed record PackageEntryInfo(string Name, string Sha256);

public sealed class PackageManifest
{
    public const int CurrentFormatVersion = 1;
    public required int FormatVersion{get; init;}
    public required string ProjectName{get; init;}
    public required string ProjectVersion{get; init;}
    public DateTimeOffset CreatedUtc{get; init;}

    /// <summary>Стартовый экран (project.json → пакет → SchemeInfo.IsStart).
    /// Опционально: старые пакеты читаются с null.</summary>
    public string? StartScheme { get; init; }

    public IReadOnlyList<PackageEntryInfo> Entries {get; init;} = [];
}

