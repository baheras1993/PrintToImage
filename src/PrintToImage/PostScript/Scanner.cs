using System.Globalization;
using System.Text;

namespace PrintToImage.PostScript;

/// <summary>PostScript tokenizer (PLRM 3.2): numbers, names, strings, procedures.</summary>
public sealed class Scanner
{
    private readonly Interpreter _ip;
    private readonly StringBuilder _sb = new();
    private readonly List<byte> _bytes = new();

    public Scanner(Interpreter ip) => _ip = ip;

    // Ctrl-D (4) is end-of-job on serial/parallel printers; drivers often send it, so treat it as whitespace.
    public static bool IsWhite(int c) => c is 0 or 4 or 9 or 10 or 12 or 13 or 27 or 32;
    public static bool IsDelim(int c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    /// <summary>Reads the next token, or null at end of file.</summary>
    public PSObject? Next(PSStream s)
    {
        while (true)
        {
            int c = s.ReadByte();
            if (c < 0) return null;
            if (IsWhite(c)) continue;
            if (c == '%')
            {
                while ((c = s.ReadByte()) >= 0 && c != '\n' && c != '\r' && c != '\f') { }
                continue;
            }
            switch (c)
            {
                case '(': return ReadString(s);
                case '<':
                    {
                        int n = s.ReadByte();
                        if (n == '<') return PSName.Exe("<<");
                        if (n == '~') return ReadA85String(s);
                        s.Unread(n);
                        return ReadHexString(s);
                    }
                case '>':
                    {
                        int n = s.ReadByte();
                        if (n == '>') return PSName.Exe(">>");
                        s.Unread(n);
                        throw new PSError("syntaxerror", "unexpected >");
                    }
                case '[': return PSName.Exe("[");
                case ']': return PSName.Exe("]");
                case '{': return ReadProc(s);
                case '}': throw new PSError("syntaxerror", "unexpected }");
                case ')': throw new PSError("syntaxerror", "unexpected )");
                case '/':
                    {
                        int n = s.ReadByte();
                        if (n == '/')
                        {
                            var name = ReadRegular(s, -1);
                            var atom = NameAtom.Get(name);
                            return _ip.Lookup(atom) ?? throw new PSError("undefined", "//" + name);
                        }
                        s.Unread(n);
                        return PSName.Lit(ReadRegular(s, -1));
                    }
                default:
                    {
                        var text = ReadRegular(s, c);
                        return ParseNumber(text) ?? PSName.Exe(text);
                    }
            }
        }
    }

    private string ReadRegular(PSStream s, int first)
    {
        _sb.Clear();
        if (first >= 0) _sb.Append((char)first);
        while (true)
        {
            int c = s.ReadByte();
            if (c < 0) break;
            if (IsWhite(c)) break; // whitespace after a token is consumed
            if (IsDelim(c)) { s.Unread(c); break; }
            _sb.Append((char)c);
        }
        return _sb.ToString();
    }

    public static PSObject? ParseNumber(string t)
    {
        if (t.Length == 0) return null;
        char f = t[0];
        if (!(char.IsAsciiDigit(f) || f is '+' or '-' or '.')) return null;

        int hash = t.IndexOf('#');
        if (hash > 0)
        {
            if (!int.TryParse(t.AsSpan(0, hash), NumberStyles.None, CultureInfo.InvariantCulture, out int radix) || radix is < 2 or > 36) return null;
            long v = 0;
            for (int i = hash + 1; i < t.Length; i++)
            {
                int dg = char.IsAsciiDigit(t[i]) ? t[i] - '0' : char.IsAsciiLetter(t[i]) ? char.ToLowerInvariant(t[i]) - 'a' + 10 : 99;
                if (dg >= radix) return null;
                v = v * radix + dg;
                if (v > uint.MaxValue) return new PSReal(v);
            }
            if (hash == t.Length - 1) return null;
            return PSInt.Get(unchecked((int)(uint)v));
        }

        bool isInt = true;
        for (int i = 0; i < t.Length; i++)
        {
            char ch = t[i];
            if (char.IsAsciiDigit(ch)) continue;
            if (i == 0 && ch is '+' or '-') continue;
            isInt = false;
            break;
        }
        if (isInt)
        {
            if (t.Length == 1 && f is '+' or '-') return null;
            if (int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int iv)) return PSInt.Get(iv);
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double big) ? new PSReal(big) : null;
        }

        // Real: must contain a digit and only digits, sign, '.', exponent.
        bool digit = false;
        foreach (var ch in t)
        {
            if (char.IsAsciiDigit(ch)) digit = true;
            else if (ch is not ('+' or '-' or '.' or 'e' or 'E')) return null;
        }
        if (!digit) return null;
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? new PSReal(d) : null;
    }

    private PSString ReadString(PSStream s)
    {
        _bytes.Clear();
        int depth = 1;
        while (true)
        {
            int c = s.ReadByte();
            if (c < 0) throw new PSError("syntaxerror", "unterminated string");
            if (c == '\\')
            {
                int n = s.ReadByte();
                switch (n)
                {
                    case 'n': _bytes.Add(10); break;
                    case 'r': _bytes.Add(13); break;
                    case 't': _bytes.Add(9); break;
                    case 'b': _bytes.Add(8); break;
                    case 'f': _bytes.Add(12); break;
                    case '\r':
                        if (s.Peek() == '\n') s.ReadByte();
                        break;
                    case '\n': break;
                    case >= '0' and <= '7':
                        {
                            int v = n - '0';
                            for (int k = 0; k < 2; k++)
                            {
                                int d = s.Peek();
                                if (d is >= '0' and <= '7') { s.ReadByte(); v = v * 8 + (d - '0'); }
                                else break;
                            }
                            _bytes.Add((byte)v);
                            break;
                        }
                    case < 0: throw new PSError("syntaxerror", "unterminated string");
                    default: _bytes.Add((byte)n); break;
                }
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) break;
            else if (c == '\r')
            {
                // end-of-line in a string is stored as \n
                if (s.Peek() == '\n') s.ReadByte();
                c = '\n';
            }
            _bytes.Add((byte)c);
        }
        return new PSString(_bytes.ToArray(), _ip.SaveLevel);
    }

    private PSString ReadHexString(PSStream s)
    {
        _bytes.Clear();
        int hi = -1;
        while (true)
        {
            int c = s.ReadByte();
            if (c < 0) throw new PSError("syntaxerror", "unterminated hex string");
            if (c == '>') break;
            int v = AsciiHexDecode.HexVal(c);
            if (v < 0)
            {
                if (IsWhite(c)) continue;
                throw new PSError("syntaxerror", "bad hex string");
            }
            if (hi < 0) hi = v; else { _bytes.Add((byte)((hi << 4) | v)); hi = -1; }
        }
        if (hi >= 0) _bytes.Add((byte)(hi << 4));
        return new PSString(_bytes.ToArray(), _ip.SaveLevel);
    }

    private PSString ReadA85String(PSStream s)
    {
        var f = new Ascii85Decode(s, false);
        _bytes.Clear();
        int b;
        while ((b = f.ReadByte()) >= 0) _bytes.Add((byte)b);
        return new PSString(_bytes.ToArray(), _ip.SaveLevel);
    }

    private PSArray ReadProc(PSStream s)
    {
        var items = new List<PSObject>();
        while (true)
        {
            int c;
            // Detect the closing brace before calling Next (which would treat it as an error).
            while (true)
            {
                c = s.ReadByte();
                if (c < 0) throw new PSError("syntaxerror", "unterminated procedure");
                if (IsWhite(c)) continue;
                if (c == '%')
                {
                    while ((c = s.ReadByte()) >= 0 && c != '\n' && c != '\r' && c != '\f') { }
                    continue;
                }
                break;
            }
            if (c == '}') break;
            s.Unread(c);
            var tok = Next(s) ?? throw new PSError("syntaxerror", "unterminated procedure");
            items.Add(tok);
        }
        return new PSArray(items.ToArray(), _ip.SaveLevel, exec: true);
    }
}
