using PrintToImage.PostScript.Fonts;
using PrintToImage.PostScript.Graphics;
using SkiaSharp;

namespace PrintToImage.PostScript;

public sealed partial class Interpreter
{
    private (double Wx, double Wy)? _type3Width;
    private PSDict? _currentShowFont;
    /// <summary>The CID of the character a cshow procedure is handling (see DecodeText).</summary>
    private int? _cshowCid;
    // Each substitution is logged once per process, not for every job.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> SubstitutionsLogged = new();

    private struct GlyphRef
    {
        public PSDict Font;
        public Matrix FM;
        public int Code;
        public int Cid;
        public bool ByCid;
        public string Name;
        public int CharCode;
    }

    private enum XMode { None, X, Y, XY }

    private sealed class ShowOpts
    {
        public bool Paint = true;
        public bool CharPath;
        public bool Measure;
        public double Ax, Ay, Cx, Cy;
        public int CChar = -1;
        public List<double>? Nums;
        public XMode Mode;
        public PSObject? KProc, CProc;
    }

    private void RegisterFontOperators()
    {
        Def("definefont", ip =>
        {
            var d = ip.PopDict();
            var key = ip.Pop();
            ip.Push(ip.DefineFont(key, d));
        });
        Def("undefinefont", ip =>
        {
            var k = PSDict.Key(ip.Pop());
            ip.DictRemove(ip.FontDirectory, k);
            ip.GlobalFontDirectory.Map.Remove(k);
        });
        Def("findfont", ip => ip.Push(ip.FindFont(ip.Pop())));
        Def("scalefont", ip =>
        {
            double s = ip.PopNum();
            var f = ip.PopDict();
            ip.Push(ip.MakeFont(f, Matrix.Scale(s, s)));
        });
        Def("makefont", ip =>
        {
            var m = ip.PopMatrix();
            var f = ip.PopDict();
            ip.Push(ip.MakeFont(f, m));
        });
        Def("setfont", ip => ip.GS.Font = ip.PopDict());
        Def("currentfont", ip => ip.Push(ip._currentShowFont ?? ip.GS.Font ?? ip.FindFont(PSName.Lit("Courier"))));
        Def("rootfont", ip => ip.Push(ip.GS.Font ?? ip.FindFont(PSName.Lit("Courier"))));
        Def("selectfont", ip =>
        {
            var s = ip.Pop();
            var key = ip.Pop();
            var f = ip.FindFont(key);
            ip.GS.Font = s is PSArray m ? ip.MakeFont(f, ToMatrix(m)) : ip.MakeFont(f, Matrix.Scale(Num(s), Num(s)));
        });
        Def("composefont", ip =>
        {
            var arr = ip.PopArray();
            var cmapObj = ip.Pop();
            var key = ip.Pop();
            ip.Push(ip.ComposeFont(key, cmapObj, arr));
        });
        Def("findencoding", ip =>
        {
            var k = ip.Pop();
            ip.Push((k as PSName)?.Text switch
            {
                "StandardEncoding" => ip.SystemDict.Get("StandardEncoding")!,
                "ISOLatin1Encoding" => ip.SystemDict.Get("ISOLatin1Encoding")!,
                "SymbolEncoding" => new PSArray(Encodings.Symbol.Select(n => (PSObject)PSName.Lit(n)).ToArray(), -1),
                _ => ip.FindResourceInstance("Encoding", k) ?? throw new PSError("undefinedresource", k.ToString()),
            });
        });

        Def("show", ip => ip.Show(ip.PopString(), new ShowOpts()));
        Def("ashow", ip =>
        {
            var s = ip.PopString();
            double ay = ip.PopNum(), ax = ip.PopNum();
            ip.Show(s, new ShowOpts { Ax = ax, Ay = ay });
        });
        Def("widthshow", ip =>
        {
            var s = ip.PopString();
            int c = ip.PopInt();
            double cy = ip.PopNum(), cx = ip.PopNum();
            ip.Show(s, new ShowOpts { Cx = cx, Cy = cy, CChar = c });
        });
        Def("awidthshow", ip =>
        {
            var s = ip.PopString();
            double ay = ip.PopNum(), ax = ip.PopNum();
            int c = ip.PopInt();
            double cy = ip.PopNum(), cx = ip.PopNum();
            ip.Show(s, new ShowOpts { Cx = cx, Cy = cy, CChar = c, Ax = ax, Ay = ay });
        });
        Def("xshow", ip => { var n = NumbersFrom(ip.Pop()); ip.Show(ip.PopString(), new ShowOpts { Nums = n, Mode = XMode.X }); });
        Def("yshow", ip => { var n = NumbersFrom(ip.Pop()); ip.Show(ip.PopString(), new ShowOpts { Nums = n, Mode = XMode.Y }); });
        Def("xyshow", ip => { var n = NumbersFrom(ip.Pop()); ip.Show(ip.PopString(), new ShowOpts { Nums = n, Mode = XMode.XY }); });
        Def("kshow", ip =>
        {
            var s = ip.PopString();
            var proc = ip.PopProc();
            ip.Show(s, new ShowOpts { KProc = proc });
        });
        Def("cshow", ip =>
        {
            var s = ip.PopString();
            var proc = ip.PopProc();
            ip.Show(s, new ShowOpts { CProc = proc, Paint = false });
        });
        Def("stringwidth", ip =>
        {
            var s = ip.PopString();
            var (dx, dy) = ip.Show(s, new ShowOpts { Paint = false, Measure = true });
            if (!ip.GS.CTM.TryInvert(out var inv)) { ip.Push(0); ip.Push(0); return; }
            var (ux, uy) = inv.DTransform(dx, dy);
            ip.Push(ux);
            ip.Push(uy);
        });
        Def("charpath", ip =>
        {
            ip.PopBool();
            ip.Show(ip.PopString(), new ShowOpts { CharPath = true, Paint = false });
        });
        Def("glyphshow", ip =>
        {
            var g = ip.Pop();
            ip.GlyphShow(g);
        });
        Def("setcachedevice", ip =>
        {
            for (int i = 0; i < 4; i++) ip.PopNum();
            double wy = ip.PopNum(), wx = ip.PopNum();
            ip._type3Width = (wx, wy);
        });
        Def("setcachedevice2", ip =>
        {
            for (int i = 0; i < 8; i++) ip.PopNum();
            double wy = ip.PopNum(), wx = ip.PopNum();
            ip._type3Width = (wx, wy);
        });
        Def("setcharwidth", ip =>
        {
            double wy = ip.PopNum(), wx = ip.PopNum();
            ip._type3Width = (wx, wy);
        });
    }

    // ---------------- font management ----------------

    internal PSDict DefineFont(PSObject key, PSDict d)
    {
        if (d.Font is null || !d.Has("FID")) d.Font = BuildFontObject(d);
        d.Map[NameAtom.Get("FID")] = PSFontId.Instance;
        if (!d.Has("FontName") && key is PSName kn) d.Map[NameAtom.Get("FontName")] = kn;
        var dir = d.SaveLevel < 0 ? GlobalFontDirectory : FontDirectory;
        DictPut(dir, PSDict.Key(key), d);
        return d;
    }

    private FontBase BuildFontObject(PSDict d)
    {
        if (d.Get("PTI_SystemFont") is PSString sk) return SystemFont.Get(sk.Text);
        string fontName = d.Get("FontName")?.ToString() ?? d.Get("CIDFontName")?.ToString() ?? "Unknown";
        try
        {
            if (d.Get("CIDFontType") is { } cft)
            {
                if ((int)Num(cft) == 2) return new Type42Font(d, cid: true);
                Log($"CIDFontType {Num(cft)} ({fontName}) is not supported; its text will be missing.");
                return SystemFont.Get(SystemFont.Resolve(fontName));
            }
            int ft = d.Get("FontType") is { } t ? (int)Num(t) : 1;
            switch (ft)
            {
                case 0: return new Type0Font();
                case 3: return new Type3Font();
                case 42: return new Type42Font(d, cid: false);
                case 1:
                    {
                        bool embedded = d.Get("CharStrings") is PSDict cs && cs.Map.Values.Any(v => v is PSString { Length: > 4 });
                        var fallback = SystemFont.Get(SystemFont.Resolve(fontName));
                        return embedded ? new Type1Font(fallback) : fallback;
                    }
                default:
                    Log($"FontType {ft} ({fontName}) is drawn with a substitute font.");
                    return SystemFont.Get(SystemFont.Resolve(fontName));
            }
        }
        catch (PSError e)
        {
            Log($"Font {fontName} could not be loaded ({e.Message}); using a substitute.");
            return SystemFont.Get(SystemFont.Resolve(fontName));
        }
    }

    private FontBase FontObject(PSDict d) => d.Font ??= BuildFontObject(d);

    internal PSDict FindFont(PSObject key)
    {
        var k = PSDict.Key(key);
        if (FontDirectory.Map.TryGetValue(k, out var f) && f is PSDict fd) return fd;
        if (GlobalFontDirectory.Map.TryGetValue(k, out f) && f is PSDict gd) return gd;
        if (FindResourceInstance("Font", key) is PSDict rd) return rd;

        string name = key switch { PSName n => n.Text, PSString s => s.Text, _ => key.ToString() ?? "Courier" };
        var d = SystemFont.MakeDict(name);
        d.Font = SystemFont.Get(((PSString)d.Get("PTI_SystemFont")!).Text);
        d.Map[NameAtom.Get("FID")] = PSFontId.Instance;
        GlobalFontDirectory.Map[k] = d;
        if (SubstitutionsLogged.TryAdd(name, true))
            Log($"Font {name} is not embedded; using installed font {((SystemFont)d.Font).Key.Split('|')[0]}.");
        return d;
    }

    internal PSDict MakeFont(PSDict f, Matrix m)
    {
        var d = new PSDict(f.Count + 1, f.SaveLevel < 0 ? -1 : AllocLevel);
        foreach (var kv in f.Map) d.Map[kv.Key] = kv.Value;
        d.Font = f.Font;
        var fm = f.Get("FontMatrix") is PSArray a && a.Length == 6 ? ToMatrix(a) : Matrix.Identity;
        d.Map[NameAtom.Get("FontMatrix")] = NewMatrix(fm.Multiply(m));
        // ScaleMatrix (Level 2) accumulates the scaling applied by makefont.
        var sm = f.Get("ScaleMatrix") is PSArray sa && sa.Length == 6 ? ToMatrix(sa) : Matrix.Identity;
        d.Map[NameAtom.Get("ScaleMatrix")] = NewMatrix(sm.Multiply(m));
        return d;
    }

    /// <summary>A copy of a font dictionary with another FontMatrix (shares the font program).</summary>
    private PSDict FontWithMatrix(PSDict f, Matrix fm)
    {
        var d = new PSDict(f.Count + 1, f.SaveLevel < 0 ? -1 : AllocLevel);
        foreach (var kv in f.Map) d.Map[kv.Key] = kv.Value;
        d.Font = f.Font;
        d.Map[NameAtom.Get("FontMatrix")] = NewMatrix(fm);
        return d;
    }

    private PSDict ComposeFont(PSObject key, PSObject cmapObj, PSArray fonts)
    {
        var cmap = cmapObj as PSDict ?? FindResourceInstance("CMap", cmapObj) as PSDict ?? throw new PSError("undefinedresource", "CMap " + cmapObj);
        var fdep = new PSObject[fonts.Length];
        for (int i = 0; i < fonts.Length; i++)
        {
            var item = fonts[i];
            fdep[i] = item as PSDict
                      ?? FindResourceInstance("CIDFont", item) as PSDict
                      ?? FindFont(item);
        }
        var t0 = NewDict(12);
        t0.PutRaw("FontType", PSInt.Get(0));
        t0.PutRaw("FMapType", PSInt.Get(9));
        t0.PutRaw("FontMatrix", NewMatrix(Matrix.Identity));
        t0.PutRaw("CMap", cmap);
        t0.PutRaw("FDepVector", new PSArray(fdep, AllocLevel));
        t0.PutRaw("Encoding", new PSArray(Enumerable.Range(0, fdep.Length).Select(i => (PSObject)PSInt.Get(i)).ToArray(), AllocLevel));
        t0.PutRaw("FontName", key is PSString ks ? PSName.Lit(ks.Text) : key);
        t0.PutRaw("WMode", cmap.Get("WMode") ?? PSInt.Get(0));
        var font = DefineFont(key, t0);
        DefineResourceInstance("Font", key, font);
        return font;
    }

    // ---------------- text decoding ----------------

    private static string GlyphNameFor(PSDict font, int code)
    {
        if (font.Get("Encoding") is PSArray enc && code >= 0 && code < enc.Length && enc[code] is PSName n) return n.Text;
        return ".notdef";
    }

    private List<GlyphRef> DecodeText(PSDict root, ReadOnlySpan<byte> s)
    {
        var list = new List<GlyphRef>(s.Length);
        var rootFM = root.Get("FontMatrix") is PSArray ra && ra.Length == 6 ? ToMatrix(ra) : Matrix.Identity;
        int ftype = root.Get("FontType") is { } ft ? (int)Num(ft) : 1;
        if (root.Has("CIDFontType"))
        {
            // A CIDFont as the current font (inside cshow). The Microsoft PS Class Driver shows each character with a
            // one-byte string made from the character code, which cannot hold CIDs above 255 (Greek, Arabic, ...):
            // a one-character show inside cshow therefore draws the CID cshow is handling.
            if (_cshowCid is { } current && s.Length == 1)
            {
                list.Add(new GlyphRef { Font = root, FM = rootFM, Code = current, CharCode = current, Cid = current, ByCid = true, Name = "" });
                return list;
            }
            foreach (var b in s)
                list.Add(new GlyphRef { Font = root, FM = rootFM, Code = b, CharCode = b, Cid = b, ByCid = true, Name = "" });
            return list;
        }
        if (ftype != 0)
        {
            foreach (var b in s)
                list.Add(new GlyphRef { Font = root, FM = rootFM, Code = b, CharCode = b, Name = GlyphNameFor(root, b) });
            return list;
        }

        int fmap = root.Get("FMapType") is { } fmt ? (int)Num(fmt) : 2;
        var enc = root.Get("Encoding") as PSArray;
        var fdep = root.Get("FDepVector") as PSArray ?? throw new PSError("invalidfont", "FDepVector");

        PSDict Desc(int fontNum)
        {
            int idx = enc is not null && fontNum >= 0 && fontNum < enc.Length && IsNum(enc[fontNum]) ? (int)Num(enc[fontNum]) : fontNum;
            if (idx < 0 || idx >= fdep.Length || fdep[idx] is not PSDict d) throw new PSError("invalidfont", "bad FDepVector index");
            return d;
        }

        void AddCode(PSDict desc, int code, int charCode)
        {
            var dfm = desc.Get("FontMatrix") is PSArray da && da.Length == 6 ? ToMatrix(da) : Matrix.Identity;
            var fm = dfm.Multiply(rootFM);
            bool cidFont = desc.Has("CIDFontType");
            list.Add(new GlyphRef
            {
                Font = desc, FM = fm, Code = code, CharCode = charCode, Cid = code, ByCid = cidFont,
                Name = cidFont ? "" : GlyphNameFor(desc, code),
            });
        }

        switch (fmap)
        {
            case 9:
                {
                    var cmapDict = root.Get("CMap") as PSDict;
                    var cmap = (cmapDict?.Get(".CMapData") as PSCMapObj)?.Data ?? CMapData.Identity();
                    int pos = 0;
                    while (pos < s.Length)
                    {
                        int n = cmap.Decode(s, pos, out int cid, out int fnum);
                        if (n <= 0) n = 1;
                        int idx = fnum < fdep.Length ? fnum : 0;
                        var desc = fdep[idx] as PSDict ?? throw new PSError("invalidfont");
                        int charCode = 0;
                        for (int i = 0; i < n && pos + i < s.Length; i++) charCode = (charCode << 8) | s[pos + i];
                        AddCode(desc, cid, charCode);
                        pos += n;
                    }
                    break;
                }
            case 4:
                foreach (var b in s) AddCode(Desc(b >> 7), b & 0x7F, b);
                break;
            case 5:
                for (int i = 0; i + 1 < s.Length; i += 2)
                    AddCode(Desc((s[i] << 1) | (s[i + 1] >> 7)), s[i + 1] & 0x7F, (s[i] << 8) | s[i + 1]);
                break;
            case 3:
                {
                    int esc = root.Get("EscChar") is { } ec ? (int)Num(ec) : 255;
                    int cur = 0;
                    for (int i = 0; i < s.Length; i++)
                    {
                        if (s[i] == esc && i + 1 < s.Length) { cur = s[++i]; continue; }
                        AddCode(Desc(cur), s[i], s[i]);
                    }
                    break;
                }
            default: // 2 (8/8) and fallbacks
                for (int i = 0; i + 1 < s.Length; i += 2)
                    AddCode(Desc(s[i]), s[i + 1], (s[i] << 8) | s[i + 1]);
                break;
        }
        return list;
    }

    private GlyphOutline? Outline(GlyphRef g)
    {
        var impl = FontObject(g.Font);
        var o = g.ByCid ? impl.GlyphByCid(g.Font, g.Cid) : impl.GlyphByName(g.Font, g.Name);
        if (o is null && !g.ByCid && g.Name != ".notdef")
        {
            // Missing glyph: keep the advance of .notdef so text spacing stays plausible.
            var nd = impl.GlyphByName(g.Font, ".notdef");
            if (nd is not null) return new GlyphOutline(new SKPath(), nd.Wx, nd.Wy);
        }
        return o;
    }

    private static int FontTypeOf(PSDict d) => d.Get("FontType") is { } t ? (int)Num(t) : 1;

    // ---------------- show ----------------

    private (double Dx, double Dy) Show(PSString s, ShowOpts o)
    {
        var root = GS.Font ?? FindFont(PSName.Lit("Courier"));
        if (!o.Measure && !GS.Path.HasCurrentPoint) throw new PSError("nocurrentpoint");
        var glyphs = DecodeText(root, s.Span);
        double cx = GS.Path.HasCurrentPoint ? GS.Path.CurX : 0, cy = GS.Path.HasCurrentPoint ? GS.Path.CurY : 0;
        double startX = cx, startY = cy;
        bool paint = o.Paint && CanPaint;
        var acc = paint ? new SKPath { FillType = SKPathFillType.Winding } : null;
        int numIdx = 0;
        // PDF: glyphs are collected into runs drawn with embedded fonts.
        bool vectorText = paint && Device.VectorText && Device.Override is null && o.CProc is null;
        TextRun? run = null;

        void FlushText()
        {
            if (run is null || run.Glyphs.Count == 0) return;
            DrawTextRun(run);
            run = null;
        }

        void Flush()
        {
            FlushText();
            if (acc is null || acc.IsEmpty) return;
            using var p = MakePaint();
            var path = acc;
            DrawClipped(c => c.DrawPath(path, p));
            acc.Reset();
        }

        // The page text (a file number printed on the page): only what is really painted on the page;
        // a cshow procedure paints the character itself (its own show is counted).
        bool collectText = paint && o.CProc is null && Device.Override is null;

        for (int i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            if (collectText) Device.AddText(UnicodeOf(g));
            var ctm = GS.CTM;
            if (o.KProc is not null && i > 0)
            {
                Flush();
                GS.Path.MoveTo(cx, cy);
                Push(glyphs[i - 1].CharCode);
                Push(g.CharCode);
                ExecuteNested(o.KProc);
                if (GS.Path.HasCurrentPoint) { cx = GS.Path.CurX; cy = GS.Path.CurY; }
                ctm = GS.CTM;
            }

            var trm = g.FM.Multiply(ctm);
            double wx, wy;
            bool type3 = FontTypeOf(g.Font) == 3;
            if (type3)
            {
                Flush();
                (wx, wy) = RunType3(g, cx, cy, paint && o.CProc is null);
            }
            else
            {
                var outline = Outline(g);
                wx = outline?.Wx ?? 0;
                wy = outline?.Wy ?? 0;
                int egid = -1;
                FontEmbedder.Source? src = null;
                if (vectorText && outline is not null && !outline.Path.IsEmpty && Device.Embedder is { } emb
                    && FontObject(g.Font) is IEmbeddableFont ef
                    && (egid = ef.EmbedGid(g.Font, g.Name, g.Cid, g.ByCid)) >= 0
                    && (src = emb.Use(ef, g.Font, egid, g.Name, g.Cid, g.ByCid)) is not null)
                {
                    double k = ef.CharSpacePerEm / 1000.0;
                    var lin = new Matrix(k * trm.A, k * trm.B, -k * trm.C, -k * trm.D, 0, 0);
                    if (run is null || run.Source != src || run.Linear != lin)
                    {
                        FlushText();
                        if (acc is not null && !acc.IsEmpty) Flush();
                        run = new TextRun(src, lin, k, cx, cy);
                    }
                    run.Glyphs.Add((ushort)egid);
                    run.Origins.Add((cx, cy));
                    run.Outlines.Add(outline.Path);
                }
                else if (outline is not null && !outline.Path.IsEmpty && (acc is not null || o.CharPath) && o.CProc is null)
                {
                    var m = new SKMatrix((float)trm.A, (float)trm.C, (float)cx, (float)trm.B, (float)trm.D, (float)cy, 0, 0, 1);
                    if (o.CharPath)
                    {
                        using var tp = new SKPath();
                        outline.Path.Transform(m, tp);
                        GS.Path.Append(PSPath.FromSKPath(tp));
                    }
                    else acc!.AddPath(outline.Path, in m);
                }
            }

            if (o.CProc is not null)
            {
                // cshow: the procedure positions and paints the character itself.
                var (ux, uy) = g.FM.DTransform(wx, wy);
                var prevFont = _currentShowFont;
                // PLRM: during the procedure the current font is the base (descendant) font selected for this
                // character, scaled like the composite font. Drivers then "show" a one-character string with it.
                var baseFont = g.Font == root ? root : FontWithMatrix(g.Font, g.FM);
                _currentShowFont = baseFont;
                GS.Font = baseFont;
                var prevCid = _cshowCid;
                _cshowCid = g.ByCid ? g.Cid : null;
                try
                {
                    Push(g.CharCode);
                    Push(ux);
                    Push(uy);
                    ExecuteNested(o.CProc);
                }
                finally
                {
                    _currentShowFont = prevFont;
                    GS.Font = root;
                    _cshowCid = prevCid;
                }
                if (GS.Path.HasCurrentPoint) { cx = GS.Path.CurX; cy = GS.Path.CurY; }
                continue;
            }

            double dx, dy;
            if (o.Nums is not null)
            {
                double ux = 0, uy = 0;
                switch (o.Mode)
                {
                    case XMode.X: ux = numIdx < o.Nums.Count ? o.Nums[numIdx] : 0; numIdx++; break;
                    case XMode.Y: uy = numIdx < o.Nums.Count ? o.Nums[numIdx] : 0; numIdx++; break;
                    default:
                        ux = numIdx < o.Nums.Count ? o.Nums[numIdx] : 0;
                        uy = numIdx + 1 < o.Nums.Count ? o.Nums[numIdx + 1] : 0;
                        numIdx += 2;
                        break;
                }
                (dx, dy) = ctm.DTransform(ux, uy);
            }
            else
            {
                (dx, dy) = trm.DTransform(wx, wy);
                if (o.Ax != 0 || o.Ay != 0)
                {
                    var (ax, ay) = ctm.DTransform(o.Ax, o.Ay);
                    dx += ax; dy += ay;
                }
                if (o.CChar >= 0 && g.CharCode == o.CChar)
                {
                    var (wcx, wcy) = ctm.DTransform(o.Cx, o.Cy);
                    dx += wcx; dy += wcy;
                }
            }
            cx += dx;
            cy += dy;
        }

        Flush();
        acc?.Dispose();
        if (collectText) Device.TextBreak();
        if (!o.Measure) GS.Path.MoveTo(cx, cy);
        return (cx - startX, cy - startY);
    }

    /// <summary>The Unicode character a glyph shows (as for searchable PDF text), or -1.</summary>
    private int UnicodeOf(GlyphRef g)
    {
        try
        {
            if (FontObject(g.Font) is IEmbeddableFont ef) return ef.EmbedUnicode(g.Font, g.Name ?? "", g.Cid, g.ByCid);
            return string.IsNullOrEmpty(g.Name) ? -1 : Encodings.ToUnicode(g.Name);
        }
        catch (PSError) { return -1; }
    }

    private sealed class TextRun
    {
        public readonly FontEmbedder.Source Source;
        public readonly Matrix Linear;           // glyph space (font size 1000, y down) -> device, without translation
        public readonly double K;                // character-space units per glyph-space unit
        public readonly double X0, Y0;           // device origin of the first glyph
        public readonly List<ushort> Glyphs = new();
        public readonly List<(double X, double Y)> Origins = new();
        public readonly List<SKPath> Outlines = new(); // character space, for the fallback
        public TextRun(FontEmbedder.Source src, Matrix linear, double k, double x0, double y0)
        { Source = src; Linear = linear; K = k; X0 = x0; Y0 = y0; }
    }

    /// <summary>Records a glyph run as a drawable that is rendered with the subset font when the PDF is written.</summary>
    private void DrawTextRun(TextRun run)
    {
        if (!run.Linear.TryInvert(out var inv) || Device.Embedder is not { } emb) return;
        var positions = new SKPoint[run.Glyphs.Count];
        var fallback = new SKPath[run.Glyphs.Count];
        var toGlyph = SKMatrix.CreateScale((float)(1 / run.K), (float)(-1 / run.K));
        for (int i = 0; i < positions.Length; i++)
        {
            var (px, py) = inv.DTransform(run.Origins[i].X - run.X0, run.Origins[i].Y - run.Y0);
            positions[i] = new SKPoint((float)px, (float)py);
            fallback[i] = new SKPath();
            run.Outlines[i].Transform(toGlyph, fallback[i]);
        }
        var drawable = new TextDrawable(run.Source, run.Glyphs.ToArray(), positions, fallback, MakePaint());
        emb.Keep(drawable);
        var m = (run.Linear with { Tx = run.X0, Ty = run.Y0 }).ToSK();
        DrawClipped(c =>
        {
            c.Concat(in m);
            c.DrawDrawable(drawable, 0, 0);
        });
    }
    /// <summary>Runs BuildGlyph/BuildChar of a Type 3 font at device point (cx, cy). Returns the width in character space.</summary>
    private (double Wx, double Wy) RunType3(GlyphRef g, double cx, double cy, bool paint)
    {
        var font = g.Font;
        var prevFont = _currentShowFont;
        var prevWidth = _type3Width;
        _type3Width = null;
        (double, double) result;
        GSave();
        try
        {
            var m = g.FM.Multiply(GS.CTM);
            GS.CTM = m with { Tx = cx, Ty = cy };
            GS.Path = new PSPath();
            if (!paint) GS.NullDevice = true;
            _currentShowFont = font;
            if (font.Get("BuildGlyph") is { } bg)
            {
                Push(font);
                Push(PSName.Lit(g.Name));
                ExecuteNested(bg);
            }
            else if (font.Get("BuildChar") is { } bc)
            {
                Push(font);
                Push(g.Code);
                ExecuteNested(bc);
            }
            result = _type3Width ?? (0, 0);
        }
        finally
        {
            GRestore();
            _currentShowFont = prevFont;
            _type3Width = prevWidth;
        }
        return result;
    }

    private void GlyphShow(PSObject g)
    {
        var font = GS.Font ?? FindFont(PSName.Lit("Courier"));
        if (!GS.Path.HasCurrentPoint) throw new PSError("nocurrentpoint");
        var fm = font.Get("FontMatrix") is PSArray a && a.Length == 6 ? ToMatrix(a) : Matrix.Identity;
        var target = font;
        if (FontTypeOf(font) == 0 && font.Get("FDepVector") is PSArray fdep && fdep.Length > 0 && fdep[0] is PSDict d0)
        {
            var dfm = d0.Get("FontMatrix") is PSArray da && da.Length == 6 ? ToMatrix(da) : Matrix.Identity;
            fm = dfm.Multiply(fm);
            target = d0;
        }
        var gr = new GlyphRef { Font = target, FM = fm };
        if (g is PSName n) { gr.Name = n.Text; gr.ByCid = false; }
        else { gr.Cid = (int)Num(g); gr.ByCid = true; gr.Name = ""; }
        double cx = GS.Path.CurX, cy = GS.Path.CurY;
        var trm = fm.Multiply(GS.CTM);
        double wx, wy;
        if (FontTypeOf(target) == 3) (wx, wy) = RunType3(gr, cx, cy, CanPaint);
        else
        {
            var o = Outline(gr);
            wx = o?.Wx ?? 0; wy = o?.Wy ?? 0;
            if (o is not null && !o.Path.IsEmpty && CanPaint)
            {
                var m = new SKMatrix((float)trm.A, (float)trm.C, (float)cx, (float)trm.B, (float)trm.D, (float)cy, 0, 0, 1);
                using var p = new SKPath();
                o.Path.Transform(m, p);
                using var paint = MakePaint();
                DrawClipped(c => c.DrawPath(p, paint));
            }
        }
        var (dx, dy) = trm.DTransform(wx, wy);
        GS.Path.MoveTo(cx + dx, cy + dy);
    }
}
