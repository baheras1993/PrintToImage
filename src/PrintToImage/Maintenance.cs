using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PrintToImage;

/// <summary>Hourly housekeeping: archive retention, old service logs, stale work files.</summary>
public sealed class Maintenance : BackgroundService
{
    private readonly Settings _s;
    private readonly ILogger<Maintenance> _log;

    public Maintenance(Settings s, ILogger<Maintenance> log) { _s = s; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { RunOnce(); }
            catch (Exception e) { _log.LogWarning("Housekeeping failed: {Message}", e.Message); }
            try { await Task.Delay(TimeSpan.FromHours(1), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void RunOnce()
    {
        int deleted = 0;
        if (_s.ArchiveRetentionDays > 0 && Directory.Exists(_s.ArchiveFolder))
        {
            var cutoff = DateTime.Now.AddDays(-_s.ArchiveRetentionDays);
            foreach (var f in Directory.EnumerateFiles(_s.ArchiveFolder, "*", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTime(f) < cutoff && TryDelete(f)) deleted++;
            }
            foreach (var d in Directory.GetDirectories(_s.ArchiveFolder))
                if (!Directory.EnumerateFileSystemEntries(d).Any()) TryDeleteDir(d);
        }
        if (deleted > 0) _log.LogInformation("Deleted {Count} archived job(s) older than {Days} days.", deleted, _s.ArchiveRetentionDays);

        if (_s.LogRetentionDays > 0 && Directory.Exists(_s.LogFolder))
        {
            var cutoff = DateTime.Now.AddDays(-_s.LogRetentionDays);
            foreach (var f in Directory.EnumerateFiles(_s.LogFolder, "service-*.log"))
                if (File.GetLastWriteTime(f) < cutoff) TryDelete(f);
        }

        // Work folders of jobs that crashed mid-way, and temporary files left in output folders.
        if (Directory.Exists(_s.WorkFolder))
        {
            foreach (var d in Directory.GetDirectories(_s.WorkFolder))
                if (Path.GetFileName(d) != "spool" && Directory.GetLastWriteTime(d) < DateTime.Now.AddDays(-1)) TryDeleteDir(d);
        }
        if (Directory.Exists(_s.OutputRoot))
        {
            foreach (var f in Directory.EnumerateFiles(_s.OutputRoot, "~PrintToImage_*.tmp", SearchOption.AllDirectories))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1)) TryDelete(f);
        }
    }

    private static bool TryDelete(string f)
    {
        try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void TryDeleteDir(string d)
    {
        try { Directory.Delete(d, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
