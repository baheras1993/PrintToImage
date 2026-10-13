using PrintToImage.PostScript.Graphics;

namespace PrintToImage.PostScript;

/// <summary>Raised when a job hits an error that no "stopped" context handles.</summary>
public sealed class PSJobError : Exception
{
    public PSJobError(string message) : base(message) { }
}

internal abstract class Frame { }

internal sealed class ProcFrame : Frame
{
    public PSArray Proc;
    public int Pos;
    public ProcFrame(PSArray p) { Proc = p; }
}

internal sealed class FileFrame : Frame
{
    public PSFile File;
    public FileFrame(PSFile f) => File = f;
}

internal sealed class StoppedFrame : Frame { }

/// <summary>Loop contexts. Step returns false when the loop is finished.</summary>
internal abstract class LoopFrame : Frame
{
    public abstract bool Step(Interpreter ip);
}

/// <summary>Callback run when the frame is reached again (used for operators that resume after a procedure).</summary>
internal sealed class ContinuationFrame : Frame
{
    public Func<Interpreter, bool> Next; // return true to stay on the stack
    public ContinuationFrame(Func<Interpreter, bool> next) => Next = next;
}

internal sealed class StopSignal : Exception { }

public sealed partial class Interpreter
{
    // ---- Stacks ----
    private PSObject[] _os = new PSObject[1024];
    private int _osp;
    internal readonly List<Frame> EStack = new();
    internal readonly List<PSDict> DStack = new();
    internal readonly List<GState> GStack = new();
    public GState GS = new();

    // ---- Standard dictionaries ----
    public readonly PSDict SystemDict, GlobalDict, UserDict, ErrorDict, StatusDict, DollarError, UserParams;
    internal readonly Scanner Scanner;
    private readonly Dictionary<string, PSOperator> _defaultErrorHandlers = new();

    // ---- VM ----
    public int SaveLevel { get; private set; }
    public bool GlobalAlloc { get; set; }
    /// <summary>Save level stamped on new composite objects (-1 = global VM, never restored).</summary>
    public int AllocLevel => GlobalAlloc ? -1 : SaveLevel;
    private readonly List<SaveRecord> _saves = new();

    // ---- Job ----
    private readonly CancellationToken _cancel;
    private long _steps;
    public readonly Action<string> Log;
    internal PSStream? JobStream;
    internal readonly StdoutSink Stdout;
    private PSObject? _currentCommand;
    public readonly Device.PageDevice Device;
    public int OperandLimit = 500_000;
    public int ExecLimit = 100_000;

    private sealed class SaveRecord
    {
        public int Level;
        public readonly List<Action> Undo = new();
        public readonly HashSet<(PSDict, object)> DictKeys = new();
        public bool GlobalAllocAtSave;
        public int GStackDepth;
    }

    public Interpreter(Device.PageDevice device, Action<string>? log = null, CancellationToken cancel = default)
    {
        Device = device;
        Log = log ?? (_ => { });
        _cancel = cancel;
        Stdout = new StdoutSink(s => Log("PS output: " + s));
        Scanner = new Scanner(this);

        SystemDict = new PSDict(600, -1);
        GlobalDict = new PSDict(64, -1);
        UserDict = new PSDict(256, 0);
        ErrorDict = new PSDict(32, -1);
        StatusDict = new PSDict(64, -1);
        DollarError = new PSDict(16, -1);
        UserParams = new PSDict(16, -1);

        DStack.Add(SystemDict);
        DStack.Add(GlobalDict);
        DStack.Add(UserDict);

        RegisterOperators();
        InitDictionaries();
        Device.Attach(this);
        InitGraphics();
    }

    // ================= Operand stack =================

    public int Count => _osp;

    public void Push(PSObject o)
    {
        if (_osp == _os.Length)
        {
            if (_os.Length >= OperandLimit) throw new PSError("stackoverflow");
            Array.Resize(ref _os, _os.Length * 2);
        }
        _os[_osp++] = o;
    }

    public void Push(int v) => Push(PSInt.Get(v));
    public void Push(double v) => Push(new PSReal(v));
    public void Push(bool v) => Push(PSBool.Get(v));

    /// <summary>Pushes an integer if the value is integral and fits, else a real (for results that may be either).</summary>
    public void PushNumber(double v)
    {
        if (v == Math.Floor(v) && Math.Abs(v) <= int.MaxValue) Push((int)v); else Push(v);
    }

    public PSObject Pop()
    {
        if (_osp == 0) throw new PSError("stackunderflow");
        return _os[--_osp]; // slot is intentionally left in place so operands can be restored after an error
    }

    public PSObject Peek(int depth = 0)
    {
        if (depth >= _osp) throw new PSError("stackunderflow");
        return _os[_osp - 1 - depth];
    }

    internal void SetPeek(int depth, PSObject o) => _os[_osp - 1 - depth] = o;

    public void Need(int n)
    {
        if (_osp < n) throw new PSError("stackunderflow");
    }

    internal void ClearStack() => _osp = 0;
    internal void SetCount(int n) => _osp = n;
    internal PSObject StackItem(int indexFromBottom) => _os[indexFromBottom];

    public int CountToMark()
    {
        for (int i = _osp - 1; i >= 0; i--)
            if (_os[i] is PSMark) return _osp - 1 - i;
        throw new PSError("unmatchedmark");
    }

    public int PopInt()
    {
        var o = Pop();
        return o switch
        {
            PSInt i => i.Value,
            PSReal r when r.Value == Math.Floor(r.Value) && Math.Abs(r.Value) <= int.MaxValue => (int)r.Value,
            _ => throw new PSError("typecheck", "expected integer, got " + o.TypeName),
        };
    }

    public double PopNum()
    {
        var o = Pop();
        return o switch
        {
            PSInt i => i.Value,
            PSReal r => r.Value,
            _ => throw new PSError("typecheck", "expected number, got " + o.TypeName),
        };
    }

    public static double Num(PSObject o) => o switch
    {
        PSInt i => i.Value,
        PSReal r => r.Value,
        _ => throw new PSError("typecheck", "expected number, got " + o.TypeName),
    };

    public static bool IsNum(PSObject o) => o is PSInt or PSReal;

    public bool PopBool() => Pop() is PSBool b ? b.Value : throw new PSError("typecheck", "expected boolean");
    public PSDict PopDict() => Pop() is PSDict d ? d : throw new PSError("typecheck", "expected dictionary");
    public PSArray PopArray() => Pop() is PSArray a ? a : throw new PSError("typecheck", "expected array");
    public PSString PopString() => Pop() is PSString s ? s : throw new PSError("typecheck", "expected string");

    public PSObject PopProc()
    {
        var o = Pop();
        if (o is PSArray || o is PSOperator || o is PSName || o is PSString || o is PSFile) return o;
        throw new PSError("typecheck", "expected procedure");
    }

    public Matrix PopMatrix() => ToMatrix(Pop());

    public static Matrix ToMatrix(PSObject o)
    {
        if (o is not PSArray a || a.Length != 6) throw new PSError("typecheck", "expected matrix");
        return new Matrix(Num(a[0]), Num(a[1]), Num(a[2]), Num(a[3]), Num(a[4]), Num(a[5]));
    }

    public PSArray NewMatrix(in Matrix m) => new(new PSObject[]
    {
        Real(m.A), Real(m.B), Real(m.C), Real(m.D), Real(m.Tx), Real(m.Ty),
    }, AllocLevel);

    internal static PSObject Real(double v) => new PSReal(v);

    /// <summary>Writes a matrix into an existing 6-element array (for currentmatrix etc.).</summary>
    public void StoreMatrix(PSArray a, in Matrix m)
    {
        if (a.Length != 6) throw new PSError("rangecheck");
        ArrayPut(a, 0, Real(m.A)); ArrayPut(a, 1, Real(m.B)); ArrayPut(a, 2, Real(m.C));
        ArrayPut(a, 3, Real(m.D)); ArrayPut(a, 4, Real(m.Tx)); ArrayPut(a, 5, Real(m.Ty));
    }

    // ================= Dictionaries =================

    public PSObject? Lookup(NameAtom name)
    {
        for (int i = DStack.Count - 1; i >= 0; i--)
            if (DStack[i].Map.TryGetValue(name, out var v)) return v;
        return null;
    }

    public PSObject? Lookup(string name) => Lookup(NameAtom.Get(name));

    public PSDict? Where(object key)
    {
        for (int i = DStack.Count - 1; i >= 0; i--)
            if (DStack[i].Map.ContainsKey(key)) return DStack[i];
        return null;
    }

    public PSDict CurrentDict => DStack[^1];

    public PSDict NewDict(int capacity) => new(capacity, AllocLevel);

    /// <summary>Stores into a dictionary, journaling the old value so restore can undo it.</summary>
    public void DictPut(PSDict d, object key, PSObject value)
    {
        if (d.Access is Access.ReadOnly or Access.None && d != SystemDict) throw new PSError("invalidaccess", "dictionary is read-only");
        if (d.SaveLevel >= 0 && d.SaveLevel < SaveLevel && _saves.Count > 0)
        {
            var rec = _saves[^1];
            if (rec.DictKeys.Add((d, key)))
            {
                if (d.Map.TryGetValue(key, out var old)) rec.Undo.Add(() => d.Map[key] = old);
                else rec.Undo.Add(() => d.Map.Remove(key));
            }
        }
        d.Map[key] = value;
        if (d.Map.Count > d.MaxLength) d.MaxLength = d.Map.Count;
    }

    public void DictPut(PSDict d, string key, PSObject value) => DictPut(d, NameAtom.Get(key), value);

    public void DictRemove(PSDict d, object key)
    {
        if (!d.Map.TryGetValue(key, out var old)) return;
        if (d.SaveLevel >= 0 && d.SaveLevel < SaveLevel && _saves.Count > 0)
        {
            var rec = _saves[^1];
            if (rec.DictKeys.Add((d, key))) rec.Undo.Add(() => d.Map[key] = old);
        }
        d.Map.Remove(key);
    }

    public void ArrayPut(PSArray a, int index, PSObject value)
    {
        if (index < 0 || index >= a.Length) throw new PSError("rangecheck");
        var buf = a.Buf;
        if (buf.SaveLevel >= 0 && buf.SaveLevel < SaveLevel && _saves.Count > 0)
        {
            var copy = (PSObject[])buf.Items.Clone();
            int oldLevel = buf.SaveLevel;
            _saves[^1].Undo.Add(() => { buf.Items = copy; buf.SaveLevel = oldLevel; });
            buf.Items = (PSObject[])buf.Items.Clone();
            buf.SaveLevel = SaveLevel;
        }
        buf.Items[a.Offset + index] = value;
    }

    /// <summary>Call before writing into a string's bytes.</summary>
    public void TouchString(PSString s)
    {
        var buf = s.Buf;
        if (buf.SaveLevel >= 0 && buf.SaveLevel < SaveLevel && _saves.Count > 0)
        {
            var copy = (byte[])buf.Data.Clone();
            int oldLevel = buf.SaveLevel;
            var current = buf.Data;
            _saves[^1].Undo.Add(() => { Array.Copy(copy, current, copy.Length); buf.SaveLevel = oldLevel; });
            buf.SaveLevel = SaveLevel;
        }
    }

    // ================= Save / restore =================

    public PSSave Save()
    {
        SaveLevel++;
        var rec = new SaveRecord { Level = SaveLevel, GlobalAllocAtSave = GlobalAlloc };
        _saves.Add(rec);
        // save performs an implicit gsave
        GStack.Add(GS);
        GS = GS.Clone();
        GS.SavedBySave = false;
        GStack[^1].SavedBySave = true;
        rec.GStackDepth = GStack.Count;
        return new PSSave(SaveLevel);
    }

    public void Restore(PSSave s)
    {
        if (!s.Valid || s.Level > SaveLevel || s.Level < 1) throw new PSError("invalidrestore");
        while (SaveLevel >= s.Level)
        {
            var rec = _saves[^1];
            for (int i = rec.Undo.Count - 1; i >= 0; i--) rec.Undo[i]();
            _saves.RemoveAt(_saves.Count - 1);
            SaveLevel--;
            GlobalAlloc = rec.GlobalAllocAtSave;
            // grestoreall back to the state saved by this save
            if (GStack.Count >= rec.GStackDepth && rec.GStackDepth > 0)
            {
                GS = GStack[rec.GStackDepth - 1];
                GStack.RemoveRange(rec.GStackDepth - 1, GStack.Count - rec.GStackDepth + 1);
                GS.SavedBySave = false;
            }
        }
        s.Valid = false;
    }

    // ================= Execution =================

    internal void CheckCancel()
    {
        if ((++_steps & 0x3FF) == 0) _cancel.ThrowIfCancellationRequested();
    }

    /// <summary>Executes the print job until end of file, quit, or an unrecoverable error.</summary>
    public void RunJob(PSStream job)
    {
        JobStream = job;
        EStack.Add(new FileFrame(new PSFile(job, exec: true)));
        try
        {
            RunLoop(0);
        }
        catch (PSQuit) { }
        finally
        {
            Stdout.Flush();
        }
    }

    /// <summary>Executes an object to completion (used by operators that call procedures).</summary>
    public void ExecuteNested(PSObject proc)
    {
        if (EStack.Count > ExecLimit) throw new PSError("execstackoverflow");
        int baseDepth = EStack.Count;
        ExecObject(proc, fromProc: false);
        if (EStack.Count > baseDepth) RunLoop(baseDepth);
    }

    /// <summary>Calls a procedure that should leave a string on the stack (filter/image data sources).</summary>
    public PSString? CallForString(PSObject proc)
    {
        // PLRM: after the procedure runs, the string is taken from the top of the operand stack. Drivers rely on
        // this: e.g. one procedure reads a row and leaves copies that the following (empty) procedures "return".
        ExecuteNested(proc);
        if (_osp == 0 || Peek() is not PSString) return null;
        return (PSString)Pop();
    }

    private void RunLoop(int baseDepth)
    {
        while (EStack.Count > baseDepth)
        {
            if ((++_steps & 0x3FF) == 0) _cancel.ThrowIfCancellationRequested();
            var frame = EStack[^1];
            int osBefore = _osp;
            try
            {
                switch (frame)
                {
                    case ProcFrame pf:
                        {
                            var proc = pf.Proc;
                            if (pf.Pos >= proc.Length) { EStack.RemoveAt(EStack.Count - 1); break; }
                            var obj = proc[pf.Pos++];
                            if (pf.Pos >= proc.Length) EStack.RemoveAt(EStack.Count - 1); // tail call
                            if (!obj.IsExec || obj is PSArray) Push(obj);
                            else
                            {
                                osBefore = _osp;
                                ExecObject(obj, fromProc: true);
                            }
                            break;
                        }
                    case FileFrame ff:
                        {
                            var tok = Scanner.Next(ff.File.Stream);
                            if (tok is null)
                            {
                                if (EStack.Count > 0 && EStack[^1] == ff) EStack.RemoveAt(EStack.Count - 1);
                                break;
                            }
                            if (!tok.IsExec || tok is PSArray) Push(tok);
                            else
                            {
                                osBefore = _osp;
                                ExecObject(tok, fromProc: true);
                            }
                            break;
                        }
                    case LoopFrame lf:
                        if (!lf.Step(this) && EStack.Count > 0 && EStack[^1] == lf) EStack.RemoveAt(EStack.Count - 1);
                        break;
                    case StoppedFrame:
                        EStack.RemoveAt(EStack.Count - 1);
                        Push(PSBool.False);
                        break;
                    case ContinuationFrame cf:
                        {
                            int idx = EStack.Count - 1;
                            if (!cf.Next(this) && idx < EStack.Count && EStack[idx] == cf) EStack.RemoveAt(idx);
                            break;
                        }
                    default:
                        EStack.RemoveAt(EStack.Count - 1);
                        break;
                }
            }
            catch (PSError e)
            {
                // Restore the operands of the failed operator (pop does not clear slots).
                if (_osp < osBefore) _osp = osBefore;
                HandleError(e, baseDepth);
            }
            catch (StopSignal)
            {
                DoStop(baseDepth);
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or PSJobError or PSQuit))
            {
                // A bug or unexpected data in an operator: report it as a PostScript error so "stopped" can handle it.
                if (_osp < osBefore) _osp = osBefore;
                Log($"Internal error in {_currentCommand}: {ex.GetType().Name}: {ex.Message}");
                HandleError(new PSError("unregistered", ex.Message), baseDepth);
            }
        }
    }

    /// <summary>Executes an executable object: names are looked up, operators invoked, procedures pushed onto the exec stack.</summary>
    internal void ExecObject(PSObject obj, bool fromProc)
    {
        while (true)
        {
            switch (obj)
            {
                case PSOperator op:
                    _currentCommand = op;
                    op.Action(this);
                    return;
                case PSName n when n.IsExec:
                    {
                        _currentCommand = n;
                        var v = Lookup(n.Atom) ?? throw new PSError("undefined", n.Text);
                        if (v is PSArray va)
                        {
                            if (va.IsExec) { PushProc(va); return; }
                            Push(va);
                            return;
                        }
                        if (!v.IsExec) { Push(v); return; }
                        obj = v;
                        continue;
                    }
                case PSArray a when a.IsExec:
                    PushProc(a);
                    return;
                case PSString s when s.IsExec:
                    EStack.Add(new FileFrame(new PSFile(new BytesSource(s.ToArray()), true)));
                    return;
                case PSFile f when f.IsExec:
                    EStack.Add(new FileFrame(f));
                    return;
                default:
                    Push(obj);
                    return;
            }
        }
    }

    internal void PushProc(PSArray a)
    {
        if (a.Length == 0) return;
        if (EStack.Count > ExecLimit) throw new PSError("execstackoverflow");
        EStack.Add(new ProcFrame(a));
    }

    /// <summary>The "exec" operator semantics: run the object as if it appeared in a procedure.</summary>
    internal void ExecOperand(PSObject o)
    {
        if (!o.IsExec) { Push(o); return; }
        ExecObject(o, fromProc: false);
    }

    // ---- stop / exit ----

    internal void DoStop(int baseDepth)
    {
        for (int i = EStack.Count - 1; i >= baseDepth; i--)
        {
            if (EStack[i] is StoppedFrame)
            {
                EStack.RemoveRange(i, EStack.Count - i);
                Push(PSBool.True);
                return;
            }
        }
        if (baseDepth == 0)
        {
            EStack.Clear();
            var name = DollarError.Get("errorname")?.ToString() ?? "stop";
            var cmd = DollarError.Get("command")?.ToString() ?? "";
            throw new PSJobError($"PostScript error {name}, offending command: {cmd}");
        }
        EStack.RemoveRange(baseDepth, EStack.Count - baseDepth);
        throw new StopSignal();
    }

    internal void DoExit()
    {
        for (int i = EStack.Count - 1; i >= 0; i--)
        {
            switch (EStack[i])
            {
                case LoopFrame:
                    EStack.RemoveRange(i, EStack.Count - i);
                    return;
                case StoppedFrame:
                case FileFrame:
                    throw new PSError("invalidexit");
            }
        }
        throw new PSError("invalidexit");
    }

    private void HandleError(PSError e, int baseDepth)
    {
        var errName = e.ErrorName;
        var cmd = _currentCommand ?? PSNull.Instance;
        if (ErrorDict.TryGet(errName, out var handler) && !(handler is PSOperator op && _defaultErrorHandlers.ContainsValue(op)))
        {
            // Job-installed error handler: push the offending command and run it.
            Push(cmd);
            try { ExecObject(handler, fromProc: false); }
            catch (PSError) { RecordError(errName, cmd, e.Message); DoStop(baseDepth); }
            return;
        }
        RecordError(errName, cmd, e.Message);
        DoStop(baseDepth);
    }

    internal void RecordError(string errName, PSObject cmd, string detail)
    {
        DollarError.PutRaw("newerror", PSBool.True);
        DollarError.PutRaw("errorname", PSName.Lit(errName));
        DollarError.PutRaw("command", cmd);
        DollarError.PutRaw("errorinfo", PSNull.Instance);
        DollarError.PutRaw(".detail", PSString.FromText(detail));
        LastError = $"{errName} in {cmd} ({detail})";
        if (TraceErrors) Log("PS error (may be handled by the job): " + LastError);
    }

    /// <summary>Log every PostScript error, including those the job handles itself with "stopped".</summary>
    public static bool TraceErrors = Environment.GetEnvironmentVariable("PRINTTOIMAGE_TRACE") == "1";

    /// <summary>Most recent PostScript error (also errors caught by "stopped"), for diagnostics.</summary>
    public string? LastError { get; private set; }

    private void RegisterDefaultErrorHandler(string name)
    {
        var op = new PSOperator(name, ip =>
        {
            // Default handler: record the error and stop. (Invoked only if a job calls errordict procedures directly.)
            var cmd = ip.Pop();
            ip.RecordError(name, cmd, name);
            throw new StopSignal();
        });
        _defaultErrorHandlers[name] = op;
        ErrorDict.PutRaw(name, op);
    }
}
