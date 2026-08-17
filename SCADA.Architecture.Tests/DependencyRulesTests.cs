using System.Reflection;

namespace SCADA.Architecture.Tests;

/// <summary>
/// Автопроверка архитектурных правил зависимостей ТЗ §5.3.
/// Защищает от случайного протаскивания конкретных драйверов или
/// инструментального кода в runtime.
/// </summary>
public class DependencyRulesTests
{
    [Theory]
    [InlineData("SCADA.Core")]
    public void Core_DoesNotReferenceOtherProjects(string projectName)
    {
        var refs = GetReferencedProjectNames(projectName);
        Assert.True(refs.All(r => !r.StartsWith("SCADA.", StringComparison.OrdinalIgnoreCase)),
            $"{projectName} не должен ссылаться на другие проекты SCADA.");
    }

    [Theory]
    [InlineData("SCADA.Drivers.Modbus")]
    [InlineData("SCADA.Drivers.Simulator")]
    public void ConcreteDrivers_DoNotReferenceOtherConcreteDrivers(string projectName)
    {
        var refs = GetReferencedProjectNames(projectName);
        var otherDrivers = refs
            .Where(r => r.StartsWith("SCADA.Drivers.", StringComparison.OrdinalIgnoreCase)
                        && !r.Equals("SCADA.Drivers.Abstractions", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(otherDrivers);
    }

    [Fact]
    public void Runtime_DoesNotReferenceConcreteDrivers()
    {
        var refs = GetReferencedProjectNames("SCADA.Runtime");
        var concreteDrivers = refs
            .Where(r => r.StartsWith("SCADA.Drivers.", StringComparison.OrdinalIgnoreCase)
                        && !r.Equals("SCADA.Drivers.Abstractions", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(concreteDrivers);
    }

    [Fact]
    public void Runtime_DoesNotReferenceInstrumentalCode()
    {
        var refs = GetReferencedProjectNames("SCADA.Runtime");
        var forbidden = new[]
        {
            "SCADA.Package.Builder",
            "SCADA.Expressions.Compiler",
            "SCADA.Editor",
            "SCADA.Graphics",
            "SCADA.App.Engineering",
            "SCADA.App.Shared"
        };

        var actual = refs
            .Where(r => forbidden.Contains(r, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(actual);
    }

    [Fact]
    public void Historian_DoesNotReferenceRuntime()
    {
        var refs = GetReferencedProjectNames("SCADA.Historian");
        Assert.DoesNotContain("SCADA.Runtime", refs, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expressions_Compiler_DoesNotReferenceRuntime()
    {
        var refs = GetReferencedProjectNames("SCADA.Expressions.Compiler");
        Assert.DoesNotContain("SCADA.Runtime", refs, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetReferencedProjectNames(string assemblyName)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));
        return assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToList()!;
    }
}
