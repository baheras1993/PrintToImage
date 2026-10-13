namespace PrintToImage;

/// <summary>Places finished files in an output folder without the EMR ever seeing a half-written file.</summary>
public static class OutputWriter
{
    private static readonly object PlaceLock = new();
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    public static string SafeFolderName(string name)
    {
        var s = new string(name.Select(c => Invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>Placeholders: {name} {number} {device} {date} {time}.</summary>
    public static string ApplyTemplate(string template, ResolvedName r, string device, DateTime received)
    {
        var s = template
            .Replace("{name}", r.CleanName ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{number}", r.FileNumber ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{device}", device, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", received.ToString("yyyyMMdd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", received.ToString("HHmmss"), StringComparison.OrdinalIgnoreCase);
        s = SafeFolderName(s);
        return s.Length > JobNameResolver.MaxNameLength ? s[..JobNameResolver.MaxNameLength].TrimEnd('.', ' ') : s;
    }

    /// <summary>
    /// Copies (or moves) the sources into the folder under hidden temporary names, then renames them to
    /// baseName.ext (one file) or baseName_p1.ext, _p2... (several), adding " (2)", " (3)"... if a name is taken.
    /// </summary>
    public static List<string> Place(IReadOnlyList<string> sources, string folder, string baseName, string ext, bool copy)
    {
        Directory.CreateDirectory(folder);
        var temps = new List<string>();
        try
        {
            foreach (var src in sources)
            {
                var tmp = Path.Combine(folder, $"~PrintToImage_{Guid.NewGuid():N}.tmp");
                if (copy) File.Copy(src, tmp); else MoveOrCopy(src, tmp);
                File.SetAttributes(tmp, FileAttributes.Hidden | FileAttributes.Temporary);
                temps.Add(tmp);
            }

            lock (PlaceLock)
            {
                for (int n = 1; n < 10_000; n++)
                {
                    var suffix = n == 1 ? "" : $" ({n})";
                    var finals = temps.Count == 1
                        ? new List<string> { Path.Combine(folder, baseName + suffix + ext) }
                        : temps.Select((_, i) => Path.Combine(folder, $"{baseName}{suffix}_p{i + 1}{ext}")).ToList();
                    if (finals.Any(File.Exists)) continue;

                    var done = new List<(string Tmp, string Final)>();
                    try
                    {
                        for (int i = 0; i < temps.Count; i++)
                        {
                            File.SetAttributes(temps[i], FileAttributes.Normal);
                            File.Move(temps[i], finals[i], overwrite: false);
                            done.Add((temps[i], finals[i]));
                        }
                        temps.Clear();
                        return finals;
                    }
                    catch (IOException)
                    {
                        // Someone created one of the names meanwhile: undo and try the next number.
                        foreach (var (tmp, fin) in done) File.Move(fin, tmp);
                        foreach (var t in temps) if (File.Exists(t)) File.SetAttributes(t, FileAttributes.Hidden | FileAttributes.Temporary);
                    }
                }
            }
            throw new IOException($"No free file name for {baseName} in {folder}.");
        }
        finally
        {
            foreach (var t in temps)
                try { if (File.Exists(t)) File.Delete(t); } catch (IOException) { }
        }
    }

    private static void MoveOrCopy(string src, string dst)
    {
        try { File.Move(src, dst); }
        catch (IOException) { File.Copy(src, dst); }
    }
}
