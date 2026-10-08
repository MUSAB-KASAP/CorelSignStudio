using CorelSignStudio.Domain.Localization;

namespace CorelSignStudio.Domain.Automation;

/// <summary>
/// Turns actions into short sentences a non-expert can check before pressing Execute. The wording comes
/// from the message catalogue (<see cref="Msg"/>), so it follows the application's language.
/// </summary>
public static class ActionDescriber
{
    public static string Describe(CorelAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action switch
        {
            CreateDocumentAction a => Msg.Format("Describe.CreateDocument", Mm(a.WidthMm), Mm(a.HeightMm)),
            OpenDocumentAction a => Msg.Format("Describe.OpenDocument", Path.GetFileName(a.FilePath)),
            CloseDocumentAction => Msg.Get("Describe.CloseDocument"),
            SetPageSizeAction a => Msg.Format("Describe.SetPageSize", Mm(a.WidthMm), Mm(a.HeightMm)),
            CreateTextAction a => Msg.Format("Describe.CreateText", Shorten(a.Text), Mm(a.XMm), Mm(a.YMm), Named(a.Name)),
            CreateRectangleAction a => Msg.Format("Describe.CreateRectangle", Mm(a.WidthMm), Mm(a.HeightMm), Mm(a.XMm), Mm(a.YMm), Named(a.Name)),
            CreateEllipseAction a => Msg.Format("Describe.CreateEllipse", Mm(a.WidthMm), Mm(a.HeightMm), Mm(a.XMm), Mm(a.YMm), Named(a.Name)),
            CreateLineAction a => Msg.Format("Describe.CreateLine", Mm(a.X1Mm), Mm(a.Y1Mm), Mm(a.X2Mm), Mm(a.Y2Mm), Named(a.Name)),
            CreatePolygonAction a => Msg.Format("Describe.CreatePolygon", a.PointsMm.Count, Named(a.Name)),
            CreateTableAction a => Msg.Format(
                "Describe.CreateTable", a.Columns, a.Rows, Mm(a.WidthMm), Mm(a.HeightMm),
                a.CellAlignment == TextAlignment.Center ? Msg.Get("Describe.CreateTable.Centered") : "", Named(a.Name)),
            ImportFileAction a => Msg.Format("Describe.ImportFile", Path.GetFileName(a.FilePath), Named(a.Name)),
            MoveAction a when a.ToXMm is not null || a.ToYMm is not null =>
                Msg.Format("Describe.MoveTo", Targets(a), Mm(a.ToXMm ?? 0), Mm(a.ToYMm ?? 0)),
            MoveAction a => Msg.Format("Describe.Move", Targets(a), Offset(a.DeltaXMm, a.DeltaYMm)),
            ResizeAction a when a.ScalePercent is { } scale => Msg.Format("Describe.Scale", Targets(a), Mm(scale)),
            ResizeAction a => Msg.Format(
                "Describe.Resize", Targets(a),
                a.WidthMm is { } width ? Mm(width) : Msg.Get("Describe.Auto"),
                a.HeightMm is { } height ? Mm(height) : Msg.Get("Describe.Auto")),
            RotateAction a => Msg.Format(a.Absolute ? "Describe.RotateAbsolute" : "Describe.Rotate", Targets(a), Mm(a.AngleDegrees)),
            SetTextAction a => Msg.Format("Describe.SetText", Targets(a), Shorten(a.Text)),
            SetFontAction a => Msg.Format(
                "Describe.SetFont", Targets(a),
                a.FontFamily is null ? "" : Msg.Format("Describe.SetFont.Family", a.FontFamily),
                a.FontSizePt is { } size ? Msg.Format("Describe.SetFont.Size", Mm(size)) : ""),
            SetFillAction a => a.Color is null
                ? Msg.Format("Describe.RemoveFill", Targets(a))
                : Msg.Format("Describe.SetFill", Targets(a), a.Color),
            SetOutlineAction a => a.Remove
                ? Msg.Format("Describe.RemoveOutline", Targets(a))
                : Msg.Format(
                    "Describe.SetOutline", Targets(a),
                    a.Color is null ? "" : Msg.Format("Describe.SetOutline.Color", a.Color),
                    a.WidthMm is { } outlineWidth ? Msg.Format("Describe.SetOutline.Width", Mm(outlineWidth)) : ""),
            AlignAction a => Msg.Format("Describe.Align", Targets(a), AlignReferenceText(a), AlignDirections(a)),
            DistributeAction a => Msg.Format(
                a.Direction == DistributeDirection.Horizontal ? "Describe.Distribute.Horizontal" : "Describe.Distribute.Vertical",
                Targets(a),
                a.SpacingMm is { } gap ? Msg.Format("Describe.Distribute.Spacing", Mm(gap)) : ""),
            DuplicateAction a => Msg.Format("Describe.Duplicate", Targets(a), a.Count > 1 ? Msg.Format("Describe.Duplicate.Count", a.Count) : ""),
            DeleteAction a => Msg.Format("Describe.Delete", Targets(a)),
            GroupAction a => Msg.Format("Describe.Group", Targets(a), Named(a.Name)),
            UngroupAction a => Msg.Format("Describe.Ungroup", Targets(a)),
            MoveToLayerAction a => Msg.Format("Describe.MoveToLayer", Targets(a), a.Layer),
            CreateLayerAction a => Msg.Format("Describe.CreateLayer", a.Name),
            RenameObjectAction a => Msg.Format("Describe.Rename", Targets(a), a.Name),
            BringToFrontAction a => Msg.Format("Describe.BringToFront", Targets(a)),
            SendToBackAction a => Msg.Format("Describe.SendToBack", Targets(a)),
            SaveDocumentAction a => Msg.Format("Describe.SaveDocument", a.FilePath),
            ExportPdfAction a => Msg.Format("Describe.ExportPdf", a.FilePath),
            ExportPngAction a => Msg.Format("Describe.ExportPng", a.Dpi, a.FilePath),
            ExportSvgAction a => Msg.Format("Describe.ExportSvg", a.FilePath),
            _ => action.TypeName,
        };
    }

    public static IReadOnlyList<string> Describe(AutomationPlan plan) =>
        plan.Actions.Select((action, index) => $"{index + 1}. {Describe(action)}").ToArray();

    private static string Targets(TargetedAction action)
    {
        var parts = action.Targets.Select(target =>
            TargetRef.IsSelection(target) ? Msg.Get("Describe.Target.Selection")
            : TargetRef.IsActionRef(target) ? Msg.Format("Describe.Target.ActionResult", TargetRef.ActionId(target))
            : TargetRef.IsName(target) ? Msg.Format("Describe.Target.Name", TargetRef.Name(target))
            : target).ToArray();
        return parts.Length <= 3 ? string.Join(", ", parts) : Msg.Format("Describe.Target.Many", parts.Length);
    }

    private static string Offset(double dx, double dy)
    {
        var parts = new List<string>(2);
        if (dx != 0)
        {
            parts.Add(Msg.Format(dx > 0 ? "Describe.Offset.Right" : "Describe.Offset.Left", Mm(Math.Abs(dx))));
        }

        if (dy != 0)
        {
            parts.Add(Msg.Format(dy > 0 ? "Describe.Offset.Down" : "Describe.Offset.Up", Mm(Math.Abs(dy))));
        }

        return string.Join(Msg.Get("Describe.And"), parts);
    }

    private static string AlignDirections(AlignAction action)
    {
        var parts = new List<string>(2);
        if (action.Horizontal != HorizontalAlign.None)
        {
            parts.Add(Msg.Get(action.Horizontal switch
            {
                HorizontalAlign.Left => "Describe.Align.Left",
                HorizontalAlign.Right => "Describe.Align.Right",
                _ => "Describe.Align.HCenter",
            }));
        }

        if (action.Vertical != VerticalAlign.None)
        {
            parts.Add(Msg.Get(action.Vertical switch
            {
                VerticalAlign.Top => "Describe.Align.Top",
                VerticalAlign.Bottom => "Describe.Align.Bottom",
                _ => "Describe.Align.VCenter",
            }));
        }

        return string.Join(Msg.Get("Describe.And"), parts);
    }

    private static string AlignReferenceText(AlignAction action) => Msg.Get(action.RelativeTo switch
    {
        AlignReference.Page => "Describe.Align.Page",
        AlignReference.FirstTarget => "Describe.Align.FirstTarget",
        _ => "Describe.Align.TargetBounds",
    });

    private static string Named(string? name) => string.IsNullOrWhiteSpace(name) ? "" : Msg.Format("Describe.Named", name);

    private static string Shorten(string text)
    {
        var single = text.ReplaceLineEndings(" ");
        return single.Length <= 48 ? single : single[..45] + "…";
    }

    private static string Mm(double value) => Msg.Number(value);
}
