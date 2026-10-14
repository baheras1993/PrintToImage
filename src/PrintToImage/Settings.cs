using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PrintToImage;

/// <summary>
/// Service settings, loaded from C:\ProgramData\PrintToImage\settings.json.
/// Changes take effect after the service is restarted.
/// </summary>
public sealed class Settings
{
    public static readonly string DataRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrintToImage");

    public static string DefaultSettingsPath => Path.Combine(DataRoot, "settings.json");

    // ---- Listeners ----
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int RawPort { get; set; } = 9100;
    public bool EnableLpd { get; set; } = false;
    public int LpdPort { get; set; } = 515;
    /// <summary>A connection that sends nothing for this long is closed (job is still processed if data arrived).</summary>
    public int ReceiveIdleTimeoutSeconds { get; set; } = 300;
    public int MaxJobSizeMB { get; set; } = 1024;
    public int MaxConcurrentConversions { get; set; } = 2;

    // ---- Output ----
    public string OutputRoot { get; set; } = @"D:\PrintToImage\Output";
    /// <summary>"pdf", "jpg" or "png".</summary>
    public string OutputFormat { get; set; } = "pdf";
    /// <summary>Optional per-device override of OutputFormat, e.g. { "Pentacam": "jpg" }.</summary>
    public Dictionary<string, string> DeviceOutputFormat { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>PDF output: false = one PDF with all pages (default), true = one PDF file per page (name_p1.pdf, name_p2.pdf, ...).</summary>
    public bool PdfOneFilePerPage { get; set; } = false;
    /// <summary>Optional per-device override of PdfOneFilePerPage, e.g. { "OCT": true }.</summary>
    public Dictionary<string, bool> DevicePdfOneFilePerPage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int JpgDpi { get; set; } = 300;
    public int JpgQuality { get; set; } = 90;
    /// <summary>Placeholders: {name} cleaned document name, {number} file number, {page} file number found on the page
    /// (PageNumberRegex), {device}, {date} yyyyMMdd, {time} HHmmss. A part left empty takes its separator with it.</summary>
    public string FileNameTemplate { get; set; } = "{name}";
    /// <summary>Windows account/group of the EMR import; gets access to the output folder (used by the installer and the settings app).</summary>
    public string EmrAccount { get; set; } = "";
    /// <summary>Who may send print jobs (Windows Firewall): "LocalSubnet", "DeviceIPs" (only the IPs in Devices), "Any",
    /// or a comma-separated list of addresses. Applied by the installer and the settings app.</summary>
    public string FirewallRemoteAddress { get; set; } = "LocalSubnet";
    /// <summary>Name of the shared Windows printer on this server that the imaging PCs print to (\\SERVER\name).</summary>
    public string SharedPrinterName { get; set; } = "PrintToImage";
    /// <summary>Paper size of the shared printer: A4 or Letter.</summary>
    public string SharedPrinterPaperSize { get; set; } = "A4";
    public string UnassignedFolderName { get; set; } = "Unassigned";
    public string UnknownDeviceFolderName { get; set; } = "UnknownDevice";
    public string FailedFolderName { get; set; } = "Failed";

    /// <summary>Imaging PC -> device (sub-folder) name. The key is the PC's IP address or its computer name
    /// (computer names identify jobs that come through the shared printer).</summary>
    public Dictionary<string, string> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["192.168.1.20"] = "OCT",
        ["192.168.1.21"] = "Pentacam",
        ["192.168.1.22"] = "IOLMaster",
    };

    // ---- Job name handling ----
    /// <summary>Regex used to find the patient file number in the cleaned job name.
    /// If it has a group named "num" (or any capture group) that group is used, otherwise the whole match.</summary>
    public string FileNumberRegex { get; set; } = @"\d+";
    /// <summary>Optional: regex for a file number printed ON THE PAGE (devices that name the print after the report,
    /// e.g. an IOLMaster: "IOL-SRK-T", with the patient's ID on the page). Empty = off. Same groups as FileNumberRegex.
    /// The first match in the job's text is used as {page}; a job whose name has no file number is still filed
    /// under its device when the page has one.</summary>
    public string PageNumberRegex { get; set; } = "";
    /// <summary>Regexes (case-insensitive) removed from the job name, e.g. application prefixes.</summary>
    public List<string> StripPatterns { get; set; } = new()
    {
        @"^Microsoft (Office )?(Word|Excel|PowerPoint|Publisher|Visio) - ",
        @"^(Adobe )?Acrobat( Reader)?( DC)? - ",
        @" - (Adobe )?Acrobat( Reader)?( DC)?$",
        @"^(Notepad|WordPad|Paint) - ",
        @" - (Notepad|WordPad|Paint)$",
    };
    /// <summary>Original file extensions removed from the end of the job name.</summary>
    public List<string> StripExtensions { get; set; } = new()
    {
        ".pdf", ".doc", ".docx", ".rtf", ".txt", ".xls", ".xlsx", ".ppt", ".pptx", ".xps", ".oxps",
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".htm", ".html", ".xml", ".dcm", ".ps", ".prn",
    };
    /// <summary>Code page for job names that are not valid UTF-8, e.g. 1252 Western, 1256 Arabic, 1251 Cyrillic.
    /// 0 = the server's Windows (ANSI) code page.</summary>
    public int FallbackCodePage { get; set; } = 0;

    [JsonIgnore]
    public int EffectiveCodePage => FallbackCodePage > 0 ? FallbackCodePage : System.Globalization.CultureInfo.InstalledUICulture.TextInfo.ANSICodePage;

    // ---- Rendering ----
    /// <summary>Maximum time to render one job before it is moved to the Failed folder.</summary>
    public int RenderTimeoutSeconds { get; set; } = 300;
    // ---- Archive / logs ----
    public string ArchiveFolder { get; set; } = Path.Combine(DataRoot, "archive");
    /// <summary>Days to keep the raw received jobs. 0 = do not archive.</summary>
    public int ArchiveRetentionDays { get; set; } = 30;
    public string LogFolder { get; set; } = Path.Combine(DataRoot, "logs");
    /// <summary>Days to keep service-*.log files. The jobs-*.csv audit log is never deleted automatically.</summary>
    public int LogRetentionDays { get; set; } = 90;

    /// <summary>Spool and temporary files while a job is received and converted.</summary>
    public string WorkFolder { get; set; } = Path.Combine(DataRoot, "work");
    [JsonIgnore] public string LoadedFrom { get; private set; } = "";
    [JsonIgnore] public Regex FileNumber { get; private set; } = new(@"\d+");
    [JsonIgnore] public Regex? PageNumber { get; private set; }
    [JsonIgnore] public IReadOnlyList<Regex> StripRegexes { get; private set; } = Array.Empty<Regex>();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loads settings; writes a default file if none exists.</summary>
    public static Settings Load(string? path = null)
    {
        path ??= DefaultSettingsPath;
        Settings s;
        if (File.Exists(path))
        {
            s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException($"Settings file {path} is empty.");
        }
        else
        {
            s = new Settings();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(s, JsonOptions));
        }
        s.LoadedFrom = path;
        s.Validate();
        return s;
    }

    public void Validate()
    {
        var errors = new List<string>();
        Devices ??= new();
        DeviceOutputFormat ??= new();
        DevicePdfOneFilePerPage ??= new();
        StripPatterns ??= new();
        StripExtensions ??= new();
        FileNameTemplate = string.IsNullOrWhiteSpace(FileNameTemplate) ? "{name}" : FileNameTemplate;

        OutputFormat = NormalizeFormat(OutputFormat, errors, nameof(OutputFormat));
        foreach (var key in DeviceOutputFormat.Keys.ToList())
            DeviceOutputFormat[key] = NormalizeFormat(DeviceOutputFormat[key], errors, $"DeviceOutputFormat[{key}]");

        if (RawPort is < 1 or > 65535) errors.Add("RawPort must be 1-65535.");
        SharedPrinterPaperSize = string.Equals(SharedPrinterPaperSize, "Letter", StringComparison.OrdinalIgnoreCase) ? "Letter" : "A4";
        if (string.IsNullOrWhiteSpace(SharedPrinterName) || SharedPrinterName.IndexOfAny(new[] { '\\', '/', ',', '!' }) >= 0)
            errors.Add("SharedPrinterName must be a printer name without \\ / , !");
        if (EnableLpd && LpdPort is < 1 or > 65535) errors.Add("LpdPort must be 1-65535.");
        if (JpgDpi is < 50 or > 1200) errors.Add("JpgDpi must be 50-1200.");
        if (JpgQuality is < 1 or > 100) errors.Add("JpgQuality must be 1-100.");
        if (string.IsNullOrWhiteSpace(OutputRoot)) errors.Add("OutputRoot is required.");
        if (ArchiveRetentionDays < 0) errors.Add("ArchiveRetentionDays must be 0 or more.");
        if (MaxConcurrentConversions < 1) MaxConcurrentConversions = 1;
        if (RenderTimeoutSeconds < 10) RenderTimeoutSeconds = 10;
        if (ReceiveIdleTimeoutSeconds < 10) ReceiveIdleTimeoutSeconds = 10;
        if (!System.Net.IPAddress.TryParse(ListenAddress, out _)) errors.Add("ListenAddress is not a valid IP address.");

        try { FileNumber = new Regex(FileNumberRegex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException ex) { errors.Add($"FileNumberRegex is invalid: {ex.Message}"); }
        PageNumberRegex ??= "";
        PageNumber = null;
        if (PageNumberRegex.Trim().Length > 0)
        {
            try { PageNumber = new Regex(PageNumberRegex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException ex) { errors.Add($"PageNumberRegex is invalid: {ex.Message}"); }
        }

        var strip = new List<Regex>();
        foreach (var p in StripPatterns)
        {
            try { strip.Add(new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))); }
            catch (ArgumentException ex) { errors.Add($"StripPatterns entry '{p}' is invalid: {ex.Message}"); }
        }
        StripRegexes = strip;

        // Rebuild with case-insensitive comparers (the JSON deserializer creates plain dictionaries).
        Devices = new Dictionary<string, string>(Devices, StringComparer.OrdinalIgnoreCase);
        DeviceOutputFormat = new Dictionary<string, string>(DeviceOutputFormat, StringComparer.OrdinalIgnoreCase);
        DevicePdfOneFilePerPage = new Dictionary<string, bool>(DevicePdfOneFilePerPage, StringComparer.OrdinalIgnoreCase);

        if (errors.Count > 0)
            throw new InvalidDataException("Invalid settings in " + LoadedFrom + ":" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    private static string NormalizeFormat(string? value, List<string> errors, string name)
    {
        var v = (value ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (v == "jpeg") v = "jpg";
        if (v is not ("pdf" or "jpg" or "png")) errors.Add($"{name} must be \"pdf\", \"jpg\" or \"png\".");
        return v;
    }

    public string FormatForDevice(string device) =>
        DeviceOutputFormat.TryGetValue(device, out var f) ? f : OutputFormat;

    public bool PdfPerPageForDevice(string device) =>
        DevicePdfOneFilePerPage.TryGetValue(device, out var v) ? v : PdfOneFilePerPage;
}
