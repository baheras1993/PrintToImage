using System.Text;
using Microsoft.Extensions.Logging;

namespace PrintToImage;

/// <summary>Writes the service log to logs\service-yyyy-MM-dd.log.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _folder;
    private readonly object _lock = new();

    public FileLoggerProvider(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    internal void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ')
            .Append(level switch
            {
                LogLevel.Critical => "CRIT ", LogLevel.Error => "ERROR", LogLevel.Warning => "WARN ",
                LogLevel.Information => "INFO ", LogLevel.Debug => "DEBUG", _ => "TRACE",
            })
            .Append(' ').Append(ShortCategory(category)).Append(": ").Append(message);
        if (ex is not null) line.AppendLine().Append(ex);
        line.AppendLine();
        var path = Path.Combine(_folder, $"service-{DateTime.Now:yyyy-MM-dd}.log");
        lock (_lock)
        {
            try { File.AppendAllText(path, line.ToString(), Encoding.UTF8); }
            catch (IOException) { /* logging must never crash the service */ }
        }
    }

    private static string ShortCategory(string c) => c.StartsWith("PrintToImage.", StringComparison.Ordinal) ? c[13..] : c;

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p;
        private readonly string _cat;
        public FileLogger(FileLoggerProvider p, string cat) { _p = p; _cat = cat; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            if (_cat.StartsWith("Microsoft.Hosting", StringComparison.Ordinal) && logLevel < LogLevel.Warning) return;
            _p.Write(logLevel, _cat, formatter(state, exception), exception);
        }
    }
}

/// <summary>One record per print job.</summary>
public sealed class JobRecord
{
    public DateTime Time = DateTime.Now;
    public string JobId = "";
    public string Protocol = "";
    public string SenderIp = "";
    public string Device = "";
    public string? RawJobName;
    public string? NameSource;
    public string? CleanName;
    public string? FileNumber;
    public string Status = "";          // OK, UNASSIGNED, FAILED
    public List<string> OutputFiles = new();
    public int Pages;
    public long Bytes;
    public long DurationMs;
    public string? ArchiveFile;
    public string? Error;
    public string? ClientComputer;   // jobs through the shared printer
    public string? ClientUser;
    public string? PageNumber;       // the number found on the page (PageNumberRegex)
}

/// <summary>Appends job records to logs\jobs-yyyy-MM.csv (open it in Excel to search jobs).</summary>
public sealed class JobLog
{
    private readonly string _folder;
    private readonly object _lock = new();
    private static readonly string Header = "Time,JobId,Protocol,SenderIP,Device,RawJobName,NameSource,CleanName,FileNumber,Status,OutputFiles,Pages,Bytes,DurationMs,ArchiveFile,Error,ClientComputer,ClientUser,PageNumber";

    public JobLog(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public void Write(JobRecord r)
    {
        var fields = new[]
        {
            r.Time.ToString("yyyy-MM-dd HH:mm:ss"), r.JobId, r.Protocol, r.SenderIp, r.Device, r.RawJobName, r.NameSource, r.CleanName,
            r.FileNumber, r.Status, string.Join(" | ", r.OutputFiles), r.Pages.ToString(), r.Bytes.ToString(), r.DurationMs.ToString(),
            r.ArchiveFile, r.Error, r.ClientComputer, r.ClientUser, r.PageNumber,
        };
        var line = string.Join(",", fields.Select(Csv));
        var path = Path.Combine(_folder, $"jobs-{r.Time:yyyy-MM}.csv");
        lock (_lock)
        {
            bool isNew = !File.Exists(path);
            // UTF-8 with BOM so Excel shows non-ASCII names correctly.
            using var w = new StreamWriter(path, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: isNew));
            if (isNew) w.WriteLine(Header);
            w.WriteLine(line);
        }
    }

    private static string Csv(string? v)
    {
        v ??= "";
        // Neutralize spreadsheet formulas in untrusted text.
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@') v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
