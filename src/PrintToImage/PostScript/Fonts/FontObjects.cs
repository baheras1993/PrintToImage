using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace PrintToImage.PostScript.Fonts;

/// <summary>Glyph outline in character space (y up) with its advance.</summary>
public sealed record GlyphOutline(SKPath Path, double Wx, double Wy);

public abstract class FontBase
{
    public virtual GlyphOutline? GlyphByName(PSDict dict, string name) => null;
    public virtual GlyphOutline? GlyphByCid(PSDict dict, int cid) => null;
    public virtual bool IsCidFont => false;

    protected static Graphics.Matrix FontMatrixOf(PSDict d) =>
        d.Get("FontMatrix") is PSArray a && a.Length == 6 ? Interpreter.ToMatrix(a) : Graphics.Matrix.Identity;
}

/// <summary>Type 3: glyphs are produced by running BuildGlyph/BuildChar (handled by the interpreter).</summary>
public sealed class Type3Font : FontBase { }

/// <summary>Type 0 composite font; mapping data is read from the dictionary.</summary>
public sealed class Type0Font : FontBase { }

/// <summary>Type 1 font with encrypted charstrings. Glyphs missing from CharStrings use the fallback font.</summary>
public sealed class Type1Font : FontBase
{
    private readonly Dictionary<string, GlyphOutline?> _cache = new();
    private readonly FontBase? _fallback;

    public Type1Font(FontBase? fallback) => _fallback = fallback;

    public override GlyphOutline? GlyphByName(PSDict dict, string name)
    {
        if (_cache.TryGetValue(name, out var g)) return g;
        g = Build(dict, name);
        _cache[name] = g;
        return g;
    }

    private GlyphOutline? Build(PSDict dict, string name)
    {
        if (dict.Get("CharStrings") is not PSDict cs) return _fallback?.GlyphByName(dict, name);
        if (!cs.TryGet(name, out var obj) || obj is not PSString s) return name == ".notdef" ? null : _fallback?.GlyphByName(dict, name);
        var priv = dict.Get("Private") as PSDict;
        int lenIV = priv?.Get("lenIV") is { } l ? (int)Interpreter.Num(l) : 4;
        var subrs = priv?.Get("Subrs") as PSArray;
        byte[]? Subr(int n) => subrs is not null && n >= 0 && n < subrs.Length && subrs[n] is PSString ss ? Type1Charstrings.Decrypt(ss.Span, lenIV) : null;
        byte[]? Seac(int code)
        {
            if (code < 0 || code > 255) return null;
            var gname = Encodings.Standard[code];
            return cs.TryGet(gname, out var o) && o is PSString gs ? Type1Charstrings.Decrypt(gs.Span, lenIV) : null;
        }
        try
        {
            var r = Type1Charstrings.Run(Type1Charstrings.Decrypt(s.Span, lenIV), Subr, Seac);
            return new GlyphOutline(r.Path, r.Wx, r.Wy);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Type 42 (TrueType) font, also used for CIDFontType 2.</summary>
public sealed class Type42Font : FontBase, IEmbeddableFont
{
    private static readonly ConditionalWeakTable<ArrayBuffer, TrueTypeData> TtCache = new();
    private readonly TrueTypeData _tt;
    private readonly bool _cid;
    private readonly Dictionary<int, GlyphOutline> _cache = new();

    public override bool IsCidFont => _cid;

    public Type42Font(PSDict dict, bool cid)
    {
        _cid = cid;
        var sfnts = dict.Get("sfnts") as PSArray ?? throw new PSError("invalidfont", "Type 42 font without sfnts");
        _tt = TtCache.GetValue(sfnts.Buf, _ => new TrueTypeData(new SegmentedBytes(sfnts.Items.ToArray().OfType<PSString>())));
    }

    private int GidForName(PSDict dict, string name)
    {
        int gid = 0;
        if (dict.Get("CharStrings") is PSDict cs && cs.TryGet(name, out var g))
        {
            if (g is PSInt gi) gid = gi.Value;
            else if (g is PSReal gr) gid = (int)gr.Value;
        }
        else if (name != ".notdef")
        {
            // Unknown name: try the font's own cmap via the glyph list.
            int u = Encodings.ToUnicode(name);
            if (u > 0) gid = _tt.GlyphForUnicode(u);
        }
        return gid;
    }

    // ---- PDF embedding ----
    public string EmbedKey => "t42:" + RuntimeHelpers.GetHashCode(_tt);
    public string EmbedName { get; private set; } = "Font";
    public TrueTypeData? EmbedData => _tt;
    public double CharSpacePerEm => 1;
    public int EmbedGid(PSDict dict, string name, int cid, bool byCid)
    {
        // Fonts with a Metrics override would need different advances: keep them as outlines.
        if (dict.Get("Metrics") is PSDict) return -1;
        EmbedName = (dict.Get("FontName") ?? dict.Get("CIDFontName"))?.ToString() ?? "Font";
        int gid = byCid ? CidToGid(dict, cid) : GidForName(dict, name);
        return gid is >= 0 and <= 65534 ? gid : -1;
    }
    public byte[]? EmbedGlyph(PSDict dict, int gid) => GlyphData(dict, gid);

    public int EmbedUnicode(PSDict dict, string name, int cid, bool byCid)
    {
        // Windows drivers add FontInfo/GlyphNames2Unicode: glyph name (or CID) -> UTF-16BE string.
        if (dict.Get("FontInfo") is PSDict fi && fi.Get("GlyphNames2Unicode") is PSDict g2u)
        {
            PSObject? v = null;
            if (byCid) g2u.Map.TryGetValue(cid, out v);
            else g2u.TryGet(name, out v);
            if (v is PSString s && s.Length >= 2)
            {
                int cp = (s[0] << 8) | s[1];
                if (cp is >= 0xD800 and <= 0xDBFF && s.Length >= 4) cp = 0x10000 + ((cp - 0xD800) << 10) + (((s[2] << 8) | s[3]) - 0xDC00);
                return cp;
            }
            if (v is PSInt iv) return iv.Value;
        }
        return byCid ? -1 : Encodings.ToUnicode(name);
    }
    public override GlyphOutline? GlyphByName(PSDict dict, string name)
    {
        int gid = GidForName(dict, name);
        var o = Glyph(dict, gid);
        if (dict.Get("Metrics") is PSDict metrics && metrics.TryGet(name, out var mv) && o is not null)
        {
            double upem = _tt.UnitsPerEm;
            if (Interpreter.IsNum(mv)) return o with { Wx = Interpreter.Num(mv) / upem, Wy = 0 };
            if (mv is PSArray ma && ma.Length >= 2) return o with { Wx = Interpreter.Num(ma[1]) / upem, Wy = 0 };
        }
        return o;
    }

    public override GlyphOutline? GlyphByCid(PSDict dict, int cid) => Glyph(dict, CidToGid(dict, cid));

    private static int CidToGid(PSDict dict, int cid)
    {
        var map = dict.Get("CIDMap");
        int gdBytes = dict.Get("GDBytes") is { } gb ? (int)Interpreter.Num(gb) : 2;
        switch (map)
        {
            case PSInt off: return cid + off.Value;
            case PSString s:
                {
                    long p = (long)cid * gdBytes;
                    if (p + gdBytes > s.Length) return 0;
                    int g = 0;
                    for (int i = 0; i < gdBytes; i++) g = (g << 8) | s[(int)p + i];
                    return g;
                }
            case PSArray a:
                {
                    long p = (long)cid * gdBytes;
                    foreach (var item in a.Items)
                    {
                        if (item is not PSString s) continue;
                        // Strings in a CIDMap array are concatenated (odd-length strings drop their padding byte).
                        int len = s.Length - s.Length % gdBytes;
                        if (p < len)
                        {
                            if (p + gdBytes > len) return 0;
                            int g = 0;
                            for (int i = 0; i < gdBytes; i++) g = (g << 8) | s[(int)p + i];
                            return g;
                        }
                        p -= len;
                    }
                    return 0;
                }
            case PSDict d:
                return d.Map.TryGetValue(cid, out var v) && Interpreter.IsNum(v) ? (int)Interpreter.Num(v) : 0;
            default:
                return cid;
        }
    }

    private byte[]? GlyphData(PSDict dict, int gid)
    {
        var gd = dict.Get("GlyphDirectory");
        int metricsCount = dict.Get("MetricsCount") is { } mc ? (int)Interpreter.Num(mc) : 0;
        PSString? s = null;
        if (gd is PSDict gdd) { if (gdd.Map.TryGetValue(gid, out var v)) s = v as PSString; else return Array.Empty<byte>(); }
        else if (gd is PSArray gda) { if (gid >= 0 && gid < gda.Length) s = gda[gid] as PSString; else return Array.Empty<byte>(); }
        else return _tt.GlyphBytes(gid);
        if (s is null) return Array.Empty<byte>();
        var bytes = s.ToArray();
        int skip = metricsCount * 2;
        return skip > 0 && bytes.Length >= skip ? bytes[skip..] : bytes;
    }

    private GlyphOutline? Glyph(PSDict dict, int gid)
    {
        if (_cache.TryGetValue(gid, out var cached)) return cached;
        var data = GlyphData(dict, gid);
        if (data is null) return null;
        var path = TrueTypeData.ParseGlyph(data, c => GlyphData(dict, c));
        float s = 1f / _tt.UnitsPerEm;
        path.Transform(SKMatrix.CreateScale(s, s));
        double wx = (double)_tt.Advance(gid) / _tt.UnitsPerEm;
        var g = new GlyphOutline(path, wx, 0);
        // Only cache glyphs that exist (incrementally downloaded glyphs may arrive later).
        if (data.Length > 0 || gid == 0) _cache[gid] = g;
        return g;
    }
}

/// <summary>Resident font substitute (Helvetica → Arial, ...), using the server's installed fonts.</summary>
public sealed class SystemFont : FontBase, IEmbeddableFont
{
    private static readonly ConcurrentDictionary<string, SystemFont> Cache = new();
    private readonly SKFont _font;
    private readonly bool _symbol;
    private readonly ConcurrentDictionary<string, GlyphOutline?> _glyphs = new();
    public string Key { get; }

    private SystemFont(string key, SKTypeface tf, bool symbol)
    {
        Key = key;
        _font = new SKFont(tf, 1000) { Subpixel = true, LinearMetrics = true, Hinting = SKFontHinting.None };
        _symbol = symbol;
    }

    public static SystemFont Get(string key) => Cache.GetOrAdd(key, k =>
    {
        var parts = k.Split('|');
        string family = parts[0];
        bool bold = parts.Length > 1 && parts[1] == "1", italic = parts.Length > 2 && parts[2] == "1";
        var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
            italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        var tf = SKTypeface.FromFamilyName(family, style) ?? SKTypeface.Default;
        return new SystemFont(k, tf, family.Equals("Symbol", StringComparison.OrdinalIgnoreCase));
    });

    /// <summary>Maps a PostScript font name to "family|bold|italic".</summary>
    public static string Resolve(string psName)
    {
        string n = psName.Trim('/');
        string lower = n.ToLowerInvariant();
        bool bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("demi");
        bool italic = lower.Contains("italic") || lower.Contains("oblique");
        string baseName = n.Split('-', ',')[0];
        string family = baseName.ToLowerInvariant() switch
        {
            "helvetica" when lower.Contains("narrow") || lower.Contains("condensed") => "Arial Narrow",
            "helvetica" or "arialmt" or "arial" or "helveticaneue" => "Arial",
            "times" or "timesnewroman" or "timesnewromanps" or "timesnewromanpsmt" or "timesroman" => "Times New Roman",
            "courier" or "couriernew" or "couriernewps" or "couriernewpsmt" => "Courier New",
            "symbol" or "symbolmt" => "Symbol",
            "zapfdingbats" => "Segoe UI Symbol",
            "avantgarde" or "itcavantgarde" => "Century Gothic",
            "bookman" or "itcbookman" => "Bookman Old Style",
            "newcenturyschlbk" => "Century Schoolbook",
            "palatino" => "Palatino Linotype",
            "zapfchancery" => "Monotype Corsiva",
            _ => SplitCamel(StripSuffix(baseName)),
        };
        // Verify the family exists, else fall back to Arial.
        using var probe = SKFontManager.Default.MatchFamily(family);
        if (probe is null)
        {
            using var probe2 = SKFontManager.Default.MatchFamily(StripSuffix(baseName));
            family = probe2 is null ? "Arial" : StripSuffix(baseName);
        }
        return $"{family}|{(bold ? 1 : 0)}|{(italic ? 1 : 0)}";
    }

    private static string StripSuffix(string s)
    {
        foreach (var suf in new[] { "PSMT", "MT", "PS" })
            if (s.EndsWith(suf, StringComparison.Ordinal) && s.Length > suf.Length + 2) return s[..^suf.Length];
        return s;
    }

    private static string SplitCamel(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public override GlyphOutline? GlyphByName(PSDict dict, string name) => _glyphs.GetOrAdd(name, Build);

    // ---- PDF embedding (subset of the installed TrueType font) ----
    private TrueTypeData? _ttData;
    private bool _ttLoaded;
    public string EmbedKey => "sys:" + Key;
    public string EmbedName => _font.Typeface.FamilyName.Replace(" ", "") + (Key.EndsWith("|1|0") ? "-Bold" : Key.EndsWith("|0|1") ? "-Italic" : Key.EndsWith("|1|1") ? "-BoldItalic" : "");
    public double CharSpacePerEm => 1000;
    public TrueTypeData? EmbedData
    {
        get
        {
            lock (_font)
            {
                if (_ttLoaded) return _ttData;
                _ttLoaded = true;
                try
                {
                    using var stream = _font.Typeface.OpenStream(out int ttcIndex);
                    if (stream is null) return null;
                    var ms = new MemoryStream();
                    var buf = new byte[65536];
                    int n;
                    while ((n = stream.Read(buf, buf.Length)) > 0) ms.Write(buf, 0, n);
                    var tt = new TrueTypeData(new SegmentedBytes(ms.ToArray()), ttcIndex);
                    _ttData = tt.HasTable("glyf") && tt.HasTable("loca") ? tt : null; // CFF-based fonts stay outlines
                }
                catch (Exception) { _ttData = null; }
                return _ttData;
            }
        }
    }
    public int EmbedGid(PSDict dict, string name, int cid, bool byCid)
    {
        if (byCid) return -1;
        int gid = GlyphId(name);
        return gid == 0 ? -1 : gid;
    }
    public byte[]? EmbedGlyph(PSDict dict, int gid) => EmbedData?.GlyphBytes(gid);

    public int EmbedUnicode(PSDict dict, string name, int cid, bool byCid)
    {
        if (byCid) return -1;
        if (_symbol)
        {
            // Symbol font: report the real character (Greek letters etc.), not the private-use code.
            int u = Encodings.ToUnicode(name);
            return u;
        }
        return Encodings.ToUnicode(name);
    }

    private ushort GlyphId(string name)
    {
        int cp = -1;
        if (_symbol)
        {
            int code = Encodings.SymbolCode(name);
            if (code >= 0) cp = 0xF000 + code;
        }
        if (cp < 0) cp = Encodings.ToUnicode(name);
        if (cp < 0) return 0;
        lock (_font)
        {
            ushort gid = _font.GetGlyph(cp);
            if (gid == 0 && _symbol && cp >= 0xF000) gid = _font.GetGlyph(cp - 0xF000);
            return gid;
        }
    }

    private GlyphOutline? Build(string name)
    {
        ushort gid = GlyphId(name);
        if (gid == 0) return null;
        lock (_font)
        {
            var path = _font.GetGlyphPath(gid) ?? new SKPath();
            path.Transform(SKMatrix.CreateScale(1, -1));
            float w = _font.GetGlyphWidths(new[] { gid })[0];
            return new GlyphOutline(path, w, 0);
        }
    }

    /// <summary>Builds the font dictionary for a substituted resident font.</summary>
    public static PSDict MakeDict(string psName)
    {
        var key = Resolve(psName);
        var d = new PSDict(16, -1);
        d.PutRaw("FontName", PSName.Lit(psName));
        d.PutRaw("FontType", PSInt.Get(1));
        d.PutRaw("PaintType", PSInt.Get(0));
        d.PutRaw("FontMatrix", new PSArray(new PSObject[] { new PSReal(0.001), PSInt.Get(0), PSInt.Get(0), new PSReal(0.001), PSInt.Get(0), PSInt.Get(0) }, -1));
        d.PutRaw("FontBBox", new PSArray(new PSObject[] { PSInt.Get(-200), PSInt.Get(-250), PSInt.Get(1200), PSInt.Get(1000) }, -1));
        bool symbol = key.StartsWith("Symbol|", StringComparison.Ordinal);
        d.PutRaw("Encoding", symbol
            ? new PSArray(Encodings.Symbol.Select(n => (PSObject)PSName.Lit(n)).ToArray(), -1)
            : Encodings.StandardArray());
        d.PutRaw("CharStrings", SharedCharStrings.Value);
        var info = new PSDict(4, -1);
        info.PutRaw("FamilyName", PSString.FromText(key.Split('|')[0], -1));
        d.PutRaw("FontInfo", info);
        d.PutRaw("PTI_SystemFont", PSString.FromText(key, -1));
        return d;
    }

    private static readonly Lazy<PSDict> SharedCharStrings = new(() =>
    {
        var names = Encodings.Standard.Concat(Encodings.IsoLatin1).Concat(Encodings.Symbol)
            .Concat(new[] { "Euro", "trademark", "Scaron", "scaron", "Zcaron", "zcaron", "Ydieresis", "OE", "oe", "florin", "bullet", "ellipsis" })
            .Distinct();
        var d = new PSDict(512, -1);
        foreach (var n in names) d.PutRaw(n, new PSString(new byte[] { 0 }, -1));
        d.Access = Access.ReadOnly;
        return d;
    });
}

/// <summary>A CMap: maps byte sequences to CIDs (and to a descendant font index).</summary>
public sealed class CMapData
{
    public readonly List<(byte[] Lo, byte[] Hi)> CodeSpace = new();
    public readonly List<(uint Lo, uint Hi, int Len, int Cid, int Font)> Ranges = new();
    public readonly Dictionary<(uint Code, int Len), (int Cid, int Font)> Chars = new();
    public int CurrentFont;
    public int WMode;

    public static CMapData Identity()
    {
        var c = new CMapData();
        c.CodeSpace.Add((new byte[] { 0, 0 }, new byte[] { 0xFF, 0xFF }));
        c.Ranges.Add((0, 0xFFFF, 2, 0, 0));
        return c;
    }

    public void CopyFrom(CMapData other)
    {
        CodeSpace.AddRange(other.CodeSpace);
        Ranges.AddRange(other.Ranges);
        foreach (var kv in other.Chars) Chars[kv.Key] = kv.Value;
    }

    /// <summary>Decodes one code at <paramref name="pos"/>; returns the number of bytes consumed.</summary>
    public int Decode(ReadOnlySpan<byte> s, int pos, out int cid, out int font)
    {
        cid = 0;
        font = 0;
        int len = CodeLength(s, pos);
        if (pos + len > s.Length) len = s.Length - pos;
        uint code = 0;
        for (int i = 0; i < len; i++) code = (code << 8) | s[pos + i];
        if (Chars.TryGetValue((code, len), out var cf)) { cid = cf.Cid; font = cf.Font; return len; }
        for (int i = Ranges.Count - 1; i >= 0; i--)
        {
            var r = Ranges[i];
            if (r.Len == len && code >= r.Lo && code <= r.Hi) { cid = r.Cid + (int)(code - r.Lo); font = r.Font; return len; }
        }
        return len; // unmapped → CID 0 (.notdef)
    }

    private int CodeLength(ReadOnlySpan<byte> s, int pos)
    {
        if (CodeSpace.Count == 0) return 2;
        for (int len = 1; len <= 4; len++)
        {
            if (pos + len > s.Length) break;
            foreach (var (lo, hi) in CodeSpace)
            {
                if (lo.Length != len) continue;
                bool inRange = true;
                for (int i = 0; i < len; i++)
                    if (s[pos + i] < lo[i] || s[pos + i] > hi[i]) { inRange = false; break; }
                if (inRange) return len;
            }
        }
        // Not in any codespace: use the shortest codespace length.
        return CodeSpace.Min(c => c.Lo.Length);
    }
}

/// <summary>Wraps CMapData so it can live inside a PostScript dictionary.</summary>
public sealed class PSCMapObj : PSObject
{
    public readonly CMapData Data;
    public PSCMapObj(CMapData d) => Data = d;
    public override string TypeName => "nulltype";
}
