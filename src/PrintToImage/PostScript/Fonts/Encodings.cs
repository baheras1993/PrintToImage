namespace PrintToImage.PostScript.Fonts;

/// <summary>Standard PostScript encodings and a glyph-name → Unicode table for font substitution.</summary>
public static class Encodings
{
    private static string[] Build(string spec)
    {
        // spec: "code:name code:name ..." with octal codes
        var arr = Enumerable.Repeat(".notdef", 256).ToArray();
        foreach (var item in spec.Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = item.IndexOf(':');
            arr[Convert.ToInt32(item[..colon], 8)] = item[(colon + 1)..];
        }
        return arr;
    }

    private static string[] Ascii(string[] arr)
    {
        string[] names =
        {
            "space","exclam","quotedbl","numbersign","dollar","percent","ampersand","quoteright","parenleft","parenright","asterisk","plus","comma","hyphen","period","slash",
            "zero","one","two","three","four","five","six","seven","eight","nine","colon","semicolon","less","equal","greater","question",
            "at","A","B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","T","U","V","W","X","Y","Z","bracketleft","backslash","bracketright","asciicircum","underscore",
            "quoteleft","a","b","c","d","e","f","g","h","i","j","k","l","m","n","o","p","q","r","s","t","u","v","w","x","y","z","braceleft","bar","braceright","asciitilde",
        };
        for (int i = 0; i < names.Length; i++) arr[32 + i] = names[i];
        return arr;
    }

    public static readonly string[] Standard = Ascii(Build(@"
241:exclamdown 242:cent 243:sterling 244:fraction 245:yen 246:florin 247:section 250:currency 251:quotesingle 252:quotedblleft 253:guillemotleft
254:guilsinglleft 255:guilsinglright 256:fi 257:fl 261:endash 262:dagger 263:daggerdbl 264:periodcentered 266:paragraph 267:bullet 270:quotesinglbase
271:quotedblbase 272:quotedblright 273:guillemotright 274:ellipsis 275:perthousand 277:questiondown 301:grave 302:acute 303:circumflex 304:tilde
305:macron 306:breve 307:dotaccent 310:dieresis 312:ring 313:cedilla 315:hungarumlaut 316:ogonek 317:caron 320:emdash 341:AE 343:ordfeminine
350:Lslash 351:Oslash 352:OE 353:ordmasculine 361:ae 365:dotlessi 370:lslash 371:oslash 372:oe 373:germandbls"));

    public static readonly string[] IsoLatin1 = BuildIsoLatin1();

    private static string[] BuildIsoLatin1()
    {
        var arr = Ascii(Build(@"
220:dotlessi 221:grave 222:acute 223:circumflex 224:tilde 225:macron 226:breve 227:dotaccent 230:dieresis 232:ring 233:cedilla 235:hungarumlaut 236:ogonek 237:caron
240:space 241:exclamdown 242:cent 243:sterling 244:currency 245:yen 246:brokenbar 247:section 250:dieresis 251:copyright 252:ordfeminine 253:guillemotleft
254:logicalnot 255:hyphen 256:registered 257:macron 260:degree 261:plusminus 262:twosuperior 263:threesuperior 264:acute 265:mu 266:paragraph
267:periodcentered 270:cedilla 271:onesuperior 272:ordmasculine 273:guillemotright 274:onequarter 275:onehalf 276:threequarters 277:questiondown
300:Agrave 301:Aacute 302:Acircumflex 303:Atilde 304:Adieresis 305:Aring 306:AE 307:Ccedilla 310:Egrave 311:Eacute 312:Ecircumflex 313:Edieresis
314:Igrave 315:Iacute 316:Icircumflex 317:Idieresis 320:Eth 321:Ntilde 322:Ograve 323:Oacute 324:Ocircumflex 325:Otilde 326:Odieresis 327:multiply
330:Oslash 331:Ugrave 332:Uacute 333:Ucircumflex 334:Udieresis 335:Yacute 336:Thorn 337:germandbls 340:agrave 341:aacute 342:acircumflex 343:atilde
344:adieresis 345:aring 346:ae 347:ccedilla 350:egrave 351:eacute 352:ecircumflex 353:edieresis 354:igrave 355:iacute 356:icircumflex 357:idieresis
360:eth 361:ntilde 362:ograve 363:oacute 364:ocircumflex 365:otilde 366:odieresis 367:divide 370:oslash 371:ugrave 372:uacute 373:ucircumflex
374:udieresis 375:yacute 376:thorn 377:ydieresis"));
        arr[0x27] = "quoteright";
        arr[0x2D] = "minus";
        arr[0x60] = "quoteleft";
        return arr;
    }

    public static readonly string[] Symbol = Build(@"
040:space 041:exclam 042:universal 043:numbersign 044:existential 045:percent 046:ampersand 047:suchthat 050:parenleft 051:parenright 052:asteriskmath
053:plus 054:comma 055:minus 056:period 057:slash 060:zero 061:one 062:two 063:three 064:four 065:five 066:six 067:seven 070:eight 071:nine 072:colon
073:semicolon 074:less 075:equal 076:greater 077:question 100:congruent 101:Alpha 102:Beta 103:Chi 104:Delta 105:Epsilon 106:Phi 107:Gamma 110:Eta
111:Iota 112:theta1 113:Kappa 114:Lambda 115:Mu 116:Nu 117:Omicron 120:Pi 121:Theta 122:Rho 123:Sigma 124:Tau 125:Upsilon 126:sigma1 127:Omega 130:Xi
131:Psi 132:Zeta 133:bracketleft 134:therefore 135:bracketright 136:perpendicular 137:underscore 140:radicalex 141:alpha 142:beta 143:chi 144:delta
145:epsilon 146:phi 147:gamma 150:eta 151:iota 152:phi1 153:kappa 154:lambda 155:mu 156:nu 157:omicron 160:pi 161:theta 162:rho 163:sigma 164:tau
165:upsilon 166:omega1 167:omega 170:xi 171:psi 172:zeta 173:braceleft 174:bar 175:braceright 176:similar 240:Euro 241:Upsilon1 242:minute 243:lessequal
244:fraction 245:infinity 246:florin 247:club 250:diamond 251:heart 252:spade 253:arrowboth 254:arrowleft 255:arrowup 256:arrowright 257:arrowdown
260:degree 261:plusminus 262:second 263:greaterequal 264:multiply 265:proportional 266:partialdiff 267:bullet 270:divide 271:notequal 272:equivalence
273:approxequal 274:ellipsis 275:arrowvertex 276:arrowhorizex 277:carriagereturn 300:aleph 301:Ifraktur 302:Rfraktur 303:weierstrass 304:circlemultiply
305:circleplus 306:emptyset 307:intersection 310:union 311:propersuperset 312:reflexsuperset 313:notsubset 314:propersubset 315:reflexsubset
316:element 317:notelement 320:angle 321:gradient 322:registerserif 323:copyrightserif 324:trademarkserif 325:product 326:radical 327:dotmath
330:logicalnot 331:logicaland 332:logicalor 333:arrowdblboth 334:arrowdblleft 335:arrowdblup 336:arrowdblright 337:arrowdbldown 340:lozenge
341:angleleft 342:registersans 343:copyrightsans 344:trademarksans 345:summation 346:parenlefttp 347:parenleftex 350:parenleftbt 351:bracketlefttp
352:bracketleftex 353:bracketleftbt 354:bracelefttp 355:braceleftmid 356:braceleftbt 357:braceex 361:angleright 362:integral 363:integraltp
364:integralex 365:integralbt 366:parenrighttp 367:parenrightex 370:parenrightbt 371:bracketrighttp 372:bracketrightex 373:bracketrightbt
374:bracerighttp 375:bracerightmid 376:bracerightbt");

    private static readonly Dictionary<string, int> SymbolCodes = Symbol
        .Select((n, i) => (n, i)).Where(x => x.n != ".notdef").GroupBy(x => x.n).ToDictionary(g => g.Key, g => g.First().i);

    public static int SymbolCode(string glyph) => SymbolCodes.TryGetValue(glyph, out var c) ? c : -1;

    public static PSArray StandardArray() => ToArray(Standard);
    public static PSArray IsoLatin1Array() => ToArray(IsoLatin1);

    private static PSArray ToArray(string[] names) =>
        new(names.Select(n => (PSObject)PSName.Lit(n)).ToArray(), -1) { Access = Access.ReadOnly };

    // ---------------- glyph names → Unicode ----------------

    private static readonly Dictionary<string, int> Agl = BuildAgl();

    private static Dictionary<string, int> BuildAgl()
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        // ASCII names
        var ascii = Ascii(new string[256]);
        for (int i = 32; i < 127; i++) if (ascii[i] != null) d[ascii[i]] = i;
        d["quoteright"] = 0x2019; d["quoteleft"] = 0x2018; d["quotesingle"] = 0x27; d["grave"] = 0x60; d["minus"] = 0x2212;
        // Latin-1 (from ISOLatin1Encoding, codes 0xA0-0xFF map to the same Unicode values)
        for (int i = 0xA1; i <= 0xFF; i++) if (IsoLatin1[i] != ".notdef" && !d.ContainsKey(IsoLatin1[i])) d[IsoLatin1[i]] = i;
        d["space"] = 0x20; d["dieresis"] = 0xA8; d["macron"] = 0xAF; d["acute"] = 0xB4; d["cedilla"] = 0xB8; d["hyphen"] = 0x2D;
        var extra = new (string, int)[]
        {
            ("dotlessi",0x131),("circumflex",0x2C6),("caron",0x2C7),("breve",0x2D8),("dotaccent",0x2D9),("ring",0x2DA),("ogonek",0x2DB),("tilde",0x2DC),
            ("hungarumlaut",0x2DD),("fi",0xFB01),("fl",0xFB02),("endash",0x2013),("emdash",0x2014),("dagger",0x2020),("daggerdbl",0x2021),("bullet",0x2022),
            ("quotesinglbase",0x201A),("quotedblbase",0x201E),("quotedblleft",0x201C),("quotedblright",0x201D),("guilsinglleft",0x2039),("guilsinglright",0x203A),
            ("ellipsis",0x2026),("perthousand",0x2030),("fraction",0x2044),("florin",0x192),("Lslash",0x141),("lslash",0x142),("OE",0x152),("oe",0x153),
            ("Scaron",0x160),("scaron",0x161),("Zcaron",0x17D),("zcaron",0x17E),("Ydieresis",0x178),("trademark",0x2122),("Euro",0x20AC),("euro",0x20AC),
            ("Amacron",0x100),("amacron",0x101),("Abreve",0x102),("abreve",0x103),("Aogonek",0x104),("aogonek",0x105),("Cacute",0x106),("cacute",0x107),
            ("Ccaron",0x10C),("ccaron",0x10D),("Dcaron",0x10E),("dcaron",0x10F),("Dcroat",0x110),("dcroat",0x111),("Emacron",0x112),("emacron",0x113),
            ("Edotaccent",0x116),("edotaccent",0x117),("Eogonek",0x118),("eogonek",0x119),("Ecaron",0x11A),("ecaron",0x11B),("Gbreve",0x11E),("gbreve",0x11F),
            ("Gcommaaccent",0x122),("gcommaaccent",0x123),("Imacron",0x12A),("imacron",0x12B),("Iogonek",0x12E),("iogonek",0x12F),("Idotaccent",0x130),
            ("Kcommaaccent",0x136),("kcommaaccent",0x137),("Lacute",0x139),("lacute",0x13A),("Lcommaaccent",0x13B),("lcommaaccent",0x13C),("Lcaron",0x13D),
            ("lcaron",0x13E),("Nacute",0x143),("nacute",0x144),("Ncommaaccent",0x145),("ncommaaccent",0x146),("Ncaron",0x147),("ncaron",0x148),
            ("Omacron",0x14C),("omacron",0x14D),("Ohungarumlaut",0x150),("ohungarumlaut",0x151),("Racute",0x154),("racute",0x155),("Rcommaaccent",0x156),
            ("rcommaaccent",0x157),("Rcaron",0x158),("rcaron",0x159),("Sacute",0x15A),("sacute",0x15B),("Scedilla",0x15E),("scedilla",0x15F),
            ("Tcommaaccent",0x162),("tcommaaccent",0x163),("Tcaron",0x164),("tcaron",0x165),("Umacron",0x16A),("umacron",0x16B),("Uring",0x16E),("uring",0x16F),
            ("Uhungarumlaut",0x170),("uhungarumlaut",0x171),("Uogonek",0x172),("uogonek",0x173),("Zacute",0x179),("zacute",0x17A),("Zdotaccent",0x17B),
            ("zdotaccent",0x17C),("Scommaaccent",0x218),("scommaaccent",0x219),("Tcedilla",0x162),("tcedilla",0x163),("Gdotaccent",0x120),("gdotaccent",0x121),
            ("commaaccent",0xF6C3),("lozenge",0x25CA),("notequal",0x2260),("lessequal",0x2264),("greaterequal",0x2265),("infinity",0x221E),("partialdiff",0x2202),
            ("summation",0x2211),("product",0x220F),("pi",0x3C0),("integral",0x222B),("radical",0x221A),("approxequal",0x2248),("Delta",0x2206),("Omega",0x2126),
            ("mu",0xB5),("estimated",0x212E),("onethird",0x2153),("twothirds",0x2154),("oneeighth",0x215B),("threeeighths",0x215C),("fiveeighths",0x215D),
            ("seveneighths",0x215E),("arrowleft",0x2190),("arrowup",0x2191),("arrowright",0x2192),("arrowdown",0x2193),("arrowboth",0x2194),("arrowupdn",0x2195),
            ("degree",0xB0),("club",0x2663),("diamond",0x2666),("heart",0x2665),("spade",0x2660),("female",0x2640),("male",0x2642),("musicalnote",0x266A),
            ("filledbox",0x25A0),("H22073",0x25A1),("triagup",0x25B2),("triagdn",0x25BC),("circle",0x25CB),("invbullet",0x25D8),("openbullet",0x25E6),
            ("smileface",0x263A),("sun",0x263C),("house",0x2302),("dkshade",0x2593),("shade",0x2592),("ltshade",0x2591),("block",0x2588),("lfblock",0x258C),
            ("rtblock",0x2590),("dnblock",0x2584),("upblock",0x2580),("nbspace",0xA0),("sfthyphen",0xAD),("middot",0xB7),("overscore",0xAF),("Ohm",0x2126),
            ("Tbar",0x166),("tbar",0x167),("Hbar",0x126),("hbar",0x127),("Eng",0x14A),("eng",0x14B),("kgreenlandic",0x138),("napostrophe",0x149),
            ("Ldot",0x13F),("ldot",0x140),("IJ",0x132),("ij",0x133),("Itilde",0x128),("itilde",0x129),("Utilde",0x168),("utilde",0x169),
            ("Wcircumflex",0x174),("wcircumflex",0x175),("Ycircumflex",0x176),("ycircumflex",0x177),("longs",0x17F),
        };
        foreach (var (n, u) in extra) d[n] = u;
        // Greek
        string[] greekUpper = { "Alpha","Beta","Gamma","Delta","Epsilon","Zeta","Eta","Theta","Iota","Kappa","Lambda","Mu","Nu","Xi","Omicron","Pi","Rho","","Sigma","Tau","Upsilon","Phi","Chi","Psi","Omega" };
        for (int i = 0; i < greekUpper.Length; i++) if (greekUpper[i] != "") d.TryAdd(greekUpper[i], 0x391 + i);
        string[] greekLower = { "alpha","beta","gamma","delta","epsilon","zeta","eta","theta","iota","kappa","lambda","mu","nu","xi","omicron","pi","rho","sigma1","sigma","tau","upsilon","phi","chi","psi","omega" };
        for (int i = 0; i < greekLower.Length; i++) if (greekLower[i] != "mu") d.TryAdd(greekLower[i], 0x3B1 + i);
        d["Delta"] = 0x394; d["Omega"] = 0x3A9; d["mu"] = 0xB5; d["pi"] = 0x3C0;
        d["theta1"] = 0x3D1; d["phi1"] = 0x3D5; d["omega1"] = 0x3D6; d["Upsilon1"] = 0x3D2;
        // Cyrillic (AGL afii names)
        int[] cyr = { 0x410, 0x411, 0x412, 0x413, 0x414, 0x415, 0x401, 0x416, 0x417, 0x418, 0x419, 0x41A, 0x41B, 0x41C, 0x41D, 0x41E, 0x41F, 0x420, 0x421, 0x422, 0x423, 0x424, 0x425, 0x426, 0x427, 0x428, 0x429, 0x42A, 0x42B, 0x42C, 0x42D, 0x42E, 0x42F };
        for (int i = 0; i < cyr.Length; i++) { d[$"afii{10017 + i}"] = cyr[i]; d[$"afii{10065 + i}"] = cyr[i] + (cyr[i] == 0x401 ? 0x50 : 0x20); }
        var cyrExtra = new (int, int)[] { (10050, 0x490), (10051, 0x402), (10052, 0x403), (10053, 0x404), (10054, 0x405), (10055, 0x406), (10056, 0x407), (10057, 0x408), (10058, 0x409), (10059, 0x40A), (10060, 0x40B), (10061, 0x40C), (10062, 0x40E), (10098, 0x491), (10099, 0x452), (10100, 0x453), (10101, 0x454), (10102, 0x455), (10103, 0x456), (10104, 0x457), (10105, 0x458), (10106, 0x459), (10107, 0x45A), (10108, 0x45B), (10109, 0x45C), (10110, 0x45E), (10145, 0x40F), (10193, 0x45F), (61352, 0x2116) };
        foreach (var (n, u) in cyrExtra) d[$"afii{n}"] = u;
        // Hebrew letters
        for (int i = 0; i <= 26; i++) d[$"afii{57664 + i}"] = 0x5D0 + i;
        return d;
    }

    /// <summary>Unicode code point for a glyph name, or -1.</summary>
    public static int ToUnicode(string name)
    {
        if (Agl.TryGetValue(name, out var u)) return u;
        int dot = name.IndexOf('.');
        if (dot > 0 && Agl.TryGetValue(name[..dot], out u)) return u;
        if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length >= 7
            && int.TryParse(name.AsSpan(3, 4), System.Globalization.NumberStyles.HexNumber, null, out u)) return u;
        if (name.StartsWith('u') && name.Length is >= 5 and <= 7
            && int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out u)) return u;
        return -1;
    }
}
