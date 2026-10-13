using SkiaSharp;

namespace PrintToImage.PostScript.Graphics;

public enum SegKind : byte { Move, Line, Curve, Close }

public struct PathSeg
{
    public SegKind Kind;
    public double X1, Y1, X2, Y2, X3, Y3; // Move/Line use X1,Y1; Curve uses all three points
}

/// <summary>A PostScript path in device space.</summary>
public sealed class PSPath
{
    public readonly List<PathSeg> Segs;
    public bool HasCurrentPoint;
    public double CurX, CurY, StartX, StartY;

    public PSPath() => Segs = new List<PathSeg>();
    private PSPath(PSPath p)
    {
        Segs = new List<PathSeg>(p.Segs);
        HasCurrentPoint = p.HasCurrentPoint;
        CurX = p.CurX; CurY = p.CurY; StartX = p.StartX; StartY = p.StartY;
    }

    public PSPath Clone() => new(this);
    public bool IsEmpty => Segs.Count == 0;

    public void Clear()
    {
        Segs.Clear();
        HasCurrentPoint = false;
    }

    public void MoveTo(double x, double y)
    {
        // Consecutive movetos: the last one wins.
        if (Segs.Count > 0 && Segs[^1].Kind == SegKind.Move) Segs.RemoveAt(Segs.Count - 1);
        Segs.Add(new PathSeg { Kind = SegKind.Move, X1 = x, Y1 = y });
        CurX = StartX = x; CurY = StartY = y;
        HasCurrentPoint = true;
    }

    private void EnsureSubpath()
    {
        if (!HasCurrentPoint) throw new PSError("nocurrentpoint");
        if (Segs.Count == 0 || Segs[^1].Kind == SegKind.Close)
        {
            Segs.Add(new PathSeg { Kind = SegKind.Move, X1 = CurX, Y1 = CurY });
            StartX = CurX; StartY = CurY;
        }
    }

    public void LineTo(double x, double y)
    {
        EnsureSubpath();
        Segs.Add(new PathSeg { Kind = SegKind.Line, X1 = x, Y1 = y });
        CurX = x; CurY = y;
    }

    public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        EnsureSubpath();
        Segs.Add(new PathSeg { Kind = SegKind.Curve, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, X3 = x3, Y3 = y3 });
        CurX = x3; CurY = y3;
    }

    public void ClosePath()
    {
        if (!HasCurrentPoint || Segs.Count == 0 || Segs[^1].Kind == SegKind.Close) return;
        Segs.Add(new PathSeg { Kind = SegKind.Close });
        CurX = StartX; CurY = StartY;
    }

    public void Append(PSPath other)
    {
        foreach (var s in other.Segs) Segs.Add(s);
        if (other.HasCurrentPoint)
        {
            HasCurrentPoint = true;
            CurX = other.CurX; CurY = other.CurY; StartX = other.StartX; StartY = other.StartY;
        }
    }

    public PSPath Transformed(in Matrix m)
    {
        var p = new PSPath();
        foreach (var s in Segs)
        {
            var n = s;
            (n.X1, n.Y1) = m.Transform(s.X1, s.Y1);
            (n.X2, n.Y2) = m.Transform(s.X2, s.Y2);
            (n.X3, n.Y3) = m.Transform(s.X3, s.Y3);
            p.Segs.Add(n);
        }
        p.HasCurrentPoint = HasCurrentPoint;
        (p.CurX, p.CurY) = m.Transform(CurX, CurY);
        (p.StartX, p.StartY) = m.Transform(StartX, StartY);
        return p;
    }

    public SKPath ToSKPath(bool evenOdd = false)
    {
        var p = new SKPath { FillType = evenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
        foreach (var s in Segs)
        {
            switch (s.Kind)
            {
                case SegKind.Move: p.MoveTo((float)s.X1, (float)s.Y1); break;
                case SegKind.Line: p.LineTo((float)s.X1, (float)s.Y1); break;
                case SegKind.Curve: p.CubicTo((float)s.X1, (float)s.Y1, (float)s.X2, (float)s.Y2, (float)s.X3, (float)s.Y3); break;
                case SegKind.Close: p.Close(); break;
            }
        }
        return p;
    }

    public static PSPath FromSKPath(SKPath sk)
    {
        var p = new PSPath();
        using var it = sk.CreateRawIterator();
        var pts = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move: p.MoveTo(pts[0].X, pts[0].Y); break;
                case SKPathVerb.Line: p.LineTo(pts[1].X, pts[1].Y); break;
                case SKPathVerb.Quad:
                    {
                        // Elevate quadratic to cubic.
                        double x0 = pts[0].X, y0 = pts[0].Y, qx = pts[1].X, qy = pts[1].Y, x3 = pts[2].X, y3 = pts[2].Y;
                        p.CurveTo(x0 + 2.0 / 3 * (qx - x0), y0 + 2.0 / 3 * (qy - y0), x3 + 2.0 / 3 * (qx - x3), y3 + 2.0 / 3 * (qy - y3), x3, y3);
                        break;
                    }
                case SKPathVerb.Conic:
                    {
                        var quads = SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], it.ConicWeight(), 2);
                        for (int i = 0; i + 2 < quads.Length; i += 2)
                        {
                            double x0 = quads[i].X, y0 = quads[i].Y, qx = quads[i + 1].X, qy = quads[i + 1].Y, x3 = quads[i + 2].X, y3 = quads[i + 2].Y;
                            p.CurveTo(x0 + 2.0 / 3 * (qx - x0), y0 + 2.0 / 3 * (qy - y0), x3 + 2.0 / 3 * (qx - x3), y3 + 2.0 / 3 * (qy - y3), x3, y3);
                        }
                        break;
                    }
                case SKPathVerb.Cubic: p.CurveTo(pts[1].X, pts[1].Y, pts[2].X, pts[2].Y, pts[3].X, pts[3].Y); break;
                case SKPathVerb.Close: p.ClosePath(); break;
            }
        }
        return p;
    }

    /// <summary>Bounding box of all points (control points included), or null if empty.</summary>
    public (double X0, double Y0, double X1, double Y1)? Bounds()
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        bool any = false;
        void Add(double x, double y) { any = true; if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y; }
        foreach (var s in Segs)
        {
            switch (s.Kind)
            {
                case SegKind.Move:
                case SegKind.Line: Add(s.X1, s.Y1); break;
                case SegKind.Curve: Add(s.X1, s.Y1); Add(s.X2, s.Y2); Add(s.X3, s.Y3); break;
            }
        }
        if (!any && HasCurrentPoint) Add(CurX, CurY);
        return any || HasCurrentPoint ? (x0, y0, x1, y1) : null;
    }

    /// <summary>Replaces curves by line segments.</summary>
    public PSPath Flattened(double tolerance = 0.25)
    {
        var p = new PSPath();
        double cx = 0, cy = 0;
        foreach (var s in Segs)
        {
            switch (s.Kind)
            {
                case SegKind.Move: p.MoveTo(s.X1, s.Y1); cx = s.X1; cy = s.Y1; break;
                case SegKind.Line: p.LineTo(s.X1, s.Y1); cx = s.X1; cy = s.Y1; break;
                case SegKind.Close: p.ClosePath(); cx = p.CurX; cy = p.CurY; break;
                case SegKind.Curve:
                    {
                        double len = Dist(cx, cy, s.X1, s.Y1) + Dist(s.X1, s.Y1, s.X2, s.Y2) + Dist(s.X2, s.Y2, s.X3, s.Y3);
                        int n = Math.Clamp((int)Math.Ceiling(Math.Sqrt(len / Math.Max(tolerance, 0.01))), 1, 200);
                        for (int i = 1; i <= n; i++)
                        {
                            double t = (double)i / n, mt = 1 - t;
                            double x = mt * mt * mt * cx + 3 * mt * mt * t * s.X1 + 3 * mt * t * t * s.X2 + t * t * t * s.X3;
                            double y = mt * mt * mt * cy + 3 * mt * mt * t * s.Y1 + 3 * mt * t * t * s.Y2 + t * t * t * s.Y3;
                            p.LineTo(x, y);
                        }
                        cx = s.X3; cy = s.Y3;
                        break;
                    }
            }
        }
        p.HasCurrentPoint = HasCurrentPoint;
        p.CurX = CurX; p.CurY = CurY;
        return p;
    }

    private static double Dist(double x0, double y0, double x1, double y1) => Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));

    /// <summary>Reverses the direction of each subpath.</summary>
    public PSPath Reversed()
    {
        var result = new PSPath();
        int i = 0;
        while (i < Segs.Count)
        {
            if (Segs[i].Kind != SegKind.Move) { i++; continue; }
            int start = i++;
            while (i < Segs.Count && Segs[i].Kind != SegKind.Move) i++;
            var sub = Segs.GetRange(start, i - start);
            bool closed = sub[^1].Kind == SegKind.Close;
            if (closed) sub.RemoveAt(sub.Count - 1);
            // collect end points
            var pts = new List<(double X, double Y)>();
            foreach (var s in sub) pts.Add(s.Kind == SegKind.Curve ? (s.X3, s.Y3) : (s.X1, s.Y1));
            result.MoveTo(pts[^1].X, pts[^1].Y);
            for (int k = sub.Count - 1; k >= 1; k--)
            {
                var s = sub[k];
                var prev = pts[k - 1];
                if (s.Kind == SegKind.Curve) result.CurveTo(s.X2, s.Y2, s.X1, s.Y1, prev.X, prev.Y);
                else result.LineTo(prev.X, prev.Y);
            }
            if (closed) result.ClosePath();
        }
        result.HasCurrentPoint = HasCurrentPoint;
        if (result.Segs.Count == 0 && HasCurrentPoint) { result.CurX = CurX; result.CurY = CurY; }
        return result;
    }

    /// <summary>Adds an arc (PostScript arc/arcn semantics) in user space, transformed by ctm.</summary>
    public void Arc(in Matrix ctm, double cx, double cy, double r, double a1, double a2, bool clockwise)
    {
        if (clockwise) { while (a2 > a1) a2 -= 360; }
        else { while (a2 < a1) a2 += 360; }
        double sweep = a2 - a1;
        var (sx, sy) = ctm.Transform(cx + r * Math.Cos(Rad(a1)), cy + r * Math.Sin(Rad(a1)));
        if (HasCurrentPoint) LineTo(sx, sy); else MoveTo(sx, sy);
        if (sweep == 0) return;
        int n = (int)Math.Ceiling(Math.Abs(sweep) / 90 - 1e-9);
        if (n < 1) n = 1;
        double step = sweep / n;
        for (int i = 0; i < n; i++)
        {
            double t0 = Rad(a1 + i * step), t1 = Rad(a1 + (i + 1) * step);
            double k = 4.0 / 3.0 * Math.Tan((t1 - t0) / 4);
            double x0 = Math.Cos(t0), y0 = Math.Sin(t0), x3 = Math.Cos(t1), y3 = Math.Sin(t1);
            double x1 = x0 - k * y0, y1 = y0 + k * x0, x2 = x3 + k * y3, y2 = y3 - k * x3;
            var p1 = ctm.Transform(cx + r * x1, cy + r * y1);
            var p2 = ctm.Transform(cx + r * x2, cy + r * y2);
            var p3 = ctm.Transform(cx + r * x3, cy + r * y3);
            CurveTo(p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y);
        }
    }

    private static double Rad(double d) => d * Math.PI / 180;
}
