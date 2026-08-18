using SCADA.Expressions.Compiler;

namespace SCADA.Package.Builder;

/// <summary>
/// Сборка проекта в .scadapkg — исключательный API поверх
/// <see cref="ProjectBuildService"/> (ТЗ §14.2): при любой ошибке бросает
/// InvalidOperationException с объединёнными сообщениями, как и раньше.
/// Инструментам и IDE — ProjectBuildService со структурированными диагностиками.
/// </summary>
public static class PackageBuilder
{
    public static void Build(string projectDirectory, string outputPath,
        IReadOnlyList<CompiledExpression>? expressions = null,
        Action<string>? report = null)
    {
        var result = ProjectBuildService.BuildCore(projectDirectory, outputPath, expressions);

        if (!result.Success)
        {
            throw new InvalidOperationException(string.Join("\n",
                result.Diagnostics
                    .Where(d => d.Severity == BuildSeverity.Error)
                    .Select(d => d.Message)));
        }

        // report получает те же строки отчёта об объёме архива, что и раньше
        foreach (var diagnostic in result.Diagnostics)
            if (diagnostic.Severity is BuildSeverity.Info or BuildSeverity.Warning)
                report?.Invoke(diagnostic.Message);
    }
}
