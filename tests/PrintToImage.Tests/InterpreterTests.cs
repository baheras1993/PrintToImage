using System.Text;
using PrintToImage.PostScript;
using PrintToImage.PostScript.Device;
using SkiaSharp;
using Xunit;

namespace PrintToImage.Tests;

public class InterpreterTests
{
    private sealed class CaptureSink : IPageSink
    {
        public readonly List<SKBitmap> Pages = new();
        public void AddPage(SKPicture page, double widthPt, double heightPt, double dpi)
        {
            var bmp = new SKBitmap((int)Math.Round(widthPt * dpi / 72), (int)Math.Round(heightPt * dpi / 72));
            using var c = new SKCanvas(bmp);
            c.Clear(SKColors.White);
            c.DrawPicture(page);
            Pages.Add(bmp);
        }
        public void Finish() { }
    }

    /// <summary>Runs a program and returns the operand stack (bottom first) as strings.</summary>
    private static (List<string> Stack, CaptureSink Sink) Run(string ps, double dpi = 72)
    {
        var sink = new CaptureSink();
        var dev = new PageDevice(sink, dpi, 100, 100);
        var ip = new Interpreter(dev);
        ip.RunJob(new BytesSource(Encoding.Latin1.GetBytes(ps)));
        var stack = Enumerable.Range(0, ip.Count).Select(i => ip.StackItem(i).ToString() ?? "").ToList();
        return (stack, sink);
    }

    private static string Top(string ps) => Run(ps).Stack.Last();

    [Theory]
    [InlineData("3 4 add", "7")]
    [InlineData("10 3 idiv", "3")]
    [InlineData("10 3 mod", "1")]
    [InlineData("7 2 div", "3.5")]
    [InlineData("2147483647 1 add type", "realtype")]
    [InlineData("16#FF", "255")]
    [InlineData("8#17", "15")]
    [InlineData("-3.7 round", "-4.0")]
    [InlineData("1 2 3 3 1 roll count", "3")]
    [InlineData("(abc) length", "3")]
    [InlineData("[1 2 3] length", "3")]
    [InlineData("0 1 1 4 { add } for", "10")]
    [InlineData("0 [1 2 3] { add } forall", "6")]
    [InlineData("0 { 1 add dup 5 eq { exit } if } loop", "5")]
    [InlineData("{ undefinedthing } stopped", "true")]
    [InlineData("{ 1 2 } stopped", "false")]
    [InlineData("/x 1 def save /x 2 def restore x", "1")]
    [InlineData("<< /a 1 /b 2 >> /b get", "2")]
    [InlineData("(hello world) (world) search pop exch pop exch pop", "hello ")]
    [InlineData("(3.25) cvr 2 mul", "6.5")]
    [InlineData("/Times-Roman findfont /FontName get", "Times-Roman")]
    [InlineData("12 (xxxx) cvs", "12")]
    [InlineData("currentfile 5 string readstring ABCDE pop", "ABCDE")]
    [InlineData("1 2 3 [ 4 5 ] aload length", "2")]
    [InlineData("/a [1 2 3] def a 1 9 put a 1 get", "9")]
    [InlineData("true { 2 } if", "2")]
    [InlineData("matrix currentmatrix 0 get", "1.0")]
    public void Evaluates(string program, string expected) => Assert.Equal(expected, Top(program));

    [Fact]
    public void SaveRestoreUndoesArrayAndStringChanges()
    {
        var r = Run("/a [1 2] def /s (ab) def save a 0 9 put s 0 88 put restore a 0 get s");
        Assert.Equal(new[] { "1", "ab" }, r.Stack);
    }

    [Fact]
    public void ErrorRestoresOperandsBeforeStop()
    {
        // The failing operator's operands are back on the stack when "stopped" returns true.
        var r = Run("{ 1 (x) add } stopped");
        Assert.Equal(new[] { "1", "x", "true" }, r.Stack);
    }

    [Fact]
    public void FillsRectangleInRequestedColor()
    {
        var (_, sink) = Run("1 0 0 setrgbcolor 10 10 50 50 rectfill showpage");
        var bmp = Assert.Single(sink.Pages);
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(30, 100 - 30));   // inside (y flipped)
        Assert.Equal(SKColors.White, bmp.GetPixel(80, 100 - 80));          // outside
    }

    [Fact]
    public void ClipLimitsPainting()
    {
        var (_, sink) = Run("0 0 50 100 rectclip 0 setgray 0 0 100 100 rectfill showpage");
        var bmp = sink.Pages[0];
        Assert.Equal(SKColors.Black, bmp.GetPixel(20, 50));
        Assert.Equal(SKColors.White, bmp.GetPixel(80, 50));
    }

    [Fact]
    public void ImageOperatorDrawsSamples()
    {
        // 2x1 image: black, white, scaled to 100x100
        var (_, sink) = Run("100 100 scale 2 1 8 [2 0 0 1 0 0] <00FF> image showpage");
        var bmp = sink.Pages[0];
        Assert.Equal(SKColors.Black, bmp.GetPixel(25, 50));
        Assert.Equal(SKColors.White, bmp.GetPixel(75, 50));
    }

    [Fact]
    public void ShowDrawsTextWithSubstitutedFont()
    {
        var (_, sink) = Run("/Helvetica findfont 40 scalefont setfont 10 30 moveto (HI) show showpage");
        var bmp = sink.Pages[0];
        int dark = 0;
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++)
                if (bmp.GetPixel(x, y).Red < 100) dark++;
        Assert.True(dark > 100, "text should produce dark pixels");
    }

    [Fact]
    public void CountsPages()
    {
        var (_, sink) = Run("showpage showpage showpage");
        Assert.Equal(3, sink.Pages.Count);
    }

    [Fact]
    public void ColourDriverPaletteImageKeepsItsColours()
    {
        // Real job from "MS Publisher Color Printer" (Windows 11): a palette image drawn through transfer functions,
        // with three-channel data procedures that share one string. The "scan" must stay orange, not grey/cyan/red.
        var job = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ms-publisher-color.prn");
        var dir = Path.Combine(Path.GetTempPath(), "pti-colour-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = PostScriptRenderer.Render(job, "png", dir, 50, 90, "t", TimeSpan.FromSeconds(60));
            Assert.Null(r.Error);
            using var bmp = SKBitmap.Decode(Assert.Single(r.Files));
            var c = bmp.GetPixel(105, 160);   // centre of the scan image (2.1 x 3.2 inch at 50 dpi)
            Assert.True(c.Red > 180 && c.Green is > 70 and < 170 && c.Blue < 90, $"expected orange, got {c}");
            var header = bmp.GetPixel(200, 38);  // blue title bar
            Assert.True(header.Blue > header.Red + 40, $"expected blue header, got {header}");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void LocatesPostScriptInsidePjl()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.Latin1.GetBytes("\u001b%-12345X@PJL JOB NAME=\"x\"\r\n@PJL ENTER LANGUAGE=POSTSCRIPT\r\n%!PS-Adobe-3.0\r\nshowpage\r\n\u0004\u001b%-12345X@PJL EOJ\r\n\u001b%-12345X"));
            var (start, length, lang) = PostScriptRenderer.Locate(path);
            Assert.Equal(JobLanguage.PostScript, lang);
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(path), (int)start, (int)length);
            Assert.StartsWith("%!PS-Adobe-3.0", text);
            Assert.DoesNotContain("EOJ", text);
        }
        finally { File.Delete(path); }
    }
}
