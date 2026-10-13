using System.Globalization;
using System.Text;

namespace PrintToImage.PostScript;

public sealed partial class Interpreter
{
    private void Def(string name, Action<Interpreter> action) => SystemDict.PutRaw(name, new PSOperator(name, action));

    private void RegisterOperators()
    {
        RegisterCoreOperators();
        RegisterFileOperators();
        RegisterVmOperators();
        RegisterGraphicsOperators();
        RegisterPathOperators();
        RegisterPaintOperators();
        RegisterFontOperators();
        RegisterResourceOperators();
    }

    private void RegisterCoreOperators()
    {
        // ---------- stack ----------
        Def("pop", ip => ip.Pop());
        Def("exch", ip => { ip.Need(2); var a = ip.Pop(); var b = ip.Pop(); ip.Push(a); ip.Push(b); });
        Def("dup", ip => ip.Push(ip.Peek()));
        Def("copy", ip =>
        {
            var top = ip.Peek();
            if (top is PSInt n)
            {
                ip.Pop();
                if (n.Value < 0) throw new PSError("rangecheck");
                ip.Need(n.Value);
                int start = ip._osp - n.Value;
                for (int i = 0; i < n.Value; i++) ip.Push(ip._os[start + i]);
                return;
            }
            var dst = ip.Pop();
            var src = ip.Pop();
            ip.Push(CopyComposite(ip, src, dst));
        });
        Def("index", ip => { int n = ip.PopInt(); if (n < 0) throw new PSError("rangecheck"); ip.Push(ip.Peek(n)); });
        Def("roll", ip =>
        {
            int j = ip.PopInt(), n = ip.PopInt();
            if (n < 0) throw new PSError("rangecheck");
            ip.Need(n);
            if (n == 0) return;
            j %= n;
            if (j < 0) j += n;
            if (j == 0) return;
            int start = ip._osp - n;
            var tmp = new PSObject[n];
            for (int i = 0; i < n; i++) tmp[(i + j) % n] = ip._os[start + i];
            Array.Copy(tmp, 0, ip._os, start, n);
        });
        Def("clear", ip => ip._osp = 0);
        Def("count", ip => ip.Push(ip._osp));
        Def("mark", ip => ip.Push(PSMark.Instance));
        Def("[", ip => ip.Push(PSMark.Instance));
        Def("<<", ip => ip.Push(PSMark.Instance));
        Def("cleartomark", ip => { int n = ip.CountToMark(); ip._osp -= n + 1; });
        Def("counttomark", ip => ip.Push(ip.CountToMark()));
        Def("]", ip =>
        {
            int n = ip.CountToMark();
            var items = new PSObject[n];
            Array.Copy(ip._os, ip._osp - n, items, 0, n);
            ip._osp -= n + 1;
            ip.Push(new PSArray(items, ip.AllocLevel));
        });
        Def(">>", ip =>
        {
            int n = ip.CountToMark();
            if (n % 2 != 0) throw new PSError("rangecheck", "odd number of items in dictionary");
            var d = ip.NewDict(n / 2);
            int start = ip._osp - n;
            for (int i = 0; i < n; i += 2)
            {
                var k = ip._os[start + i];
                if (k is PSNull) throw new PSError("typecheck");
                d.Map[PSDict.Key(k)] = ip._os[start + i + 1];
            }
            d.MaxLength = Math.Max(d.MaxLength, d.Count);
            ip._osp -= n + 1;
            ip.Push(d);
        });

        // ---------- arithmetic ----------
        Def("add", ip => ip.Arith((a, b) => (long)a + b, (a, b) => a + b));
        Def("sub", ip => ip.Arith((a, b) => (long)a - b, (a, b) => a - b));
        Def("mul", ip => ip.Arith((a, b) => (long)a * b, (a, b) => a * b));
        Def("div", ip =>
        {
            double b = ip.PopNum(), a = ip.PopNum();
            if (b == 0) throw new PSError("undefinedresult");
            ip.Push(a / b);
        });
        Def("idiv", ip =>
        {
            int b = ip.PopIntStrict(), a = ip.PopIntStrict();
            if (b == 0) throw new PSError("undefinedresult");
            ip.Push(a == int.MinValue && b == -1 ? int.MinValue : a / b);
        });
        Def("mod", ip =>
        {
            int b = ip.PopIntStrict(), a = ip.PopIntStrict();
            if (b == 0) throw new PSError("undefinedresult");
            ip.Push(b == -1 ? 0 : a % b);
        });
        Def("abs", ip => { var o = ip.Pop(); if (o is PSInt i && i.Value != int.MinValue) ip.Push(Math.Abs(i.Value)); else ip.Push(Math.Abs(Num(o))); });
        Def("neg", ip => { var o = ip.Pop(); if (o is PSInt i && i.Value != int.MinValue) ip.Push(-i.Value); else ip.Push(-Num(o)); });
        Def("ceiling", ip => ip.RoundOp(Math.Ceiling));
        Def("floor", ip => ip.RoundOp(Math.Floor));
        Def("round", ip => ip.RoundOp(v => Math.Floor(v + 0.5)));
        Def("truncate", ip => ip.RoundOp(Math.Truncate));
        Def("sqrt", ip => { double v = ip.PopNum(); if (v < 0) throw new PSError("rangecheck"); ip.Push(Math.Sqrt(v)); });
        Def("atan", ip =>
        {
            double den = ip.PopNum(), num = ip.PopNum();
            if (num == 0 && den == 0) throw new PSError("undefinedresult");
            double a = Math.Atan2(num, den) * 180 / Math.PI;
            if (a < 0) a += 360;
            ip.Push(a);
        });
        Def("cos", ip => ip.Push(Math.Cos(ip.PopNum() * Math.PI / 180)));
        Def("sin", ip => ip.Push(Math.Sin(ip.PopNum() * Math.PI / 180)));
        Def("exp", ip => { double e = ip.PopNum(), b = ip.PopNum(); ip.Push(Math.Pow(b, e)); });
        Def("ln", ip => { double v = ip.PopNum(); if (v <= 0) throw new PSError("rangecheck"); ip.Push(Math.Log(v)); });
        Def("log", ip => { double v = ip.PopNum(); if (v <= 0) throw new PSError("rangecheck"); ip.Push(Math.Log10(v)); });
        Def("rand", ip => { ip._rand = unchecked(ip._rand * 1103515245 + 12345) & 0x7FFFFFFF; ip.Push(ip._rand); });
        Def("srand", ip => ip._rand = ip.PopInt());
        Def("rrand", ip => ip.Push(ip._rand));
        Def("cvi", ip =>
        {
            var o = ip.Pop();
            double v = o switch
            {
                PSInt i => i.Value,
                PSReal r => r.Value,
                PSString s => Scanner.ParseNumber(s.Text.Trim()) is { } n ? Num(n) : throw new PSError("syntaxerror"),
                _ => throw new PSError("typecheck"),
            };
            v = Math.Truncate(v);
            if (v > int.MaxValue || v < int.MinValue || double.IsNaN(v)) throw new PSError("rangecheck");
            ip.Push((int)v);
        });
        Def("cvr", ip =>
        {
            var o = ip.Pop();
            ip.Push(o switch
            {
                PSInt i => i.Value,
                PSReal r => r.Value,
                PSString s => Scanner.ParseNumber(s.Text.Trim()) is { } n ? Num(n) : throw new PSError("syntaxerror"),
                _ => throw new PSError("typecheck"),
            });
        });

        // ---------- relational / boolean ----------
        Def("eq", ip => { var b = ip.Pop(); var a = ip.Pop(); ip.Push(PSEquals(a, b)); });
        Def("ne", ip => { var b = ip.Pop(); var a = ip.Pop(); ip.Push(!PSEquals(a, b)); });
        Def("gt", ip => ip.Push(ip.Compare() > 0));
        Def("ge", ip => ip.Push(ip.Compare() >= 0));
        Def("lt", ip => ip.Push(ip.Compare() < 0));
        Def("le", ip => ip.Push(ip.Compare() <= 0));
        Def("and", ip => ip.Logic((a, b) => a & b, (a, b) => a && b));
        Def("or", ip => ip.Logic((a, b) => a | b, (a, b) => a || b));
        Def("xor", ip => ip.Logic((a, b) => a ^ b, (a, b) => a ^ b));
        Def("not", ip =>
        {
            var o = ip.Pop();
            if (o is PSBool b) ip.Push(!b.Value);
            else if (o is PSInt i) ip.Push(~i.Value);
            else throw new PSError("typecheck");
        });
        Def("bitshift", ip =>
        {
            int shift = ip.PopInt(), v = ip.PopIntStrict();
            ip.Push(shift >= 32 || shift <= -32 ? 0 : shift >= 0 ? v << shift : (int)((uint)v >> -shift));
        });
        SystemDict.PutRaw("true", PSBool.True);
        SystemDict.PutRaw("false", PSBool.False);
        SystemDict.PutRaw("null", PSNull.Instance);

        // ---------- composite objects ----------
        Def("array", ip =>
        {
            int n = ip.PopInt();
            if (n < 0 || n > 10_000_000) throw new PSError("rangecheck");
            var items = new PSObject[n];
            Array.Fill(items, PSNull.Instance);
            ip.Push(new PSArray(items, ip.AllocLevel));
        });
        Def("packedarray", ip =>
        {
            int n = ip.PopInt();
            ip.Need(n);
            var items = new PSObject[n];
            Array.Copy(ip._os, ip._osp - n, items, 0, n);
            ip._osp -= n;
            ip.Push(new PSArray(items, ip.AllocLevel) { Packed = true, Access = Access.ReadOnly });
        });
        Def("setpacking", ip => ip.PopBool());
        Def("currentpacking", ip => ip.Push(false));
        Def("dict", ip =>
        {
            int n = ip.PopInt();
            if (n < 0) throw new PSError("rangecheck");
            ip.Push(ip.NewDict(Math.Min(n, 100_000)));
        });
        Def("string", ip =>
        {
            int n = ip.PopInt();
            if (n < 0 || n > 64_000_000) throw new PSError("rangecheck");
            ip.Push(new PSString(new byte[n], ip.AllocLevel));
        });
        Def("length", ip =>
        {
            var o = ip.Pop();
            ip.Push(o switch
            {
                PSArray a => a.Length,
                PSString s => s.Length,
                PSDict d => d.Count,
                PSName n => Encoding.Latin1.GetByteCount(n.Text),
                _ => throw new PSError("typecheck"),
            });
        });
        Def("maxlength", ip => ip.Push(ip.PopDict().MaxLength));
        Def("begin", ip =>
        {
            var d = ip.PopDict();
            if (ip.DStack.Count > 10_000) throw new PSError("dictstackoverflow");
            ip.DStack.Add(d);
        });
        Def("end", ip =>
        {
            if (ip.DStack.Count <= 3) throw new PSError("dictstackunderflow");
            ip.DStack.RemoveAt(ip.DStack.Count - 1);
        });
        Def("def", ip =>
        {
            var v = ip.Pop();
            var k = ip.Pop();
            ip.DictPut(ip.CurrentDict, PSDict.Key(k), v);
        });
        Def("load", ip =>
        {
            var k = ip.Pop();
            var key = PSDict.Key(k);
            var d = ip.Where(key) ?? throw new PSError("undefined", k.ToString());
            ip.Push(d.Map[key]);
        });
        Def("store", ip =>
        {
            var v = ip.Pop();
            var key = PSDict.Key(ip.Pop());
            ip.DictPut(ip.Where(key) ?? ip.CurrentDict, key, v);
        });
        Def("get", ip =>
        {
            var k = ip.Pop();
            var c = ip.Pop();
            switch (c)
            {
                case PSArray a:
                    {
                        int i = ToInt(k);
                        if (i < 0 || i >= a.Length) throw new PSError("rangecheck");
                        ip.Push(a[i]);
                        break;
                    }
                case PSString s:
                    {
                        int i = ToInt(k);
                        if (i < 0 || i >= s.Length) throw new PSError("rangecheck");
                        ip.Push((int)s[i]);
                        break;
                    }
                case PSDict d:
                    if (!d.Map.TryGetValue(PSDict.Key(k), out var v)) throw new PSError("undefined", k.ToString());
                    ip.Push(v);
                    break;
                default: throw new PSError("typecheck");
            }
        });
        Def("put", ip =>
        {
            var v = ip.Pop();
            var k = ip.Pop();
            var c = ip.Pop();
            switch (c)
            {
                case PSArray a:
                    if (a.Access != Access.Unlimited) throw new PSError("invalidaccess");
                    ip.ArrayPut(a, ToInt(k), v);
                    break;
                case PSString s:
                    {
                        int i = ToInt(k);
                        if (i < 0 || i >= s.Length) throw new PSError("rangecheck");
                        ip.TouchString(s);
                        s.Buf.Data[s.Offset + i] = (byte)ToInt(v);
                        break;
                    }
                case PSDict d:
                    ip.DictPut(d, PSDict.Key(k), v);
                    break;
                default: throw new PSError("typecheck");
            }
        });
        Def("known", ip => { var k = ip.Pop(); var d = ip.PopDict(); ip.Push(d.Map.ContainsKey(PSDict.Key(k))); });
        Def("undef", ip => { var k = ip.Pop(); var d = ip.PopDict(); ip.DictRemove(d, PSDict.Key(k)); });
        Def("where", ip =>
        {
            var key = PSDict.Key(ip.Pop());
            var d = ip.Where(key);
            if (d is null) ip.Push(false); else { ip.Push(d); ip.Push(true); }
        });
        Def("getinterval", ip =>
        {
            int count = ip.PopInt(), index = ip.PopInt();
            var c = ip.Pop();
            switch (c)
            {
                case PSArray a:
                    if (index < 0 || count < 0 || index + count > a.Length) throw new PSError("rangecheck");
                    ip.Push(a.Sub(index, count));
                    break;
                case PSString s:
                    if (index < 0 || count < 0 || index + count > s.Length) throw new PSError("rangecheck");
                    ip.Push(s.Sub(index, count));
                    break;
                default: throw new PSError("typecheck");
            }
        });
        Def("putinterval", ip =>
        {
            var src = ip.Pop();
            int index = ip.PopInt();
            var dst = ip.Pop();
            switch (dst, src)
            {
                case (PSArray da, PSArray sa):
                    if (index < 0 || index + sa.Length > da.Length) throw new PSError("rangecheck");
                    var items = sa.Items.ToArray();
                    for (int i = 0; i < items.Length; i++) ip.ArrayPut(da, index + i, items[i]);
                    break;
                case (PSString ds, PSString ss):
                    if (index < 0 || index + ss.Length > ds.Length) throw new PSError("rangecheck");
                    ip.TouchString(ds);
                    ss.Span.ToArray().CopyTo(ds.Buf.Data, ds.Offset + index);
                    break;
                default: throw new PSError("typecheck");
            }
        });
        Def("aload", ip =>
        {
            var a = ip.PopArray();
            foreach (var o in a.Items) ip.Push(o);
            ip.Push(a);
        });
        Def("astore", ip =>
        {
            var a = ip.PopArray();
            ip.Need(a.Length);
            for (int i = a.Length - 1; i >= 0; i--) ip.ArrayPut(a, i, ip.Pop());
            ip.Push(a);
        });
        Def("forall", ip =>
        {
            var proc = ip.PopProc();
            var c = ip.Pop();
            ip.EStack.Add(c switch
            {
                PSArray a => new ForallArrayFrame(a, proc),
                PSString s => new ForallStringFrame(s, proc),
                PSDict d => new ForallDictFrame(d, proc),
                _ => throw new PSError("typecheck"),
            });
        });
        Def("currentdict", ip => ip.Push(ip.CurrentDict));
        Def("countdictstack", ip => ip.Push(ip.DStack.Count));
        Def("dictstack", ip =>
        {
            var a = ip.PopArray();
            if (a.Length < ip.DStack.Count) throw new PSError("rangecheck");
            for (int i = 0; i < ip.DStack.Count; i++) ip.ArrayPut(a, i, ip.DStack[i]);
            ip.Push(a.Sub(0, ip.DStack.Count));
        });
        Def("cleardictstack", ip => ip.DStack.RemoveRange(3, ip.DStack.Count - 3));
        Def("anchorsearch", ip =>
        {
            var seek = ip.PopString();
            var s = ip.PopString();
            if (s.Length >= seek.Length && s.Span[..seek.Length].SequenceEqual(seek.Span))
            {
                ip.Push(s.Sub(seek.Length, s.Length - seek.Length));
                ip.Push(s.Sub(0, seek.Length));
                ip.Push(true);
            }
            else { ip.Push(s); ip.Push(false); }
        });
        Def("search", ip =>
        {
            var seek = ip.PopString();
            var s = ip.PopString();
            int idx = seek.Length == 0 ? 0 : s.Span.IndexOf(seek.Span);
            if (idx >= 0)
            {
                ip.Push(s.Sub(idx + seek.Length, s.Length - idx - seek.Length));
                ip.Push(s.Sub(idx, seek.Length));
                ip.Push(s.Sub(0, idx));
                ip.Push(true);
            }
            else { ip.Push(s); ip.Push(false); }
        });

        // ---------- control ----------
        Def("exec", ip => ip.ExecOperand(ip.Pop()));
        Def("if", ip =>
        {
            var proc = ip.PopProc();
            if (ip.PopBool()) ip.ExecOperand(proc);
        });
        Def("ifelse", ip =>
        {
            var p2 = ip.PopProc();
            var p1 = ip.PopProc();
            ip.ExecOperand(ip.PopBool() ? p1 : p2);
        });
        Def("for", ip =>
        {
            var proc = ip.PopProc();
            var limit = ip.Pop(); var inc = ip.Pop(); var init = ip.Pop();
            if (!IsNum(limit) || !IsNum(inc) || !IsNum(init)) throw new PSError("typecheck");
            ip.EStack.Add(new ForFrame(init, inc, limit, proc));
        });
        Def("repeat", ip =>
        {
            var proc = ip.PopProc();
            int n = ip.PopInt();
            if (n < 0) throw new PSError("rangecheck");
            ip.EStack.Add(new RepeatFrame(n, proc));
        });
        Def("loop", ip => ip.EStack.Add(new InfiniteLoopFrame(ip.PopProc())));
        Def("exit", ip => ip.DoExit());
        Def("stop", ip => throw new StopSignal());
        Def("stopped", ip =>
        {
            var proc = ip.Pop();
            ip.EStack.Add(new StoppedFrame());
            ip.ExecOperand(proc);
        });
        Def("countexecstack", ip => ip.Push(ip.EStack.Count));
        Def("execstack", ip =>
        {
            var a = ip.PopArray();
            ip.Push(a.Sub(0, 0));
        });
        Def("quit", ip => throw new PSQuit());
        Def("start", ip => { });
        Def("bind", ip =>
        {
            var p = ip.Peek();
            if (p is PSArray a) ip.Bind(a, 0);
        });
        Def("exechistorystack", ip => { });

        // ---------- types ----------
        Def("type", ip => ip.Push(PSName.Exe(ip.Pop().TypeName)));
        Def("cvlit", ip =>
        {
            var o = ip.Pop();
            ip.Push(o switch
            {
                PSArray a => a.WithExec(false),
                PSString s => s.WithExec(false),
                PSName n => n.Atom.Literal,
                PSFile f => f.WithExec(false),
                _ => o,
            });
        });
        Def("cvx", ip =>
        {
            var o = ip.Pop();
            ip.Push(o switch
            {
                PSArray a => a.WithExec(true),
                PSString s => s.WithExec(true),
                PSName n => n.Atom.Executable,
                PSFile f => f.WithExec(true),
                _ => o,
            });
        });
        Def("xcheck", ip => ip.Push(ip.Pop().IsExec));
        Def("executeonly", ip => SetAccess(ip.Peek(), Access.ExecuteOnly));
        Def("noaccess", ip => SetAccess(ip.Peek(), Access.None));
        Def("readonly", ip => SetAccess(ip.Peek(), Access.ReadOnly));
        Def("rcheck", ip => ip.Push(GetAccess(ip.Pop()) is Access.Unlimited or Access.ReadOnly));
        Def("wcheck", ip => ip.Push(GetAccess(ip.Pop()) == Access.Unlimited));
        Def("cvn", ip =>
        {
            var s = ip.PopString();
            ip.Push(s.IsExec ? PSName.Exe(s.Text) : PSName.Lit(s.Text));
        });
        Def("cvs", ip =>
        {
            var dst = ip.PopString();
            var o = ip.Pop();
            var text = o switch
            {
                PSString s => s.Text,
                PSName n => n.Text,
                PSInt or PSReal or PSBool => o.ToString(),
                PSOperator op => op.Name,
                _ => "--nostringval--",
            };
            var bytes = Encoding.Latin1.GetBytes(text);
            if (bytes.Length > dst.Length) throw new PSError("rangecheck");
            ip.TouchString(dst);
            bytes.CopyTo(dst.Buf.Data, dst.Offset);
            ip.Push(dst.Sub(0, bytes.Length));
        });
        Def("cvrs", ip =>
        {
            var dst = ip.PopString();
            int radix = ip.PopInt();
            var n = ip.Pop();
            string text;
            if (radix == 10) text = n is PSInt ? n.ToString() : Num(n).ToString(CultureInfo.InvariantCulture);
            else
            {
                long v = (uint)(n is PSInt i ? i.Value : (int)Num(n));
                text = v == 0 ? "0" : "";
                while (v > 0) { text = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"[(int)(v % radix)] + text; v /= radix; }
            }
            var bytes = Encoding.Latin1.GetBytes(text);
            if (bytes.Length > dst.Length) throw new PSError("rangecheck");
            ip.TouchString(dst);
            bytes.CopyTo(dst.Buf.Data, dst.Offset);
            ip.Push(dst.Sub(0, bytes.Length));
        });
    }

    private int _rand = 1;

    private int PopIntStrict()
    {
        var o = Pop();
        return o is PSInt i ? i.Value : throw new PSError("typecheck");
    }

    private static int ToInt(PSObject o) => o switch
    {
        PSInt i => i.Value,
        PSReal r when r.Value == Math.Floor(r.Value) => (int)r.Value,
        _ => throw new PSError("typecheck"),
    };

    private void Arith(Func<int, int, long> intOp, Func<double, double, double> realOp)
    {
        var b = Pop();
        var a = Pop();
        if (a is PSInt ia && b is PSInt ib)
        {
            long r = intOp(ia.Value, ib.Value);
            if (r >= int.MinValue && r <= int.MaxValue) { Push((int)r); return; }
            Push((double)r);
            return;
        }
        Push(realOp(Num(a), Num(b)));
    }

    private void RoundOp(Func<double, double> f)
    {
        var o = Pop();
        if (o is PSInt) Push(o);
        else Push(f(Num(o)));
    }

    private int Compare()
    {
        var b = Pop();
        var a = Pop();
        if (IsNum(a) && IsNum(b)) return Num(a).CompareTo(Num(b));
        if (a is PSString sa && b is PSString sb) return sa.Span.SequenceCompareTo(sb.Span);
        throw new PSError("typecheck");
    }

    private void Logic(Func<int, int, int> intOp, Func<bool, bool, bool> boolOp)
    {
        var b = Pop();
        var a = Pop();
        if (a is PSBool ba && b is PSBool bb) Push(boolOp(ba.Value, bb.Value));
        else if (a is PSInt ia && b is PSInt ib) Push(intOp(ia.Value, ib.Value));
        else throw new PSError("typecheck");
    }

    public static bool PSEquals(PSObject a, PSObject b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (IsNum(a) && IsNum(b)) return Num(a) == Num(b);
        switch (a)
        {
            case PSString sa when b is PSString sb: return sa.Span.SequenceEqual(sb.Span);
            case PSString s when b is PSName n: return s.Text == n.Text;
            case PSName n when b is PSString s: return s.Text == n.Text;
            case PSName na when b is PSName nb: return na.Atom == nb.Atom;
            case PSBool ba when b is PSBool bb: return ba.Value == bb.Value;
            case PSNull when b is PSNull: return true;
            case PSMark when b is PSMark: return true;
            case PSArray aa when b is PSArray ab: return aa.Buf == ab.Buf && aa.Offset == ab.Offset && aa.Length == ab.Length;
            case PSOperator oa when b is PSOperator ob: return oa.Name == ob.Name;
            case PSFile fa when b is PSFile fb: return fa.Stream == fb.Stream;
            case PSFontId when b is PSFontId: return true;
        }
        return false;
    }

    private static void SetAccess(PSObject o, Access access)
    {
        switch (o)
        {
            case PSArray a: a.Access = access; break;
            case PSString s: s.Access = access; break;
            case PSDict d when access != Access.ExecuteOnly: d.Access = access; break;
        }
    }

    private static Access GetAccess(PSObject o) => o switch
    {
        PSArray a => a.Access,
        PSString s => s.Access,
        PSDict d => d.Access,
        PSFile => Access.Unlimited,
        _ => throw new PSError("typecheck"),
    };

    private static PSObject CopyComposite(Interpreter ip, PSObject src, PSObject dst)
    {
        switch (src, dst)
        {
            case (PSArray sa, PSArray da):
                {
                    if (sa.Length > da.Length) throw new PSError("rangecheck");
                    var items = sa.Items.ToArray();
                    for (int i = 0; i < items.Length; i++) ip.ArrayPut(da, i, items[i]);
                    return da.Sub(0, sa.Length);
                }
            case (PSString ss, PSString ds):
                {
                    if (ss.Length > ds.Length) throw new PSError("rangecheck");
                    ip.TouchString(ds);
                    ss.Span.ToArray().CopyTo(ds.Buf.Data, ds.Offset);
                    return ds.Sub(0, ss.Length);
                }
            case (PSDict sd, PSDict dd):
                foreach (var kv in sd.Map.ToList()) ip.DictPut(dd, kv.Key, kv.Value);
                return dd;
            case (PSGState sg, PSGState dg):
                dg.State = sg.State.Clone();
                return dg;
            default: throw new PSError("typecheck");
        }
    }

    /// <summary>Replaces executable names that refer to operators with the operators themselves (recursively).</summary>
    private void Bind(PSArray a, int depth)
    {
        if (depth > 100 || a.Access != Access.Unlimited) return;
        for (int i = 0; i < a.Length; i++)
        {
            var o = a[i];
            if (o is PSName n && n.IsExec)
            {
                var v = Lookup(n.Atom);
                if (v is PSOperator) a.Buf.Items[a.Offset + i] = v;
            }
            else if (o is PSArray sub && sub.IsExec)
            {
                Bind(sub, depth + 1);
                if (sub.Access == Access.Unlimited) sub.Access = Access.ReadOnly;
            }
        }
    }

    // ---------- loop frames ----------

    private sealed class ForFrame : LoopFrame
    {
        private readonly bool _int;
        private double _cur;
        private readonly double _inc, _limit;
        private int _icur;
        private readonly int _iinc;
        private readonly PSObject _proc;
        public ForFrame(PSObject init, PSObject inc, PSObject limit, PSObject proc)
        {
            _proc = proc;
            _int = init is PSInt && inc is PSInt;
            if (_int) { _icur = ((PSInt)init).Value; _iinc = ((PSInt)inc).Value; _limit = Num(limit); }
            else { _cur = Num(init); _inc = Num(inc); _limit = Num(limit); }
        }
        public override bool Step(Interpreter ip)
        {
            if (_int)
            {
                if (_iinc > 0 ? _icur > _limit : _icur < _limit) return false;
                ip.Push(_icur);
                _icur += _iinc;
            }
            else
            {
                if (_inc > 0 ? _cur > _limit : _cur < _limit) return false;
                ip.Push(_cur);
                _cur += _inc;
            }
            ip.ExecOperand(_proc);
            return true;
        }
    }

    private sealed class RepeatFrame : LoopFrame
    {
        private int _n;
        private readonly PSObject _proc;
        public RepeatFrame(int n, PSObject proc) { _n = n; _proc = proc; }
        public override bool Step(Interpreter ip)
        {
            if (_n-- <= 0) return false;
            ip.ExecOperand(_proc);
            return true;
        }
    }

    private sealed class InfiniteLoopFrame : LoopFrame
    {
        private readonly PSObject _proc;
        public InfiniteLoopFrame(PSObject proc) => _proc = proc;
        public override bool Step(Interpreter ip)
        {
            ip.ExecOperand(_proc);
            return true;
        }
    }

    private sealed class ForallArrayFrame : LoopFrame
    {
        private readonly PSArray _a; private readonly PSObject _proc; private int _i;
        public ForallArrayFrame(PSArray a, PSObject proc) { _a = a; _proc = proc; }
        public override bool Step(Interpreter ip)
        {
            if (_i >= _a.Length) return false;
            ip.Push(_a[_i++]);
            ip.ExecOperand(_proc);
            return true;
        }
    }

    private sealed class ForallStringFrame : LoopFrame
    {
        private readonly PSString _s; private readonly PSObject _proc; private int _i;
        public ForallStringFrame(PSString s, PSObject proc) { _s = s; _proc = proc; }
        public override bool Step(Interpreter ip)
        {
            if (_i >= _s.Length) return false;
            ip.Push((int)_s[_i++]);
            ip.ExecOperand(_proc);
            return true;
        }
    }

    private sealed class ForallDictFrame : LoopFrame
    {
        private readonly KeyValuePair<object, PSObject>[] _items; private readonly PSObject _proc; private int _i;
        public ForallDictFrame(PSDict d, PSObject proc) { _items = d.Map.ToArray(); _proc = proc; }
        public override bool Step(Interpreter ip)
        {
            if (_i >= _items.Length) return false;
            var kv = _items[_i++];
            ip.Push(PSDict.KeyObject(kv.Key));
            ip.Push(kv.Value);
            ip.ExecOperand(_proc);
            return true;
        }
    }
}
