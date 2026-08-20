namespace SCADA.Package.Builder;

/// <summary>Важность диагностики сборки проекта.</summary>
public enum BuildSeverity
{
    /// <summary>Информационное сообщение (отчёт об объёме архива).</summary>
    Info,

    /// <summary>Предупреждение: пакет собран, но требует внимания.</summary>
    Warning,

    /// <summary>Ошибка: пакет не записывается.</summary>
    Error
}

/// <summary>
/// Структурированная диагностика сборки для панели «Проблемы» в IDE.
/// <paramref name="Source"/> — источник: project / alarm:&lt;имя правила&gt; / rights /
/// alarms:sounds / archive / build.
/// </summary>
public sealed record BuildDiagnostic(BuildSeverity Severity, string Source, string Message);

/// <summary>
/// Результат сборки проекта в .scadapkg. <see cref="Success"/> =
/// ни одной диагностики уровня <see cref="BuildSeverity.Error"/>;
/// пакет пишется только при успехе.
/// </summary>
public sealed record BuildResult(
    bool Success, string? PackagePath, IReadOnlyList<BuildDiagnostic> Diagnostics);
