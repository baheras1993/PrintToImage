using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using PrintToImage;

namespace PrintToImageSettings;

internal sealed class MainForm : Form
{
    private readonly string _settingsPath;
    private Settings _loaded = new();
    private bool _dirty, _loading;

    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };

    // Output
    private readonly TextBox _outputRoot = new() { Dock = DockStyle.Fill };
    private readonly RadioButton _pdf = new() { Text = "PDF", AutoSize = true };
    private readonly RadioButton _jpg = new() { Text = "JPG", AutoSize = true };
    private readonly RadioButton _png = new() { Text = "PNG", AutoSize = true };
    private readonly CheckBox _split = new() { Text = "One PDF file per page (otherwise all pages in one PDF)", AutoSize = true };
    private readonly NumericUpDown _dpi = new() { Minimum = 50, Maximum = 1200, Increment = 50, Width = 90 };
    private readonly NumericUpDown _quality = new() { Minimum = 1, Maximum = 100, Width = 90 };
    private readonly TextBox _template = new() { Dock = DockStyle.Fill };
    private readonly Label _templatePreview = Hint("");
    private readonly TextBox _emr = new() { Dock = DockStyle.Fill };
    private readonly TextBox _unassigned = new() { Width = 200 }, _unknown = new() { Width = 200 }, _failed = new() { Width = 200 };

    // Shared printer
    private readonly Label _spStatus = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold), Padding = new Padding(0, 6, 0, 0) };
    private readonly TextBox _spPath = new() { ReadOnly = true, Width = 330, Font = new Font(FontFamily.GenericMonospace, 10f) };
    private readonly TextBox _spName = new() { Width = 200 };
    private readonly ComboBox _spPaper = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly Button _spCreate = Btn("Create / repair the shared printer"), _spTest = Btn("Print a Windows test page"), _spRemove = Btn("Remove");
    private Admin.PrinterState? _spState;

    // Devices
    private readonly DataGridView _devices = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true, RowHeadersWidth = 28,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle,
    };

    // Job names
    private readonly ComboBox _regexPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly TextBox _regex = new() { Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9.5f) };
    private readonly TextBox _pageRegex = new() { Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9.5f) };
    private readonly TextBox _testPage = new() { Dock = DockStyle.Fill, Text = "Last name: ID: Date of birth: D1935 27/05/1993 K1: 43.95 D" };
    private readonly TextBox _strip = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 100, Font = new Font(FontFamily.GenericMonospace, 9f), WordWrap = false };
    private readonly TextBox _exts = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _codePage = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly TextBox _testName = new() { Dock = DockStyle.Fill, Text = "Microsoft Word - 12345 Report.docx" };
    private readonly Label _testResult = new() { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 9.5f), Padding = new Padding(0, 4, 0, 4) };

    // Network
    private readonly NumericUpDown _rawPort = new() { Minimum = 1, Maximum = 65535, Width = 90 };
    private readonly CheckBox _lpd = new() { Text = "Also accept LPR/LPD jobs on port", AutoSize = true };
    private readonly NumericUpDown _lpdPort = new() { Minimum = 1, Maximum = 65535, Width = 90 };
    private readonly TextBox _listen = new() { Width = 200 };
    private readonly RadioButton _fwSubnet = new() { Text = "Any computer in the local network (local subnet)", AutoSize = true };
    private readonly RadioButton _fwDevices = new() { Text = "Only the PCs listed on the Devices tab (recommended)", AutoSize = true };
    private readonly RadioButton _fwCustom = new() { Text = "These addresses:", AutoSize = true };
    private readonly TextBox _fwCustomText = new() { Width = 360 };
    private readonly NumericUpDown _idle = new() { Minimum = 10, Maximum = 3600, Width = 90 };
    private readonly NumericUpDown _maxMb = new() { Minimum = 1, Maximum = 10000, Width = 90 };
    private readonly NumericUpDown _conc = new() { Minimum = 1, Maximum = 16, Width = 90 };
    private readonly NumericUpDown _renderTimeout = new() { Minimum = 10, Maximum = 3600, Width = 90 };

    // Archive & logs
    private readonly TextBox _archive = new() { Dock = DockStyle.Fill }, _logs = new() { Dock = DockStyle.Fill }, _work = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _archiveDays = new() { Minimum = 0, Maximum = 3650, Width = 90 };
    private readonly NumericUpDown _logDays = new() { Minimum = 0, Maximum = 3650, Width = 90 };

    // Service & jobs
    private readonly Label _svcStatus = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 11f, FontStyle.Bold) };
    private readonly Button _btnStart = Btn("Start"), _btnStop = Btn("Stop"), _btnRestart = Btn("Restart");
    private readonly TextBox _testTitle = new() { Width = 330, Text = "Test 12345" };
    private readonly ListView _jobs = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

    private readonly Button _save = new() { Text = "Save", Width = 110, Height = 32 };
    private readonly Button _saveRestart = new() { Text = "Save and restart service", Width = 190, Height = 32 };
    private readonly Button _close = new() { Text = "Close", Width = 110, Height = 32 };
    private readonly Label _status = new() { UseMnemonic = false, AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 9, 0, 0) };

    private static readonly (string Text, string Regex)[] Presets =
    {
        ("First group of digits (default)", @"\d+"),
        ("Exactly 6 digits", @"\b\d{6}\b"),
        ("Exactly 7 digits", @"\b\d{7}\b"),
        ("Digits after \"MRN\"", @"MRN\D*(?<num>\d+)"),
        ("Digits after \"ID\"", @"ID\D*(?<num>\d+)"),
        ("Last group of digits", @"(\d+)(?!.*\d)"),
        ("Custom (type below)", ""),
    };

    private static readonly (int Cp, string Text)[] CodePages =
    {
        (0, "Server's Windows language (automatic)"), (1256, "Arabic (1256)"), (1252, "Western European (1252)"),
        (1250, "Central European (1250)"), (1251, "Cyrillic (1251)"), (1253, "Greek (1253)"), (1254, "Turkish (1254)"),
        (1255, "Hebrew (1255)"), (874, "Thai (874)"), (936, "Chinese Simplified (936)"), (950, "Chinese Traditional (950)"),
        (932, "Japanese (932)"), (949, "Korean (949)"), (65001, "UTF-8 (65001)"),
    };

    public MainForm(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Settings.DefaultSettingsPath;
        Text = "PrintToImage Settings";
        Font = SystemFonts.MessageBoxFont ?? Font;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(940, 680);
        MinimumSize = new Size(820, 600);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

        _tabs.TabPages.Add(OutputTab());
        _tabs.TabPages.Add(PrinterTab());
        _tabs.TabPages.Add(DevicesTab());
        _tabs.TabPages.Add(NamesTab());
        _tabs.TabPages.Add(NetworkTab());
        _tabs.TabPages.Add(ArchiveTab());
        _tabs.TabPages.Add(ServiceTab());

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8, 8, 8, 8) };
        bottom.Controls.AddRange(new Control[] { _close, _saveRestart, _save, _status });
        Controls.Add(_tabs);
        Controls.Add(bottom);
        AcceptButton = _save;

        _save.Click += (_, _) => Save(restart: false);
        _saveRestart.Click += (_, _) => Save(restart: true);
        _close.Click += (_, _) => Close();
        _timer.Tick += (_, _) => RefreshService();
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab?.Text == "Service && jobs") LoadJobs();
            if (_tabs.SelectedTab?.Text == "Shared printer") RefreshPrinter();
        };

        LoadSettings();
        HookDirty(this);
        RefreshService();
        _timer.Start();
    }

    // ------------------------------------------------------------------ layout helpers

    private static Label Hint(string text) => new() { Text = text, UseMnemonic = false, AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(700, 0), Padding = new Padding(0, 0, 0, 6) };
    private static Button Btn(string text) => new() { Text = text, AutoSize = true, Height = 30, Padding = new Padding(6, 0, 6, 0) };

    private static TableLayoutPanel Grid()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoScroll = true, Padding = new Padding(14, 12, 14, 12) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return t;
    }

    private static void Row(TableLayoutPanel t, string label, Control c, Control? extra = null, string? hint = null)
    {
        int r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(new Label { Text = label, UseMnemonic = false, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 12, 0) }, 0, r);
        c.Margin = new Padding(3, 4, 3, 2);
        t.Controls.Add(c, 1, r);
        if (extra is not null) t.Controls.Add(extra, 2, r);
        if (hint is not null)
        {
            int h = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var l = Hint(hint);
            t.Controls.Add(l, 1, h);
            t.SetColumnSpan(l, 2);
        }
    }

    private static void Heading(TableLayoutPanel t, string text)
    {
        int r = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var l = new Label { Text = text, UseMnemonic = false, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10f, FontStyle.Bold), Padding = new Padding(0, 10, 0, 4) };
        t.Controls.Add(l, 0, r);
        t.SetColumnSpan(l, 3);
    }

    private static FlowLayoutPanel Flow(params Control[] cs)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        f.Controls.AddRange(cs);
        return f;
    }

    private Button BrowseButton(TextBox target)
    {
        var b = Btn("Browse…");
        b.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = target.Text, ShowNewFolderButton = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.SelectedPath;
        };
        return b;
    }

    private Button OpenButton(TextBox target)
    {
        var b = Btn("Open");
        b.Click += (_, _) => Admin.OpenFolder(target.Text);
        return b;
    }

    // ------------------------------------------------------------------ tabs

    private TabPage OutputTab()
    {
        var t = Grid();
        Heading(t, "Where and how jobs are saved");
        Row(t, "Output folder", _outputRoot, Flow(BrowseButton(_outputRoot), OpenButton(_outputRoot)),
            "Each device gets a subfolder here (e.g. Reception, Lab). Other programs, such as an EMR or document system, can pick the files up from there.");
        Row(t, "Format", Flow(_pdf, _jpg, _png),
            hint: "PDF: one file with sharp, searchable text.  JPG: one small image per page.  PNG: one lossless image per page (larger).\nDevices can have their own format on the Devices tab.");
        Row(t, "PDF pages", _split);
        Row(t, "Image resolution (DPI)", Flow(_dpi, new Label { Text = "for JPG and PNG  (150 = smaller files, 300 = standard, 600 = very sharp)", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(6, 6, 0, 0) }));
        Row(t, "JPG quality", Flow(_quality, new Label { Text = "1–100 (90 recommended). PNG is always lossless.", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(6, 6, 0, 0) }));
        Row(t, "File name", _template, hint: "{name} = document name   {number} = file number   {page} = number on the page   {device}   {date} = yyyyMMdd   {time} = HHmmss");
        Row(t, "", _templatePreview);
        Heading(t, "Access");
        Row(t, "Reader account", _emr, hint: "Windows user or group of the program that reads the files (e.g. an EMR or document system), e.g. OFFICE\\svc-import. Only administrators, the PrintToImage service and this account can open the output folder.");
        Heading(t, "Special folders (inside the output folder)");
        Row(t, "No file number", _unassigned, hint: "Jobs whose name has no file number. Someone must file these by hand.");
        Row(t, "Unknown sender", _unknown, hint: "Jobs from IP addresses not listed on the Devices tab.");
        Row(t, "Failed jobs", _failed, hint: "Jobs that could not be converted; the original data is kept here.");

        _template.TextChanged += (_, _) => UpdatePreviews();
        _pdf.CheckedChanged += (_, _) => UpdateFormatControls();
        _jpg.CheckedChanged += (_, _) => UpdateFormatControls();
        _png.CheckedChanged += (_, _) => UpdateFormatControls();
        return Page("Output", t);
    }

    private TabPage PrinterTab()
    {
        var t = Grid();
        Heading(t, "The printer the PCs print to");
        var copy = Btn("Copy");
        copy.Click += (_, _) => { if (_spPath.Text.Length > 0) Clipboard.SetText(_spPath.Text); };
        Row(t, "Status", _spStatus);
        Row(t, "Network path", Flow(_spPath, copy), hint: "Connect every PC to this printer (see below). Jobs go from it to PrintToImage on this server.");
        Row(t, "Printer name", _spName, hint: "Changing the name creates a new shared printer; the PCs must then be connected again.");
        _spPaper.Items.AddRange(new object[] { "A4", "Letter" });
        Row(t, "Paper size", _spPaper);
        Row(t, "", Flow(_spCreate, _spTest, _spRemove),
            hint: "Create / repair: sets up the printer with the PostScript driver built into Windows, shares it, turns on the Print Spooler and the\n" +
                  "\"File and Printer Sharing\" firewall rules (domain/private networks). Safe to run again at any time.\n" +
                  "Test page: Windows names it \"Test Page\" (no file number), so it is saved in the no-file-number folder — that is expected.");

        Heading(t, "Connect a PC (Windows 10 / 11)");
        Row(t, "", Hint("1.  Settings → Bluetooth & devices → Printers & scanners → Add device → \"Add manually\".\n" +
                        "2.  Choose \"Select a shared printer by name\" and type the network path shown above, e.g. \\\\SERVER\\PrintToImage → Next.\n" +
                        "3.  If asked for a password, enter a Windows account of this server (see below). Finish.\n" +
                        "4.  In any program choose this printer, print a test document, and check the Service & jobs tab here."));
        Heading(t, "Windows 7");
        Row(t, "", Hint("Devices and Printers → Add a printer → Add a network printer → \"The printer that I want isn't listed\" →\n" +
                        "\"Select a shared printer by name\" → \\\\SERVER\\PrintToImage."));
        Heading(t, "If the PCs are not in the same Windows domain");
        Row(t, "", Hint("Create a Windows user on this server (e.g. \"printuser\"). On each PC open Credential Manager → Windows Credentials →\n" +
                        "\"Add a Windows credential\": server name, SERVER\\printuser and its password. Then connect the printer as above.\n" +
                        "If a PC cannot connect to a shared printer at all, print directly instead: add a printer with a Standard TCP/IP port to this\n" +
                        "server's IP address, Raw, port 9100, and a classic PostScript driver such as \"MS Publisher Color Printer\" — not the Microsoft PS\n" +
                        "Class Driver, which does not send the document name when printing directly (see README.txt, section 6)."));
        Heading(t, "How jobs are assigned to devices");
        Row(t, "", Hint("Jobs through the shared printer are recognised by the PC's computer name; list the PCs on the Devices tab by\n" +
                        "computer name. (Jobs sent directly to port 9100 are recognised by IP address.)"));

        _spCreate.Click += (_, _) => PrinterAction(remove: false);
        _spRemove.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"Remove the shared printer \"{_loaded.SharedPrinterName}\"? The PCs can then no longer print to it.",
                    "PrintToImage", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) PrinterAction(remove: true);
        };
        _spTest.Click += (_, _) =>
        {
            var r = Admin.PrintTestPage(_loaded.SharedPrinterName);
            _status.Text = r.Ok ? "Test page sent to the shared printer; see the Service & jobs tab in a few seconds." : "Test page failed: " + r.Output;
        };
        return Page("Shared printer", t);
    }

    private async void RefreshPrinter()
    {
        _spStatus.Text = "checking…";
        _spStatus.ForeColor = Color.DimGray;
        var name = _loaded.SharedPrinterName;
        var st = await Task.Run(() => Admin.GetPrinter(name));
        _spState = st;
        _spPath.Text = st.Exists && st.Shared ? $@"\\{Environment.MachineName}\{st.ShareName}" : "";
        (_spStatus.Text, _spStatus.ForeColor) = st switch
        {
            { SpoolerRunning: false } => ("not set up (the Print Spooler service is not running)", Color.Firebrick),
            { Exists: false } => ("not set up", Color.Firebrick),
            { Shared: false } => ("exists but is not shared — click Create / repair", Color.DarkOrange),
            _ when !st.Port.StartsWith("PrintToImage", StringComparison.OrdinalIgnoreCase) => ($"shared, but its port \"{st.Port}\" does not go to PrintToImage — click Create / repair", Color.DarkOrange),
            _ => ($"ready (driver: {st.Driver})", Color.DarkGreen),
        };
        _spTest.Enabled = _spRemove.Enabled = st.Exists;
    }

    private async void PrinterAction(bool remove)
    {
        if (_dirty && !remove && (_spName.Text.Trim() != _loaded.SharedPrinterName || (_spPaper.SelectedItem as string) != _loaded.SharedPrinterPaperSize))
        {
            MessageBox.Show(this, "Save the settings first (the printer name or paper size was changed).", "PrintToImage");
            return;
        }
        Cursor = Cursors.WaitCursor;
        _spCreate.Enabled = _spRemove.Enabled = false;
        var s = _loaded;
        var r = await Task.Run(() => Admin.SharedPrinter(s.SharedPrinterName, s.RawPort, s.SharedPrinterPaperSize, remove));
        Cursor = Cursors.Default;
        _spCreate.Enabled = true;
        if (!r.Ok) MessageBox.Show(this, r.Output, "Shared printer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else _status.Text = r.Output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        RefreshPrinter();
    }

    private TabPage DevicesTab()
    {
        _devices.Columns.Add(new DataGridViewTextBoxColumn { Name = "Ip", HeaderText = "Computer name or IP address of the PC", FillWeight = 34 });
        _devices.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Device / folder name", FillWeight = 30 });
        var fmt = new DataGridViewComboBoxColumn { Name = "Format", HeaderText = "Format", FillWeight = 18, FlatStyle = FlatStyle.Flat };
        fmt.Items.AddRange("Default", "PDF", "JPG", "PNG");
        _devices.Columns.Add(fmt);
        var pages = new DataGridViewComboBoxColumn { Name = "Pages", HeaderText = "PDF pages", FillWeight = 22, FlatStyle = FlatStyle.Flat };
        pages.Items.AddRange("Default", "One PDF", "One PDF per page");
        _devices.Columns.Add(pages);
        _devices.DefaultValuesNeeded += (_, e) => { e.Row.Cells["Format"].Value = "Default"; e.Row.Cells["Pages"].Value = "Default"; };
        _devices.DataError += (_, e) => e.ThrowException = false;

        var addUnknown = Btn("Add unknown senders from the job log");
        addUnknown.Click += (_, _) => AddUnknownSenders();
        var remove = Btn("Remove selected");
        remove.Click += (_, _) =>
        {
            foreach (DataGridViewRow r in _devices.SelectedRows) if (!r.IsNewRow) _devices.Rows.Remove(r);
            _dirty = true;
        };

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 12, 14, 12) };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(Hint("Each PC's jobs are saved in a folder with its device name. PCs that print to the shared printer are recognised by\n" +
                               "their computer name (e.g. RECEPTION-PC); PCs that print directly to port 9100 by their IP address (give them fixed addresses).\n" +
                               "\"Default\" uses the settings from the Output tab. Type in the empty last row to add a device."), 0, 0);
        panel.Controls.Add(_devices, 0, 1);
        panel.Controls.Add(Flow(addUnknown, remove), 0, 2);
        return Page("Devices", panel);
    }

    private TabPage NamesTab()
    {
        var t = Grid();
        Heading(t, "File number");
        foreach (var p in Presets) _regexPreset.Items.Add(p.Text);
        Row(t, "Find the file number", _regexPreset);
        Row(t, "Pattern (regular expression)", _regex,
            hint: "Used on the cleaned document name. A group named \"num\" (or the first group) is used if present, otherwise the whole match.\nNo match → the job goes to the \"no file number\" folder; PrintToImage never guesses.");
        Row(t, "Number on the page", _pageRegex,
            hint: "Optional. For devices that name the print after the report (an IOLMaster prints \"IOL-SRK-T\") but print the patient's number ON the page.\n" +
                  @"The first match in the printed text is the {page} part of the file name, e.g. \b[A-Za-z]{1,2}\d{3,7}\b finds D1935 (not K1). Empty = off.");
        Heading(t, "Try it");
        Row(t, "Document name", _testName, hint: "Type a document name as a program would send it.");
        Row(t, "Text on the page", _testPage, hint: "Optional: text printed on the page, to try \"Number on the page\".");
        Row(t, "Result", _testResult);
        Heading(t, "Cleaning the document name");
        Row(t, "Remove these parts", _strip, hint: "One regular expression per line (not case-sensitive), e.g. application names such as \"Microsoft Word - \".");
        Row(t, "Remove extensions", _exts, hint: "Comma-separated, removed from the end of the name, e.g. .pdf, .docx");
        Row(t, "Non-English names", _codePage, hint: "Character set for job names that are not UTF-8. Choose the Windows language of the PCs if names look wrong.");

        _regexPreset.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            var p = Presets[_regexPreset.SelectedIndex];
            if (p.Regex.Length > 0) _regex.Text = p.Regex;
        };
        _regex.TextChanged += (_, _) =>
        {
            int i = Array.FindIndex(Presets, p => p.Regex == _regex.Text && p.Regex.Length > 0);
            bool old = _loading;
            _loading = true;
            _regexPreset.SelectedIndex = i >= 0 ? i : Presets.Length - 1;
            _loading = old;
            UpdatePreviews();
        };
        _testName.TextChanged += (_, _) => UpdatePreviews();
        _testPage.TextChanged += (_, _) => UpdatePreviews();
        _pageRegex.TextChanged += (_, _) => UpdatePreviews();
        _strip.TextChanged += (_, _) => UpdatePreviews();
        _exts.TextChanged += (_, _) => UpdatePreviews();
        foreach (var c in CodePages) _codePage.Items.Add(c.Text);
        return Page("Job names", t);
    }

    private TabPage NetworkTab()
    {
        var t = Grid();
        Heading(t, "Receiving print jobs");
        Row(t, "Printer port (raw)", Flow(_rawPort, new Label { Text = "9100 is the standard; the PCs' printer port must use the same number.", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(6, 6, 0, 0) }));
        Row(t, "LPR / LPD", Flow(_lpd, _lpdPort), hint: "Only needed if a device can only print with LPR. The Windows \"LPD Print Service\" must not be running.");
        Row(t, "Listen on address", _listen, hint: "0.0.0.0 = all network cards.");
        Heading(t, "Who may send print jobs (Windows Firewall)");
        var fw = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        fw.Controls.AddRange(new Control[] { _fwDevices, _fwSubnet, Flow(_fwCustom, _fwCustomText) });
        Row(t, "Allow", fw, hint: "Addresses separated by commas, e.g. 192.168.1.20, 192.168.1.21 or a range 192.168.1.0/24. The rule is updated when you save.");
        Heading(t, "Limits");
        Row(t, "Connection idle timeout (s)", _idle);
        Row(t, "Maximum job size (MB)", _maxMb);
        Row(t, "Jobs converted at the same time", _conc);
        Row(t, "Conversion timeout (s)", _renderTimeout, hint: "A job that takes longer is moved to the failed folder.");
        _fwCustom.CheckedChanged += (_, _) => _fwCustomText.Enabled = _fwCustom.Checked;
        _lpd.CheckedChanged += (_, _) => _lpdPort.Enabled = _lpd.Checked;
        return Page("Network", t);
    }

    private TabPage ArchiveTab()
    {
        var t = Grid();
        Heading(t, "Original print jobs (for troubleshooting)");
        Row(t, "Archive folder", _archive, Flow(BrowseButton(_archive), OpenButton(_archive)));
        Row(t, "Keep for (days)", _archiveDays, hint: "0 = do not keep the original jobs. Older files are deleted automatically.");
        Heading(t, "Logs");
        Row(t, "Log folder", _logs, Flow(BrowseButton(_logs), OpenButton(_logs)),
            "jobs-yyyy-MM.csv: one line per job (open in Excel).  service-yyyy-MM-dd.log: detailed service log.");
        Row(t, "Keep service logs for (days)", _logDays, hint: "The jobs-*.csv job log is never deleted automatically.");
        Heading(t, "Temporary files");
        Row(t, "Work folder", _work, Flow(BrowseButton(_work)));
        Row(t, "", Hint("These folders may contain confidential documents. Keep them on this server."));
        return Page("Archive && logs", t);
    }

    private TabPage ServiceTab()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 12, 14, 12) };
        for (int i = 0; i < 4; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _btnStart.Click += (_, _) => ServiceAction(start: true, stop: false);
        _btnStop.Click += (_, _) => ServiceAction(start: false, stop: true);
        _btnRestart.Click += (_, _) => ServiceAction(start: true, stop: true);
        var openOut = Btn("Open output folder");
        openOut.Click += (_, _) => Admin.OpenFolder(_outputRoot.Text);
        var openLogs = Btn("Open logs");
        openLogs.Click += (_, _) => Admin.OpenFolder(_logs.Text);
        t.Controls.Add(Flow(new Label { Text = "Service:", AutoSize = true, Padding = new Padding(0, 8, 6, 0) }, _svcStatus, Spacer(20), _btnStart, _btnStop, _btnRestart, Spacer(20), openOut, openLogs), 0, 0);

        var send = Btn("Send test print");
        send.Click += (_, _) => SendTest();
        t.Controls.Add(Flow(new Label { Text = "Test print with document name:", AutoSize = true, Padding = new Padding(0, 8, 6, 0) }, _testTitle, send), 0, 1);
        t.Controls.Add(Hint("The test job is sent from this computer (127.0.0.1), so it is saved in the unknown-sender folder unless 127.0.0.1 is listed on the Devices tab."), 0, 2);

        var refresh = Btn("Refresh");
        refresh.Click += (_, _) => LoadJobs();
        t.Controls.Add(Flow(new Label { Text = "Recent jobs (double-click to open the file):", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10f, FontStyle.Bold), Padding = new Padding(0, 10, 10, 0) }, refresh), 0, 3);
        foreach (var (h, w) in new[] { ("Time", 130), ("Device", 90), ("Sent from", 110), ("Document name", 220), ("File number", 90), ("Status", 95), ("Saved as / error", 400) })
            _jobs.Columns.Add(h, w);
        _jobs.DoubleClick += (_, _) =>
        {
            if (_jobs.SelectedItems.Count == 0) return;
            var path = (_jobs.SelectedItems[0].Tag as string)?.Split(" | ")[0];
            if (!string.IsNullOrEmpty(path)) Admin.OpenFile(path);
        };
        t.Controls.Add(_jobs, 0, 4);
        return Page("Service && jobs", t);
    }

    private static Control Spacer(int w) => new Panel { Width = w, Height = 1 };

    private static TabPage Page(string title, Control content)
    {
        var p = new TabPage(title) { UseVisualStyleBackColor = true };
        p.Controls.Add(content);
        return p;
    }

    // ------------------------------------------------------------------ load / save

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            if (File.Exists(_settingsPath))
            {
                try { _loaded = Settings.Load(_settingsPath); }
                catch (Exception e)
                {
                    MessageBox.Show(this, $"The settings file has an error:\n\n{e.Message}\n\nDefault values are shown; saving will replace the file.",
                        "PrintToImage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _loaded = new Settings();
                }
            }
            else _loaded = new Settings();
            var s = _loaded;

            _outputRoot.Text = s.OutputRoot;
            _pdf.Checked = s.OutputFormat == "pdf";
            _jpg.Checked = s.OutputFormat == "jpg";
            _png.Checked = s.OutputFormat == "png";
            _split.Checked = s.PdfOneFilePerPage;
            _dpi.Value = Math.Clamp(s.JpgDpi, (int)_dpi.Minimum, (int)_dpi.Maximum);
            _quality.Value = Math.Clamp(s.JpgQuality, 1, 100);
            _template.Text = s.FileNameTemplate;
            _emr.Text = s.EmrAccount;
            _unassigned.Text = s.UnassignedFolderName;
            _unknown.Text = s.UnknownDeviceFolderName;
            _failed.Text = s.FailedFolderName;

            _devices.Rows.Clear();
            foreach (var (ip, name) in s.Devices)
            {
                var fmt = s.DeviceOutputFormat.TryGetValue(name, out var f) ? f.ToUpperInvariant() : "Default";
                var pages = s.DevicePdfOneFilePerPage.TryGetValue(name, out var sp) ? (sp ? "One PDF per page" : "One PDF") : "Default";
                _devices.Rows.Add(ip, name, fmt, pages);
            }

            _regex.Text = s.FileNumberRegex;
            _pageRegex.Text = s.PageNumberRegex;
            int pi = Array.FindIndex(Presets, p => p.Regex == s.FileNumberRegex && p.Regex.Length > 0);
            _regexPreset.SelectedIndex = pi >= 0 ? pi : Presets.Length - 1;
            _strip.Text = string.Join(Environment.NewLine, s.StripPatterns);
            _exts.Text = string.Join(", ", s.StripExtensions);
            int ci = Array.FindIndex(CodePages, c => c.Cp == s.FallbackCodePage);
            if (ci < 0) { _codePage.Items.Add($"Code page {s.FallbackCodePage}"); ci = _codePage.Items.Count - 1; }
            _codePage.SelectedIndex = ci;

            _spName.Text = s.SharedPrinterName;
            _spPaper.SelectedItem = s.SharedPrinterPaperSize == "Letter" ? "Letter" : "A4";

            _rawPort.Value = s.RawPort;
            _lpd.Checked = s.EnableLpd;
            _lpdPort.Value = s.LpdPort;
            _lpdPort.Enabled = s.EnableLpd;
            _listen.Text = s.ListenAddress;
            var fwv = (s.FirewallRemoteAddress ?? "").Trim();
            _fwDevices.Checked = fwv.Equals("DeviceIPs", StringComparison.OrdinalIgnoreCase);
            _fwSubnet.Checked = fwv.Length == 0 || fwv.Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase);
            _fwCustom.Checked = !_fwDevices.Checked && !_fwSubnet.Checked;
            _fwCustomText.Text = _fwCustom.Checked ? fwv : "";
            _fwCustomText.Enabled = _fwCustom.Checked;
            _idle.Value = Math.Clamp(s.ReceiveIdleTimeoutSeconds, 10, 3600);
            _maxMb.Value = Math.Clamp(s.MaxJobSizeMB, 1, 10000);
            _conc.Value = Math.Clamp(s.MaxConcurrentConversions, 1, 16);
            _renderTimeout.Value = Math.Clamp(s.RenderTimeoutSeconds, 10, 3600);

            _archive.Text = s.ArchiveFolder;
            _archiveDays.Value = Math.Clamp(s.ArchiveRetentionDays, 0, 3650);
            _logs.Text = s.LogFolder;
            _logDays.Value = Math.Clamp(s.LogRetentionDays, 0, 3650);
            _work.Text = s.WorkFolder;
        }
        finally { _loading = false; }
        UpdateFormatControls();
        UpdatePreviews();
        _dirty = false;
        _status.Text = File.Exists(_settingsPath) ? _settingsPath : "No settings file yet — defaults shown.";
    }

    /// <summary>Builds a Settings object from the form. Throws InvalidDataException with all problems.</summary>
    private Settings FromForm()
    {
        var errors = new List<string>();
        var s = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(_loaded, Settings.JsonOptions), Settings.JsonOptions)!; // keep unknown/advanced values
        s.OutputRoot = _outputRoot.Text.Trim();
        s.OutputFormat = _png.Checked ? "png" : _jpg.Checked ? "jpg" : "pdf";
        s.PdfOneFilePerPage = _split.Checked;
        s.JpgDpi = (int)_dpi.Value;
        s.JpgQuality = (int)_quality.Value;
        s.FileNameTemplate = _template.Text.Trim();
        s.EmrAccount = _emr.Text.Trim();
        s.UnassignedFolderName = _unassigned.Text.Trim();
        s.UnknownDeviceFolderName = _unknown.Text.Trim();
        s.FailedFolderName = _failed.Text.Trim();

        s.Devices = new(StringComparer.OrdinalIgnoreCase);
        s.DeviceOutputFormat = new(StringComparer.OrdinalIgnoreCase);
        s.DevicePdfOneFilePerPage = new(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow r in _devices.Rows)
        {
            if (r.IsNewRow) continue;
            var ip = (r.Cells["Ip"].Value as string ?? "").Trim();
            var name = (r.Cells["Name"].Value as string ?? "").Trim();
            if (ip.Length == 0 && name.Length == 0) continue;
            if (!System.Net.IPAddress.TryParse(ip, out _) && !System.Text.RegularExpressions.Regex.IsMatch(ip, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$"))
            { errors.Add($"Devices: \"{ip}\" is not a valid computer name or IP address."); continue; }
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { errors.Add($"Devices: enter a valid folder name for {ip}."); continue; }
            if (s.Devices.ContainsKey(ip)) { errors.Add($"Devices: {ip} is listed twice."); continue; }
            s.Devices[ip] = name;
            var fmt = r.Cells["Format"].Value as string ?? "Default";
            if (fmt != "Default") s.DeviceOutputFormat[name] = fmt.ToLowerInvariant();
            var pages = r.Cells["Pages"].Value as string ?? "Default";
            if (pages != "Default") s.DevicePdfOneFilePerPage[name] = pages == "One PDF per page";
        }

        s.FileNumberRegex = _regex.Text;
        s.PageNumberRegex = _pageRegex.Text.Trim();
        s.StripPatterns = _strip.Lines.Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        s.StripExtensions = _exts.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e).ToList();
        s.FallbackCodePage = _codePage.SelectedIndex >= 0 && _codePage.SelectedIndex < CodePages.Length ? CodePages[_codePage.SelectedIndex].Cp : _loaded.FallbackCodePage;

        s.SharedPrinterName = _spName.Text.Trim();
        s.SharedPrinterPaperSize = _spPaper.SelectedItem as string ?? "A4";

        s.RawPort = (int)_rawPort.Value;
        s.EnableLpd = _lpd.Checked;
        s.LpdPort = (int)_lpdPort.Value;
        s.ListenAddress = _listen.Text.Trim();
        s.FirewallRemoteAddress = _fwDevices.Checked ? "DeviceIPs" : _fwSubnet.Checked ? "LocalSubnet" : _fwCustomText.Text.Trim();
        if (_fwCustom.Checked && s.FirewallRemoteAddress.Length == 0) errors.Add("Network: enter the addresses that may print, or choose another option.");
        if (s.EnableLpd && s.LpdPort == s.RawPort) errors.Add("Network: the LPD port must differ from the raw port.");
        s.ReceiveIdleTimeoutSeconds = (int)_idle.Value;
        s.MaxJobSizeMB = (int)_maxMb.Value;
        s.MaxConcurrentConversions = (int)_conc.Value;
        s.RenderTimeoutSeconds = (int)_renderTimeout.Value;

        s.ArchiveFolder = _archive.Text.Trim();
        s.ArchiveRetentionDays = (int)_archiveDays.Value;
        s.LogFolder = _logs.Text.Trim();
        s.LogRetentionDays = (int)_logDays.Value;
        s.WorkFolder = _work.Text.Trim();

        foreach (var (label, value) in new[] { ("output folder", s.OutputRoot), ("archive folder", s.ArchiveFolder), ("log folder", s.LogFolder), ("work folder", s.WorkFolder) })
            if (value.Length == 0 || !Path.IsPathFullyQualified(value)) errors.Add($"Enter a full path for the {label} (e.g. D:\\PrintToImage\\Output).");
        foreach (var (label, value) in new[] { ("no file number", s.UnassignedFolderName), ("unknown sender", s.UnknownDeviceFolderName), ("failed jobs", s.FailedFolderName) })
            if (value.Length == 0 || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) errors.Add($"Enter a valid folder name for \"{label}\".");

        try { s.Validate(); }
        catch (InvalidDataException e) { errors.AddRange(e.Message.Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0)); }
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors.Distinct()));
        return s;
    }

    private void Save(bool restart)
    {
        Settings s;
        try { s = FromForm(); }
        catch (InvalidDataException e)
        {
            MessageBox.Show(this, "Please correct the following:\n\n" + e.Message, "PrintToImage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var notes = new List<string>();
        Cursor = Cursors.WaitCursor;
        try
        {
            // 1. Settings file (with a backup of the previous version).
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var json = JsonSerializer.Serialize(s, Settings.JsonOptions);
            var tmp = _settingsPath + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            if (File.Exists(_settingsPath)) File.Copy(_settingsPath, _settingsPath + ".bak", overwrite: true);
            File.Move(tmp, _settingsPath, overwrite: true);

            // 2. Output folders and their permissions (confidential data).
            bool rootChanged = !string.Equals(Path.GetFullPath(s.OutputRoot), Path.GetFullPath(_loaded.OutputRoot), StringComparison.OrdinalIgnoreCase)
                               || !Directory.Exists(s.OutputRoot) || s.EmrAccount != _loaded.EmrAccount;
            Directory.CreateDirectory(s.OutputRoot);
            foreach (var sub in s.Devices.Values.Append(s.UnassignedFolderName).Append(s.UnknownDeviceFolderName).Append(s.FailedFolderName))
                Directory.CreateDirectory(Path.Combine(s.OutputRoot, sub));
            if (rootChanged)
            {
                var r = Admin.SecureFolder(s.OutputRoot, s.EmrAccount);
                notes.Add(r.Ok ? "Output folder permissions updated." : "Could not set output folder permissions: " + r.Output);
            }
            foreach (var d in new[] { s.ArchiveFolder, s.LogFolder, s.WorkFolder }) Directory.CreateDirectory(d);

            // 3. Firewall rule.
            bool fwChanged = s.RawPort != _loaded.RawPort || s.EnableLpd != _loaded.EnableLpd || s.LpdPort != _loaded.LpdPort
                             || !Admin.RemoteAddresses(s).SequenceEqual(Admin.RemoteAddresses(_loaded));
            if (fwChanged && Admin.ServiceStatus() is not null)
            {
                var r = Admin.ApplyFirewall(s);
                notes.Add(r.Ok ? "Firewall rule updated (allowed: " + string.Join(", ", Admin.RemoteAddresses(s)) + ")." : "Could not update the firewall rule: " + r.Output);
            }

            // 4. Shared printer: apply a new name, paper size or port if the printer is set up.
            var old = _loaded;
            bool printerChanged = s.SharedPrinterName != old.SharedPrinterName || s.SharedPrinterPaperSize != old.SharedPrinterPaperSize || s.RawPort != old.RawPort;
            if (printerChanged && Admin.GetPrinter(old.SharedPrinterName).Exists)
            {
                if (s.SharedPrinterName != old.SharedPrinterName) Admin.SharedPrinter(old.SharedPrinterName, old.RawPort, old.SharedPrinterPaperSize, remove: true);
                var r = Admin.SharedPrinter(s.SharedPrinterName, s.RawPort, s.SharedPrinterPaperSize, remove: false);
                notes.Add(r.Ok ? $@"Shared printer updated: \\{Environment.MachineName}\{s.SharedPrinterName}." : "Could not update the shared printer: " + r.Output);
            }

            _loaded = s;
            _dirty = false;

            // 5. Service.
            if (Admin.ServiceStatus() is null) notes.Add("The PrintToImage service is not installed on this computer (run install.ps1).");
            else if (restart || MessageBox.Show(this, "Settings saved.\n\nRestart the PrintToImage service now so that the changes take effect?",
                         "PrintToImage", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Admin.ControlService(start: true, stop: true);
                notes.Add("Service restarted.");
            }
            else notes.Add("Restart the service later to apply the changes.");
            _status.Text = $"Saved {DateTime.Now:HH:mm:ss}. " + string.Join(" ", notes);
        }
        catch (Exception e)
        {
            MessageBox.Show(this, "Saving failed:\n\n" + e.Message, "PrintToImage", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Cursor = Cursors.Default;
            RefreshService();
        }
        if (notes.Any(n => n.StartsWith("Could not", StringComparison.Ordinal)))
            MessageBox.Show(this, string.Join("\n\n", notes), "PrintToImage", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_dirty)
        {
            var r = MessageBox.Show(this, "Save the changes before closing?", "PrintToImage", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes)
            {
                Save(restart: false);
                if (_dirty) { e.Cancel = true; return; }
            }
        }
        base.OnFormClosing(e);
    }

    private void HookDirty(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c == _testName || c == _testTitle) continue;
            switch (c)
            {
                case TextBox tb when !tb.ReadOnly: tb.TextChanged += MarkDirty; break;
                case CheckBox cb: cb.CheckedChanged += MarkDirty; break;
                case RadioButton rb: rb.CheckedChanged += MarkDirty; break;
                case NumericUpDown nu: nu.ValueChanged += MarkDirty; break;
                case ComboBox co: co.SelectedIndexChanged += MarkDirty; break;
                case DataGridView dg: dg.CellValueChanged += MarkDirty; dg.RowsRemoved += MarkDirty; break;
            }
            HookDirty(c);
        }
    }

    private void MarkDirty(object? sender, EventArgs e)
    {
        if (_loading) return;
        _dirty = true;
        _status.Text = "Unsaved changes.";
    }

    // ------------------------------------------------------------------ previews

    private void UpdateFormatControls()
    {
        _split.Enabled = _pdf.Checked;
        _quality.Enabled = !_png.Checked;
        UpdatePreviews();
    }

    private void UpdatePreviews()
    {
        if (_loading) return;
        string ext = _png.Checked ? ".png" : _jpg.Checked ? ".jpg" : ".pdf";
        var sample = new ResolvedName("Microsoft Word - 12345 Report.docx", "", "12345 Report", "12345");
        try { _templatePreview.Text = "Example: " + OutputWriter.ApplyTemplate(_template.Text, sample, "Reception", DateTime.Now, "D1935") + ext; }
        catch (Exception) { _templatePreview.Text = ""; }

        var test = new Settings
        {
            FileNumberRegex = _regex.Text,
            PageNumberRegex = _pageRegex.Text.Trim(),
            StripPatterns = _strip.Lines.Where(l => l.Trim().Length > 0).ToList(),
            StripExtensions = _exts.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(e => e.StartsWith('.') ? e : "." + e).ToList(),
            FileNameTemplate = _template.Text,
        };
        try { test.Validate(); }
        catch (InvalidDataException e)
        {
            _testResult.ForeColor = Color.Firebrick;
            _testResult.Text = string.Join("\n", e.Message.Split('\n').Skip(1).Select(l => l.Trim()));
            return;
        }
        var r = JobNameResolver.Resolve(new (string, string?)[] { ("test", _testName.Text) }, test);
        string device = _devices.Rows.Cast<DataGridViewRow>().Where(x => !x.IsNewRow).Select(x => x.Cells["Name"].Value as string).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Reception";
        string? page = test.PageNumber is { } prx && _testPage.Text.Trim().Length > 0 ? JobNameResolver.ExtractFileNumber(_testPage.Text, prx) : null;
        bool assigned = r.IsAssigned || page is not null;
        string pageLine = test.PageNumber is null ? "" : $"\nOn the page  : {page ?? "(none)"}";
        _testResult.ForeColor = assigned ? Color.DarkGreen : Color.DarkOrange;
        _testResult.Text = assigned
            ? $"Cleaned name : {r.CleanName}\nFile number  : {r.FileNumber ?? "(none)"}{pageLine}\nSaved as     : {Path.Combine(device, OutputWriter.ApplyTemplate(test.FileNameTemplate, r, device, DateTime.Now, page) + ext)}"
            : $"Cleaned name : {r.CleanName ?? "(none)"}\nFile number  : (none){pageLine}\nSaved as     : {Path.Combine(_unassigned.Text, device + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ext)}";
    }

    // ------------------------------------------------------------------ service & jobs

    private void RefreshService()
    {
        var st = Admin.ServiceStatus();
        _svcStatus.Text = st switch
        {
            null => "not installed",
            ServiceControllerStatus.Running => "running",
            ServiceControllerStatus.Stopped => "stopped",
            _ => st.ToString()!.ToLowerInvariant(),
        };
        _svcStatus.ForeColor = st == ServiceControllerStatus.Running ? Color.DarkGreen : Color.Firebrick;
        _btnStart.Enabled = st == ServiceControllerStatus.Stopped;
        _btnStop.Enabled = _btnRestart.Enabled = st == ServiceControllerStatus.Running;
    }

    private void ServiceAction(bool start, bool stop)
    {
        Cursor = Cursors.WaitCursor;
        try { Admin.ControlService(start, stop); }
        catch (Exception e)
        {
            MessageBox.Show(this, "The service could not be " + (start ? "started" : "stopped") + ":\n\n" + e.Message +
                                  "\n\nSee the service log or the Windows Application event log.", "PrintToImage", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Cursor = Cursors.Default; RefreshService(); }
    }

    private void SendTest()
    {
        var sender = Path.Combine(AppContext.BaseDirectory, "SendTestPrint.exe");
        if (!File.Exists(sender)) { MessageBox.Show(this, "SendTestPrint.exe was not found next to this program.", "PrintToImage"); return; }
        var r = Admin.Run(sender, new[] { "--host", "127.0.0.1", "--port", ((int)_loaded.RawPort).ToString(), "--title", _testTitle.Text });
        if (!r.Ok) { MessageBox.Show(this, r.Output, "Test print failed", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        _status.Text = "Test print sent. It appears in the list below in a few seconds.";
        var t = new System.Windows.Forms.Timer { Interval = 3000 };
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); LoadJobs(); };
        t.Start();
    }

    private void LoadJobs()
    {
        _jobs.BeginUpdate();
        _jobs.Items.Clear();
        foreach (var row in JobLogReader.ReadRecent(_loaded.LogFolder))
        {
            var item = new ListViewItem(new[] { row.Time, row.Device, row.Sender, row.RawName, row.FileNumber, row.Status, row.Error.Length > 0 && row.Status != "OK" ? row.Error + "   " + row.Output : row.Output })
            {
                Tag = row.Output,
                ForeColor = row.Status switch { "OK" => Color.DarkGreen, "UNASSIGNED" => Color.DarkOrange, _ => Color.Firebrick },
            };
            _jobs.Items.Add(item);
        }
        _jobs.EndUpdate();
    }

    private void AddUnknownSenders()
    {
        var known = _devices.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).Select(r => (r.Cells["Ip"].Value as string ?? "").Trim()).ToHashSet();
        var unknown = JobLogReader.ReadRecent(_loaded.LogFolder, 2000)
            .Where(r => r.Device == _loaded.UnknownDeviceFolderName && r.Sender.Length > 0 && !known.Contains(r.Sender, StringComparer.OrdinalIgnoreCase))
            .Select(r => r.Sender).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count == 0)
        {
            MessageBox.Show(this, "No jobs from unknown PCs were found in the job log.", "PrintToImage");
            return;
        }
        foreach (var ip in unknown) _devices.Rows.Add(ip, "", "Default", "Default");
        _dirty = true;
        MessageBox.Show(this, $"Added {unknown.Count} PC(s). Enter a device name for each, then save.", "PrintToImage");
    }

    /// <summary>Diagnostics: saves a picture of every tab (used to check the layout without a screen).</summary>
    public void SaveScreenshots(string folder)
    {
        Directory.CreateDirectory(folder);
        for (int i = 0; i < _tabs.TabCount; i++)
        {
            _tabs.SelectedIndex = i;
            Application.DoEvents();
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(Path.Combine(folder, $"tab{i + 1}.png"));
        }
    }
}
