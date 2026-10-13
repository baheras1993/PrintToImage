using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PrintToImage;

/// <summary>
/// When imaging PCs print to the shared printer on this server, the Windows spooler forwards each job to the service
/// over 127.0.0.1. The spooler still knows which computer and user submitted the job; this class asks it.
/// </summary>
public static class Spooler
{
    public sealed record JobInfo(string Printer, uint JobId, string Computer, string User, string Document);

    private const int PRINTER_ENUM_LOCAL = 2;
    private const int JOB_STATUS_PRINTING = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTER_INFO_2
    {
        public string? pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation;
        public IntPtr pDevMode;
        public string? pSepFile, pPrintProcessor, pDatatype, pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JOB_INFO_1
    {
        public uint JobId;
        public string? pPrinterName, pMachineName, pUserName, pDocument, pDatatype, pStatus;
        public uint Status, Priority, Position, TotalPages, PagesPrinted;
        public SYSTEMTIME Submitted;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrinters(int flags, string? name, int level, IntPtr buf, int cbBuf, out int needed, out int returned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumJobs(IntPtr printer, int firstJob, int noJobs, int level, IntPtr buf, int cbBuf, out int needed, out int returned);

    /// <summary>Standard TCP/IP ports of this computer that point at the PrintToImage service.</summary>
    public static HashSet<string> PortsToService(int rawPort)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var ports = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports");
            if (ports is null) return result;
            var self = SelfAddresses();
            foreach (var name in ports.GetSubKeyNames())
            {
                using var p = ports.OpenSubKey(name);
                if (p is null) continue;
                var host = (p.GetValue("HostName") as string)?.Trim() ?? "";
                var ip = (p.GetValue("IPAddress") as string)?.Trim() ?? "";
                int port = p.GetValue("PortNumber") is int n ? n : 9100;
                if (port == rawPort && (self.Contains(host) || self.Contains(ip))) result.Add(name);
            }
        }
        catch (Exception) { }
        return result;
    }

    private static HashSet<string> SelfAddresses()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "::1", Environment.MachineName };
        try
        {
            foreach (var a in Dns.GetHostAddresses(Dns.GetHostName())) s.Add(a.ToString());
            s.Add(Dns.GetHostName());
        }
        catch (Exception) { }
        return s;
    }

    /// <summary>
    /// The job the spooler is currently sending to the service (status "printing" on a printer whose port points here).
    /// Returns null if none is found (for example when the job came directly over the network).
    /// </summary>
    public static JobInfo? CurrentJob(int rawPort)
    {
        try
        {
            var ports = PortsToService(rawPort);
            if (ports.Count == 0) return null;
            JobInfo? firstInQueue = null;
            uint firstPos = uint.MaxValue;
            foreach (var printer in LocalPrinters())
            {
                if (printer.Port is null || !printer.Port.Split(',').Any(p => ports.Contains(p.Trim()))) continue;
                foreach (var j in Jobs(printer.Name))
                {
                    var info = ToInfo(printer.Name, j);
                    if ((j.Status & JOB_STATUS_PRINTING) != 0) return info;
                    // Not flagged yet: the spooler sends jobs in queue order, so the first one is the candidate.
                    if (j.Position < firstPos) { firstPos = j.Position; firstInQueue = info; }
                }
            }
            return firstInQueue;
        }
        catch (Exception) { /* spooler not running or not accessible */ }
        return null;
    }

    /// <summary>Re-reads one job (its document name can still change while the application is spooling it).</summary>
    public static JobInfo? GetJob(string printer, uint jobId)
    {
        try
        {
            foreach (var j in Jobs(printer))
                if (j.JobId == jobId) return ToInfo(printer, j);
        }
        catch (Exception) { }
        return null;
    }

    private static JobInfo ToInfo(string printer, JOB_INFO_1 j) =>
        new(printer, j.JobId, (j.pMachineName ?? "").TrimStart('\\'), j.pUserName ?? "", j.pDocument ?? "");

    /// <summary>Diagnostics text: ports that point at the service, printers, and their jobs.</summary>
    public static string Diagnose(int rawPort)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var ports = PortsToService(rawPort);
            sb.AppendLine($"Ports to this service (127.0.0.1/this PC, port {rawPort}): {(ports.Count == 0 ? "(none)" : string.Join(", ", ports))}");
            foreach (var p in LocalPrinters())
            {
                bool ours = p.Port is not null && p.Port.Split(',').Any(x => ports.Contains(x.Trim()));
                sb.AppendLine($"Printer \"{p.Name}\"  port: {p.Port}{(ours ? "   <- PrintToImage" : "")}");
                foreach (var j in Jobs(p.Name))
                    sb.AppendLine($"    job {j.JobId}: status 0x{j.Status:X} \"{j.pStatus}\"  from {j.pMachineName}  user {j.pUserName}  document \"{j.pDocument}\"");
            }
            var cur = CurrentJob(rawPort);
            sb.AppendLine("Job being sent to the service now: " + (cur is null ? "(none)" : $"{cur.Computer} / {cur.User} / \"{cur.Document}\""));
        }
        catch (Exception e) { sb.AppendLine("Error: " + e.Message); }
        return sb.ToString();
    }

    private static List<(string Name, string? Port)> LocalPrinters()
    {
        var list = new List<(string, string?)>();
        EnumPrinters(PRINTER_ENUM_LOCAL, null, 2, IntPtr.Zero, 0, out int needed, out _);
        if (needed <= 0) return list;
        var buf = Marshal.AllocHGlobal(needed);
        try
        {
            if (!EnumPrinters(PRINTER_ENUM_LOCAL, null, 2, buf, needed, out _, out int count)) return list;
            int size = Marshal.SizeOf<PRINTER_INFO_2>();
            for (int i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buf + i * size);
                if (info.pPrinterName is not null) list.Add((info.pPrinterName, info.pPortName));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    private static List<JOB_INFO_1> Jobs(string printer)
    {
        var list = new List<JOB_INFO_1>();
        if (!OpenPrinter(printer, out var h, IntPtr.Zero)) return list;
        try
        {
            EnumJobs(h, 0, 255, 1, IntPtr.Zero, 0, out int needed, out _);
            if (needed <= 0) return list;
            var buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (!EnumJobs(h, 0, 255, 1, buf, needed, out _, out int count)) return list;
                int size = Marshal.SizeOf<JOB_INFO_1>();
                for (int i = 0; i < count; i++) list.Add(Marshal.PtrToStructure<JOB_INFO_1>(buf + i * size));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { ClosePrinter(h); }
        return list;
    }
}
