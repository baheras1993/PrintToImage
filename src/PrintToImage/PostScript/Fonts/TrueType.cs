using SkiaSharp;

namespace PrintToImage.PostScript.Fonts;

/// <summary>A read-only byte view over several string segments (the Type 42 sfnts array), read live.</summary>
public sealed class SegmentedBytes
{
    private readonly (StringBuffer Buf, int Offset, int Length)[] _segs;
    private readonly long[] _starts;
    public long Length { get; }

    public SegmentedBytes(IEnumerable<PSString> strings)
    {
        var list = new List<(StringBuffer, int, int)>();
        foreach (var s in strings)
        {
            // Type 42: a string with odd length carries one padding byte at the end.
            int len = s.Length % 2 == 1 ? s.Length - 1 : s.Length;
            list.Add((s.Buf, s.Offset, len));
        }
        _segs = list.ToArray();
        _starts = new long[_segs.Length];
        long pos = 0;
        for (int i = 0; i < _segs.Length; i++) { _starts[i] = pos; pos += _segs[i].Length; }
        Length = pos;
    }

    public SegmentedBytes(byte[] data)
    {
        _segs = new[] { (new StringBuffer(data, -1), 0, data.Length) };
        _starts = new long[] { 0 };
        Length = data.Length;
    }

    public byte this[long pos]
    {
        get
        {
            if (pos < 0 || pos >= Length) return 0;
            int i = Array.BinarySearch(_starts, pos);
            if (i < 0) i = ~i - 1;
            while (i < _segs.Length - 1 && pos >= _starts[i] + _segs[i].Length) i++;
            var seg = _segs[i];
            return seg.Buf.Data[seg.Offset + (int)(pos - _starts[i])];
        }
    }

    public byte[] Slice(long pos, int count)
    {
        var r = new byte[Math.Max(0, Math.Min(count, (int)Math.Max(0, Length - pos)))];
        for (int i = 0; i < r.Length; i++) r[i] = this[pos + i];
        return r;
    }

    public int U16(long p) => (this[p] << 8) | this[p + 1];
    public short S16(long p) => (short)U16(p);
    public uint U32(long p) => (uint)((this[p] << 24) | (this[p + 1] << 16) | (this[p + 2] << 8) | this[p + 3]);
}

/// <summary>Minimal TrueType reader: table directory, head, hhea/hmtx, loca/glyf, cmap.</summary>
public sealed class TrueTypeData
{
    private readonly SegmentedBytes _d;
    private readonly Dictionary<string, (long Offset, long Length)> _tables = new();
    public int UnitsPerEm { get; } = 1000;
    public int IndexToLocFormat { get; }
    public int NumHMetrics { get; }
    public int NumGlyphs { get; }

    public TrueTypeData(SegmentedBytes d, int ttcIndex = 0)
    {
        _d = d;
        long start = 0;
        if (d.U32(0) == 0x74746366) // 'ttcf' collection
        {
            int n = (int)d.U32(8);
            start = d.U32(12 + 4 * Math.Clamp(ttcIndex, 0, Math.Max(0, n - 1)));
        }
        int numTables = d.U16(start + 4);
        for (int i = 0; i < numTables && i < 200; i++)
        {
            long rec = start + 12 + i * 16;
            var tag = new string(new[] { (char)d[rec], (char)d[rec + 1], (char)d[rec + 2], (char)d[rec + 3] });
            _tables[tag] = (d.U32(rec + 8), d.U32(rec + 12));
        }
        if (_tables.TryGetValue("head", out var head))
        {
            int upem = d.U16(head.Offset + 18);
            if (upem is >= 16 and <= 16384) UnitsPerEm = upem;
            IndexToLocFormat = d.S16(head.Offset + 50);
        }
        if (_tables.TryGetValue("hhea", out var hhea)) NumHMetrics = d.U16(hhea.Offset + 34);
        if (_tables.TryGetValue("maxp", out var maxp)) NumGlyphs = d.U16(maxp.Offset + 4);
    }

    public bool HasTable(string tag) => _tables.ContainsKey(tag);

    /// <summary>Raw bytes of a table, or null.</summary>
    public byte[]? Table(string tag) =>
        _tables.TryGetValue(tag, out var t) && t.Length > 0 && t.Length < 64 * 1024 * 1024 ? _d.Slice(t.Offset, (int)t.Length) : null;

    /// <summary>Raw glyph description from loca/glyf, or null.</summary>
    public byte[]? GlyphBytes(int gid)
    {
        if (!_tables.TryGetValue("loca", out var loca) || !_tables.TryGetValue("glyf", out var glyf)) return null;
        long off0, off1;
        if (IndexToLocFormat == 0)
        {
            if ((gid + 1) * 2 + 2 > loca.Length) return null;
            off0 = _d.U16(loca.Offset + gid * 2) * 2L;
            off1 = _d.U16(loca.Offset + gid * 2 + 2) * 2L;
        }
        else
        {
            if ((gid + 1) * 4 + 4 > loca.Length) return null;
            off0 = _d.U32(loca.Offset + gid * 4);
            off1 = _d.U32(loca.Offset + gid * 4 + 4);
        }
        if (off1 <= off0) return Array.Empty<byte>();
        return _d.Slice(glyf.Offset + off0, (int)(off1 - off0));
    }

    /// <summary>Advance width in font units.</summary>
    public int Advance(int gid)
    {
        if (!_tables.TryGetValue("hmtx", out var hmtx) || NumHMetrics == 0) return UnitsPerEm / 2;
        int i = Math.Min(gid, NumHMetrics - 1);
        return _d.U16(hmtx.Offset + i * 4);
    }

    /// <summary>Left side bearing in font units.</summary>
    public int Lsb(int gid)
    {
        if (!_tables.TryGetValue("hmtx", out var hmtx) || NumHMetrics == 0) return 0;
        long p = gid < NumHMetrics ? hmtx.Offset + gid * 4L + 2 : hmtx.Offset + NumHMetrics * 4L + (gid - NumHMetrics) * 2L;
        return p + 2 <= hmtx.Offset + hmtx.Length ? _d.S16(p) : 0;
    }

    /// <summary>Glyph ids referenced by a composite glyph description.</summary>
    public static IEnumerable<int> Components(byte[]? g)
    {
        if (g is null || g.Length < 10 || (short)((g[0] << 8) | g[1]) >= 0) yield break;
        int p = 10;
        while (p + 4 <= g.Length)
        {
            int flags = (g[p] << 8) | g[p + 1];
            yield return (g[p + 2] << 8) | g[p + 3];
            p += 4 + ((flags & 1) != 0 ? 4 : 2);
            if ((flags & 8) != 0) p += 2; else if ((flags & 0x40) != 0) p += 4; else if ((flags & 0x80) != 0) p += 8;
            if ((flags & 0x20) == 0) yield break;
        }
    }

    /// <summary>A copy of a composite glyph with its component ids replaced through <paramref name="map"/>.</summary>
    public static byte[] RemapComponents(byte[] g, Func<int, int> map)
    {
        var r = (byte[])g.Clone();
        if (r.Length < 10 || (short)((r[0] << 8) | r[1]) >= 0) return r;
        int p = 10;
        while (p + 4 <= r.Length)
        {
            int flags = (r[p] << 8) | r[p + 1];
            int ng = map((r[p + 2] << 8) | r[p + 3]);
            r[p + 2] = (byte)(ng >> 8);
            r[p + 3] = (byte)ng;
            p += 4 + ((flags & 1) != 0 ? 4 : 2);
            if ((flags & 8) != 0) p += 2; else if ((flags & 0x40) != 0) p += 4; else if ((flags & 0x80) != 0) p += 8;
            if ((flags & 0x20) == 0) break;
        }
        return r;
    }

    /// <summary>Glyph id for a Unicode code point via the (3,1) or (3,0) cmap, or 0.</summary>
    public int GlyphForUnicode(int cp)
    {
        if (!_tables.TryGetValue("cmap", out var cmap)) return 0;
        int n = _d.U16(cmap.Offset + 2);
        for (int i = 0; i < n; i++)
        {
            long rec = cmap.Offset + 4 + i * 8;
            int pid = _d.U16(rec), eid = _d.U16(rec + 2);
            long sub = cmap.Offset + _d.U32(rec + 4);
            if (pid != 3 || _d.U16(sub) != 4) continue;
            int code = eid == 0 && cp < 0x100 ? 0xF000 + cp : cp;
            int g = Format4Lookup(sub, code);
            if (g != 0) return g;
        }
        return 0;
    }

    private int Format4Lookup(long sub, int code)
    {
        int segX2 = _d.U16(sub + 6);
        long ends = sub + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
        for (int i = 0; i < segX2 / 2; i++)
        {
            int end = _d.U16(ends + i * 2);
            if (code > end) continue;
            int start = _d.U16(starts + i * 2);
            if (code < start) return 0;
            int delta = _d.S16(deltas + i * 2);
            int ro = _d.U16(ranges + i * 2);
            if (ro == 0) return (code + delta) & 0xFFFF;
            long p = ranges + i * 2 + ro + (code - start) * 2;
            int g = _d.U16(p);
            return g == 0 ? 0 : (g + delta) & 0xFFFF;
        }
        return 0;
    }

    /// <summary>
    /// Builds the outline of a glyph description (font units, y up). Composite glyphs fetch components through <paramref name="component"/>.
    /// </summary>
    public static SKPath ParseGlyph(byte[]? g, Func<int, byte[]?> component, int depth = 0)
    {
        var path = new SKPath { FillType = SKPathFillType.Winding };
        if (g is null || g.Length < 10 || depth > 8) return path;
        int nContours = (short)((g[0] << 8) | g[1]);
        if (nContours >= 0) SimpleGlyph(g, nContours, path);
        else CompositeGlyph(g, component, path, depth);
        return path;
    }

    private static void SimpleGlyph(byte[] g, int nContours, SKPath path)
    {
        int p = 10;
        if (nContours == 0) return;
        var endPts = new int[nContours];
        for (int i = 0; i < nContours; i++) { endPts[i] = (g[p] << 8) | g[p + 1]; p += 2; }
        int nPts = endPts[^1] + 1;
        if (nPts <= 0 || nPts > 65535) return;
        int insLen = (g[p] << 8) | g[p + 1];
        p += 2 + insLen;
        var flags = new byte[nPts];
        for (int i = 0; i < nPts;)
        {
            if (p >= g.Length) return;
            byte f = g[p++];
            flags[i++] = f;
            if ((f & 8) != 0 && p < g.Length)
            {
                int rep = g[p++];
                for (int r = 0; r < rep && i < nPts; r++) flags[i++] = f;
            }
        }
        var xs = new int[nPts];
        var ys = new int[nPts];
        int v = 0;
        for (int i = 0; i < nPts; i++)
        {
            byte f = flags[i];
            if ((f & 2) != 0) { if (p >= g.Length) return; int d = g[p++]; v += (f & 16) != 0 ? d : -d; }
            else if ((f & 16) == 0) { if (p + 1 >= g.Length) return; v += (short)((g[p] << 8) | g[p + 1]); p += 2; }
            xs[i] = v;
        }
        v = 0;
        for (int i = 0; i < nPts; i++)
        {
            byte f = flags[i];
            if ((f & 4) != 0) { if (p >= g.Length) return; int d = g[p++]; v += (f & 32) != 0 ? d : -d; }
            else if ((f & 32) == 0) { if (p + 1 >= g.Length) return; v += (short)((g[p] << 8) | g[p + 1]); p += 2; }
            ys[i] = v;
        }

        int startIdx = 0;
        for (int c = 0; c < nContours; c++)
        {
            int end = endPts[c];
            if (end < startIdx || end >= nPts) break;
            AddContour(path, xs, ys, flags, startIdx, end);
            startIdx = end + 1;
        }
    }

    private static void AddContour(SKPath path, int[] xs, int[] ys, byte[] flags, int s, int e)
    {
        int n = e - s + 1;
        if (n < 2) return;
        bool OnCurve(int i) => (flags[s + ((i % n) + n) % n] & 1) != 0;
        SKPoint Pt(int i) { int k = s + ((i % n) + n) % n; return new SKPoint(xs[k], ys[k]); }

        // Find a starting on-curve point (or synthesize the midpoint of two off-curve points).
        int first = -1;
        for (int i = 0; i < n; i++) if (OnCurve(i)) { first = i; break; }
        SKPoint start;
        if (first < 0)
        {
            var a = Pt(0); var b = Pt(1);
            start = new SKPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            first = 0;
            path.MoveTo(start);
            for (int i = 1; i <= n; i++)
            {
                var c = Pt(i);
                var nx = Pt(i + 1);
                var mid = new SKPoint((c.X + nx.X) / 2, (c.Y + nx.Y) / 2);
                path.QuadTo(c, i == n ? start : mid);
            }
            path.Close();
            return;
        }
        start = Pt(first);
        path.MoveTo(start);
        SKPoint? ctrl = null;
        for (int k = 1; k <= n; k++)
        {
            int i = first + k;
            var pt = Pt(i);
            if (OnCurve(i))
            {
                if (ctrl is { } c) path.QuadTo(c, pt); else path.LineTo(pt);
                ctrl = null;
            }
            else
            {
                if (ctrl is { } c)
                {
                    var mid = new SKPoint((c.X + pt.X) / 2, (c.Y + pt.Y) / 2);
                    path.QuadTo(c, mid);
                }
                ctrl = pt;
            }
        }
        if (ctrl is { } last) path.QuadTo(last, start);
        path.Close();
    }

    private static void CompositeGlyph(byte[] g, Func<int, byte[]?> component, SKPath path, int depth)
    {
        int p = 10;
        while (p + 4 <= g.Length)
        {
            int flags = (g[p] << 8) | g[p + 1];
            int gid = (g[p + 2] << 8) | g[p + 3];
            p += 4;
            double dx, dy;
            if ((flags & 1) != 0)
            {
                if (p + 4 > g.Length) break;
                dx = (short)((g[p] << 8) | g[p + 1]); dy = (short)((g[p + 2] << 8) | g[p + 3]); p += 4;
            }
            else
            {
                if (p + 2 > g.Length) break;
                dx = (sbyte)g[p]; dy = (sbyte)g[p + 1]; p += 2;
            }
            if ((flags & 2) == 0) { dx = 0; dy = 0; } // point matching: not supported, place at origin
            double a = 1, b = 0, c = 0, d = 1;
            static double F2Dot14(byte[] g, int p) => (short)((g[p] << 8) | g[p + 1]) / 16384.0;
            if ((flags & 8) != 0) { if (p + 2 > g.Length) break; a = d = F2Dot14(g, p); p += 2; }
            else if ((flags & 0x40) != 0) { if (p + 4 > g.Length) break; a = F2Dot14(g, p); d = F2Dot14(g, p + 2); p += 4; }
            else if ((flags & 0x80) != 0) { if (p + 8 > g.Length) break; a = F2Dot14(g, p); b = F2Dot14(g, p + 2); c = F2Dot14(g, p + 4); d = F2Dot14(g, p + 6); p += 8; }

            using var sub = ParseGlyph(component(gid), component, depth + 1);
            var m = new SKMatrix((float)a, (float)c, (float)dx, (float)b, (float)d, (float)dy, 0, 0, 1);
            path.AddPath(sub, in m);
            if ((flags & 0x20) == 0) break; // MORE_COMPONENTS
        }
    }
}
