using System.Text;

namespace PrintToImage.PostScript;

public sealed partial class Interpreter
{
    private PSFile? _stdoutFile;

    /// <summary>The file currently being executed (top-most file frame).</summary>
    internal PSFile CurrentFile()
    {
        for (int i = EStack.Count - 1; i >= 0; i--)
            if (EStack[i] is FileFrame ff) return ff.File;
        return new PSFile(new BytesSource(Array.Empty<byte>()));
    }

    private static PSStream SourceOf(PSObject o) => o switch
    {
        PSFile f => f.Stream,
        _ => throw new PSError("typecheck", "expected file"),
    };

    private void RegisterFileOperators()
    {
        _stdoutFile = new PSFile(Stdout);

        Def("currentfile", ip => ip.Push(ip.CurrentFile().WithExec(false)));
        Def("file", ip =>
        {
            var access = ip.PopString().Text;
            var name = ip.PopString().Text;
            switch (name)
            {
                case "%stdout":
                case "%stderr":
                    ip.Push(ip._stdoutFile!);
                    break;
                case "%stdin":
                    ip.Push(new PSFile(ip.JobStream ?? new BytesSource(Array.Empty<byte>())));
                    break;
                default:
                    // No disk access from print jobs.
                    throw new PSError("undefinedfilename", name);
            }
        });
        Def("closefile", ip =>
        {
            var f = ip.Pop() as PSFile ?? throw new PSError("typecheck");
            if (f.Stream == ip.JobStream) return; // never close the job itself
            f.Stream.Close();
        });
        Def("read", ip =>
        {
            var s = SourceOf(ip.Pop());
            int b = s.ReadByte();
            if (b < 0) ip.Push(false); else { ip.Push(b); ip.Push(true); }
        });
        Def("readstring", ip =>
        {
            var str = ip.PopString();
            var src = SourceOf(ip.Pop());
            ip.TouchString(str);
            int n = src.Read(str.Buf.Data.AsSpan(str.Offset, str.Length));
            ip.Push(str.Sub(0, n));
            ip.Push(n == str.Length);
        });
        Def("readhexstring", ip =>
        {
            var str = ip.PopString();
            var src = SourceOf(ip.Pop());
            ip.TouchString(str);
            int n = 0, hi = -1;
            var data = str.Buf.Data;
            while (n < str.Length)
            {
                int c = src.ReadByte();
                if (c < 0) break;
                int v = AsciiHexDecode.HexVal(c);
                if (v < 0) continue;
                if (hi < 0) hi = v;
                else { data[str.Offset + n++] = (byte)((hi << 4) | v); hi = -1; }
            }
            ip.Push(str.Sub(0, n));
            ip.Push(n == str.Length);
        });
        Def("readline", ip =>
        {
            var str = ip.PopString();
            var src = SourceOf(ip.Pop());
            ip.TouchString(str);
            int n = 0;
            bool eol = false;
            while (true)
            {
                int c = src.ReadByte();
                if (c < 0) break;
                if (c == '\n') { eol = true; break; }
                if (c == '\r') { if (src.Peek() == '\n') src.ReadByte(); eol = true; break; }
                if (n >= str.Length) throw new PSError("rangecheck");
                str.Buf.Data[str.Offset + n++] = (byte)c;
            }
            ip.Push(str.Sub(0, n));
            ip.Push(eol);
        });
        Def("bytesavailable", ip => { ip.Pop(); ip.Push(-1); });
        Def("fileposition", ip => { var f = SourceOf(ip.Pop()); ip.Push((int)Math.Min(int.MaxValue, f.BytesRead)); });
        Def("setfileposition", ip => { ip.Pop(); ip.Pop(); throw new PSError("ioerror"); });
        Def("resetfile", ip => ip.Pop());
        Def("flushfile", ip =>
        {
            var o = ip.Pop();
            if (o is PSFile f && f.Stream != ip.Stdout && f.Stream != ip.JobStream)
            {
                // Read and discard until EOF (flushfile on an input file).
                while (f.Stream.ReadByte() >= 0) { }
            }
            if (o is PSFile f2 && f2.Stream == ip.JobStream)
            {
                // Flushing the job: skip remaining data (used by some error handlers).
                while (f2.Stream.ReadByte() >= 0) { }
            }
        });
        Def("flush", ip => ip.Stdout.Flush());
        Def("write", ip => { int b = ip.PopInt(); var f = SourceOf(ip.Pop()); f.Write(new[] { (byte)b }); });
        Def("writestring", ip => { var s = ip.PopString(); var f = SourceOf(ip.Pop()); f.Write(s.Span); });
        Def("writehexstring", ip => { var s = ip.PopString(); var f = SourceOf(ip.Pop()); f.Write(Encoding.ASCII.GetBytes(Convert.ToHexString(s.Span))); });
        Def("print", ip => ip.Stdout.Write(ip.PopString().Span));
        Def("=", ip => ip.Stdout.Write(Encoding.Latin1.GetBytes(ip.Pop().ToString() + "\n")));
        Def("==", ip => ip.Stdout.Write(Encoding.Latin1.GetBytes(DebugString(ip.Pop()) + "\n")));
        Def("=only", ip => ip.Stdout.Write(Encoding.Latin1.GetBytes(ip.Pop().ToString())));
        Def("==only", ip => ip.Stdout.Write(Encoding.Latin1.GetBytes(DebugString(ip.Pop()))));
        Def("stack", ip => { for (int i = ip._osp - 1; i >= 0; i--) ip.Stdout.Write(Encoding.Latin1.GetBytes(ip._os[i] + "\n")); });
        Def("pstack", ip => { for (int i = ip._osp - 1; i >= 0; i--) ip.Stdout.Write(Encoding.Latin1.GetBytes(DebugString(ip._os[i]) + "\n")); });
        Def("printobject", ip => { ip.Pop(); ip.Pop(); });
        Def("writeobject", ip => { ip.Pop(); ip.Pop(); ip.Pop(); });
        Def("setobjectformat", ip => ip.Pop());
        Def("currentobjectformat", ip => ip.Push(0));
        Def("echo", ip => ip.Pop());
        Def("prompt", ip => { });
        Def("status", ip =>
        {
            var o = ip.Pop();
            if (o is PSFile f) ip.Push(!f.Stream.IsClosed);
            else { ip.Push(false); }
        });
        Def("run", ip => throw new PSError("undefinedfilename", ip.Pop().ToString()));
        Def("deletefile", ip => throw new PSError("undefinedfilename", ip.Pop().ToString()));
        Def("renamefile", ip => { ip.Pop(); throw new PSError("undefinedfilename", ip.Pop().ToString()); });
        Def("filenameforall", ip => { ip.Pop(); ip.Pop(); ip.Pop(); });
        Def("token", ip =>
        {
            var o = ip.Pop();
            if (o is PSString s)
            {
                var src = new BytesSource(s.Buf.Data, s.Offset, s.Length);
                var tok = ip.Scanner.Next(src);
                if (tok is null) { ip.Push(false); return; }
                int consumed = (int)src.BytesRead;
                ip.Push(s.Sub(consumed, s.Length - consumed));
                ip.Push(tok);
                ip.Push(true);
            }
            else if (o is PSFile f)
            {
                var tok = ip.Scanner.Next(f.Stream);
                if (tok is null) ip.Push(false); else { ip.Push(tok); ip.Push(true); }
            }
            else throw new PSError("typecheck");
        });
        Def("eexec", ip =>
        {
            var src = ip.Pop();
            PSStream s = src switch
            {
                PSFile f => f.Stream,
                PSString str => new BytesSource(str.ToArray()),
                _ => throw new PSError("typecheck"),
            };
            // eexec pushes systemdict on the dictionary stack and runs the decrypted text.
            ip.DStack.Add(ip.SystemDict);
            var dec = new EexecDecode(s);
            ip.EStack.Add(new ContinuationFrame(i =>
            {
                // after the decrypted section finishes, pop systemdict if still on top
                if (i.DStack.Count > 3 && i.DStack[^1] == i.SystemDict) i.DStack.RemoveAt(i.DStack.Count - 1);
                return false;
            }));
            ip.EStack.Add(new FileFrame(new PSFile(dec, true)));
        });
        Def("filter", ip => ip.Push(new PSFile(ip.MakeFilter())));
    }

    private PSStream MakeFilter()
    {
        var name = Pop() as PSName ?? throw new PSError("typecheck", "filter name");
        PSDict? param = null;
        // Optional parameters before the filter name: a dictionary and/or filter-specific operands.
        string fname = name.Text;

        PSObject PopSourceOrTarget()
        {
            var o = Pop();
            if (o is PSDict d && param is null) { param = d; o = Pop(); }
            return o;
        }

        if (fname.EndsWith("Encode", StringComparison.Ordinal))
        {
            // Encoding filters write to a target; output is discarded.
            if (Peek() is PSDict) Pop();
            Pop();
            return new NullWriteStream();
        }

        PSStream Src(PSObject o) => o switch
        {
            PSFile f => f.Stream,
            PSString s => new BytesSource(s.ToArray()),
            PSArray a when a.IsExec => new ProcedureSource(this, a),
            PSOperator op => new ProcedureSource(this, op),
            _ => throw new PSError("typecheck", "filter source"),
        };

        switch (fname)
        {
            case "SubFileDecode":
                {
                    PSObject eod = Pop();
                    long count;
                    byte[] eodString;
                    if (eod is PSDict pd)
                    {
                        count = pd.Get("EODCount") is { } c ? (long)Num(c) : 0;
                        eodString = pd.Get("EODString") is PSString es ? es.ToArray() : Array.Empty<byte>();
                        var srcObj = Pop();
                        return new SubFileDecode(Src(srcObj), count, eodString);
                    }
                    eodString = eod is PSString es2 ? es2.ToArray() : throw new PSError("typecheck");
                    count = (long)PopNum();
                    var maybeDict = Pop();
                    var source = maybeDict is PSDict ? Pop() : maybeDict;
                    return new SubFileDecode(Src(source), count, eodString);
                }
            default:
                {
                    var srcObj = PopSourceOrTarget();
                    var src = Src(srcObj);
                    bool close = param?.Get("CloseSource") is PSBool { Value: true };
                    PSStream result = fname switch
                    {
                        "ASCIIHexDecode" => new AsciiHexDecode(src, close),
                        "ASCII85Decode" => new Ascii85Decode(src, close),
                        "RunLengthDecode" => new RunLengthDecode(src, close),
                        "LZWDecode" => new LzwDecode(src, close, param?.Get("EarlyChange") is { } ec ? (int)Num(ec) : 1),
                        "FlateDecode" => new FlateDecode(src, close),
                        "DCTDecode" => new DctDecode(src, close),
                        "NullDecode" => src,
                        "ReusableStreamDecode" => ReadAllReusable(src, param),
                        "CCITTFaxDecode" => throw new PSError("undefined", "CCITTFaxDecode filter is not supported"),
                        "eexecDecode" => new EexecDecode(src),
                        _ => throw new PSError("undefined", "filter " + fname),
                    };
                    if (fname is "LZWDecode" or "FlateDecode" && param?.Get("Predictor") is { } pr && Num(pr) >= 2)
                    {
                        result = new PredictorDecode(result, (int)Num(pr),
                            param.Get("Colors") is { } col ? (int)Num(col) : 1,
                            param.Get("BitsPerComponent") is { } bpc ? (int)Num(bpc) : 8,
                            param.Get("Columns") is { } cols ? (int)Num(cols) : 1);
                    }
                    return result;
                }
        }
    }

    private PSStream ReadAllReusable(PSStream src, PSDict? param)
    {
        // Apply an optional inner filter chain given by /Filter in the parameter dictionary.
        if (param?.Get("Filter") is { } filt)
        {
            var names = filt is PSArray fa ? fa.Items.ToArray() : new[] { filt };
            foreach (var n in names)
            {
                src = (n as PSName)?.Text switch
                {
                    "ASCIIHexDecode" => new AsciiHexDecode(src, false),
                    "ASCII85Decode" => new Ascii85Decode(src, false),
                    "RunLengthDecode" => new RunLengthDecode(src, false),
                    "LZWDecode" => new LzwDecode(src, false, 1),
                    "FlateDecode" => new FlateDecode(src, false),
                    "DCTDecode" => new DctDecode(src, false),
                    _ => src,
                };
            }
        }
        var ms = new MemoryStream();
        var buf = new byte[65536];
        int n2;
        while ((n2 = src.Read(buf)) > 0) ms.Write(buf, 0, n2);
        return new BytesSource(ms.ToArray(), reusable: true);
    }

    internal static string DebugString(PSObject o, int depth = 0) => o switch
    {
        PSString s => "(" + s.Text + ")",
        PSName n => n.IsExec ? n.Text : "/" + n.Text,
        PSArray a when depth < 3 => (a.IsExec ? "{" : "[") + string.Join(" ", a.Items.ToArray().Take(50).Select(x => DebugString(x, depth + 1))) + (a.IsExec ? "}" : "]"),
        PSArray a => a.IsExec ? "{...}" : "[...]",
        PSDict => "-dict-",
        _ => o.ToString(),
    };
}
