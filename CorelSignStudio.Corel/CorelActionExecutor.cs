using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Localization;

namespace CorelSignStudio.Corel;

/// <summary>
/// Executes an <see cref="AutomationPlan"/> in CorelDRAW. The whole plan runs as one unit of work on the
/// service's dedicated STA thread, so nothing else can interleave COM calls with it.
/// </summary>
/// <remarks>
/// Rollback: when a plan modifies a document that was already open, all of its changes are wrapped in
/// one CorelDRAW command group. If an action fails the group is undone, leaving the document as it was
/// (and a successful plan is a single Ctrl+Z step for the user). Not rolled back: files already written
/// by save/export actions, and documents the plan itself created or opened — those stay open so the
/// user can see how far the plan got.
/// </remarks>
public sealed class CorelActionExecutor(CorelAutomationService service) : ICorelActionExecutor
{
    public Task<PlanExecutionResult> ExecuteAsync(
        AutomationPlan plan,
        ExecutionOptions? options = null,
        IProgress<ActionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return service.RunAsync("ExecutePlan", application =>
        {
            var result = PlanExecutionEngine.Run(
                plan,
                new CorelAutomationSession(application, service.Log),
                options,
                progress,
                cancellationToken);
            service.Log($"Plan '{plan.Name}' ({plan.Id}): {result.Summary}" +
                        (result.RollbackNote is null ? "" : $" Rollback: {result.RollbackNote}"));
            return result;
        }, cancellationToken);
    }
}

internal sealed class CorelAutomationSession(object applicationObject, Action<string>? log) : IAutomationSession
{
    private const int FilterPng = 802;
    private const int FilterSvg = 1345;
    private const int ExportCurrentPage = 1;
    private const int ImageTypeRgb = 4;
    private const int AntiAliasingNormal = 1;
    private const int TriStateTrue = -1;
    private const int TriStateFalse = 0;

    private readonly dynamic _application = applicationObject;
    private readonly Dictionary<string, List<int>> _createdByAction = new(StringComparer.Ordinal);
    private List<int> _initialSelection = [];
    private object? _document;
    private bool _groupOpen;
    private bool _groupHasContent;
    private bool _documentCreatedByPlan;

    private dynamic Document =>
        _document ?? throw new InvalidOperationException(Msg.Get("Corel.NoDocument"));

    private PageFrame Frame => PageFrame.Of(Document.ActivePage);

    public void Begin(AutomationPlan plan)
    {
        if (plan.Target == DocumentTarget.NewDocument)
        {
            return; // The first action creates or opens the document.
        }

        _document = (object?)_application.ActiveDocument
                    ?? throw new InvalidOperationException(Msg.Get("Corel.NoDocumentStart"));
        dynamic document = _document;
        document.Unit = CorelShapes.UnitMillimeter;
        _initialSelection = ReadSelection(document);

        document.BeginCommandGroup(string.IsNullOrWhiteSpace(plan.Name) ? Msg.Get("Corel.DefaultGroupName") : plan.Name);
        _groupOpen = true;
        try
        {
            // Guarantee the group is never empty: undoing an empty group would instead undo the user's
            // own last manual step. Creating and deleting a marker costs nothing and is invisible.
            dynamic marker = document.ActiveLayer.CreateRectangle2(0d, 0d, 0.01d, 0.01d);
            marker.Delete();
            _groupHasContent = true;
            RestoreSelection(document);
        }
        catch (Exception exception)
        {
            log?.Invoke($"Undo marker could not be created (active layer locked?): {exception.Message}");
        }
    }

    public void Complete() => EndGroup();

    public RollbackOutcome Rollback()
    {
        if (_documentCreatedByPlan || _document is null)
        {
            EndGroup();
            return new RollbackOutcome(false, Msg.Get(_document is null ? "Corel.Rollback.NoDocument" : "Corel.Rollback.OwnDocument"));
        }

        if (!_groupOpen)
        {
            return new RollbackOutcome(false, Msg.Get("Corel.Rollback.NoGroup"));
        }

        var hadContent = _groupHasContent;
        EndGroup();
        if (!hadContent)
        {
            return new RollbackOutcome(false, Msg.Get("Corel.Rollback.NotGrouped"));
        }

        Document.Undo(1);
        return new RollbackOutcome(true, Msg.Get("Corel.Rollback.Done"));
    }

    public ActionOutcome Execute(CorelAction action)
    {
        try
        {
            return ExecuteCore(action);
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            // The raw COM text is rarely meaningful to a user; it is kept as the inner exception for the log.
            throw new InvalidOperationException(Msg.Get("Corel.ComFailure"), exception);
        }
    }

    private ActionOutcome ExecuteCore(CorelAction action) => action switch
    {
        CreateDocumentAction a => CreateDocument(a),
        OpenDocumentAction a => OpenDocument(a),
        CloseDocumentAction => CloseDocument(),
        SetPageSizeAction a => SetPageSize(a),
        CreateTextAction a => Created(a, CreateText(a)),
        CreateRectangleAction a => Created(a, CreateRectangle(a)),
        CreateEllipseAction a => Created(a, CreateEllipse(a)),
        CreateLineAction a => Created(a, CreateLine(a)),
        CreatePolygonAction a => Created(a, CreatePolygon(a)),
        CreateTableAction a => CreateTable(a),
        ImportFileAction a => Created(a, ImportFile(a)),
        MoveAction a => Move(a),
        ResizeAction a => Resize(a),
        RotateAction a => Rotate(a),
        SetTextAction a => SetText(a),
        SetFontAction a => SetFont(a),
        SetFillAction a => SetFill(a),
        SetOutlineAction a => SetOutline(a),
        AlignAction a => Align(a),
        DistributeAction a => Distribute(a),
        DuplicateAction a => Duplicate(a),
        DeleteAction a => Delete(a),
        GroupAction a => Group(a),
        UngroupAction a => Ungroup(a),
        MoveToLayerAction a => MoveToLayer(a),
        CreateLayerAction a => CreateLayer(a),
        RenameObjectAction a => Rename(a),
        BringToFrontAction a => Order(a, toFront: true),
        SendToBackAction a => Order(a, toFront: false),
        SaveDocumentAction a => SaveDocument(a),
        ExportPdfAction a => ExportPdf(a),
        ExportPngAction a => ExportPng(a),
        ExportSvgAction a => ExportSvg(a),
        _ => throw new NotSupportedException(Msg.Format("Corel.NotSupported", action.TypeName)),
    };

    // ---- Document -------------------------------------------------------------------------

    private ActionOutcome CreateDocument(CreateDocumentAction action)
    {
        EndGroup();
        _document = (object)_application.CreateDocument();
        _documentCreatedByPlan = true;
        _initialSelection = [];
        dynamic document = _document;
        document.Unit = CorelShapes.UnitMillimeter;
        document.ActivePage.SetSize(action.WidthMm, action.HeightMm);
        return new ActionOutcome { Message = Msg.Format("Corel.DocumentCreated", Msg.Number(action.WidthMm), Msg.Number(action.HeightMm)) };
    }

    private ActionOutcome OpenDocument(OpenDocumentAction action)
    {
        var fullPath = Path.GetFullPath(action.FilePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(Msg.Format("Corel.FileNotFound", fullPath), fullPath);
        }

        EndGroup();
        _document = (object)_application.OpenDocument(fullPath);
        _documentCreatedByPlan = true;
        _initialSelection = [];
        Document.Unit = CorelShapes.UnitMillimeter;
        return new ActionOutcome { Message = Msg.Format("Corel.DocumentOpened", fullPath) };
    }

    private ActionOutcome CloseDocument()
    {
        dynamic document = Document;
        EndGroup();
        document.Dirty = false; // Suppress the "save changes?" prompt; saving is a separate, explicit action.
        document.Close();
        _document = null;
        return new ActionOutcome { Message = Msg.Get("Corel.DocumentClosed") };
    }

    private ActionOutcome SetPageSize(SetPageSizeAction action)
    {
        Document.ActivePage.SetSize(action.WidthMm, action.HeightMm);
        return new ActionOutcome { Message = Msg.Format("Corel.PageSizeSet", Msg.Number(action.WidthMm), Msg.Number(action.HeightMm)) };
    }

    // ---- Create ---------------------------------------------------------------------------

    private object CreateText(CreateTextAction action)
    {
        dynamic layer = LayerFor(action.Layer);
        PageFrame frame = Frame;
        var font = string.IsNullOrWhiteSpace(action.FontFamily) ? "Arial" : action.FontFamily;
        var size = (float)(action.FontSizePt ?? 24d);
        var bold = action.Bold ? TriStateTrue : TriStateFalse;
        var italic = action.Italic ? TriStateTrue : TriStateFalse;

        dynamic shape;
        if (action.FrameWidthMm is { } frameWidth && action.FrameHeightMm is { } frameHeight)
        {
            var (left, top) = action.Anchor == PositionAnchor.Center
                ? (action.XMm - (frameWidth / 2), action.YMm - (frameHeight / 2))
                : (action.XMm, action.YMm);
            shape = layer.CreateParagraphText(
                frame.ToCorelX(left),
                frame.ToCorelY(top),
                frame.ToCorelX(left + frameWidth),
                frame.ToCorelY(top + frameHeight),
                action.Text, 0, 0, font, size, bold, italic);
        }
        else
        {
            shape = layer.CreateArtisticText(0d, 0d, action.Text, 0, 0, font, size, bold, italic);
            double width = shape.SizeWidth;
            double height = shape.SizeHeight;
            if (action.Anchor == PositionAnchor.Center)
            {
                shape.CenterX = frame.ToCorelX(action.XMm);
                shape.CenterY = frame.ToCorelY(action.YMm);
            }
            else
            {
                shape.CenterX = frame.ToCorelX(action.XMm) + (width / 2);
                shape.CenterY = frame.ToCorelY(action.YMm) - (height / 2);
            }
        }

        if (action.Alignment != TextAlignment.Left)
        {
            shape.Text.Story.Alignment = CorelAlignment(action.Alignment);
        }

        ApplyCreateStyle(shape, action);
        return shape;
    }

    private object CreateRectangle(CreateRectangleAction action)
    {
        dynamic layer = LayerFor(action.Layer);
        PageFrame frame = Frame;
        dynamic shape = layer.CreateRectangle2(
            frame.ToCorelX(action.XMm),
            frame.ToCorelY(action.YMm + action.HeightMm),
            action.WidthMm,
            action.HeightMm,
            action.CornerRadiusMm,
            action.CornerRadiusMm,
            action.CornerRadiusMm,
            action.CornerRadiusMm);
        ApplyCreateStyle(shape, action);
        return shape;
    }

    private object CreateEllipse(CreateEllipseAction action)
    {
        dynamic layer = LayerFor(action.Layer);
        PageFrame frame = Frame;
        dynamic shape = layer.CreateEllipse(
            frame.ToCorelX(action.XMm),
            frame.ToCorelY(action.YMm),
            frame.ToCorelX(action.XMm + action.WidthMm),
            frame.ToCorelY(action.YMm + action.HeightMm));
        ApplyCreateStyle(shape, action);
        return shape;
    }

    private object CreateLine(CreateLineAction action)
    {
        dynamic layer = LayerFor(action.Layer);
        PageFrame frame = Frame;
        dynamic shape = layer.CreateLineSegment(
            frame.ToCorelX(action.X1Mm),
            frame.ToCorelY(action.Y1Mm),
            frame.ToCorelX(action.X2Mm),
            frame.ToCorelY(action.Y2Mm));
        ApplyCreateStyle(shape, action);
        return shape;
    }

    private object CreatePolygon(CreatePolygonAction action)
    {
        dynamic layer = LayerFor(action.Layer);
        PageFrame frame = Frame;

        // Verified signatures (CorelDRAW 2026): Application.CreateCurve(Document), Curve.CreateSubPath(x, y),
        // SubPath.AppendLineSegment(x, y), SubPath.Closed, Layer.CreateCurve(Curve).
        dynamic curve = _application.CreateCurve(Document);
        dynamic path = curve.CreateSubPath(frame.ToCorelX(action.PointsMm[0][0]), frame.ToCorelY(action.PointsMm[0][1]));
        foreach (var point in action.PointsMm.Skip(1))
        {
            path.AppendLineSegment(frame.ToCorelX(point[0]), frame.ToCorelY(point[1]));
        }

        if (action.Closed)
        {
            path.Closed = true;
        }

        dynamic shape = layer.CreateCurve(curve);
        ApplyCreateStyle(shape, action);
        return shape;
    }

    private ActionOutcome CreateTable(CreateTableAction action)
    {
        dynamic layer = LayerFor(action.Layer);
        PageFrame frame = Frame;

        // Verified against CorelDRAW 2026: CreateCustomShape("Table", left, top, right, bottom, columns, rows).
        dynamic shape = layer.CreateCustomShape(
            "Table",
            frame.ToCorelX(action.XMm),
            frame.ToCorelY(action.YMm),
            frame.ToCorelX(action.XMm + action.WidthMm),
            frame.ToCorelY(action.YMm + action.HeightMm),
            action.Columns,
            action.Rows);

        var styleEveryCell = action.CellAlignment != TextAlignment.Left || action.FontFamily is not null || action.FontSizePt is not null;
        var skipped = 0;
        if (action.Cells is not null || styleEveryCell)
        {
            dynamic table = shape.Custom;
            for (var row = 1; row <= action.Rows; row++)
            {
                for (var column = 1; column <= action.Columns; column++)
                {
                    var text = action.Cells is { } cells && row <= cells.Count && cells[row - 1] is { } cellRow && column <= cellRow.Count
                        ? cellRow[column - 1]
                        : null;
                    if (text is null && !styleEveryCell)
                    {
                        continue;
                    }

                    try
                    {
                        dynamic story = table.Cell(column, row).TextShape.Text.Story;
                        if (text is not null)
                        {
                            story.Text = text;
                        }

                        if (action.FontFamily is not null)
                        {
                            story.Font = action.FontFamily;
                        }

                        if (action.FontSizePt is { } size)
                        {
                            story.Size = (float)size;
                        }

                        if (action.CellAlignment != TextAlignment.Left)
                        {
                            story.Alignment = CorelAlignment(action.CellAlignment);
                        }
                    }
                    catch
                    {
                        skipped++;
                    }
                }
            }
        }

        ApplyCreateStyle(shape, action);
        var outcome = Created(action, (object)shape);
        return outcome with
        {
            Message = Msg.Format("Corel.TableCreated", action.Columns, action.Rows) +
                      (skipped > 0 ? Msg.Format("Corel.TableCellsSkipped", skipped) : ""),
        };
    }

    private object ImportFile(ImportFileAction action)
    {
        var fullPath = Path.GetFullPath(action.FilePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(Msg.Format("Corel.FileNotFound", fullPath), fullPath);
        }

        dynamic document = Document;
        dynamic layer = LayerFor(action.Layer);
        PageFrame frame = Frame;

        // Same call shape as the verified CorelAutomationService.ImportAsset.
        dynamic importOptions = _application.CreateStructImportOptions();
        dynamic importFilter = layer.ImportEx(fullPath, 0, importOptions);
        importFilter.Finish();

        dynamic imported = document.SelectionRange;
        int count = imported.Count;
        if (count == 0)
        {
            throw new InvalidOperationException(Msg.Format("Corel.ImportedNothing", fullPath));
        }

        dynamic shape = count == 1 ? imported[1] : imported.Group();

        double width = shape.SizeWidth;
        double height = shape.SizeHeight;
        if (action.FitWidthMm is { } fitWidth && action.FitHeightMm is { } fitHeight && width > 0 && height > 0)
        {
            var scale = Math.Min(fitWidth / width, fitHeight / height);
            shape.SetSize(width * scale, height * scale);
            width *= scale;
            height *= scale;
        }

        if (action.XMm is { } x && action.YMm is { } y)
        {
            shape.CenterX = frame.ToCorelX(x) + (width / 2);
            shape.CenterY = frame.ToCorelY(y) - (height / 2);
        }

        if (!string.IsNullOrWhiteSpace(action.Name))
        {
            shape.Name = action.Name;
        }

        return shape;
    }

    // ---- Transform ------------------------------------------------------------------------

    private ActionOutcome Move(MoveAction action)
    {
        var shapes = Resolve(action);
        PageFrame frame = Frame;
        foreach (dynamic shape in shapes)
        {
            if (action.ToXMm is not null || action.ToYMm is not null)
            {
                if (action.ToXMm is { } x)
                {
                    shape.LeftX = frame.ToCorelX(x);
                }

                if (action.ToYMm is { } y)
                {
                    shape.TopY = frame.ToCorelY(y);
                }
            }
            else
            {
                shape.Move(action.DeltaXMm, -action.DeltaYMm);
            }
        }

        return Modified(shapes);
    }

    private ActionOutcome Resize(ResizeAction action)
    {
        var shapes = Resolve(action);
        foreach (dynamic shape in shapes)
        {
            double currentWidth = shape.SizeWidth;
            double currentHeight = shape.SizeHeight;
            double width;
            double height;
            if (action.ScalePercent is { } percent)
            {
                width = currentWidth * percent / 100;
                height = currentHeight * percent / 100;
            }
            else
            {
                width = action.WidthMm ?? (action.KeepAspectRatio && currentHeight > 0 ? currentWidth * action.HeightMm!.Value / currentHeight : currentWidth);
                height = action.HeightMm ?? (action.KeepAspectRatio && currentWidth > 0 ? currentHeight * action.WidthMm!.Value / currentWidth : currentHeight);
            }

            double centerX = shape.CenterX;
            double centerY = shape.CenterY;
            double left = shape.LeftX;
            double top = shape.TopY;
            shape.SetSize(width, height);
            if (action.Anchor == PositionAnchor.Center)
            {
                shape.CenterX = centerX;
                shape.CenterY = centerY;
            }
            else
            {
                shape.LeftX = left;
                shape.TopY = top;
            }
        }

        return Modified(shapes);
    }

    private ActionOutcome Rotate(RotateAction action)
    {
        var shapes = Resolve(action);
        foreach (dynamic shape in shapes)
        {
            if (action.Absolute)
            {
                shape.RotationAngle = action.AngleDegrees;
            }
            else
            {
                shape.Rotate(action.AngleDegrees);
            }
        }

        return Modified(shapes);
    }

    // ---- Content and style ----------------------------------------------------------------

    private ActionOutcome SetText(SetTextAction action)
    {
        var shapes = Resolve(action);
        foreach (dynamic shape in shapes)
        {
            RequireText(shape);
        }

        foreach (dynamic shape in shapes)
        {
            shape.Text.Story.Text = action.Text;
        }

        return Modified(shapes);
    }

    private ActionOutcome SetFont(SetFontAction action)
    {
        var shapes = Resolve(action);
        foreach (dynamic shape in shapes)
        {
            RequireText(shape);
        }

        foreach (dynamic shape in shapes)
        {
            dynamic story = shape.Text.Story;
            if (action.FontFamily is not null)
            {
                story.Font = action.FontFamily;
            }

            if (action.FontSizePt is { } size)
            {
                story.Size = (float)size;
            }

            if (action.Bold is { } bold)
            {
                story.Bold = bold;
            }

            if (action.Italic is { } italic)
            {
                story.Italic = italic;
            }

            if (action.Alignment is { } alignment)
            {
                story.Alignment = CorelAlignment(alignment);
            }
        }

        return Modified(shapes);
    }

    private ActionOutcome SetFill(SetFillAction action)
    {
        var shapes = Resolve(action);
        foreach (dynamic shape in shapes)
        {
            ApplyFill(shape, action.Color, removeWhenNull: true);
        }

        return Modified(shapes);
    }

    private ActionOutcome SetOutline(SetOutlineAction action)
    {
        var shapes = Resolve(action);
        foreach (dynamic shape in shapes)
        {
            if (action.Remove)
            {
                shape.Outline.SetNoOutline();
            }
            else
            {
                ApplyOutline(shape, action.Color, action.WidthMm);
            }
        }

        return Modified(shapes);
    }

    // ---- Arrange --------------------------------------------------------------------------

    private ActionOutcome Align(AlignAction action)
    {
        var shapes = Resolve(action);
        var boxes = shapes.Select(Box.Of).ToList();

        Box reference;
        var skipFirst = false;
        switch (action.RelativeTo)
        {
            case AlignReference.FirstTarget:
                reference = boxes[0];
                skipFirst = true;
                break;
            case AlignReference.TargetBounds:
                reference = Box.Union(boxes);
                break;
            default:
                PageFrame frame = Frame;
                reference = new Box(frame.Left, frame.Top, frame.Width, frame.Height);
                break;
        }

        for (var index = skipFirst ? 1 : 0; index < shapes.Count; index++)
        {
            var box = boxes[index];
            var deltaX = action.Horizontal switch
            {
                HorizontalAlign.Left => reference.Left - box.Left,
                HorizontalAlign.Center => reference.CenterX - box.CenterX,
                HorizontalAlign.Right => reference.Right - box.Right,
                _ => 0,
            };
            var deltaY = action.Vertical switch
            {
                VerticalAlign.Top => reference.Top - box.Top,
                VerticalAlign.Center => reference.CenterY - box.CenterY,
                VerticalAlign.Bottom => reference.Bottom - box.Bottom,
                _ => 0,
            };
            if (deltaX != 0 || deltaY != 0)
            {
                ((dynamic)shapes[index]).Move(deltaX, deltaY);
            }
        }

        return Modified(shapes);
    }

    private ActionOutcome Distribute(DistributeAction action)
    {
        var shapes = Resolve(action);
        if (shapes.Count < 2)
        {
            throw new InvalidOperationException(Msg.Get("Corel.DistributeNeedsTwo"));
        }

        var horizontal = action.Direction == DistributeDirection.Horizontal;
        var items = shapes.Select(shape => (Shape: shape, Box: Box.Of(shape)))
            .OrderBy(item => horizontal ? item.Box.Left : -item.Box.Top)
            .ToList();

        double gap;
        if (action.SpacingMm is { } spacing)
        {
            gap = spacing;
        }
        else
        {
            var span = horizontal
                ? items[^1].Box.Right - items[0].Box.Left
                : items[0].Box.Top - items[^1].Box.Bottom;
            var occupied = items.Sum(item => horizontal ? item.Box.Width : item.Box.Height);
            gap = (span - occupied) / (items.Count - 1);
        }

        // "cursor" is the next free position: a left edge when horizontal, a top edge when vertical.
        var cursor = horizontal ? items[0].Box.Left : items[0].Box.Top;
        foreach (var (shape, box) in items)
        {
            dynamic corelShape = shape;
            if (horizontal)
            {
                var delta = cursor - box.Left;
                if (Math.Abs(delta) > 1e-9)
                {
                    corelShape.Move(delta, 0d);
                }

                cursor += box.Width + gap;
            }
            else
            {
                var delta = cursor - box.Top;
                if (Math.Abs(delta) > 1e-9)
                {
                    corelShape.Move(0d, delta);
                }

                cursor -= box.Height + gap;
            }
        }

        return Modified(shapes);
    }

    private ActionOutcome Duplicate(DuplicateAction action)
    {
        var shapes = Resolve(action);
        var copies = new List<object>();
        foreach (dynamic shape in shapes)
        {
            for (var copy = 1; copy <= action.Count; copy++)
            {
                copies.Add((object)shape.Duplicate(action.OffsetXMm * copy, -action.OffsetYMm * copy));
            }
        }

        return Created(action, copies);
    }

    private ActionOutcome Delete(DeleteAction action)
    {
        var shapes = Resolve(action);
        var ids = shapes.Select(shape => (string)CorelShapes.LogicalId(shape)).ToArray();
        foreach (dynamic shape in shapes)
        {
            shape.Delete();
        }

        return new ActionOutcome { ModifiedObjectIds = ids, Message = Msg.Format("Corel.Deleted", ids.Length) };
    }

    private ActionOutcome Group(GroupAction action)
    {
        var shapes = Resolve(action);
        if (shapes.Count < 2)
        {
            throw new InvalidOperationException(Msg.Get("Corel.GroupNeedsTwo"));
        }

        dynamic range = _application.CreateShapeRange();
        foreach (dynamic shape in shapes)
        {
            range.Add(shape);
        }

        // Observed with CorelDRAW 2026: ShapeRange.Group() can return nothing even though the group was
        // created (seen with artistic text). The new group is then the members' common parent.
        object? group = range.Group();
        group ??= ((dynamic)shapes[0]).ParentGroup;
        if (group is null)
        {
            // Last resort: group through the selection, which always yields the group shape.
            dynamic document = Document;
            document.ClearSelection();
            foreach (dynamic shape in shapes)
            {
                shape.AddToSelection();
            }

            group = document.Selection().Group();
        }

        if (group is null)
        {
            throw new InvalidOperationException(Msg.Get("Corel.ComFailure"));
        }

        if (!string.IsNullOrWhiteSpace(action.Name))
        {
            ((dynamic)group).Name = action.Name;
        }

        return Created(action, group);
    }

    private ActionOutcome Ungroup(UngroupAction action)
    {
        var shapes = Resolve(action);
        var released = new List<object>();
        foreach (dynamic shape in shapes)
        {
            if ((int)shape.Type != CorelShapes.ShapeTypeGroup)
            {
                throw new InvalidOperationException(Msg.Format("Corel.NotAGroup", (string)CorelShapes.LogicalId(shape)));
            }

            dynamic range = shape.UngroupEx();
            int count = range.Count;
            for (var index = 1; index <= count; index++)
            {
                released.Add((object)range[index]);
            }
        }

        return Modified(released);
    }

    private ActionOutcome MoveToLayer(MoveToLayerAction action)
    {
        var shapes = Resolve(action);
        dynamic layer = FindLayer(action.Layer)
                        ?? throw new InvalidOperationException(Msg.Format("Corel.LayerMissing", action.Layer));
        foreach (dynamic shape in shapes)
        {
            shape.MoveToLayer(layer);
        }

        return Modified(shapes);
    }

    private ActionOutcome CreateLayer(CreateLayerAction action)
    {
        object? existing = FindLayer(action.Name);
        if (existing is not null)
        {
            ((dynamic)existing).Activate();
            return new ActionOutcome { Message = Msg.Format("Corel.LayerExists", action.Name) };
        }

        Document.ActivePage.CreateLayer(action.Name);
        return new ActionOutcome { Message = Msg.Format("Corel.LayerCreated", action.Name) };
    }

    private ActionOutcome Rename(RenameObjectAction action)
    {
        var shapes = Resolve(action);
        foreach (dynamic shape in shapes)
        {
            shape.Name = action.Name;
        }

        return Modified(shapes);
    }

    private ActionOutcome Order(TargetedAction action, bool toFront)
    {
        var shapes = Resolve(action);
        foreach (dynamic shape in shapes)
        {
            if (toFront)
            {
                shape.OrderToFront();
            }
            else
            {
                shape.OrderToBack();
            }
        }

        return Modified(shapes);
    }

    // ---- Output ---------------------------------------------------------------------------

    private ActionOutcome SaveDocument(SaveDocumentAction action)
    {
        var fullPath = PrepareOutputPath(action.FilePath);
        object document = Document;
        CorelAutomationService.SaveCdrCore((object)_application, document, fullPath, log);
        return Produced(fullPath);
    }

    private ActionOutcome ExportPdf(ExportPdfAction action)
    {
        var fullPath = PrepareOutputPath(action.FilePath);
        Document.PublishToPDF(fullPath);
        return Produced(fullPath);
    }

    private ActionOutcome ExportPng(ExportPngAction action)
    {
        var fullPath = PrepareOutputPath(action.FilePath);
        dynamic document = Document;

        // Exports the artwork on the active page (its bounding box) at the requested resolution.
        dynamic options = _application.CreateStructExportOptions();
        options.ImageType = ImageTypeRgb;
        options.AntiAliasingType = AntiAliasingNormal;
        options.Transparent = action.Transparent;
        options.MaintainAspect = true;
        options.ResolutionX = action.Dpi;
        options.ResolutionY = action.Dpi;
        ExportWithOptions(document, fullPath, FilterPng, options);
        return Produced(fullPath);
    }

    private ActionOutcome ExportSvg(ExportSvgAction action)
    {
        var fullPath = PrepareOutputPath(action.FilePath);
        ExportWithOptions(Document, fullPath, FilterSvg, _application.CreateStructExportOptions());
        return Produced(fullPath);
    }

    /// <summary>
    /// Verified against CorelDRAW 2026: ExportEx only binds late when every argument, including the
    /// "optional" palette options, is supplied — shorter calls fail with DISP_E_TYPEMISMATCH.
    /// </summary>
    private void ExportWithOptions(dynamic document, string fullPath, int filter, dynamic options)
    {
        dynamic palette = _application.CreateStructPaletteOptions();
        dynamic exportFilter = document.ExportEx(fullPath, filter, ExportCurrentPage, options, palette);
        exportFilter.Finish();
    }

    // ---- Helpers --------------------------------------------------------------------------

    private List<object> Resolve(TargetedAction action)
    {
        dynamic document = Document;
        var shapes = new List<object>();
        var seen = new HashSet<int>();

        void Add(object? shape, string reference)
        {
            if (shape is null)
            {
                throw new InvalidOperationException(Msg.Format("Corel.TargetNotFound", reference));
            }

            if (seen.Add((int)((dynamic)shape).StaticID))
            {
                shapes.Add(shape);
            }
        }

        foreach (var reference in action.Targets)
        {
            if (TargetRef.IsSelection(reference))
            {
                if (_initialSelection.Count == 0)
                {
                    throw new InvalidOperationException(Msg.Get("Corel.NothingSelected"));
                }

                foreach (var staticId in _initialSelection)
                {
                    Add((object?)CorelShapes.FindByStaticId(document, staticId), reference);
                }
            }
            else if (TargetRef.IsActionRef(reference))
            {
                if (!_createdByAction.TryGetValue(TargetRef.ActionId(reference), out var ids) || ids.Count == 0)
                {
                    throw new InvalidOperationException(Msg.Format("Corel.StepCreatedNothing", reference));
                }

                foreach (var staticId in ids)
                {
                    Add((object?)CorelShapes.FindByStaticId(document, staticId), reference);
                }
            }
            else if (TargetRef.IsName(reference))
            {
                dynamic matches = document.ActivePage.Shapes.FindShapes(TargetRef.Name(reference), 0, true);
                int count = matches.Count;
                if (count == 0)
                {
                    throw new InvalidOperationException(Msg.Format("Corel.NoObjectNamed", TargetRef.Name(reference)));
                }

                for (var index = 1; index <= count; index++)
                {
                    Add((object)matches[index], reference);
                }
            }
            else if (LogicalShapeId.TryGetNativeId(reference, out var nativeId))
            {
                Add((object?)CorelShapes.FindByStaticId(document, nativeId), reference);
            }
            else
            {
                throw new InvalidOperationException(Msg.Format("Corel.InvalidTarget", reference));
            }
        }

        return shapes;
    }

    private dynamic LayerFor(string? layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName))
        {
            return Document.ActiveLayer;
        }

        return FindLayer(layerName)
               ?? throw new InvalidOperationException(Msg.Format("Corel.LayerMissing", layerName));
    }

    private object? FindLayer(string layerName)
    {
        dynamic layers = Document.ActivePage.Layers;
        int count = layers.Count;
        for (var index = 1; index <= count; index++)
        {
            dynamic layer = layers[index];
            if (string.Equals((string)layer.Name, layerName, StringComparison.OrdinalIgnoreCase))
            {
                return layer;
            }
        }

        return null;
    }

    private void ApplyCreateStyle(dynamic shape, CreateShapeAction action)
    {
        ApplyFill(shape, action.FillColor, removeWhenNull: false);
        if (action.OutlineColor is not null || action.OutlineWidthMm is not null)
        {
            if (action.OutlineWidthMm == 0)
            {
                shape.Outline.SetNoOutline();
            }
            else
            {
                ApplyOutline(shape, action.OutlineColor, action.OutlineWidthMm);
            }
        }

        if (!string.IsNullOrWhiteSpace(action.Name))
        {
            shape.Name = action.Name;
        }
    }

    private static void ApplyFill(dynamic shape, string? colorHex, bool removeWhenNull)
    {
        if (colorHex is not null)
        {
            var (red, green, blue) = CorelShapes.ParseRgb(colorHex);
            shape.Fill.UniformColor.RGBAssign(red, green, blue);
        }
        else if (removeWhenNull)
        {
            shape.Fill.ApplyNoFill();
        }
    }

    private static void ApplyOutline(dynamic shape, string? colorHex, double? widthMm)
    {
        // Set the width first: assigning a colour to a shape without an outline has no visible effect.
        if (widthMm is { } width)
        {
            shape.Outline.Width = width;
        }
        else if ((int)shape.Outline.Type == 0)
        {
            shape.Outline.Width = 0.2;
        }

        if (colorHex is not null)
        {
            var (red, green, blue) = CorelShapes.ParseRgb(colorHex);
            shape.Outline.Color.RGBAssign(red, green, blue);
        }
    }

    private static void RequireText(dynamic shape)
    {
        if ((int)shape.Type != CorelShapes.ShapeTypeText)
        {
            throw new InvalidOperationException(Msg.Format(
                "Corel.NotText", (string)CorelShapes.LogicalId(shape), CorelShapes.NativeTypeName((int)shape.Type)));
        }
    }

    private static int CorelAlignment(TextAlignment alignment) => alignment switch
    {
        TextAlignment.Center => 3,
        TextAlignment.Right => 2,
        _ => 1,
    };

    private static string PrepareOutputPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    private static ActionOutcome Produced(string fullPath)
    {
        if (!File.Exists(fullPath) || new FileInfo(fullPath).Length == 0)
        {
            throw new IOException(Msg.Format("Corel.FileNotCreated", fullPath));
        }

        return new ActionOutcome { ProducedFiles = [fullPath], Message = Msg.Format("Corel.FileWritten", fullPath) };
    }

    private ActionOutcome Created(CorelAction action, object shape) => Created(action, [shape]);

    private ActionOutcome Created(CorelAction action, IReadOnlyList<object> shapes)
    {
        var nativeIds = shapes.Select(shape => (int)((dynamic)shape).StaticID).ToList();
        _createdByAction[action.Id] = nativeIds;
        return new ActionOutcome { CreatedObjectIds = nativeIds.Select(LogicalShapeId.FromNativeId).ToArray() };
    }

    private static ActionOutcome Modified(IReadOnlyList<object> shapes) => new()
    {
        ModifiedObjectIds = shapes.Select(shape => (string)CorelShapes.LogicalId(shape)).ToArray(),
    };

    private static List<int> ReadSelection(dynamic document)
    {
        var ids = new List<int>();
        try
        {
            dynamic selection = document.SelectionRange;
            int count = selection.Count;
            for (var index = 1; index <= count; index++)
            {
                ids.Add((int)selection[index].StaticID);
            }
        }
        catch
        {
            // No selection.
        }

        return ids;
    }

    private void RestoreSelection(dynamic document)
    {
        try
        {
            document.ClearSelection();
            foreach (var staticId in _initialSelection)
            {
                object? shape = CorelShapes.FindByStaticId(document, staticId);
                if (shape is not null)
                {
                    ((dynamic)shape).AddToSelection();
                }
            }
        }
        catch
        {
            // Selection is a convenience; targets are resolved from the captured ids regardless.
        }
    }

    private void EndGroup()
    {
        if (!_groupOpen || _document is null)
        {
            _groupOpen = false;
            return;
        }

        _groupOpen = false;
        _groupHasContent = false;
        ((dynamic)_document).EndCommandGroup();
    }

    /// <summary>A bounding box in CorelDRAW coordinates (Y grows upwards).</summary>
    private readonly record struct Box(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top - Height;
        public double CenterX => Left + (Width / 2);
        public double CenterY => Top - (Height / 2);

        public static Box Of(object shapeObject)
        {
            dynamic shape = shapeObject;
            return new Box((double)shape.LeftX, (double)shape.TopY, (double)shape.SizeWidth, (double)shape.SizeHeight);
        }

        public static Box Union(IReadOnlyList<Box> boxes)
        {
            var left = boxes.Min(box => box.Left);
            var top = boxes.Max(box => box.Top);
            return new Box(left, top, boxes.Max(box => box.Right) - left, top - boxes.Min(box => box.Bottom));
        }
    }
}
