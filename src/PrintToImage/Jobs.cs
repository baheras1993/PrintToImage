using System.Net;
using System.Text.Json;
using System.Threading.Channels;

namespace PrintToImage;

/// <summary>A print job that has been fully received and written to the spool folder.</summary>
public sealed class ReceivedJob
{
    public string Id { get; set; } = "";
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
    public string SenderIp { get; set; } = "";
    public string Protocol { get; set; } = "RAW";
    public string SpoolPath { get; set; } = "";
    public long Bytes { get; set; }
    public bool Incomplete { get; set; }
    public string? LpdJobName { get; set; }
    public string? LpdSourceName { get; set; }
    public string? LpdUser { get; set; }
    public string? LpdHost { get; set; }
    /// <summary>Set when the job came through the shared printer on this server (from the Windows spooler).</summary>
    public string? ClientComputer { get; set; }
    public string? ClientUser { get; set; }
    public string? SpoolerDocument { get; set; }
    public string? SpoolerPrinter { get; set; }

    public static string NewId() => $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Random.Shared.Next(0x10000):x4}";

    public static string NormalizeIp(IPAddress? ip)
    {
        if (ip is null) return "";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString();
    }

    public string MetaPath => Path.ChangeExtension(SpoolPath, ".json");

    /// <summary>Writes a sidecar file so a job spooled before a crash or restart is still processed.</summary>
    public void SaveMeta() => File.WriteAllText(MetaPath, JsonSerializer.Serialize(this));

    public static ReceivedJob? LoadMeta(string path)
    {
        try { return JsonSerializer.Deserialize<ReceivedJob>(File.ReadAllText(path)); }
        catch (Exception) { return null; }
    }
}

/// <summary>Queue between the network listeners and the converter workers.</summary>
public sealed class JobQueue
{
    private readonly Channel<ReceivedJob> _channel = Channel.CreateUnbounded<ReceivedJob>();
    public ChannelReader<ReceivedJob> Reader => _channel.Reader;
    public void Enqueue(ReceivedJob job) => _channel.Writer.TryWrite(job);
}
