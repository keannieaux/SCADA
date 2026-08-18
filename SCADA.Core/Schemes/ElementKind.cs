namespace SCADA.Core.Schemes;

/// <summary>
/// Вид элемента схемы (docs/visualization-concept.md §3). Числовые значения
/// стабильны: они пишутся в пакет; новый вид = новый номер, старые никогда
/// не переиспользуются. Неизвестный номер при чтении пропускается (§11.2).
/// </summary>
public enum ElementKind : byte
{
    Rectangle = 0,
    Ellipse = 1,
    Line = 2,
    Polyline = 3,
    Path = 4,
    Text = 5,
    Image = 6,
    Symbol = 7,

    /// <summary>Слот hosted-контрола: тренд, журнал аварий и т.п. (§8).</summary>
    Control = 20,

    /// <summary>Экземпляр шаблона: ссылка на templates/<имя> + параметры (§7).</summary>
    Instance = 21,
}
