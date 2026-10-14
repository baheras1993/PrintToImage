using System.Text;
using Xunit;

namespace PrintToImage.Tests;

public class JobNameParserTests
{
    private static JobHeaderInfo Parse(string text, int cp = 1252) => JobNameParser.Parse(Encoding.Latin1.GetBytes(text), cp);

    [Fact]
    public void ReadsPjlJobName()
    {
        var info = Parse("\u001b%-12345X@PJL JOB NAME=\"Microsoft Word - 12345.docx\"\r\n@PJL ENTER LANGUAGE=POSTSCRIPT\r\n%!PS-Adobe-3.0\r\n%%Title: Other\r\n");
        Assert.Equal("Microsoft Word - 12345.docx", info.PjlJobName);
        Assert.Equal("Other", info.PostScriptTitle);
    }

    [Fact]
    public void ReadsPjlWithSpacesAroundEquals()
    {
        var info = Parse("@PJL JOB NAME = \"Report 778\" DISPLAY = \"x\"\r\n");
        Assert.Equal("Report 778", info.PjlJobName);
    }

    [Fact]
    public void ReadsPlainTitle()
    {
        Assert.Equal("Microsoft Word - buffercache_description.doc",
            Parse("%!PS-Adobe-3.0\r\n%%Title: Microsoft Word - buffercache_description.doc\r\n%%Creator: PScript5.dll").PostScriptTitle);
    }

    [Fact]
    public void ReadsParenthesizedTitleWithEscapes()
    {
        Assert.Equal("Scan (left) \\ 123", Parse("%!PS\n%%Title: (Scan \\(left\\) \\\\ 123)\n").PostScriptTitle);
    }

    [Fact]
    public void DecodesOctalUtf8InTitle()
    {
        // "Café 12" in UTF-8, written with octal escapes
        Assert.Equal("Café 12", Parse("%!PS\n%%Title: (Caf\\303\\251 12)\n").PostScriptTitle);
    }

    [Fact]
    public void DecodesHexTitleInAnsiCodePage()
    {
        // Real PScript5 output: Cyrillic title in code page 1251 as a hex string.
        var info = Parse("%!PS-Adobe-3.0\r\n%%Title: <4B4F4D504153202D20D8F2F3F6E5F020F7E5F0F2E5E62E636477>\r\n", 1251);
        Assert.Equal("KOMPAS - Штуцер чертеж.cdw", info.PostScriptTitle);
    }

    [Fact]
    public void DecodesUtf16HexTitle()
    {
        var hex = "FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("مريض 4521"));
        Assert.Equal("مريض 4521", Parse($"%!PS\n%%Title: <{hex}>\n").PostScriptTitle);
    }

    [Fact]
    public void ArabicTitleInCodePage1256()
    {
        var bytes = Encoding.GetEncoding(1256).GetBytes("تقرير 9876");
        var job = Encoding.Latin1.GetBytes("%!PS\n%%Title: ").Concat(bytes).Concat(Encoding.Latin1.GetBytes("\n")).ToArray();
        Assert.Equal("تقرير 9876", JobNameParser.Parse(job, 1256).PostScriptTitle);
    }

    [Fact]
    public void JoinsContinuationLines()
    {
        Assert.Equal("Long name 555", Parse("%!PS\n%%Title: Long \n%%+ name 555\n").PostScriptTitle?.Replace("  ", " "));
    }

    [Fact]
    public void PlainPathWithDigitsIsNotTreatedAsOctal()
    {
        Assert.Equal(@"D:\2023\12345.pdf", Parse("%!PS\n%%Title: D:\\2023\\12345.pdf\n").PostScriptTitle);
    }

    [Fact]
    public void NoNameFound()
    {
        var info = Parse("%!PS-Adobe-3.0\n%%Creator: x\n");
        Assert.Null(info.PjlJobName);
        Assert.Null(info.PostScriptTitle);
    }
}

public class JobNameResolverTests
{
    private static Settings S(string? regex = null)
    {
        var s = new Settings();
        if (regex is not null) s.FileNumberRegex = regex;
        s.Validate();
        return s;
    }

    [Theory]
    [InlineData("Microsoft Word - 12345 OCT.docx", "12345 OCT")]
    [InlineData("Microsoft Excel - 777.xlsx", "777")]
    [InlineData("Report 555.pdf - Adobe Acrobat Reader DC", "Report 555")]
    [InlineData("12345.pdf.pdf", "12345")]
    [InlineData("C:\\Exports\\Patient 4411.pdf", "Patient 4411")]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j 9", "a_b_c_d_e_f_g_h_i_j 9")]
    [InlineData("CON", "_CON")]
    [InlineData("  Name   with   spaces  42 ", "Name with spaces 42")]
    [InlineData("Ends with dots 33...", "Ends with dots 33")]
    public void CleansNames(string raw, string expected) => Assert.Equal(expected, JobNameResolver.Clean(raw, S()));

    [Fact]
    public void NothingUsableLeft() => Assert.Null(JobNameResolver.Clean("Microsoft Word - .docx", S()));

    [Theory]
    [InlineData("Patient 12345 OCT 2024", "12345")]
    [InlineData("OCT_998877", "998877")]
    [InlineData("no digits here", null)]
    public void DefaultRegexTakesFirstDigitRun(string name, string? expected) =>
        Assert.Equal(expected, JobNameResolver.ExtractFileNumber(name, S().FileNumber));

    [Fact]
    public void CustomRegexWithNamedGroup()
    {
        var s = S(@"MRN[-_ ]?(?<num>\d{4,8})");
        Assert.Equal("004512", JobNameResolver.ExtractFileNumber("Visit 2024 MRN-004512 OD", s.FileNumber));
        Assert.Null(JobNameResolver.ExtractFileNumber("Visit 2024 OD", s.FileNumber));
    }

    [Fact]
    public void FirstCandidateWithNumberWins()
    {
        var r = JobNameResolver.Resolve(new (string, string?)[] { ("PJL", "Untitled"), ("Title", "Microsoft Word - 4455.docx") }, S());
        Assert.Equal("Title", r.Source);
        Assert.Equal("4455", r.FileNumber);
        Assert.True(r.IsAssigned);
    }

    [Fact]
    public void NoNumberMeansUnassigned()
    {
        var r = JobNameResolver.Resolve(new (string, string?)[] { ("PJL", "Untitled"), ("Title", null) }, S());
        Assert.False(r.IsAssigned);
        Assert.Equal("Untitled", r.RawName);
    }

    [Fact]
    public void NoNameAtAll()
    {
        var r = JobNameResolver.Resolve(new (string, string?)[] { ("PJL", null), ("Title", "  ") }, S());
        Assert.False(r.IsAssigned);
        Assert.Null(r.RawName);
    }
}

public class OutputWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pti-test-" + Guid.NewGuid().ToString("N"));

    public OutputWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Src(string content)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".src");
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void AddsNumberSuffixWhenNameExists()
    {
        var outDir = Path.Combine(_dir, "out");
        var a = OutputWriter.Place(new[] { Src("1") }, outDir, "12345", ".pdf", copy: false);
        var b = OutputWriter.Place(new[] { Src("2") }, outDir, "12345", ".pdf", copy: false);
        var c = OutputWriter.Place(new[] { Src("3") }, outDir, "12345", ".pdf", copy: false);
        Assert.Equal(Path.Combine(outDir, "12345.pdf"), a.Single());
        Assert.Equal(Path.Combine(outDir, "12345 (2).pdf"), b.Single());
        Assert.Equal(Path.Combine(outDir, "12345 (3).pdf"), c.Single());
        Assert.Equal("1", File.ReadAllText(a[0]));
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp"));
    }

    [Fact]
    public void MultiPageJpgNames()
    {
        var outDir = Path.Combine(_dir, "jpg");
        var r1 = OutputWriter.Place(new[] { Src("a"), Src("b") }, outDir, "777", ".jpg", copy: false);
        Assert.Equal(new[] { "777_p1.jpg", "777_p2.jpg" }, r1.Select(Path.GetFileName));
        var r2 = OutputWriter.Place(new[] { Src("c"), Src("d") }, outDir, "777", ".jpg", copy: false);
        Assert.Equal(new[] { "777 (2)_p1.jpg", "777 (2)_p2.jpg" }, r2.Select(Path.GetFileName));
    }

    [Fact]
    public void TemplatePlaceholders()
    {
        var r = new ResolvedName("raw", "PJL", "Scan 42", "42");
        Assert.Equal("42_OCT_20240102", OutputWriter.ApplyTemplate("{number}_{device}_{date}", r, "OCT", new DateTime(2024, 1, 2, 3, 4, 5)));
        Assert.Equal("Scan 42", OutputWriter.ApplyTemplate("{name}", r, "OCT", DateTime.Now));
    }

    [Fact]
    public void PagePlaceholderAndEmptyPartsTakeTheirSeparator()
    {
        var r = new ResolvedName("IOL-SRK-T", "PostScript %%Title", "IOL-SRK-T", null);
        var t = new DateTime(2026, 10, 14, 14, 2, 29);
        Assert.Equal("D1935_IOL-SRK-T_20261014_140229", OutputWriter.ApplyTemplate("{page}_{name}_{date}_{time}", r, "IOLMaster", t, "D1935"));
        Assert.Equal("IOL-SRK-T_20261014_140229", OutputWriter.ApplyTemplate("{page}_{name}_{date}_{time}", r, "IOLMaster", t));
        Assert.Equal("IOL-SRK-T_20261014", OutputWriter.ApplyTemplate("{name}_{number}_{date}", r, "IOLMaster", t));
        Assert.Equal("IOL-SRK-T", OutputWriter.ApplyTemplate("{name} - {page}", r, "IOLMaster", t));
    }
}
