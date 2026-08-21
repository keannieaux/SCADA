using SkiaSharp;
using SCADA.Runtime.Runtime;
using Svg.Skia;

namespace SCADA.Graphics;

/// <summary>
/// Символы приезжают ассетами пакета ("symbols/имя.svg"), диск не используется.
/// Кэш по имени ассета: один символ обычно стоит на многих элементах, а вызов
/// GetAsset может быть дорогим (в remote — сетевым).
/// SKSvg держим целиком, а не только Picture: он владеет нативной памятью
/// картинки, и его освобождение сделало бы Picture висячим указателем.
/// </summary>
internal static class SymbolCache
{
    private static readonly Dictionary<string, SKSvg> _cache=new();

    public static SKPicture Load(string assetName, IRuntimeClient client)
    {
        if(_cache.TryGetValue(assetName, out var cached))
            return cached.Picture!;

        var svg=new SKSvg();
        using(var stream=new MemoryStream(client.GetAsset(assetName)))
            svg.Load(stream);

        if(svg.Picture is null)
            throw new InvalidOperationException($"Не удалось разобрать символ '{assetName}'");

        _cache[assetName]=svg;
        return svg.Picture;
    }
}
