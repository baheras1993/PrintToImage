using SkiaSharp;

namespace PrintToImage.PostScript.Fonts;

/// <summary>A font whose glyphs can be embedded in PDF output as a TrueType subset.</summary>
public interface IEmbeddableFont
{
    /// <summary>Identifies the font program within a job.</summary>
    string EmbedKey { get; }
    string EmbedName { get; }
    /// <summary>Source of head/hhea/hmtx/maxp tables, or null if the font has no TrueType outlines.</summary>
    TrueTypeData? EmbedData { get; }
    /// <summary>Character-space units per em (1000 for Type 1 style fonts, 1 for Type 42).</summary>
    double CharSpacePerEm { get; }
    /// <summary>Glyph id for a character, or -1.</summary>
    int EmbedGid(PSDict dict, string name, int cid, bool byCid);
    /// <summary>The glyph description as it is now (Type 42 glyphs may still be downloaded later).</summary>
    byte[]? EmbedGlyph(PSDict dict, int gid);
    /// <summary>Unicode code point of a character (for searchable PDF text), or -1.</summary>
    int EmbedUnicode(PSDict dict, string name, int cid, bool byCid);
}

/// <summary>
/// Collects the glyphs a job uses per font and, when the job is complete, builds one subset font per font
/// (SkiaSharp's PDF backend cannot subset fonts itself).
/// </summary>
public sealed class FontEmbedder : IDisposable
{
    public sealed class Source
    {
        public readonly IEmbeddableFont Font;
        public readonly Dictionary<int, byte[]> Glyphs = new();
        public readonly Dictionary<int, int> Unicode = new();   // original glyph id -> code point
        public SKTypeface? Typeface;
        public Dictionary<int, int>? Map;
        public bool Failed;
        public Source(IEmbeddableFont f) => Font = f;
    }

    private readonly Dictionary<string, Source> _sources = new();
    private readonly List<IDisposable> _keepAlive = new();
    public bool Built { get; private set; }

    /// <summary>Registers use of a glyph and captures its description. Returns null if the font cannot be embedded.</summary>
    public Source? Use(IEmbeddableFont f, PSDict dict, int gid, string name, int cid, bool byCid)
    {
        if (Built || f.EmbedData is null) return null;
        if (!_sources.TryGetValue(f.EmbedKey, out var src)) _sources[f.EmbedKey] = src = new Source(f);
        if (src.Failed) return null;
        if (!src.Glyphs.ContainsKey(gid))
        {
            int u = f.EmbedUnicode(dict, name, cid, byCid);
            if (u > 0) src.Unicode[gid] = u;
        }
        Capture(src, dict, gid, 0);
        return src;
    }

    private static void Capture(Source src, PSDict dict, int gid, int depth)
    {
        if (depth > 8 || src.Glyphs.ContainsKey(gid)) return;
        var data = src.Font.EmbedGlyph(dict, gid) ?? Array.Empty<byte>();
        src.Glyphs[gid] = data;
        foreach (var c in TrueTypeData.Components(data)) Capture(src, dict, c, depth + 1);
    }

    public void Keep(IDisposable d) => _keepAlive.Add(d);

    /// <summary>Builds the subset fonts. Call once, after the last page.</summary>
    public void BuildAll(Action<string>? log)
    {
        if (Built) return;
        Built = true;
        foreach (var src in _sources.Values)
        {
            try
            {
                var r = TtfBuilder.BuildSubset(src.Font.EmbedData!, src.Glyphs, src.Unicode, src.Font.EmbedName);
                var tf = r is null ? null : SKTypeface.FromData(SKData.CreateCopy(r.Value.Font));
                if (tf is null) { src.Failed = true; continue; }
                src.Typeface = tf;
                src.Map = r!.Value.Map;
            }
            catch (Exception e)
            {
                src.Failed = true;
                log?.Invoke($"Font {src.Font.EmbedName} could not be embedded ({e.Message}); its text is drawn as outlines.");
            }
        }
    }

    public void Dispose()
    {
        foreach (var d in _keepAlive) d.Dispose();
        _keepAlive.Clear();
        foreach (var s in _sources.Values) s.Typeface?.Dispose();
    }
}

/// <summary>
/// A run of glyphs recorded into a page, drawn when the PDF is written (after the subset fonts exist).
/// Coordinates are glyph space: font size 1000, y down.
/// </summary>
public sealed class TextDrawable : SKDrawable
{
    private readonly FontEmbedder.Source _src;
    private readonly ushort[] _gids;          // original glyph ids
    private readonly SKPoint[] _positions;
    private readonly SKPath[] _fallback;      // outlines in glyph space, used if the font could not be built
    private readonly SKPaint _paint;
    private readonly SKRect _bounds;

    public TextDrawable(FontEmbedder.Source src, ushort[] gids, SKPoint[] positions, SKPath[] fallback, SKPaint paint)
    {
        _src = src;
        _gids = gids;
        _positions = positions;
        _fallback = fallback;
        _paint = paint;
        float minX = positions.Min(p => p.X) - 3000, minY = positions.Min(p => p.Y) - 3000;
        float maxX = positions.Max(p => p.X) + 3000, maxY = positions.Max(p => p.Y) + 3000;
        _bounds = new SKRect(minX, minY, maxX, maxY);
    }

    protected override SKRect OnGetBounds() => _bounds;

    protected override void OnDraw(SKCanvas canvas)
    {
        if (_src.Typeface is not null && _src.Map is not null && !_src.Failed)
        {
            using var font = new SKFont(_src.Typeface, 1000) { Subpixel = true, LinearMetrics = true, Hinting = SKFontHinting.None };
            using var builder = new SKTextBlobBuilder();
            var run = builder.AllocatePositionedRun(font, _gids.Length);
            run.SetGlyphs(_gids.Select(g => (ushort)(_src.Map.TryGetValue(g, out var n) ? n : 0)).ToArray());
            run.SetPositions(_positions);
            using var blob = builder.Build();
            if (blob is not null) canvas.DrawText(blob, 0, 0, _paint);
            return;
        }
        for (int i = 0; i < _fallback.Length; i++)
        {
            canvas.Save();
            canvas.Translate(_positions[i].X, _positions[i].Y);
            canvas.DrawPath(_fallback[i], _paint);
            canvas.Restore();
        }
    }

    protected override void DisposeManaged()
    {
        _paint.Dispose();
        foreach (var p in _fallback) p.Dispose();
        base.DisposeManaged();
    }
}
