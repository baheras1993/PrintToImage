using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PrintToImage.PostScript;
using Xunit;

namespace PrintToImage.Tests;

/// <summary>End-to-end: a spooled job goes through archive, naming, rendering and placement.</summary>
public class ProcessorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pti-proc-" + Guid.NewGuid().ToString("N"));
    private readonly Settings _s;
    private readonly JobProcessor _processor;

    public ProcessorTests()
    {
        _s = new Settings
        {
            OutputRoot = Path.Combine(_root, "Output"),
            ArchiveFolder = Path.Combine(_root, "archive"),
            LogFolder = Path.Combine(_root, "logs"),
            WorkFolder = Path.Combine(_root, "work"),
            Devices = new() { ["10.0.0.5"] = "OCT" },
        };
        _s.Validate();
        _processor = new JobProcessor(_s, new JobQueue(), new JobLog(_s.LogFolder), NullLogger<JobProcessor>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private ReceivedJob Spool(string ps, string ip = "10.0.0.5")
    {
        var job = new ReceivedJob { Id = ReceivedJob.NewId(), SenderIp = ip, ReceivedAt = DateTime.Now };
        Directory.CreateDirectory(Path.Combine(_s.WorkFolder, "spool"));
        job.SpoolPath = Path.Combine(_s.WorkFolder, "spool", job.Id + ".prn");
        File.WriteAllBytes(job.SpoolPath, Encoding.Latin1.GetBytes(ps));
        job.Bytes = new FileInfo(job.SpoolPath).Length;
        return job;
    }

    private const string Page = "/Helvetica findfont 20 scalefont setfont 72 700 moveto (Report) show 0 0 1 setrgbcolor 72 72 200 200 rectfill showpage\n";

    [Fact]
    public void AssignedJobGoesToDeviceFolderAndIsArchived()
    {
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: Microsoft Word - 12345 OCT.docx\n" + Page));
        var pdf = Path.Combine(_s.OutputRoot, "OCT", "12345 OCT.pdf");
        Assert.True(File.Exists(pdf));
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(File.ReadAllBytes(pdf), 0, 4));
        Assert.Single(Directory.GetFiles(_s.ArchiveFolder, "*.prn", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.Combine(_s.WorkFolder, "spool")));
        var csv = File.ReadAllText(Directory.GetFiles(_s.LogFolder, "jobs-*.csv").Single());
        Assert.Contains(",12345,OK,", csv);
    }

    /// <summary>A device that names the print after the report (IOLMaster: "IOL-SRK-T") and prints the ID on the page.</summary>
    private const string IolPage = "/Helvetica findfont 10 scalefont setfont 72 760 moveto (Last name: ID: Date of birth:) show "
        + "72 745 moveto (D1935) show 72 730 moveto (K1: 43.95 D K2: 44.29 D) show showpage\n";

    [Fact]
    public void FileNumberPrintedOnThePageNamesAndFilesTheJob()
    {
        _s.PageNumberRegex = @"\b[A-Za-z]{1,2}\d{3,7}\b";
        _s.FileNameTemplate = "{page}_{name}_{date}";
        _s.Validate();
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: IOL-SRK-T\n" + IolPage));
        var files = Directory.GetFiles(Path.Combine(_s.OutputRoot, "OCT"));
        Assert.Single(files);
        Assert.Equal($"D1935_IOL-SRK-T_{DateTime.Now:yyyyMMdd}.pdf", Path.GetFileName(files[0]));
        var csv = File.ReadAllText(Directory.GetFiles(_s.LogFolder, "jobs-*.csv").Single());
        Assert.Contains(",D1935", csv);
    }

    [Fact]
    public void PageWithoutNumberKeepsTheOldRulesAndDropsTheEmptyPart()
    {
        _s.PageNumberRegex = @"\b[A-Za-z]{1,2}\d{3,7}\b";
        _s.FileNameTemplate = "{page}_{name}";
        _s.Validate();
        // no number in the title nor on the page (K1 / K2 are too short) → Unassigned, as before
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: MMT-Full\n/Helvetica findfont 10 scalefont setfont 72 700 moveto (K1: 42.99 D) show showpage\n"));
        Assert.Single(Directory.GetFiles(Path.Combine(_s.OutputRoot, "Unassigned")));
        // a number in the title, none on the page → named without the empty {page} part
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: Scan 4242\n" + Page));
        Assert.Equal("Scan 4242.pdf", Path.GetFileName(Directory.GetFiles(Path.Combine(_s.OutputRoot, "OCT")).Single()));
    }

    [Fact]
    public void JobWithoutFileNumberIsUnassigned()
    {
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: Untitled\n" + Page));
        var files = Directory.GetFiles(Path.Combine(_s.OutputRoot, "Unassigned"));
        Assert.Single(files);
        Assert.StartsWith("OCT_", Path.GetFileName(files[0]));
    }

    [Fact]
    public void UnknownSenderGoesToUnknownDevice()
    {
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: 777\n" + Page, ip: "10.9.9.9"));
        Assert.True(File.Exists(Path.Combine(_s.OutputRoot, "UnknownDevice", "777.pdf")));
    }

    [Fact]
    public void BrokenJobGoesToFailedWithRawData()
    {
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: 4444\n/Helvetica findfont 12 scalefont setfont 72 72 moveto (x) show nosuchoperator showpage\n"));
        Assert.False(Directory.Exists(Path.Combine(_s.OutputRoot, "OCT")) && Directory.GetFiles(Path.Combine(_s.OutputRoot, "OCT")).Length > 0);
        var failed = Directory.GetFiles(Path.Combine(_s.OutputRoot, "Failed"));
        Assert.Contains(failed, f => f.EndsWith(".prn"));
        var csv = File.ReadAllText(Directory.GetFiles(_s.LogFolder, "jobs-*.csv").Single());
        Assert.Contains("FAILED", csv);
        Assert.Contains("nosuchoperator", csv);
    }

    [Fact]
    public void JpgModeWritesOneFilePerPage()
    {
        _s.DeviceOutputFormat["OCT"] = "jpg";
        _s.JpgDpi = 50;
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: 9090\n" + Page + Page));
        var names = Directory.GetFiles(Path.Combine(_s.OutputRoot, "OCT")).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "9090_p1.jpg", "9090_p2.jpg" }, names);
    }

    [Fact]
    public void SharedPrinterJobIsMatchedByComputerName()
    {
        _s.Devices["PENTACAM-PC"] = "Pentacam";
        var job = Spool("%!PS-Adobe-3.0\n%%Title: 5151\n" + Page, ip: "127.0.0.1");
        job.ClientComputer = "pentacam-pc.clinic.local";   // full DNS name, other case
        job.SpoolerDocument = "5151";
        _processor.Process(job);
        Assert.True(File.Exists(Path.Combine(_s.OutputRoot, "Pentacam", "5151.pdf")));
        var csv = File.ReadAllText(Directory.GetFiles(_s.LogFolder, "jobs-*.csv").Single());
        Assert.Contains("pentacam-pc.clinic.local", csv);
    }

    [Fact]
    public void SharedPrinterJobFromUnlistedComputerIsUnknownDevice()
    {
        var job = Spool("%!PS-Adobe-3.0\n%%Title: 5252\n" + Page, ip: "10.0.0.5");   // IP is listed, but the job came via the spooler
        job.ClientComputer = "SOME-OTHER-PC";
        _processor.Process(job);
        Assert.True(File.Exists(Path.Combine(_s.OutputRoot, "UnknownDevice", "5252.pdf")));
    }

    [Fact]
    public void SpoolerDocumentNameIsUsedWhenTheJobHasNoTitle()
    {
        var job = Spool("%!PS-Adobe-3.0\n" + Page);
        job.ClientComputer = "10.0.0.5";
        job.SpoolerDocument = "Microsoft Word - 9191 OCT.docx";
        _processor.Process(job);
        Assert.True(File.Exists(Path.Combine(_s.OutputRoot, "OCT", "9191 OCT.pdf")));
    }

    [Fact]
    public void MicrosoftPsClassDriverJobUsesTheQueueDocumentName()
    {
        // Real job from the "Microsoft PS Class Driver": its own title is just "MSxpsPS".
        var job = Spool("");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ms-ps-class-driver.prn"), job.SpoolPath, overwrite: true);
        job.Bytes = new FileInfo(job.SpoolPath).Length;
        job.ClientComputer = "10.0.0.5";
        job.SpoolerDocument = "Microsoft Word - 6262 OCT macula.docx";
        _processor.Process(job);
        var pdf = Path.Combine(_s.OutputRoot, "OCT", "6262 OCT macula.pdf");
        Assert.True(File.Exists(pdf));
        var csv = File.ReadAllText(Directory.GetFiles(_s.LogFolder, "jobs-*.csv").Single());
        Assert.Contains("Windows print queue", csv);
        Assert.Contains(",2,", csv); // 2 pages
    }

    [Fact]
    public void ClassDriverPlaceholderTitleIsNotAName()
    {
        var job = Spool("");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ms-ps-class-driver.prn"), job.SpoolPath, overwrite: true);
        job.Bytes = new FileInfo(job.SpoolPath).Length;
        _processor.Process(job);
        var csv = File.ReadAllText(Directory.GetFiles(_s.LogFolder, "jobs-*.csv").Single());
        Assert.DoesNotContain("MSxpsPS", csv);
        Assert.Contains("UNASSIGNED", csv);
    }

    [Fact]
    public void PdfCombinesPagesByDefault()
    {
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: 6060\n" + Page + Page + Page));
        var files = Directory.GetFiles(Path.Combine(_s.OutputRoot, "OCT"));
        Assert.Equal("6060.pdf", Path.GetFileName(Assert.Single(files)));
    }

    [Fact]
    public void PdfOneFilePerPageWhenConfigured()
    {
        _s.PdfOneFilePerPage = true;
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: 7070\n" + Page + Page + Page));
        var names = Directory.GetFiles(Path.Combine(_s.OutputRoot, "OCT")).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "7070_p1.pdf", "7070_p2.pdf", "7070_p3.pdf" }, names);
        foreach (var f in Directory.GetFiles(Path.Combine(_s.OutputRoot, "OCT")))
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(File.ReadAllBytes(f), 0, 4));
    }

    [Fact]
    public void PdfOneFilePerPagePerDevice()
    {
        _s.DevicePdfOneFilePerPage["OCT"] = true;
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: 7171\n" + Page));
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: 7272\n" + Page + Page, ip: "10.1.1.1"));
        Assert.True(File.Exists(Path.Combine(_s.OutputRoot, "OCT", "7171.pdf")));            // single page: no _p1
        Assert.True(File.Exists(Path.Combine(_s.OutputRoot, "UnknownDevice", "7272.pdf")));  // other device: combined
    }

    [Fact]
    public void PngModeWritesLosslessPngFiles()
    {
        _s.OutputFormat = "png";
        _s.JpgDpi = 50;
        _processor.Process(Spool("%!PS-Adobe-3.0\n%%Title: 8080\n" + Page));
        var file = Path.Combine(_s.OutputRoot, "OCT", "8080.png");
        Assert.True(File.Exists(file));
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, File.ReadAllBytes(file)[..4]);
    }

    [Fact]
    public void PdfEmbedsSubsetFontsWithSearchableText()
    {
        var job = Path.Combine(_root, "t.ps");
        Directory.CreateDirectory(_root);
        File.WriteAllText(job, "%!PS\n/Helvetica findfont 24 scalefont setfont 72 700 moveto (Hello Clinic) show showpage\n");
        var pdf = Path.Combine(_root, "t.pdf");
        var r = PostScriptRenderer.Render(job, "pdf", pdf, 300, 90, "t", TimeSpan.FromSeconds(30));
        Assert.Null(r.Error);
        var bytes = File.ReadAllBytes(pdf);
        var text = Encoding.Latin1.GetString(bytes);
        Assert.Contains("/FontFile2", text);
        Assert.True(bytes.Length < 60_000, $"PDF should embed a small font subset, was {bytes.Length} bytes");
    }
}
