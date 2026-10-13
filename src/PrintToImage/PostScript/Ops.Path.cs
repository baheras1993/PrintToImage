using PrintToImage.PostScript.Graphics;
using SkiaSharp;

namespace PrintToImage.PostScript;

public sealed partial class Interpreter
{
    private void RegisterPathOperators()
    {
        Def("newpath", ip => ip.GS.Path = new PSPath());
        Def("currentpoint", ip =>
        {
            if (!ip.GS.Path.HasCurrentPoint) throw new PSError("nocurrentpoint");
            if (!ip.GS.CTM.TryInvert(out var inv)) throw new PSError("undefinedresult");
            var (x, y) = inv.Transform(ip.GS.Path.CurX, ip.GS.Path.CurY);
            ip.Push(x);
            ip.Push(y);
        });
        Def("moveto", ip => { double y = ip.PopNum(), x = ip.PopNum(); var (dx, dy) = ip.GS.CTM.Transform(x, y); ip.GS.Path.MoveTo(dx, dy); });
        Def("rmoveto", ip =>
        {
            double y = ip.PopNum(), x = ip.PopNum();
            if (!ip.GS.Path.HasCurrentPoint) throw new PSError("nocurrentpoint");
            var (dx, dy) = ip.GS.CTM.DTransform(x, y);
            ip.GS.Path.MoveTo(ip.GS.Path.CurX + dx, ip.GS.Path.CurY + dy);
        });
        Def("lineto", ip => { double y = ip.PopNum(), x = ip.PopNum(); var (dx, dy) = ip.GS.CTM.Transform(x, y); ip.GS.Path.LineTo(dx, dy); });
        Def("rlineto", ip =>
        {
            double y = ip.PopNum(), x = ip.PopNum();
            if (!ip.GS.Path.HasCurrentPoint) throw new PSError("nocurrentpoint");
            var (dx, dy) = ip.GS.CTM.DTransform(x, y);
            ip.GS.Path.LineTo(ip.GS.Path.CurX + dx, ip.GS.Path.CurY + dy);
        });
        Def("curveto", ip =>
        {
            double y3 = ip.PopNum(), x3 = ip.PopNum(), y2 = ip.PopNum(), x2 = ip.PopNum(), y1 = ip.PopNum(), x1 = ip.PopNum();
            var m = ip.GS.CTM;
            var p1 = m.Transform(x1, y1); var p2 = m.Transform(x2, y2); var p3 = m.Transform(x3, y3);
            ip.GS.Path.CurveTo(p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
        });
        Def("rcurveto", ip =>
        {
            double y3 = ip.PopNum(), x3 = ip.PopNum(), y2 = ip.PopNum(), x2 = ip.PopNum(), y1 = ip.PopNum(), x1 = ip.PopNum();
            var path = ip.GS.Path;
            if (!path.HasCurrentPoint) throw new PSError("nocurrentpoint");
            var m = ip.GS.CTM;
            double cx = path.CurX, cy = path.CurY;
            var d1 = m.DTransform(x1, y1); var d2 = m.DTransform(x2, y2); var d3 = m.DTransform(x3, y3);
            path.CurveTo(cx + d1.X, cy + d1.Y, cx + d2.X, cy + d2.Y, cx + d3.X, cy + d3.Y);
        });
        Def("arc", ip => { double a2 = ip.PopNum(), a1 = ip.PopNum(), r = ip.PopNum(), y = ip.PopNum(), x = ip.PopNum(); ip.GS.Path.Arc(ip.GS.CTM, x, y, r, a1, a2, false); });
        Def("arcn", ip => { double a2 = ip.PopNum(), a1 = ip.PopNum(), r = ip.PopNum(), y = ip.PopNum(), x = ip.PopNum(); ip.GS.Path.Arc(ip.GS.CTM, x, y, r, a1, a2, true); });
        Def("arct", ip => ip.ArcTo(false));
        Def("arcto", ip => ip.ArcTo(true));
        Def("closepath", ip => ip.GS.Path.ClosePath());
        Def("flattenpath", ip => ip.GS.Path = ip.GS.Path.Flattened(Math.Max(0.1, ip.GS.Flatness * 0.5)));
        Def("reversepath", ip => ip.GS.Path = ip.GS.Path.Reversed());
        Def("strokepath", ip =>
        {
            var outline = ip.StrokeOutline();
            ip.GS.Path = outline ?? new PSPath();
        });
        Def("clippath", ip =>
        {
            ip.GS.Path = ip.GS.Clip is null ? ip.PageRectPath() : PSPath.FromSKPath(ip.GS.Clip);
        });
        Def("pathbbox", ip =>
        {
            var path = ip.GS.Path;
            if (!path.HasCurrentPoint) throw new PSError("nocurrentpoint");
            if (!ip.GS.CTM.TryInvert(out var inv)) throw new PSError("undefinedresult");
            var b = path.Transformed(inv).Bounds() ?? (0, 0, 0, 0);
            ip.Push(b.X0); ip.Push(b.Y0); ip.Push(b.X1); ip.Push(b.Y1);
        });
        Def("pathforall", ip =>
        {
            var close = ip.PopProc(); var curve = ip.PopProc(); var line = ip.PopProc(); var move = ip.PopProc();
            if (!ip.GS.CTM.TryInvert(out var inv)) throw new PSError("undefinedresult");
            var segs = ip.GS.Path.Transformed(inv).Segs.ToArray();
            ip.EStack.Add(new PathForallFrame(segs, move, line, curve, close));
        });
        Def("initclip", ip => ip.GS.Clip = null);
        Def("clip", ip => ip.ClipWith(ip.GS.Path, false));
        Def("eoclip", ip => ip.ClipWith(ip.GS.Path, true));
        Def("rectclip", ip =>
        {
            var p = ip.RectsPath(ip.PopRects());
            ip.ClipWith(p, false);
            ip.GS.Path = new PSPath();
        });
        Def("clipsave", ip => ip.GS.ClipStack.Add(ip.GS.Clip));
        Def("cliprestore", ip =>
        {
            if (ip.GS.ClipStack.Count == 0) return;
            ip.GS.Clip = ip.GS.ClipStack[^1];
            ip.GS.ClipStack.RemoveAt(ip.GS.ClipStack.Count - 1);
        });
        Def("infill", ip => ip.Push(ip.InsideTest(false, false)));
        Def("ineofill", ip => ip.Push(ip.InsideTest(true, false)));
        Def("instroke", ip => ip.Push(ip.InsideTest(false, true)));

        // ---- user paths ----
        Def("setbbox", ip => { ip.Pop(); ip.Pop(); ip.Pop(); ip.Pop(); });
        Def("ucache", ip => { });
        Def("uappend", ip => ip.AppendUserPath(ip.Pop()));
        Def("upath", ip =>
        {
            ip.PopBool();
            ip.Push(new PSArray(Array.Empty<PSObject>(), ip.AllocLevel, exec: true));
        });
        Def("ufill", ip => ip.UserPathPaint(p => ip.FillPath(false)));
        Def("ueofill", ip => ip.UserPathPaint(p => ip.FillPath(true)));
        Def("ustroke", ip =>
        {
            Matrix? m = null;
            if (ip.Peek() is PSArray a && a.Length == 6 && !a.IsExec) m = ip.PopMatrix();
            var up = ip.Pop();
            ip.GSave();
            try
            {
                ip.GS.Path = new PSPath();
                ip.AppendUserPath(up);
                if (m is { } mm) ip.GS.CTM = mm.Multiply(ip.GS.CTM);
                ip.Stroke();
            }
            finally { ip.GRestore(); }
        });
        Def("ustrokepath", ip =>
        {
            if (ip.Peek() is PSArray a && a.Length == 6 && !a.IsExec) ip.Pop();
            var up = ip.Pop();
            ip.GS.Path = new PSPath();
            ip.AppendUserPath(up);
            ip.GS.Path = ip.StrokeOutline() ?? new PSPath();
        });
        Def("inufill", ip => { ip.Pop(); ip.Pop(); ip.Push(false); });
        Def("inueofill", ip => { ip.Pop(); ip.Pop(); ip.Push(false); });
        Def("inustroke", ip => { ip.Pop(); ip.Pop(); ip.Push(false); });
    }

    internal PSPath PageRectPath()
    {
        var p = new PSPath();
        double w = Device.PixelWidth, h = Device.PixelHeight;
        p.MoveTo(0, 0); p.LineTo(w, 0); p.LineTo(w, h); p.LineTo(0, h); p.ClosePath();
        return p;
    }

    private void ArcTo(bool pushResults)
    {
        double r = PopNum(), y2 = PopNum(), x2 = PopNum(), y1 = PopNum(), x1 = PopNum();
        var path = GS.Path;
        if (!path.HasCurrentPoint) throw new PSError("nocurrentpoint");
        var inv = GS.CTM.Invert();
        var (x0, y0) = inv.Transform(path.CurX, path.CurY);
        // Unit vectors from the corner (x1,y1) towards the two other points.
        double ax = x0 - x1, ay = y0 - y1, bx = x2 - x1, by = y2 - y1;
        double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
        if (la == 0 || lb == 0 || r == 0)
        {
            var (dx, dy) = GS.CTM.Transform(x1, y1);
            path.LineTo(dx, dy);
            if (pushResults) { Push(x1); Push(y1); Push(x1); Push(y1); }
            return;
        }
        ax /= la; ay /= la; bx /= lb; by /= lb;
        double cos = ax * bx + ay * by;
        double angle = Math.Acos(Math.Clamp(cos, -1, 1));
        if (Math.Abs(Math.Sin(angle)) < 1e-9)
        {
            var (dx, dy) = GS.CTM.Transform(x1, y1);
            path.LineTo(dx, dy);
            if (pushResults) { Push(x1); Push(y1); Push(x1); Push(y1); }
            return;
        }
        double dist = r / Math.Tan(angle / 2);
        double t1x = x1 + ax * dist, t1y = y1 + ay * dist, t2x = x1 + bx * dist, t2y = y1 + by * dist;
        // Center lies along the bisector.
        double bisx = ax + bx, bisy = ay + by, bl = Math.Sqrt(bisx * bisx + bisy * bisy);
        bisx /= bl; bisy /= bl;
        double cd = r / Math.Sin(angle / 2);
        double cx = x1 + bisx * cd, cy = y1 + bisy * cd;
        double a1 = Math.Atan2(t1y - cy, t1x - cx) * 180 / Math.PI;
        double a2 = Math.Atan2(t2y - cy, t2x - cx) * 180 / Math.PI;
        // Direction: cross product sign decides clockwise.
        double cross = ax * by - ay * bx;
        var (sx, sy) = GS.CTM.Transform(t1x, t1y);
        path.LineTo(sx, sy);
        path.Arc(GS.CTM, cx, cy, r, a1, a2, cross > 0);
        if (pushResults) { Push(t1x); Push(t1y); Push(t2x); Push(t2y); }
    }

    internal void ClipWith(PSPath path, bool evenOdd)
    {
        using var sk = path.ToSKPath(evenOdd);
        SKPath result;
        if (GS.Clip is null) result = new SKPath(sk);
        else
        {
            result = GS.Clip.Op(sk, SKPathOp.Intersect) ?? new SKPath();
        }
        GS.Clip = result;
    }

    private bool InsideTest(bool evenOdd, bool stroke)
    {
        double x, y;
        if (Peek() is PSArray) { Pop(); return false; }
        y = PopNum(); x = PopNum();
        var (dx, dy) = GS.CTM.Transform(x, y);
        if (stroke)
        {
            var o = StrokeOutline();
            if (o is null) return false;
            using var sp = o.ToSKPath();
            return sp.Contains((float)dx, (float)dy);
        }
        using var p = GS.Path.ToSKPath(evenOdd);
        return p.Contains((float)dx, (float)dy);
    }

    private void AppendUserPath(PSObject up)
    {
        if (up is not PSArray a) throw new PSError("typecheck");
        if (!a.IsExec && a.Length == 2 && a[0] is PSString) throw new PSError("undefined", "encoded user paths are not supported");
        ExecuteNested(a.WithExec(true));
    }

    private void UserPathPaint(Action<PSObject> paint)
    {
        var up = Pop();
        GSave();
        try
        {
            GS.Path = new PSPath();
            AppendUserPath(up);
            paint(up);
        }
        finally { GRestore(); }
    }

    /// <summary>Rectangles for rectfill/rectstroke/rectclip: x y w h, or an array / encoded number string.</summary>
    internal List<(double X, double Y, double W, double H)> PopRects()
    {
        var list = new List<(double, double, double, double)>();
        if (Peek() is PSArray or PSString)
        {
            var nums = NumbersFrom(Pop());
            for (int i = 0; i + 3 < nums.Count; i += 4) list.Add((nums[i], nums[i + 1], nums[i + 2], nums[i + 3]));
            return list;
        }
        double h = PopNum(), w = PopNum(), y = PopNum(), x = PopNum();
        list.Add((x, y, w, h));
        return list;
    }

    internal PSPath RectsPath(List<(double X, double Y, double W, double H)> rects)
    {
        var p = new PSPath();
        var m = GS.CTM;
        foreach (var (x, y, w, h) in rects)
        {
            var a = m.Transform(x, y); var b = m.Transform(x + w, y); var c = m.Transform(x + w, y + h); var d = m.Transform(x, y + h);
            // PLRM: the rectangle is drawn counterclockwise if w and h have the same sign.
            p.MoveTo(a.X, a.Y); p.LineTo(b.X, b.Y); p.LineTo(c.X, c.Y); p.LineTo(d.X, d.Y); p.ClosePath();
        }
        return p;
    }

    /// <summary>Numbers from an array or a binary-encoded number string (PLRM 3.14.5).</summary>
    internal static List<double> NumbersFrom(PSObject o)
    {
        var list = new List<double>();
        if (o is PSArray a)
        {
            foreach (var x in a.Items) list.Add(Num(x));
            return list;
        }
        if (o is not PSString s) throw new PSError("typecheck");
        var d = s.Span;
        if (d.Length < 4 || d[0] != 149) throw new PSError("typecheck", "not an encoded number string");
        int rep = d[1];
        bool little = rep >= 128;
        int r = rep & 0x7F;
        int count = little ? d[2] | (d[3] << 8) : (d[2] << 8) | d[3];
        int pos = 4;
        Span<byte> f = stackalloc byte[4];
        for (int i = 0; i < count; i++)
        {
            if (r < 32)
            {
                // 32-bit fixed point with r fraction bits
                if (pos + 4 > d.Length) break;
                int v = little ? d[pos] | (d[pos + 1] << 8) | (d[pos + 2] << 16) | (d[pos + 3] << 24)
                               : (d[pos] << 24) | (d[pos + 1] << 16) | (d[pos + 2] << 8) | d[pos + 3];
                list.Add(v / Math.Pow(2, r));
                pos += 4;
            }
            else if (r < 48)
            {
                int sc = r - 32;
                if (pos + 2 > d.Length) break;
                short v = (short)(little ? d[pos] | (d[pos + 1] << 8) : (d[pos] << 8) | d[pos + 1]);
                list.Add(v / Math.Pow(2, sc));
                pos += 2;
            }
            else if (r == 48 || r == 49)
            {
                if (pos + 4 > d.Length) break;
                d.Slice(pos, 4).CopyTo(f);
                // 48 = IEEE in the byte order given by the representation byte, 49 = native order.
                bool le = r == 49 ? BitConverter.IsLittleEndian : little;
                if (le != BitConverter.IsLittleEndian) f.Reverse();
                list.Add(BitConverter.ToSingle(f));
                pos += 4;
            }
            else throw new PSError("typecheck", "bad number string representation");
        }
        return list;
    }

    private sealed class PathForallFrame : LoopFrame
    {
        private readonly PathSeg[] _segs;
        private readonly PSObject _move, _line, _curve, _close;
        private int _i;
        public PathForallFrame(PathSeg[] segs, PSObject move, PSObject line, PSObject curve, PSObject close)
        { _segs = segs; _move = move; _line = line; _curve = curve; _close = close; }
        public override bool Step(Interpreter ip)
        {
            if (_i >= _segs.Length) return false;
            var s = _segs[_i++];
            switch (s.Kind)
            {
                case SegKind.Move: ip.Push(s.X1); ip.Push(s.Y1); ip.ExecOperand(_move); break;
                case SegKind.Line: ip.Push(s.X1); ip.Push(s.Y1); ip.ExecOperand(_line); break;
                case SegKind.Curve:
                    ip.Push(s.X1); ip.Push(s.Y1); ip.Push(s.X2); ip.Push(s.Y2); ip.Push(s.X3); ip.Push(s.Y3);
                    ip.ExecOperand(_curve);
                    break;
                case SegKind.Close: ip.ExecOperand(_close); break;
            }
            return true;
        }
    }
}
