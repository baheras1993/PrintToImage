using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace PrintToImage.PostScript;

/// <summary>Base of every PostScript object. Composite objects share their value between copies.</summary>
public abstract class PSObject
{
    public virtual bool IsExec => false;
    public abstract string TypeName { get; }
    public override string ToString() => "--" + TypeName + "--";
}

public sealed class PSInt : PSObject
{
    private static readonly PSInt[] Small = Enumerable.Range(-128, 1153).Select(i => new PSInt(i)).ToArray();
    public readonly int Value;
    private PSInt(int v) => Value = v;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PSInt Get(int v) => v is >= -128 and <= 1024 ? Small[v + 128] : new PSInt(v);
    public override string TypeName => "integertype";
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class PSReal : PSObject
{
    public readonly double Value;
    public PSReal(double v) => Value = v;
    public override string TypeName => "realtype";
    public override string ToString()
    {
        var s = ((float)Value).ToString("G7", CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') || s.Contains('N') || s.Contains('I') ? s : s + ".0";
    }
}

public sealed class PSBool : PSObject
{
    public static readonly PSBool True = new(true), False = new(false);
    public readonly bool Value;
    private PSBool(bool v) => Value = v;
    public static PSBool Get(bool v) => v ? True : False;
    public override string TypeName => "booleantype";
    public override string ToString() => Value ? "true" : "false";
}

public sealed class PSNull : PSObject
{
    public static readonly PSNull Instance = new();
    public override string TypeName => "nulltype";
    public override string ToString() => "null";
}

public sealed class PSMark : PSObject
{
    public static readonly PSMark Instance = new();
    public override string TypeName => "marktype";
}

/// <summary>An interned name. Names are compared by reference, which makes dictionary lookups fast.</summary>
public sealed class NameAtom
{
    private static readonly Dictionary<string, NameAtom> Table = new(StringComparer.Ordinal);
    public readonly string Text;
    public readonly PSName Literal, Executable;

    private NameAtom(string text)
    {
        Text = text;
        Literal = new PSName(this, false);
        Executable = new PSName(this, true);
    }

    public static NameAtom Get(string text)
    {
        lock (Table)
        {
            if (!Table.TryGetValue(text, out var a)) Table[text] = a = new NameAtom(text);
            return a;
        }
    }

    public override string ToString() => Text;
}

public sealed class PSName : PSObject
{
    public readonly NameAtom Atom;
    private readonly bool _exec;
    internal PSName(NameAtom atom, bool exec) { Atom = atom; _exec = exec; }
    public static PSName Lit(string s) => NameAtom.Get(s).Literal;
    public static PSName Exe(string s) => NameAtom.Get(s).Executable;
    public string Text => Atom.Text;
    public override bool IsExec => _exec;
    public override string TypeName => "nametype";
    public override string ToString() => Atom.Text;
}

public enum Access { Unlimited, ReadOnly, ExecuteOnly, None }

/// <summary>A (sub)string sharing a byte buffer with other strings created by getinterval.</summary>
public sealed class PSString : PSObject
{
    public readonly StringBuffer Buf;
    public readonly int Offset, Length;
    private readonly bool _exec;
    public Access Access;

    public PSString(StringBuffer buf, int offset, int length, bool exec = false, Access access = Access.Unlimited)
    { Buf = buf; Offset = offset; Length = length; _exec = exec; Access = access; }

    public PSString(byte[] bytes, int saveLevel = 0, bool exec = false) : this(new StringBuffer(bytes, saveLevel), 0, bytes.Length, exec) { }
    public static PSString FromText(string s, int saveLevel = 0) => new(Encoding.Latin1.GetBytes(s), saveLevel);

    public override bool IsExec => _exec;
    public override string TypeName => "stringtype";
    public Span<byte> Span => Buf.Data.AsSpan(Offset, Length);
    public byte this[int i] { get => Buf.Data[Offset + i]; }
    public string Text => Encoding.Latin1.GetString(Buf.Data, Offset, Length);
    public byte[] ToArray() => Span.ToArray();
    public PSString WithExec(bool exec) => new(Buf, Offset, Length, exec, Access);
    public PSString Sub(int index, int count) => new(Buf, Offset + index, count, _exec, Access);
    public override string ToString() => Text;
}

public sealed class StringBuffer
{
    public byte[] Data;
    public int SaveLevel;   // save level at which this buffer was created or last journaled
    public StringBuffer(byte[] data, int saveLevel) { Data = data; SaveLevel = saveLevel; }
}

/// <summary>Array or procedure. getinterval shares the underlying storage.</summary>
public sealed class PSArray : PSObject
{
    public readonly ArrayBuffer Buf;
    public readonly int Offset, Length;
    private readonly bool _exec;
    public Access Access;
    public bool Packed;

    public PSArray(ArrayBuffer buf, int offset, int length, bool exec = false, Access access = Access.Unlimited)
    { Buf = buf; Offset = offset; Length = length; _exec = exec; Access = access; }

    public PSArray(PSObject[] items, int saveLevel = 0, bool exec = false) : this(new ArrayBuffer(items, saveLevel), 0, items.Length, exec) { }

    public override bool IsExec => _exec;
    public override string TypeName => Packed ? "packedarraytype" : "arraytype";
    public PSObject this[int i] => Buf.Items[Offset + i];
    public ReadOnlySpan<PSObject> Items => Buf.Items.AsSpan(Offset, Length);
    public PSArray WithExec(bool exec) => new(Buf, Offset, Length, exec, Access) { Packed = Packed };
    public PSArray Sub(int index, int count) => new(Buf, Offset + index, count, _exec, Access) { Packed = Packed };
}

public sealed class ArrayBuffer
{
    public PSObject[] Items;
    public int SaveLevel;
    public ArrayBuffer(PSObject[] items, int saveLevel) { Items = items; SaveLevel = saveLevel; }
}

public sealed class PSDict : PSObject
{
    public readonly Dictionary<object, PSObject> Map;
    public int MaxLength;
    public int SaveLevel;
    public Access Access;
    /// <summary>Set for font dictionaries after definefont; holds the parsed font.</summary>
    public Fonts.FontBase? Font;

    public PSDict(int capacity = 8, int saveLevel = 0)
    {
        Map = new Dictionary<object, PSObject>(Math.Max(capacity, 1));
        MaxLength = Math.Max(capacity, 1);
        SaveLevel = saveLevel;
    }

    public override string TypeName => "dicttype";
    public int Count => Map.Count;

    /// <summary>Normalizes a key: names and strings become NameAtoms, integral reals become ints.</summary>
    public static object Key(PSObject k) => k switch
    {
        PSName n => n.Atom,
        PSString s => NameAtom.Get(s.Text),
        PSInt i => i.Value,
        PSReal r => r.Value == Math.Floor(r.Value) && Math.Abs(r.Value) < int.MaxValue ? (int)r.Value : r.Value,
        PSBool b => b.Value,
        PSArray a => a.Buf,
        _ => k,
    };

    public static PSObject KeyObject(object k) => k switch
    {
        NameAtom a => a.Literal,
        int i => PSInt.Get(i),
        double d => new PSReal(d),
        bool b => PSBool.Get(b),
        ArrayBuffer ab => new PSArray(ab, 0, ab.Items.Length),
        PSObject o => o,
        _ => PSNull.Instance,
    };

    public bool TryGet(string name, out PSObject value) => Map.TryGetValue(NameAtom.Get(name), out value!);
    public bool TryGet(NameAtom name, out PSObject value) => Map.TryGetValue(name, out value!);
    public PSObject? Get(string name) => Map.TryGetValue(NameAtom.Get(name), out var v) ? v : null;
    public bool Has(string name) => Map.ContainsKey(NameAtom.Get(name));

    /// <summary>Raw put without save journaling (used while building dictionaries the interpreter owns).</summary>
    public void PutRaw(string name, PSObject value) => Map[NameAtom.Get(name)] = value;
}

public sealed class PSOperator : PSObject
{
    public readonly string Name;
    public readonly Action<Interpreter> Action;
    public PSOperator(string name, Action<Interpreter> action) { Name = name; Action = action; }
    public override bool IsExec => true;
    public override string TypeName => "operatortype";
    public override string ToString() => "--" + Name + "--";
}

public sealed class PSFile : PSObject
{
    public readonly PSStream Stream;
    private readonly bool _exec;
    public PSFile(PSStream s, bool exec = false) { Stream = s; _exec = exec; }
    public override bool IsExec => _exec;
    public override string TypeName => "filetype";
    public PSFile WithExec(bool exec) => new(Stream, exec);
}

public sealed class PSSave : PSObject
{
    public readonly int Level;
    public bool Valid = true;
    public PSSave(int level) => Level = level;
    public override string TypeName => "savetype";
}

public sealed class PSFontId : PSObject
{
    public static readonly PSFontId Instance = new();
    public override string TypeName => "fonttype";
}

public sealed class PSGState : PSObject
{
    public Graphics.GState State;
    public PSGState(Graphics.GState s) => State = s;
    public override string TypeName => "gstatetype";
}

/// <summary>A PostScript error such as /typecheck or /undefined.</summary>
public sealed class PSError : Exception
{
    public readonly string ErrorName;
    public PSError(string name, string? detail = null) : base(detail is null ? name : name + ": " + detail) => ErrorName = name;
}

/// <summary>Thrown by quit / end of job.</summary>
public sealed class PSQuit : Exception { }
