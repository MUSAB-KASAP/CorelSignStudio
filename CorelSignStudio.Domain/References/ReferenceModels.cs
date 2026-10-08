using CorelSignStudio.Domain.Automation;

namespace CorelSignStudio.Domain.References;

public enum ReferenceFileType
{
    Unknown,
    Cdr,
    Pdf,
    Svg,
    Jpeg,
    Png,
}

public enum ReferenceRole
{
    /// <summary>"Make it look like this."</summary>
    VisualReference,

    /// <summary>Content to place in the design (logo, icon, photo).</summary>
    Content,

    /// <summary>An existing design to open and modify.</summary>
    SourceDocument,
}

public static class ReferenceFileTypes
{
    public static IReadOnlyList<string> SupportedExtensions { get; } = [".cdr", ".pdf", ".svg", ".jpg", ".jpeg", ".png"];

    public static ReferenceFileType Detect(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cdr" => ReferenceFileType.Cdr,
        ".pdf" => ReferenceFileType.Pdf,
        ".svg" => ReferenceFileType.Svg,
        ".jpg" or ".jpeg" => ReferenceFileType.Jpeg,
        ".png" => ReferenceFileType.Png,
        _ => ReferenceFileType.Unknown,
    };

    public static bool IsSupported(string path) => Detect(path) != ReferenceFileType.Unknown;

    /// <summary>Vector formats carry editable geometry, so they can be reused instead of redrawn.</summary>
    public static bool IsVector(ReferenceFileType type) =>
        type is ReferenceFileType.Cdr or ReferenceFileType.Pdf or ReferenceFileType.Svg;
}

/// <summary>A file the user attached to a request.</summary>
public sealed record ReferenceInput
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string FilePath { get; init; }
    public ReferenceFileType FileType { get; init; }
    public ReferenceRole Role { get; init; } = ReferenceRole.VisualReference;
    public string? Notes { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string FileName => Path.GetFileName(FilePath);

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsVector => ReferenceFileTypes.IsVector(FileType);

    public static ReferenceInput FromFile(string path, ReferenceRole role = ReferenceRole.VisualReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var type = ReferenceFileTypes.Detect(path);
        if (type == ReferenceFileType.Unknown)
        {
            throw new NotSupportedException(
                $"'{Path.GetFileName(path)}' is not a supported reference. Supported: {string.Join(", ", ReferenceFileTypes.SupportedExtensions)}.");
        }

        return new ReferenceInput { FilePath = Path.GetFullPath(path), FileType = type, Role = role };
    }
}

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
}

/// <summary>One thing an analyzer found in a reference, in millimetres from the top-left corner.</summary>
public sealed record ReferenceElement
{
    public ReferenceElementKind Kind { get; init; }
    public double XMm { get; init; }
    public double YMm { get; init; }
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
    public string? Text { get; init; }
    public string? FontFamily { get; init; }
    public double? FontSizePt { get; init; }
    public string? FillColor { get; init; }
    public string? OutlineColor { get; init; }

    /// <summary>0..1; how sure the analyzer is.</summary>
    public double Confidence { get; init; } = 1;
}

/// <summary>What an analyzer (file-based today, AI vision later) learned about a reference.</summary>
public sealed record ReferenceAnalysis
{
    public required string ReferenceId { get; init; }
    public required string AnalyzerName { get; init; }
    public ReferenceFileType FileType { get; init; }
    public long FileSizeBytes { get; init; }
    public double? WidthMm { get; init; }
    public double? HeightMm { get; init; }
    public int? PixelWidth { get; init; }
    public int? PixelHeight { get; init; }

    /// <summary>True when the file's own geometry can be imported and edited instead of redrawn.</summary>
    public bool CanReuseVectorContent { get; init; }

    public IReadOnlyList<ReferenceElement> Elements { get; init; } = [];
    public IReadOnlyList<string> Colors { get; init; } = [];
    public IReadOnlyList<string> Fonts { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Extension point for file inspection, OCR and AI vision.</summary>
public interface IReferenceAnalyzer
{
    string Name { get; }

    bool CanAnalyze(ReferenceInput reference);

    Task<ReferenceAnalysis> AnalyzeAsync(ReferenceInput reference, CancellationToken cancellationToken = default);
}

/// <summary>Extension point that turns an analysis into an executable plan.</summary>
public interface IReferencePlanBuilder
{
    AutomationPlan BuildPlan(ReferenceInput reference, ReferenceAnalysis analysis);
}

/// <summary>
/// The no-AI baseline: vector references are imported as editable objects, bitmaps are placed as
/// a tracing backdrop. A future AI builder replaces this for "recreate this image" requests.
/// </summary>
public sealed class ImportReferencePlanBuilder : IReferencePlanBuilder
{
    public AutomationPlan BuildPlan(ReferenceInput reference, ReferenceAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(analysis);

        var actions = new List<CorelAction>();
        if (reference.FileType == ReferenceFileType.Cdr && reference.Role == ReferenceRole.SourceDocument)
        {
            actions.Add(new OpenDocumentAction { Id = "open", FilePath = reference.FilePath });
        }
        else
        {
            actions.Add(new ImportFileAction
            {
                Id = "import",
                FilePath = reference.FilePath,
                Name = analysis.CanReuseVectorContent ? "Reference (editable)" : "Reference (bitmap)",
            });
        }

        return new AutomationPlan
        {
            Name = $"Use reference {reference.FileName}",
            Description = analysis.CanReuseVectorContent
                ? "Reuses the reference's own vector objects instead of redrawing them."
                : "Places the bitmap reference in the document as a backdrop.",
            Target = actions[0].OpensDocument ? DocumentTarget.NewDocument : DocumentTarget.ActiveDocument,
            Actions = actions,
            ReferenceFiles = [reference],
        };
    }
}
