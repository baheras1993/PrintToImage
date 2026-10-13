using System.Buffers.Binary;
using System.Text;

namespace PrintToImage.PostScript.Fonts;

/// <summary>
/// Builds a standalone TrueType font from a Type 42 font's tables and glyph descriptions, so PDF output
/// can embed real fonts instead of drawing every character as a path. Hinting programs are left out.
/// </summary>
public static class TtfBuilder
{
    /// <summary>
    /// Builds a subset font containing <paramref name="glyphs"/> (original id → glyph description), renumbered
    /// 0..n-1 with .notdef first. Composite glyph components must be included in the dictionary.
    /// Returns the font and the map from original to new glyph ids, or null if the tables are unusable.
    /// </summary>
    public static (byte[] Font, Dictionary<int, int> Map)? BuildSubset(TrueTypeData tt, IReadOnlyDictionary<int, byte[]> glyphs, IReadOnlyDictionary<int, int> unicode, string psName)
    {
        var head = tt.Table("head");
        var hhea = tt.Table("hhea");
        var maxp = tt.Table("maxp");
        if (head is null || head.Length < 54 || hhea is null || hhea.Length < 36 || maxp is null || maxp.Length < 6) return null;

        var order = new List<int> { 0 };
        order.AddRange(glyphs.Keys.Where(g => g != 0).OrderBy(g => g));
        if (order.Count > 65535) return null;
        var map = new Dictionary<int, int>();
        for (int i = 0; i < order.Count; i++) map[order[i]] = i;

        var glyf = new MemoryStream();
        var loca = new byte[(order.Count + 1) * 4];
        var hmtx = new byte[order.Count * 4];
        for (int i = 0; i < order.Count; i++)
        {
            int g = order[i];
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(i * 4), (uint)glyf.Length);
            if (glyphs.TryGetValue(g, out var data) && data.Length > 0)
            {
                glyf.Write(TrueTypeData.RemapComponents(data, c => map.TryGetValue(c, out var n) ? n : 0));
                while (glyf.Length % 4 != 0) glyf.WriteByte(0);
            }
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(i * 4), (ushort)tt.Advance(g));
            BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(i * 4 + 2), (short)tt.Lsb(g));
        }
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(order.Count * 4), (uint)glyf.Length);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = (byte[])head.Clone(),
            ["hhea"] = (byte[])hhea.Clone(),
            ["maxp"] = (byte[])maxp.Clone(),
            ["hmtx"] = hmtx,
            ["glyf"] = glyf.ToArray(),
            ["loca"] = loca,
            ["post"] = PostFormat3(),
            ["cmap"] = Cmap(unicode.Where(kv => map.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value), map),
            ["name"] = NameTable(psName),
        };
        if (tt.Table("OS/2") is { } os2) tables["OS/2"] = os2;
        BinaryPrimitives.WriteInt16BigEndian(tables["head"].AsSpan(50), 1);           // long loca
        BinaryPrimitives.WriteUInt32BigEndian(tables["head"].AsSpan(8), 0);           // checkSumAdjustment
        BinaryPrimitives.WriteUInt16BigEndian(tables["maxp"].AsSpan(4), (ushort)order.Count);
        BinaryPrimitives.WriteUInt16BigEndian(tables["hhea"].AsSpan(34), (ushort)order.Count);
        return (Assemble(tables), map);
    }
    private static byte[] Assemble(SortedDictionary<string, byte[]> tables)
    {
        int n = tables.Count;
        int searchRange = 1, entrySelector = 0;
        while (searchRange * 2 <= n) { searchRange *= 2; entrySelector++; }
        searchRange *= 16;
        var ms = new MemoryStream();
        var hdr = new byte[12 + 16 * n];
        BinaryPrimitives.WriteUInt32BigEndian(hdr, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(4), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(10), (ushort)(n * 16 - searchRange));
        ms.Write(hdr);
        int i = 0;
        foreach (var (tag, data) in tables)
        {
            int rec = 12 + 16 * i++;
            long offset = ms.Length;
            Encoding.ASCII.GetBytes(tag.PadRight(4)).CopyTo(hdr, rec);
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(rec + 4), Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(rec + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(rec + 12), (uint)data.Length);
            ms.Write(data);
            while (ms.Length % 4 != 0) ms.WriteByte(0);
        }
        var result = ms.ToArray();
        hdr.CopyTo(result, 0);
        return result;
    }

    private static uint Checksum(byte[] d)
    {
        uint sum = 0;
        for (int i = 0; i < d.Length; i += 4)
        {
            uint v = 0;
            for (int k = 0; k < 4; k++) v = (v << 8) | (i + k < d.Length ? d[i + k] : 0u);
            sum += v;
        }
        return sum;
    }

    private static byte[] PostFormat3()
    {
        var p = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(p, 0x00030000);
        return p;
    }

    /// <summary>
    /// A cmap with one (3,1) format 4 subtable mapping each known code point to its new glyph id, so PDF viewers
    /// can extract and search the text. Each code point maps to one glyph; code points above U+FFFF are skipped.
    /// </summary>
    private static byte[] Cmap(Dictionary<int, int> unicodeByOldGid, Dictionary<int, int> map)
    {
        var pairs = new SortedDictionary<int, int>();
        foreach (var (gid, cp) in unicodeByOldGid)
            if (cp is > 0 and < 0xFFFF && map.TryGetValue(gid, out var ng) && !pairs.ContainsKey(cp)) pairs[cp] = ng;
        int segCount = pairs.Count + 1;
        int subLen = 16 + segCount * 8;
        var c = new byte[12 + subLen];
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(2), 1);      // one encoding record
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(4), 3);      // platform 3 (Windows)
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(6), 1);      // encoding 1 (Unicode BMP)
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(8), 12);
        var s = c.AsSpan(12);
        int sr = 2, es = 0;
        while (sr * 2 <= segCount * 2) { sr *= 2; es++; }
        BinaryPrimitives.WriteUInt16BigEndian(s, 4);
        BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)subLen);
        BinaryPrimitives.WriteUInt16BigEndian(s[6..], (ushort)(segCount * 2));
        BinaryPrimitives.WriteUInt16BigEndian(s[8..], (ushort)sr);
        BinaryPrimitives.WriteUInt16BigEndian(s[10..], (ushort)es);
        BinaryPrimitives.WriteUInt16BigEndian(s[12..], (ushort)(segCount * 2 - sr));
        int ends = 14, starts = ends + segCount * 2 + 2, deltas = starts + segCount * 2, ranges = deltas + segCount * 2;
        int i = 0;
        foreach (var (cp, ng) in pairs)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[(ends + i * 2)..], (ushort)cp);
            BinaryPrimitives.WriteUInt16BigEndian(s[(starts + i * 2)..], (ushort)cp);
            BinaryPrimitives.WriteUInt16BigEndian(s[(deltas + i * 2)..], (ushort)((ng - cp) & 0xFFFF));
            i++;
        }
        BinaryPrimitives.WriteUInt16BigEndian(s[(ends + i * 2)..], 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(s[(starts + i * 2)..], 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(s[(deltas + i * 2)..], 1);
        _ = ranges; // idRangeOffset entries stay 0
        return c;
    }

    private static byte[] NameTable(string psName)
    {
        var clean = new string(psName.Where(ch => ch > 32 && ch < 127 && !"[](){}<>/%".Contains(ch)).ToArray());
        if (clean.Length == 0) clean = "PrintToImageFont";
        if (clean.Length > 60) clean = clean[..60];
        var records = new (int Id, string Text)[] { (1, clean), (2, "Regular"), (4, clean), (6, clean) };
        var strings = records.Select(r => Encoding.BigEndianUnicode.GetBytes(r.Text)).ToArray();
        int headerLen = 6 + 12 * records.Length;
        var ms = new MemoryStream();
        var hdr = new byte[headerLen];
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(2), (ushort)records.Length);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(4), (ushort)headerLen);
        int off = 0;
        for (int i = 0; i < records.Length; i++)
        {
            var r = hdr.AsSpan(6 + 12 * i);
            BinaryPrimitives.WriteUInt16BigEndian(r, 3);
            BinaryPrimitives.WriteUInt16BigEndian(r[2..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(r[4..], 0x409);
            BinaryPrimitives.WriteUInt16BigEndian(r[6..], (ushort)records[i].Id);
            BinaryPrimitives.WriteUInt16BigEndian(r[8..], (ushort)strings[i].Length);
            BinaryPrimitives.WriteUInt16BigEndian(r[10..], (ushort)off);
            off += strings[i].Length;
        }
        ms.Write(hdr);
        foreach (var s in strings) ms.Write(s);
        return ms.ToArray();
    }
}
