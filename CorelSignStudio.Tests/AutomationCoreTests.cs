using System.Text.Json;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Inspection;

namespace CorelSignStudio.Tests;

public sealed class InspectionModelTests
{
    private static DocumentSnapshot SampleSnapshot()
    {
        var child = new ShapeSnapshot
        {
            Id = "shape_003", NativeId = 3, Type = ShapeKind.Ellipse, Bounds = new BoundsMm(10, 10, 20, 20),
            LayerName = "Layer 1", ParentGroupId = "shape_002", Order = 3,
        };
        return new DocumentSnapshot
        {
            Title = "example.cdr",
            FilePath = @"C:\work\example.cdr",
            Pages =
            [
                new PageSnapshot
                {
                    Index = 1, WidthMm = 500, HeightMm = 700,
                    Layers = [new LayerSnapshot { Name = "Layer 1", ShapeCount = 2 }],
                    Shapes =
                    [
                        new ShapeSnapshot
                        {
                            Id = "shape_001", NativeId = 1, Type = ShapeKind.ArtisticText, Name = "Title", Text = "YASAKTIR",
                            FontFamily = "Arial", FontSizePt = 72, Bounds = new BoundsMm(80, 520, 340, 62), LayerName = "Layer 1",
                            Fill = new FillInfo(FillKind.Uniform, "#D8202A"), Outline = new OutlineInfo(false), Order = 1,
                        },
                        new ShapeSnapshot
                        {
                            Id = "shape_002", NativeId = 2, Type = ShapeKind.Group, Bounds = new BoundsMm(10, 10, 20, 20),
                            LayerName = "Layer 1", Children = [child], Order = 2,
                        },
                    ],
                },
            ],
        };
    }

    [Fact]
    public void Snapshot_finds_nested_shapes_and_counts_them()
    {
        var snapshot = SampleSnapshot();

        Assert.Equal(3, snapshot.ShapeCount);
        Assert.Equal("shape_002", snapshot.FindShape("SHAPE_003")!.ParentGroupId);
        Assert.Equal("shape_001", Assert.Single(snapshot.FindShapesByName("title")).Id);
        Assert.Null(snapshot.FindShape("shape_999"));
        Assert.Equal(420, snapshot.FindShape("shape_001")!.Bounds.RightMm);
        Assert.True(snapshot.FindShape("shape_001")!.IsText);
    }

    [Fact]
    public void Snapshot_text_rendering_lists_page_and_shapes()
    {
        var text = SampleSnapshot().ToText();

        Assert.Contains("Page 1: 500 x 700 mm", text);
        Assert.Contains("shape_001", text);
        Assert.Contains("Type: ArtisticText", text);
        Assert.Contains("Text: YASAKTIR", text);
        Assert.Contains("X: 80  Y: 520  Width: 340  Height: 62", text);
        Assert.Contains("Layer: Layer 1", text);
    }

    [Fact]
    public void Snapshot_round_trips_through_json()
    {
        var snapshot = SampleSnapshot();
        var json = AutomationJson.Serialize(snapshot);
        var restored = AutomationJson.Deserialize<DocumentSnapshot>(json);

        Assert.Equal(json, AutomationJson.Serialize(restored));
        Assert.Equal(ShapeKind.Ellipse, restored.FindShape("shape_003")!.Type);
    }

    [Theory]
    [InlineData(1, "shape_001")]
    [InlineData(17, "shape_017")]
    [InlineData(123456, "shape_123456")]
    public void Logical_ids_are_derived_from_the_native_id_and_parse_back(int nativeId, string expected)
    {
        var id = LogicalShapeId.FromNativeId(nativeId);

        Assert.Equal(expected, id);
        Assert.Equal(id, LogicalShapeId.FromNativeId(nativeId)); // stable: same object, same id, every time
        Assert.True(LogicalShapeId.TryGetNativeId(id, out var parsed));
        Assert.Equal(nativeId, parsed);
    }

    [Theory]
    [InlineData("shape_")]
    [InlineData("shape_0")]
    [InlineData("shape_abc")]
    [InlineData("rect_001")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalid_logical_ids_are_rejected(string? value) => Assert.False(LogicalShapeId.TryGetNativeId(value, out _));

    [Fact]
    public void Logical_ids_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LogicalShapeId.FromNativeId(0));
}

public sealed class CorelActionTests
{
    public static IEnumerable<object[]> EveryAction() =>
        AutomationTestData.OneOfEveryAction().Select(action => new object[] { action.TypeName });

    [Fact]
    public void Samples_cover_every_registered_action_type()
    {
        var sampled = AutomationTestData.OneOfEveryAction().Select(action => action.GetType()).ToHashSet();

        Assert.Empty(ActionTypes.All.Where(type => !sampled.Contains(type)).Select(type => type.Name));
        Assert.Equal(ActionTypes.All.Count, ActionTypes.AllNames.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(EveryAction))]
    public void Every_action_type_round_trips_through_json(string typeName)
    {
        CorelAction action = AutomationTestData.OneOfEveryAction().Single(sample => sample.TypeName == typeName);

        var json = AutomationJson.Serialize(action);
        var restored = AutomationJson.Deserialize<CorelAction>(json);

        Assert.Equal(action.GetType(), restored.GetType());
        Assert.Equal(json, AutomationJson.Serialize(restored));
        Assert.Equal(action.Id, restored.Id);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(typeName, document.RootElement.GetProperty("type").GetString());
    }

    [Theory]
    [MemberData(nameof(EveryAction))]
    public void Every_sample_action_is_valid_and_describable(string typeName)
    {
        var action = AutomationTestData.OneOfEveryAction().Single(sample => sample.TypeName == typeName);

        Assert.Empty(action.Validate());
        Assert.False(string.IsNullOrWhiteSpace(ActionDescriber.Describe(action)));
    }

    [Fact]
    public void Json_uses_camel_case_and_readable_enums()
    {
        var json = AutomationJson.Serialize<CorelAction>(new AlignAction
        {
            Id = "a", Targets = ["shape_001"], Horizontal = HorizontalAlign.Center, RelativeTo = AlignReference.FirstTarget,
        });

        Assert.Contains("\"type\": \"align\"", json);
        Assert.Contains("\"horizontal\": \"center\"", json);
        Assert.Contains("\"relativeTo\": \"firstTarget\"", json);
        Assert.DoesNotContain("isDestructive", json);
    }

    [Fact]
    public void Unknown_action_type_is_rejected() =>
        Assert.Throws<JsonException>(() => AutomationJson.Deserialize<CorelAction>("""{ "type": "launchRocket", "id": "x" }"""));

    [Fact]
    public void Action_without_id_is_rejected_when_deserializing() =>
        Assert.Throws<JsonException>(() => AutomationJson.Deserialize<CorelAction>("""{ "type": "createLayer", "name": "L" }"""));

    private static readonly CorelAction[] InvalidActions =
    [
        new CreateDocumentAction { Id = "a", WidthMm = 0, HeightMm = 700 },
        new CreateDocumentAction { Id = "a", WidthMm = double.NaN, HeightMm = 700 },
        new SetPageSizeAction { Id = "a", WidthMm = 100, HeightMm = -1 },
        new CreateTextAction { Id = "a", Text = " " },
        new CreateTextAction { Id = "a", Text = "x", FontSizePt = 0 },
        new CreateTextAction { Id = "a", Text = "x", FrameWidthMm = 50 },
        new CreateTextAction { Id = "a", Text = "x", FillColor = "red" },
        new CreateRectangleAction { Id = "a", WidthMm = 10, HeightMm = 0 },
        new CreateRectangleAction { Id = "a", WidthMm = 10, HeightMm = 10, CornerRadiusMm = -1 },
        new CreateEllipseAction { Id = "a", WidthMm = -5, HeightMm = 10 },
        new CreateLineAction { Id = "a", X1Mm = 1, Y1Mm = 1, X2Mm = 1, Y2Mm = 1 },
        new CreateTableAction { Id = "a", WidthMm = 10, HeightMm = 10, Columns = 0, Rows = 2 },
        new CreateTableAction { Id = "a", WidthMm = 10, HeightMm = 10, Columns = 1, Rows = 1, Cells = [["a", "b"]] },
        new ImportFileAction { Id = "a", FilePath = "" },
        new ImportFileAction { Id = "a", FilePath = "x.svg", XMm = 5 },
        new MoveAction { Id = "a", Targets = ["shape_001"] },
        new MoveAction { Id = "a", Targets = ["shape_001"], DeltaXMm = 5, ToXMm = 10 },
        new MoveAction { Id = "a", Targets = [], DeltaXMm = 5 },
        new MoveAction { Id = "a", Targets = ["the logo"], DeltaXMm = 5 },
        new ResizeAction { Id = "a", Targets = ["shape_001"] },
        new ResizeAction { Id = "a", Targets = ["shape_001"], WidthMm = 10, ScalePercent = 50 },
        new ResizeAction { Id = "a", Targets = ["shape_001"], WidthMm = -10 },
        new RotateAction { Id = "a", Targets = ["shape_001"], AngleDegrees = double.PositiveInfinity },
        new SetFontAction { Id = "a", Targets = ["shape_001"] },
        new SetFillAction { Id = "a", Targets = ["shape_001"], Color = "#12345" },
        new SetOutlineAction { Id = "a", Targets = ["shape_001"] },
        new AlignAction { Id = "a", Targets = ["shape_001"] },
        new DistributeAction { Id = "a", Targets = ["shape_001"], Direction = DistributeDirection.Horizontal },
        new DuplicateAction { Id = "a", Targets = ["shape_001"], Count = 0 },
        new GroupAction { Id = "a", Targets = ["shape_001"] },
        new MoveToLayerAction { Id = "a", Targets = ["shape_001"], Layer = "" },
        new CreateLayerAction { Id = "a", Name = " " },
        new RenameObjectAction { Id = "a", Targets = ["shape_001"], Name = "" },
        new SaveDocumentAction { Id = "a", FilePath = @"C:\out\job.pdf" },
        new ExportPdfAction { Id = "a", FilePath = "" },
        new ExportPngAction { Id = "a", FilePath = @"C:\out\job.png", Dpi = 5 },
        new CreateLayerAction { Id = "has space", Name = "L" },
        new CreateLayerAction { Id = "@a", Name = "L" },
    ];

    [Fact]
    public void Invalid_actions_are_detected()
    {
        Assert.True(InvalidActions.Length > 30);
        Assert.All(InvalidActions, action => Assert.NotEmpty(action.Validate()));
    }

    [Fact]
    public void Invalid_action_inside_a_plan_is_reported_with_its_id()
    {
        foreach (var action in InvalidActions.Where(action => action.Id == "a"))
        {
            var plan = new AutomationPlan { Name = "p", Actions = [new CreateLayerAction { Id = "first", Name = "L" }, action] };
            Assert.Contains(plan.Validate().Errors, error => error.ActionId == "a");
        }
    }

    [Fact]
    public void Multi_object_targets_satisfy_minimum_target_counts()
    {
        Assert.Empty(new GroupAction { Id = "g", Targets = ["selection"] }.Validate());
        Assert.Empty(new DistributeAction { Id = "d", Targets = ["name:Row"], Direction = DistributeDirection.Vertical }.Validate());
    }

    [Fact]
    public void Destructive_actions_are_flagged()
    {
        Assert.True(new DeleteAction { Id = "d", Targets = ["shape_001"] }.IsDestructive);
        Assert.True(new CloseDocumentAction { Id = "c" }.IsDestructive);
        Assert.False(new MoveAction { Id = "m", Targets = ["shape_001"], DeltaXMm = 1 }.IsDestructive);
    }

    [Theory]
    [InlineData("shape_001", true)]
    [InlineData("@title", true)]
    [InlineData("name:Logo", true)]
    [InlineData("selection", true)]
    [InlineData("@", false)]
    [InlineData("name:", false)]
    [InlineData("logo", false)]
    public void Target_references_are_recognised(string reference, bool valid) =>
        Assert.Equal(valid, TargetRef.IsValid(reference));
}

public sealed class AutomationPlanTests
{
    [Fact]
    public void Plan_round_trips_through_json_with_all_parts()
    {
        var plan = AutomationTestData.DoorSignPlan() with
        {
            Parameters = new Dictionary<string, string> { ["PERSON_NAME"] = "Ahmet Yılmaz" },
            ReferenceFiles = [Domain.References.ReferenceInput.FromFile(@"C:\refs\sample.png")],
            Outputs = [new OutputRequirement(OutputFormat.Cdr, @"C:\out\a.cdr"), new OutputRequirement(OutputFormat.Pdf)],
            Metadata = new Dictionary<string, string> { ["customer"] = "ACME" },
        };

        var json = plan.ToJson();
        var restored = AutomationPlan.FromJson(json);

        Assert.Equal(json, restored.ToJson());
        Assert.Equal(plan.Id, restored.Id);
        Assert.Equal(plan.CreatedUtc, restored.CreatedUtc);
        Assert.Equal(DocumentTarget.NewDocument, restored.Target);
        Assert.Equal(["doc", "frame", "name", "center"], restored.Actions.Select(action => action.Id));
        Assert.IsType<CreateTextAction>(restored.Actions[2]);
        Assert.Equal("Ahmet Yılmaz", ((CreateTextAction)restored.Actions[2]).Text);
        Assert.Contains("Ahmet Yılmaz", json); // readable JSON: no \u escapes for Turkish letters
        Assert.Equal("ACME", restored.Metadata["customer"]);
        Assert.Equal(Domain.References.ReferenceFileType.Png, restored.ReferenceFiles[0].FileType);
    }

    [Fact]
    public void Valid_plan_passes_validation() => Assert.True(AutomationTestData.DoorSignPlan().Validate().IsValid);

    [Fact]
    public void Empty_plan_is_invalid() =>
        Assert.Contains(new AutomationPlan { Name = "Empty" }.Validate().Errors, error => error.Message.Contains("no actions"));

    [Fact]
    public void Duplicate_action_ids_are_reported()
    {
        var plan = new AutomationPlan
        {
            Name = "Dup",
            Actions =
            [
                new CreateLayerAction { Id = "a", Name = "One" },
                new CreateLayerAction { Id = "a", Name = "Two" },
            ],
        };

        var error = Assert.Single(plan.Validate().Errors);
        Assert.Equal("a", error.ActionId);
        Assert.Contains("more than once", error.Message);
    }

    [Fact]
    public void Action_errors_are_attributed_to_the_action()
    {
        var plan = new AutomationPlan
        {
            Name = "Bad",
            Actions =
            [
                new CreateLayerAction { Id = "ok", Name = "L" },
                new CreateRectangleAction { Id = "bad", WidthMm = -1, HeightMm = 10 },
            ],
        };

        var result = plan.Validate();
        Assert.False(result.IsValid);
        Assert.All(result.Errors, error => Assert.Equal("bad", error.ActionId));
    }

    [Fact]
    public void Action_reference_must_point_to_an_earlier_creating_action()
    {
        var forward = new AutomationPlan
        {
            Name = "Forward",
            Actions =
            [
                new MoveAction { Id = "move", Targets = ["@text"], DeltaXMm = 5 },
                new CreateTextAction { Id = "text", Text = "x" },
            ],
        };
        var nonCreator = new AutomationPlan
        {
            Name = "NonCreator",
            Actions =
            [
                new CreateLayerAction { Id = "layer", Name = "L" },
                new MoveAction { Id = "move", Targets = ["@layer"], DeltaXMm = 5 },
            ],
        };

        Assert.Contains(forward.Validate().Errors, error => error.ActionId == "move" && error.Message.Contains("earlier action"));
        Assert.Contains(nonCreator.Validate().Errors, error => error.Message.Contains("does not create objects"));
    }

    [Fact]
    public void New_document_plan_must_start_by_creating_or_opening_a_document()
    {
        var plan = new AutomationPlan
        {
            Name = "No doc",
            Target = DocumentTarget.NewDocument,
            Actions = [new CreateTextAction { Id = "t", Text = "x" }],
        };

        Assert.Contains(plan.Validate().Errors, error => error.Message.Contains("must start with createDocument"));
    }

    [Fact]
    public void Unfilled_variables_make_a_plan_invalid()
    {
        var plan = new AutomationPlan
        {
            Name = "Template",
            Actions = [new CreateTextAction { Id = "t", Text = "Hello {{PERSON_NAME}}" }],
        };

        Assert.Contains(plan.Validate().Errors, error => error.Message.Contains("PERSON_NAME"));
    }

    [Fact]
    public void Plan_describes_itself_as_numbered_steps()
    {
        var steps = ActionDescriber.Describe(AutomationTestData.DoorSignPlan());

        Assert.Equal(4, steps.Count);
        Assert.StartsWith("1. Create a new 200 x 80 mm document", steps[0]);
        Assert.Contains("Ahmet Yılmaz", steps[2]);
        Assert.Contains("on the page", steps[3]);
    }
}

public sealed class PlanExecutionEngineTests
{
    [Fact]
    public void Successful_run_reports_every_action_and_its_effects()
    {
        var plan = AutomationTestData.DoorSignPlan() with
        {
            Actions = [.. AutomationTestData.DoorSignPlan().Actions, new ExportPdfAction { Id = "pdf", FilePath = @"C:\out\sign.pdf" }],
        };
        var session = new FakeAutomationSession();
        var progress = new List<ActionProgress>();

        var result = PlanExecutionEngine.Run(plan, session, progress: new SynchronousProgress<ActionProgress>(progress.Add));

        Assert.True(result.Success);
        Assert.Equal(PlanExecutionStatus.Succeeded, result.Status);
        Assert.Equal(["doc", "frame", "name", "center", "pdf"], result.CompletedActionIds);
        Assert.Null(result.FailedActionId);
        Assert.Equal(2, result.CreatedObjectIds.Count);
        Assert.Equal(["@name"], result.ModifiedObjectIds);
        Assert.Equal([@"C:\out\sign.pdf"], result.ProducedFiles);
        Assert.Equal(5, result.ActionResults.Count);
        Assert.All(result.ActionResults, action => Assert.True(action.Success));
        Assert.True(result.Duration >= TimeSpan.Zero);
        Assert.True(session.Began && session.Completed);
        Assert.False(session.RollbackRequested);
        Assert.Equal(10, progress.Count); // one "starting" and one "finished" report per action
    }

    [Fact]
    public void Failure_reports_exactly_which_action_failed_and_stops()
    {
        var plan = AutomationTestData.DoorSignPlan();
        var session = new FakeAutomationSession { FailOnActionId = "name" };

        var result = PlanExecutionEngine.Run(plan, session);

        Assert.False(result.Success);
        Assert.Equal(PlanExecutionStatus.Failed, result.Status);
        Assert.Equal("name", result.FailedActionId);
        Assert.Equal("createText", result.FailedActionType);
        Assert.Equal(3, result.FailedActionIndex);
        Assert.Equal("Simulated failure in name.", result.ErrorMessage);
        Assert.Contains(nameof(InvalidOperationException), result.ErrorDetails);
        Assert.Equal(["doc", "frame"], result.CompletedActionIds);
        Assert.Equal(["doc", "frame"], session.Executed); // "center" never ran
        Assert.Equal(3, result.ActionResults.Count);
        Assert.False(result.ActionResults[^1].Success);
        Assert.Equal("Simulated failure in name.", result.ActionResults[^1].Error);
        Assert.True(result.RolledBack);
        Assert.Equal("undone", result.RollbackNote);
        Assert.False(session.Completed);
        Assert.Contains("Action 3 'name' (createText) failed", result.Summary);
    }

    [Fact]
    public void Rollback_can_be_disabled_and_its_limits_are_reported()
    {
        var plan = AutomationTestData.DoorSignPlan();

        var notRequested = new FakeAutomationSession { FailOnActionId = "frame" };
        var skipped = PlanExecutionEngine.Run(plan, notRequested, new ExecutionOptions { RollbackOnFailure = false });
        Assert.False(skipped.RolledBack);
        Assert.False(notRequested.RollbackRequested);

        var impossible = PlanExecutionEngine.Run(plan, new FakeAutomationSession { FailOnActionId = "frame", CanRollback = false });
        Assert.False(impossible.RolledBack);
        Assert.Equal("not undone", impossible.RollbackNote);
    }

    [Fact]
    public void Invalid_plan_is_never_started()
    {
        var plan = new AutomationPlan { Name = "Bad", Actions = [new CreateRectangleAction { Id = "r", WidthMm = 0, HeightMm = 0 }] };
        var session = new FakeAutomationSession();

        var result = PlanExecutionEngine.Run(plan, session);

        Assert.Equal(PlanExecutionStatus.ValidationFailed, result.Status);
        Assert.NotEmpty(result.ValidationErrors);
        Assert.False(session.Began);
        Assert.Empty(session.Executed);
    }

    [Fact]
    public void Cancellation_stops_before_the_next_action_and_rolls_back()
    {
        var plan = AutomationTestData.DoorSignPlan();
        var session = new FakeAutomationSession();
        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress<ActionProgress>(report =>
        {
            if (report.Result is not null && report.Action.Id == "frame")
            {
                cancellation.Cancel();
            }
        });

        var result = PlanExecutionEngine.Run(plan, session, progress: progress, cancellationToken: cancellation.Token);

        Assert.Equal(PlanExecutionStatus.Cancelled, result.Status);
        Assert.Equal(["doc", "frame"], result.CompletedActionIds);
        Assert.True(result.RolledBack);
    }

    [Fact]
    public void Execution_result_serializes_for_history()
    {
        var result = PlanExecutionEngine.Run(AutomationTestData.DoorSignPlan(), new FakeAutomationSession { FailOnActionId = "center" });

        var restored = AutomationJson.Deserialize<PlanExecutionResult>(AutomationJson.Serialize(result));

        Assert.Equal("center", restored.FailedActionId);
        Assert.Equal(PlanExecutionStatus.Failed, restored.Status);
        Assert.Equal(result.CompletedActionIds, restored.CompletedActionIds);
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
