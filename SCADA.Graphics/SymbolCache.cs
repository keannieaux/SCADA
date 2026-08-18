using SkiaSharp;
using Svg.Skia;

namespace SCADA.Graphics;

internal static class SymbolCache
{
    private static readonly Dictionary<string, SKSvg> _cache=new();


    public static SKPicture Load(string path)
    {
        if(_cache.TryGetValue(path, out var cachedSvg))
            return cachedSvg.Picture!;

        var svg=new SKSvg();
        svg.Load(path);

        if(svg.Picture is null)
            throw new InvalidOperationException($"Не удалось загрузить SVG: '{path}'");

        _cache[path]=svg;
        return svg.Picture;
    }
}
