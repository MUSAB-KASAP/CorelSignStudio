using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.References;

namespace CorelSignStudio.Domain.Production;

public sealed record PreflightRequest
{
    /// <summary>The document as inspected just before production.</summary>
    public DocumentSnapshot? Document { get; init; }

    /// <summary>The size the job is supposed to have, when known.</summary>
    public PhysicalSize? ExpectedSize { get; init; }

    /// <summary>The analysis the design came from, when it was rebuilt from a reference.</summary>
    public ReferenceAnalysis? Analysis { get; init; }

    /// <summary>Notes from reconstruction that matter for production (approximate fonts, cropped bitmaps…).</summary>
    public IReadOnlyList<string> ReconstructionWarnings { get; init; } = [];

    /// <summary>Files about to be written.</summary>
    public IReadOnlyList<string> OutputPaths { get; init; } = [];

    /// <summary>Files the job reads (imports, assets).</summary>
    public IReadOnlyList<string> ReferencedFiles { get; init; } = [];

    /// <summary>Fonts available on this computer; <c>null</c> skips the font check.</summary>
    public IReadOnlyCollection<string>? InstalledFonts { get; init; }
}

public sealed record PreflightResult
{
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> Info { get; init; } = [];

    /// <summary>Errors block final production; warnings may be accepted by the user.</summary>
    public bool CanProduce => Errors.Count == 0;

    public bool IsClean => Errors.Count == 0 && Warnings.Count == 0;
}

public interface IDesignPreflightService
{
    PreflightResult Check(PreflightRequest request);
}

/// <summary>
/// Last look before files are written. It reports only what it can actually determine from the inspected
/// document and the job; anything it cannot measure (for example bitmap resolution, which the snapshot
/// does not carry) is left out rather than guessed.
/// </summary>
public sealed class DesignPreflightService(Func<string, bool>? fileExists = null, Func<string, bool>? directoryWritable = null) : IDesignPreflightService
{
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly Func<string, bool> _directoryWritable = directoryWritable ?? IsWritable;

    public PreflightResult Check(PreflightRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        var warnings = new List<string>();
        var info = new List<string>();

        var page = request.Document?.ActivePage;
        if (request.Document is null || page is null)
        {
            errors.Add(Msg.Get("Preflight.NoDocument"));
        }
        else
        {
            info.Add(Msg.Format("Preflight.Page", Msg.Number(page.WidthMm), Msg.Number(page.HeightMm), request.Document.ShapeCount));
            if (page.WidthMm <= 0 || page.HeightMm <= 0)
            {
                errors.Add(Msg.Get("Preflight.PageInvalid"));
            }

            if (request.ExpectedSize is { IsValid: true } expected &&
                (Math.Abs(page.WidthMm - expected.WidthMm) > 0.5 || Math.Abs(page.HeightMm - expected.HeightMm) > 0.5))
            {
                errors.Add(Msg.Format("Preflight.PageMismatch", Msg.Number(page.WidthMm), Msg.Number(page.HeightMm), Msg.Number(expected.WidthMm), Msg.Number(expected.HeightMm)));
            }

            var placeholderPrefix = Msg.Format("Reconstruct.Placeholder", "");
            foreach (var shape in page.AllShapes())
            {
                var name = string.IsNullOrWhiteSpace(shape.Name) ? shape.Id : $"{shape.Name} ({shape.Id})";
                if (shape.Name is not null && shape.Name.StartsWith(placeholderPrefix, StringComparison.Ordinal))
                {
                    errors.Add(Msg.Format("Preflight.Placeholder", shape.Name[placeholderPrefix.Length..]));
                    continue;
                }

                if (shape.Type == ShapeKind.Group)
                {
                    continue;
                }

                var bounds = shape.Bounds;
                var lineLike = shape.Type == ShapeKind.Curve;
                if ((bounds.WidthMm <= 0 && bounds.HeightMm <= 0) || (!lineLike && (bounds.WidthMm <= 0 || bounds.HeightMm <= 0)))
                {
                    warnings.Add(Msg.Format("Preflight.ZeroSize", name));
                }
                else if (bounds.XMm < -0.5 || bounds.YMm < -0.5 || bounds.RightMm > page.WidthMm + 0.5 || bounds.BottomMm > page.HeightMm + 0.5)
                {
                    warnings.Add(Msg.Format(shape.IsText ? "Preflight.TextOutsidePage" : "Preflight.OutsidePage", name));
                }

                if (shape.IsText && string.IsNullOrWhiteSpace(shape.Text))
                {
                    warnings.Add(Msg.Format("Preflight.EmptyText", name));
                }

                if (shape.IsText && request.InstalledFonts is { } fonts && shape.FontFamily is { Length: > 0 } font &&
                    !fonts.Contains(font, StringComparer.OrdinalIgnoreCase))
                {
                    warnings.Add(Msg.Format("Preflight.FontMissing", name, font));
                }
            }
        }

        if (request.Analysis is { } analysis)
        {
            foreach (var element in analysis.Elements.Where(element => element.TextUncertain))
            {
                warnings.Add(Msg.Format("Preflight.UncertainText", element.Text?.ReplaceLineEndings(" ") ?? element.Id));
            }
        }

        warnings.AddRange(request.ReconstructionWarnings);

        foreach (var file in request.ReferencedFiles.Distinct(StringComparer.OrdinalIgnoreCase).Where(file => !Safe(_fileExists, file)))
        {
            errors.Add(Msg.Format("Preflight.MissingFile", file));
        }

        foreach (var duplicate in request.OutputPaths.GroupBy(path => path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            errors.Add(Msg.Format("Preflight.DuplicateOutput", duplicate.Key));
        }

        foreach (var folder in request.OutputPaths.Select(Path.GetDirectoryName).Where(folder => !string.IsNullOrEmpty(folder)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Safe(_directoryWritable, folder!))
            {
                errors.Add(Msg.Format("Preflight.NotWritable", folder));
            }
        }

        foreach (var existing in request.OutputPaths.Distinct(StringComparer.OrdinalIgnoreCase).Where(path => Safe(_fileExists, path)))
        {
            warnings.Add(Msg.Format("Preflight.Overwrite", existing));
        }

        return new PreflightResult
        {
            Errors = errors.Distinct(StringComparer.Ordinal).ToArray(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            Info = info,
        };
    }

    /// <summary>The files a plan reads and writes, for a preflight of that plan.</summary>
    public static (IReadOnlyList<string> Outputs, IReadOnlyList<string> Inputs) FilesOf(AutomationPlan plan) =>
    (
        plan.Actions.OfType<OutputAction>().Select(action => action.FilePath).ToArray(),
        plan.Actions.OfType<ImportFileAction>().Select(action => action.FilePath)
            .Concat(plan.Actions.OfType<OpenDocumentAction>().Select(action => action.FilePath)).ToArray()
    );

    private static bool Safe(Func<string, bool> check, string path)
    {
        try
        {
            return check(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
