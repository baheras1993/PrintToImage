using SkiaSharp;

namespace PrintToImage.PostScript.Graphics;

public enum CSFamily { DeviceGray, DeviceRGB, DeviceCMYK, Indexed, Pattern, Separation, DeviceN }

/// <summary>A color space. CIE-based and ICC spaces are treated as the device space with the same number of components.</summary>
public sealed class ColorSpace
{
    public CSFamily Family;
    public int NComps;
    public PSObject Source = null!;               // the original name or array, returned by currentcolorspace
    public ColorSpace? Base;              // Indexed base, Separation/DeviceN alternate, Pattern underlying space
    public int HiVal;                     // Indexed
    public byte[]? Lookup;                // Indexed lookup table
    public PSObject? LookupProc;          // Indexed lookup procedure
    public PSObject? TintTransform;       // Separation / DeviceN
    public bool SeparationNone;           // /None separation paints nothing

    public static readonly ColorSpace Gray = new() { Family = CSFamily.DeviceGray, NComps = 1, Source = PSName.Lit("DeviceGray") };
    public static readonly ColorSpace RGB = new() { Family = CSFamily.DeviceRGB, NComps = 3, Source = PSName.Lit("DeviceRGB") };
    public static readonly ColorSpace CMYK = new() { Family = CSFamily.DeviceCMYK, NComps = 4, Source = PSName.Lit("DeviceCMYK") };

    public double[] InitialColor() => Family switch
    {
        CSFamily.DeviceCMYK => new double[] { 0, 0, 0, 1 },
        CSFamily.Indexed => new double[] { 0 },
        CSFamily.Separation or CSFamily.DeviceN => Enumerable.Repeat(1.0, NComps).ToArray(),
        CSFamily.Pattern => Array.Empty<double>(),
        _ => new double[NComps],
    };
}

/// <summary>A pattern created by makepattern.</summary>
public sealed class PatternInfo
{
    public int PatternType;     // 1 tiling, 2 shading
    public int PaintType;       // 1 colored, 2 uncolored
    public SKShader? Shader;    // tile shader in device space (null if empty)
    public SKColor AverageColor = SKColors.Gray;
    public PSDict? Dict;
}

public sealed class GState
{
    public Matrix CTM = Matrix.Identity;
    public PSPath Path = new();
    public SKPath? Clip;                       // device space; null = whole page
    public ColorSpace ColorSpace = ColorSpace.Gray;
    public double[] Color = { 0 };
    public PatternInfo? Pattern;               // when ColorSpace is Pattern
    public double LineWidth = 1;
    public int LineCap, LineJoin;
    public double MiterLimit = 10;
    public double[] Dash = Array.Empty<double>();
    public double DashOffset;
    public double Flatness = 1;
    public bool StrokeAdjust;
    public PSDict? Font;
    public PSObject? BlackGeneration, UnderColorRemoval, Halftone, ColorRendering;
    /// <summary>Transfer functions (red, green, blue, gray); null = identity.</summary>
    public PSObject? TransferR, TransferG, TransferB, TransferGray;
    public bool Overprint;
    public double Smoothness = 0.02;
    /// <summary>Set by gsave from a save operator (restore pops back to this one).</summary>
    public bool SavedBySave;
    /// <summary>nulldevice: painting operators have no effect.</summary>
    public bool NullDevice;
    /// <summary>Stack for clipsave/cliprestore.</summary>
    public List<SKPath?> ClipStack = new();

    public GState Clone() => new()
    {
        CTM = CTM,
        Path = Path.Clone(),
        Clip = Clip,                 // clip paths are never mutated after creation, so sharing is safe
        ColorSpace = ColorSpace,
        Color = (double[])Color.Clone(),
        Pattern = Pattern,
        LineWidth = LineWidth,
        LineCap = LineCap,
        LineJoin = LineJoin,
        MiterLimit = MiterLimit,
        Dash = Dash,
        DashOffset = DashOffset,
        Flatness = Flatness,
        StrokeAdjust = StrokeAdjust,
        Font = Font,
        TransferR = TransferR, TransferG = TransferG, TransferB = TransferB, TransferGray = TransferGray,
        BlackGeneration = BlackGeneration,
        UnderColorRemoval = UnderColorRemoval,
        Halftone = Halftone,
        ColorRendering = ColorRendering,
        Overprint = Overprint,
        Smoothness = Smoothness,
        NullDevice = NullDevice,
        ClipStack = new List<SKPath?>(ClipStack),
    };
}
