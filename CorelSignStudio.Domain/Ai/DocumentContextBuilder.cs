using System.Globalization;
using System.Text;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.References;

namespace CorelSignStudio.Domain.Ai;

/// <summary>The document as the model sees it.</summary>
public sealed record DocumentContext
{
    public required string Text { get; init; }

    /// <summary>Shapes described in full.</summary>
    public IReadOnlyList<string> DetailedShapeIds { get; init; } = [];

    /// <summary>Shapes listed by id and type only.</summary>
    public IReadOnlyList<string> BriefShapeIds { get; init; } = [];

    /// <summary>Shapes that did not fit at all (only counted).</summary>
    public int OmittedShapeCount { get; init; }

    public bool WasClipped => BriefShapeIds.Count > 0 || OmittedShapeCount > 0;
}

public interface IDocumentContextBuilder
{
    DocumentContext Build(DocumentSnapshot? document, AiPlannerOptions options);

    string DescribeReferences(IReadOnlyList<ReferenceInput> references);
}

/// <summary>
/// Renders a <see cref="DocumentSnapshot"/> as compact text for a language model. Logical ids are always
/// kept verbatim. When a document is too large, shapes are prioritised (selected, then named or textual,
/// then the rest in front-to-back order); what does not fit in detail is still listed by id and type, and
/// only beyond that is it reduced to a count.
/// </summary>
public sealed class DocumentContextBuilder : IDocumentContextBuilder
{
    private const int MaxTextLength = 80;

    public DocumentContext Build(DocumentSnapshot? document, AiPlannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (document is null)
        {
            return new DocumentContext
            {
                Text = "DOCUMENT: none inspected. No existing object can be targeted; only plans that start by " +
                       "creating or opening a document are possible.",
            };
        }

        var page = document.ActivePage;
        var builder = new StringBuilder();
        builder.Append("DOCUMENT: ").AppendLine(document.Title);
        if (page is not null)
        {
            builder.AppendLine(Invariant($"PAGE: {N(page.WidthMm)} x {N(page.HeightMm)} mm (page {page.Index} of {document.Pages.Count}; origin top-left, y grows downwards)"));
            if (page.Layers.Count > 0)
            {
                builder.Append("LAYERS: ").AppendLine(string.Join(", ", page.Layers.Select(layer =>
                    layer.Name + (layer.Locked ? " (locked)" : "") + (layer.Visible ? "" : " (hidden)"))));
            }
        }

        var selected = document.SelectedShapeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        builder.Append("SELECTED: ").AppendLine(selected.Count == 0 ? "nothing" : string.Join(", ", document.SelectedShapeIds));

        // Only the active page can be edited, so only its shapes are offered as targets.
        var shapes = (page?.AllShapes() ?? []).ToList();
        var ordered = shapes
            .OrderBy(shape => selected.Contains(shape.Id) ? 0 : !string.IsNullOrWhiteSpace(shape.Name) || shape.Text is not null ? 1 : 2)
            .ThenBy(shape => shape.Order)
            .ToList();

        var detailed = new List<string>();
        var brief = new List<string>();
        var detailLines = new List<(int Order, string Line)>();
        var budget = Math.Max(2000, options.MaxContextCharacters) - builder.Length;
        var used = 0;
        var index = 0;
        for (; index < ordered.Count && detailed.Count < options.MaxDetailedShapes; index++)
        {
            var line = Describe(ordered[index], page, selected);
            if (used + line.Length + 1 > budget * 0.8)
            {
                break;
            }

            used += line.Length + 1;
            detailed.Add(ordered[index].Id);
            detailLines.Add((ordered[index].Order, line));
        }

        builder.AppendLine(Invariant($"SHAPES ({shapes.Count} on the active page; listed front to back):"));
        foreach (var (_, line) in detailLines.OrderBy(entry => entry.Order))
        {
            builder.AppendLine(line);
        }

        if (shapes.Count == 0)
        {
            builder.AppendLine("(the page is empty)");
        }

        // Whatever is left is still named by id, so the model can refer to it if the user does.
        var briefBuilder = new StringBuilder();
        for (; index < ordered.Count; index++)
        {
            var entry = ordered[index].Id + ":" + ordered[index].Type;
            if (used + briefBuilder.Length + entry.Length + 1 > budget)
            {
                break;
            }

            briefBuilder.Append(entry).Append(' ');
            brief.Add(ordered[index].Id);
        }

        var omitted = ordered.Count - index;
        if (brief.Count > 0)
        {
            builder.AppendLine(Invariant($"MORE SHAPES (id:type only, {brief.Count} unnamed objects without text):"));
            builder.AppendLine(briefBuilder.ToString().TrimEnd());
        }

        if (omitted > 0)
        {
            var summary = string.Join(", ", ordered.Skip(index).GroupBy(shape => shape.Type)
                .OrderByDescending(group => group.Count()).Select(group => $"{group.Key} x{group.Count()}"));
            builder.AppendLine(Invariant($"NOT LISTED: {omitted} further objects ({summary}). They cannot be targeted individually; ask the user to select them instead."));
        }

        return new DocumentContext
        {
            Text = builder.ToString().TrimEnd(),
            DetailedShapeIds = detailed,
            BriefShapeIds = brief,
            OmittedShapeCount = omitted,
        };
    }

    public string DescribeReferences(IReadOnlyList<ReferenceInput> references)
    {
        if (references is null || references.Count == 0)
        {
            return "REFERENCE FILES: none";
        }

        var builder = new StringBuilder("REFERENCE FILES (metadata only; their visual content is not available to you):");
        foreach (var reference in references)
        {
            builder.AppendLine().Append("- ").Append(Quote(reference.FilePath))
                .Append(" | type=").Append(reference.FileTypeLabel)
                .Append(" | ").Append(reference.IsVector ? "vector (editable when imported)" : "bitmap")
                .Append(" | role=").Append(reference.Role);
            if (!string.IsNullOrWhiteSpace(reference.Notes))
            {
                builder.Append(" | ").Append(reference.Notes);
            }
        }

        return builder.ToString();
    }

    private static string Describe(ShapeSnapshot shape, PageSnapshot? page, HashSet<string> selected)
    {
        var builder = new StringBuilder();
        builder.Append(shape.Id).Append(" | ").Append(shape.Type);
        if (!string.IsNullOrWhiteSpace(shape.Name))
        {
            builder.Append(" | name=").Append(Quote(shape.Name));
        }

        if (shape.Text is not null)
        {
            builder.Append(" | text=").Append(Quote(Shorten(shape.Text)));
        }

        var bounds = shape.Bounds;
        builder.Append(Invariant($" | x={N(bounds.XMm)} y={N(bounds.YMm)} w={N(bounds.WidthMm)} h={N(bounds.HeightMm)}"));
        if (page is not null)
        {
            builder.Append(" | at=").Append(Region(bounds, page));
        }

        if (shape.Fill is { Kind: FillKind.Uniform, ColorHex: { } fill })
        {
            builder.Append(" | fill=").Append(fill);
        }
        else if (shape.Fill is { Kind: not FillKind.None and not FillKind.Uniform } other)
        {
            builder.Append(" | fill=").Append(other.Kind);
        }

        if (shape.Outline is { HasOutline: true } outline)
        {
            builder.Append(Invariant($" | outline={outline.ColorHex ?? "?"} {N(outline.WidthMm)}mm"));
        }

        if (shape.FontFamily is not null)
        {
            builder.Append(Invariant($" | font={Quote(shape.FontFamily)} {N(shape.FontSizePt ?? 0)}pt"));
        }

        if (Math.Abs(shape.RotationDegrees) > 0.05)
        {
            builder.Append(Invariant($" | rotation={N(shape.RotationDegrees)}"));
        }

        builder.Append(" | layer=").Append(Quote(shape.LayerName));
        if (shape.ParentGroupId is not null)
        {
            builder.Append(" | in-group=").Append(shape.ParentGroupId);
        }

        if (shape.Children.Count > 0)
        {
            builder.Append(Invariant($" | children={shape.Children.Count}"));
        }

        if (selected.Contains(shape.Id))
        {
            builder.Append(" | SELECTED");
        }

        if (shape.Locked)
        {
            builder.Append(" | locked");
        }

        if (!shape.Visible)
        {
            builder.Append(" | hidden");
        }

        return builder.ToString();
    }

    /// <summary>A coarse position word pair, so "the one at the top right" can be resolved without arithmetic.</summary>
    private static string Region(BoundsMm bounds, PageSnapshot page)
    {
        static string Third(double value, double size, string low, string middle, string high) =>
            size <= 0 ? middle : value < size / 3 ? low : value > size * 2 / 3 ? high : middle;

        return Third(bounds.CenterYMm, page.HeightMm, "top", "middle", "bottom") + "-" +
               Third(bounds.CenterXMm, page.WidthMm, "left", "center", "right");
    }

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" / ");
        return single.Length <= MaxTextLength ? single : single[..(MaxTextLength - 1)] + "…";
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "'", StringComparison.Ordinal) + "\"";

    private static string N(double value) => Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
