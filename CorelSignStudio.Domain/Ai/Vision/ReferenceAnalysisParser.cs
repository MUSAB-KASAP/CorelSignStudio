using System.Text.Json;
using System.Text.Json.Nodes;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.References;

namespace CorelSignStudio.Domain.Ai.Vision;

public sealed record ReferenceParseResult
{
    public required AiPlanningStatus Status { get; init; }
    public IReadOnlyList<ReferenceElement> Elements { get; init; } = [];
    public string? Summary { get; init; }
    public string? ClarificationQuestion { get; init; }
    public string? BackgroundColor { get; init; }
    public double? Confidence { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<string> AppliedModifications { get; init; } = [];
    public string? UserError { get; init; }
    public string? RepairHint { get; init; }
}

/// <summary>
/// Validates the vision model's answer and turns it into <see cref="ReferenceElement"/>s with stable
/// <c>ref_NNN</c> ids. As with plans, nothing is trusted: unknown kinds, geometry outside the canvas,
/// malformed colours and inconsistent tables reject the whole analysis.
/// </summary>
public sealed class ReferenceAnalysisParser
{
    /// <summary>Text read with less confidence than this is flagged for the user to check.</summary>
    public const double UncertainTextThreshold = 0.6;

    public ReferenceParseResult Parse(string content)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(AiPlanParser.ExtractJson(content ?? ""), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject
                   ?? throw new JsonException("The answer is not a JSON object.");
        }
        catch (JsonException exception)
        {
            return Invalid(Msg.Get("Vision.Invalid.Malformed"), "Your answer was not a single valid JSON object: " + exception.Message);
        }

        var summary = Text(root, "summary");
        var status = (Text(root, "status") ?? "").Trim().ToLowerInvariant();
        var confidence = Number(root, "confidence") is { } raw ? Math.Clamp(raw, 0, 1) : (double?)null;
        var warnings = Strings(root, "warnings");

        if (status == "needs_clarification")
        {
            var question = Text(root, "clarificationQuestion");
            return string.IsNullOrWhiteSpace(question)
                ? Invalid(Msg.Get("Ai.Invalid.ClarificationEmpty"), "status was needs_clarification but clarificationQuestion was empty.")
                : new ReferenceParseResult { Status = AiPlanningStatus.NeedsClarification, ClarificationQuestion = question.Trim(), Summary = summary };
        }

        if (status == "cannot_analyze")
        {
            return new ReferenceParseResult
            {
                Status = AiPlanningStatus.Invalid,
                Summary = summary,
                UserError = Msg.Format("Vision.Invalid.CannotAnalyze", string.IsNullOrWhiteSpace(summary) ? "—" : summary),
            };
        }

        if (status != "ready")
        {
            return Invalid(Msg.Get("Vision.Invalid.Malformed"), $"status must be one of {string.Join(", ", ReferenceVisionPrompt.StatusValues)} but was '{status}'.");
        }

        if (root["elements"] is not JsonArray nodes || nodes.Count == 0)
        {
            return Invalid(Msg.Get("Vision.Invalid.NoElements"), "status was ready but elements was empty.");
        }

        var background = Text(root, "backgroundColor");
        if (!string.IsNullOrWhiteSpace(background) && !ColorHex.IsValid(background))
        {
            return Invalid(Msg.Format("Vision.Invalid.Color", background), $"backgroundColor '{background}' is not #RRGGBB.");
        }

        // First pass: read and validate every element, remembering the model's own keys.
        var drafts = new List<(string Key, string ParentKey, ReferenceElement Element)>();
        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index] is not JsonObject node)
            {
                return Invalid(Msg.Get("Vision.Invalid.Malformed"), $"elements[{index}] is not an object.");
            }

            var where = $"elements[{index}]";
            var kindText = Text(node, "kind") ?? "";
            if (!Enum.TryParse<ReferenceElementKind>(kindText, ignoreCase: true, out var kind) || kind == ReferenceElementKind.Unknown || int.TryParse(kindText, out _))
            {
                return Invalid(Msg.Format("Vision.Invalid.Kind", kindText.Length == 0 ? "?" : kindText),
                    $"{where}.kind '{kindText}' is not allowed. Use only: {string.Join(", ", ReferenceVisionPrompt.KindNames)}.");
            }

            if (node["bounds"] is not JsonObject boundsNode ||
                Number(boundsNode, "x") is not { } x || Number(boundsNode, "y") is not { } y ||
                Number(boundsNode, "width") is not { } width || Number(boundsNode, "height") is not { } height)
            {
                return Invalid(Msg.Format("Vision.Invalid.Geometry", where), $"{where}.bounds must have numeric x, y, width and height.");
            }

            var bounds = new NormalizedBounds(x, y, width, height);
            var lineLike = kind is ReferenceElementKind.Line or ReferenceElementKind.Arrow;
            if (!bounds.IsValid || (!lineLike && height <= 0))
            {
                return Invalid(Msg.Format("Vision.Invalid.Geometry", $"{where}: x={x}, y={y}, width={width}, height={height}"),
                    $"{where}.bounds ({x}, {y}, {width}, {height}) must lie inside the image: 0 <= x, y; width, height > 0; x+width <= 1; y+height <= 1.");
            }

            string? fill = NullIfEmpty(Text(node, "fillColor")), outline = NullIfEmpty(Text(node, "outlineColor"));
            foreach (var color in new[] { fill, outline })
            {
                if (color is not null && !ColorHex.IsValid(color))
                {
                    return Invalid(Msg.Format("Vision.Invalid.Color", color), $"{where} has colour '{color}', which is not #RRGGBB.");
                }
            }

            var text = Text(node, "text");
            if (kind == ReferenceElementKind.Text && string.IsNullOrWhiteSpace(text))
            {
                return Invalid(Msg.Format("Vision.Invalid.Geometry", where), $"{where} is text but has no \"text\".");
            }

            NormalizedPoint? lineStart = null, lineEnd = null;
            if (node["line"] is JsonObject line && Number(line, "x1") is { } x1 && Number(line, "y1") is { } y1 && Number(line, "x2") is { } x2 && Number(line, "y2") is { } y2)
            {
                if (!InRange(x1) || !InRange(y1) || !InRange(x2) || !InRange(y2))
                {
                    return Invalid(Msg.Format("Vision.Invalid.Geometry", where), $"{where}.line coordinates must be within 0..1.");
                }

                (lineStart, lineEnd) = (new NormalizedPoint(Clamp(x1), Clamp(y1)), new NormalizedPoint(Clamp(x2), Clamp(y2)));
            }

            var points = new List<NormalizedPoint>();
            if (node["points"] is JsonArray pointNodes)
            {
                foreach (var pointNode in pointNodes)
                {
                    if (pointNode is not JsonArray { Count: 2 } pair || AsNumber(pair[0]) is not { } px || AsNumber(pair[1]) is not { } py || !InRange(px) || !InRange(py))
                    {
                        return Invalid(Msg.Format("Vision.Invalid.Geometry", where), $"{where}.points must be [x, y] pairs within 0..1.");
                    }

                    points.Add(new NormalizedPoint(Clamp(px), Clamp(py)));
                }
            }

            if (kind is ReferenceElementKind.Polygon && points.Count < 3)
            {
                return Invalid(Msg.Format("Vision.Invalid.Geometry", where), $"{where} is a polygon but has fewer than 3 points.");
            }

            ReferenceTable? table = null;
            if (kind == ReferenceElementKind.Table)
            {
                if (node["table"] is not JsonObject tableNode || Number(tableNode, "rows") is not { } rows || Number(tableNode, "columns") is not { } columns ||
                    rows < 1 || columns < 1 || rows > 500 || columns > 200 || rows != Math.Floor(rows) || columns != Math.Floor(columns))
                {
                    return Invalid(Msg.Format("Vision.Invalid.Geometry", where), $"{where} is a table but \"table\" lacks valid rows/columns.");
                }

                var cells = (tableNode["cells"] as JsonArray)?.Select(row => (IReadOnlyList<string>)((row as JsonArray)?.Select(cell => cell?.ToString() ?? "").ToArray() ?? [])).ToList() ?? [];
                if (cells.Count > rows || cells.Any(row => row.Count > columns))
                {
                    return Invalid(Msg.Format("Vision.Invalid.Geometry", where), $"{where}.table.cells has more rows or columns than the table.");
                }

                table = new ReferenceTable
                {
                    Rows = (int)rows,
                    Columns = (int)columns,
                    Cells = cells,
                    CellAlignment = NullIfEmpty(Text(tableNode, "cellAlignment")),
                    HasHeaderRow = Flag(tableNode, "hasHeaderRow"),
                    MergedCellsNote = NullIfEmpty(Text(tableNode, "mergedCellsNote")),
                };
            }

            var strategyText = Text(node, "strategy") ?? "";
            if (!Enum.TryParse<ReconstructionStrategy>(strategyText, ignoreCase: true, out var strategy) || int.TryParse(strategyText, out _))
            {
                strategy = DefaultStrategy(kind);
            }

            var textConfidence = Number(node, "textConfidence") is { } tc ? Math.Clamp(tc, 0, 1) : (double?)null;
            drafts.Add((Text(node, "key") ?? "", Text(node, "parentKey") ?? "", new ReferenceElement
            {
                Kind = kind,
                Label = NullIfEmpty(Text(node, "label")),
                Bounds = bounds.Clamp(),
                ZIndex = (int)(Number(node, "zIndex") ?? index),
                RotationDegrees = Number(node, "rotation") ?? 0,
                FillColor = fill?.ToUpperInvariant(),
                OutlineColor = outline?.ToUpperInvariant(),
                OutlineWidthRatio = Positive(Number(node, "outlineWidthRatio")),
                CornerRadiusRatio = Positive(Number(node, "cornerRadiusRatio")),
                Text = kind == ReferenceElementKind.Text ? text : NullIfEmpty(text),
                TextConfidence = textConfidence,
                TextUncertain = kind == ReferenceElementKind.Text && textConfidence is { } value && value < UncertainTextThreshold,
                FontFamilyGuess = NullIfEmpty(Text(node, "fontFamilyGuess")),
                FontConfidence = Number(node, "fontConfidence") is { } fc ? Math.Clamp(fc, 0, 1) : null,
                Bold = Flag(node, "bold"),
                Italic = Flag(node, "italic"),
                TextAlignment = NullIfEmpty(Text(node, "textAlignment"))?.ToLowerInvariant(),
                Direction = string.Equals(Text(node, "direction"), "rtl", StringComparison.OrdinalIgnoreCase) ? ReferenceTextDirection.RightToLeft : ReferenceTextDirection.LeftToRight,
                LineStart = lineStart,
                LineEnd = lineEnd,
                Points = points,
                Table = table,
                Strategy = strategy,
                AssetHint = NullIfEmpty(Text(node, "assetHint")),
                Confidence = Math.Clamp(Number(node, "confidence") ?? 0.5, 0, 1),
                Evidence = NullIfEmpty(Text(node, "evidence")),
            }));
        }

        // Second pass: stable ids in back-to-front order, and parents resolved to those ids.
        var ordered = drafts.Select((draft, index) => (draft, index)).OrderBy(entry => entry.draft.Element.ZIndex).ThenBy(entry => entry.index).ToList();
        var idByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var position = 0; position < ordered.Count; position++)
        {
            var key = ordered[position].draft.Key;
            if (key.Length > 0)
            {
                idByKey.TryAdd(key, ReferenceAnalysis.ElementId(position + 1));
            }
        }

        var elements = new List<ReferenceElement>();
        for (var position = 0; position < ordered.Count; position++)
        {
            var (key, parentKey, element) = ordered[position].draft;
            var id = ReferenceAnalysis.ElementId(position + 1);
            var parentId = parentKey.Length > 0 && parentKey != key && idByKey.TryGetValue(parentKey, out var resolved) && resolved != id ? resolved : null;
            elements.Add(element with { Id = id, ParentId = parentId, ZIndex = position + 1 });
            if (element.TextUncertain)
            {
                warnings.Add(Msg.Format("Vision.Warning.TextUncertain", id, Shorten(element.Text ?? "")));
            }
        }

        return new ReferenceParseResult
        {
            Status = AiPlanningStatus.Ready,
            Elements = elements,
            Summary = summary,
            BackgroundColor = NullIfEmpty(background)?.ToUpperInvariant(),
            Confidence = confidence,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
            AppliedModifications = Strings(root, "appliedModifications"),
        };
    }

    public static ReconstructionStrategy DefaultStrategy(ReferenceElementKind kind) => kind switch
    {
        ReferenceElementKind.Text => ReconstructionStrategy.Text,
        ReferenceElementKind.Table => ReconstructionStrategy.Table,
        ReferenceElementKind.Logo => ReconstructionStrategy.NeedsUserAsset,
        ReferenceElementKind.Photo or ReferenceElementKind.Image => ReconstructionStrategy.ImportImage,
        ReferenceElementKind.Icon or ReferenceElementKind.Pictogram or ReferenceElementKind.Curve => ReconstructionStrategy.UnsupportedComplexArtwork,
        _ => ReconstructionStrategy.NativeShape,
    };

    private static ReferenceParseResult Invalid(string userError, string repairHint) =>
        new() { Status = AiPlanningStatus.Invalid, UserError = userError, RepairHint = repairHint };

    private static bool InRange(double value) => double.IsFinite(value) && value >= -NormalizedBounds.Tolerance && value <= 1 + NormalizedBounds.Tolerance;

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);

    private static double? Positive(double? value) => value is { } number && double.IsFinite(number) && number > 0 ? number : null;

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonObject node, string name) => AsNumber(node[name]);

    private static double? AsNumber(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && double.IsFinite(value.GetValue<double>()) ? value.GetValue<double>() : null;

    private static bool Flag(JsonObject node, string name) => node[name] is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    private static List<string> Strings(JsonObject node, string name) =>
        (node[name] as JsonArray)?.Select(item => item?.ToString()).Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text!).ToList() ?? [];

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length <= 40 ? single : single[..39] + "…";
    }
}
