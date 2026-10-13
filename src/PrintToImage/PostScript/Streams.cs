using System.IO.Compression;
using SkiaSharp;

namespace PrintToImage.PostScript;

/// <summary>Byte stream with one byte of push-back, as used by the scanner and the file operators.</summary>
public abstract class PSStream
{
    private int _pushback = -1;
    public bool IsClosed { get; protected set; }
    public virtual bool CanWrite => false;
    /// <summary>Total bytes delivered to the reader (for limits and diagnostics).</summary>
    public long BytesRead { get; private set; }

    protected abstract int ReadCore();

    public int ReadByte()
    {
        if (_pushback >= 0) { var b = _pushback; _pushback = -1; BytesRead++; return b; }
        if (IsClosed) return -1;
        var r = ReadCore();
        if (r >= 0) BytesRead++;
        return r;
    }

    public int Peek()
    {
        if (_pushback >= 0) return _pushback;
        var b = ReadByte();
        if (b >= 0) Unread(b);
        return b;
    }

    public void Unread(int b)
    {
        if (b < 0) return;
        _pushback = b;
        BytesRead--;
    }

    /// <summary>Reads up to buffer.Length bytes; returns the count (0 at EOF).</summary>
    public virtual int Read(Span<byte> buffer)
    {
        int n = 0;
        while (n < buffer.Length)
        {
            var b = ReadByte();
            if (b < 0) break;
            buffer[n++] = (byte)b;
        }
        return n;
    }

    public virtual void Write(ReadOnlySpan<byte> data) { }
    public virtual void Close() { IsClosed = true; _pushback = -1; }
    /// <summary>For ReusableStreamDecode files: go back to the start.</summary>
    public virtual bool TryReset() => false;
}

/// <summary>Reads from a .NET stream (the print job).</summary>
public sealed class DotNetStreamSource : PSStream
{
    private readonly Stream _s;
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _pos, _len;
    private readonly bool _ownsStream;

    public DotNetStreamSource(Stream s, bool ownsStream = true) { _s = s; _ownsStream = ownsStream; }

    protected override int ReadCore()
    {
        if (_pos >= _len)
        {
            _len = _s.Read(_buf, 0, _buf.Length);
            _pos = 0;
            if (_len <= 0) { _len = 0; return -1; }
        }
        return _buf[_pos++];
    }

    public override void Close()
    {
        base.Close();
        if (_ownsStream) _s.Dispose();
    }
}

public sealed class BytesSource : PSStream
{
    private readonly byte[] _data;
    private readonly int _start, _end;
    private int _pos;
    private readonly bool _reusable;

    public BytesSource(byte[] data, int offset, int count, bool reusable = false)
    { _data = data; _start = _pos = offset; _end = offset + count; _reusable = reusable; }

    public BytesSource(byte[] data, bool reusable = false) : this(data, 0, data.Length, reusable) { }

    protected override int ReadCore() => _pos < _end ? _data[_pos++] : -1;

    public override int Read(Span<byte> buffer)
    {
        int n = 0;
        if (buffer.Length > 0 && Peek() >= 0) { buffer[0] = (byte)ReadByte(); n = 1; }
        int count = Math.Min(buffer.Length - n, _end - _pos);
        if (count > 0) { _data.AsSpan(_pos, count).CopyTo(buffer[n..]); _pos += count; n += count; }
        return n;
    }

    public override bool TryReset()
    {
        if (!_reusable) return false;
        _pos = _start;
        IsClosed = false;
        return true;
    }
}

/// <summary>Output sink for %stdout / print: discarded except for diagnostics.</summary>
public sealed class StdoutSink : PSStream
{
    private readonly Action<string>? _log;
    private readonly System.Text.StringBuilder _line = new();
    public StdoutSink(Action<string>? log) => _log = log;
    public override bool CanWrite => true;
    protected override int ReadCore() => -1;
    public override void Write(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (b == '\n' || b == '\r') { Flush(); continue; }
            if (_line.Length < 1000) _line.Append((char)b);
        }
    }
    public void Flush()
    {
        if (_line.Length > 0) _log?.Invoke(_line.ToString());
        _line.Clear();
    }
}

/// <summary>Reads strings returned by a procedure (filter data source). An empty string means EOF.</summary>
public sealed class ProcedureSource : PSStream
{
    private readonly Interpreter _ip;
    private readonly PSObject _proc;
    private byte[] _cur = Array.Empty<byte>();
    private int _pos;
    private bool _eof;

    public ProcedureSource(Interpreter ip, PSObject proc) { _ip = ip; _proc = proc; }

    protected override int ReadCore()
    {
        while (_pos >= _cur.Length)
        {
            if (_eof) return -1;
            var s = _ip.CallForString(_proc);
            if (s is null || s.Length == 0) { _eof = true; return -1; }
            _cur = s.ToArray();
            _pos = 0;
        }
        return _cur[_pos++];
    }
}

/// <summary>Base class for decoding filters, producing output in chunks.</summary>
public abstract class FilterStream : PSStream
{
    protected readonly PSStream Src;
    private byte[] _out = Array.Empty<byte>();
    private int _outPos, _outLen;
    private bool _eod;
    protected readonly bool CloseSource;

    protected FilterStream(PSStream src, bool closeSource) { Src = src; CloseSource = closeSource; }

    /// <summary>Decode the next chunk into the list. Return false at end of data.</summary>
    protected abstract bool Fill(List<byte> output);

    private readonly List<byte> _tmp = new();

    protected override int ReadCore()
    {
        while (_outPos >= _outLen)
        {
            if (_eod) return -1;
            _tmp.Clear();
            if (!Fill(_tmp)) _eod = true;
            if (_tmp.Count > _out.Length) _out = new byte[Math.Max(_tmp.Count, 4096)];
            _tmp.CopyTo(_out);
            _outLen = _tmp.Count;
            _outPos = 0;
        }
        return _out[_outPos++];
    }

    public override void Close()
    {
        base.Close();
        if (CloseSource) Src.Close();
    }

    protected static bool IsWhite(int c) => c is 0 or 9 or 10 or 12 or 13 or 32;
}

public sealed class AsciiHexDecode : FilterStream
{
    public AsciiHexDecode(PSStream src, bool closeSource) : base(src, closeSource) { }
    protected override bool Fill(List<byte> o)
    {
        int hi = -1;
        while (o.Count < 4096)
        {
            int c = Src.ReadByte();
            if (c < 0 || c == '>') { if (hi >= 0) o.Add((byte)(hi << 4)); return false; }
            int v = HexVal(c);
            if (v < 0) continue;
            if (hi < 0) hi = v; else { o.Add((byte)((hi << 4) | v)); hi = -1; }
        }
        if (hi >= 0) Src.Unread("0123456789ABCDEF"[hi]);
        return true;
    }
    internal static int HexVal(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}

public sealed class Ascii85Decode : FilterStream
{
    public Ascii85Decode(PSStream src, bool closeSource) : base(src, closeSource) { }
    protected override bool Fill(List<byte> o)
    {
        Span<int> group = stackalloc int[5];
        int n = 0;
        while (o.Count < 4096)
        {
            int c = Src.ReadByte();
            if (c < 0 || c == '~')
            {
                if (c == '~') Src.ReadByte(); // '>'
                if (n > 1)
                {
                    for (int i = n; i < 5; i++) group[i] = 84;
                    uint v = 0;
                    for (int i = 0; i < 5; i++) v = v * 85 + (uint)group[i];
                    for (int i = 0; i < n - 1; i++) o.Add((byte)(v >> (24 - 8 * i)));
                }
                return false;
            }
            if (IsWhite(c)) continue;
            if (c == 'z' && n == 0) { o.Add(0); o.Add(0); o.Add(0); o.Add(0); continue; }
            if (c < '!' || c > 'u') continue;
            group[n++] = c - '!';
            if (n == 5)
            {
                uint v = 0;
                for (int i = 0; i < 5; i++) v = unchecked(v * 85 + (uint)group[i]);
                o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v);
                n = 0;
            }
        }
        // Put back a partial group is not possible; continue reading until it completes.
        while (n > 0)
        {
            int c = Src.ReadByte();
            if (c < 0 || c == '~')
            {
                if (c == '~') Src.ReadByte();
                if (n > 1)
                {
                    for (int i = n; i < 5; i++) group[i] = 84;
                    uint v = 0;
                    for (int i = 0; i < 5; i++) v = unchecked(v * 85 + (uint)group[i]);
                    for (int i = 0; i < n - 1; i++) o.Add((byte)(v >> (24 - 8 * i)));
                }
                return false;
            }
            if (c < '!' || c > 'u') continue;
            group[n++] = c - '!';
            if (n == 5)
            {
                uint v = 0;
                for (int i = 0; i < 5; i++) v = unchecked(v * 85 + (uint)group[i]);
                o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v);
                n = 0;
            }
        }
        return true;
    }
}

public sealed class RunLengthDecode : FilterStream
{
    public RunLengthDecode(PSStream src, bool closeSource) : base(src, closeSource) { }
    protected override bool Fill(List<byte> o)
    {
        while (o.Count < 4096)
        {
            int len = Src.ReadByte();
            if (len < 0 || len == 128) return false;
            if (len < 128)
            {
                for (int i = 0; i <= len; i++)
                {
                    int b = Src.ReadByte();
                    if (b < 0) return false;
                    o.Add((byte)b);
                }
            }
            else
            {
                int b = Src.ReadByte();
                if (b < 0) return false;
                for (int i = 0; i < 257 - len; i++) o.Add((byte)b);
            }
        }
        return true;
    }
}

public sealed class LzwDecode : FilterStream
{
    private readonly int _early;
    private readonly List<byte[]> _table = new();
    private int _codeLen = 9, _bitBuf, _bitCnt;
    private byte[]? _prev;

    public LzwDecode(PSStream src, bool closeSource, int earlyChange) : base(src, closeSource)
    {
        _early = earlyChange;
        ResetTable();
    }

    private void ResetTable()
    {
        _table.Clear();
        for (int i = 0; i < 256; i++) _table.Add(new[] { (byte)i });
        _table.Add(Array.Empty<byte>()); // 256 clear
        _table.Add(Array.Empty<byte>()); // 257 EOD
        _codeLen = 9;
        _prev = null;
    }

    private int NextCode()
    {
        while (_bitCnt < _codeLen)
        {
            int b = Src.ReadByte();
            if (b < 0) return 257;
            _bitBuf = (_bitBuf << 8) | b;
            _bitCnt += 8;
        }
        int code = (_bitBuf >> (_bitCnt - _codeLen)) & ((1 << _codeLen) - 1);
        _bitCnt -= _codeLen;
        return code;
    }

    protected override bool Fill(List<byte> o)
    {
        while (o.Count < 4096)
        {
            int code = NextCode();
            if (code == 257) return false;
            if (code == 256) { ResetTable(); continue; }
            byte[] entry;
            if (code < _table.Count) entry = _table[code];
            else if (_prev != null) { entry = new byte[_prev.Length + 1]; _prev.CopyTo(entry, 0); entry[^1] = _prev[0]; }
            else return false;
            o.AddRange(entry);
            if (_prev != null && _table.Count < 4096)
            {
                var ne = new byte[_prev.Length + 1];
                _prev.CopyTo(ne, 0);
                ne[^1] = entry[0];
                _table.Add(ne);
            }
            _prev = entry;
            int next = _table.Count + _early;
            if (next >= 4096) _codeLen = 12;
            else if (next >= 2048) _codeLen = 12;
            else if (next >= 1024) _codeLen = 11;
            else if (next >= 512) _codeLen = 10;
            else _codeLen = 9;
        }
        return true;
    }
}

/// <summary>Adapts a PSStream to a .NET Stream (for ZLibStream).</summary>
internal sealed class PSStreamAdapter : Stream
{
    private readonly PSStream _s;
    public PSStreamAdapter(PSStream s) => _s = s;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    // Read one byte at a time so the inflater never consumes bytes beyond the compressed data.
    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;
        int b = _s.ReadByte();
        if (b < 0) return 0;
        buffer[0] = (byte)b;
        return 1;
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public sealed class FlateDecode : FilterStream
{
    private readonly Stream _z;
    private readonly byte[] _buf = new byte[8192];
    public FlateDecode(PSStream src, bool closeSource) : base(src, closeSource)
        => _z = new ZLibStream(new PSStreamAdapter(src), CompressionMode.Decompress);
    protected override bool Fill(List<byte> o)
    {
        int n;
        try { n = _z.Read(_buf, 0, _buf.Length); }
        catch (InvalidDataException) { return false; }
        if (n <= 0) return false;
        for (int i = 0; i < n; i++) o.Add(_buf[i]);
        return true;
    }
}

/// <summary>Applies PNG / TIFF predictors (Predictor >= 2) on top of a decoded stream.</summary>
public sealed class PredictorDecode : FilterStream
{
    private readonly int _predictor, _colors, _bpc, _columns, _bpp, _rowLen;
    private byte[] _prevRow;
    public PredictorDecode(PSStream src, int predictor, int colors, int bpc, int columns) : base(src, true)
    {
        _predictor = predictor; _colors = colors; _bpc = bpc; _columns = columns;
        _bpp = Math.Max(1, colors * bpc / 8);
        _rowLen = (colors * bpc * columns + 7) / 8;
        _prevRow = new byte[_rowLen];
    }
    protected override bool Fill(List<byte> o)
    {
        var row = new byte[_rowLen];
        int filter = 0;
        if (_predictor >= 10) { filter = Src.ReadByte(); if (filter < 0) return false; }
        int n = Src.Read(row);
        if (n <= 0) return false;
        if (_predictor == 2)
        {
            if (_bpc == 8) for (int i = _bpp; i < _rowLen; i++) row[i] = (byte)(row[i] + row[i - _bpp]);
        }
        else
        {
            for (int i = 0; i < _rowLen; i++)
            {
                int left = i >= _bpp ? row[i - _bpp] : 0, up = _prevRow[i], ul = i >= _bpp ? _prevRow[i - _bpp] : 0;
                row[i] = filter switch
                {
                    1 => (byte)(row[i] + left),
                    2 => (byte)(row[i] + up),
                    3 => (byte)(row[i] + ((left + up) >> 1)),
                    4 => (byte)(row[i] + Paeth(left, up, ul)),
                    _ => row[i],
                };
            }
        }
        _prevRow = row;
        o.AddRange(row.AsSpan(0, n).ToArray());
        return n == _rowLen;
    }
    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}

public sealed class SubFileDecode : FilterStream
{
    private long _count;
    private readonly byte[] _eod;
    private int _match;
    private readonly List<byte> _pending = new();

    /// <summary>Empty EOD string: pass EODCount bytes (0 = until source EOF).
    /// Otherwise: data ends at occurrence number EODCount+1 of the EOD string (which is consumed).</summary>
    public SubFileDecode(PSStream src, long eodCount, byte[] eodString) : base(src, false)
    {
        _count = eodCount;
        _eod = eodString;
    }

    protected override bool Fill(List<byte> o)
    {
        if (_eod.Length == 0)
        {
            bool unlimited = _count == 0;
            while (o.Count < 4096)
            {
                int b = Src.ReadByte();
                if (b < 0) return false;
                o.Add((byte)b);
                if (!unlimited && --_count == 0) return false;
            }
            return true;
        }
        while (o.Count < 4096)
        {
            int b = Src.ReadByte();
            if (b < 0) { o.AddRange(_pending); _pending.Clear(); return false; }
            if (b == _eod[_match])
            {
                _pending.Add((byte)b);
                if (++_match == _eod.Length)
                {
                    _match = 0;
                    if (_count == 0) { _pending.Clear(); return false; }
                    _count--;
                    o.AddRange(_pending);
                    _pending.Clear();
                }
                continue;
            }
            // Mismatch: flush the pending partial match, then re-check this byte against the start.
            if (_pending.Count > 0)
            {
                // Simple restart (EOD strings in practice have no self-overlap).
                o.AddRange(_pending);
                _pending.Clear();
                _match = 0;
                if (b == _eod[0]) { _pending.Add((byte)b); _match = 1; if (_eod.Length == 1) { _match = 0; if (_count == 0) { _pending.Clear(); return false; } _count--; o.AddRange(_pending); _pending.Clear(); } continue; }
            }
            o.Add((byte)b);
        }
        return true;
    }
}
/// <summary>eexec decryption (Type 1 fonts). Detects hex or binary input from the first 4 bytes.</summary>
public sealed class EexecDecode : FilterStream
{
    private ushort _r = 55665;
    private bool _started, _hex;
    public EexecDecode(PSStream src) : base(src, false) { }

    private int RawByte()
    {
        if (!_hex) return Src.ReadByte();
        int hi = -1;
        while (true)
        {
            int c = Src.ReadByte();
            if (c < 0) return -1;
            int v = AsciiHexDecode.HexVal(c);
            if (v < 0) continue;
            if (hi < 0) hi = v; else return (hi << 4) | v;
        }
    }

    private int Decrypt(int c)
    {
        int p = c ^ (_r >> 8);
        _r = (ushort)((c + _r) * 52845 + 22719);
        return p;
    }

    protected override bool Fill(List<byte> o)
    {
        if (!_started)
        {
            _started = true;
            // Skip leading whitespace, then look at 4 bytes to detect the format.
            int c;
            do c = Src.ReadByte(); while (c is 9 or 10 or 13 or 32);
            if (c < 0) return false;
            var first = new List<int> { c };
            for (int i = 0; i < 3; i++) first.Add(Src.ReadByte());
            _hex = first.All(x => x >= 0 && AsciiHexDecode.HexVal(x) >= 0);
            if (_hex)
            {
                // Re-read the 4 hex chars as 2 bytes, then 2 more bytes for the 4-byte lead-in.
                var b0 = (AsciiHexDecode.HexVal(first[0]) << 4) | AsciiHexDecode.HexVal(first[1]);
                var b1 = (AsciiHexDecode.HexVal(first[2]) << 4) | AsciiHexDecode.HexVal(first[3]);
                Decrypt(b0); Decrypt(b1);
                for (int i = 0; i < 2; i++) { var b = RawByte(); if (b < 0) return false; Decrypt(b); }
            }
            else
            {
                foreach (var b in first) { if (b < 0) return false; Decrypt(b); }
            }
        }
        // One byte at a time: after "currentfile closefile" the rest of the job must not have been consumed.
        int x = RawByte();
        if (x < 0) return false;
        o.Add((byte)Decrypt(x));
        return true;
    }
}

/// <summary>Reads a whole JPEG from the source (up to EOI) and outputs its pixels (1, 3 or 4 components).</summary>
public sealed class DctDecode : FilterStream
{
    private bool _done, _taken;
    private byte[]? _raw;
    public int Components { get; private set; }
    public DctDecode(PSStream src, bool closeSource) : base(src, closeSource) { }

    /// <summary>Reads the compressed JPEG without decoding it (for pass-through). Null if decoding already started.</summary>
    public byte[]? TakeRawJpeg()
    {
        if (_done || _taken) return null;
        _taken = true;
        return ReadJpeg(Src);
    }

    /// <summary>After TakeRawJpeg: decode these bytes when the filter is read.</summary>
    public void UseRawJpeg(byte[] jpeg) => _raw = jpeg;

    protected override bool Fill(List<byte> o)
    {
        if (_done) return false;
        _done = true;
        if (_taken && _raw is null) return false;
        var jpeg = _raw ?? ReadJpeg(Src);
        if (jpeg.Length == 0) return false;
        int comps = JpegComponents(jpeg);
        Components = comps;
        using var codec = SKCodec.Create(new MemoryStream(jpeg));
        if (codec is null) return false;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        var res = codec.GetPixels(info, bmp.GetPixels());
        if (res is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return false;
        var px = bmp.GetPixelSpan();
        int n = info.Width * info.Height;
        o.Capacity = Math.Max(o.Capacity, n * comps);
        for (int i = 0; i < n; i++)
        {
            byte r = px[i * 4], g = px[i * 4 + 1], b = px[i * 4 + 2];
            switch (comps)
            {
                case 1: o.Add(r); break;
                case 4: o.Add((byte)(255 - r)); o.Add((byte)(255 - g)); o.Add((byte)(255 - b)); o.Add(0); break;
                default: o.Add(r); o.Add(g); o.Add(b); break;
            }
        }
        return false;
    }

    private static int JpegComponents(byte[] j)
    {
        int i = 2;
        while (i + 9 < j.Length)
        {
            if (j[i] != 0xFF) { i++; continue; }
            int m = j[i + 1];
            if (m is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC) return j[i + 9];
            if (m == 0xD8 || m == 0x01 || m is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            int len = (j[i + 2] << 8) | j[i + 3];
            i += 2 + len;
        }
        return 3;
    }

    /// <summary>Copies exactly one JPEG datastream (SOI..EOI) from the source.</summary>
    public static byte[] ReadJpeg(PSStream s)
    {
        var ms = new MemoryStream();
        int c;
        // Find SOI
        int prev = -1;
        while ((c = s.ReadByte()) >= 0)
        {
            if (prev == 0xFF && c == 0xD8) { ms.WriteByte(0xFF); ms.WriteByte(0xD8); break; }
            prev = c;
        }
        if (c < 0) return Array.Empty<byte>();
        while (true)
        {
            c = s.ReadByte();
            if (c < 0) break;
            if (c != 0xFF) { ms.WriteByte((byte)c); continue; }
            // marker
            int m;
            do m = s.ReadByte(); while (m == 0xFF);
            if (m < 0) break;
            ms.WriteByte(0xFF); ms.WriteByte((byte)m);
            if (m == 0xD9) break; // EOI
            if (m == 0x00 || m is >= 0xD0 and <= 0xD7 || m == 0x01) continue; // stuffed byte / RST / TEM
            int hi = s.ReadByte(), lo = s.ReadByte();
            if (hi < 0 || lo < 0) break;
            ms.WriteByte((byte)hi); ms.WriteByte((byte)lo);
            int len = (hi << 8) | lo;
            for (int i = 0; i < len - 2; i++)
            {
                int b = s.ReadByte();
                if (b < 0) break;
                ms.WriteByte((byte)b);
            }
            // After SOS the entropy-coded data follows; it is copied by the outer loop (0xFF00 stuffing handled above).
        }
        return ms.ToArray();
    }
}

/// <summary>Write-side filters are accepted but discard their data.</summary>
public sealed class NullWriteStream : PSStream
{
    public override bool CanWrite => true;
    protected override int ReadCore() => -1;
}
