namespace SCADA.Graphics;

/// <summary>
/// Область просмотра содержимого фиксированного размера: масштаб и смещение
/// с ограничениями. Одна механика на три случая — вписывание схемы, пан-зум
/// схемы и будущая прокрутка попапа различаются только пределами, а не кодом.
///
/// Работает в ЛОГИЧЕСКИХ единицах Avalonia: системное масштабирование по DPI
/// она применяет сама, и пересчёт в физические пиксели здесь умножил бы всё
/// дважды (на машине со 125% схема была бы крупнее в 1.25 раза).
///
/// Правила (docs/visualization-concept.md §11, ответ A от 2026-08-21):
/// базовый масштаб вписывает проектную область целиком; отдалить меньше
/// вписанного нельзя — схема уплыла бы в угол окна; по осям, где содержимое
/// меньше окна, оно центрируется, а поля заливает фон схемы.
/// </summary>
public sealed class SchemeViewport(double designWidth, double designHeight,
    bool allowPanZoom, double maxZoom, double startZoom)
{
    private double _availableWidth;
    private double _availableHeight;

    /// <summary>Масштаб, при котором проектная область помещается целиком.</summary>
    public double FitScale { get; private set; } = 1;

    public double Scale { get; private set; } = 1;
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }

    public double DesignWidth { get; } = designWidth > 0 ? designWidth : 1;
    public double DesignHeight { get; } = designHeight > 0 ? designHeight : 1;

    /// <summary>Двигать содержимое можно, только если оно крупнее вписанного:
    /// при равном масштабе двигать нечего, и любое движение мыши уводило бы
    /// схему в угол.</summary>
    public bool CanPan => allowPanZoom && Scale > FitScale;

    public bool CanZoom => allowPanZoom;

    /// <summary>Размер окна изменился: пересчитать вписывание и подтянуть
    /// текущий вид под новые пределы.</summary>
    public void Resize(double availableWidth, double availableHeight)
    {
        _availableWidth = availableWidth;
        _availableHeight = availableHeight;

        double previousFit = FitScale;
        FitScale = Math.Min(availableWidth / DesignWidth, availableHeight / DesignHeight);
        if (!double.IsFinite(FitScale) || FitScale <= 0)
            FitScale = 1;

        // сохраняем то, во сколько раз пользователь приблизил относительно
        // вписанного: при растягивании окна вид не должен «прыгать»
        double userZoom = previousFit > 0 ? Scale / previousFit : 1;
        SetScale(FitScale * userZoom);
    }

    /// <summary>Вернуть исходный вид: вписано целиком, с учётом StartZoom.
    /// Нужна команда оператору — случайно утащив схему мышью, он не должен
    /// оставаться со сдвинутым экраном до перезапуска.</summary>
    public void Reset()
    {
        // StartZoom значим только при разрешённом пане: иначе оператор увидел
        // бы обрезанный экран без возможности его подвинуть
        SetScale(FitScale * (allowPanZoom ? startZoom : 1));
        Center();
    }

    /// <summary>Приближение колесом с якорем под курсором: точка под
    /// указателем остаётся на месте.</summary>
    public void ZoomAt(double pointerX, double pointerY, double factor)
    {
        if (!CanZoom)
            return;

        double previous = Scale;
        SetScale(Scale * factor);
        if (Scale == previous)
            return;

        double ratio = Scale / previous;
        OffsetX = pointerX - (pointerX - OffsetX) * ratio;
        OffsetY = pointerY - (pointerY - OffsetY) * ratio;
        Clamp();
    }

    public void PanBy(double deltaX, double deltaY)
    {
        if (!CanPan)
            return;

        OffsetX += deltaX;
        OffsetY += deltaY;
        Clamp();
    }

    /// <summary>Экранная точка → координаты схемы (клик, hit-test).</summary>
    public (double X, double Y) ToContent(double screenX, double screenY)
        => ((screenX - OffsetX) / Scale, (screenY - OffsetY) / Scale);

    /// <summary>Видимая часть проектной области в координатах схемы —
    /// для куллинга при построении визуалов.</summary>
    public (double X, double Y, double Width, double Height) VisibleContentRect()
        => (-OffsetX / Scale, -OffsetY / Scale,
            _availableWidth / Scale, _availableHeight / Scale);

    private void SetScale(double value)
    {
        double max = FitScale * (allowPanZoom ? Math.Max(maxZoom, 1) : 1);
        Scale = Math.Clamp(value, FitScale, max);
        Clamp();
    }

    private void Center()
    {
        OffsetX = (_availableWidth - DesignWidth * Scale) / 2;
        OffsetY = (_availableHeight - DesignHeight * Scale) / 2;
    }

    /// <summary>Одно правило на обе оси: если содержимое уже окна — центрируем
    /// (двигать нечего, поля симметричны), иначе не даём краю содержимого
    /// заехать внутрь окна.</summary>
    private void Clamp()
    {
        OffsetX = ClampAxis(OffsetX, DesignWidth * Scale, _availableWidth);
        OffsetY = ClampAxis(OffsetY, DesignHeight * Scale, _availableHeight);
    }

    private static double ClampAxis(double offset, double scaledContent, double available)
        => scaledContent <= available
            ? (available - scaledContent) / 2
            : Math.Clamp(offset, available - scaledContent, 0);
}
