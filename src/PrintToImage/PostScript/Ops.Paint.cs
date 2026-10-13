using PrintToImage.PostScript.Graphics;
using SkiaSharp;

namespace PrintToImage.PostScript;

public sealed partial class Interpreter
{
    private void RegisterPaintOperators()
    {
        Def("fill", ip => { ip.FillPath(false); ip.GS.Path = new PSPath(); });
        Def("eofill", ip => { ip.FillPath(true); ip.GS.Path = new PSPath(); });
        Def("stroke", ip => { ip.Stroke(); ip.GS.Path = new PSPath(); });
        Def("rectfill", ip =>
        {
            var p = ip.RectsPath(ip.PopRects());
            ip.PaintDevicePath(p, false);
        });
        Def("rectstroke", ip =>
        {
            Matrix? m = null;
            // A 6-element array on top is a matrix (a rectangle array always has a multiple of 4 numbers).
            if (ip.Peek() is PSArray a && a.Length == 6 && !a.IsExec) m = ip.PopMatrix();
            var rects = ip.PopRects();
            var saved = ip.GS.Path;
            ip.GS.Path = ip.RectsPath(rects);
            if (m is { } mm)
            {
                var oldCtm = ip.GS.CTM;
                ip.GS.CTM = mm.Multiply(oldCtm);
                ip.Stroke();
                ip.GS.CTM = oldCtm;
            }
            else ip.Stroke();
            ip.GS.Path = saved;
        });
        Def("image", ip => ip.ImageOp(ImageKind.Image));
        Def("colorimage", ip => ip.ImageOp(ImageKind.ColorImage));
        Def("imagemask", ip => ip.ImageOp(ImageKind.Mask));
        Def("makepattern", ip =>
        {
            var m = ip.PopMatrix();
            var d = ip.PopDict();
            ip.Push(ip.MakePattern(d, m));
        });
        Def("shfill", ip => ip.ShFill(ip.PopDict()));
        Def("execform", ip => ip.ExecForm(ip.PopDict()));
    }

    // ---------------- paths ----------------

    internal bool CanPaint => !GS.NullDevice;

    internal void FillPath(bool evenOdd) => PaintDevicePath(GS.Path, evenOdd);

    internal void PaintDevicePath(PSPath path, bool evenOdd)
    {
        if (!CanPaint || path.IsEmpty) return;
        using var sk = path.ToSKPath(evenOdd);
        using var paint = MakePaint();
        DrawClipped(c => c.DrawPath(sk, paint));
    }

    /// <summary>Runs a drawing action with the current clip applied.</summary>
    internal void DrawClipped(Action<SKCanvas> draw)
    {
        var canvas = Device.Target;
        int save = canvas.Save();
        if (GS.Clip is not null) canvas.ClipPath(GS.Clip, SKClipOperation.Intersect, true);
        draw(canvas);
        canvas.RestoreToCount(save);
    }

    /// <summary>Fill paint for the current color or pattern.</summary>
    internal SKPaint MakePaint()
    {
        var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        if (GS.ColorSpace.Family == CSFamily.Pattern && GS.Pattern is { } pat)
        {
            if (pat.Shader is null) { paint.Color = SKColors.Transparent; return paint; }
            paint.Shader = pat.Shader;
            if (pat.PaintType == 2)
            {
                var (r, g, b) = GS.ColorSpace.Base is { } bcs && GS.Color.Length > 0 ? ToRgb(bcs, GS.Color) : (0d, 0d, 0d);
                paint.ColorFilter = SKColorFilter.CreateBlendMode(new SKColor((byte)(r * 255), (byte)(g * 255), (byte)(b * 255)), SKBlendMode.SrcIn);
            }
            return paint;
        }
        if (GS.ColorSpace.Family == CSFamily.Separation && GS.ColorSpace.SeparationNone)
        {
            paint.Color = SKColors.Transparent;
            return paint;
        }
        paint.Color = CurrentSKColor();
        return paint;
    }

    internal SKPaint MakeStrokePaint(out bool hairline)
    {
        var paint = MakePaint();
        paint.Style = SKPaintStyle.Stroke;
        // PostScript paints every pixel a line touches; lines thinner than one device pixel become hairlines.
        var ctm = GS.CTM;
        double devWidth = GS.LineWidth * ctm.ScaleFactor;
        hairline = devWidth < 1.0;
        paint.StrokeWidth = hairline ? 0 : (float)GS.LineWidth;
        paint.StrokeCap = GS.LineCap switch { 1 => SKStrokeCap.Round, 2 => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
        paint.StrokeJoin = GS.LineJoin switch { 1 => SKStrokeJoin.Round, 2 => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };
        paint.StrokeMiter = (float)GS.MiterLimit;
        if (GS.Dash.Length > 0)
        {
            var d = GS.Dash.Length % 2 == 0 ? GS.Dash : GS.Dash.Concat(GS.Dash).ToArray();
            var intervals = d.Select(v => (float)Math.Max(v, 0.001)).ToArray();
            double total = intervals.Sum(v => (double)v);
            if (total > 0)
            {
                float phase = (float)(GS.DashOffset % total);
                if (phase < 0) phase += (float)total;
                if (hairline)
                {
                    // Hairlines are drawn in device space, so scale the dash pattern.
                    float s = (float)ctm.ScaleFactor;
                    intervals = intervals.Select(v => v * s).ToArray();
                    phase *= s;
                }
                paint.PathEffect = SKPathEffect.CreateDash(intervals, phase);
            }
        }
        return paint;
    }

    internal void Stroke()
    {
        if (!CanPaint || GS.Path.IsEmpty) return;
        using var paint = MakeStrokePaint(out bool hairline);
        if (hairline || !GS.CTM.TryInvert(out var inv))
        {
            using var dev = GS.Path.ToSKPath();
            DrawClipped(c => c.DrawPath(dev, paint));
            return;
        }
        using var user = GS.Path.Transformed(inv).ToSKPath();
        var ctm = GS.CTM.ToSK();
        DrawClipped(c =>
        {
            c.Concat(in ctm);
            c.DrawPath(user, paint);
        });
    }

    /// <summary>The outline of the current path as stroked (device space), for strokepath.</summary>
    internal PSPath? StrokeOutline()
    {
        if (GS.Path.IsEmpty) return null;
        using var paint = MakeStrokePaint(out bool hairline);
        if (hairline) paint.StrokeWidth = (float)(1 / Math.Max(GS.CTM.ScaleFactor, 1e-9));
        if (!GS.CTM.TryInvert(out var inv)) return null;
        using var user = GS.Path.Transformed(inv).ToSKPath();
        using var outline = new SKPath();
        if (!paint.GetFillPath(user, outline)) return new PSPath();
        return PSPath.FromSKPath(outline).Transformed(GS.CTM);
    }

    // ---------------- forms ----------------

    private void ExecForm(PSDict form)
    {
        var proc = form.Get("PaintProc") ?? throw new PSError("undefined", "PaintProc");
        GSave();
        try
        {
            if (form.Get("Matrix") is PSArray m && m.Length == 6) GS.CTM = ToMatrix(m).Multiply(GS.CTM);
            if (form.Get("BBox") is PSArray bb && bb.Length == 4)
            {
                double x0 = Num(bb[0]), y0 = Num(bb[1]), x1 = Num(bb[2]), y1 = Num(bb[3]);
                ClipWith(RectsPath(new() { (x0, y0, x1 - x0, y1 - y0) }), false);
            }
            GS.Path = new PSPath();
            Push(form);
            ExecuteNested(proc);
        }
        finally { GRestore(); }
    }

    // ---------------- patterns ----------------

    internal PatternInfo PatternFromDict(PSDict d)
    {
        if (d.Get("Implementation") is PSPatternImpl impl) return impl.Info;
        // A pattern dictionary that was not created by makepattern: build it with the current CTM.
        var made = MakePattern(d, Matrix.Identity);
        return ((PSPatternImpl)made.Get("Implementation")!).Info;
    }

    private sealed class PSPatternImpl : PSObject
    {
        public readonly PatternInfo Info;
        public PSPatternImpl(PatternInfo i) => Info = i;
        public override string TypeName => "nulltype";
    }

    private PSDict MakePattern(PSDict src, Matrix m)
    {
        var d = NewDict(src.Count + 1);
        foreach (var kv in src.Map) d.Map[kv.Key] = kv.Value;
        var info = new PatternInfo { Dict = d };
        var patMatrix = m.Multiply(GS.CTM);
        int ptype = d.Get("PatternType") is { } pt ? (int)Num(pt) : 1;
        info.PatternType = ptype;

        if (ptype == 2)
        {
            info.PaintType = 1;
            if (d.Get("Shading") is PSDict sh)
            {
                info.Shader = BuildShadingShader(sh, patMatrix, out var avg);
                info.AverageColor = avg;
            }
        }
        else
        {
            info.PaintType = d.Get("PaintType") is { } pp ? (int)Num(pp) : 1;
            var bbox = d.Get("BBox") as PSArray ?? throw new PSError("rangecheck", "pattern BBox");
            double bx0 = Num(bbox[0]), by0 = Num(bbox[1]), bx1 = Num(bbox[2]), by1 = Num(bbox[3]);
            double xstep = d.Get("XStep") is { } xs ? Num(xs) : bx1 - bx0;
            double ystep = d.Get("YStep") is { } ys ? Num(ys) : by1 - by0;
            if (xstep == 0 || ystep == 0) throw new PSError("rangecheck", "pattern step");
            var proc = d.Get("PaintProc") ?? throw new PSError("undefined", "PaintProc");

            using var rec = new SKPictureRecorder();
            var cellRect = new SKRect((float)Math.Min(bx0, bx1), (float)Math.Min(by0, by1), (float)Math.Max(bx0, bx1), (float)Math.Max(by0, by1));
            var canvas = rec.BeginRecording(cellRect);
            canvas.ClipRect(cellRect);
            var prevOverride = Device.Override;
            GSave();
            try
            {
                Device.Override = canvas;
                GS.CTM = Matrix.Identity;
                GS.Clip = null;
                GS.Path = new PSPath();
                GS.NullDevice = false;
                if (info.PaintType == 2) SetColor(ColorSpace.Gray, 0);
                Push(d);
                ExecuteNested(proc);
            }
            finally
            {
                Device.Override = prevOverride;
                GRestore();
            }
            using var pic = rec.EndRecording();
            var tile = new SKRect((float)bx0, (float)by0, (float)(bx0 + Math.Abs(xstep)), (float)(by0 + Math.Abs(ystep)));
            var local = patMatrix.ToSK();
            info.Shader = SKShader.CreatePicture(pic, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKFilterMode.Linear, local, tile);
        }
        d.Map[NameAtom.Get("Implementation")] = new PSPatternImpl(info);
        d.Access = Access.ReadOnly;
        return d;
    }
}
