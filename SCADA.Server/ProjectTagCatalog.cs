using SCADA.Expressions.Compiler;

namespace SCADA.Server;

/// <summary>
/// Каталог тегов проекта для компилятора выражений. Используется только в
/// dev-режиме (исходный каталог проекта): боевая поставка получает готовый
/// байткод из пакета и компилятор не содержит (ТЗ §5.4).
/// </summary>
public sealed class ProjectTagCatalog : ITagCatalog
{
    private readonly Dictionary<string, int> _byName;

    public ProjectTagCatalog(ProjectConfiguration config)
        => _byName = config.Tags.ToDictionary(t => t.Name, t => t.Id.Value);

    public bool TryGetIndex(string name, out int index)
        => _byName.TryGetValue(name, out index);
}
