using System.Diagnostics;

namespace PrintToImage.PostScript;

public sealed partial class Interpreter
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public PSDict PageDeviceDict = null!;
    public PSDict FontDirectory = null!, GlobalFontDirectory = null!;
    private PSDict _internalDict = null!;

    private void InitDictionaries()
    {
        foreach (var e in new[] { "dictfull", "dictstackoverflow", "dictstackunderflow", "execstackoverflow", "handleerror",
                     "interrupt", "invalidaccess", "invalidexit", "invalidfileaccess", "invalidfont", "invalidrestore",
                     "ioerror", "limitcheck", "nocurrentpoint", "rangecheck", "stackoverflow", "stackunderflow",
                     "syntaxerror", "timeout", "typecheck", "undefined", "undefinedfilename", "undefinedresource",
                     "undefinedresult", "unmatchedmark", "unregistered", "VMerror", "configurationerror", "invalidid" })
            RegisterDefaultErrorHandler(e);
        ErrorDict.PutRaw("handleerror", new PSOperator("handleerror", _ => { }));

        DollarError.PutRaw("newerror", PSBool.False);
        DollarError.PutRaw("errorname", PSNull.Instance);
        DollarError.PutRaw("command", PSNull.Instance);
        DollarError.PutRaw("errorinfo", PSNull.Instance);
        DollarError.PutRaw("ostack", PSNull.Instance);
        DollarError.PutRaw("estack", PSNull.Instance);
        DollarError.PutRaw("dstack", PSNull.Instance);
        DollarError.PutRaw("recordstacks", PSBool.False);
        DollarError.PutRaw("binary", PSBool.False);

        FontDirectory = new PSDict(64, 0);
        GlobalFontDirectory = new PSDict(64, -1);
        _internalDict = new PSDict(16, -1);

        SystemDict.PutRaw("systemdict", SystemDict);
        SystemDict.PutRaw("userdict", UserDict);
        SystemDict.PutRaw("globaldict", GlobalDict);
        SystemDict.PutRaw("errordict", ErrorDict);
        SystemDict.PutRaw("statusdict", StatusDict);
        SystemDict.PutRaw("$error", DollarError);
        SystemDict.PutRaw("FontDirectory", FontDirectory);
        SystemDict.PutRaw("GlobalFontDirectory", GlobalFontDirectory);
        SystemDict.PutRaw("SharedFontDirectory", GlobalFontDirectory);
        SystemDict.PutRaw("shareddict", GlobalDict);
        SystemDict.PutRaw("StandardEncoding", Fonts.Encodings.StandardArray());
        SystemDict.PutRaw("ISOLatin1Encoding", Fonts.Encodings.IsoLatin1Array());
        SystemDict.PutRaw("languagelevel", PSInt.Get(3));
        SystemDict.PutRaw("version", PSString.FromText("3011.104", -1));
        SystemDict.PutRaw("product", PSString.FromText("PrintToImage", -1));
        SystemDict.PutRaw("revision", PSInt.Get(1));
        SystemDict.PutRaw("serialnumber", PSInt.Get(12345));
        SystemDict.PutRaw("=string", new PSString(new byte[256], -1));

        StatusDict.PutRaw("product", PSString.FromText("PrintToImage", -1));
        StatusDict.PutRaw("jobname", PSString.FromText("", -1));
        StatusDict.PutRaw("manualfeed", PSBool.False);
        StatusDict.PutRaw("waittimeout", PSInt.Get(0));
        StatusDict.PutRaw("printername", new PSOperator("printername", ip => ip.Push(PSString.FromText("PrintToImage"))));
        StatusDict.PutRaw("setjobtimeout", new PSOperator("setjobtimeout", ip => ip.Pop()));
        StatusDict.PutRaw("jobtimeout", new PSOperator("jobtimeout", ip => ip.Push(0)));
        StatusDict.PutRaw("setduplexmode", new PSOperator("setduplexmode", ip => ip.Pop()));
        StatusDict.PutRaw("duplexmode", new PSOperator("duplexmode", ip => ip.Push(false)));
        StatusDict.PutRaw("settumble", new PSOperator("settumble", ip => ip.Pop()));
        StatusDict.PutRaw("setpapertray", new PSOperator("setpapertray", ip => ip.Pop()));
        StatusDict.PutRaw("papertray", new PSOperator("papertray", ip => ip.Push(1)));
        StatusDict.PutRaw("setresolution", new PSOperator("setresolution", ip => ip.Pop()));
        StatusDict.PutRaw("resolution", new PSOperator("resolution", ip => ip.Push((int)ip.Device.Dpi)));
        StatusDict.PutRaw("setdefaulttimeouts", new PSOperator("setdefaulttimeouts", ip => { ip.Pop(); ip.Pop(); ip.Pop(); }));
        StatusDict.PutRaw("setpagetype", new PSOperator("setpagetype", ip => ip.Pop()));
        StatusDict.PutRaw("setmargins", new PSOperator("setmargins", ip => { ip.Pop(); ip.Pop(); }));
        StatusDict.PutRaw("ramsize", PSInt.Get(64 * 1024 * 1024));
        // An RGB device (like Ghostscript's colour image devices): drivers then send RGB rather than CMYK data.
        StatusDict.PutRaw("processcolors", new PSOperator("processcolors", ip => ip.Push(3)));
        StatusDict.PutRaw("setsoftwareiomode", new PSOperator("setsoftwareiomode", ip => ip.Pop()));
        StatusDict.PutRaw("ignore", PSBool.False);
        StatusDict.PutRaw("letter", new PSOperator("letter", ip => ip.SetPageSizeOp(612, 792)));
        StatusDict.PutRaw("legal", new PSOperator("legal", ip => ip.SetPageSizeOp(612, 1008)));
        StatusDict.PutRaw("a4", new PSOperator("a4", ip => ip.SetPageSizeOp(595, 842)));
        StatusDict.PutRaw("a3", new PSOperator("a3", ip => ip.SetPageSizeOp(842, 1191)));
        StatusDict.PutRaw("a5", new PSOperator("a5", ip => ip.SetPageSizeOp(420, 595)));
        StatusDict.PutRaw("b5", new PSOperator("b5", ip => ip.SetPageSizeOp(516, 729)));
        foreach (var n in new[] { "letter", "legal", "a4", "a3", "a5", "b5" })
            SystemDict.PutRaw(n, StatusDict.Get(n)!);
        SystemDict.PutRaw("note", StatusDict.Get("letter")!);

        UserDict.PutRaw("#copies", PSInt.Get(1));

        PageDeviceDict = new PSDict(32, -1);
        UpdatePageDeviceDict();
    }

    internal void SetPageSizeOp(double w, double h)
    {
        Device.SetPageSize(w, h);
        UpdatePageDeviceDict();
        InitGraphics();
    }

    private void UpdatePageDeviceDict()
    {
        PageDeviceDict.PutRaw("PageSize", new PSArray(new PSObject[] { Real(Device.WidthPt), Real(Device.HeightPt) }, -1));
        PageDeviceDict.PutRaw("HWResolution", new PSArray(new PSObject[] { Real(Device.Dpi), Real(Device.Dpi) }, -1));
        if (!PageDeviceDict.Has("ImagingBBox")) PageDeviceDict.PutRaw("ImagingBBox", PSNull.Instance);
        if (!PageDeviceDict.Has("NumCopies")) PageDeviceDict.PutRaw("NumCopies", PSNull.Instance);
        if (!PageDeviceDict.Has("Orientation")) PageDeviceDict.PutRaw("Orientation", PSInt.Get(0));
        if (!PageDeviceDict.Has("Duplex")) PageDeviceDict.PutRaw("Duplex", PSBool.False);
        if (!PageDeviceDict.Has("Tumble")) PageDeviceDict.PutRaw("Tumble", PSBool.False);
        if (!PageDeviceDict.Has("Collate")) PageDeviceDict.PutRaw("Collate", PSBool.False);
        if (!PageDeviceDict.Has("ManualFeed")) PageDeviceDict.PutRaw("ManualFeed", PSBool.False);
        if (!PageDeviceDict.Has("MediaColor")) PageDeviceDict.PutRaw("MediaColor", PSNull.Instance);
        if (!PageDeviceDict.Has("MediaType")) PageDeviceDict.PutRaw("MediaType", PSNull.Instance);
        if (!PageDeviceDict.Has("MediaWeight")) PageDeviceDict.PutRaw("MediaWeight", PSNull.Instance);
        if (!PageDeviceDict.Has("PageDeviceName")) PageDeviceDict.PutRaw("PageDeviceName", PSNull.Instance);
        if (!PageDeviceDict.Has("ProcessColorModel")) PageDeviceDict.PutRaw("ProcessColorModel", PSName.Lit("DeviceRGB"));
        if (!PageDeviceDict.Has("Policies"))
        {
            var pol = new PSDict(4, -1);
            pol.PutRaw("PageSize", PSInt.Get(3));
            pol.PutRaw("PolicyNotFound", PSInt.Get(1));
            PageDeviceDict.PutRaw("Policies", pol);
        }
        if (!PageDeviceDict.Has("InputAttributes"))
        {
            var ia = new PSDict(4, -1);
            var tray = new PSDict(2, -1);
            tray.PutRaw("PageSize", new PSArray(new PSObject[] { Real(Device.WidthPt), Real(Device.HeightPt) }, -1));
            ia.PutRaw("0", tray);
            ia.PutRaw("Priority", new PSArray(new PSObject[] { PSInt.Get(0) }, -1));
            PageDeviceDict.PutRaw("InputAttributes", ia);
        }
        if (!PageDeviceDict.Has("OutputAttributes")) PageDeviceDict.PutRaw("OutputAttributes", new PSDict(2, -1));
        if (!PageDeviceDict.Has("BeginPage")) PageDeviceDict.PutRaw("BeginPage", new PSArray(Array.Empty<PSObject>(), -1, exec: true));
        if (!PageDeviceDict.Has("EndPage"))
            PageDeviceDict.PutRaw("EndPage", new PSArray(new PSObject[] { PSName.Exe("exch"), PSName.Exe("pop"), PSInt.Get(2), PSName.Exe("ne") }, -1, exec: true));
        if (!PageDeviceDict.Has("Install")) PageDeviceDict.PutRaw("Install", new PSArray(Array.Empty<PSObject>(), -1, exec: true));
    }

    private void RegisterVmOperators()
    {
        Def("save", ip => ip.Push(ip.Save()));
        Def("restore", ip =>
        {
            var s = ip.Pop() as PSSave ?? throw new PSError("typecheck");
            ip.Restore(s);
        });
        Def("vmstatus", ip => { ip.Push(ip.SaveLevel); ip.Push(2_000_000); ip.Push(256_000_000); });
        Def("vmreclaim", ip => ip.Pop());
        Def("setvmthreshold", ip => ip.Pop());
        Def("setglobal", ip => ip.GlobalAlloc = ip.PopBool());
        Def("currentglobal", ip => ip.Push(ip.GlobalAlloc));
        Def("setshared", ip => ip.GlobalAlloc = ip.PopBool());
        Def("currentshared", ip => ip.Push(ip.GlobalAlloc));
        Def("gcheck", ip =>
        {
            var o = ip.Pop();
            ip.Push(o switch
            {
                PSDict d => d.SaveLevel < 0,
                PSArray a => a.Buf.SaveLevel < 0,
                PSString s => s.Buf.SaveLevel < 0,
                _ => true,
            });
        });
        Def("realtime", ip => ip.Push((int)(ip._clock.ElapsedMilliseconds & 0x7FFFFFFF)));
        Def("usertime", ip => ip.Push((int)(ip._clock.ElapsedMilliseconds & 0x7FFFFFFF)));
        Def("setuserparams", ip => { var d = ip.PopDict(); foreach (var kv in d.Map) ip.UserParams.Map[kv.Key] = kv.Value; });
        Def("currentuserparams", ip => ip.Push(ip.UserParams));
        Def("setsystemparams", ip => ip.PopDict());
        Def("currentsystemparams", ip =>
        {
            var d = ip.NewDict(4);
            d.PutRaw("MaxFontCache", PSInt.Get(64_000_000));
            ip.Push(d);
        });
        Def("setdevparams", ip => { ip.Pop(); ip.Pop(); });
        Def("currentdevparams", ip => { ip.Pop(); ip.Push(ip.NewDict(1)); });
        Def("setcacheparams", ip => { int n = ip.CountToMark(); ip._osp -= n + 1; });
        Def("currentcacheparams", ip => { ip.Push(PSMark.Instance); ip.Push(1_000_000); ip.Push(1000); });
        Def("setcachelimit", ip => ip.Pop());
        Def("cachestatus", ip => { for (int i = 0; i < 7; i++) ip.Push(0); });
        Def("setucacheparams", ip => { int n = ip.CountToMark(); ip._osp -= n + 1; });
        Def("ucachestatus", ip => { ip.Push(PSMark.Instance); for (int i = 0; i < 5; i++) ip.Push(0); });
        Def("internaldict", ip => { ip.PopInt(); ip.Push(ip._internalDict); });
        Def("executive", ip => { });
        Def("serverdict", ip => ip.Push(ip.NewDict(1)));
        Def("startjob", ip => { ip.Pop(); ip.Pop(); ip.Push(false); });
        Def("exitserver", ip => ip.Pop());

        // ---- page device ----
        Def("showpage", ip => ip.ShowPage());
        Def("copypage", ip => { /* rarely used; treated like showpage without erase is not possible with recording */ });
        Def("erasepage", ip => ip.Device.ErasePage());
        Def("setpagedevice", ip =>
        {
            var d = ip.PopDict();
            foreach (var kv in d.Map)
            {
                if (kv.Value is PSNull && kv.Key is NameAtom { Text: "ImagingBBox" or "NumCopies" }) { ip.PageDeviceDict.Map[kv.Key] = kv.Value; continue; }
                ip.PageDeviceDict.Map[kv.Key] = kv.Value;
            }
            if (d.Get("PageSize") is PSArray ps && ps.Length >= 2 && IsNum(ps[0]) && IsNum(ps[1]))
            {
                double w = Num(ps[0]), h = Num(ps[1]);
                if (w > 0 && h > 0) ip.Device.SetPageSize(w, h);
            }
            ip.UpdatePageDeviceDict();
            ip.InitGraphics();
            ip.Device.ErasePage();
        });
        Def("currentpagedevice", ip =>
        {
            var copy = new PSDict(ip.PageDeviceDict.Count + 2, -1);
            foreach (var kv in ip.PageDeviceDict.Map) copy.Map[kv.Key] = kv.Value;
            ip.Push(copy);
        });
        Def("nulldevice", ip => ip.GS.NullDevice = true);
        Def("framedevice", ip => { ip.Pop(); ip.Pop(); ip.Pop(); ip.Pop(); });
        Def("banddevice", ip => { ip.Pop(); ip.Pop(); ip.Pop(); ip.Pop(); });
        Def("renderbands", ip => ip.Pop());
    }

    private void ShowPage()
    {
        // EndPage procedure: (count, reason) -> bool. We always transmit the page.
        Device.ShowPage();
        if (UserDict.Get("#copies") is { } c && IsNum(c) && Num(c) > 1)
            Log($"Note: job requested {Num(c)} copies; one copy saved.");
        Device.ErasePage();
        InitGraphics();
    }
}
