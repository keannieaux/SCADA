using SCADA.Core.Tags;

namespace SCADA.Runtime.Historian;

/// <summary>
/// Реестр архивных потоков: сопоставляет имя тега и его архивный streamId.
/// Реестр ведёт файл <c>archive/streams.idx</c>, идентификаторы выдаются
/// последовательно и никогда не переиспользуются.
/// </summary>
public interface IArchiveStreamRegistry
{
    /// <summary>Возвращает существующий streamId для имени тега или создаёт новый.</summary>
    int Resolve(string tagName, TagDataType dataType);
}
