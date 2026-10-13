using System.Text;
using System.Text.RegularExpressions;

namespace PrintToImage;

/// <summary>The outcome of naming a job.</summary>
public sealed record ResolvedName(
    string? RawName,        // the name as received (first non-empty source)
    string? Source,         // "PJL", "PostScript %%Title", "LPR job name", ...
    string? CleanName,      // cleaned, file-system safe name
    string? FileNumber)     // null => Unassigned
{
    public bool IsAssigned => FileNumber is not null && CleanName is not null;
}

public static class JobNameResolver
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Reserved = new(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DrivePath = new(@"^([A-Za-z]:[\\/]|\\\\|file:/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public const int MaxNameLength = 150;

    /// <summary>Titles some drivers write instead of the document name (e.g. the Microsoft PS Class Driver).</summary>
    private static readonly HashSet<string> DriverPlaceholders = new(StringComparer.OrdinalIgnoreCase) { "MSxpsPS" };

    /// <summary>Tries each candidate (in priority order) and uses the first one that yields a file number.
    /// Never guesses: if none has a file number the job is unassigned.</summary>
    public static ResolvedName Resolve(IEnumerable<(string Source, string? Raw)> candidates, Settings s)
    {
        ResolvedName? firstNamed = null;
        foreach (var (source, raw) in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw) || DriverPlaceholders.Contains(raw.Trim())) continue;
            var clean = Clean(raw, s);
            if (clean is null) { firstNamed ??= new ResolvedName(raw, source, null, null); continue; }
            var number = ExtractFileNumber(clean, s.FileNumber);
            var result = new ResolvedName(raw, source, clean, number);
            if (number is not null) return result;
            firstNamed ??= result;
        }
        return firstNamed ?? new ResolvedName(null, null, null, null);
    }

    /// <summary>Cleans a job name for use as a file name. Returns null if nothing usable is left.</summary>
    public static string? Clean(string raw, Settings s)
    {
        var name = new string(raw.Where(c => !char.IsControl(c)).ToArray());
        name = Spaces.Replace(name, " ").Trim();

        // A full path as job name ("C:\Scans\12345.pdf"): keep only the file name part.
        if (DrivePath.IsMatch(name))
        {
            int slash = name.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0 && slash < name.Length - 1) name = name[(slash + 1)..];
        }

        // Application prefixes/suffixes and original extensions, repeated until nothing changes.
        for (int round = 0; round < 5; round++)
        {
            var before = name;
            foreach (var rx in s.StripRegexes) name = rx.Replace(name, "").Trim();
            foreach (var ext in s.StripExtensions)
            {
                if (ext.Length > 0 && name.Length >= ext.Length && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    name = name[..^ext.Length].TrimEnd();
            }
            if (name == before) break;
        }

        // Characters not allowed in Windows file names.
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Invalid.Contains(c) ? '_' : c);
        name = Spaces.Replace(sb.ToString(), " ").Trim().TrimEnd('.', ' ');

        if (Reserved.IsMatch(name)) name = "_" + name;
        if (name.Length > MaxNameLength)
        {
            int cut = MaxNameLength;
            if (char.IsLowSurrogate(name[cut])) cut--;
            name = name[..cut].TrimEnd('.', ' ');
        }
        return name.Any(char.IsLetterOrDigit) ? name : null;
    }

    /// <summary>Applies the file-number regex: group "num", else group 1, else the whole match.</summary>
    public static string? ExtractFileNumber(string name, Regex rx)
    {
        Match m;
        try { m = rx.Match(name); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!m.Success) return null;
        Group g = m.Groups["num"].Success ? m.Groups["num"] : m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1] : m.Groups[0];
        var v = g.Value.Trim();
        if (v.Length == 0) return null;
        var sb = new StringBuilder();
        foreach (var c in v) sb.Append(Invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }
}
