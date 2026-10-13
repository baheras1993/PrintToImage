using System.Text;
using System.Text.RegularExpressions;

namespace PrintToImage;

/// <summary>Job names found in the header of a print job.</summary>
public sealed record JobHeaderInfo(string? PjlJobName, string? PostScriptTitle);

/// <summary>
/// Reads the job name from the start of a print job: @PJL JOB NAME="..." and the
/// PostScript DSC "%%Title:" comment (plain text, (string) with escapes, or &lt;hex&gt;).
/// </summary>
public static class JobNameParser
{
    static JobNameParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>How many bytes from the start of a job are searched for names.</summary>
    public const int HeaderScanBytes = 256 * 1024;

    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    private static readonly Regex PjlNameRegex = new(
        @"@PJL[ \t]+(?:JOB[ \t]+NAME|SET[ \t]+JOBNAME)[ \t]*=[ \t]*""([^""\r\n]*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DscTitleRegex = new(
        @"(?:^|[\r\n])%%Title:[ \t]*([^\r\n]*)((?:(?:\r\n|\r|\n)%%\+[ \t]?[^\r\n]*)*)",
        RegexOptions.CultureInvariant);

    private static readonly Regex DscContinuationRegex = new(@"%%\+[ \t]?([^\r\n]*)", RegexOptions.CultureInvariant);

    /// <summary>Runs of 2+ octal escapes for high bytes (\2xx/\3xx) inside an unquoted title, e.g. UTF-8 written as \303\251.</summary>
    private static readonly Regex OctalHighByteRun = new(@"(?:\\[23][0-7]{2}){2,}", RegexOptions.CultureInvariant);

    public static JobHeaderInfo Parse(ReadOnlySpan<byte> header, int fallbackCodePage = 1252)
    {
        // Latin-1 maps every byte to exactly one char, so we can regex over it and get the raw bytes back.
        var text = Latin1.GetString(header);

        string? pjl = null;
        var m = PjlNameRegex.Match(text);
        if (m.Success)
            pjl = NullIfBlank(DecodeBytes(Latin1.GetBytes(m.Groups[1].Value), fallbackCodePage));

        string? title = null;
        var t = DscTitleRegex.Match(text);
        if (t.Success)
        {
            var value = t.Groups[1].Value;
            foreach (Match c in DscContinuationRegex.Matches(t.Groups[2].Value))
                value += c.Groups[1].Value;
            title = NullIfBlank(DecodeDscText(value.Trim(), fallbackCodePage));
        }

        return new JobHeaderInfo(pjl, title);
    }

    /// <summary>Decodes a DSC text value: "(PostScript string)", "&lt;hex string&gt;" or plain text (Latin-1 chars = raw bytes).</summary>
    public static string DecodeDscText(string value, int fallbackCodePage = 1252)
    {
        if (value.Length == 0) return "";
        if (value[0] == '(')
            return DecodeBytes(ParsePsLiteralString(value), fallbackCodePage);
        if (value[0] == '<' && !value.StartsWith("<<", StringComparison.Ordinal))
            return DecodeBytes(ParsePsHexString(value), fallbackCodePage);

        // Plain text. Some drivers write UTF-8 as octal escapes even without parentheses; decode
        // only runs that form valid UTF-8, so paths like "D:\2023\..." are left untouched.
        var expanded = OctalHighByteRun.Replace(value, run =>
        {
            var bytes = run.Value.Split('\\', StringSplitOptions.RemoveEmptyEntries)
                .Select(o => (byte)Convert.ToInt32(o, 8)).ToArray();
            try { return Latin1.GetString(Encoding.UTF8.GetBytes(StrictUtf8.GetString(bytes))); }
            catch (DecoderFallbackException) { return run.Value; }
        });
        return DecodeBytes(Latin1.GetBytes(expanded), fallbackCodePage);
    }

    /// <summary>Parses a PostScript literal string "(...)" into bytes, handling nesting and all escape forms.</summary>
    public static byte[] ParsePsLiteralString(string s)
    {
        var bytes = new List<byte>(s.Length);
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (i == 0 && c == '(') { depth = 1; continue; }
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                switch (n)
                {
                    case 'n': bytes.Add((byte)'\n'); break;
                    case 'r': bytes.Add((byte)'\r'); break;
                    case 't': bytes.Add((byte)'\t'); break;
                    case 'b': bytes.Add((byte)'\b'); break;
                    case 'f': bytes.Add((byte)'\f'); break;
                    case '\r': if (i + 1 < s.Length && s[i + 1] == '\n') i++; break; // line continuation
                    case '\n': break;
                    case >= '0' and <= '7':
                        int val = n - '0', digits = 1;
                        while (digits < 3 && i + 1 < s.Length && s[i + 1] is >= '0' and <= '7')
                        {
                            val = val * 8 + (s[++i] - '0');
                            digits++;
                        }
                        bytes.Add((byte)(val & 0xFF));
                        break;
                    default: bytes.Add((byte)n); break; // \( \) \\ and unknown escapes = the char itself
                }
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) break;
            bytes.Add((byte)c);
        }
        return bytes.ToArray();
    }

    public static byte[] ParsePsHexString(string s)
    {
        var hex = new StringBuilder();
        foreach (char c in s.Skip(1))
        {
            if (c == '>') break;
            if (Uri.IsHexDigit(c)) hex.Append(c);
        }
        if (hex.Length % 2 == 1) hex.Append('0');
        return Convert.FromHexString(hex.ToString());
    }

    /// <summary>Bytes to text: BOM-detected UTF-16/UTF-8, else strict UTF-8, else the fallback ANSI code page.</summary>
    public static string DecodeBytes(byte[] bytes, int fallbackCodePage = 1252)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];

        if (bytes.All(b => b < 0x80)) return Encoding.ASCII.GetString(bytes);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { }

        try { return Encoding.GetEncoding(fallbackCodePage).GetString(bytes); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static string? NullIfBlank(string s)
    {
        var cleaned = new string(s.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
