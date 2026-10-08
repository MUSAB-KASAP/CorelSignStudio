using System.Globalization;

namespace CorelSignStudio.Domain.Automation;

/// <summary>Turns actions into short sentences a non-expert can check before pressing Execute.</summary>
public static class ActionDescriber
{
    public static string Describe(CorelAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action switch
        {
            CreateDocumentAction a => $"Create a new {Mm(a.WidthMm)} x {Mm(a.HeightMm)} mm document",
            OpenDocumentAction a => $"Open '{Path.GetFileName(a.FilePath)}'",
            CloseDocumentAction => "Close the document without saving",
            SetPageSizeAction a => $"Set the page size to {Mm(a.WidthMm)} x {Mm(a.HeightMm)} mm",
            CreateTextAction a => $"Add text \"{Shorten(a.Text)}\" at {Mm(a.XMm)}, {Mm(a.YMm)} mm{Named(a.Name)}",
            CreateRectangleAction a => $"Draw a {Mm(a.WidthMm)} x {Mm(a.HeightMm)} mm rectangle at {Mm(a.XMm)}, {Mm(a.YMm)} mm{Named(a.Name)}",
            CreateEllipseAction a => $"Draw a {Mm(a.WidthMm)} x {Mm(a.HeightMm)} mm ellipse at {Mm(a.XMm)}, {Mm(a.YMm)} mm{Named(a.Name)}",
            CreateLineAction a => $"Draw a line from {Mm(a.X1Mm)}, {Mm(a.Y1Mm)} to {Mm(a.X2Mm)}, {Mm(a.Y2Mm)} mm{Named(a.Name)}",
            CreateTableAction a => $"Create a table with {a.Columns} column(s) and {a.Rows} row(s), {Mm(a.WidthMm)} x {Mm(a.HeightMm)} mm" +
                                   (a.CellAlignment == TextAlignment.Center ? ", text centred" : "") + Named(a.Name),
            ImportFileAction a => $"Import '{Path.GetFileName(a.FilePath)}'{Named(a.Name)}",
            MoveAction a when a.ToXMm is not null || a.ToYMm is not null =>
                $"Move {Targets(a)} to {Mm(a.ToXMm ?? 0)}, {Mm(a.ToYMm ?? 0)} mm",
            MoveAction a => $"Move {Targets(a)} {Offset(a.DeltaXMm, a.DeltaYMm)}",
            ResizeAction a when a.ScalePercent is { } scale => $"Scale {Targets(a)} to {Mm(scale)}%",
            ResizeAction a => $"Resize {Targets(a)} to {(a.WidthMm is { } w ? Mm(w) : "auto")} x {(a.HeightMm is { } h ? Mm(h) : "auto")} mm",
            RotateAction a => a.Absolute
                ? $"Set the rotation of {Targets(a)} to {Mm(a.AngleDegrees)}°"
                : $"Rotate {Targets(a)} by {Mm(a.AngleDegrees)}°",
            SetTextAction a => $"Change the text of {Targets(a)} to \"{Shorten(a.Text)}\"",
            SetFontAction a => $"Change the font of {Targets(a)}" +
                               (a.FontFamily is null ? "" : $" to {a.FontFamily}") +
                               (a.FontSizePt is { } size ? $" {Mm(size)} pt" : ""),
            SetFillAction a => a.Color is null ? $"Remove the fill of {Targets(a)}" : $"Fill {Targets(a)} with {a.Color}",
            SetOutlineAction a => a.Remove
                ? $"Remove the outline of {Targets(a)}"
                : $"Set the outline of {Targets(a)}" + (a.Color is null ? "" : $" to {a.Color}") +
                  (a.WidthMm is { } width ? $", {Mm(width)} mm" : ""),
            AlignAction a => $"Align {Targets(a)} {AlignText(a)}",
            DistributeAction a => $"Distribute {Targets(a)} {a.Direction.ToString().ToLowerInvariant()}ly" +
                                  (a.SpacingMm is { } gap ? $" with {Mm(gap)} mm gaps" : ""),
            DuplicateAction a => $"Duplicate {Targets(a)}" + (a.Count > 1 ? $" {a.Count} times" : ""),
            DeleteAction a => $"Delete {Targets(a)}",
            GroupAction a => $"Group {Targets(a)}{Named(a.Name)}",
            UngroupAction a => $"Ungroup {Targets(a)}",
            MoveToLayerAction a => $"Move {Targets(a)} to layer '{a.Layer}'",
            CreateLayerAction a => $"Create layer '{a.Name}'",
            RenameObjectAction a => $"Rename {Targets(a)} to '{a.Name}'",
            BringToFrontAction a => $"Bring {Targets(a)} to the front",
            SendToBackAction a => $"Send {Targets(a)} to the back",
            SaveDocumentAction a => $"Save as CDR: {a.FilePath}",
            ExportPdfAction a => $"Export PDF: {a.FilePath}",
            ExportPngAction a => $"Export PNG ({a.Dpi} dpi): {a.FilePath}",
            ExportSvgAction a => $"Export SVG: {a.FilePath}",
            _ => action.TypeName,
        };
    }

    public static IReadOnlyList<string> Describe(AutomationPlan plan) =>
        plan.Actions.Select((action, index) => $"{index + 1}. {Describe(action)}").ToArray();

    private static string Targets(TargetedAction action)
    {
        var parts = action.Targets.Select(target =>
            TargetRef.IsSelection(target) ? "the selected objects"
            : TargetRef.IsActionRef(target) ? $"the result of step '{TargetRef.ActionId(target)}'"
            : TargetRef.IsName(target) ? $"'{TargetRef.Name(target)}'"
            : target).ToArray();
        return parts.Length <= 3 ? string.Join(", ", parts) : $"{parts.Length} objects";
    }

    private static string Offset(double dx, double dy)
    {
        var parts = new List<string>(2);
        if (dx != 0)
        {
            parts.Add($"{Mm(Math.Abs(dx))} mm {(dx > 0 ? "right" : "left")}");
        }

        if (dy != 0)
        {
            parts.Add($"{Mm(Math.Abs(dy))} mm {(dy > 0 ? "down" : "up")}");
        }

        return string.Join(" and ", parts);
    }

    private static string AlignText(AlignAction action)
    {
        var parts = new List<string>(2);
        if (action.Horizontal != HorizontalAlign.None)
        {
            parts.Add("horizontally " + action.Horizontal.ToString().ToLowerInvariant());
        }

        if (action.Vertical != VerticalAlign.None)
        {
            parts.Add("vertically " + action.Vertical.ToString().ToLowerInvariant());
        }

        var reference = action.RelativeTo switch
        {
            AlignReference.Page => "on the page",
            AlignReference.FirstTarget => "to the first object",
            _ => "to each other",
        };
        return $"{string.Join(" and ", parts)} {reference}";
    }

    private static string Named(string? name) => string.IsNullOrWhiteSpace(name) ? "" : $" (\"{name}\")";

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length <= 48 ? single : single[..45] + "…";
    }

    private static string Mm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
