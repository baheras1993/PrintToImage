using PrintToImage.PostScript.Graphics;
using SkiaSharp;

namespace PrintToImage.PostScript;

public sealed partial class Interpreter
{
    internal enum ImageKind { Image, ColorImage, Mask }

    private sealed class ImageSpec
    {
        public int Width, Height, Bpc = 8, NComps = 1;
        public Matrix ImageMatrix = Matrix.Identity;
        public PSObject[] Sources = Array.Empty<PSObject>();
        public bool Multi;
        public double[]? Decode;
        public ColorSpace ColorSpace = ColorSpace.Gray;
        public bool IsMask, Polarity, Interpolate;
        public double[]? MaskColor;
    }

    private void ImageOp(ImageKind kind)
    {
        var spec = new ImageSpec();
        if (kind != ImageKind.ColorImage && Peek() is PSDict d)
        {
            Pop();
            ParseImageDict(d, spec, kind == ImageKind.Mask);
        }
        else if (kind == ImageKind.ColorImage)
        {
            int ncomp = PopInt();
            bool multi = PopBool();
            int nsrc = multi ? ncomp : 1;
            var srcs = new PSObject[nsrc];
            for (int i = nsrc - 1; i >= 0; i--) srcs[i] = Pop();
            spec.ImageMatrix = PopMatrix();
            spec.Bpc = PopInt();
            spec.Height = PopInt();
            spec.Width = PopInt();
            spec.Sources = srcs;
            spec.Multi = multi;
            spec.NComps = ncomp;
            spec.ColorSpace = ncomp switch { 1 => ColorSpace.Gray, 3 => ColorSpace.RGB, 4 => ColorSpace.CMYK, _ => throw new PSError("rangecheck") };
        }
        else
        {
            var src = Pop();
            spec.ImageMatrix = PopMatrix();
            var third = Pop();
            spec.Height = PopInt();
            spec.Width = PopInt();
            spec.Sources = new[] { src };
            if (kind == ImageKind.Mask)
            {
                spec.IsMask = true;
                spec.Bpc = 1;
                spec.Polarity = third is PSBool b ? b.Value : throw new PSError("typecheck");
            }
            else
            {
                spec.Bpc = (int)Num(third);
                spec.ColorSpace = ColorSpace.Gray;
            }
        }
        DrawImage(spec);
    }

    private void ParseImageDict(PSDict d, ImageSpec spec, bool mask)
    {
        int type = d.Get("ImageType") is { } it ? (int)Num(it) : 1;
        if (type == 3)
        {
            // Masked image: draw the data dictionary, ignoring the explicit mask.
            if (d.Get("DataDict") is PSDict dd) { ParseImageDict(dd, spec, false); return; }
            throw new PSError("rangecheck", "ImageType 3 without DataDict");
        }
        spec.Width = (int)Num(d.Get("Width") ?? throw new PSError("undefined", "Width"));
        spec.Height = (int)Num(d.Get("Height") ?? throw new PSError("undefined", "Height"));
        spec.ImageMatrix = ToMatrix(d.Get("ImageMatrix") ?? throw new PSError("undefined", "ImageMatrix"));
        spec.Bpc = mask ? 1 : (int)Num(d.Get("BitsPerComponent") ?? throw new PSError("undefined", "BitsPerComponent"));
        spec.Multi = d.Get("MultipleDataSources") is PSBool { Value: true };
        spec.Interpolate = d.Get("Interpolate") is PSBool { Value: true };
        if (d.Get("Decode") is PSArray dec) spec.Decode = dec.Items.ToArray().Select(Num).ToArray();
        if (type == 4 && d.Get("MaskColor") is PSArray mc) spec.MaskColor = mc.Items.ToArray().Select(Num).ToArray();
        var ds = d.Get("DataSource") ?? throw new PSError("undefined", "DataSource");
        if (mask)
        {
            spec.IsMask = true;
            spec.NComps = 1;
            spec.Polarity = spec.Decode is { Length: >= 2 } && spec.Decode[0] > spec.Decode[1];
            spec.Sources = new[] { ds };
            return;
        }
        spec.ColorSpace = GS.ColorSpace.Family == CSFamily.Pattern ? ColorSpace.Gray : GS.ColorSpace;
        spec.NComps = spec.ColorSpace.NComps;
        spec.Sources = spec.Multi && ds is PSArray arr && !arr.IsExec ? arr.Items.ToArray() : new[] { ds };
    }

    private void DrawImage(ImageSpec s)
    {
        if (s.Width <= 0 || s.Height <= 0) return;
        if ((long)s.Width * s.Height > 150_000_000) throw new PSError("limitcheck", "image too large");
        if (s.Bpc is not (1 or 2 or 4 or 8 or 12 or 16)) throw new PSError("rangecheck", "bits per component");

        foreach (var src in s.Sources)
            if (src is PSFile f) f.Stream.TryReset();

        // JPEG pass-through: keeps photos compact in PDF output and avoids a decode/encode cycle.
        if (!s.IsMask && s.Sources.Length == 1 && s.Sources[0] is PSFile { Stream: DctDecode dct } && s.Decode is null && s.MaskColor is null
            && s.ColorSpace.Family is CSFamily.DeviceRGB or CSFamily.DeviceGray)
        {
            var jpeg = dct.TakeRawJpeg();
            if (jpeg is not null)
            {
                using var enc = SKImage.FromEncodedData(jpeg);
                if (enc is not null && enc.Width == s.Width && enc.Height == s.Height)
                {
                    if (CanPaint) PaintImage(enc, s, null);
                    return;
                }
                // Size mismatch: decode the data we already read.
                dct.UseRawJpeg(jpeg);
            }
        }

        int nsrc = s.Multi ? s.NComps : 1;
        int compsPerSource = s.Multi ? 1 : s.NComps;
        long rowBytes = ((long)s.Width * compsPerSource * s.Bpc + 7) / 8;
        long needed = rowBytes * s.Height;
        if (needed > int.MaxValue) throw new PSError("limitcheck", "image too large");
        var data = ReadImageSources(s.Sources, nsrc, (int)needed, (int)rowBytes);

        if (!CanPaint) return;

        if (s.IsMask)
        {
            var alpha = new byte[s.Width * s.Height];
            for (int y = 0; y < s.Height; y++)
            {
                int rowOff = (int)(y * rowBytes);
                for (int x = 0; x < s.Width; x++)
                {
                    int bit = (data[0][rowOff + (x >> 3)] >> (7 - (x & 7))) & 1;
                    // polarity false: 0 bits paint; polarity true: 1 bits paint
                    alpha[y * s.Width + x] = (bit == 1) == s.Polarity ? (byte)255 : (byte)0;
                }
            }
            var info = new SKImageInfo(s.Width, s.Height, SKColorType.Alpha8, SKAlphaType.Premul);
            using var img = SKImage.FromPixelCopy(info, alpha);
            using var paint = MakePaint();
            PaintImage(img, s, paint);
            return;
        }

        using var image = BuildColorImage(s, data, (int)rowBytes);
        if (image is not null) PaintImage(image, s, null);
    }

    private byte[][] ReadImageSources(PSObject[] sources, int nsrc, int needed, int rowBytes)
    {
        var bufs = new byte[nsrc][];
        var have = new int[nsrc];
        var stringPos = new int[nsrc];
        var done = new bool[nsrc];
        for (int i = 0; i < nsrc; i++) bufs[i] = new byte[needed];

        while (true)
        {
            bool progress = false, allDone = true;
            for (int i = 0; i < nsrc; i++)
            {
                if (done[i] || have[i] >= needed) continue;
                allDone = false;
                var src = i < sources.Length ? sources[i] : sources[^1];
                switch (src)
                {
                    case PSString str:
                        {
                            if (str.Length == 0) { done[i] = true; break; }
                            // A string source is reused from the beginning as often as needed.
                            int n = Math.Min(str.Length - stringPos[i], needed - have[i]);
                            str.Span.Slice(stringPos[i], n).CopyTo(bufs[i].AsSpan(have[i]));
                            have[i] += n;
                            stringPos[i] = (stringPos[i] + n) % str.Length;
                            progress = true;
                            break;
                        }
                    case PSFile file:
                        {
                            int want = Math.Min(nsrc > 1 ? rowBytes : needed - have[i], needed - have[i]);
                            int n = file.Stream.Read(bufs[i].AsSpan(have[i], want));
                            if (n <= 0) { done[i] = true; break; }
                            have[i] += n;
                            progress = true;
                            break;
                        }
                    default:
                        {
                            var r = CallForString(src);
                            if (r is null || r.Length == 0) { done[i] = true; break; }
                            int n = Math.Min(r.Length, needed - have[i]);
                            r.Span[..n].CopyTo(bufs[i].AsSpan(have[i]));
                            have[i] += n;
                            progress = true;
                            break;
                        }
                }
            }
            if (allDone || !progress) break;
        }
        if (have.Any(h => h < needed)) Log($"Image data shorter than expected ({have.Min()} of {needed} bytes).");
        return bufs;
    }

    private SKImage? BuildColorImage(ImageSpec s, byte[][] data, int rowBytes)
    {
        int w = s.Width, h = s.Height, nc = s.NComps, bpc = s.Bpc;
        int maxVal = (1 << Math.Min(bpc, 16)) - 1;
        var cs = s.ColorSpace;

        // Decode ranges per component.
        var dmin = new double[nc];
        var dmax = new double[nc];
        for (int c = 0; c < nc; c++)
        {
            if (s.Decode is { } dec && dec.Length >= 2 * (c + 1)) { dmin[c] = dec[2 * c]; dmax[c] = dec[2 * c + 1]; }
            else if (cs.Family == CSFamily.Indexed) { dmin[c] = 0; dmax[c] = maxVal; }
            else { dmin[c] = 0; dmax[c] = 1; }
        }

        bool gray = cs.Family == CSFamily.DeviceGray && s.MaskColor is null;
        var pixels = new byte[w * h * (gray ? 1 : 4)];
        var samples = new int[nc];
        var comps = new double[nc];

        // Palette for single-component spaces with <= 8 bits: avoids per-pixel conversion.
        (byte R, byte G, byte B)[]? palette = null;
        if (nc == 1 && bpc <= 8 && !gray)
        {
            palette = new (byte, byte, byte)[maxVal + 1];
            for (int v = 0; v <= maxVal; v++)
            {
                comps[0] = dmin[0] + v * (dmax[0] - dmin[0]) / maxVal;
                var (r, g, b) = ToRgb(cs, comps);
                palette[v] = (ToByte(r), ToByte(g), ToByte(b));
            }
        }
        Dictionary<long, (byte, byte, byte)>? cache = cs.Family is CSFamily.DeviceN or CSFamily.Separation && palette is null ? new() : null;

        bool fast8 = bpc == 8 && s.Decode is null && s.MaskColor is null && !s.Multi && cs.Family is CSFamily.DeviceRGB or CSFamily.DeviceGray;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * (gray ? 1 : 4);
                if (fast8)
                {
                    int p = y * rowBytes + x * nc;
                    if (gray) pixels[o] = data[0][p];
                    else { pixels[o] = data[0][p]; pixels[o + 1] = data[0][p + 1]; pixels[o + 2] = data[0][p + 2]; pixels[o + 3] = 255; }
                    continue;
                }
                for (int c = 0; c < nc; c++)
                {
                    int src = s.Multi ? c : 0;
                    long bitPos = s.Multi ? (long)x * bpc : (long)(x * nc + c) * bpc;
                    samples[c] = ReadSample(data[src], y * rowBytes, bitPos, bpc);
                }
                byte alpha = 255;
                if (s.MaskColor is { } mcol && MatchesMaskColor(samples, mcol, nc)) alpha = 0;
                if (gray)
                {
                    double v = dmin[0] + samples[0] * (dmax[0] - dmin[0]) / maxVal;
                    pixels[o] = ToByte(v);
                    continue;
                }
                (byte R, byte G, byte B) rgb;
                if (palette is not null) rgb = palette[Math.Min(samples[0], maxVal)];
                else
                {
                    long key = 0;
                    if (cache is not null)
                    {
                        for (int c = 0; c < nc; c++) key = key * 65537 + samples[c];
                        if (cache.TryGetValue(key, out rgb)) goto store;
                    }
                    for (int c = 0; c < nc; c++) comps[c] = dmin[c] + samples[c] * (dmax[c] - dmin[c]) / maxVal;
                    var (r, g, b) = ToRgb(cs, comps);
                    rgb = (ToByte(r), ToByte(g), ToByte(b));
                    if (cache is not null && cache.Count < 65536) cache[key] = rgb;
                }
            store:
                pixels[o] = rgb.R; pixels[o + 1] = rgb.G; pixels[o + 2] = rgb.B; pixels[o + 3] = alpha;
            }
        }

        if (TransferActive)
        {
            // Transfer functions map device values; drivers use them e.g. to turn palette indices into colours.
            var lr = TransferLut(GS.TransferR) ?? IdentityLut;
            var lg = TransferLut(GS.TransferG) ?? IdentityLut;
            var lb = TransferLut(GS.TransferB) ?? IdentityLut;
            if (gray)
            {
                var rgba = new byte[w * h * 4];
                for (int i = 0, n = w * h; i < n; i++)
                {
                    byte v = pixels[i];
                    rgba[i * 4] = lr[v]; rgba[i * 4 + 1] = lg[v]; rgba[i * 4 + 2] = lb[v]; rgba[i * 4 + 3] = 255;
                }
                pixels = rgba;
                gray = false;
            }
            else
            {
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = lr[pixels[i]]; pixels[i + 1] = lg[pixels[i + 1]]; pixels[i + 2] = lb[pixels[i + 2]];
                }
            }
        }

        SKImageInfo info = gray
            ? new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque)
            : new SKImageInfo(w, h, SKColorType.Rgba8888, s.MaskColor is null ? SKAlphaType.Opaque : SKAlphaType.Unpremul);
        return SKImage.FromPixelCopy(info, pixels);
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    private static readonly byte[] IdentityLut = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    private static int ReadSample(byte[] row, int rowOffset, long bitPos, int bpc)
    {
        switch (bpc)
        {
            case 8: return row[rowOffset + bitPos / 8];
            case 16:
                {
                    int p = (int)(rowOffset + bitPos / 8);
                    return (row[p] << 8) | row[p + 1];
                }
            default:
                {
                    int v = 0;
                    for (int i = 0; i < bpc; i++)
                    {
                        long bp = bitPos + i;
                        int bit = (row[rowOffset + (int)(bp >> 3)] >> (7 - (int)(bp & 7))) & 1;
                        v = (v << 1) | bit;
                    }
                    return v;
                }
        }
    }

    private static bool MatchesMaskColor(int[] samples, double[] mask, int nc)
    {
        if (mask.Length == nc)
        {
            for (int c = 0; c < nc; c++) if (samples[c] != (int)mask[c]) return false;
            return true;
        }
        if (mask.Length >= 2 * nc)
        {
            for (int c = 0; c < nc; c++) if (samples[c] < mask[2 * c] || samples[c] > mask[2 * c + 1]) return false;
            return true;
        }
        return false;
    }

    private void PaintImage(SKImage img, ImageSpec s, SKPaint? maskPaint)
    {
        if (!s.ImageMatrix.TryInvert(out var inv)) return;
        var m = inv.Multiply(GS.CTM);
        double scale = Math.Sqrt(Math.Abs(m.Determinant));
        bool axisAligned = Math.Abs(m.B) < 1e-9 && Math.Abs(m.C) < 1e-9 || Math.Abs(m.A) < 1e-9 && Math.Abs(m.D) < 1e-9;
        SKSamplingOptions sampling;
        if (scale < 0.999) sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        else if (s.Interpolate || !s.IsMask && scale > 1.5 && img.Width > 64 && img.Height > 64) sampling = new SKSamplingOptions(SKFilterMode.Linear);
        else sampling = new SKSamplingOptions(SKFilterMode.Nearest);
        var sk = m.ToSK();
        using var paint = maskPaint is null ? new SKPaint() : null;
        var p = maskPaint ?? paint!;
        p.IsAntialias = !axisAligned;
        DrawClipped(c =>
        {
            c.Concat(in sk);
            c.DrawImage(img, 0, 0, sampling, p);
        });
    }
}
