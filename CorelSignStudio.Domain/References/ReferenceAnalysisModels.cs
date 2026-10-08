using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CorelSignStudio.Domain.References;

/// <summary>A real-world size in millimetres. Pixels are never a physical size.</summary>
public sealed record PhysicalSize(double WidthMm, double HeightMm)
{
    [JsonIgnore]
    public double AspectRatio => HeightMm <= 0 ? 0 : WidthMm / HeightMm;

    [JsonIgnore]
    public bool IsValid => double.IsFinite(WidthMm) && double.IsFinite(HeightMm) && WidthMm > 0 && HeightMm > 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{WidthMm:0.##} x {HeightMm:0.##} mm");
}

/// <summary>Finds a size such as "500x700 mm", "50 x 70 cm" or "1,2x2 m" in free text and normalises it to millimetres.</summary>
public static partial class DimensionParser
{
    public static bool TryParse(string? text, out PhysicalSize size)
    {
        size = new PhysicalSize(0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = Pattern().Match(text);
        if (!match.Success)
        {
            return false;
        }

        // A unit written after either number applies to both ("50x70 cm", "50 cm x 70 cm").
        var unit = match.Groups["u2"].Success ? match.Groups["u2"].Value : match.Groups["u1"].Value;
        var factor = unit.ToLowerInvariant() switch
        {
            "cm" => 10d,
            "m" => 1000d,
            _ => 1d, // mm, or no unit: millimetres are the application's working unit
        };
        var firstFactor = match.Groups["u1"].Success ? UnitFactor(match.Groups["u1"].Value) : factor;
        var width = Number(match.Groups["w"].Value) * firstFactor;
        var height = Number(match.Groups["h"].Value) * factor;
        size = new PhysicalSize(width, height);
        return size.IsValid;
    }

    /// <summary>The text with any size removed; two requests that differ only in size ask for the same content.</summary>
    public static string RemoveSizes(string? text) => string.IsNullOrWhiteSpace(text) ? "" : string.Join(' ', Pattern().Replace(text, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static double UnitFactor(string unit) => unit.ToLowerInvariant() switch { "cm" => 10d, "m" => 1000d, _ => 1d };

    private static double Number(string value) => double.Parse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"(?<![\w.,])(?<w>\d+(?:[.,]\d+)?)\s*(?<u1>mm|cm|m)?\s*[x×*]\s*(?<h>\d+(?:[.,]\d+)?)\s*(?<u2>mm|cm|m)?(?![\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>A rectangle in 0..1 coordinates of the reference canvas (origin top-left, y downwards).</summary>
public sealed record NormalizedBounds(double X, double Y, double Width, double Height)
{
    [JsonIgnore]
    public double CenterX => X + (Width / 2);

    [JsonIgnore]
    public double CenterY => Y + (Height / 2);

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height) &&
        X >= -Tolerance && Y >= -Tolerance && Width > 0 && Height >= 0 && X + Width <= 1 + Tolerance && Y + Height <= 1 + Tolerance;

    public const double Tolerance = 0.02;

    /// <summary>Clamps small overshoots (within <see cref="Tolerance"/>) back onto the canvas.</summary>
    public NormalizedBounds Clamp()
    {
        var x = Math.Clamp(X, 0, 1);
        var y = Math.Clamp(Y, 0, 1);
        return new NormalizedBounds(x, y, Math.Min(Width, 1 - x), Math.Min(Height, 1 - y));
    }
}

public sealed record NormalizedPoint(double X, double Y);

public enum ReferenceElementKind
{
    Unknown,
    Text,
    Rectangle,
    Ellipse,
    Line,
    Curve,
    Image,
    Table,
    Logo,
    Icon,
    Polygon,
    Arrow,
    Pictogram,
    Photo,
    ProhibitionSign,
    Group,
}

/// <summary>How an analysed element should be rebuilt in the design application.</summary>
public enum ReconstructionStrategy
{
    /// <summary>Draw it with native shapes (rectangle, ellipse, line, polygon).</summary>
    NativeShape,

    /// <summary>Editable text.</summary>
    Text,

    /// <summary>A native, editable table.</summary>
    Table,

    /// <summary>Import the reference's own vector content.</summary>
    ReuseVector,

    /// <summary>Use a matching file from the asset library.</summary>
    UseAsset,

    /// <summary>Place the cropped bitmap from the reference — a last resort for a component, never for the whole design.</summary>
    ImportImage,

    /// <summary>The user must supply a clean source file; a placeholder keeps the layout.</summary>
    NeedsUserAsset,

    /// <summary>Too complex to rebuild reliably; a placeholder keeps the layout.</summary>
    UnsupportedComplexArtwork,
}

/// <summary>How sure the font choice is. "Exact" is only claimed for an installed font the analysis was confident about.</summary>
public enum FontMatchKind
{
    Exact,
    Likely,
    Fallback,
}

public enum ReferenceTextDirection
{
    LeftToRight,
    RightToLeft,
}

public sealed record ReferenceTable
{
    public int Rows { get; init; }
    public int Columns { get; init; }

    /// <summary>Cell texts row by row; missing cells are empty.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Cells { get; init; } = [];

    public string? CellAlignment { get; init; }
    public bool HasHeaderRow { get; init; }

    /// <summary>Free-text note about merged cells the analyzer noticed; merged cells are not reproduced automatically.</summary>
    public string? MergedCellsNote { get; init; }
}

/// <summary>One thing found in a reference. Geometry is normalised so it stays valid before the real size is known.</summary>
public sealed record ReferenceElement
{
    /// <summary>Stable id within one analysis: ref_001, ref_002, …</summary>
    public string Id { get; init; } = "";

    public ReferenceElementKind Kind { get; init; }

    /// <summary>Short human label ("Başlık", "Logo", "Yasak işareti").</summary>
    public string? Label { get; init; }

    public NormalizedBounds Bounds { get; init; } = new(0, 0, 1, 1);

    /// <summary>Larger is nearer the viewer.</summary>
    public int ZIndex { get; init; }

    /// <summary>Id of the group element this belongs to.</summary>
    public string? ParentId { get; init; }

    public double RotationDegrees { get; init; }
    public string? FillColor { get; init; }
    public string? OutlineColor { get; init; }

    /// <summary>Outline width as a fraction of the canvas's shorter side.</summary>
    public double? OutlineWidthRatio { get; init; }

    /// <summary>Corner radius as a fraction of the canvas's shorter side.</summary>
    public double? CornerRadiusRatio { get; init; }

    // Text
    public string? Text { get; init; }
    public double? TextConfidence { get; init; }

    /// <summary>True when the text could not be read reliably; it must be checked by a person.</summary>
    public bool TextUncertain { get; init; }

    public string? FontFamilyGuess { get; init; }
    public double? FontConfidence { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public string? TextAlignment { get; init; }
    public ReferenceTextDirection Direction { get; init; }

    // Geometry beyond the bounding box
    public NormalizedPoint? LineStart { get; init; }
    public NormalizedPoint? LineEnd { get; init; }
    public IReadOnlyList<NormalizedPoint> Points { get; init; } = [];

    public ReferenceTable? Table { get; init; }
    public ReconstructionStrategy Strategy { get; init; }

    /// <summary>Words that may match an asset in the library ("acme logo", "no smoking").</summary>
    public string? AssetHint { get; init; }

    /// <summary>0..1; how sure the analyzer is about this element as a whole.</summary>
    public double Confidence { get; init; } = 1;

    /// <summary>What in the reference this was read from, for review ("large red text near the bottom").</summary>
    public string? Evidence { get; init; }
}

/// <summary>What an analyzer (file-based, or AI vision) learned about a reference.</summary>
public sealed record ReferenceAnalysis
{
    public required string ReferenceId { get; init; }
    public required string AnalyzerName { get; init; }
    public ReferenceFileType FileType { get; init; }
    public string? FileName { get; init; }
    public long FileSizeBytes { get; init; }

    /// <summary>The page that was analysed (1-based) and how many the file has.</summary>
    public int PageNumber { get; init; } = 1;
    public int PageCount { get; init; } = 1;

    /// <summary>Real size, only when the source reliably states one (vector/PDF/CDR page). Never derived from pixels.</summary>
    public PhysicalSize? PhysicalSize { get; init; }

    public int? PixelWidth { get; init; }
    public int? PixelHeight { get; init; }

    /// <summary>Width divided by height of the analysed canvas.</summary>
    public double? AspectRatio { get; init; }

    public string? BackgroundColor { get; init; }

    /// <summary>True when the file's own geometry can be imported and edited instead of redrawn.</summary>
    public bool CanReuseVectorContent { get; init; }

    public IReadOnlyList<ReferenceElement> Elements { get; init; } = [];
    public IReadOnlyList<string> Colors { get; init; } = [];
    public IReadOnlyList<string> Fonts { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Changes the user asked for that were already applied to this description ("YASAKTIR → GİRİLMEZ").</summary>
    public IReadOnlyList<string> AppliedModifications { get; init; } = [];

    public string? Summary { get; init; }

    /// <summary>0..1 overall.</summary>
    public double? Confidence { get; init; }

    public ReferenceElement? FindElement(string id) =>
        Elements.FirstOrDefault(element => string.Equals(element.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string ElementId(int number) => "ref_" + number.ToString("000", CultureInfo.InvariantCulture);
}

/// <summary>A normalised picture of one reference page, ready to show to a vision model.</summary>
public sealed record ReferencePreview
{
    public required string ReferenceId { get; init; }
    public required string FileName { get; init; }
    public int PageNumber { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public required int WidthPixels { get; init; }
    public required int HeightPixels { get; init; }
    public required string MimeType { get; init; }

    /// <summary>Encoded image bytes. Kept in memory; never logged.</summary>
    [JsonIgnore]
    public byte[] Bytes { get; init; } = [];

    /// <summary>Pixel size of the untouched original.</summary>
    public int OriginalWidthPixels { get; init; }
    public int OriginalHeightPixels { get; init; }

    /// <summary>Only when the source reliably states it (PDF/SVG/CDR page size).</summary>
    public PhysicalSize? PhysicalSize { get; init; }
}

public sealed record ReferencePreviewOptions
{
    /// <summary>Long edge of the preview. Large enough to keep sign text readable, small enough to be cheap.</summary>
    public int MaxLongEdgePixels { get; init; } = 1568;

    /// <summary>1-based page for multi-page sources.</summary>
    public int PageNumber { get; init; } = 1;
}

/// <summary>Turns a reference file into a preview image. Implementations live outside the Domain.</summary>
public interface IReferencePreviewRenderer
{
    bool CanRender(ReferenceInput reference);

    /// <exception cref="ReferencePreviewException">The file cannot be read or rendered.</exception>
    Task<ReferencePreview> RenderAsync(ReferenceInput reference, ReferencePreviewOptions options, CancellationToken cancellationToken = default);
}

public sealed class ReferencePreviewException(string userMessage, Exception? innerException = null) : Exception(userMessage, innerException);

/// <summary>Cuts one element out of a reference bitmap into a temporary file, for the last-resort ImportImage strategy.</summary>
public interface IReferenceImageCropper
{
    /// <returns>Path of the cropped image, or <c>null</c> when the reference cannot be cropped.</returns>
    string? Crop(ReferenceInput reference, NormalizedBounds bounds, string elementId);
}
