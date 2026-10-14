using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrintToImage.PostScript;

namespace PrintToImage;

/// <summary>Converts received jobs and places the results in the output folders.</summary>
public sealed class JobProcessor : BackgroundService
{
    private readonly Settings _s;
    private readonly JobQueue _queue;
    private readonly JobLog _jobLog;
    private readonly ILogger<JobProcessor> _log;

    public JobProcessor(Settings s, JobQueue queue, JobLog jobLog, ILogger<JobProcessor> log)
    {
        _s = s; _queue = queue; _jobLog = jobLog; _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stop)
    {
        RecoverSpooledJobs();
        var workers = Enumerable.Range(0, _s.MaxConcurrentConversions).Select(_ => Task.Run(() => WorkerAsync(stop), stop));
        return Task.WhenAll(workers);
    }

    /// <summary>Jobs received before a restart (their sidecar .json exists) are processed now.</summary>
    private void RecoverSpooledJobs()
    {
        var folder = Spool.Folder(_s);
        foreach (var meta in Directory.GetFiles(folder, "*.json"))
        {
            var job = ReceivedJob.LoadMeta(meta);
            if (job is not null && File.Exists(job.SpoolPath))
            {
                _log.LogInformation("Recovering job {Id} received before the service restarted.", job.Id);
                _queue.Enqueue(job);
            }
            else File.Delete(meta);
        }
        // Spool files without metadata were cut off mid-transfer by a crash: keep them for troubleshooting.
        foreach (var prn in Directory.GetFiles(folder, "*.prn"))
        {
            if (File.Exists(Path.ChangeExtension(prn, ".json"))) continue;
            var target = Path.Combine(_s.OutputRoot, _s.FailedFolderName, "incomplete_" + Path.GetFileName(prn));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(prn, target, overwrite: false);
                _log.LogWarning("Found an incompletely received job from before a restart; moved to {Path}.", target);
            }
            catch (IOException e) { _log.LogWarning("Could not move {Path}: {Message}", prn, e.Message); }
        }
    }

    private async Task WorkerAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stop))
            {
                try { Process(job); }
                catch (Exception e)
                {
                    _log.LogError(e, "Unexpected failure processing job {Id}.", job.Id);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Process(ReceivedJob job)
    {
        var sw = Stopwatch.StartNew();
        var device = DeviceFor(job);
        var rec = new JobRecord
        {
            Time = job.ReceivedAt, JobId = job.Id, Protocol = job.Protocol, SenderIp = job.SenderIp, Device = device, Bytes = job.Bytes,
            ClientComputer = job.ClientComputer, ClientUser = job.ClientUser,
        };
        string jobFile = job.SpoolPath;
        string workDir = Path.Combine(_s.WorkFolder, job.Id);
        try
        {
            // 1. Keep the raw job for troubleshooting.
            if (_s.ArchiveRetentionDays > 0)
            {
                var dayFolder = Path.Combine(_s.ArchiveFolder, job.ReceivedAt.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(dayFolder);
                var archived = Path.Combine(dayFolder, $"{job.ReceivedAt:yyyyMMdd_HHmmss_fff}_{device}_{job.Id[^4..]}.prn");
                File.Move(jobFile, archived, overwrite: true);
                jobFile = archived;
                rec.ArchiveFile = archived;
            }

            // 2. Job name -> file number.
            var header = ReadHeader(jobFile);
            var info = JobNameParser.Parse(header, _s.EffectiveCodePage);
            var resolved = JobNameResolver.Resolve(new (string, string?)[]
            {
                ("PJL JOB NAME", info.PjlJobName),
                ("PostScript %%Title", info.PostScriptTitle),
                ("Windows print queue", job.SpoolerDocument),
                ("LPR job name", job.LpdJobName),
                ("LPR file name", job.LpdSourceName),
            }, _s);
            rec.RawJobName = resolved.RawName;
            rec.NameSource = resolved.Source;
            rec.CleanName = resolved.CleanName;
            rec.FileNumber = resolved.FileNumber;

            // 3. Render.
            var format = _s.FormatForDevice(device);
            Directory.CreateDirectory(workDir);
            bool pdfPerPage = format == "pdf" && _s.PdfPerPageForDevice(device);
            string renderTarget = format == "pdf" && !pdfPerPage ? Path.Combine(workDir, "out.pdf") : Path.Combine(workDir, "pages");
            var title = resolved.CleanName ?? $"{device} {job.ReceivedAt:yyyy-MM-dd HH:mm}";
            var result = PostScriptRenderer.Render(jobFile, format, renderTarget, _s.JpgDpi, _s.JpgQuality, title,
                TimeSpan.FromSeconds(_s.RenderTimeoutSeconds), m =>
                {
                    // Text the job prints to its "console" (%%[ Page: 1 ]%% etc.) is not interesting in the log.
                    if (!m.StartsWith("PS output:", StringComparison.Ordinal)) _log.LogInformation("Job {Id}: {Message}", job.Id, m);
                }, pdfPerPage);
            rec.Pages = Math.Max(result.Pages, 0);

            if (result.Error is not null || result.Files.Count == 0)
            {
                // 4a. Failed: never hand an incomplete document to the EMR.
                rec.Status = "FAILED";
                rec.Error = result.Error ?? "No output was produced.";
                if (job.Incomplete) rec.Error = "Job was not received completely. " + rec.Error;
                var failedFolder = Path.Combine(_s.OutputRoot, _s.FailedFolderName);
                var baseName = $"{device}_{job.ReceivedAt:yyyyMMdd_HHmmss}" + (resolved.CleanName is { } cn ? "_" + cn : "");
                rec.OutputFiles.AddRange(OutputWriter.Place(new[] { jobFile }, failedFolder, baseName, ".prn", copy: true));
                var partial = result.Files.Where(File.Exists).ToList();
                if (partial.Count > 0)
                    rec.OutputFiles.AddRange(OutputWriter.Place(partial, failedFolder, baseName + "_partial", "." + format, copy: false));
                _log.LogError("Job {Id} from {Ip} ({Device}) FAILED: {Error}. Raw job saved to {Files}.", job.Id, job.SenderIp, device, rec.Error, string.Join(", ", rec.OutputFiles));
                return;
            }

            // 4a'. A file number printed on the page (devices that name the print after the report).
            string? pageNumber = _s.PageNumber is { } prx && result.Text.Length > 0 ? JobNameResolver.ExtractFileNumber(result.Text, prx) : null;
            rec.PageNumber = pageNumber;

            // 4b. Place the output. Unassigned when there is no file number in the name or on the page — never guess.
            string folder, name;
            if ((resolved.IsAssigned || pageNumber is not null) && !job.Incomplete)
            {
                folder = Path.Combine(_s.OutputRoot, device);
                name = OutputWriter.ApplyTemplate(_s.FileNameTemplate, resolved, device, job.ReceivedAt, pageNumber);
                if (name == "_") name = $"{pageNumber}_{job.ReceivedAt:yyyyMMdd_HHmmss}";   // a template of only parts that are empty
                rec.Status = "OK";
            }
            else
            {
                folder = Path.Combine(_s.OutputRoot, _s.UnassignedFolderName);
                name = $"{device}_{job.ReceivedAt:yyyyMMdd_HHmmss}";
                rec.Status = "UNASSIGNED";
                rec.Error = job.Incomplete ? "Job was not received completely."
                    : resolved.RawName is null ? "No job name found."
                    : _s.PageNumber is not null ? "No file number found in the job name or on the page." : "No file number found in the job name.";
            }
            rec.OutputFiles.AddRange(OutputWriter.Place(result.Files, folder, name, "." + format, copy: false));
            rec.Pages = result.Pages > 0 ? result.Pages : rec.OutputFiles.Count;
            _log.LogInformation("Job {Id} from {Sender} ({Device}): \"{Raw}\" -> file number {Num}, on the page {Page} -> {Files} ({Pages} page(s), {Status}).",
                job.Id, job.ClientComputer ?? job.SenderIp, device, resolved.RawName, resolved.FileNumber ?? "(none)", pageNumber ?? "(none)",
                string.Join(", ", rec.OutputFiles), rec.Pages, rec.Status);
        }
        catch (Exception e)
        {
            rec.Status = "FAILED";
            rec.Error = e.Message;
            _log.LogError(e, "Job {Id} from {Ip} failed.", job.Id, job.SenderIp);
            try
            {
                if (File.Exists(jobFile))
                {
                    var failedFolder = Path.Combine(_s.OutputRoot, _s.FailedFolderName);
                    rec.OutputFiles.AddRange(OutputWriter.Place(new[] { jobFile }, failedFolder, $"{device}_{job.ReceivedAt:yyyyMMdd_HHmmss}", ".prn", copy: true));
                }
            }
            catch (Exception e2) { _log.LogError("Could not save failed job {Id}: {Message}", job.Id, e2.Message); }
        }
        finally
        {
            rec.DurationMs = sw.ElapsedMilliseconds;
            try { _jobLog.Write(rec); }
            catch (Exception e) { _log.LogError("Could not write the job log: {Message}", e.Message); }
            TryDelete(job.MetaPath);
            if (_s.ArchiveRetentionDays == 0 || rec.ArchiveFile is null) TryDelete(job.SpoolPath);
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Device folder for a job: by the client computer name (jobs through the shared printer) or by the sender's IP
    /// (jobs sent directly to port 9100). Devices may be listed by computer name or IP address.
    /// </summary>
    internal string DeviceFor(ReceivedJob job)
    {
        string? Lookup(string key) => _s.Devices.TryGetValue(key, out var n) && !string.IsNullOrWhiteSpace(n) ? OutputWriter.SafeFolderName(n) : null;

        if (!string.IsNullOrWhiteSpace(job.ClientComputer))
        {
            var computer = job.ClientComputer.Trim();
            var found = Lookup(computer) ?? Lookup(computer.Split('.')[0]);
            if (found is not null) return found;
            // Listed by IP address: resolve the computer name.
            if (!System.Net.IPAddress.TryParse(computer, out _))
            {
                try
                {
                    var t = System.Net.Dns.GetHostAddressesAsync(computer);
                    if (t.Wait(TimeSpan.FromSeconds(3)))
                        foreach (var a in t.Result)
                            if (Lookup(ReceivedJob.NormalizeIp(a)) is { } byIp) return byIp;
                }
                catch (Exception) { }
            }
            return _s.UnknownDeviceFolderName;
        }
        return Lookup(job.SenderIp) ?? _s.UnknownDeviceFolderName;
    }

    private static byte[] ReadHeader(string path)
    {
        using var fs = File.OpenRead(path);
        var buf = new byte[(int)Math.Min(fs.Length, JobNameParser.HeaderScanBytes)];
        fs.ReadExactly(buf);
        return buf;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
