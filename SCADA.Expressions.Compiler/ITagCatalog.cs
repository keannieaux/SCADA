namespace SCADA.Expressions.Compiler;

/// <summary>
/// Источник имён тегов для компилятора. Отвязывает компилятор от способа
/// хранения конфигурации: боевой каталог — над ProjectConfiguration,
/// редактор — для автодополнения (§11.9), тесты — словарь.
/// </summary>
public interface ITagCatalog
{
    bool TryGetIndex(string name, out int index);
}
