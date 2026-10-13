using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using PrintToImage;
using PrintToImage.PostScript;

// Usage:
//   PrintToImage.exe                      run (as a Windows service, or as a console app when started from a console)
//   PrintToImage.exe --console            same, explicit console mode for testing
//   PrintToImage.exe --settings <file>    use another settings file
//   PrintToImage.exe --render <job.prn> <output.pdf | output-folder> [--format pdf|jpg|png] [--split] [--dpi 300] [--timeout 600]
//                                         (--split: one PDF per page, written into output-folder)
//                                         convert a saved job (e.g. from the archive) without the service
//   PrintToImage.exe --check-name <job.prn>   show the job name and file number that would be used

string? settingsPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--settings" && i + 1 < args.Length) settingsPath = args[i + 1];
}

if (args.Contains("--version"))
{
    Console.WriteLine("PrintToImage " + typeof(Settings).Assembly.GetName().Version);
    return 0;
}

int renderIdx = Array.IndexOf(args, "--render");
if (renderIdx >= 0) return RenderCommand(args, renderIdx);

if (args.Contains("--spooler"))
{
    // Diagnostics for the shared printer: what the Windows spooler reports.
    var s = Settings.Load(settingsPath);
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine(Spooler.Diagnose(s.RawPort));
    return 0;
}

int checkIdx = Array.IndexOf(args, "--check-name");
if (checkIdx >= 0) return CheckNameCommand(args, checkIdx, settingsPath);

Settings settings;
try
{
    settings = Settings.Load(settingsPath);
}
catch (Exception e)
{
    Console.Error.WriteLine("Cannot start: " + e.Message);
    if (WindowsServiceHelpers.IsWindowsService())
    {
        try { System.Diagnostics.EventLog.WriteEntry("PrintToImage", "Cannot start: " + e.Message, System.Diagnostics.EventLogEntryType.Error); }
        catch (Exception) { }
    }
    return 1;
}

Directory.CreateDirectory(settings.LogFolder);
Directory.CreateDirectory(settings.WorkFolder);

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(o => o.ServiceName = "PrintToImage");
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(settings.LogFolder));
if (WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.AddEventLog(new EventLogSettings { SourceName = "PrintToImage", LogName = "Application", Filter = (_, level) => level >= LogLevel.Warning });
}
else
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
}

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton(new JobLog(settings.LogFolder));
builder.Services.AddHostedService<RawListener>();
builder.Services.AddHostedService<LpdListener>();
builder.Services.AddHostedService<JobProcessor>();
builder.Services.AddHostedService<Maintenance>();

var host = builder.Build();
var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PrintToImage");
log.LogInformation("PrintToImage {Version} starting. Settings: {Path}. Output: {Out} ({Format}).",
    typeof(Settings).Assembly.GetName().Version, settings.LoadedFrom, settings.OutputRoot, settings.OutputFormat);
if (!Directory.Exists(Path.GetPathRoot(Path.GetFullPath(settings.OutputRoot))))
    log.LogError("The drive of the output folder {Out} does not exist.", settings.OutputRoot);
foreach (var (ip, dev) in settings.Devices) log.LogInformation("Device mapping: {Ip} -> {Device}", ip, dev);

await host.RunAsync();
return 0;

static int RenderCommand(string[] args, int idx)
{
    if (idx + 2 >= args.Length)
    {
        Console.Error.WriteLine("Usage: PrintToImage --render <job.prn> <output.pdf | output-folder> [--format pdf|jpg|png] [--split] [--dpi 300] [--quality 90] [--timeout 600]");
        return 2;
    }
    string job = args[idx + 1], output = args[idx + 2];
    string format = (Arg(args, "--format") ?? (output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || args.Contains("--split") ? "pdf" : "jpg")).ToLowerInvariant();
    int dpi = int.TryParse(Arg(args, "--dpi"), out var d) ? d : 300;
    int quality = int.TryParse(Arg(args, "--quality"), out var q) ? q : 90;
    int timeout = int.TryParse(Arg(args, "--timeout"), out var t) ? t : 600;
    bool split = args.Contains("--split");
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var r = PostScriptRenderer.Render(job, format, output, dpi, quality, Path.GetFileNameWithoutExtension(job), TimeSpan.FromSeconds(timeout), Console.WriteLine, split);
    Console.WriteLine($"Pages: {r.Pages}, time: {sw.ElapsedMilliseconds} ms");
    foreach (var f in r.Files) Console.WriteLine("Output: " + f);
    if (r.Error is not null) { Console.WriteLine("ERROR: " + r.Error); return 1; }
    return 0;
}

static int CheckNameCommand(string[] args, int idx, string? settingsPath)
{
    if (idx + 1 >= args.Length) { Console.Error.WriteLine("Usage: PrintToImage --check-name <job.prn>"); return 2; }
    var s = Settings.Load(settingsPath);
    using var fs = File.OpenRead(args[idx + 1]);
    var buf = new byte[(int)Math.Min(fs.Length, JobNameParser.HeaderScanBytes)];
    fs.ReadExactly(buf);
    var info = JobNameParser.Parse(buf, s.EffectiveCodePage);
    var r = JobNameResolver.Resolve(new (string, string?)[] { ("PJL JOB NAME", info.PjlJobName), ("PostScript %%Title", info.PostScriptTitle) }, s);
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine($"PJL JOB NAME     : {info.PjlJobName ?? "(none)"}");
    Console.WriteLine($"%%Title          : {info.PostScriptTitle ?? "(none)"}");
    Console.WriteLine($"Used             : {r.Source ?? "(none)"}");
    Console.WriteLine($"Clean name       : {r.CleanName ?? "(none)"}");
    Console.WriteLine($"File number      : {r.FileNumber ?? "(none) -> Unassigned"}");
    return 0;
}

static string? Arg(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
