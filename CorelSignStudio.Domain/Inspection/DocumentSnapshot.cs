using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CorelSignStudio.Domain.Inspection;

/// <summary>
/// Application-neutral picture of a document that is open in the design application.
/// Coordinates are millimetres measured from the top-left corner of the page
/// (X grows to the right, Y grows downwards), matching <see cref="DesignSpec"/>.
/// </summary>
public sealed record DocumentSnapshot
{
    public required string Title { get; init; }
    public string? FilePath { get; init; }
    public string Unit { get; init; } = "mm";
    public int ActivePageIndex { get; init; } = 1;
    public IReadOnlyList<PageSnapshot> Pages { get; init; } = [];
    public IReadOnlyList<string> SelectedShapeIds { get; init; } = [];
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public PageSnapshot? ActivePage => Pages.FirstOrDefault(page => page.Index == ActivePageIndex) ?? Pages.FirstOrDefault();

    /// <summary>Every shape in the document, including shapes nested inside groups.</summary>
    public IEnumerable<ShapeSnapshot> AllShapes() => Pages.SelectMany(page => page.AllShapes());

    [System.Text.Json.Serialization.JsonIgnore]
    public int ShapeCount => AllShapes().Count();

    public ShapeSnapshot? FindShape(string logicalId) =>
        AllShapes().FirstOrDefault(shape => string.Equals(shape.Id, logicalId, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ShapeSnapshot> FindShapesByName(string name) =>
        AllShapes().Where(shape => string.Equals(shape.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Plain-text rendering used by logs, the UI and (later) AI planner prompts.</summary>
    public string ToText()
    {
        var builder = new StringBuilder();
        builder.Append("Document: ").AppendLine(Title);
        if (!string.IsNullOrWhiteSpace(FilePath))
        {
            builder.Append("File: ").AppendLine(FilePath);
        }

        foreach (var page in Pages)
        {
            builder.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Page {page.Index}: {page.WidthMm:0.##} x {page.HeightMm:0.##} mm"));
            foreach (var layer in page.Layers)
            {
                builder.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  Layer: {layer.Name} ({layer.ShapeCount} object(s){(layer.Visible ? "" : ", hidden")}{(layer.Locked ? ", locked" : "")})"));
            }

            foreach (var shape in page.Shapes)
            {
                AppendShape(builder, shape, 0);
            }
        }

        return builder.ToString();
    }

    private static void AppendShape(StringBuilder builder, ShapeSnapshot shape, int depth)
    {
        var indent = new string(' ', depth * 2);
        builder.AppendLine();
        builder.Append(indent).AppendLine(shape.Id);
        builder.Append(indent).Append("Type: ").AppendLine(shape.Type.ToString());
        if (!string.IsNullOrWhiteSpace(shape.Name))
        {
            builder.Append(indent).Append("Name: ").AppendLine(shape.Name);
        }

        if (shape.Text is not null)
        {
            builder.Append(indent).Append("Text: ").AppendLine(shape.Text.ReplaceLineEndings(" / "));
        }

        builder.Append(indent).AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"X: {shape.Bounds.XMm:0.##}  Y: {shape.Bounds.YMm:0.##}  Width: {shape.Bounds.WidthMm:0.##}  Height: {shape.Bounds.HeightMm:0.##}"));
        if (Math.Abs(shape.RotationDegrees) > 0.005)
        {
            builder.Append(indent).AppendLine(string.Create(CultureInfo.InvariantCulture, $"Rotation: {shape.RotationDegrees:0.##}"));
        }

        builder.Append(indent).Append("Layer: ").AppendLine(shape.LayerName);
        foreach (var child in shape.Children)
        {
            AppendShape(builder, child, depth + 1);
        }
    }
}

public sealed record PageSnapshot
{
    public required int Index { get; init; }
    public string? Name { get; init; }
    public required double WidthMm { get; init; }
    public required double HeightMm { get; init; }
    public IReadOnlyList<LayerSnapshot> Layers { get; init; } = [];

    /// <summary>Top-level shapes in front-to-back order; grouped shapes are in <see cref="ShapeSnapshot.Children"/>.</summary>
    public IReadOnlyList<ShapeSnapshot> Shapes { get; init; } = [];

    public IEnumerable<ShapeSnapshot> AllShapes() => Shapes.SelectMany(shape => shape.SelfAndDescendants());
}

public sealed record LayerSnapshot
{
    public required string Name { get; init; }
    public bool Visible { get; init; } = true;
    public bool Locked { get; init; }
    public bool Printable { get; init; } = true;
    public int ShapeCount { get; init; }
}

public enum ShapeKind
{
    Unknown,
    Rectangle,
    Ellipse,
    Curve,
    Polygon,
    Bitmap,
    ArtisticText,
    ParagraphText,
    Group,
    Table,
    Symbol,
    Guideline,
    Other,
}

/// <summary>Pixel dimensions of a placed bitmap, as reported by the design application.</summary>
public sealed record BitmapInfo(int PixelWidth, int PixelHeight)
{
    /// <summary>dpi = pixels / (millimetres / 25.4); null when either value is unknown.</summary>
    public static double? EffectiveDpi(int pixels, double millimetres) =>
        pixels > 0 && millimetres > 0 ? pixels / (millimetres / 25.4) : null;
}

public sealed record ShapeSnapshot
{
    /// <summary>Stable logical id (for example <c>shape_017</c>); see <see cref="LogicalShapeId"/>.</summary>
    public required string Id { get; init; }

    /// <summary>The design application's own persistent object id the logical id is derived from.</summary>
    public int NativeId { get; init; }

    public ShapeKind Type { get; init; }

    /// <summary>The design application's own type name, kept for diagnostics.</summary>
    public string? NativeType { get; init; }

    public string? Name { get; init; }
    public string? Text { get; init; }
    public string? FontFamily { get; init; }
    public double? FontSizePt { get; init; }
    public required BoundsMm Bounds { get; init; }
    public double RotationDegrees { get; init; }

    /// <summary>Only for bitmaps, and only when the pixel size could be read.</summary>
    public BitmapInfo? Bitmap { get; init; }

    /// <summary>
    /// Resolution of a bitmap at the size it is placed. Unknown (null) without pixel dimensions, and for
    /// a bitmap rotated off the axes, whose bounding box is not its placed size.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public (double X, double Y)? EffectiveDpi
    {
        get
        {
            if (Bitmap is null)
            {
                return null;
            }

            var quarterTurns = RotationDegrees / 90;
            if (Math.Abs(quarterTurns - Math.Round(quarterTurns)) > 0.01)
            {
                return null;
            }

            var sideways = ((int)Math.Round(quarterTurns) % 2) != 0;
            var x = BitmapInfo.EffectiveDpi(Bitmap.PixelWidth, sideways ? Bounds.HeightMm : Bounds.WidthMm);
            var y = BitmapInfo.EffectiveDpi(Bitmap.PixelHeight, sideways ? Bounds.WidthMm : Bounds.HeightMm);
            return x is null || y is null ? null : (x.Value, y.Value);
        }
    }
    public FillInfo? Fill { get; init; }
    public OutlineInfo? Outline { get; init; }
    public required string LayerName { get; init; }
    public int PageIndex { get; init; } = 1;

    /// <summary>Logical id of the group that directly contains this shape, when it is grouped.</summary>
    public string? ParentGroupId { get; init; }

    public IReadOnlyList<ShapeSnapshot> Children { get; init; } = [];
    public bool Visible { get; init; } = true;
    public bool Locked { get; init; }

    /// <summary>Front-to-back order within the page (1 = frontmost).</summary>
    public int Order { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsText => Type is ShapeKind.ArtisticText or ShapeKind.ParagraphText;

    public IEnumerable<ShapeSnapshot> SelfAndDescendants()
    {
        yield return this;
        foreach (var descendant in Children.SelectMany(child => child.SelfAndDescendants()))
        {
            yield return descendant;
        }
    }
}

/// <summary>Bounding box in millimetres; X/Y is the top-left corner measured from the page's top-left corner.</summary>
public sealed record BoundsMm(double XMm, double YMm, double WidthMm, double HeightMm)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public double RightMm => XMm + WidthMm;

    [System.Text.Json.Serialization.JsonIgnore]
    public double BottomMm => YMm + HeightMm;

    [System.Text.Json.Serialization.JsonIgnore]
    public double CenterXMm => XMm + (WidthMm / 2);

    [System.Text.Json.Serialization.JsonIgnore]
    public double CenterYMm => YMm + (HeightMm / 2);
}

public enum FillKind
{
    None,
    Uniform,
    Fountain,
    Pattern,
    Texture,
    Other,
}

public sealed record FillInfo(FillKind Kind, string? ColorHex = null);

public sealed record OutlineInfo(bool HasOutline, string? ColorHex = null, double WidthMm = 0);

/// <summary>
/// Logical shape ids are derived from the design application's persistent per-document object id,
/// so the same object keeps the same id across inspections and across save/reopen.
/// </summary>
public static partial class LogicalShapeId
{
    public const string Prefix = "shape_";

    public static string FromNativeId(int nativeId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nativeId);
        return Prefix + nativeId.ToString("000", CultureInfo.InvariantCulture);
    }

    public static bool IsLogicalId(string? value) => value is not null && Pattern().IsMatch(value);

    public static bool TryGetNativeId(string? value, out int nativeId)
    {
        nativeId = 0;
        return IsLogicalId(value) &&
               int.TryParse(value.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out nativeId) &&
               nativeId > 0;
    }

    [GeneratedRegex(@"^shape_\d{1,9}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

/// <summary>Reads the document that is currently active in the design application.</summary>
public interface ICorelDocumentInspector
{
    /// <returns>The snapshot, or <c>null</c> when no document is open.</returns>
    Task<DocumentSnapshot?> InspectActiveDocumentAsync(CancellationToken cancellationToken = default);
}
