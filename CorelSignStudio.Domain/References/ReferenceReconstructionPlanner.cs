using System.Globalization;
using CorelSignStudio.Domain.Ai;
using CorelSignStudio.Domain.Assets;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Localization;

namespace CorelSignStudio.Domain.References;

public sealed record FontResolution(string FontFamily, FontMatchKind Match);

/// <summary>Turns a font guess into a font that can actually be used, and says how good the match is.</summary>
public interface IFontResolver
{
    FontResolution Resolve(string? fontFamilyGuess, double? confidence);
}

/// <summary>
/// Resolves against the fonts installed on this computer. "Exact" is claimed only for an installed font
/// the analysis was confident about; a known look-alike is "Likely"; everything else falls back.
/// </summary>
public sealed class InstalledFontResolver(IEnumerable<string>? installedFonts = null, string fallbackFont = "Arial") : IFontResolver
{
    public const double ExactConfidenceThreshold = 0.8;

    private static readonly IReadOnlyDictionary<string, string[]> Alternatives = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Helvetica"] = ["Arial"], ["Helvetica Neue"] = ["Arial"], ["Swiss 721"] = ["Arial"], ["Liberation Sans"] = ["Arial"],
        ["DIN"] = ["Bahnschrift", "Arial"], ["DIN 1451"] = ["Bahnschrift", "Arial"], ["DIN Condensed"] = ["Bahnschrift", "Arial Narrow"],
        ["Futura"] = ["Century Gothic", "Arial"], ["Gill Sans"] = ["Gill Sans MT", "Calibri"], ["Frutiger"] = ["Segoe UI", "Calibri"],
        ["Univers"] = ["Arial"], ["Impact"] = ["Arial Black"], ["Helvetica Bold"] = ["Arial"], ["Arial Bold"] = ["Arial"],
        ["Times"] = ["Times New Roman"], ["Times Roman"] = ["Times New Roman"], ["Garamond"] = ["Georgia", "Times New Roman"],
        ["Courier"] = ["Courier New"], ["Roboto"] = ["Segoe UI", "Arial"], ["Open Sans"] = ["Segoe UI", "Arial"],
        ["Noto Naskh Arabic"] = ["Traditional Arabic", "Arial"], ["Noto Sans Arabic"] = ["Segoe UI", "Arial"],
    };

    // Keyed case-insensitively, but the value keeps the font's real spelling.
    private readonly Dictionary<string, string>? _installed = installedFonts?
        .GroupBy(font => font, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    public FontResolution Resolve(string? fontFamilyGuess, double? confidence)
    {
        var guess = fontFamilyGuess?.Trim();
        if (string.IsNullOrEmpty(guess))
        {
            return new FontResolution(fallbackFont, FontMatchKind.Fallback);
        }

        if (Installed(guess) is { } installed)
        {
            return new FontResolution(installed, (confidence ?? 0) >= ExactConfidenceThreshold ? FontMatchKind.Exact : FontMatchKind.Likely);
        }

        if (Alternatives.TryGetValue(guess, out var candidates) && candidates.Select(Installed).FirstOrDefault(font => font is not null) is { } alternative)
        {
            return new FontResolution(alternative, FontMatchKind.Likely);
        }

        return new FontResolution(fallbackFont, FontMatchKind.Fallback);
    }

    // Without a font list (tests, non-Windows) only the fallback font is assumed to exist.
    private string? Installed(string font) => _installed is null
        ? (string.Equals(font, fallbackFont, StringComparison.OrdinalIgnoreCase) ? fallbackFont : null)
        : _installed.GetValueOrDefault(font);
}

public sealed record ReconstructionRequest
{
    public required ReferenceInput Reference { get; init; }
    public required ReferenceAnalysis Analysis { get; init; }

    /// <summary>The user's words; an explicit size in them ("500x700 mm", "50x70 cm") always wins.</summary>
    public string UserRequest { get; init; } = "";

    /// <summary>A size already confirmed by the user, if any.</summary>
    public PhysicalSize? TargetSize { get; init; }

    public IReadOnlyList<Asset> Assets { get; init; } = [];
}

public sealed record ReconstructionResult
{
    public required AiPlanningStatus Status { get; init; }
    public AutomationPlan? Plan { get; init; }
    public PhysicalSize? Size { get; init; }
    public string? ClarificationQuestion { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public required string UserMessage { get; init; }

    /// <summary>Which created action rebuilds which analysed element (element id → action ids).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ElementActions { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    public bool IsReady => Status == AiPlanningStatus.Ready && Plan is not null;
}

public interface IReferenceReconstructionPlanner
{
    ReconstructionResult Plan(ReconstructionRequest request);
}

/// <summary>
/// Deterministically converts a <see cref="ReferenceAnalysis"/> into an <see cref="AutomationPlan"/> made
/// only of existing actions. No second rendering engine: rectangles, ellipses, lines, polygons, text and
/// tables become native editable objects, in back-to-front order; logos and complex artwork become an
/// asset, a cropped bitmap, or an honest placeholder; vector references are imported, not redrawn.
/// </summary>
public sealed class ReferenceReconstructionPlanner(IFontResolver? fontResolver = null, IReferenceImageCropper? cropper = null) : IReferenceReconstructionPlanner
{
    private const double PointsPerMillimetre = 72 / 25.4;

    private readonly IFontResolver _fonts = fontResolver ?? new InstalledFontResolver();

    public ReconstructionResult Plan(ReconstructionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var analysis = request.Analysis;

        // Size: what the user said wins; then what the source file reliably states; otherwise ask. Never guess from pixels.
        PhysicalSize? size = DimensionParser.TryParse(request.UserRequest, out var stated) ? stated
            : request.TargetSize is { IsValid: true } confirmed ? confirmed
            : analysis.PhysicalSize is { IsValid: true } fromFile ? fromFile
            : null;
        if (size is null)
        {
            var question = Msg.Get("Reconstruct.SizeQuestion");
            return new ReconstructionResult { Status = AiPlanningStatus.NeedsClarification, ClarificationQuestion = question, UserMessage = question };
        }

        if (analysis.Elements.Count == 0)
        {
            return new ReconstructionResult { Status = AiPlanningStatus.Invalid, UserMessage = Msg.Get("Reconstruct.NoElements"), Size = size };
        }

        var builder = new Builder(size, _fonts, cropper, request);
        builder.Actions.Add(new CreateDocumentAction { Id = "doc", WidthMm = size.WidthMm, HeightMm = size.HeightMm });

        if (analysis.AspectRatio is { } referenceAspect && referenceAspect > 0 && Math.Abs((size.AspectRatio / referenceAspect) - 1) > 0.03)
        {
            builder.Warnings.Add(Msg.Format("Reconstruct.Warning.AspectMismatch", Ratio(size.AspectRatio), Ratio(referenceAspect)));
        }

        if (analysis.CanReuseVectorContent && analysis.Elements.All(element => element.Strategy == ReconstructionStrategy.ReuseVector))
        {
            // Keep the reference's own vectors: import them at the target size instead of redrawing.
            builder.Actions.Add(new ImportFileAction
            {
                Id = "ref_001",
                FilePath = request.Reference.FilePath,
                Name = request.Reference.FileName,
                XMm = 0,
                YMm = 0,
                FitWidthMm = size.WidthMm,
                FitHeightMm = size.HeightMm,
            });
            builder.ElementActions["ref_001"] = ["ref_001"];
        }
        else
        {
            if (analysis.BackgroundColor is { } background && !string.Equals(background, "#FFFFFF", StringComparison.OrdinalIgnoreCase))
            {
                builder.Actions.Add(new CreateRectangleAction
                {
                    Id = "background", XMm = 0, YMm = 0, WidthMm = size.WidthMm, HeightMm = size.HeightMm,
                    FillColor = background, OutlineWidthMm = 0, Name = Msg.Get("Reconstruct.Background"),
                });
            }

            // Back to front: later objects are created on top, so no reordering actions are needed.
            foreach (var element in analysis.Elements.Where(element => element.Kind != ReferenceElementKind.Group).OrderBy(element => element.ZIndex))
            {
                builder.Add(element);
            }

            // Meaningful components become groups; the document as a whole never does.
            foreach (var group in analysis.Elements.Where(element => element.Kind == ReferenceElementKind.Group))
            {
                var members = analysis.Elements.Where(element => element.ParentId == group.Id)
                    .SelectMany(element => builder.ElementActions.TryGetValue(element.Id, out var ids) ? ids.TakeLast(1) : [])
                    .Select(TargetRef.ForAction).ToArray();
                if (members.Length >= 2 && members.Length < builder.CreatedObjectCount)
                {
                    builder.Actions.Add(new GroupAction { Id = group.Id + "_group", Targets = members, Name = group.Label ?? group.Id });
                }
            }
        }

        var plan = new AutomationPlan
        {
            Name = Msg.Format("Reconstruct.PlanName", request.Reference.FileName),
            Description = Msg.Format("Reconstruct.PlanDescription", Size(size), analysis.Elements.Count(element => element.Kind != ReferenceElementKind.Group)),
            UserRequest = request.UserRequest,
            Target = DocumentTarget.NewDocument,
            Actions = builder.Actions,
            ReferenceFiles = [request.Reference],
            Metadata = new Dictionary<string, string>
            {
                ["planner"] = nameof(ReferenceReconstructionPlanner),
                ["referenceId"] = request.Reference.Id,
                ["sizeMm"] = string.Create(CultureInfo.InvariantCulture, $"{size.WidthMm}x{size.HeightMm}"),
            },
        };

        var validation = plan.Validate();
        if (!validation.IsValid)
        {
            return new ReconstructionResult { Status = AiPlanningStatus.Invalid, Size = size, UserMessage = Msg.Format("Reconstruct.Invalid", string.Join("; ", validation.Errors)) };
        }

        return new ReconstructionResult
        {
            Status = AiPlanningStatus.Ready,
            Plan = plan,
            Size = size,
            Warnings = builder.Warnings.Distinct(StringComparer.Ordinal).ToList(),
            UserMessage = Msg.Format("Reconstruct.Ready", plan.Actions.Count, Size(size)),
            ElementActions = builder.ElementActions,
        };
    }

    private static string Size(PhysicalSize size) => $"{Msg.Number(size.WidthMm)} × {Msg.Number(size.HeightMm)} mm";

    private static string Ratio(double value) => value.ToString("0.00", Msg.Culture);

    private sealed class Builder(PhysicalSize size, IFontResolver fonts, IReferenceImageCropper? cropper, ReconstructionRequest request)
    {
        private readonly double _shortSide = Math.Min(size.WidthMm, size.HeightMm);

        public List<CorelAction> Actions { get; } = [];
        public List<string> Warnings { get; } = [];
        public Dictionary<string, IReadOnlyList<string>> ElementActions { get; } = new(StringComparer.Ordinal);
        public int CreatedObjectCount { get; private set; }

        public void Add(ReferenceElement element)
        {
            var before = Actions.Count;
            // Three decimals (a micrometre) keeps plans readable and free of floating-point noise.
            var x = Math.Round(element.Bounds.X * size.WidthMm, 3);
            var y = Math.Round(element.Bounds.Y * size.HeightMm, 3);
            var width = Math.Round(element.Bounds.Width * size.WidthMm, 3);
            var height = Math.Round(element.Bounds.Height * size.HeightMm, 3);
            var name = string.IsNullOrWhiteSpace(element.Label) ? element.Id : $"{element.Label} ({element.Id})";
            var outlineWidth = element.OutlineWidthRatio is { } ratio ? Math.Round(ratio * _shortSide, 2) : (double?)null;

            switch (element.Strategy)
            {
                case ReconstructionStrategy.UseAsset when FindAsset(element) is { } asset:
                    Actions.Add(new ImportFileAction { Id = element.Id, FilePath = asset.FilePath, Name = name, XMm = x, YMm = y, FitWidthMm = width, FitHeightMm = height });
                    Warnings.Add(Msg.Format("Reconstruct.Warning.AssetUsed", element.Id, Label(element), asset.Name));
                    break;

                case ReconstructionStrategy.ImportImage when cropper?.Crop(request.Reference, element.Bounds, element.Id) is { } cropped:
                    Actions.Add(new ImportFileAction { Id = element.Id, FilePath = cropped, Name = name, XMm = x, YMm = y, FitWidthMm = width, FitHeightMm = height });
                    Warnings.Add(Msg.Format("Reconstruct.Warning.ImageUsed", element.Id, Label(element)));
                    break;

                case ReconstructionStrategy.UseAsset or ReconstructionStrategy.ImportImage or ReconstructionStrategy.NeedsUserAsset:
                    Placeholder(element, x, y, width, height, "Reconstruct.Warning.NeedsAsset");
                    break;

                case ReconstructionStrategy.UnsupportedComplexArtwork:
                    Placeholder(element, x, y, width, height, "Reconstruct.Warning.Complex");
                    break;

                default:
                    AddNative(element, name, x, y, width, height, outlineWidth);
                    break;
            }

            if (Math.Abs(element.RotationDegrees) > 0.5 && Actions.Count > before && element.Kind != ReferenceElementKind.ProhibitionSign)
            {
                Actions.Add(new RotateAction { Id = element.Id + "_rotate", Targets = [TargetRef.ForAction(LastCreator(before))], AngleDegrees = element.RotationDegrees });
            }

            if (Actions.Count > before)
            {
                ElementActions[element.Id] = Actions.Skip(before).Where(action => action.CreatesObjects).Select(action => action.Id).ToArray();
                CreatedObjectCount++;
            }
        }

        private void AddNative(ReferenceElement element, string name, double x, double y, double width, double height, double? outlineWidth)
        {
            switch (element.Kind)
            {
                case ReferenceElementKind.Rectangle:
                    Actions.Add(new CreateRectangleAction
                    {
                        Id = element.Id, XMm = x, YMm = y, WidthMm = width, HeightMm = height, Name = name,
                        CornerRadiusMm = Math.Round((element.CornerRadiusRatio ?? 0) * _shortSide, 2),
                        FillColor = element.FillColor, OutlineColor = element.OutlineColor,
                        OutlineWidthMm = element.OutlineColor is null ? 0 : outlineWidth,
                    });
                    break;

                case ReferenceElementKind.Ellipse:
                    Actions.Add(new CreateEllipseAction
                    {
                        Id = element.Id, XMm = x, YMm = y, WidthMm = width, HeightMm = height, Name = name,
                        FillColor = element.FillColor, OutlineColor = element.OutlineColor,
                        OutlineWidthMm = element.OutlineColor is null ? 0 : outlineWidth,
                    });
                    break;

                case ReferenceElementKind.Line or ReferenceElementKind.Arrow:
                    var start = element.LineStart ?? new NormalizedPoint(element.Bounds.X, element.Bounds.Y);
                    var end = element.LineEnd ?? new NormalizedPoint(element.Bounds.X + element.Bounds.Width, element.Bounds.Y + element.Bounds.Height);
                    if (Math.Abs(start.X - end.X) < 1e-6 && Math.Abs(start.Y - end.Y) < 1e-6)
                    {
                        Skip(element, "0 mm");
                        break;
                    }

                    Actions.Add(new CreateLineAction
                    {
                        Id = element.Id, Name = name,
                        X1Mm = Math.Round(start.X * size.WidthMm, 3), Y1Mm = Math.Round(start.Y * size.HeightMm, 3),
                        X2Mm = Math.Round(end.X * size.WidthMm, 3), Y2Mm = Math.Round(end.Y * size.HeightMm, 3),
                        OutlineColor = element.OutlineColor ?? element.FillColor ?? "#000000", OutlineWidthMm = outlineWidth ?? Math.Max(0.3, _shortSide * 0.004),
                    });
                    if (element.Kind == ReferenceElementKind.Arrow)
                    {
                        Warnings.Add(Msg.Format("Reconstruct.Warning.Arrow", element.Id));
                    }

                    break;

                case ReferenceElementKind.Polygon when element.Points.Count >= 3:
                    Actions.Add(new CreatePolygonAction
                    {
                        Id = element.Id, Name = name,
                        PointsMm = element.Points.Select(point => (IReadOnlyList<double>)[Math.Round(point.X * size.WidthMm, 3), Math.Round(point.Y * size.HeightMm, 3)]).ToArray(),
                        FillColor = element.FillColor, OutlineColor = element.OutlineColor,
                        OutlineWidthMm = element.OutlineColor is null ? 0 : outlineWidth,
                    });
                    break;

                case ReferenceElementKind.ProhibitionSign:
                    AddProhibitionSign(element, name, x, y, width, height, outlineWidth);
                    break;

                case ReferenceElementKind.Text when !string.IsNullOrWhiteSpace(element.Text):
                    AddText(element, name, x, y, width, height);
                    break;

                case ReferenceElementKind.Table when element.Table is { } table:
                    Actions.Add(new CreateTableAction
                    {
                        Id = element.Id, XMm = x, YMm = y, WidthMm = width, HeightMm = height, Name = name,
                        Columns = table.Columns, Rows = table.Rows, Cells = table.Cells.Count == 0 ? null : table.Cells,
                        CellAlignment = Alignment(table.CellAlignment),
                    });
                    if (table.MergedCellsNote is not null)
                    {
                        Warnings.Add(Msg.Format("Reconstruct.Warning.MergedCells", element.Id, table.MergedCellsNote));
                    }

                    break;

                default:
                    // Curves, icons and anything else that reached here cannot be drawn with native shapes.
                    Placeholder(element, x, y, width, height, "Reconstruct.Warning.Complex");
                    break;
            }
        }

        /// <summary>A ring plus a diagonal bar from upper-left to lower-right, both editable and grouped by the caller's group if any.</summary>
        private void AddProhibitionSign(ReferenceElement element, string name, double x, double y, double width, double height, double? outlineWidth)
        {
            var color = element.OutlineColor ?? element.FillColor ?? "#D8202A";
            var stroke = outlineWidth ?? Math.Round(Math.Min(width, height) * 0.1, 2);
            var inset = stroke / 2; // keep the outer edge of the ring on the analysed bounds
            Actions.Add(new CreateEllipseAction
            {
                Id = element.Id, Name = name, XMm = x + inset, YMm = y + inset, WidthMm = Math.Max(0.1, width - stroke), HeightMm = Math.Max(0.1, height - stroke),
                OutlineColor = color, OutlineWidthMm = stroke,
            });

            // Bar endpoints on the ring's centre line at 45°.
            double centerX = x + (width / 2), centerY = y + (height / 2);
            double radiusX = (width - stroke) / 2 * Math.Sqrt(0.5), radiusY = (height - stroke) / 2 * Math.Sqrt(0.5);
            Actions.Add(new CreateLineAction
            {
                Id = element.Id + "_bar", Name = name + " /",
                X1Mm = centerX - radiusX, Y1Mm = centerY - radiusY, X2Mm = centerX + radiusX, Y2Mm = centerY + radiusY,
                OutlineColor = color, OutlineWidthMm = stroke,
            });
            Actions.Add(new GroupAction { Id = element.Id + "_sign", Targets = [TargetRef.ForAction(element.Id), TargetRef.ForAction(element.Id + "_bar")], Name = name });
        }

        private void AddText(ReferenceElement element, string name, double x, double y, double width, double height)
        {
            var text = element.Text!.Replace("\r\n", "\n", StringComparison.Ordinal);
            var lines = Math.Max(1, text.Split('\n').Length);
            var font = fonts.Resolve(element.FontFamilyGuess, element.FontConfidence);
            if (font.Match == FontMatchKind.Likely)
            {
                Warnings.Add(Msg.Format("Reconstruct.Warning.FontLikely", element.Id, font.FontFamily));
            }
            else if (font.Match == FontMatchKind.Fallback)
            {
                Warnings.Add(Msg.Format("Reconstruct.Warning.FontFallback", element.Id, font.FontFamily));
            }

            // First estimate of the size from the box height; the resize below then fits the width exactly,
            // so differences between font metrics do not shift the layout.
            var lineHeight = height / (1 + ((lines - 1) * 1.2));
            var glyphFactor = text.Any(char.IsLower) || element.Direction == ReferenceTextDirection.RightToLeft ? 0.95 : 0.72;
            var fontSize = Math.Clamp(Math.Round(lineHeight / glyphFactor * PointsPerMillimetre, 1), 1, 3000);

            Actions.Add(new CreateTextAction
            {
                Id = element.Id, Text = text, Name = name,
                XMm = Math.Round(x + (width / 2), 3), YMm = Math.Round(y + (height / 2), 3), Anchor = PositionAnchor.Center,
                FontFamily = font.FontFamily, FontSizePt = fontSize, Bold = element.Bold, Italic = element.Italic,
                Alignment = Alignment(element.TextAlignment), FillColor = element.FillColor ?? "#000000",
            });
            Actions.Add(new ResizeAction { Id = element.Id + "_fit", Targets = [TargetRef.ForAction(element.Id)], WidthMm = width, KeepAspectRatio = true });

            // Changing alignment or size can shift artistic text sideways; pin its left edge to the analysed box.
            Actions.Add(new MoveAction { Id = element.Id + "_place", Targets = [TargetRef.ForAction(element.Id)], ToXMm = x });

            if (element.Direction == ReferenceTextDirection.RightToLeft)
            {
                Warnings.Add(Msg.Format("Reconstruct.Warning.Rtl", element.Id));
            }
        }

        /// <summary>Keeps the layout honest: a visible, clearly named frame where something could not be rebuilt.</summary>
        private void Placeholder(ReferenceElement element, double x, double y, double width, double height, string warningKey)
        {
            if (width <= 0 || height <= 0)
            {
                Skip(element, "0 mm");
                return;
            }

            Actions.Add(new CreateRectangleAction
            {
                Id = element.Id, XMm = x, YMm = y, WidthMm = width, HeightMm = height,
                Name = Msg.Format("Reconstruct.Placeholder", Label(element)),
                OutlineColor = "#FF00FF", OutlineWidthMm = Math.Max(0.25, _shortSide * 0.002),
            });
            Warnings.Add(Msg.Format(warningKey, element.Id, Label(element)));
        }

        private void Skip(ReferenceElement element, string reason) => Warnings.Add(Msg.Format("Reconstruct.Warning.Skipped", element.Id, reason));

        private string LastCreator(int from) => Actions.Skip(from).Last(action => action.CreatesObjects).Id;

        private Asset? FindAsset(ReferenceElement element)
        {
            var words = $"{element.AssetHint} {element.Label}".Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(word => word.Length >= 3).ToArray();
            return words.Length == 0
                ? null
                : request.Assets
                    .Select(asset => (asset, score: words.Count(word => asset.Matches(new AssetQuery { Text = word }))))
                    .Where(entry => entry.score > 0)
                    .OrderByDescending(entry => entry.score)
                    .Select(entry => entry.asset)
                    .FirstOrDefault();
        }

        private static string Label(ReferenceElement element) => string.IsNullOrWhiteSpace(element.Label) ? Msg.Get("Vision.Kind." + element.Kind) : element.Label;

        private static TextAlignment Alignment(string? value) => value?.ToLowerInvariant() switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
    }
}
