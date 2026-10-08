using System.Diagnostics;
using System.Globalization;
using CorelSignStudio.Domain.Ai;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Localization;

namespace CorelSignStudio.Domain.References;

/// <summary>
/// What one object of a reconstruction should look like in the document. Derived from the reconstruction
/// plan itself, so "expected" always means exactly what the analysis asked for, in millimetres.
/// </summary>
public sealed record ExpectedObject
{
    /// <summary>The object name the plan gave the shape; this is how the shape is found again.</summary>
    public required string ShapeName { get; init; }

    public required string ActionId { get; init; }
    public double CenterXMm { get; init; }
    public double CenterYMm { get; init; }
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }

    /// <summary>False for text: its height depends on the font, so only width and position are compared.</summary>
    public bool CompareHeight { get; init; } = true;

    public string? Text { get; init; }
    public string? FillColor { get; init; }
    public string? OutlineColor { get; init; }
    public bool IsPlaceholder { get; init; }
}

public static class ExpectedObjects
{
    /// <summary>Reads the expected geometry of every named shape a reconstruction plan creates.</summary>
    public static IReadOnlyList<ExpectedObject> FromPlan(AutomationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var placeholderPrefix = Msg.Format("Reconstruct.Placeholder", "");
        var result = new List<ExpectedObject>();
        foreach (var action in plan.Actions.OfType<CreateShapeAction>().Where(action => !string.IsNullOrWhiteSpace(action.Name)))
        {
            var expected = action switch
            {
                CreateRectangleAction a => Box(a, a.XMm, a.YMm, a.WidthMm, a.HeightMm),
                CreateEllipseAction a => Box(a, a.XMm, a.YMm, a.WidthMm, a.HeightMm),
                CreateTableAction a => Box(a, a.XMm, a.YMm, a.WidthMm, a.HeightMm),
                CreateLineAction a => Box(a, Math.Min(a.X1Mm, a.X2Mm), Math.Min(a.Y1Mm, a.Y2Mm), Math.Abs(a.X2Mm - a.X1Mm), Math.Abs(a.Y2Mm - a.Y1Mm)) with { FillColor = null },
                CreatePolygonAction a => Box(
                    a,
                    a.PointsMm.Min(point => point[0]),
                    a.PointsMm.Min(point => point[1]),
                    a.PointsMm.Max(point => point[0]) - a.PointsMm.Min(point => point[0]),
                    a.PointsMm.Max(point => point[1]) - a.PointsMm.Min(point => point[1])),
                CreateTextAction a => Text(a, plan),
                _ => null,
            };
            if (expected is not null)
            {
                result.Add(expected with { IsPlaceholder = action.Name!.StartsWith(placeholderPrefix, StringComparison.Ordinal) });
            }
        }

        return result;
    }

    private static ExpectedObject Box(CreateShapeAction action, double x, double y, double width, double height) => new()
    {
        ShapeName = action.Name!,
        ActionId = action.Id,
        CenterXMm = x + (width / 2),
        CenterYMm = y + (height / 2),
        WidthMm = width,
        HeightMm = height,
        FillColor = action.FillColor,
        OutlineColor = action.OutlineColor,
    };

    private static ExpectedObject? Text(CreateTextAction action, AutomationPlan plan)
    {
        // The planner fits each text to its analysed width; that resize carries the expected width.
        var fit = plan.Actions.OfType<ResizeAction>().FirstOrDefault(resize => resize.Targets.Contains(TargetRef.ForAction(action.Id)) && resize.WidthMm is not null);
        if (fit is null || action.Anchor != PositionAnchor.Center)
        {
            return null;
        }

        return new ExpectedObject
        {
            ShapeName = action.Name!,
            ActionId = action.Id,
            CenterXMm = action.XMm,
            CenterYMm = action.YMm,
            WidthMm = fit.WidthMm!.Value,
            CompareHeight = false,
            Text = action.Text,
            FillColor = action.FillColor,
        };
    }
}

public enum DifferenceKind
{
    PageSize,
    Missing,
    Position,
    Size,
    Text,
    FillColor,
    OutlineColor,
    Unexpected,
    Visual,
}

public enum DifferenceSeverity
{
    Info,
    Minor,
    Major,
}

/// <summary>One discrepancy between what was expected and what is in the document.</summary>
public sealed record VisualDifference
{
    public required DifferenceKind Kind { get; init; }
    public required DifferenceSeverity Severity { get; init; }

    /// <summary>User-facing sentence.</summary>
    public required string Description { get; init; }

    public string? ShapeName { get; init; }

    /// <summary>Logical id of the shape in the document, when it exists.</summary>
    public string? ShapeId { get; init; }

    /// <summary>Actual minus expected centre, in millimetres (positive X is right, positive Y is down).</summary>
    public double DeltaXMm { get; init; }
    public double DeltaYMm { get; init; }

    public double? ExpectedWidthMm { get; init; }
    public double? ExpectedHeightMm { get; init; }
    public string? ExpectedText { get; init; }
    public string? ExpectedColor { get; init; }

    /// <summary>"structure" for measured differences, "ai" for differences a vision model reported.</summary>
    public string Source { get; init; } = "structure";
}

public sealed record VisualComparisonResult
{
    /// <summary>0..1; 1 means everything expected is present and in place.</summary>
    public double Similarity { get; init; }

    public IReadOnlyList<VisualDifference> Differences { get; init; } = [];

    /// <summary>User-facing lines for things that are right ("Sayfa ölçüsü doğru").</summary>
    public IReadOnlyList<string> Matches { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>True when a vision model also looked at the two images.</summary>
    public bool AiUsed { get; init; }

    public int ComparedObjects { get; init; }

    public bool HasCorrectableDifferences => Differences.Any(difference => difference.ShapeId is not null &&
        difference.Kind is DifferenceKind.Position or DifferenceKind.Size or DifferenceKind.Text or DifferenceKind.FillColor or DifferenceKind.OutlineColor);
}

public sealed record VisualComparisonRequest
{
    public required IReadOnlyList<ExpectedObject> Expected { get; init; }
    public required DocumentSnapshot Document { get; init; }
    public required PhysicalSize Size { get; init; }

    /// <summary>The reference image and a full-page picture of the result, for the optional visual check.</summary>
    public ReferencePreview? ReferenceImage { get; init; }
    public ReferencePreview? OutputImage { get; init; }
}

/// <summary>Compares a reconstruction with what was expected of it.</summary>
public interface IVisualComparisonService
{
    Task<VisualComparisonResult> CompareAsync(VisualComparisonRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Renders the page that is open in the design application, whole page and background included.</summary>
public interface ICorelPagePreviewRenderer
{
    /// <summary>The active page as an image. The document must be left exactly as it was.</summary>
    Task<ReferencePreview> RenderActivePageAsync(int maxLongEdgePixels = 1568, CancellationToken cancellationToken = default);
}

public sealed record ComparisonTolerances
{
    /// <summary>Position differences below this are treated as equal (millimetres).</summary>
    public double PositionMm { get; init; } = 1.0;

    /// <summary>…or below this fraction of the page's shorter side, whichever is larger.</summary>
    public double PositionRatio { get; init; } = 0.004;

    /// <summary>Relative size difference treated as equal.</summary>
    public double SizeRatio { get; init; } = 0.02;

    /// <summary>RGB distance (0..441) treated as the same colour.</summary>
    public double ColorDistance { get; init; } = 28;
}

/// <summary>
/// The measurable half of the comparison: expected objects against the inspected document. Every
/// difference found here carries exact numbers, so corrections can be computed instead of guessed.
/// </summary>
public sealed class StructuralComparer(ComparisonTolerances? tolerances = null)
{
    private readonly ComparisonTolerances _tolerances = tolerances ?? new ComparisonTolerances();

    public VisualComparisonResult Compare(VisualComparisonRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = request.Document.ActivePage;
        var differences = new List<VisualDifference>();
        var matches = new List<string>();
        var pageFactor = 1.0;

        if (page is null || Math.Abs(page.WidthMm - request.Size.WidthMm) > 0.5 || Math.Abs(page.HeightMm - request.Size.HeightMm) > 0.5)
        {
            pageFactor = 0.5;
            differences.Add(new VisualDifference
            {
                Kind = DifferenceKind.PageSize,
                Severity = DifferenceSeverity.Major,
                Description = Msg.Format("Compare.PageSize", Mm(page?.WidthMm ?? 0), Mm(page?.HeightMm ?? 0), Mm(request.Size.WidthMm), Mm(request.Size.HeightMm)),
            });
        }
        else
        {
            matches.Add(Msg.Format("Compare.Ok.PageSize", Mm(page.WidthMm), Mm(page.HeightMm)));
        }

        var shapes = (page?.AllShapes() ?? []).ToList();
        var positionTolerance = Math.Max(_tolerances.PositionMm, Math.Min(request.Size.WidthMm, request.Size.HeightMm) * _tolerances.PositionRatio);
        var diagonal = Math.Sqrt((request.Size.WidthMm * request.Size.WidthMm) + (request.Size.HeightMm * request.Size.HeightMm));
        var scores = new List<double>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var expected in request.Expected)
        {
            // A group and its first member can share a name (prohibition sign); the non-group is the drawn shape.
            var shape = shapes.Where(candidate => candidate.Name == expected.ShapeName).OrderBy(candidate => candidate.Type == ShapeKind.Group ? 1 : 0).FirstOrDefault();
            if (shape is null)
            {
                scores.Add(0);
                differences.Add(new VisualDifference
                {
                    Kind = DifferenceKind.Missing, Severity = DifferenceSeverity.Major, ShapeName = expected.ShapeName,
                    Description = Msg.Format("Compare.Missing", expected.ShapeName),
                });
                continue;
            }

            used.Add(shape.Id);
            var penalty = 0.0;
            var before = differences.Count;
            var dx = shape.Bounds.CenterXMm - expected.CenterXMm;
            var dy = shape.Bounds.CenterYMm - expected.CenterYMm;
            if (Math.Abs(dx) > positionTolerance || Math.Abs(dy) > positionTolerance)
            {
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                penalty += 0.45 * Math.Min(1, distance / (diagonal * 0.08));
                differences.Add(new VisualDifference
                {
                    Kind = DifferenceKind.Position, Severity = distance > diagonal * 0.02 ? DifferenceSeverity.Major : DifferenceSeverity.Minor,
                    ShapeName = expected.ShapeName, ShapeId = shape.Id, DeltaXMm = Math.Round(dx, 2), DeltaYMm = Math.Round(dy, 2),
                    Description = Msg.Format("Compare.Position", expected.ShapeName, Offset(dx, dy, positionTolerance)),
                });
            }

            var widthRatio = expected.WidthMm > 0 ? shape.Bounds.WidthMm / expected.WidthMm : 1;
            var heightRatio = expected.CompareHeight && expected.HeightMm > 0 ? shape.Bounds.HeightMm / expected.HeightMm : 1;
            var widthOff = Math.Abs(widthRatio - 1) > _tolerances.SizeRatio && Math.Abs(shape.Bounds.WidthMm - expected.WidthMm) > 0.5;
            var heightOff = Math.Abs(heightRatio - 1) > _tolerances.SizeRatio && Math.Abs(shape.Bounds.HeightMm - expected.HeightMm) > 0.5;
            if (widthOff || heightOff)
            {
                var worst = Math.Abs(widthRatio - 1) >= Math.Abs(heightRatio - 1) ? widthRatio : heightRatio;
                penalty += 0.3 * Math.Min(1, Math.Abs(worst - 1) / 0.3);
                differences.Add(new VisualDifference
                {
                    Kind = DifferenceKind.Size, Severity = Math.Abs(worst - 1) > 0.08 ? DifferenceSeverity.Major : DifferenceSeverity.Minor,
                    ShapeName = expected.ShapeName, ShapeId = shape.Id,
                    ExpectedWidthMm = expected.WidthMm, ExpectedHeightMm = expected.CompareHeight ? expected.HeightMm : null,
                    Description = Msg.Format(worst > 1 ? "Compare.TooLarge" : "Compare.TooSmall", expected.ShapeName, Math.Round(Math.Abs(worst - 1) * 100).ToString(Msg.Culture)),
                });
            }

            if (expected.Text is not null && !string.Equals(Normalize(shape.Text), Normalize(expected.Text), StringComparison.Ordinal))
            {
                penalty += 0.3;
                differences.Add(new VisualDifference
                {
                    Kind = DifferenceKind.Text, Severity = DifferenceSeverity.Major, ShapeName = expected.ShapeName, ShapeId = shape.Id, ExpectedText = expected.Text,
                    Description = Msg.Format("Compare.Text", expected.ShapeName, Shorten(shape.Text ?? ""), Shorten(expected.Text)),
                });
            }

            if (expected.FillColor is not null && shape.Fill is { } fill && (fill.Kind != FillKind.Uniform || ColorDistance(fill.ColorHex, expected.FillColor) > _tolerances.ColorDistance))
            {
                penalty += 0.12;
                differences.Add(new VisualDifference
                {
                    Kind = DifferenceKind.FillColor, Severity = DifferenceSeverity.Minor, ShapeName = expected.ShapeName, ShapeId = shape.Id, ExpectedColor = expected.FillColor,
                    Description = Msg.Format("Compare.Fill", expected.ShapeName, fill.ColorHex ?? "—", expected.FillColor),
                });
            }

            if (expected.OutlineColor is not null && shape.Outline is { HasOutline: true } outline && ColorDistance(outline.ColorHex, expected.OutlineColor) > _tolerances.ColorDistance)
            {
                penalty += 0.1;
                differences.Add(new VisualDifference
                {
                    Kind = DifferenceKind.OutlineColor, Severity = DifferenceSeverity.Minor, ShapeName = expected.ShapeName, ShapeId = shape.Id, ExpectedColor = expected.OutlineColor,
                    Description = Msg.Format("Compare.Outline", expected.ShapeName, outline.ColorHex ?? "—", expected.OutlineColor),
                });
            }

            scores.Add(Math.Max(0, 1 - penalty));
            if (differences.Count == before)
            {
                matches.Add(Msg.Format("Compare.Ok.Object", expected.ShapeName));
            }
        }

        var expectedNames = request.Expected.Select(expected => expected.ShapeName).ToHashSet(StringComparer.Ordinal);
        var unexpected = shapes.Count(shape => shape.ParentGroupId is null && shape.Type != ShapeKind.Group && !used.Contains(shape.Id) && (shape.Name is null || !expectedNames.Contains(shape.Name)));
        if (unexpected > 0 && request.Expected.Count > 0)
        {
            differences.Add(new VisualDifference { Kind = DifferenceKind.Unexpected, Severity = DifferenceSeverity.Info, Description = Msg.Format("Compare.Unexpected", unexpected) });
        }

        return new VisualComparisonResult
        {
            // Half the average, half the worst object: one badly placed title must not hide behind five good shapes.
            Similarity = Math.Round((scores.Count == 0 ? 1 : (scores.Average() + scores.Min()) / 2) * pageFactor, 4),
            Differences = differences,
            Matches = matches,
            ComparedObjects = request.Expected.Count,
        };
    }

    public static double ColorDistance(string? first, string? second)
    {
        if (!ColorHex.IsValid(first) || !ColorHex.IsValid(second))
        {
            return double.MaxValue;
        }

        static (int R, int G, int B) Parse(string hex) =>
            (int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
             int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
             int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        var (r1, g1, b1) = Parse(first!);
        var (r2, g2, b2) = Parse(second!);
        return Math.Sqrt(((r1 - r2) * (r1 - r2)) + ((g1 - g2) * (g1 - g2)) + ((b1 - b2) * (b1 - b2)));
    }

    private static string Offset(double dx, double dy, double tolerance)
    {
        var parts = new List<string>(2);
        if (Math.Abs(dx) > tolerance)
        {
            parts.Add(Msg.Format(dx > 0 ? "Compare.Offset.Right" : "Compare.Offset.Left", Mm(Math.Abs(dx))));
        }

        if (Math.Abs(dy) > tolerance)
        {
            parts.Add(Msg.Format(dy > 0 ? "Compare.Offset.Down" : "Compare.Offset.Up", Mm(Math.Abs(dy))));
        }

        return string.Join(Msg.Get("Describe.And"), parts);
    }

    private static string Normalize(string? text) => (text ?? "").ReplaceLineEndings("\n").Trim();

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length <= 30 ? single : single[..29] + "…";
    }

    private static string Mm(double value) => Math.Round(value, 1).ToString("0.#", Msg.Culture);
}

/// <summary>
/// Hybrid comparison: measured structure first, then — when a vision model and both images are available —
/// a visual check for things measurement cannot see. The model's findings are added as differences of
/// kind <see cref="DifferenceKind.Visual"/>; they never override a measurement.
/// </summary>
public sealed class VisualComparisonService(StructuralComparer? structural = null, AiVisualComparer? visual = null) : IVisualComparisonService
{
    private readonly StructuralComparer _structural = structural ?? new StructuralComparer();

    public async Task<VisualComparisonResult> CompareAsync(VisualComparisonRequest request, CancellationToken cancellationToken = default)
    {
        var result = _structural.Compare(request);
        if (visual is null || request.ReferenceImage is not { Bytes.Length: > 0 } || request.OutputImage is not { Bytes.Length: > 0 })
        {
            return result;
        }

        var seen = await visual.CompareAsync(request, cancellationToken).ConfigureAwait(false);
        if (seen is null)
        {
            return result with { Notes = [.. result.Notes, Msg.Get("Compare.Note.VisualUnavailable")] };
        }

        return result with
        {
            AiUsed = true,
            Similarity = Math.Round((result.Similarity * 0.7) + (seen.Similarity * 0.3), 4),
            Differences = [.. result.Differences, .. seen.Differences],
            Notes = [.. result.Notes, .. seen.Notes],
        };
    }
}

// ---- Correction -----------------------------------------------------------------------------

public sealed record CorrectionPlanResult
{
    /// <summary>A validated plan of safe edits, or <c>null</c> when nothing can be corrected automatically.</summary>
    public AutomationPlan? Plan { get; init; }

    /// <summary>User-facing description of each correction in the plan.</summary>
    public IReadOnlyList<string> Corrections { get; init; } = [];

    /// <summary>Differences that need a person (a missing object, something only seen visually).</summary>
    public IReadOnlyList<string> Unresolved { get; init; } = [];
}

public interface IVisualCorrectionPlanner
{
    CorrectionPlanResult Plan(VisualComparisonResult comparison, DocumentSnapshot document);
}

/// <summary>
/// Turns measured differences into ordinary typed actions: resize to the expected size, move by the
/// measured offset, set the expected text or colour. Numbers come from measurement, never from a model's
/// guess, and nothing destructive is ever produced — a missing object is reported, not improvised.
/// </summary>
public sealed class VisualCorrectionPlanner : IVisualCorrectionPlanner
{
    public CorrectionPlanResult Plan(VisualComparisonResult comparison, DocumentSnapshot document)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(document);
        var actions = new List<CorelAction>();
        var corrections = new List<string>();
        var unresolved = new List<string>();
        var counter = 0;
        string NextId(string prefix) => $"{prefix}{++counter}";

        // Size first (anchored at the centre), then position, so a resize never disturbs a corrected position.
        foreach (var difference in comparison.Differences.OrderBy(difference => difference.Kind == DifferenceKind.Size ? 0 : 1))
        {
            if (difference.ShapeId is null || document.FindShape(difference.ShapeId) is null)
            {
                if (difference.Kind is DifferenceKind.Missing or DifferenceKind.PageSize or DifferenceKind.Visual)
                {
                    unresolved.Add(difference.Description);
                }

                continue;
            }

            string[] target = [difference.ShapeId];
            switch (difference.Kind)
            {
                case DifferenceKind.Size when difference.ExpectedWidthMm is { } width:
                    actions.Add(new ResizeAction
                    {
                        Id = NextId("fix_size"), Targets = target, WidthMm = width, HeightMm = difference.ExpectedHeightMm,
                        KeepAspectRatio = difference.ExpectedHeightMm is null, Anchor = PositionAnchor.Center,
                    });
                    corrections.Add(difference.Description);
                    break;

                case DifferenceKind.Position:
                    actions.Add(new MoveAction { Id = NextId("fix_move"), Targets = target, DeltaXMm = -difference.DeltaXMm, DeltaYMm = -difference.DeltaYMm });
                    corrections.Add(difference.Description);
                    break;

                case DifferenceKind.Text when difference.ExpectedText is { } text:
                    actions.Add(new SetTextAction { Id = NextId("fix_text"), Targets = target, Text = text });
                    corrections.Add(difference.Description);
                    break;

                case DifferenceKind.FillColor when difference.ExpectedColor is { } fill:
                    actions.Add(new SetFillAction { Id = NextId("fix_fill"), Targets = target, Color = fill });
                    corrections.Add(difference.Description);
                    break;

                case DifferenceKind.OutlineColor when difference.ExpectedColor is { } outline:
                    actions.Add(new SetOutlineAction { Id = NextId("fix_outline"), Targets = target, Color = outline });
                    corrections.Add(difference.Description);
                    break;

                case DifferenceKind.Visual:
                    unresolved.Add(difference.Description);
                    break;
            }
        }

        if (actions.Count == 0)
        {
            return new CorrectionPlanResult { Unresolved = unresolved };
        }

        var plan = new AutomationPlan
        {
            Name = Msg.Get("Correct.PlanName"),
            Description = Msg.Format("Correct.PlanDescription", actions.Count),
            Target = DocumentTarget.ActiveDocument,
            Actions = actions,
            Metadata = new Dictionary<string, string> { ["planner"] = nameof(VisualCorrectionPlanner) },
        };

        // A correction plan obeys the same rules as any other plan; an invalid or destructive one is never returned.
        return plan.Validate().IsValid && !plan.HasDestructiveActions
            ? new CorrectionPlanResult { Plan = plan, Corrections = corrections, Unresolved = unresolved }
            : new CorrectionPlanResult { Unresolved = [.. unresolved, .. corrections] };
    }
}

// ---- Bounded improvement loop ----------------------------------------------------------------

public sealed record ImprovementOptions
{
    public int MaxPasses { get; init; } = 3;
    public double TargetSimilarity { get; init; } = 0.98;

    /// <summary>A pass that improves similarity by less than this ends the loop.</summary>
    public double MinimumImprovement { get; init; } = 0.01;
}

public enum ImprovementStopReason
{
    TargetReached,
    NoSafeCorrections,
    NoImprovement,
    MaxPassesReached,
    ExecutionFailed,
    Cancelled,
    NoDocument,
}

public sealed record ImprovementPass
{
    public required int Number { get; init; }
    public required double SimilarityBefore { get; init; }
    public double SimilarityAfter { get; init; }
    public IReadOnlyList<string> Corrections { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public TimeSpan Duration { get; init; }
}

public sealed record ImprovementResult
{
    public required ImprovementStopReason StopReason { get; init; }
    public IReadOnlyList<ImprovementPass> Passes { get; init; } = [];
    public VisualComparisonResult? FinalComparison { get; init; }
    public double InitialSimilarity { get; init; }
    public double FinalSimilarity => FinalComparison?.Similarity ?? InitialSimilarity;
    public string? Error { get; init; }
}

public sealed record ImprovementRequest
{
    public required IReadOnlyList<ExpectedObject> Expected { get; init; }
    public required PhysicalSize Size { get; init; }
    public ReferencePreview? ReferenceImage { get; init; }
}

/// <summary>
/// compare → plan corrections → apply → compare again, at most <see cref="ImprovementOptions.MaxPasses"/>
/// times. It stops as soon as the target is reached, nothing safe is left to correct, a pass does not
/// help, the executor fails, or the user cancels. Every pass is one undo step in CorelDRAW.
/// </summary>
public sealed class VisualImprovementLoop(
    ICorelDocumentInspector inspector,
    ICorelActionExecutor executor,
    IVisualComparisonService comparer,
    IVisualCorrectionPlanner planner,
    ICorelPagePreviewRenderer? previewRenderer = null)
{
    public async Task<ImprovementResult> RunAsync(
        ImprovementRequest request,
        ImprovementOptions? options = null,
        IProgress<ImprovementPass>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new ImprovementOptions();
        var passes = new List<ImprovementPass>();
        var comparison = await CompareAsync(request, cancellationToken).ConfigureAwait(false);
        if (comparison is null)
        {
            return new ImprovementResult { StopReason = ImprovementStopReason.NoDocument };
        }

        var initial = comparison.Value.Result.Similarity;
        ImprovementResult Finish(ImprovementStopReason reason, string? error = null) =>
            new() { StopReason = reason, Passes = passes, FinalComparison = comparison.Value.Result, InitialSimilarity = initial, Error = error };

        for (var number = 1; ; number++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Finish(ImprovementStopReason.Cancelled);
            }

            if (comparison.Value.Result.Similarity >= options.TargetSimilarity)
            {
                return Finish(ImprovementStopReason.TargetReached);
            }

            if (number > options.MaxPasses)
            {
                return Finish(ImprovementStopReason.MaxPassesReached);
            }

            var correction = planner.Plan(comparison.Value.Result, comparison.Value.Document);
            if (correction.Plan is null)
            {
                return Finish(ImprovementStopReason.NoSafeCorrections);
            }

            var stopwatch = Stopwatch.StartNew();
            var before = comparison.Value.Result.Similarity;
            var execution = await executor.ExecuteAsync(correction.Plan, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!execution.Success)
            {
                // The executor has already rolled the failed pass back; the document is as it was before it.
                passes.Add(new ImprovementPass { Number = number, SimilarityBefore = before, SimilarityAfter = before, Corrections = correction.Corrections, Warnings = [execution.Summary], Duration = stopwatch.Elapsed });
                return Finish(execution.Status == PlanExecutionStatus.Cancelled ? ImprovementStopReason.Cancelled : ImprovementStopReason.ExecutionFailed, execution.Summary);
            }

            var next = await CompareAsync(request, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                return Finish(ImprovementStopReason.NoDocument);
            }

            comparison = next;
            var pass = new ImprovementPass
            {
                Number = number,
                SimilarityBefore = before,
                SimilarityAfter = next.Value.Result.Similarity,
                Corrections = correction.Corrections,
                Warnings = correction.Unresolved,
                Duration = stopwatch.Elapsed,
            };
            passes.Add(pass);
            progress?.Report(pass);

            if (next.Value.Result.Similarity >= options.TargetSimilarity)
            {
                return Finish(ImprovementStopReason.TargetReached);
            }

            if (next.Value.Result.Similarity - before < options.MinimumImprovement)
            {
                return Finish(ImprovementStopReason.NoImprovement);
            }
        }
    }

    private async Task<(VisualComparisonResult Result, DocumentSnapshot Document)?> CompareAsync(ImprovementRequest request, CancellationToken cancellationToken)
    {
        var document = await inspector.InspectActiveDocumentAsync(cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        ReferencePreview? output = null;
        if (previewRenderer is not null && request.ReferenceImage is { Bytes.Length: > 0 })
        {
            try
            {
                output = await previewRenderer.RenderActivePageAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (ReferencePreviewException)
            {
                // Without a picture the measured comparison still stands.
            }
        }

        var result = await comparer.CompareAsync(
            new VisualComparisonRequest { Expected = request.Expected, Document = document, Size = request.Size, ReferenceImage = request.ReferenceImage, OutputImage = output },
            cancellationToken).ConfigureAwait(false);
        return (result, document);
    }
}

/// <summary>
/// The visual half of the comparison: shows a vision model the reference and the result and asks for
/// visible discrepancies as structured data. Its findings are advisory and are reported to the user;
/// numeric corrections always come from measurement.
/// </summary>
public sealed class AiVisualComparer(Func<IAiClient?> clientFactory)
{
    public const string SystemPrompt = """
        You compare two images for a CorelDRAW automation tool. The first image is a reference design. The
        second is a reconstruction of it made from editable vector objects. Report what is visibly different.
        A separate program has already measured positions and sizes of the known objects, so concentrate on
        what measurement cannot see: wrong or missing artwork, wrong colours, wrong font weight, missing
        details, wrong stacking order, text that reads differently.

        Return one JSON object and nothing else:

        {"similarity": 0.0-1.0,
         "differences": [{"object": "name of the object from the list if it applies, else empty",
                          "severity": "info" | "minor" | "major",
                          "description": "one short sentence in Turkish that a non-designer understands"}],
         "notes": ["optional remarks in Turkish"]}

        Rules: describe only what you can see in the images. Do not propose code, macros or drawing
        commands. Do not mention pixel coordinates. If the two images match, return an empty list.
        Magenta outlined boxes in the second image are placeholders for artwork that was deliberately not
        redrawn; mention each at most once as "info".
        """;

    public async Task<VisualComparisonResult?> CompareAsync(VisualComparisonRequest request, CancellationToken cancellationToken = default)
    {
        var client = clientFactory();
        if (client is null || !client.SupportsImages || request.ReferenceImage is null || request.OutputImage is null)
        {
            return null;
        }

        try
        {
            var response = await client.CompleteAsync(
                new AiRequest
                {
                    SystemPrompt = SystemPrompt,
                    UserMessage = "Image 1 is the reference, image 2 is the reconstruction.\nKnown objects: " +
                                  string.Join("; ", request.Expected.Select(expected => expected.ShapeName)),
                    Images = [ToImage(request.ReferenceImage, "reference"), ToImage(request.OutputImage, "output")],
                    MaxOutputTokens = 4000,
                },
                cancellationToken).ConfigureAwait(false);
            return Parse(response.Content, request);
        }
        catch (AiClientException)
        {
            return null; // the measured comparison is still valid on its own
        }
    }

    /// <summary>Strictly parses the model's answer; anything malformed yields <c>null</c> rather than a guess.</summary>
    public static VisualComparisonResult? Parse(string content, VisualComparisonRequest request)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(AiPlanParser.ExtractJson(content)) is not System.Text.Json.Nodes.JsonObject root ||
                root["similarity"] is not System.Text.Json.Nodes.JsonValue similarityNode || similarityNode.GetValueKind() != System.Text.Json.JsonValueKind.Number)
            {
                return null;
            }

            var names = request.Expected.Select(expected => expected.ShapeName).ToHashSet(StringComparer.Ordinal);
            var differences = new List<VisualDifference>();
            foreach (var node in root["differences"] as System.Text.Json.Nodes.JsonArray ?? [])
            {
                if (node is not System.Text.Json.Nodes.JsonObject item || item["description"]?.ToString() is not { Length: > 0 } description)
                {
                    continue;
                }

                var name = item["object"]?.ToString();
                differences.Add(new VisualDifference
                {
                    Kind = DifferenceKind.Visual,
                    Severity = item["severity"]?.ToString()?.ToLowerInvariant() switch { "major" => DifferenceSeverity.Major, "minor" => DifferenceSeverity.Minor, _ => DifferenceSeverity.Info },
                    Description = description.Trim(),
                    ShapeName = name is not null && names.Contains(name) ? name : null,
                    Source = "ai",
                });
            }

            return new VisualComparisonResult
            {
                Similarity = Math.Clamp(similarityNode.GetValue<double>(), 0, 1),
                Differences = differences,
                Notes = (root["notes"] as System.Text.Json.Nodes.JsonArray)?.Select(note => note?.ToString()).Where(note => !string.IsNullOrWhiteSpace(note)).Select(note => note!).ToArray() ?? [],
                AiUsed = true,
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static AiImage ToImage(ReferencePreview preview, string role) => new()
    {
        FileName = $"{role}:{preview.FileName}",
        MimeType = preview.MimeType,
        Bytes = preview.Bytes,
        WidthPixels = preview.WidthPixels,
        HeightPixels = preview.HeightPixels,
    };
}
