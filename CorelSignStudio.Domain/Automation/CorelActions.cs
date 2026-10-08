using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Localization;

namespace CorelSignStudio.Domain.Automation;

/// <summary>
/// One application-neutral operation on a design document. All lengths are millimetres and all
/// positions are measured from the page's top-left corner (Y grows downwards). Actions never hold
/// application objects; they refer to shapes through target references (see <see cref="TargetRef"/>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CreateDocumentAction), "createDocument")]
[JsonDerivedType(typeof(OpenDocumentAction), "openDocument")]
[JsonDerivedType(typeof(CloseDocumentAction), "closeDocument")]
[JsonDerivedType(typeof(SetPageSizeAction), "setPageSize")]
[JsonDerivedType(typeof(CreateTextAction), "createText")]
[JsonDerivedType(typeof(CreateRectangleAction), "createRectangle")]
[JsonDerivedType(typeof(CreateEllipseAction), "createEllipse")]
[JsonDerivedType(typeof(CreateLineAction), "createLine")]
[JsonDerivedType(typeof(CreateTableAction), "createTable")]
[JsonDerivedType(typeof(ImportFileAction), "importFile")]
[JsonDerivedType(typeof(MoveAction), "move")]
[JsonDerivedType(typeof(ResizeAction), "resize")]
[JsonDerivedType(typeof(RotateAction), "rotate")]
[JsonDerivedType(typeof(SetTextAction), "setText")]
[JsonDerivedType(typeof(SetFontAction), "setFont")]
[JsonDerivedType(typeof(SetFillAction), "setFill")]
[JsonDerivedType(typeof(SetOutlineAction), "setOutline")]
[JsonDerivedType(typeof(AlignAction), "align")]
[JsonDerivedType(typeof(DistributeAction), "distribute")]
[JsonDerivedType(typeof(DuplicateAction), "duplicate")]
[JsonDerivedType(typeof(DeleteAction), "delete")]
[JsonDerivedType(typeof(GroupAction), "group")]
[JsonDerivedType(typeof(UngroupAction), "ungroup")]
[JsonDerivedType(typeof(MoveToLayerAction), "moveToLayer")]
[JsonDerivedType(typeof(CreateLayerAction), "createLayer")]
[JsonDerivedType(typeof(RenameObjectAction), "renameObject")]
[JsonDerivedType(typeof(BringToFrontAction), "bringToFront")]
[JsonDerivedType(typeof(SendToBackAction), "sendToBack")]
[JsonDerivedType(typeof(SaveDocumentAction), "saveDocument")]
[JsonDerivedType(typeof(ExportPdfAction), "exportPdf")]
[JsonDerivedType(typeof(ExportPngAction), "exportPng")]
[JsonDerivedType(typeof(ExportSvgAction), "exportSvg")]
public abstract record CorelAction
{
    /// <summary>Unique within a plan. Later actions can target this action's output as <c>@id</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Optional free-text explanation shown to the user next to the action.</summary>
    public string? Note { get; init; }

    [JsonIgnore]
    public string TypeName => ActionTypes.NameOf(GetType());

    /// <summary>True when the action removes or overwrites user work and should be confirmed first.</summary>
    [JsonIgnore]
    public virtual bool IsDestructive => false;

    /// <summary>True when the action produces shapes that later actions may target as <c>@id</c>.</summary>
    [JsonIgnore]
    public virtual bool CreatesObjects => false;

    /// <summary>True when the action switches the plan to a different document.</summary>
    [JsonIgnore]
    public virtual bool OpensDocument => false;

    [JsonIgnore]
    public virtual IReadOnlyList<string> TargetRefs => [];

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Id))
        {
            errors.Add(Msg.Get("Validation.ActionIdRequired"));
        }
        else if (Id.Any(char.IsWhiteSpace) || Id.StartsWith('@'))
        {
            errors.Add(Msg.Format("Validation.ActionIdInvalid", Id));
        }

        ValidateCore(errors);
        return errors;
    }

    protected abstract void ValidateCore(List<string> errors);

    protected static void RequirePositive(List<string> errors, double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            errors.Add(Msg.Format("Validation.Positive", name));
        }
    }

    protected static void RequireFinite(List<string> errors, double? value, string name)
    {
        if (value is { } number && !double.IsFinite(number))
        {
            errors.Add(Msg.Format("Validation.Finite", name));
        }
    }

    protected static void RequireNonNegative(List<string> errors, double? value, string name)
    {
        if (value is { } number && (!double.IsFinite(number) || number < 0))
        {
            errors.Add(Msg.Format("Validation.NonNegative", name));
        }
    }

    protected static void RequireText(List<string> errors, string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(Msg.Format("Validation.Required", name));
        }
    }

    protected static void RequireColor(List<string> errors, string? value, string name)
    {
        if (value is not null && !ColorHex.IsValid(value))
        {
            errors.Add(Msg.Format("Validation.Color", name));
        }
    }

    protected static void RequireFileExtension(List<string> errors, string? path, string name, params string[] extensions)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add(Msg.Format("Validation.Required", name));
            return;
        }

        if (path.Contains("{{", StringComparison.Ordinal))
        {
            return; // Unresolved placeholders are reported once at plan level.
        }

        var extension = Path.GetExtension(path);
        if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(Msg.Format("Validation.Extension", name, string.Join(" / ", extensions)));
        }
    }
}

/// <summary>An action that operates on existing shapes.</summary>
public abstract record TargetedAction : CorelAction
{
    public required IReadOnlyList<string> Targets { get; init; }

    [JsonIgnore]
    public override IReadOnlyList<string> TargetRefs => Targets ?? [];

    /// <summary>Minimum number of targets the action needs.</summary>
    [JsonIgnore]
    protected virtual int MinimumTargets => 1;

    protected override void ValidateCore(List<string> errors)
    {
        if (Targets is null || Targets.Count == 0)
        {
            errors.Add(Msg.Get("Validation.TargetRequired"));
            return;
        }

        foreach (var target in Targets)
        {
            if (!TargetRef.IsValid(target))
            {
                errors.Add(Msg.Format("Validation.TargetInvalid", target));
            }
        }

        var multiTarget = Targets.Any(target => TargetRef.IsSelection(target) || TargetRef.IsName(target));
        if (Targets.Count < MinimumTargets && !multiTarget)
        {
            errors.Add(Msg.Format("Validation.MinimumTargets", MinimumTargets));
        }
    }
}

/// <summary>Shared styling for actions that create a new shape.</summary>
public abstract record CreateShapeAction : CorelAction
{
    /// <summary>Object name shown in CorelDRAW's Objects docker.</summary>
    public string? Name { get; init; }

    /// <summary>Existing layer to create the shape on; the active layer when omitted.</summary>
    public string? Layer { get; init; }

    public string? FillColor { get; init; }
    public string? OutlineColor { get; init; }
    public double? OutlineWidthMm { get; init; }

    [JsonIgnore]
    public override bool CreatesObjects => true;

    protected override void ValidateCore(List<string> errors)
    {
        RequireColor(errors, FillColor, nameof(FillColor));
        RequireColor(errors, OutlineColor, nameof(OutlineColor));
        RequireNonNegative(errors, OutlineWidthMm, nameof(OutlineWidthMm));
    }
}

// ---- Document ---------------------------------------------------------------------------

public sealed record CreateDocumentAction : CorelAction
{
    public required double WidthMm { get; init; }
    public required double HeightMm { get; init; }

    [JsonIgnore]
    public override bool OpensDocument => true;

    protected override void ValidateCore(List<string> errors)
    {
        RequirePositive(errors, WidthMm, nameof(WidthMm));
        RequirePositive(errors, HeightMm, nameof(HeightMm));
    }
}

public sealed record OpenDocumentAction : CorelAction
{
    public required string FilePath { get; init; }

    [JsonIgnore]
    public override bool OpensDocument => true;

    protected override void ValidateCore(List<string> errors) => RequireText(errors, FilePath, nameof(FilePath));
}

/// <summary>Closes the plan's current document without saving. Save or export first.</summary>
public sealed record CloseDocumentAction : CorelAction
{
    [JsonIgnore]
    public override bool IsDestructive => true;

    protected override void ValidateCore(List<string> errors)
    {
    }
}

public sealed record SetPageSizeAction : CorelAction
{
    public required double WidthMm { get; init; }
    public required double HeightMm { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        RequirePositive(errors, WidthMm, nameof(WidthMm));
        RequirePositive(errors, HeightMm, nameof(HeightMm));
    }
}

// ---- Create -----------------------------------------------------------------------------

public enum PositionAnchor
{
    TopLeft,
    Center,
}

public enum TextAlignment
{
    Left,
    Center,
    Right,
}

public sealed record CreateTextAction : CreateShapeAction
{
    public required string Text { get; init; }
    public double XMm { get; init; }
    public double YMm { get; init; }

    /// <summary>Whether X/Y is the top-left corner or the centre of the text.</summary>
    public PositionAnchor Anchor { get; init; } = PositionAnchor.TopLeft;

    public string? FontFamily { get; init; }
    public double? FontSizePt { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public TextAlignment Alignment { get; init; } = TextAlignment.Left;

    /// <summary>When both are set the text is created as paragraph text inside this frame.</summary>
    public double? FrameWidthMm { get; init; }
    public double? FrameHeightMm { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireText(errors, Text, nameof(Text));
        RequireFinite(errors, XMm, nameof(XMm));
        RequireFinite(errors, YMm, nameof(YMm));
        if (FontSizePt is { } size)
        {
            RequirePositive(errors, size, nameof(FontSizePt));
        }

        if (FrameWidthMm is null != FrameHeightMm is null)
        {
            errors.Add(Msg.Get("Validation.FrameTogether"));
        }
        else if (FrameWidthMm is { } width && FrameHeightMm is { } height)
        {
            RequirePositive(errors, width, nameof(FrameWidthMm));
            RequirePositive(errors, height, nameof(FrameHeightMm));
        }
    }
}

public sealed record CreateRectangleAction : CreateShapeAction
{
    public double XMm { get; init; }
    public double YMm { get; init; }
    public required double WidthMm { get; init; }
    public required double HeightMm { get; init; }
    public double CornerRadiusMm { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireFinite(errors, XMm, nameof(XMm));
        RequireFinite(errors, YMm, nameof(YMm));
        RequirePositive(errors, WidthMm, nameof(WidthMm));
        RequirePositive(errors, HeightMm, nameof(HeightMm));
        RequireNonNegative(errors, CornerRadiusMm, nameof(CornerRadiusMm));
    }
}

public sealed record CreateEllipseAction : CreateShapeAction
{
    public double XMm { get; init; }
    public double YMm { get; init; }
    public required double WidthMm { get; init; }
    public required double HeightMm { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireFinite(errors, XMm, nameof(XMm));
        RequireFinite(errors, YMm, nameof(YMm));
        RequirePositive(errors, WidthMm, nameof(WidthMm));
        RequirePositive(errors, HeightMm, nameof(HeightMm));
    }
}

public sealed record CreateLineAction : CreateShapeAction
{
    public required double X1Mm { get; init; }
    public required double Y1Mm { get; init; }
    public required double X2Mm { get; init; }
    public required double Y2Mm { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireFinite(errors, X1Mm, nameof(X1Mm));
        RequireFinite(errors, Y1Mm, nameof(Y1Mm));
        RequireFinite(errors, X2Mm, nameof(X2Mm));
        RequireFinite(errors, Y2Mm, nameof(Y2Mm));
        if (X1Mm == X2Mm && Y1Mm == Y2Mm)
        {
            errors.Add(Msg.Get("Validation.LineLength"));
        }
    }
}

public sealed record CreateTableAction : CreateShapeAction
{
    public double XMm { get; init; }
    public double YMm { get; init; }
    public required double WidthMm { get; init; }
    public required double HeightMm { get; init; }
    public required int Columns { get; init; }
    public required int Rows { get; init; }

    /// <summary>Optional cell texts, row by row. Missing rows/cells are left empty.</summary>
    public IReadOnlyList<IReadOnlyList<string>>? Cells { get; init; }

    public TextAlignment CellAlignment { get; init; } = TextAlignment.Left;
    public string? FontFamily { get; init; }
    public double? FontSizePt { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireFinite(errors, XMm, nameof(XMm));
        RequireFinite(errors, YMm, nameof(YMm));
        RequirePositive(errors, WidthMm, nameof(WidthMm));
        RequirePositive(errors, HeightMm, nameof(HeightMm));
        if (Columns is < 1 or > 200)
        {
            errors.Add(Msg.Get("Validation.Columns"));
        }

        if (Rows is < 1 or > 500)
        {
            errors.Add(Msg.Get("Validation.Rows"));
        }

        if (Cells is not null)
        {
            if (Cells.Count > Rows)
            {
                errors.Add(Msg.Get("Validation.CellsRows"));
            }

            if (Cells.Any(row => row is not null && row.Count > Columns))
            {
                errors.Add(Msg.Get("Validation.CellsColumns"));
            }
        }

        if (FontSizePt is { } size)
        {
            RequirePositive(errors, size, nameof(FontSizePt));
        }
    }
}

/// <summary>Imports a file (SVG, PDF, CDR, JPG, PNG, …) as editable objects or a bitmap.</summary>
public sealed record ImportFileAction : CorelAction
{
    public required string FilePath { get; init; }
    public string? Name { get; init; }
    public string? Layer { get; init; }

    /// <summary>Optional top-left position of the imported content.</summary>
    public double? XMm { get; init; }
    public double? YMm { get; init; }

    /// <summary>Optional box the import is scaled to fit inside, keeping its proportions.</summary>
    public double? FitWidthMm { get; init; }
    public double? FitHeightMm { get; init; }

    [JsonIgnore]
    public override bool CreatesObjects => true;

    protected override void ValidateCore(List<string> errors)
    {
        RequireText(errors, FilePath, nameof(FilePath));
        RequireFinite(errors, XMm, nameof(XMm));
        RequireFinite(errors, YMm, nameof(YMm));
        if (XMm is null != YMm is null)
        {
            errors.Add(Msg.Get("Validation.PositionTogether"));
        }

        if (FitWidthMm is null != FitHeightMm is null)
        {
            errors.Add(Msg.Get("Validation.FitTogether"));
        }
        else if (FitWidthMm is { } width && FitHeightMm is { } height)
        {
            RequirePositive(errors, width, nameof(FitWidthMm));
            RequirePositive(errors, height, nameof(FitHeightMm));
        }
    }
}

// ---- Transform --------------------------------------------------------------------------

/// <summary>
/// Moves shapes either by an offset (<see cref="DeltaXMm"/>/<see cref="DeltaYMm"/>; positive Y is down)
/// or to an absolute top-left position (<see cref="ToXMm"/>/<see cref="ToYMm"/>).
/// </summary>
public sealed record MoveAction : TargetedAction
{
    public double DeltaXMm { get; init; }
    public double DeltaYMm { get; init; }
    public double? ToXMm { get; init; }
    public double? ToYMm { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireFinite(errors, DeltaXMm, nameof(DeltaXMm));
        RequireFinite(errors, DeltaYMm, nameof(DeltaYMm));
        RequireFinite(errors, ToXMm, nameof(ToXMm));
        RequireFinite(errors, ToYMm, nameof(ToYMm));
        var absolute = ToXMm is not null || ToYMm is not null;
        var relative = DeltaXMm != 0 || DeltaYMm != 0;
        if (absolute && relative)
        {
            errors.Add(Msg.Get("Validation.MoveBoth"));
        }
        else if (!absolute && !relative)
        {
            errors.Add(Msg.Get("Validation.MoveNone"));
        }
    }
}

public sealed record ResizeAction : TargetedAction
{
    public double? WidthMm { get; init; }
    public double? HeightMm { get; init; }

    /// <summary>Alternative to explicit sizes: 50 halves the shape, 200 doubles it.</summary>
    public double? ScalePercent { get; init; }

    /// <summary>When only one of width/height is given, scale the other proportionally.</summary>
    public bool KeepAspectRatio { get; init; } = true;

    /// <summary>The point of the shape that stays in place.</summary>
    public PositionAnchor Anchor { get; init; } = PositionAnchor.Center;

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        if (WidthMm is { } width)
        {
            RequirePositive(errors, width, nameof(WidthMm));
        }

        if (HeightMm is { } height)
        {
            RequirePositive(errors, height, nameof(HeightMm));
        }

        if (ScalePercent is { } scale)
        {
            RequirePositive(errors, scale, nameof(ScalePercent));
        }

        var explicitSize = WidthMm is not null || HeightMm is not null;
        if (explicitSize == ScalePercent is not null)
        {
            errors.Add(Msg.Get("Validation.ResizeChoice"));
        }
    }
}

public sealed record RotateAction : TargetedAction
{
    /// <summary>Counter-clockwise degrees.</summary>
    public required double AngleDegrees { get; init; }

    /// <summary>False rotates by the angle; true sets the shape's rotation to the angle.</summary>
    public bool Absolute { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireFinite(errors, AngleDegrees, nameof(AngleDegrees));
    }
}

// ---- Content and style --------------------------------------------------------------------

public sealed record SetTextAction : TargetedAction
{
    public required string Text { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        if (Text is null)
        {
            errors.Add(Msg.Get("Validation.TextRequired"));
        }
    }
}

public sealed record SetFontAction : TargetedAction
{
    public string? FontFamily { get; init; }
    public double? FontSizePt { get; init; }
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public TextAlignment? Alignment { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        if (FontFamily is null && FontSizePt is null && Bold is null && Italic is null && Alignment is null)
        {
            errors.Add(Msg.Get("Validation.FontNone"));
        }

        if (FontFamily is not null && string.IsNullOrWhiteSpace(FontFamily))
        {
            errors.Add(Msg.Get("Validation.FontEmpty"));
        }

        if (FontSizePt is { } size)
        {
            RequirePositive(errors, size, nameof(FontSizePt));
        }
    }
}

public sealed record SetFillAction : TargetedAction
{
    /// <summary>#RRGGBB, or <c>null</c> to remove the fill.</summary>
    public string? Color { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireColor(errors, Color, nameof(Color));
    }
}

public sealed record SetOutlineAction : TargetedAction
{
    public string? Color { get; init; }
    public double? WidthMm { get; init; }

    /// <summary>True removes the outline; Color/WidthMm are then ignored.</summary>
    public bool Remove { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireColor(errors, Color, nameof(Color));
        RequireNonNegative(errors, WidthMm, nameof(WidthMm));
        if (!Remove && Color is null && WidthMm is null)
        {
            errors.Add(Msg.Get("Validation.OutlineNone"));
        }
    }
}

// ---- Arrange ------------------------------------------------------------------------------

public enum HorizontalAlign
{
    None,
    Left,
    Center,
    Right,
}

public enum VerticalAlign
{
    None,
    Top,
    Center,
    Bottom,
}

public enum AlignReference
{
    /// <summary>Align to the page.</summary>
    Page,

    /// <summary>Align the other targets to the first target, which stays in place.</summary>
    FirstTarget,

    /// <summary>Align inside the combined bounding box of all targets.</summary>
    TargetBounds,
}

public sealed record AlignAction : TargetedAction
{
    public HorizontalAlign Horizontal { get; init; }
    public VerticalAlign Vertical { get; init; }
    public AlignReference RelativeTo { get; init; } = AlignReference.Page;

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        if (Horizontal == HorizontalAlign.None && Vertical == VerticalAlign.None)
        {
            errors.Add(Msg.Get("Validation.AlignNone"));
        }
    }
}

public enum DistributeDirection
{
    Horizontal,
    Vertical,
}

public sealed record DistributeAction : TargetedAction
{
    public required DistributeDirection Direction { get; init; }

    /// <summary>Fixed gap between neighbours. When omitted, shapes are spread evenly between the outermost two.</summary>
    public double? SpacingMm { get; init; }

    [JsonIgnore]
    protected override int MinimumTargets => 2;

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireFinite(errors, SpacingMm, nameof(SpacingMm));
    }
}

public sealed record DuplicateAction : TargetedAction
{
    public double OffsetXMm { get; init; }
    public double OffsetYMm { get; init; }

    /// <summary>Number of copies per target; each copy is offset further than the previous one.</summary>
    public int Count { get; init; } = 1;

    [JsonIgnore]
    public override bool CreatesObjects => true;

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireFinite(errors, OffsetXMm, nameof(OffsetXMm));
        RequireFinite(errors, OffsetYMm, nameof(OffsetYMm));
        if (Count is < 1 or > 1000)
        {
            errors.Add(Msg.Get("Validation.DuplicateCount"));
        }
    }
}

public sealed record DeleteAction : TargetedAction
{
    [JsonIgnore]
    public override bool IsDestructive => true;
}

public sealed record GroupAction : TargetedAction
{
    public string? Name { get; init; }

    [JsonIgnore]
    public override bool CreatesObjects => true;

    [JsonIgnore]
    protected override int MinimumTargets => 2;
}

public sealed record UngroupAction : TargetedAction;

public sealed record MoveToLayerAction : TargetedAction
{
    public required string Layer { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireText(errors, Layer, nameof(Layer));
    }
}

public sealed record CreateLayerAction : CorelAction
{
    public required string Name { get; init; }

    protected override void ValidateCore(List<string> errors) => RequireText(errors, Name, nameof(Name));
}

public sealed record RenameObjectAction : TargetedAction
{
    public required string Name { get; init; }

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        RequireText(errors, Name, nameof(Name));
    }
}

public sealed record BringToFrontAction : TargetedAction;

public sealed record SendToBackAction : TargetedAction;

// ---- Output -------------------------------------------------------------------------------

/// <summary>An action that writes a file.</summary>
public abstract record OutputAction : CorelAction
{
    public required string FilePath { get; init; }

    [JsonIgnore]
    public abstract string Extension { get; }

    protected override void ValidateCore(List<string> errors) =>
        RequireFileExtension(errors, FilePath, nameof(FilePath), Extension);
}

public sealed record SaveDocumentAction : OutputAction
{
    [JsonIgnore]
    public override string Extension => ".cdr";
}

public sealed record ExportPdfAction : OutputAction
{
    [JsonIgnore]
    public override string Extension => ".pdf";
}

public sealed record ExportPngAction : OutputAction
{
    public int Dpi { get; init; } = 300;
    public bool Transparent { get; init; } = true;

    [JsonIgnore]
    public override string Extension => ".png";

    protected override void ValidateCore(List<string> errors)
    {
        base.ValidateCore(errors);
        if (Dpi is < 10 or > 2400)
        {
            errors.Add(Msg.Get("Validation.Dpi"));
        }
    }
}

public sealed record ExportSvgAction : OutputAction
{
    [JsonIgnore]
    public override string Extension => ".svg";
}

// ---- Helpers ------------------------------------------------------------------------------

/// <summary>
/// Ways an action can point at shapes:
/// <c>shape_017</c> (logical id from an inspection), <c>@actionId</c> (the shapes an earlier action
/// in the same plan created), <c>name:Logo</c> (object name) and <c>selection</c> (what the user has selected).
/// </summary>
public static partial class TargetRef
{
    public const string Selection = "selection";
    public const string NamePrefix = "name:";

    public static string ForAction(string actionId) => "@" + actionId;

    public static string ForName(string name) => NamePrefix + name;

    public static bool IsSelection(string? value) => string.Equals(value, Selection, StringComparison.OrdinalIgnoreCase);

    public static bool IsActionRef(string? value) => value is { Length: > 1 } && value[0] == '@';

    public static bool IsName(string? value) =>
        value is not null && value.Length > NamePrefix.Length && value.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsValid(string? value) =>
        LogicalShapeId.IsLogicalId(value) || IsSelection(value) || IsActionRef(value) || IsName(value);

    public static string ActionId(string actionRef) => actionRef[1..];

    public static string Name(string nameRef) => nameRef[NamePrefix.Length..];
}

public static partial class ColorHex
{
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex Pattern();
}

/// <summary>The registry of action types and their JSON discriminators.</summary>
public static class ActionTypes
{
    private static readonly IReadOnlyDictionary<Type, string> Names = typeof(CorelAction)
        .GetCustomAttributes<JsonDerivedTypeAttribute>()
        .ToDictionary(attribute => attribute.DerivedType, attribute => (string)attribute.TypeDiscriminator!);

    public static IReadOnlyCollection<Type> All => (IReadOnlyCollection<Type>)Names.Keys;

    public static IReadOnlyCollection<string> AllNames => (IReadOnlyCollection<string>)Names.Values;

    public static string NameOf(Type actionType) =>
        Names.TryGetValue(actionType, out var name) ? name : actionType.Name;
}
