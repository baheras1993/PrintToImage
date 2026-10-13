using PrintToImage.PostScript.Graphics;
using SkiaSharp;

namespace PrintToImage.PostScript.Device;

/// <summary>Receives finished pages.</summary>
public interface IPageSink
{
    /// <param name="page">Page content in device pixels (origin top-left) at <paramref name="dpi"/>.</param>
    void AddPage(SKPicture page, double widthPt, double heightPt, double dpi);
    void Finish();
}

/// <summary>
/// The output device. Device space is pixels at <see cref="Dpi"/> with the origin at the top-left of the page.
/// Each page is recorded into an SKPicture and handed to the sink on showpage.
/// </summary>
public sealed class PageDevice : IDisposable
{
    public readonly double Dpi;
    public double WidthPt { get; private set; }
    public double HeightPt { get; private set; }
    public int PagesEmitted { get; private set; }
    public int MaxPages { get; set; } = 2000;
    /// <summary>PDF output: text is drawn with embedded subset fonts (built when the job ends).</summary>
    public bool VectorText => Embedder is not null;
    public Fonts.FontEmbedder? Embedder { get; }
    private readonly IPageSink _sink;
    private SKPictureRecorder? _recorder;
    private SKCanvas? _canvas;
    private Interpreter? _ip;

    public PageDevice(IPageSink sink, double dpi, double widthPt = 595, double heightPt = 842)
    {
        _sink = sink;
        if (sink is IDeferredPageSink) Embedder = new Fonts.FontEmbedder();
        Dpi = dpi;
        WidthPt = widthPt;
        HeightPt = heightPt;
    }

    internal void Attach(Interpreter ip) => _ip = ip;

    public double Scale => Dpi / 72.0;
    public int PixelWidth => Math.Max(1, (int)Math.Round(WidthPt * Scale));
    public int PixelHeight => Math.Max(1, (int)Math.Round(HeightPt * Scale));

    /// <summary>Default user space: 1/72 inch, origin bottom-left.</summary>
    public Matrix DefaultMatrix => new(Scale, 0, 0, -Scale, 0, HeightPt * Scale);

    public SKCanvas Canvas
    {
        get
        {
            if (_canvas is null)
            {
                _recorder = new SKPictureRecorder();
                _canvas = _recorder.BeginRecording(new SKRect(0, 0, PixelWidth, PixelHeight));
            }
            return _canvas;
        }
    }

    /// <summary>Replaces the current page canvas temporarily (pattern cells, Type 3 measuring).</summary>
    public SKCanvas? Override { get; set; }

    /// <summary>The canvas drawing operators paint on.</summary>
    public SKCanvas Target => Override ?? Canvas;

    public void SetPageSize(double w, double h)
    {
        if (w <= 0 || h <= 0 || w > 14400 || h > 14400) return;
        if (Math.Abs(w - WidthPt) < 0.01 && Math.Abs(h - HeightPt) < 0.01) return;
        WidthPt = w;
        HeightPt = h;
        ErasePage();
    }

    public void ErasePage()
    {
        _canvas = null;
        _recorder?.Dispose();
        _recorder = null;
    }

    public void ShowPage()
    {
        if (PagesEmitted >= MaxPages) throw new PSError("limitcheck", "too many pages");
        var canvas = Canvas;
        _ = canvas;
        if (_sink is IDeferredPageSink deferred)
        {
            // Keep text drawables live: they are rendered when the PDF is written at the end of the job.
            var drawable = _recorder!.EndRecordingAsDrawable();
            deferred.AddPage(drawable, WidthPt, HeightPt, Dpi);
        }
        else
        {
            using var pic = _recorder!.EndRecording();
            _sink.AddPage(pic, WidthPt, HeightPt, Dpi);
        }
        _recorder.Dispose();
        _recorder = null;
        _canvas = null;
        PagesEmitted++;
    }

    /// <summary>Called once after the job: builds embedded fonts, then lets the sink write its output.</summary>
    public void Finish(Action<string>? log)
    {
        Embedder?.BuildAll(log);
        _sink.Finish();
    }

    public void Dispose()
    {
        ErasePage();
        Embedder?.Dispose();
    }
}

/// <summary>A sink that receives pages as live drawables and draws them when the job is complete.</summary>
public interface IDeferredPageSink : IPageSink
{
    void AddPage(SKDrawable page, double widthPt, double heightPt, double dpi);
}

/// <summary>Writes all pages into one PDF (vector content, embedded subset fonts, images embedded losslessly).</summary>
public sealed class PdfSink : IDeferredPageSink, IDisposable
{
    private readonly SKDocument _doc;
    private readonly Stream _stream;
    private readonly List<(SKDrawable Page, double W, double H, double Dpi)> _pending = new();
    public int Pages { get; private set; }

    public void AddPage(SKDrawable page, double widthPt, double heightPt, double dpi) => _pending.Add((page, widthPt, heightPt, dpi));

    private void WritePending()
    {
        foreach (var (page, w, h, dpi) in _pending)
        {
            var canvas = _doc.BeginPage((float)w, (float)h);
            canvas.Scale((float)(72.0 / dpi));
            canvas.DrawDrawable(page, 0, 0);
            _doc.EndPage();
            page.Dispose();
            Pages++;
        }
        _pending.Clear();
    }

    public PdfSink(Stream output, string title)
    {
        _stream = output;
        _doc = SKDocument.CreatePdf(_stream, Metadata(title));
    }

    internal static SKDocumentPdfMetadata Metadata(string title) => new()
    {
        Title = title,
        Creator = "PrintToImage",
        Producer = "PrintToImage",
        Creation = DateTime.Now,
        Modified = DateTime.Now,
        RasterDpi = 300,
        EncodingQuality = 101, // lossless for images that must be re-encoded
        PdfA = false,
    };

    public void AddPage(SKPicture page, double widthPt, double heightPt, double dpi)
    {
        var canvas = _doc.BeginPage((float)widthPt, (float)heightPt);
        canvas.Scale((float)(72.0 / dpi));
        canvas.DrawPicture(page);
        _doc.EndPage();
        Pages++;
    }

    public void Finish()
    {
        WritePending();
        _doc.Close();
    }

    public void Dispose()
    {
        foreach (var p in _pending) p.Page.Dispose();
        _doc.Dispose();
    }
}

/// <summary>Writes every page as its own PDF file in a folder (page_001.pdf, page_002.pdf, ...).</summary>
public sealed class SplitPdfSink : IDeferredPageSink, IDisposable
{
    private readonly string _folder;
    private readonly string _title;
    private readonly List<(SKDrawable Page, double W, double H, double Dpi)> _pending = new();
    public readonly List<string> Files = new();

    public SplitPdfSink(string folder, string title)
    {
        _folder = folder;
        _title = title;
        Directory.CreateDirectory(folder);
    }

    public void AddPage(SKDrawable page, double widthPt, double heightPt, double dpi) => _pending.Add((page, widthPt, heightPt, dpi));

    // Only used without deferred text; kept for completeness.
    public void AddPage(SKPicture page, double widthPt, double heightPt, double dpi) =>
        Write(c => c.DrawPicture(page), widthPt, heightPt, dpi);

    private void Write(Action<SKCanvas> draw, double w, double h, double dpi)
    {
        var path = Path.Combine(_folder, $"page_{Files.Count + 1:D3}.pdf");
        using (var fs = File.Create(path))
        using (var doc = SKDocument.CreatePdf(fs, PdfSink.Metadata(_title)))
        {
            var canvas = doc.BeginPage((float)w, (float)h);
            canvas.Scale((float)(72.0 / dpi));
            draw(canvas);
            doc.EndPage();
            doc.Close();
        }
        Files.Add(path);
    }

    public void Finish()
    {
        foreach (var (page, w, h, dpi) in _pending)
        {
            Write(c => c.DrawDrawable(page, 0, 0), w, h, dpi);
            page.Dispose();
        }
        _pending.Clear();
    }

    public void Dispose()
    {
        foreach (var p in _pending) p.Page.Dispose();
        _pending.Clear();
    }
}

/// <summary>Renders each page to an image file in a folder: page_001.jpg ... (JPEG) or page_001.png ... (PNG, lossless).</summary>
public sealed class ImageFileSink : IPageSink
{
    private readonly string _folder;
    private readonly int _quality;
    private readonly bool _png;
    public readonly List<string> Files = new();

    public ImageFileSink(string folder, string format, int quality)
    {
        _folder = folder;
        _quality = quality;
        _png = format == "png";
        Directory.CreateDirectory(folder);
    }

    public void AddPage(SKPicture page, double widthPt, double heightPt, double dpi)
    {
        int w = Math.Max(1, (int)Math.Round(widthPt * dpi / 72)), h = Math.Max(1, (int)Math.Round(heightPt * dpi / 72));
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawPicture(page);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = _png ? image.Encode(SKEncodedImageFormat.Png, 100) : image.Encode(SKEncodedImageFormat.Jpeg, _quality);
        var path = Path.Combine(_folder, $"page_{Files.Count + 1:D3}.{(_png ? "png" : "jpg")}");
        using (var fs = File.Create(path)) data.SaveTo(fs);
        Files.Add(path);
    }

    public void Finish() { }
}

/// <summary>Renders pages to PNG (used by tests and diagnostics).</summary>
public sealed class PngSink : IPageSink
{
    private readonly string _folder;
    private readonly string _prefix;
    private readonly double _dpi;
    public readonly List<string> Files = new();

    public PngSink(string folder, string prefix, double outputDpi)
    {
        _folder = folder;
        _prefix = prefix;
        _dpi = outputDpi;
        Directory.CreateDirectory(folder);
    }

    public void AddPage(SKPicture page, double widthPt, double heightPt, double dpi)
    {
        int w = Math.Max(1, (int)Math.Round(widthPt * _dpi / 72)), h = Math.Max(1, (int)Math.Round(heightPt * _dpi / 72));
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            canvas.Scale((float)(_dpi / dpi));
            canvas.DrawPicture(page);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(_folder, $"{_prefix}_p{Files.Count + 1}.png");
        using (var fs = File.Create(path)) data.SaveTo(fs);
        Files.Add(path);
    }

    public void Finish() { }
}
