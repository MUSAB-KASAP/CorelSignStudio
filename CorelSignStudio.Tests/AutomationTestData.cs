using CorelSignStudio.Domain.Automation;

namespace CorelSignStudio.Tests;

internal static class AutomationTestData
{
    /// <summary>One valid, fully-populated sample of every registered action type.</summary>
    public static IReadOnlyList<CorelAction> OneOfEveryAction() =>
    [
        new CreateDocumentAction { Id = "doc", WidthMm = 500, HeightMm = 700 },
        new OpenDocumentAction { Id = "open", FilePath = @"C:\in\source.cdr" },
        new CloseDocumentAction { Id = "close" },
        new SetPageSizeAction { Id = "page", WidthMm = 210, HeightMm = 297 },
        new CreateTextAction
        {
            Id = "title", Text = "YASAKTIR", XMm = 80, YMm = 520, FontFamily = "Arial", FontSizePt = 72, Bold = true,
            Alignment = TextAlignment.Center, FillColor = "#D8202A", Name = "Title", Note = "main title",
        },
        new CreateRectangleAction { Id = "rect", XMm = 10, YMm = 10, WidthMm = 100, HeightMm = 50, CornerRadiusMm = 3, FillColor = "#FFFFFF", OutlineColor = "#000000", OutlineWidthMm = 0.5 },
        new CreateEllipseAction { Id = "ellipse", XMm = 20, YMm = 20, WidthMm = 60, HeightMm = 60, Layer = "Artwork" },
        new CreateLineAction { Id = "line", X1Mm = 0, Y1Mm = 0, X2Mm = 100, Y2Mm = 0, OutlineWidthMm = 1 },
        new CreatePolygonAction { Id = "triangle", PointsMm = [[100, 20], [180, 160], [20, 160]], FillColor = "#FFFF00", OutlineColor = "#000000", OutlineWidthMm = 2 },
        new CreateTableAction
        {
            Id = "table", XMm = 10, YMm = 10, WidthMm = 180, HeightMm = 100, Columns = 2, Rows = 2,
            Cells = [["A1", "B1"], ["A2", "B2"]], CellAlignment = TextAlignment.Center, FontSizePt = 10,
        },
        new ImportFileAction { Id = "logo", FilePath = @"C:\assets\logo.svg", XMm = 5, YMm = 5, FitWidthMm = 40, FitHeightMm = 40, Name = "Logo" },
        new MoveAction { Id = "move", Targets = ["shape_001"], DeltaXMm = 10, DeltaYMm = -5 },
        new ResizeAction { Id = "resize", Targets = ["@logo"], WidthMm = 100, HeightMm = 50, KeepAspectRatio = false, Anchor = PositionAnchor.TopLeft },
        new RotateAction { Id = "rotate", Targets = ["name:Logo"], AngleDegrees = 45, Absolute = true },
        new SetTextAction { Id = "settext", Targets = ["@title"], Text = "HELLO" },
        new SetFontAction { Id = "font", Targets = ["@title"], FontFamily = "Verdana", FontSizePt = 36, Bold = false, Italic = true, Alignment = TextAlignment.Right },
        new SetFillAction { Id = "fill", Targets = ["@rect"], Color = "#00FF00" },
        new SetOutlineAction { Id = "outline", Targets = ["@rect"], Color = "#0000FF", WidthMm = 1.5 },
        new AlignAction { Id = "align", Targets = ["@title", "@rect"], Horizontal = HorizontalAlign.Center, Vertical = VerticalAlign.Top, RelativeTo = AlignReference.FirstTarget },
        new DistributeAction { Id = "distribute", Targets = ["@rect", "@ellipse", "@line"], Direction = DistributeDirection.Horizontal, SpacingMm = 5 },
        new DuplicateAction { Id = "duplicate", Targets = ["@rect"], OffsetXMm = 20, OffsetYMm = 0, Count = 3 },
        new DeleteAction { Id = "delete", Targets = ["shape_042"] },
        new GroupAction { Id = "group", Targets = ["@rect", "@ellipse"], Name = "Badge" },
        new UngroupAction { Id = "ungroup", Targets = ["@group"] },
        new MoveToLayerAction { Id = "tolayer", Targets = ["@title"], Layer = "Text" },
        new CreateLayerAction { Id = "layer", Name = "Text" },
        new RenameObjectAction { Id = "rename", Targets = ["selection"], Name = "Renamed" },
        new BringToFrontAction { Id = "front", Targets = ["@title"] },
        new SendToBackAction { Id = "back", Targets = ["@rect"] },
        new SaveDocumentAction { Id = "save", FilePath = @"C:\out\job.cdr" },
        new ExportPdfAction { Id = "pdf", FilePath = @"C:\out\job.pdf" },
        new ExportPngAction { Id = "png", FilePath = @"C:\out\job.png", Dpi = 150, Transparent = false },
        new ExportSvgAction { Id = "svg", FilePath = @"C:\out\job.svg" },
    ];

    /// <summary>A small, valid new-document plan with a text whose content is a natural recipe variable.</summary>
    public static AutomationPlan DoorSignPlan(string personName = "Ahmet Yılmaz") => new()
    {
        Name = "Employee door sign",
        Description = "Name plate",
        UserRequest = $"Create employee door sign for {personName}.",
        Target = DocumentTarget.NewDocument,
        Actions =
        [
            new CreateDocumentAction { Id = "doc", WidthMm = 200, HeightMm = 80 },
            new CreateRectangleAction { Id = "frame", XMm = 5, YMm = 5, WidthMm = 190, HeightMm = 70, OutlineWidthMm = 1 },
            new CreateTextAction { Id = "name", Text = personName, FontFamily = "Arial", FontSizePt = 40, Name = "PersonName" },
            new AlignAction { Id = "center", Targets = ["@name"], Horizontal = HorizontalAlign.Center, Vertical = VerticalAlign.Center },
        ],
    };
}

/// <summary>In-memory stand-in for CorelDRAW that records what the engine asked for.</summary>
internal sealed class FakeAutomationSession : IAutomationSession
{
    public string? FailOnActionId { get; init; }
    public bool CanRollback { get; init; } = true;
    public List<string> Executed { get; } = [];
    public bool Began { get; private set; }
    public bool Completed { get; private set; }
    public bool RollbackRequested { get; private set; }

    public void Begin(AutomationPlan plan) => Began = true;

    public ActionOutcome Execute(CorelAction action)
    {
        if (action.Id == FailOnActionId)
        {
            throw new InvalidOperationException($"Simulated failure in {action.Id}.");
        }

        Executed.Add(action.Id);
        return action switch
        {
            OutputAction output => new ActionOutcome { ProducedFiles = [output.FilePath] },
            _ when action.CreatesObjects => new ActionOutcome { CreatedObjectIds = [$"shape_{Executed.Count:000}"] },
            TargetedAction targeted => new ActionOutcome { ModifiedObjectIds = targeted.Targets },
            _ => ActionOutcome.Empty,
        };
    }

    public void Complete() => Completed = true;

    public RollbackOutcome Rollback()
    {
        RollbackRequested = true;
        return new RollbackOutcome(CanRollback, CanRollback ? "undone" : "not undone");
    }
}

internal sealed class FakeExecutor : ICorelActionExecutor
{
    public Func<AutomationPlan, bool> ShouldFail { get; init; } = _ => false;
    public List<AutomationPlan> Plans { get; } = [];

    public Task<PlanExecutionResult> ExecuteAsync(
        AutomationPlan plan,
        ExecutionOptions? options = null,
        IProgress<ActionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Plans.Add(plan);
        var failOn = ShouldFail(plan) ? plan.Actions[^1].Id : null;
        return Task.FromResult(PlanExecutionEngine.Run(plan, new FakeAutomationSession { FailOnActionId = failOn }, options, progress, cancellationToken));
    }
}
