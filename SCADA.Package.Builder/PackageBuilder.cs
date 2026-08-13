using SCADA.Expressions.Compiler;
using SCADA.Package.Builder.Sections;
using SCADA.Runtime.Configuration;

namespace SCADA.Package.Builder;

/// <summary>
/// Сборка проекта в .scadapkg — конвейер (ТЗ §14.2):
/// исходный каталог → загрузка+валидация → секции → пакет.
/// Новые стадии (схемы в M3, правила сигнализации в M5) — новые строки здесь.
/// </summary>
public static class PackageBuilder
{
    public static void Build(string projectDirectory, string outputPath,
        IReadOnlyList<CompiledExpression>? expressions = null)
    {
        // загрузка включает валидацию — битый проект не собирается в пакет
        var config = ProjectLoader.Load(projectDirectory);

        var writer = new PackageWriter();
        writer.AddEntry("tags.bin", TagsSectionWriter.Write(config.Tags));
        writer.AddEntry("devices.bin", DevicesSectionWriter.Write(config.Channels, config.Devices));
        writer.AddEntry("code.bin", CodeSectionWriter.Write(expressions ?? []));

        writer.Save(outputPath, config.Name, config.Version);
    }
}
