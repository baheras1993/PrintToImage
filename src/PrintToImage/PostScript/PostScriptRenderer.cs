using PrintToImage.PostScript.Device;

namespace PrintToImage.PostScript;

public enum JobLanguage { PostScript, Pdf, Unknown }

public sealed class RenderResult
{
    public int Pages;
    /// <summary>Output files in page order (one PDF, or one JPG per page).</summary>
    public List<string> Files = new();
    public List<string> Messages = new();
    /// <summary>The text painted on the pages (empty for a job received as PDF).</summary>
    public string Text = "";
    /// <summary>Set when the job did not complete; pages rendered before the error may still exist.</summary>
    public string? Error;
}

/// <summary>Converts a received print job (PostScript, optionally wrapped in PJL) to PDF or JPG files.</summary>
public static class PostScriptRenderer
{
    /// <summary>Finds the page-description data inside a job: skips PJL/UEL headers and Ctrl-D, trims the trailing UEL.</summary>
    public static (long Start, long Length, JobLanguage Language) Locate(string jobPath)
    {
        using var fs = File.OpenRead(jobPath);
        long size = fs.Length;
        var head = new byte[(int)Math.Min(size, 1 << 20)];
        int n = fs.Read(head, 0, head.Length);

        long start = -1;
        var lang = JobLanguage.Unknown;
        for (int i = 0; i < n - 1; i++)
        {
            if (head[i] == '%' && head[i + 1] == '!' && (i == 0 || IsLineStartOrCtrl(head[i - 1]))) { start = i; lang = JobLanguage.PostScript; break; }
            if (head[i] == '%' && i + 4 < n && head[i + 1] == 'P' && head[i + 2] == 'D' && head[i + 3] == 'F' && head[i + 4] == '-')
            { start = i; lang = JobLanguage.Pdf; break; }
        }
        if (start < 0)
        {
            // Some drivers omit the %! line after "@PJL ENTER LANGUAGE = POSTSCRIPT".
            var text = System.Text.Encoding.Latin1.GetString(head, 0, n);
            int enter = text.IndexOf("ENTER LANGUAGE", StringComparison.OrdinalIgnoreCase);
            if (enter >= 0 && text.IndexOf("POSTSCRIPT", enter, StringComparison.OrdinalIgnoreCase) is var ps && ps > 0)
            {
                int eol = text.IndexOfAny(new[] { '\n', '\r' }, ps);
                if (eol > 0) { start = eol + 1; lang = JobLanguage.PostScript; }
            }
        }
        if (start < 0) return (0, size, JobLanguage.Unknown);

        // End: the last UEL (ESC%-12345X) after the start, else end of file. Trailing Ctrl-D is skipped by the scanner.
        long end = size;
        int tailLen = (int)Math.Min(size - start, 64 * 1024);
        var tail = new byte[tailLen];
        fs.Seek(size - tailLen, SeekOrigin.Begin);
        fs.ReadExactly(tail, 0, tailLen);
        var uel = "\u001b%-12345X"u8;
        int idx = tail.AsSpan().IndexOf(uel);
        if (idx >= 0 && size - tailLen + idx > start) end = size - tailLen + idx;
        return (start, end - start, lang);
    }

    private static bool IsLineStartOrCtrl(byte b) => b is (byte)'\n' or (byte)'\r' or 4 or (byte)'X' or 0x1B or (byte)' ';

    /// <param name="format">"pdf", "jpg" or "png".</param>
    /// <param name="outputPath">PDF: the file to write. JPG/PNG, or PDF with <paramref name="pdfPerPage"/>: a folder for page_001.* ...</param>
    /// <param name="pdfPerPage">PDF only: write each page as its own PDF file.</param>
    public static RenderResult Render(string jobPath, string format, string outputPath, int jpgDpi, int jpgQuality,
        string title, TimeSpan timeout, Action<string>? log = null, bool pdfPerPage = false)
    {
        var result = new RenderResult();
        void Log(string m) { result.Messages.Add(m); log?.Invoke(m); }

        var (start, length, lang) = Locate(jobPath);
        if (lang == JobLanguage.Pdf)
        {
            if (format != "pdf")
            {
                result.Error = "The job is a PDF; PDF jobs can only be saved in PDF format (set the device's output format to pdf).";
                return result;
            }
            var target = outputPath;
            if (pdfPerPage)
            {
                // A received PDF is not split into pages; it is saved as one file.
                Directory.CreateDirectory(outputPath);
                target = Path.Combine(outputPath, "page_001.pdf");
            }
            using var src = File.OpenRead(jobPath);
            src.Seek(start, SeekOrigin.Begin);
            using var dst = File.Create(target);
            CopyBytes(src, dst, length);
            result.Files.Add(target);
            result.Pages = -1;
            Log("Job is PDF; saved as received" + (pdfPerPage ? " (not split into pages)." : "."));
            return result;
        }
        if (lang == JobLanguage.Unknown)
        {
            result.Error = "The job is not PostScript (the printer driver on the device must be a PostScript driver).";
            return result;
        }

        using var cts = new CancellationTokenSource(timeout);
        var thread = new Thread(() => RunInterpreter(jobPath, start, length, format, outputPath, jpgDpi, jpgQuality, title, pdfPerPage, result, Log, cts.Token), 64 * 1024 * 1024)
        {
            IsBackground = true,
            Name = "PostScript interpreter",
        };
        thread.Start();
        if (!thread.Join(timeout + TimeSpan.FromSeconds(30)))
        {
            result.Error ??= "Rendering did not finish in time.";
        }
        return result;
    }

    private static void RunInterpreter(string jobPath, long start, long length, string format, string outputPath, int jpgDpi, int jpgQuality,
        string title, bool pdfPerPage, RenderResult result, Action<string> log, CancellationToken cancel)
    {
        PdfSink? pdf = null;
        SplitPdfSink? splitPdf = null;
        ImageFileSink? jpg = null;
        FileStream? pdfStream = null;
        try
        {
            IPageSink sink;
            double dpi;
            if (format == "pdf" && pdfPerPage)
            {
                splitPdf = new SplitPdfSink(outputPath, title);
                sink = splitPdf;
                dpi = 600;
            }
            else if (format == "pdf")
            {
                pdfStream = File.Create(outputPath);
                pdf = new PdfSink(pdfStream, title);
                sink = pdf;
                dpi = 600; // device resolution used for recording; the PDF itself is vector
            }
            else
            {
                jpg = new ImageFileSink(outputPath, format, jpgQuality);
                sink = jpg;
                dpi = jpgDpi;
            }

            using var device = new PageDevice(sink, dpi);
            using var fs = new FileStream(jobPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            fs.Seek(start, SeekOrigin.Begin);
            var input = new DotNetStreamSource(new BoundedStream(fs, length));
            var ip = new Interpreter(device, log, cancel);
            try
            {
                ip.RunJob(input);
            }
            catch (PSJobError e)
            {
                result.Error = e.Message;
            }
            catch (OperationCanceledException)
            {
                result.Error = "Rendering timed out.";
            }
            result.Pages = device.PagesEmitted;
            result.Text = device.Text;
            if (result.Pages == 0 && result.Error is null) result.Error = "The job produced no pages.";
            device.Finish(log);
            if (pdf is not null) result.Files.Add(outputPath);
            if (jpg is not null) result.Files.AddRange(jpg.Files);
            if (splitPdf is not null) result.Files.AddRange(splitPdf.Files);
        }
        catch (Exception e)
        {
            result.Error = "Renderer failure: " + e.Message;
            log("Renderer exception: " + e);
        }
        finally
        {
            pdf?.Dispose();
            splitPdf?.Dispose();
            pdfStream?.Dispose();
        }
    }

    private static void CopyBytes(Stream src, Stream dst, long count)
    {
        var buf = new byte[81920];
        while (count > 0)
        {
            int n = src.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) break;
            dst.Write(buf, 0, n);
            count -= n;
        }
    }

    /// <summary>Read-only view of the next N bytes of a stream.</summary>
    private sealed class BoundedStream : Stream
    {
        private readonly Stream _s;
        private long _left;
        public BoundedStream(Stream s, long length) { _s = s; _left = length; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) return 0;
            int n = _s.Read(buffer, offset, (int)Math.Min(count, _left));
            _left -= n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }
}
