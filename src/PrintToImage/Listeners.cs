using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PrintToImage;

/// <summary>Raw / JetDirect printing (TCP 9100): everything received until the connection closes is one job.</summary>
public sealed class RawListener : BackgroundService
{
    private readonly Settings _s;
    private readonly JobQueue _queue;
    private readonly ILogger<RawListener> _log;

    private DateTime _lastDiag = DateTime.MinValue;

    public RawListener(Settings s, JobQueue queue, ILogger<RawListener> log) { _s = s; _queue = queue; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var listener = new TcpListener(IPAddress.Parse(_s.ListenAddress), _s.RawPort);
        try
        {
            listener.Start(64);
        }
        catch (SocketException e)
        {
            _log.LogCritical("Cannot listen on TCP port {Port}: {Message}. Is another printer service using it?", _s.RawPort, e.Message);
            throw;
        }
        _log.LogInformation("Listening for raw print jobs on {Address}:{Port}", _s.ListenAddress, _s.RawPort);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                _ = Task.Run(() => HandleAsync(client, stop), stop);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private static bool IsThisComputer(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        try { return Dns.GetHostAddresses(Dns.GetHostName()).Any(x => x.Equals(a)); }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Finds the spooler job being sent to us and keeps re-reading it until the transfer ends: its document name is
    /// only final once the application has finished spooling.
    /// </summary>
    private async Task<Spooler.JobInfo?> TrackSpoolerJob(CancellationToken receiving)
    {
        Spooler.JobInfo? job = null;
        int port = _s.RawPort;
        var deadline = DateTime.UtcNow.AddMinutes(30);
        while (DateTime.UtcNow < deadline)
        {
            bool last = receiving.IsCancellationRequested;
            if (job is null) job = Spooler.CurrentJob(port);
            else if (Spooler.GetJob(job.Printer, job.JobId) is { } again) job = again;
            if (last) break;
            try { await Task.Delay(300, receiving); } catch (OperationCanceledException) { }
        }
        // Transfer finished but the job was not seen yet (a fast transfer right after start-up): look a little longer.
        for (int i = 0; job is null && i < 15; i++)
        {
            job = Spooler.CurrentJob(port);
            if (job is null) await Task.Delay(200);
        }
        return job;
    }

    private async Task HandleAsync(TcpClient client, CancellationToken stop)
    {
        var ip = ReceivedJob.NormalizeIp((client.Client.RemoteEndPoint as IPEndPoint)?.Address);
        var job = new ReceivedJob { Id = ReceivedJob.NewId(), SenderIp = ip, Protocol = "RAW", ReceivedAt = DateTime.Now };
        job.SpoolPath = Path.Combine(Spool.Folder(_s), job.Id + ".prn");
        // A job from this computer is usually the shared printer's spooler forwarding a job: ask the spooler who sent it.
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(stop);
        Task<Spooler.JobInfo?>? lookup = remote is not null && IsThisComputer(remote) ? Task.Run(() => TrackSpoolerJob(receiving.Token)) : null;
        Spooler.JobInfo? spoolerJob = null;
        try
        {
            using (client)
            {
                await using (var file = new FileStream(job.SpoolPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    client.ReceiveBufferSize = 1 << 16;
                    var net = client.GetStream();
                    var (bytes, timedOut, tooBig) = await Spool.CopyWithIdleTimeoutAsync(net, file, -1, _s, stop);
                    job.Bytes = bytes;
                    job.Incomplete = timedOut || tooBig;
                    if (timedOut) _log.LogWarning("Job {Id} from {Ip}: no data for {Sec} s, closing connection after {Bytes} bytes.", job.Id, ip, _s.ReceiveIdleTimeoutSeconds, bytes);
                    if (tooBig) _log.LogError("Job {Id} from {Ip} exceeded the maximum job size ({Max} MB) and was cut off.", job.Id, ip, _s.MaxJobSizeMB);
                }
                // Keep the connection open until the spooler has told us who sent the job: the spooler keeps the job
                // (status "printing") until we close, then deletes it.
                receiving.Cancel();
                if (lookup is not null) spoolerJob = await lookup;
            }
            if (job.Bytes == 0)
            {
                File.Delete(job.SpoolPath);
                return; // status probes and empty connections
            }
            if (lookup is not null && spoolerJob is null && DateTime.UtcNow - _lastDiag > TimeSpan.FromMinutes(10))
            {
                _lastDiag = DateTime.UtcNow;
                _log.LogWarning("Job {Id} came from this computer, but the Windows print queue did not report which PC sent it. " +
                                "Spooler diagnostics:\n{Diag}", job.Id, Spooler.Diagnose(_s.RawPort));
            }
            if (spoolerJob is { } sj)
            {
                job.ClientComputer = sj.Computer;
                job.ClientUser = sj.User;
                job.SpoolerDocument = sj.Document;
                job.SpoolerPrinter = sj.Printer;
                job.Protocol = "SHARED";
            }
            job.SaveMeta();
            if (job.ClientComputer is not null)
                _log.LogInformation("Received job {Id} from computer {Computer} (user {User}) through printer \"{Printer}\" ({Bytes} bytes).",
                    job.Id, job.ClientComputer, job.ClientUser, job.SpoolerPrinter, job.Bytes);
            else
                _log.LogInformation("Received job {Id} from {Ip} ({Bytes} bytes, raw).", job.Id, ip, job.Bytes);
            _queue.Enqueue(job);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            _log.LogError("Receiving job {Id} from {Ip} failed: {Message}", job.Id, ip, e.Message);
            Spool.KeepPartial(job, _s, _queue);
        }
        catch (OperationCanceledException)
        {
            Spool.KeepPartial(job, _s, _queue);
        }
    }
}


/// <summary>LPD / LPR (RFC 1179) on TCP 515. The control file's J line carries the job name.</summary>
public sealed class LpdListener : BackgroundService
{
    private readonly Settings _s;
    private readonly JobQueue _queue;
    private readonly ILogger<LpdListener> _log;

    public LpdListener(Settings s, JobQueue queue, ILogger<LpdListener> log) { _s = s; _queue = queue; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!_s.EnableLpd) return;
        var listener = new TcpListener(IPAddress.Parse(_s.ListenAddress), _s.LpdPort);
        try { listener.Start(32); }
        catch (SocketException e)
        {
            _log.LogCritical("Cannot listen on LPD port {Port}: {Message}. (Is the Windows 'LPD Print Service' running?)", _s.LpdPort, e.Message);
            return;
        }
        _log.LogInformation("Listening for LPD print jobs on {Address}:{Port}", _s.ListenAddress, _s.LpdPort);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop);
                _ = Task.Run(() => HandleAsync(client, stop), stop);
            }
        }
        catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken stop)
    {
        var ip = ReceivedJob.NormalizeIp((client.Client.RemoteEndPoint as IPEndPoint)?.Address);
        var dataJobs = new List<ReceivedJob>();
        string? jobName = null, sourceName = null, user = null, host = null;
        try
        {
            using (client)
            {
                var net = client.GetStream();
                var cmd = await ReadLineAsync(net, stop);
                if (cmd is null || cmd.Length == 0) return;
                switch (cmd[0])
                {
                    case 0x02: // receive a printer job
                        net.WriteByte(0);
                        while (true)
                        {
                            var sub = await ReadLineAsync(net, stop);
                            if (sub is null || sub.Length == 0) break;
                            byte code = sub[0];
                            string rest = Encoding.Latin1.GetString(sub, 1, sub.Length - 1);
                            if (code == 0x01) { dataJobs.ForEach(j => File.Delete(j.SpoolPath)); dataJobs.Clear(); net.WriteByte(0); continue; } // abort
                            if (code is not (0x02 or 0x03)) { net.WriteByte(1); continue; }
                            int sp = rest.IndexOf(' ');
                            long count = long.TryParse(sp > 0 ? rest[..sp] : rest, out var c) ? c : 0;
                            net.WriteByte(0);
                            if (code == 0x02)
                            {
                                // Control file: small, read fully
                                var ctl = await ReadCountedAsync(net, count, stop);
                                foreach (var line in Encoding.Latin1.GetString(ctl).Split('\n'))
                                {
                                    if (line.Length < 2) continue;
                                    var v = line[1..].TrimEnd('\r');
                                    switch (line[0])
                                    {
                                        case 'J': jobName = v; break;
                                        case 'N': sourceName = v; break;
                                        case 'P': user = v; break;
                                        case 'H': host = v; break;
                                    }
                                }
                                net.WriteByte(0);
                            }
                            else
                            {
                                var job = new ReceivedJob { Id = ReceivedJob.NewId(), SenderIp = ip, Protocol = "LPD", ReceivedAt = DateTime.Now };
                                job.SpoolPath = Path.Combine(Spool.Folder(_s), job.Id + ".prn");
                                dataJobs.Add(job);
                                // Count 0 or an absurd size (byte counting disabled): read until the connection closes.
                                bool untilClose = count <= 0 || count > (long)_s.MaxJobSizeMB * 1024 * 1024;
                                await using (var file = new FileStream(job.SpoolPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                                {
                                    var (bytes, timedOut, tooBig) = await Spool.CopyWithIdleTimeoutAsync(net, file, untilClose ? -1 : count, _s, stop);
                                    job.Bytes = bytes;
                                    job.Incomplete = timedOut || tooBig || !untilClose && bytes < count;
                                }
                                if (untilClose) break;
                                net.ReadByte(); // trailing zero octet
                                net.WriteByte(0);
                            }
                        }
                        break;
                    case 0x01: // print any waiting jobs
                        net.WriteByte(0);
                        break;
                    case 0x03:
                    case 0x04: // queue state
                        var msg = Encoding.ASCII.GetBytes("PrintToImage: no entries\n");
                        await net.WriteAsync(msg, stop);
                        break;
                    case 0x05: // remove jobs
                        net.WriteByte(0);
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            if (dataJobs.Count == 0) _log.LogWarning("LPD connection from {Ip} ended: {Message}", ip, e.Message);
        }
        catch (OperationCanceledException) { }

        foreach (var job in dataJobs)
        {
            if (!File.Exists(job.SpoolPath)) continue;
            if (job.Bytes == 0) { File.Delete(job.SpoolPath); continue; }
            job.LpdJobName = jobName;
            job.LpdSourceName = sourceName;
            job.LpdUser = user;
            job.LpdHost = host;
            job.SaveMeta();
            _log.LogInformation("Received job {Id} from {Ip} ({Bytes} bytes, LPD, job name \"{Name}\").", job.Id, ip, job.Bytes, jobName);
            _queue.Enqueue(job);
        }
    }

    private static async Task<byte[]?> ReadLineAsync(NetworkStream s, CancellationToken stop)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (buf.Count < 4096)
        {
            int n = await s.ReadAsync(one, stop);
            if (n == 0) return buf.Count == 0 ? null : buf.ToArray();
            if (one[0] == '\n') return buf.ToArray();
            buf.Add(one[0]);
        }
        return buf.ToArray();
    }

    private static async Task<byte[]> ReadCountedAsync(NetworkStream s, long count, CancellationToken stop)
    {
        count = Math.Clamp(count, 0, 1 << 20);
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(got), stop);
            if (n == 0) break;
            got += n;
        }
        if (s.ReadByte() < 0) { } // trailing zero octet
        return buf[..got];
    }
}

internal static class Spool
{
    public static string Folder(Settings s)
    {
        var f = Path.Combine(s.WorkFolder, "spool");
        Directory.CreateDirectory(f);
        return f;
    }

    /// <summary>Copies from the network to the spool file. limit -1 = until the peer closes.</summary>
    public static async Task<(long Bytes, bool TimedOut, bool TooBig)> CopyWithIdleTimeoutAsync(Stream src, Stream dst, long limit, Settings s, CancellationToken stop)
    {
        var buf = new byte[1 << 16];
        long total = 0, max = (long)s.MaxJobSizeMB * 1024 * 1024;
        while (limit < 0 || total < limit)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
            idle.CancelAfter(TimeSpan.FromSeconds(s.ReceiveIdleTimeoutSeconds));
            int want = limit < 0 ? buf.Length : (int)Math.Min(buf.Length, limit - total);
            int n;
            try { n = await src.ReadAsync(buf.AsMemory(0, want), idle.Token); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { return (total, true, false); }
            if (n == 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n), stop);
            total += n;
            if (total > max) return (total, false, true);
        }
        return (total, false, false);
    }

    /// <summary>A receive that broke off: still process what arrived (it will usually fail and land in Failed).</summary>
    public static void KeepPartial(ReceivedJob job, Settings s, JobQueue queue)
    {
        try
        {
            if (!File.Exists(job.SpoolPath)) return;
            job.Bytes = new FileInfo(job.SpoolPath).Length;
            if (job.Bytes == 0) { File.Delete(job.SpoolPath); return; }
            job.Incomplete = true;
            job.SaveMeta();
            queue.Enqueue(job);
        }
        catch (IOException) { }
    }
}
