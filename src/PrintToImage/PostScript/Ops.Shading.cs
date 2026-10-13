using PrintToImage.PostScript.Graphics;
using SkiaSharp;

namespace PrintToImage.PostScript;

/// <summary>PostScript Level 3 functions (types 0, 2, 3).</summary>
internal sealed class PSFunction
{
    private readonly int _type;
    private readonly double[] _domain;
    private readonly double[]? _range;
    // type 2
    private readonly double[] _c0 = { 0 }, _c1 = { 1 };
    private readonly double _n = 1;
    // type 3
    private readonly PSFunction[] _subs = Array.Empty<PSFunction>();
    private readonly double[] _bounds = Array.Empty<double>(), _encode = Array.Empty<double>();
    // type 0
    private readonly int[] _size = Array.Empty<int>();
    private readonly int _bps;
    private readonly byte[] _samples = Array.Empty<byte>();
    private readonly double[]? _decode;
    private readonly int _nOut;

    public int OutputCount => _nOut;

    public PSFunction(PSDict d)
    {
        _type = d.Get("FunctionType") is { } ft ? (int)Interpreter.Num(ft) : throw new PSError("undefined", "FunctionType");
        _domain = Nums(d.Get("Domain")) ?? new double[] { 0, 1 };
        _range = Nums(d.Get("Range"));
        switch (_type)
        {
            case 2:
                _c0 = Nums(d.Get("C0")) ?? new double[] { 0 };
                _c1 = Nums(d.Get("C1")) ?? new double[] { 1 };
                _n = d.Get("N") is { } n ? Interpreter.Num(n) : 1;
                _nOut = _c0.Length;
                break;
            case 3:
                _subs = (d.Get("Functions") as PSArray ?? throw new PSError("undefined", "Functions")).Items.ToArray()
                    .Select(f => new PSFunction(f as PSDict ?? throw new PSError("typecheck"))).ToArray();
                _bounds = Nums(d.Get("Bounds")) ?? Array.Empty<double>();
                _encode = Nums(d.Get("Encode")) ?? Enumerable.Range(0, _subs.Length).SelectMany(_ => new double[] { 0, 1 }).ToArray();
                _nOut = _subs.Length > 0 ? _subs[0].OutputCount : 1;
                break;
            case 0:
                _size = (Nums(d.Get("Size")) ?? throw new PSError("undefined", "Size")).Select(x => (int)x).ToArray();
                _bps = (int)Interpreter.Num(d.Get("BitsPerSample") ?? throw new PSError("undefined", "BitsPerSample"));
                _decode = Nums(d.Get("Decode"));
                _nOut = (_range?.Length ?? 2) / 2;
                var ds = d.Get("DataSource");
                if (ds is PSString s) _samples = s.ToArray();
                else if (ds is PSFile f)
                {
                    var ms = new MemoryStream();
                    var buf = new byte[8192];
                    int r;
                    while ((r = f.Stream.Read(buf)) > 0) ms.Write(buf, 0, r);
                    _samples = ms.ToArray();
                }
                break;
            default:
                throw new PSError("rangecheck", "FunctionType " + _type);
        }
    }

    private static double[]? Nums(PSObject? o) => o is PSArray a ? a.Items.ToArray().Select(Interpreter.Num).ToArray() : null;

    public double[] Eval(double x)
    {
        x = Math.Clamp(x, Math.Min(_domain[0], _domain[1]), Math.Max(_domain[0], _domain[1]));
        double[] r;
        switch (_type)
        {
            case 2:
                {
                    r = new double[_c0.Length];
                    double xn = _n == 1 ? x : Math.Pow(Math.Max(x, 0), _n);
                    for (int i = 0; i < r.Length; i++) r[i] = _c0[i] + xn * ((i < _c1.Length ? _c1[i] : 1) - _c0[i]);
                    break;
                }
            case 3:
                {
                    int k = 0;
                    while (k < _bounds.Length && x >= _bounds[k]) k++;
                    double lo = k == 0 ? _domain[0] : _bounds[k - 1];
                    double hi = k == _bounds.Length ? _domain[1] : _bounds[k];
                    double e0 = _encode.Length > 2 * k ? _encode[2 * k] : 0, e1 = _encode.Length > 2 * k + 1 ? _encode[2 * k + 1] : 1;
                    double t = hi == lo ? e0 : e0 + (x - lo) * (e1 - e0) / (hi - lo);
                    r = _subs.Length == 0 ? new double[] { 0 } : _subs[Math.Min(k, _subs.Length - 1)].Eval(t);
                    break;
                }
            default:
                {
                    // 1-input sampled function with linear interpolation.
                    int size = _size.Length > 0 ? _size[0] : 1;
                    double d0 = _domain[0], d1 = _domain[1];
                    double e = size <= 1 ? 0 : (x - d0) * (size - 1) / (d1 - d0);
                    e = Math.Clamp(e, 0, size - 1);
                    int i0 = (int)Math.Floor(e), i1 = Math.Min(i0 + 1, size - 1);
                    double f = e - i0;
                    r = new double[_nOut];
                    int max = (1 << _bps) - 1;
                    for (int o = 0; o < _nOut; o++)
                    {
                        double s0 = Sample(i0 * _nOut + o), s1 = Sample(i1 * _nOut + o);
                        double v = s0 + f * (s1 - s0);
                        double rmin = _range is { Length: > 0 } ? _range[2 * o] : 0, rmax = _range is { Length: > 1 } ? _range[2 * o + 1] : 1;
                        double dmin = _decode is not null ? _decode[2 * o] : rmin, dmax = _decode is not null ? _decode[2 * o + 1] : rmax;
                        r[o] = dmin + v * (dmax - dmin) / max;
                    }
                    break;
                }
        }
        if (_range is not null)
            for (int i = 0; i < r.Length && 2 * i + 1 < _range.Length; i++) r[i] = Math.Clamp(r[i], _range[2 * i], _range[2 * i + 1]);
        return r;
    }

    private double Sample(int index)
    {
        long bit = (long)index * _bps;
        if (_bps == 8) return bit / 8 < _samples.Length ? _samples[bit / 8] : 0;
        int v = 0;
        for (int i = 0; i < _bps; i++)
        {
            long b = bit + i;
            if (b / 8 >= _samples.Length) return 0;
            v = (v << 1) | ((_samples[b / 8] >> (7 - (int)(b % 8))) & 1);
        }
        return v;
    }
}

public sealed partial class Interpreter
{
    private Func<double, double[]>? ShadingFunction(PSDict sh)
    {
        var f = sh.Get("Function");
        if (f is PSDict fd) { var fn = new PSFunction(fd); return fn.Eval; }
        if (f is PSArray fa && !fa.IsExec)
        {
            var fns = fa.Items.ToArray().Select(x => new PSFunction(x as PSDict ?? throw new PSError("typecheck"))).ToArray();
            return t => fns.Select(fn => fn.Eval(t)[0]).ToArray();
        }
        return null;
    }

    private SKColor ColorOf(ColorSpace cs, double[] comps)
    {
        var (r, g, b) = ToRgb(cs, comps);
        return new SKColor(ToByte(r), ToByte(g), ToByte(b));
    }

    /// <summary>Builds a shader for ShadingType 2/3 (gradients), or a picture shader for meshes.</summary>
    private SKShader? BuildShadingShader(PSDict sh, Matrix toDevice, out SKColor average)
    {
        int type = (int)Num(sh.Get("ShadingType") ?? throw new PSError("undefined", "ShadingType"));
        var cs = ParseColorSpace(sh.Get("ColorSpace") ?? throw new PSError("undefined", "ColorSpace"));
        average = SKColors.Gray;
        switch (type)
        {
            case 2:
            case 3:
                {
                    var coords = (sh.Get("Coords") as PSArray ?? throw new PSError("undefined", "Coords")).Items.ToArray().Select(Num).ToArray();
                    var domain = sh.Get("Domain") is PSArray da ? da.Items.ToArray().Select(Num).ToArray() : new double[] { 0, 1 };
                    var ext = sh.Get("Extend") is PSArray ea ? ea.Items.ToArray().Select(e => e is PSBool { Value: true }).ToArray() : new[] { false, false };
                    var fn = ShadingFunction(sh) ?? throw new PSError("undefined", "Function");
                    const int stops = 64;
                    var colors = new SKColor[stops];
                    var pos = new float[stops];
                    for (int i = 0; i < stops; i++)
                    {
                        double t = domain[0] + (domain[1] - domain[0]) * i / (stops - 1);
                        colors[i] = ColorOf(cs, fn(t));
                        pos[i] = (float)i / (stops - 1);
                    }
                    average = colors[stops / 2];
                    var mode = ext[0] || ext[1] ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal;
                    var local = toDevice.ToSK();
                    if (type == 2)
                        return SKShader.CreateLinearGradient(new SKPoint((float)coords[0], (float)coords[1]), new SKPoint((float)coords[2], (float)coords[3]), colors, pos, mode, local);
                    return SKShader.CreateTwoPointConicalGradient(new SKPoint((float)coords[0], (float)coords[1]), (float)coords[2],
                        new SKPoint((float)coords[3], (float)coords[4]), (float)coords[5], colors, pos, mode, local);
                }
            case 4:
            case 5:
                {
                    var tris = MeshTriangles(sh, type, cs);
                    if (tris.Count == 0) return null;
                    average = tris[0].C0;
                    using var img = RasterizeTriangles(tris, toDevice, out var origin);
                    if (img is null) return null;
                    var m = SKMatrix.CreateTranslation(origin.X, origin.Y);
                    return SKShader.CreateImage(img, SKShaderTileMode.Decal, SKShaderTileMode.Decal, m);
                }
            default:
                {
                    Log($"Shading type {type} is drawn as a flat color.");
                    var fn = ShadingFunction(sh);
                    var c = fn is null ? SKColors.Gray : ColorOf(cs, fn(0.5));
                    average = c;
                    return SKShader.CreateColor(c);
                }
        }
    }

    private void ShFill(PSDict sh)
    {
        if (!CanPaint) return;
        var shader = BuildShadingShader(sh, GS.CTM, out _);
        if (shader is null) return;
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        SKPath? bboxClip = null;
        if (sh.Get("BBox") is PSArray bb && bb.Length == 4)
        {
            double x0 = Num(bb[0]), y0 = Num(bb[1]), x1 = Num(bb[2]), y1 = Num(bb[3]);
            bboxClip = RectsPath(new() { (x0, y0, x1 - x0, y1 - y0) }).ToSKPath();
        }
        DrawClipped(c =>
        {
            if (bboxClip is not null) c.ClipPath(bboxClip, SKClipOperation.Intersect, true);
            c.DrawPaint(paint);
        });
        bboxClip?.Dispose();
        shader.Dispose();
    }

    private readonly record struct Tri(double X0, double Y0, SKColor C0, double X1, double Y1, SKColor C1, double X2, double Y2, SKColor C2);

    private readonly record struct MeshVertex(double X, double Y, SKColor C);

    private List<Tri> MeshTriangles(PSDict sh, int type, ColorSpace cs)
    {
        var fn = ShadingFunction(sh);
        int ncomp = fn is not null ? 1 : cs.NComps;
        var verts = new List<(int Flag, MeshVertex V)>();
        var ds = sh.Get("DataSource");
        if (ds is PSArray arr && !arr.IsExec)
        {
            var nums = arr.Items.ToArray().Select(Num).ToArray();
            int per = (type == 4 ? 1 : 0) + 2 + ncomp;
            for (int i = 0; i + per <= nums.Length; i += per)
            {
                int k = i;
                int flag = type == 4 ? (int)nums[k++] : 0;
                double x = nums[k++], y = nums[k++];
                var comps = nums.AsSpan(k, ncomp).ToArray();
                verts.Add((flag, new MeshVertex(x, y, ColorOf(cs, fn is not null ? fn(comps[0]) : comps))));
            }
        }
        else if (ds is PSString or PSFile)
        {
            byte[] data = ds is PSString s ? s.ToArray() : ReadAll(((PSFile)ds).Stream);
            int bpf = type == 4 ? (int)Num(sh.Get("BitsPerFlag") ?? PSInt.Get(8)) : 0;
            int bpcoord = (int)Num(sh.Get("BitsPerCoordinate") ?? throw new PSError("undefined", "BitsPerCoordinate"));
            int bpcomp = (int)Num(sh.Get("BitsPerComponent") ?? throw new PSError("undefined", "BitsPerComponent"));
            var dec = (sh.Get("Decode") as PSArray ?? throw new PSError("undefined", "Decode")).Items.ToArray().Select(Num).ToArray();
            long bit = 0;
            long total = data.Length * 8L;
            long ReadBits(int n)
            {
                long v = 0;
                for (int i = 0; i < n; i++, bit++) v = (v << 1) | (uint)((data[bit >> 3] >> (7 - (int)(bit & 7))) & 1);
                return v;
            }
            while (bit + bpf + 2L * bpcoord + (long)ncomp * bpcomp <= total)
            {
                int flag = bpf > 0 ? (int)ReadBits(bpf) : 0;
                double cmax = Math.Pow(2, bpcoord) - 1, kmax = Math.Pow(2, bpcomp) - 1;
                double x = dec[0] + ReadBits(bpcoord) * (dec[1] - dec[0]) / cmax;
                double y = dec[2] + ReadBits(bpcoord) * (dec[3] - dec[2]) / cmax;
                var comps = new double[ncomp];
                for (int c = 0; c < ncomp; c++) comps[c] = dec[4 + 2 * c] + ReadBits(bpcomp) * (dec[5 + 2 * c] - dec[4 + 2 * c]) / kmax;
                verts.Add((flag, new MeshVertex(x, y, ColorOf(cs, fn is not null ? fn(comps[0]) : comps))));
                if (type == 4 && bpf > 0) bit = (bit + 7) / 8 * 8; // each vertex starts on a byte boundary? (only for flags) keep aligned
            }
        }

        var tris = new List<Tri>();
        if (type == 4)
        {
            MeshVertex a = default, b = default, c = default;
            int have = 0;
            foreach (var (flag, v) in verts)
            {
                if (have < 3)
                {
                    if (have == 0 && flag != 0) continue;
                    if (have == 0) a = v; else if (have == 1) b = v; else c = v;
                    have++;
                    if (have == 3) tris.Add(MakeTri(a, b, c));
                    continue;
                }
                if (flag == 0) { a = v; have = 1; continue; }
                if (flag == 1) { a = b; b = c; c = v; }
                else { b = c; c = v; }
                tris.Add(MakeTri(a, b, c));
            }
        }
        else
        {
            int perRow = (int)Num(sh.Get("VerticesPerRow") ?? PSInt.Get(2));
            int rows = verts.Count / Math.Max(perRow, 1);
            for (int r = 0; r + 1 < rows; r++)
                for (int k = 0; k + 1 < perRow; k++)
                {
                    var v00 = verts[r * perRow + k].V; var v01 = verts[r * perRow + k + 1].V;
                    var v10 = verts[(r + 1) * perRow + k].V; var v11 = verts[(r + 1) * perRow + k + 1].V;
                    tris.Add(MakeTri(v00, v01, v10));
                    tris.Add(MakeTri(v01, v11, v10));
                }
        }
        return tris;
    }

    private static Tri MakeTri(MeshVertex a, MeshVertex b, MeshVertex c) => new(a.X, a.Y, a.C, b.X, b.Y, b.C, c.X, c.Y, c.C);

    private static byte[] ReadAll(PSStream s)
    {
        var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = s.Read(buf)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    /// <summary>Renders Gouraud triangles (in shading space, mapped by toDevice) into an image in device space.</summary>
    private SKImage? RasterizeTriangles(List<Tri> tris, Matrix toDevice, out SKPoint origin)
    {
        var pts = new List<SKPoint>(tris.Count * 3);
        var cols = new List<SKColor>(tris.Count * 3);
        foreach (var t in tris)
        {
            var p0 = toDevice.Transform(t.X0, t.Y0); var p1 = toDevice.Transform(t.X1, t.Y1); var p2 = toDevice.Transform(t.X2, t.Y2);
            pts.Add(new SKPoint((float)p0.X, (float)p0.Y)); pts.Add(new SKPoint((float)p1.X, (float)p1.Y)); pts.Add(new SKPoint((float)p2.X, (float)p2.Y));
            cols.Add(t.C0); cols.Add(t.C1); cols.Add(t.C2);
        }
        float minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        // Clamp to the page area plus a margin.
        minX = Math.Max(minX, -10); minY = Math.Max(minY, -10);
        maxX = Math.Min(maxX, Device.PixelWidth + 10); maxY = Math.Min(maxY, Device.PixelHeight + 10);
        origin = new SKPoint((float)Math.Floor(minX), (float)Math.Floor(minY));
        int w = (int)Math.Ceiling(maxX - origin.X) + 1, h = (int)Math.Ceiling(maxY - origin.Y) + 1;
        if (w <= 0 || h <= 0) return null;
        // Smooth gradients need little resolution: rasterize at no more than 150 dpi and 3000 px.
        double scale = Math.Min(Math.Min(1.0, 150.0 / Device.Dpi), 3000.0 / Math.Max(w, h));
        int bw = Math.Max(1, (int)(w * scale)), bh = Math.Max(1, (int)(h * scale));
        using var surface = SKSurface.Create(new SKImageInfo(bw, bh, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)scale);
        canvas.Translate(-origin.X, -origin.Y);
        using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, pts.ToArray(), null, cols.ToArray());
        using var paint = new SKPaint { IsAntialias = false, Color = SKColors.White };
        canvas.DrawVertices(vertices, SKBlendMode.Dst, paint);
        var snap = surface.Snapshot();
        if (scale < 1.0)
        {
            // Return an image that, drawn at origin, covers the full size.
            using var up = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            up.Canvas.DrawImage(snap, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear));
            snap.Dispose();
            return up.Snapshot();
        }
        return snap;
    }
}
