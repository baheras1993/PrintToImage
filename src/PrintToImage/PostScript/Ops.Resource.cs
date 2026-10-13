using PrintToImage.PostScript.Fonts;

namespace PrintToImage.PostScript;

public sealed partial class Interpreter
{
    private readonly Dictionary<string, (PSDict Local, PSDict Global)> _resources = new(StringComparer.Ordinal);

    private static readonly string[] StandardCategories =
    {
        "Category", "Generic", "Font", "CIDFont", "CMap", "FontSet", "Encoding", "Form", "Pattern", "ProcSet", "ColorSpace",
        "Halftone", "ColorRendering", "IdiomSet", "InkParams", "TrapParams", "OutputDevice", "ControlLanguage", "Localization",
        "PDL", "HWOptions", "Filter", "ColorSpaceFamily", "Emulator", "IODevice", "ColorRenderingType", "FMapType", "FontType",
        "FormType", "HalftoneType", "ImageType", "PatternType", "FunctionType", "ShadingType", "TrappingType",
    };

    private (PSDict Local, PSDict Global) Category(string name)
    {
        if (!_resources.TryGetValue(name, out var c))
        {
            c = (new PSDict(16, 0), new PSDict(16, -1));
            _resources[name] = c;
        }
        return c;
    }

    internal PSObject? FindResourceInstance(string category, PSObject key)
    {
        var k = PSDict.Key(key);
        var cat = Category(category);
        if (cat.Local.Map.TryGetValue(k, out var v)) return v;
        if (cat.Global.Map.TryGetValue(k, out v)) return v;
        if (category == "Font")
        {
            if (FontDirectory.Map.TryGetValue(k, out v)) return v;
            if (GlobalFontDirectory.Map.TryGetValue(k, out v)) return v;
            return null;
        }
        if (category == "CMap" && key.ToString() is "Identity-H" or "Identity-V")
        {
            var d = new PSDict(8, -1);
            d.PutRaw("CMapName", PSName.Lit(key.ToString()!));
            d.PutRaw("CMapType", PSInt.Get(1));
            d.PutRaw("WMode", PSInt.Get(key.ToString() == "Identity-V" ? 1 : 0));
            d.PutRaw(".CMapData", new PSCMapObj(CMapData.Identity()));
            cat.Global.Map[k] = d;
            return d;
        }
        if (category == "ProcSet" && key.ToString() == "CIDInit")
        {
            var p = BuildCidInit();
            cat.Global.Map[k] = p;
            return p;
        }
        if (category == "Category" && StandardCategories.Contains(key.ToString()))
        {
            var d = new PSDict(4, -1);
            d.PutRaw("Category", PSName.Lit(key.ToString()!));
            return d;
        }
        // Implementation categories: report only what is really supported, so drivers fall back to other methods
        // (e.g. Windows drivers use Type 32 bitmap fonts when FontType 32 is available, otherwise Type 3).
        if (key is PSInt kind)
        {
            int[]? supported = category switch
            {
                "FontType" => new[] { 0, 1, 3, 11, 42 },
                "FMapType" => new[] { 2, 3, 4, 5, 9 },
                "ImageType" => new[] { 1, 3, 4 },
                "PatternType" => new[] { 1, 2 },
                "ShadingType" => new[] { 1, 2, 3, 4, 5, 6, 7 },
                "FunctionType" => new[] { 0, 2, 3 },
                "FormType" => new[] { 1 },
                "HalftoneType" => new[] { 1, 2, 3, 4, 5, 6, 10, 16 },
                "ColorRenderingType" => new[] { 1 },
                _ => null,
            };
            if (supported is not null) return supported.Contains(kind.Value) ? key : null;
        }
        if (category == "ColorSpaceFamily" || category == "Filter")
            return key;
        if (category == "Encoding")
        {
            return key.ToString() switch
            {
                "StandardEncoding" => SystemDict.Get("StandardEncoding"),
                "ISOLatin1Encoding" => SystemDict.Get("ISOLatin1Encoding"),
                _ => null,
            };
        }
        return null;
    }

    internal void DefineResourceInstance(string category, PSObject key, PSObject value)
    {
        var cat = Category(category);
        bool global = value switch
        {
            PSDict d => d.SaveLevel < 0,
            PSArray a => a.Buf.SaveLevel < 0,
            PSString s => s.Buf.SaveLevel < 0,
            _ => GlobalAlloc,
        };
        if (global) cat.Global.Map[PSDict.Key(key)] = value;
        else DictPut(cat.Local, PSDict.Key(key), value);
    }

    private void RegisterResourceOperators()
    {
        Def("defineresource", ip =>
        {
            var category = ip.Pop().ToString()!;
            var instance = ip.Pop();
            var key = ip.Pop();
            switch (category)
            {
                case "Font":
                    if (instance is PSDict fd) instance = ip.DefineFont(key, fd);
                    break;
                case "CIDFont":
                    if (instance is PSDict cd)
                    {
                        cd.Font ??= ip.BuildFontObject(cd);
                        cd.Map[NameAtom.Get("FID")] = PSFontId.Instance;
                    }
                    break;
                case "CMap":
                    if (instance is PSDict cm && cm.Get(".CMapData") is null)
                        cm.Map[NameAtom.Get(".CMapData")] = new PSCMapObj(CMapData.Identity());
                    break;
            }
            ip.DefineResourceInstance(category, key, instance);
            ip.Push(instance);
        });
        Def("undefineresource", ip =>
        {
            var category = ip.Pop().ToString()!;
            var key = PSDict.Key(ip.Pop());
            var cat = ip.Category(category);
            ip.DictRemove(cat.Local, key);
            cat.Global.Map.Remove(key);
            if (category == "Font") { ip.DictRemove(ip.FontDirectory, key); ip.GlobalFontDirectory.Map.Remove(key); }
        });
        Def("findresource", ip =>
        {
            var category = ip.Pop().ToString()!;
            var key = ip.Pop();
            if (category == "Font") { ip.Push(ip.FindFont(key)); return; }
            var v = ip.FindResourceInstance(category, key) ?? throw new PSError("undefinedresource", $"{key} ({category})");
            ip.Push(v);
        });
        Def("resourcestatus", ip =>
        {
            var category = ip.Pop().ToString()!;
            var key = ip.Pop();
            var v = ip.FindResourceInstance(category, key);
            if (v is null) { ip.Push(false); return; }
            var k = PSDict.Key(key);
            ip.Push(ip.Category(category).Global.Map.ContainsKey(k) ? 2 : 1);
            ip.Push(0);
            ip.Push(true);
        });
        Def("resourceforall", ip =>
        {
            var category = ip.Pop().ToString()!;
            var scratch = ip.PopString();
            var proc = ip.PopProc();
            var template = ip.PopString().Text;
            var cat = ip.Category(category);
            var keys = cat.Local.Map.Keys.Concat(cat.Global.Map.Keys).Distinct()
                .Select(k => PSDict.KeyObject(k).ToString() ?? "")
                .Where(n => WildcardMatch(template, n)).ToList();
            ip.EStack.Add(new ResourceForallFrame(keys, scratch, proc));
        });
    }

    private static bool WildcardMatch(string pattern, string text)
    {
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(text, rx);
    }

    private sealed class ResourceForallFrame : LoopFrame
    {
        private readonly List<string> _keys;
        private readonly PSString _scratch;
        private readonly PSObject _proc;
        private int _i;
        public ResourceForallFrame(List<string> keys, PSString scratch, PSObject proc) { _keys = keys; _scratch = scratch; _proc = proc; }
        public override bool Step(Interpreter ip)
        {
            if (_i >= _keys.Count) return false;
            var bytes = System.Text.Encoding.Latin1.GetBytes(_keys[_i++]);
            if (bytes.Length > _scratch.Length) throw new PSError("rangecheck");
            ip.TouchString(_scratch);
            bytes.CopyTo(_scratch.Buf.Data, _scratch.Offset);
            ip.Push(_scratch.Sub(0, bytes.Length));
            ip.ExecOperand(_proc);
            return true;
        }
    }

    // ---------------- CIDInit procset (CMap construction) ----------------

    private CMapData CurrentCMap()
    {
        var d = CurrentDict;
        if (d.Get(".CMapData") is PSCMapObj c) return c.Data;
        var data = new CMapData();
        d.Map[NameAtom.Get(".CMapData")] = new PSCMapObj(data);
        return data;
    }

    private PSDict BuildCidInit()
    {
        var p = new PSDict(24, -1);
        void Op(string name, Action<Interpreter> a) => p.PutRaw(name, new PSOperator(name, a));
        Op("begincmap", ip => ip.CurrentCMap());
        Op("endcmap", ip =>
        {
            var d = ip.CurrentDict;
            if (d.Get("WMode") is { } wm && d.Get(".CMapData") is PSCMapObj c) c.Data.WMode = (int)Num(wm);
        });
        Op("usecmap", ip =>
        {
            var key = ip.Pop();
            if (ip.FindResourceInstance("CMap", key) is PSDict other && other.Get(".CMapData") is PSCMapObj oc)
                ip.CurrentCMap().CopyFrom(oc.Data);
        });
        Op("usefont", ip => ip.CurrentCMap().CurrentFont = ip.PopInt());
        Op("begincodespacerange", ip => { ip.PopInt(); ip.Push(PSMark.Instance); });
        Op("endcodespacerange", ip =>
        {
            int n = ip.CountToMark();
            var items = new PSObject[n];
            for (int i = n - 1; i >= 0; i--) items[i] = ip.Pop();
            ip.Pop();
            var cm = ip.CurrentCMap();
            for (int i = 0; i + 1 < n; i += 2)
                if (items[i] is PSString lo && items[i + 1] is PSString hi) cm.CodeSpace.Add((lo.ToArray(), hi.ToArray()));
        });
        Op("begincidrange", ip => { ip.PopInt(); ip.Push(PSMark.Instance); });
        Op("endcidrange", ip =>
        {
            int n = ip.CountToMark();
            var items = new PSObject[n];
            for (int i = n - 1; i >= 0; i--) items[i] = ip.Pop();
            ip.Pop();
            var cm = ip.CurrentCMap();
            for (int i = 0; i + 2 < n; i += 3)
                if (items[i] is PSString lo && items[i + 1] is PSString hi && IsNum(items[i + 2]))
                    cm.Ranges.Add((ToCode(lo), ToCode(hi), lo.Length, (int)Num(items[i + 2]), cm.CurrentFont));
        });
        Op("begincidchar", ip => { ip.PopInt(); ip.Push(PSMark.Instance); });
        Op("endcidchar", ip =>
        {
            int n = ip.CountToMark();
            var items = new PSObject[n];
            for (int i = n - 1; i >= 0; i--) items[i] = ip.Pop();
            ip.Pop();
            var cm = ip.CurrentCMap();
            for (int i = 0; i + 1 < n; i += 2)
                if (items[i] is PSString c && IsNum(items[i + 1]))
                    cm.Chars[(ToCode(c), c.Length)] = ((int)Num(items[i + 1]), cm.CurrentFont);
        });
        foreach (var (begin, end) in new[] { ("beginnotdefrange", "endnotdefrange"), ("beginnotdefchar", "endnotdefchar"),
                     ("beginbfrange", "endbfrange"), ("beginbfchar", "endbfchar"), ("beginrearrangedfont", "endrearrangedfont") })
        {
            Op(begin, ip => { ip.Pop(); ip.Push(PSMark.Instance); });
            Op(end, ip => { int n = ip.CountToMark(); ip.SetCount(ip.Count - n - 1); });
        }
        Op("beginusematrix", ip => ip.Pop());
        Op("endusematrix", ip => ip.Pop());
        Op("StartData", ip => throw new PSError("undefined", "CFF font data (StartData) is not supported"));
        p.PutRaw("CIDSystemInfo", new PSDict(3, -1));
        return p;
    }

    private static uint ToCode(PSString s)
    {
        uint v = 0;
        for (int i = 0; i < s.Length && i < 4; i++) v = (v << 8) | s[i];
        return v;
    }
}
