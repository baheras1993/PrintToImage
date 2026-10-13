using SkiaSharp;

namespace PrintToImage.PostScript.Fonts;

/// <summary>Type 1 charstring decryption and interpretation (Adobe Type 1 Font Format, ch. 6).</summary>
public static class Type1Charstrings
{
    public static byte[] Decrypt(ReadOnlySpan<byte> data, int lenIV)
    {
        if (lenIV < 0) return data.ToArray();
        ushort r = 4330;
        var plain = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            byte c = data[i];
            plain[i] = (byte)(c ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
        }
        return plain.Length > lenIV ? plain[lenIV..] : Array.Empty<byte>();
    }

    public sealed class Result
    {
        public SKPath Path = new() { FillType = SKPathFillType.Winding };
        public double Wx, Wy;
    }

    /// <param name="subr">Returns the decrypted subroutine n, or null.</param>
    /// <param name="seacGlyph">Returns the decrypted charstring for a StandardEncoding code (for accented characters).</param>
    public static Result Run(byte[] cs, Func<int, byte[]?> subr, Func<int, byte[]?> seacGlyph)
    {
        var res = new Result();
        var st = new State(res, subr, seacGlyph);
        st.Exec(cs, 0);
        return res;
    }

    private sealed class State
    {
        private readonly Result _r;
        private readonly Func<int, byte[]?> _subr, _seac;
        private readonly double[] _stack = new double[48];
        private int _sp;
        private readonly Stack<double> _ps = new();
        private double _x, _y, _sbx, _sby;
        private bool _open, _done;
        private bool _flex;
        private readonly List<(double X, double Y)> _flexPts = new();
        private double _offX, _offY; // for seac accent placement

        public State(Result r, Func<int, byte[]?> subr, Func<int, byte[]?> seac) { _r = r; _subr = subr; _seac = seac; }

        private void Push(double v) { if (_sp < _stack.Length) _stack[_sp++] = v; }
        private void Clear() => _sp = 0;

        private void MoveTo(double x, double y)
        {
            if (_flex) { _flexPts.Add((x, y)); _x = x; _y = y; return; }
            if (_open) _r.Path.Close();
            _x = x; _y = y;
            _r.Path.MoveTo((float)(_x + _offX), (float)(_y + _offY));
            _open = true;
        }

        private void LineTo(double x, double y)
        {
            if (!_open) MoveTo(_x, _y);
            _x = x; _y = y;
            _r.Path.LineTo((float)(_x + _offX), (float)(_y + _offY));
        }

        private void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
        {
            if (!_open) MoveTo(_x, _y);
            _r.Path.CubicTo((float)(x1 + _offX), (float)(y1 + _offY), (float)(x2 + _offX), (float)(y2 + _offY), (float)(x3 + _offX), (float)(y3 + _offY));
            _x = x3; _y = y3;
        }

        public void Exec(byte[] cs, int depth)
        {
            if (depth > 12) return;
            int i = 0;
            while (i < cs.Length && !_done)
            {
                int v = cs[i++];
                if (v >= 32)
                {
                    if (v <= 246) Push(v - 139);
                    else if (v <= 250) { if (i >= cs.Length) return; Push((v - 247) * 256 + cs[i++] + 108); }
                    else if (v <= 254) { if (i >= cs.Length) return; Push(-(v - 251) * 256 - cs[i++] - 108); }
                    else { if (i + 4 > cs.Length) return; Push((cs[i] << 24) | (cs[i + 1] << 16) | (cs[i + 2] << 8) | cs[i + 3]); i += 4; }
                    continue;
                }
                switch (v)
                {
                    case 1: case 3: Clear(); break; // hstem, vstem
                    case 4: if (_sp >= 1) MoveTo(_x, _y + _stack[_sp - 1]); Clear(); break; // vmoveto
                    case 5: if (_sp >= 2) LineTo(_x + _stack[_sp - 2], _y + _stack[_sp - 1]); Clear(); break; // rlineto
                    case 6: if (_sp >= 1) LineTo(_x + _stack[_sp - 1], _y); Clear(); break; // hlineto
                    case 7: if (_sp >= 1) LineTo(_x, _y + _stack[_sp - 1]); Clear(); break; // vlineto
                    case 8: // rrcurveto
                        if (_sp >= 6)
                        {
                            double x1 = _x + _stack[0], y1 = _y + _stack[1], x2 = x1 + _stack[2], y2 = y1 + _stack[3], x3 = x2 + _stack[4], y3 = y2 + _stack[5];
                            CurveTo(x1, y1, x2, y2, x3, y3);
                        }
                        Clear();
                        break;
                    case 9: // closepath
                        if (_open) { _r.Path.Close(); _open = false; }
                        Clear();
                        break;
                    case 10: // callsubr
                        {
                            if (_sp < 1) break;
                            int n = (int)_stack[--_sp];
                            var s = _subr(n);
                            if (s is not null) Exec(s, depth + 1);
                            break;
                        }
                    case 11: return; // return
                    case 12:
                        {
                            if (i >= cs.Length) return;
                            int e = cs[i++];
                            Escape(e, depth);
                            break;
                        }
                    case 13: // hsbw
                        if (_sp >= 2)
                        {
                            _sbx = _stack[0]; _sby = 0;
                            _r.Wx = _stack[1]; _r.Wy = 0;
                            _x = _sbx; _y = _sby;
                        }
                        Clear();
                        break;
                    case 14: // endchar
                        if (_open) { _r.Path.Close(); _open = false; }
                        _done = true;
                        return;
                    case 21: if (_sp >= 2) MoveTo(_x + _stack[_sp - 2], _y + _stack[_sp - 1]); Clear(); break; // rmoveto
                    case 22: if (_sp >= 1) MoveTo(_x + _stack[_sp - 1], _y); Clear(); break; // hmoveto
                    case 30: // vhcurveto
                        if (_sp >= 4)
                        {
                            double x1 = _x, y1 = _y + _stack[0], x2 = x1 + _stack[1], y2 = y1 + _stack[2], x3 = x2 + _stack[3], y3 = y2;
                            CurveTo(x1, y1, x2, y2, x3, y3);
                        }
                        Clear();
                        break;
                    case 31: // hvcurveto
                        if (_sp >= 4)
                        {
                            double x1 = _x + _stack[0], y1 = _y, x2 = x1 + _stack[1], y2 = y1 + _stack[2], x3 = x2, y3 = y2 + _stack[3];
                            CurveTo(x1, y1, x2, y2, x3, y3);
                        }
                        Clear();
                        break;
                    default: Clear(); break;
                }
            }
        }

        private void Escape(int e, int depth)
        {
            switch (e)
            {
                case 0: case 1: case 2: Clear(); break; // dotsection, vstem3, hstem3
                case 6: // seac: asb adx ady bchar achar
                    {
                        if (_sp < 5) { Clear(); break; }
                        double asb = _stack[0], adx = _stack[1], ady = _stack[2];
                        int bchar = (int)_stack[3], achar = (int)_stack[4];
                        Clear();
                        var baseCs = _seac(bchar);
                        var accCs = _seac(achar);
                        double wx = _r.Wx, wy = _r.Wy;
                        if (baseCs is not null)
                        {
                            var sub = new State(_r, _subr, _seac);
                            sub.Exec(baseCs, depth + 1);
                        }
                        if (accCs is not null)
                        {
                            // The accent's own hsbw moves to its sidebearing (asb); shift so it lands at adx.
                            var sub = new State(_r, _subr, _seac) { _offX = adx - asb, _offY = ady };
                            sub.Exec(accCs, depth + 1);
                        }
                        _r.Wx = wx; _r.Wy = wy;
                        _done = true;
                        break;
                    }
                case 7: // sbw
                    if (_sp >= 4)
                    {
                        _sbx = _stack[0]; _sby = _stack[1];
                        _r.Wx = _stack[2]; _r.Wy = _stack[3];
                        _x = _sbx; _y = _sby;
                    }
                    Clear();
                    break;
                case 12: // div
                    if (_sp >= 2) { double b = _stack[--_sp], a = _stack[--_sp]; Push(b == 0 ? 0 : a / b); }
                    break;
                case 16: // callothersubr
                    {
                        if (_sp < 2) { Clear(); break; }
                        int othersubr = (int)_stack[--_sp];
                        int n = (int)_stack[--_sp];
                        var args = new double[Math.Max(0, Math.Min(n, _sp))];
                        for (int k = args.Length - 1; k >= 0; k--) args[k] = _stack[--_sp];
                        switch (othersubr)
                        {
                            case 1: // start flex
                                _flex = true;
                                _flexPts.Clear();
                                break;
                            case 2: // add flex point (point was recorded by rmoveto)
                                break;
                            case 0: // end flex
                                {
                                    _flex = false;
                                    // Points: [0] reference, [1..6] two curves.
                                    if (_flexPts.Count >= 7)
                                    {
                                        var p = _flexPts;
                                        CurveTo(p[1].X, p[1].Y, p[2].X, p[2].Y, p[3].X, p[3].Y);
                                        CurveTo(p[4].X, p[4].Y, p[5].X, p[5].Y, p[6].X, p[6].Y);
                                    }
                                    else if (_flexPts.Count > 0)
                                    {
                                        var last = _flexPts[^1];
                                        LineTo(last.X, last.Y);
                                    }
                                    // Leave the end point for "pop pop setcurrentpoint".
                                    _ps.Push(_y);
                                    _ps.Push(_x);
                                    break;
                                }
                            case 3: // hint replacement: returns the subr number 3
                                _ps.Push(3);
                                break;
                            default:
                                for (int k = args.Length - 1; k >= 0; k--) _ps.Push(args[k]);
                                break;
                        }
                        break;
                    }
                case 17: // pop
                    Push(_ps.Count > 0 ? _ps.Pop() : 0);
                    break;
                case 33: // setcurrentpoint
                    if (_sp >= 2) { _x = _stack[0]; _y = _stack[1]; }
                    Clear();
                    break;
                default: Clear(); break;
            }
        }
    }
}
