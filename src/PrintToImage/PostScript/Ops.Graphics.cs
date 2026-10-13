using PrintToImage.PostScript.Graphics;
using SkiaSharp;

namespace PrintToImage.PostScript;

public sealed partial class Interpreter
{
    internal void InitGraphics()
    {
        var font = GS.Font;
        bool nul = GS.NullDevice;
        GS.CTM = Device.DefaultMatrix;
        GS.Path = new PSPath();
        GS.Clip = null;
        GS.ColorSpace = ColorSpace.Gray;
        GS.Color = new double[] { 0 };
        GS.Pattern = null;
        GS.LineWidth = 1;
        GS.LineCap = 0;
        GS.LineJoin = 0;
        GS.MiterLimit = 10;
        GS.Dash = Array.Empty<double>();
        GS.DashOffset = 0;
        GS.Flatness = 1;
        GS.StrokeAdjust = false;
        GS.Font = font;
        GS.NullDevice = nul;
    }

    private void GSave()
    {
        if (GStack.Count > 10_000) throw new PSError("limitcheck", "gsave nesting");
        GStack.Add(GS);
        GS = GS.Clone();
        GS.SavedBySave = false;
    }

    private void GRestore()
    {
        if (GStack.Count == 0) return;
        var top = GStack[^1];
        if (top.SavedBySave)
        {
            // grestore at a save boundary restores the state but keeps it on the stack.
            GS = top.Clone();
            GS.SavedBySave = false;
            return;
        }
        GS = top;
        GStack.RemoveAt(GStack.Count - 1);
    }

    private void RegisterGraphicsOperators()
    {
        Def("gsave", ip => ip.GSave());
        Def("grestore", ip => ip.GRestore());
        Def("grestoreall", ip =>
        {
            while (ip.GStack.Count > 0 && !ip.GStack[^1].SavedBySave) { ip.GS = ip.GStack[^1]; ip.GStack.RemoveAt(ip.GStack.Count - 1); }
            if (ip.GStack.Count > 0) { ip.GS = ip.GStack[^1].Clone(); ip.GS.SavedBySave = false; }
        });
        Def("initgraphics", ip => ip.InitGraphics());
        Def("gstate", ip => ip.Push(new PSGState(ip.GS.Clone())));
        Def("currentgstate", ip =>
        {
            var g = ip.Pop() as PSGState ?? throw new PSError("typecheck");
            g.State = ip.GS.Clone();
            ip.Push(g);
        });
        Def("setgstate", ip =>
        {
            var g = ip.Pop() as PSGState ?? throw new PSError("typecheck");
            ip.GS = g.State.Clone();
        });

        Def("setlinewidth", ip => ip.GS.LineWidth = Math.Abs(ip.PopNum()));
        Def("currentlinewidth", ip => ip.Push(ip.GS.LineWidth));
        Def("setlinecap", ip => { int v = ip.PopInt(); if (v is < 0 or > 2) throw new PSError("rangecheck"); ip.GS.LineCap = v; });
        Def("currentlinecap", ip => ip.Push(ip.GS.LineCap));
        Def("setlinejoin", ip => { int v = ip.PopInt(); if (v is < 0 or > 2) throw new PSError("rangecheck"); ip.GS.LineJoin = v; });
        Def("currentlinejoin", ip => ip.Push(ip.GS.LineJoin));
        Def("setmiterlimit", ip => { double v = ip.PopNum(); if (v < 1) throw new PSError("rangecheck"); ip.GS.MiterLimit = v; });
        Def("currentmiterlimit", ip => ip.Push(ip.GS.MiterLimit));
        Def("setdash", ip =>
        {
            double off = ip.PopNum();
            var a = ip.PopArray();
            var d = new double[a.Length];
            bool allZero = true;
            for (int i = 0; i < d.Length; i++) { d[i] = Num(a[i]); if (d[i] < 0) throw new PSError("rangecheck"); if (d[i] != 0) allZero = false; }
            ip.GS.Dash = allZero ? Array.Empty<double>() : d;
            ip.GS.DashOffset = off;
        });
        Def("currentdash", ip =>
        {
            ip.Push(new PSArray(ip.GS.Dash.Select(x => Real(x)).ToArray(), ip.AllocLevel));
            ip.Push(ip.GS.DashOffset);
        });
        Def("setflat", ip => ip.GS.Flatness = Math.Clamp(ip.PopNum(), 0.2, 100));
        Def("currentflat", ip => ip.Push(ip.GS.Flatness));
        Def("setstrokeadjust", ip => ip.GS.StrokeAdjust = ip.PopBool());
        Def("currentstrokeadjust", ip => ip.Push(ip.GS.StrokeAdjust));
        Def("setsmoothness", ip => ip.GS.Smoothness = ip.PopNum());
        Def("currentsmoothness", ip => ip.Push(ip.GS.Smoothness));
        Def("setoverprint", ip => ip.GS.Overprint = ip.PopBool());
        Def("currentoverprint", ip => ip.Push(ip.GS.Overprint));
        Def("settransfer", ip => { var p = ip.PopProc(); ip.GS.TransferR = ip.GS.TransferG = ip.GS.TransferB = ip.GS.TransferGray = p; });
        Def("currenttransfer", ip => ip.Push(ip.GS.TransferGray ?? IdentityProc()));
        Def("setcolortransfer", ip =>
        {
            var gray = ip.PopProc(); var b = ip.PopProc(); var g = ip.PopProc(); var r = ip.PopProc();
            (ip.GS.TransferR, ip.GS.TransferG, ip.GS.TransferB, ip.GS.TransferGray) = (r, g, b, gray);
        });
        Def("currentcolortransfer", ip =>
        {
            ip.Push(ip.GS.TransferR ?? IdentityProc()); ip.Push(ip.GS.TransferG ?? IdentityProc());
            ip.Push(ip.GS.TransferB ?? IdentityProc()); ip.Push(ip.GS.TransferGray ?? IdentityProc());
        });
        Def("setblackgeneration", ip => ip.GS.BlackGeneration = ip.Pop());
        Def("currentblackgeneration", ip => ip.Push(ip.GS.BlackGeneration ?? new PSArray(Array.Empty<PSObject>(), -1, exec: true)));
        Def("setundercolorremoval", ip => ip.GS.UnderColorRemoval = ip.Pop());
        Def("currentundercolorremoval", ip => ip.Push(ip.GS.UnderColorRemoval ?? new PSArray(new PSObject[] { PSName.Exe("pop"), PSInt.Get(0) }, -1, exec: true)));
        Def("setscreen", ip => { ip.Pop(); ip.Pop(); ip.Pop(); });
        Def("currentscreen", ip => { ip.Push(60.0); ip.Push(45.0); ip.Push(new PSArray(new PSObject[] { PSName.Exe("pop") }, -1, exec: true)); });
        Def("setcolorscreen", ip => { for (int i = 0; i < 12; i++) ip.Pop(); });
        Def("currentcolorscreen", ip => { for (int i = 0; i < 4; i++) { ip.Push(60.0); ip.Push(45.0); ip.Push(new PSArray(new PSObject[] { PSName.Exe("pop") }, -1, exec: true)); } });
        Def("sethalftone", ip => ip.GS.Halftone = ip.PopDict());
        Def("currenthalftone", ip =>
        {
            if (ip.GS.Halftone is not null) { ip.Push(ip.GS.Halftone); return; }
            var d = ip.NewDict(4);
            d.PutRaw("HalftoneType", PSInt.Get(1));
            d.PutRaw("Frequency", Real(60));
            d.PutRaw("Angle", Real(45));
            d.PutRaw("SpotFunction", new PSArray(new PSObject[] { PSName.Exe("pop") }, -1, exec: true));
            ip.Push(d);
        });
        Def("sethalftonephase", ip => { ip.Pop(); ip.Pop(); });
        Def("currenthalftonephase", ip => { ip.Push(0); ip.Push(0); });
        Def("setcolorrendering", ip => ip.GS.ColorRendering = ip.PopDict());
        Def("currentcolorrendering", ip =>
        {
            if (ip.GS.ColorRendering is not null) { ip.Push(ip.GS.ColorRendering); return; }
            var d = ip.NewDict(2);
            d.PutRaw("ColorRenderingType", PSInt.Get(1));
            ip.Push(d);
        });

        // ---- matrices ----
        Def("matrix", ip => ip.Push(ip.NewMatrix(Matrix.Identity)));
        Def("initmatrix", ip => ip.GS.CTM = ip.Device.DefaultMatrix);
        Def("identmatrix", ip => { var a = ip.PopArray(); ip.StoreMatrix(a, Matrix.Identity); ip.Push(a); });
        Def("defaultmatrix", ip => { var a = ip.PopArray(); ip.StoreMatrix(a, ip.Device.DefaultMatrix); ip.Push(a); });
        Def("currentmatrix", ip => { var a = ip.PopArray(); ip.StoreMatrix(a, ip.GS.CTM); ip.Push(a); });
        Def("setmatrix", ip => ip.GS.CTM = ip.PopMatrix());
        Def("translate", ip => ip.MatrixOp(m => Matrix.Translate(ip.PopNum2(out var tx), tx)));
        Def("scale", ip => ip.MatrixOp(m => Matrix.Scale(ip.PopNum2(out var sy), sy)));
        Def("rotate", ip => ip.MatrixOp(m => Matrix.Rotate(ip.PopNum())));
        Def("concat", ip => ip.GS.CTM = ip.PopMatrix().Multiply(ip.GS.CTM));
        Def("concatmatrix", ip =>
        {
            var dst = ip.PopArray();
            var m2 = ip.PopMatrix();
            var m1 = ip.PopMatrix();
            ip.StoreMatrix(dst, m1.Multiply(m2));
            ip.Push(dst);
        });
        Def("invertmatrix", ip =>
        {
            var dst = ip.PopArray();
            var m = ip.PopMatrix();
            ip.StoreMatrix(dst, m.Invert());
            ip.Push(dst);
        });
        Def("transform", ip => ip.PointOp((m, x, y) => m.Transform(x, y), false));
        Def("dtransform", ip => ip.PointOp((m, x, y) => m.DTransform(x, y), false));
        Def("itransform", ip => ip.PointOp((m, x, y) => m.Invert().Transform(x, y), true));
        Def("idtransform", ip => ip.PointOp((m, x, y) => m.Invert().DTransform(x, y), true));

        // ---- color ----
        Def("setgray", ip => ip.SetColor(ColorSpace.Gray, Clamp01(ip.PopNum())));
        Def("currentgray", ip => { var c = ip.CurrentRgb(); ip.Push(0.3 * c.R + 0.59 * c.G + 0.11 * c.B); });
        Def("setrgbcolor", ip =>
        {
            double b = ip.PopNum(), g = ip.PopNum(), r = ip.PopNum();
            ip.SetColor(ColorSpace.RGB, Clamp01(r), Clamp01(g), Clamp01(b));
        });
        Def("currentrgbcolor", ip => { var c = ip.CurrentRgb(); ip.Push(c.R); ip.Push(c.G); ip.Push(c.B); });
        Def("sethsbcolor", ip =>
        {
            double b = Clamp01(ip.PopNum()), s = Clamp01(ip.PopNum()), h = Clamp01(ip.PopNum());
            var (r, g, bl) = HsbToRgb(h, s, b);
            ip.SetColor(ColorSpace.RGB, r, g, bl);
        });
        Def("currenthsbcolor", ip =>
        {
            var c = ip.CurrentRgb();
            var (h, s, v) = RgbToHsb(c.R, c.G, c.B);
            ip.Push(h); ip.Push(s); ip.Push(v);
        });
        Def("setcmykcolor", ip =>
        {
            double k = ip.PopNum(), y = ip.PopNum(), m = ip.PopNum(), c = ip.PopNum();
            ip.SetColor(ColorSpace.CMYK, Clamp01(c), Clamp01(m), Clamp01(y), Clamp01(k));
        });
        Def("currentcmykcolor", ip =>
        {
            if (ip.GS.ColorSpace.Family == CSFamily.DeviceCMYK) { foreach (var v in ip.GS.Color) ip.Push(v); return; }
            var c = ip.CurrentRgb();
            double k = 1 - Math.Max(c.R, Math.Max(c.G, c.B));
            ip.Push(1 - c.R - k); ip.Push(1 - c.G - k); ip.Push(1 - c.B - k); ip.Push(k);
        });
        Def("setcolorspace", ip =>
        {
            var cs = ip.ParseColorSpace(ip.Pop());
            ip.GS.ColorSpace = cs;
            ip.GS.Color = cs.InitialColor();
            ip.GS.Pattern = null;
        });
        Def("currentcolorspace", ip => ip.Push(ip.GS.ColorSpace.Source is PSArray sa ? sa : new PSArray(new[] { ip.GS.ColorSpace.Source }, ip.AllocLevel)));
        Def("setcolor", ip =>
        {
            var cs = ip.GS.ColorSpace;
            if (cs.Family == CSFamily.Pattern)
            {
                var pat = ip.PopDict();
                int under = cs.Base?.NComps ?? 0;
                var comps = new double[under];
                for (int i = under - 1; i >= 0; i--) comps[i] = ip.PopNum();
                ip.GS.Color = comps;
                ip.GS.Pattern = ip.PatternFromDict(pat);
                return;
            }
            var c = new double[cs.NComps];
            for (int i = cs.NComps - 1; i >= 0; i--) c[i] = ip.PopNum();
            ip.GS.Color = c;
        });
        Def("currentcolor", ip =>
        {
            foreach (var v in ip.GS.Color) ip.Push(v);
            if (ip.GS.ColorSpace.Family == CSFamily.Pattern && ip.GS.Pattern?.Dict is { } pd) ip.Push(pd);
        });
        Def("setpattern", ip =>
        {
            var pat = ip.PopDict();
            var info = ip.PatternFromDict(pat);
            if (ip.GS.ColorSpace.Family != CSFamily.Pattern || info.PaintType == 2 && ip.GS.ColorSpace.Base is null)
            {
                var baseCs = info.PaintType == 2 ? ip.GS.ColorSpace : null;
                var src = baseCs is null
                    ? (PSObject)PSName.Lit("Pattern")
                    : new PSArray(new PSObject[] { PSName.Lit("Pattern"), baseCs.Source }, ip.AllocLevel);
                var prevColor = ip.GS.Color;
                ip.GS.ColorSpace = new ColorSpace { Family = CSFamily.Pattern, NComps = 1, Source = src, Base = baseCs };
                ip.GS.Color = info.PaintType == 2 ? prevColor : Array.Empty<double>();
            }
            else if (info.PaintType == 2)
            {
                int under = ip.GS.ColorSpace.Base?.NComps ?? 0;
                var comps = new double[under];
                for (int i = under - 1; i >= 0; i--) comps[i] = ip.PopNum();
                ip.GS.Color = comps;
            }
            ip.GS.Pattern = info;
        });
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private double PopNum2(out double second)
    {
        second = PopNum();
        return PopNum();
    }

    /// <summary>translate / scale / rotate: either modify the CTM, or (with a matrix operand) fill that matrix.</summary>
    private void MatrixOp(Func<Matrix, Matrix> make)
    {
        if (Peek() is PSArray a && a.Length == 6 && !a.IsExec)
        {
            Pop();
            var m = make(Matrix.Identity);
            StoreMatrix(a, m);
            Push(a);
            return;
        }
        GS.CTM = make(Matrix.Identity).Multiply(GS.CTM);
    }

    private void PointOp(Func<Matrix, double, double, (double, double)> f, bool inverse)
    {
        Matrix m = GS.CTM;
        if (Peek() is PSArray) m = PopMatrix();
        double y = PopNum(), x = PopNum();
        var (rx, ry) = f(m, x, y);
        Push(rx);
        Push(ry);
    }

    internal void SetColor(ColorSpace cs, params double[] comps)
    {
        GS.ColorSpace = cs;
        GS.Color = comps;
        GS.Pattern = null;
    }

    private static (double, double, double) HsbToRgb(double h, double s, double v)
    {
        if (s == 0) return (v, v, v);
        double hh = (h * 6) % 6;
        int i = (int)Math.Floor(hh);
        double f = hh - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
    }

    private static (double, double, double) RgbToHsb(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            if (h < 0) h += 1;
        }
        return (h, max == 0 ? 0 : d / max, max);
    }

    // ---------------- color spaces ----------------

    internal ColorSpace ParseColorSpace(PSObject o)
    {
        string family;
        PSArray? arr = null;
        if (o is PSName n) family = n.Text;
        else if (o is PSArray a && a.Length > 0 && a[0] is PSName fn) { family = fn.Text; arr = a; }
        else throw new PSError("typecheck", "color space");

        switch (family)
        {
            case "DeviceGray":
            case "CalGray":
            case "CIEBasedA":
                return arr is null ? ColorSpace.Gray : new ColorSpace { Family = CSFamily.DeviceGray, NComps = 1, Source = o };
            case "DeviceRGB":
            case "CalRGB":
            case "CIEBasedABC":
            case "Lab":
            case "CIEBasedDEF":
                return arr is null ? ColorSpace.RGB : new ColorSpace { Family = CSFamily.DeviceRGB, NComps = 3, Source = o };
            case "DeviceCMYK":
            case "CIEBasedDEFG":
                return arr is null ? ColorSpace.CMYK : new ColorSpace { Family = CSFamily.DeviceCMYK, NComps = 4, Source = o };
            case "ICCBased":
                {
                    int ncomp = 3;
                    if (arr!.Length > 1 && arr[1] is PSDict d && d.Get("N") is { } nn) ncomp = (int)Num(nn);
                    else if (arr.Length > 1 && arr[1] is PSFile f) { /* stream form: no N available */ }
                    return new ColorSpace { Family = ncomp switch { 1 => CSFamily.DeviceGray, 4 => CSFamily.DeviceCMYK, _ => CSFamily.DeviceRGB }, NComps = ncomp, Source = o };
                }
            case "Indexed":
                {
                    if (arr is null || arr.Length < 4) throw new PSError("rangecheck");
                    var baseCs = ParseColorSpace(arr[1]);
                    var cs = new ColorSpace { Family = CSFamily.Indexed, NComps = 1, Source = o, Base = baseCs, HiVal = (int)Num(arr[2]) };
                    if (arr[3] is PSString s) cs.Lookup = s.ToArray();
                    else cs.LookupProc = arr[3];
                    return cs;
                }
            case "Separation":
                {
                    if (arr is null || arr.Length < 4) throw new PSError("rangecheck");
                    var alt = ParseColorSpace(arr[2]);
                    return new ColorSpace
                    {
                        Family = CSFamily.Separation, NComps = 1, Source = o, Base = alt, TintTransform = arr[3],
                        SeparationNone = arr[1] is PSName { Text: "None" },
                    };
                }
            case "DeviceN":
                {
                    if (arr is null || arr.Length < 4 || arr[1] is not PSArray names) throw new PSError("rangecheck");
                    var alt = ParseColorSpace(arr[2]);
                    return new ColorSpace { Family = CSFamily.DeviceN, NComps = names.Length, Source = o, Base = alt, TintTransform = arr[3] };
                }
            case "Pattern":
                {
                    ColorSpace? under = arr is not null && arr.Length > 1 ? ParseColorSpace(arr[1]) : null;
                    return new ColorSpace { Family = CSFamily.Pattern, NComps = 1, Source = o, Base = under };
                }
            default:
                throw new PSError("undefined", "color space " + family);
        }
    }

    /// <summary>Converts components in a color space to RGB (0..1).</summary>
    internal (double R, double G, double B) ToRgb(ColorSpace cs, ReadOnlySpan<double> c)
    {
        switch (cs.Family)
        {
            case CSFamily.DeviceGray: return c.Length > 0 ? (c[0], c[0], c[0]) : (0, 0, 0);
            case CSFamily.DeviceRGB: return c.Length >= 3 ? (Clamp01(c[0]), Clamp01(c[1]), Clamp01(c[2])) : (0, 0, 0);
            case CSFamily.DeviceCMYK:
                if (c.Length < 4) return (0, 0, 0);
                return (1 - Math.Min(1, c[0] + c[3]), 1 - Math.Min(1, c[1] + c[3]), 1 - Math.Min(1, c[2] + c[3]));
            case CSFamily.Indexed:
                {
                    int idx = Math.Clamp((int)Math.Round(c.Length > 0 ? c[0] : 0), 0, cs.HiVal);
                    var baseCs = cs.Base!;
                    var comps = new double[baseCs.NComps];
                    if (cs.Lookup is not null)
                    {
                        for (int i = 0; i < comps.Length; i++)
                        {
                            int p = idx * comps.Length + i;
                            comps[i] = p < cs.Lookup.Length ? cs.Lookup[p] / 255.0 : 0;
                        }
                    }
                    else if (cs.LookupProc is not null)
                    {
                        int before = _osp;
                        Push(idx);
                        ExecuteNested(cs.LookupProc);
                        for (int i = comps.Length - 1; i >= 0; i--) comps[i] = _osp > before ? PopNum() : 0;
                        _osp = Math.Min(_osp, before);
                    }
                    return ToRgb(baseCs, comps);
                }
            case CSFamily.Separation:
            case CSFamily.DeviceN:
                {
                    var alt = cs.Base!;
                    int before = _osp;
                    foreach (var v in c) Push(v);
                    var comps = new double[alt.NComps];
                    try
                    {
                        ExecuteNested(cs.TintTransform!);
                        for (int i = comps.Length - 1; i >= 0; i--) comps[i] = _osp > before ? PopNum() : 0;
                    }
                    catch (PSError)
                    {
                        // Fall back to treating the tint as gray.
                        double t = c.Length > 0 ? c[0] : 1;
                        return (1 - t, 1 - t, 1 - t);
                    }
                    _osp = Math.Min(_osp, before);
                    return ToRgb(alt, comps);
                }
            default:
                return (0, 0, 0);
        }
    }

    internal (double R, double G, double B) CurrentRgb()
    {
        if (GS.ColorSpace.Family == CSFamily.Pattern)
        {
            if (GS.ColorSpace.Base is { } b && GS.Color.Length > 0) return ToRgb(b, GS.Color);
            var a = GS.Pattern?.AverageColor ?? SKColors.Gray;
            return (a.Red / 255.0, a.Green / 255.0, a.Blue / 255.0);
        }
        return ToRgb(GS.ColorSpace, GS.Color);
    }

    internal SKColor CurrentSKColor()
    {
        var (r, g, b) = CurrentRgb();
        if (TransferActive)
        {
            r = ApplyTransfer(GS.TransferR, r);
            g = ApplyTransfer(GS.TransferG, g);
            b = ApplyTransfer(GS.TransferB, b);
        }
        return new SKColor((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    // ---------------- transfer functions (applied to device RGB values) ----------------

    private static PSArray IdentityProc() => new(Array.Empty<PSObject>(), -1, exec: true);

    private static bool IsIdentity(PSObject? p) => p is null || p is PSArray { Length: 0 };

    internal bool TransferActive => !(IsIdentity(GS.TransferR) && IsIdentity(GS.TransferG) && IsIdentity(GS.TransferB));

    internal double ApplyTransfer(PSObject? proc, double v)
    {
        if (IsIdentity(proc)) return v;
        int before = _osp;
        try
        {
            Push(v);
            ExecuteNested(proc!);
            double r = _osp > before && IsNum(Peek()) ? Num(Peek()) : v;
            return Clamp01(r);
        }
        catch (PSError) { return v; }
        finally { _osp = before; }
    }

    /// <summary>256-entry lookup table for a transfer function, or null for identity.</summary>
    internal byte[]? TransferLut(PSObject? proc)
    {
        if (IsIdentity(proc)) return null;
        var lut = new byte[256];
        for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Round(ApplyTransfer(proc, i / 255.0) * 255);
        return lut;
    }
}
