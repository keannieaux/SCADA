using SCADA.Expressions.Compiler;

namespace SCADA.Graphics;

public sealed class ProjectTagCatalog : ITagCatalog
{
    private readonly Dictionary<string, int> _byName;

    public ProjectTagCatalog(ProjectConfiguration config)
        => _byName=config.Tags.ToDictionary(t=>t.Name, t=>t.Id.Value);

    public bool TryGetIndex(string name, out int index)
        => _byName.TryGetValue(name,out index);
}
