using SkiaSharp;

namespace PrintToImage.PostScript.Graphics;

/// <summary>PostScript matrix [a b c d tx ty]: x' = a*x + c*y + tx, y' = b*x + d*y + ty.</summary>
public readonly record struct Matrix(double A, double B, double C, double D, double Tx, double Ty)
{
    public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

    /// <summary>Returns this × m (apply this first, then m) — PostScript "concat" order is m' = this × CTM.</summary>
    public Matrix Multiply(in Matrix m) => new(
        A * m.A + B * m.C,
        A * m.B + B * m.D,
        C * m.A + D * m.C,
        C * m.B + D * m.D,
        Tx * m.A + Ty * m.C + m.Tx,
        Tx * m.B + Ty * m.D + m.Ty);

    public static Matrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);
    public static Matrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);
    public static Matrix Rotate(double degrees)
    {
        double r = degrees * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        // Exact values for multiples of 90 degrees avoid tiny rounding errors.
        if (degrees % 90 == 0)
        {
            int q = (int)(((degrees / 90) % 4 + 4) % 4);
            (c, s) = q switch { 0 => (1d, 0d), 1 => (0d, 1d), 2 => (-1d, 0d), _ => (0d, -1d) };
        }
        return new(c, s, -s, c, 0, 0);
    }

    public double Determinant => A * D - B * C;

    public Matrix Invert()
    {
        double det = Determinant;
        if (det == 0 || double.IsNaN(det)) throw new PSError("undefinedresult", "singular matrix");
        return new(D / det, -B / det, -C / det, A / det, (C * Ty - D * Tx) / det, (B * Tx - A * Ty) / det);
    }

    public bool TryInvert(out Matrix inv)
    {
        double det = Determinant;
        if (det == 0 || double.IsNaN(det) || double.IsInfinity(det)) { inv = Identity; return false; }
        inv = Invert();
        return true;
    }

    public (double X, double Y) Transform(double x, double y) => (A * x + C * y + Tx, B * x + D * y + Ty);
    public (double X, double Y) DTransform(double x, double y) => (A * x + C * y, B * x + D * y);

    public SKMatrix ToSK() => new((float)A, (float)C, (float)Tx, (float)B, (float)D, (float)Ty, 0, 0, 1);

    /// <summary>Average scale factor (for line widths and flatness).</summary>
    public double ScaleFactor => Math.Sqrt(Math.Abs(Determinant));
}
