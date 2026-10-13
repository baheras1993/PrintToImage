using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using PrintToImage;

namespace PrintToImageSettings;

/// <summary>Service control, firewall and folder permissions (the same steps install.ps1 performs).</summary>
internal static class Admin
{
    public const string ServiceName = "PrintToImage";

    public static ServiceControllerStatus? ServiceStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status;
        }
        catch (InvalidOperationException) { return null; } // not installed
    }

    public static void ControlService(bool start, bool stop)
    {
        using var sc = new ServiceController(ServiceName);
        if (stop && sc.Status != ServiceControllerStatus.Stopped)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        }
        if (start)
        {
            sc.Refresh();
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }
    }

    private static RegistryKey? ServiceKey() => Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);

    /// <summary>Path of the installed service executable (for the firewall rule).</summary>
    public static string ServiceExePath()
    {
        using var key = ServiceKey();
        var image = key?.GetValue("ImagePath") as string;
        if (!string.IsNullOrWhiteSpace(image))
        {
            image = image.Trim();
            if (image.StartsWith('"')) image = image[1..image.IndexOf('"', 1)];
            return Environment.ExpandEnvironmentVariables(image);
        }
        return Path.Combine(AppContext.BaseDirectory, "PrintToImage.exe");
    }

    /// <summary>The account the service runs as, in a form icacls accepts.</summary>
    public static string ServiceAccount()
    {
        using var key = ServiceKey();
        var obj = key?.GetValue("ObjectName") as string;
        if (string.IsNullOrWhiteSpace(obj)) return @"NT SERVICE\" + ServiceName;
        return obj.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ? "*S-1-5-18" : obj;
    }

    /// <summary>Remote addresses for the firewall rule from the FirewallRemoteAddress setting.</summary>
    public static string[] RemoteAddresses(Settings s)
    {
        var v = (s.FirewallRemoteAddress ?? "").Trim();
        if (v.Equals("DeviceIPs", StringComparison.OrdinalIgnoreCase))
        {
            var ips = s.Devices.Keys.Where(k => System.Net.IPAddress.TryParse(k, out _)).ToArray();
            return ips.Length > 0 ? ips : new[] { "LocalSubnet" };
        }
        if (v.Length == 0) return new[] { "LocalSubnet" };
        return v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Recreates the PrintToImage firewall rules.</summary>
    public static (bool Ok, string Output) ApplyFirewall(Settings s)
    {
        var exe = ServiceExePath().Replace("'", "''");
        var remote = string.Join(",", RemoteAddresses(s).Select(a => "'" + a.Replace("'", "''") + "'"));
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine("Get-NetFirewallRule -Group 'PrintToImage' -ErrorAction SilentlyContinue | Remove-NetFirewallRule");
        script.AppendLine($"New-NetFirewallRule -DisplayName 'PrintToImage raw printing (TCP {s.RawPort})' -Group 'PrintToImage' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {s.RawPort} -Program '{exe}' -RemoteAddress @({remote}) -Profile Any | Out-Null");
        if (s.EnableLpd)
            script.AppendLine($"New-NetFirewallRule -DisplayName 'PrintToImage LPD printing (TCP {s.LpdPort})' -Group 'PrintToImage' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {s.LpdPort} -Program '{exe}' -RemoteAddress @({remote}) -Profile Any | Out-Null");
        return PowerShell(script.ToString());
    }

    /// <summary>Restricts a folder to SYSTEM, Administrators, the service account and the EMR account.</summary>
    public static (bool Ok, string Output) SecureFolder(string path, string emrAccount)
    {
        Directory.CreateDirectory(path);
        var args = new List<string> { path, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F" };
        if (ServiceStatus() is not null) args.Add(ServiceAccount() + ":(OI)(CI)M");
        if (!string.IsNullOrWhiteSpace(emrAccount)) args.Add(emrAccount.Trim() + ":(OI)(CI)M");
        args.Add("/Q");
        var r = Run("icacls.exe", args);
        if (!r.Ok) return r;
        if (Directory.EnumerateFileSystemEntries(path).Any())
            Run("icacls.exe", new[] { Path.Combine(path, "*"), "/reset", "/T", "/C", "/Q" });
        return r;
    }

    // ------------------------------------------------------------------ shared printer

    public sealed record PrinterState(bool SpoolerRunning, bool Exists, bool Shared, string ShareName, string Port, string Driver);

    /// <summary>State of the shared printer (runs Get-Printer; takes about a second).</summary>
    public static PrinterState GetPrinter(string name)
    {
        bool spooler;
        try { using var sc = new ServiceController("Spooler"); spooler = sc.Status == ServiceControllerStatus.Running; }
        catch (InvalidOperationException) { spooler = false; }
        if (!spooler) return new PrinterState(false, false, false, "", "", "");
        var r = PowerShell($"$p = Get-Printer -Name '{name.Replace("'", "''")}' -ErrorAction SilentlyContinue; if ($p) {{ '{{0}}|{{1}}|{{2}}|{{3}}' -f $p.Shared, $p.ShareName, $p.PortName, $p.DriverName }}");
        var parts = r.Output.Trim().Split('|');
        if (!r.Ok || parts.Length < 4) return new PrinterState(true, false, false, "", "", "");
        return new PrinterState(true, true, parts[0].Equals("True", StringComparison.OrdinalIgnoreCase), parts[1], parts[2], parts[3]);
    }

    /// <summary>Runs shared-printer.ps1 (next to this program) to create/repair or remove the shared printer.</summary>
    public static (bool Ok, string Output) SharedPrinter(string name, int rawPort, string paperSize, bool remove)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "shared-printer.ps1");
        if (!File.Exists(script)) return (false, "shared-printer.ps1 was not found next to this program.");
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Name", name, "-RawPort", rawPort.ToString(), "-PaperSize", paperSize };
        if (remove) args.Add("-Remove");
        return Run("powershell.exe", args);
    }

    /// <summary>Prints the Windows test page on a printer (its document name is "Test Page").</summary>
    public static (bool Ok, string Output) PrintTestPage(string name) =>
        Run("rundll32.exe", new[] { "printui.dll,PrintUIEntry", "/k", "/n", name });

    public static (bool Ok, string Output) PowerShell(string script)
    {
        // No progress records: Windows PowerShell writes them to stderr as CLIXML ("Preparing modules for first use").
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference = 'SilentlyContinue'\n" + script));
        return Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded });
    }

    public static (bool Ok, string Output) Run(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        bool ok = p.ExitCode == 0;
        // Error output only matters when the command failed (it may contain CLIXML noise otherwise).
        var err = stderr.StartsWith("#< CLIXML", StringComparison.Ordinal) ? "" : stderr;
        return (ok, (ok ? stdout.Result : stdout.Result + err).Trim());
    }

    public static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenFile(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else OpenFolder(Path.GetDirectoryName(path) ?? path);
    }
}

/// <summary>Reads the service's jobs-yyyy-MM.csv log.</summary>
internal static class JobLogReader
{
    public sealed record Row(string Time, string SenderIp, string Device, string RawName, string FileNumber, string Status, string Output, string Error, string ClientComputer)
    {
        /// <summary>Computer name for jobs through the shared printer, otherwise the sender's IP address.</summary>
        public string Sender => ClientComputer.Length > 0 ? ClientComputer : SenderIp;
    }

    public static List<Row> ReadRecent(string logFolder, int max = 200)
    {
        var rows = new List<Row>();
        if (!Directory.Exists(logFolder)) return rows;
        foreach (var file in Directory.GetFiles(logFolder, "jobs-*.csv").OrderByDescending(f => f).Take(3))
        {
            string[] lines;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs, Encoding.UTF8);
                lines = r.ReadToEnd().Split('\n');
            }
            catch (IOException) { continue; }
            foreach (var line in lines.Skip(1).Reverse())
            {
                var f = ParseCsv(line.TrimEnd('\r'));
                if (f.Count < 16) continue;
                rows.Add(new Row(f[0], f[3], f[4], Unescape(f[5]), f[8], f[9], f[10], Unescape(f[15]), f.Count > 16 ? f[16] : ""));
                if (rows.Count >= max) return rows;
            }
        }
        return rows;
    }

    private static string Unescape(string v) => v.StartsWith('\'') ? v[1..] : v;

    private static List<string> ParseCsv(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }
}
