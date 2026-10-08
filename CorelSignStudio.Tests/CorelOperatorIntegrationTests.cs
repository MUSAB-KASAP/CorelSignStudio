using CorelSignStudio.Corel;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Batch;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Recipes;
using Xunit.Abstractions;

namespace CorelSignStudio.Tests;

/// <summary>Serializes every test that drives the real CorelDRAW so they never share the application concurrently.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CorelDrawCollection
{
    public const string Name = "CorelDRAW";
}

/// <summary>
/// One CorelDRAW connection shared by the operator tests, so a full run starts CorelDRAW once instead of
/// once per test. Does nothing unless COREL_INTEGRATION=1.
/// </summary>
public sealed class CorelOperatorFixture : IAsyncLifetime
{
    private CorelProcessJanitor _janitor = new();

    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("COREL_INTEGRATION"), "1", StringComparison.Ordinal);

    public CorelAutomationService? Service { get; private set; }
    public CorelConnectionInfo? Connection { get; private set; }
    public List<string> Log { get; } = [];
    public Action<string>? Sink { get; set; }

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        _janitor = new CorelProcessJanitor();
        Service = new CorelAutomationService(log: message =>
        {
            lock (Log)
            {
                Log.Add(message);
            }

            try
            {
                Sink?.Invoke(message);
            }
            catch (InvalidOperationException)
            {
                // The test that owned the output helper has already finished.
            }
        });
        Connection = await Service.ConnectPreservingVisibilityAsync();
    }

    public async Task DisposeAsync()
    {
        if (Service is null)
        {
            return;
        }

        try
        {
            await Service.DisposeAsync();
        }
        catch (Exception)
        {
            // Quit can be refused; the janitor still ends an instance this run started.
        }

        _janitor.Dispose();
    }
}

/// <summary>
/// Observed with the CorelDRAW 2026 trial: a hidden automation instance stays alive after Quit(), and a
/// later test can then bind to that half-closed instance and hang. This fixture ends the instances a
/// test class itself started — never one that was already running when the class began.
/// </summary>
public sealed class CorelProcessJanitor : IDisposable
{
    private readonly HashSet<int> _before = CorelOperatorFixture.Enabled ? CorelProcessIds() : [];

    public void Dispose()
    {
        if (!CorelOperatorFixture.Enabled)
        {
            return;
        }

        foreach (var id in CorelProcessIds().Except(_before))
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(id);
                if (!process.WaitForExit(TimeSpan.FromSeconds(8)))
                {
                    process.Kill();
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
        }
    }

    private static HashSet<int> CorelProcessIds()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("CorelDRW");
        try
        {
            return processes.Select(process => process.Id).ToHashSet();
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

/// <summary>
/// Opt-in end-to-end test of the inspector, executor, recipes and batch engine against the installed
/// CorelDRAW 2026. Run with the environment variable COREL_INTEGRATION=1.
/// </summary>
[Collection(CorelDrawCollection.Name)]
public sealed class CorelOperatorIntegrationTests : IClassFixture<CorelOperatorFixture>
{
    private readonly ITestOutputHelper output;
    private readonly CorelOperatorFixture fixture;

    public CorelOperatorIntegrationTests(CorelOperatorFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
        fixture.Sink = output.WriteLine;
    }

    private static bool Enabled => CorelOperatorFixture.Enabled;

    [Fact]
    [Trait("Category", "CorelIntegration")]
    public async Task Operator_core_inspects_modifies_saves_and_reopens_a_real_document()
    {
        if (!Enabled)
        {
            output.WriteLine("Set COREL_INTEGRATION=1 to run the installed-CorelDRAW operator test.");
            return;
        }

        var outputRoot = PrepareOutputFolder("operator");
        var log = fixture.Log;
        var service = fixture.Service!;
        var inspector = new CorelDocumentInspector(service);
        var executor = new CorelActionExecutor(service);

        // 1. Connect.
        var connection = fixture.Connection!;
        Assert.Equal(ApartmentState.STA, connection.ApartmentState);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;
        try
        {
            await RunOperatorScenario(service, inspector, executor, connection, outputRoot, log);
        }
        finally
        {
            await CloseTestDocuments(inspector, executor, userHadDocumentOpen);
        }
    }

    private async Task RunOperatorScenario(
        CorelAutomationService service,
        CorelDocumentInspector inspector,
        CorelActionExecutor executor,
        CorelConnectionInfo connection,
        string outputRoot,
        List<string> log)
    {
        Assert.True(service.IsConnected);

        // 2. Create a test document and inspect it.
        await Run(executor, new AutomationPlan
        {
            Name = "Create test document",
            Target = DocumentTarget.NewDocument,
            Actions = [new CreateDocumentAction { Id = "doc", WidthMm = 500, HeightMm = 700 }],
        });
        var empty = (await inspector.InspectActiveDocumentAsync())!;
        Assert.Equal(500, empty.ActivePage!.WidthMm, 1);
        Assert.Equal(700, empty.ActivePage.HeightMm, 1);
        Assert.Equal(0, empty.ShapeCount);

        // 3. Create shapes and text in the now-active document.
        var created = await Run(executor, new AutomationPlan
        {
            Name = "Create content",
            Actions =
            [
                new CreateRectangleAction { Id = "frame", XMm = 20, YMm = 20, WidthMm = 460, HeightMm = 660, OutlineWidthMm = 2, OutlineColor = "#000000", Name = "Frame" },
                new CreateTextAction { Id = "title", Text = "YASAKTIR", XMm = 80, YMm = 520, FontFamily = "Arial", FontSizePt = 72, Bold = true, FillColor = "#D8202A", Name = "Title" },
                new CreateEllipseAction { Id = "dot", XMm = 50, YMm = 50, WidthMm = 40, HeightMm = 40, FillColor = "#0000FF" },
                new CreateLineAction { Id = "rule", X1Mm = 50, Y1Mm = 120, X2Mm = 250, Y2Mm = 120, OutlineWidthMm = 1 },
                new CreateTableAction
                {
                    Id = "table", XMm = 50, YMm = 150, WidthMm = 400, HeightMm = 90, Columns = 4, Rows = 3,
                    Cells = [["No", "Ad", "Soyad", "Bölüm"], ["1", "Ahmet", "Yılmaz", "Üretim"]],
                    CellAlignment = TextAlignment.Center, Name = "Staff",
                },
                new CreateTextAction { Id = "note", Text = "Paragraf metni", XMm = 50, YMm = 260, FrameWidthMm = 200, FrameHeightMm = 40, FontSizePt = 14 },
                new GroupAction { Id = "badge", Targets = ["@dot", "@rule"], Name = "Badge" },
                new AlignAction { Id = "center-title", Targets = ["@title"], Horizontal = HorizontalAlign.Center },
            ],
        });
        Assert.Equal(7, created.CreatedObjectIds.Count);
        string Id(string actionId) => created.ActionResults.Single(result => result.ActionId == actionId).CreatedObjectIds.Single();

        // 4. Inspect again: every created object is visible under its logical id.
        var before = (await inspector.InspectActiveDocumentAsync())!;
        output.WriteLine(before.ToText());
        var frame = before.FindShape(Id("frame"))!;
        Assert.Equal(ShapeKind.Rectangle, frame.Type);
        Assert.Equal("Frame", frame.Name);
        AssertBounds(frame.Bounds, 20, 20, 460, 660);
        Assert.True(frame.Outline!.HasOutline);

        var title = before.FindShape(Id("title"))!;
        Assert.Equal(ShapeKind.ArtisticText, title.Type);
        Assert.Equal("YASAKTIR", title.Text);
        Assert.Equal("Arial", title.FontFamily);
        Assert.Equal(72, title.FontSizePt!.Value, 1);
        Assert.Equal("#D8202A", title.Fill!.ColorHex);
        Assert.Equal(250, title.Bounds.CenterXMm, 0); // centred on the 500 mm page
        Assert.Equal(520, title.Bounds.YMm, 0);

        Assert.Equal(ShapeKind.Table, before.FindShape(Id("table"))!.Type);
        Assert.Equal(ShapeKind.ParagraphText, before.FindShape(Id("note"))!.Type);
        var badge = before.FindShape(Id("badge"))!;
        Assert.Equal(ShapeKind.Group, badge.Type);
        Assert.Equal(2, badge.Children.Count);
        Assert.All(badge.Children, child => Assert.Equal(badge.Id, child.ParentGroupId));
        Assert.Equal(Id("dot"), before.FindShape(Id("dot"))!.Id);
        Assert.False(string.IsNullOrWhiteSpace(frame.LayerName));

        // 5–7. Change text, move, resize and restyle by logical id.
        var modified = await Run(executor, new AutomationPlan
        {
            Name = "Modify content",
            Actions =
            [
                new SetTextAction { Id = "set", Targets = [title.Id], Text = "HELLO" },
                new SetFontAction { Id = "font", Targets = [title.Id], FontSizePt = 60, Italic = true },
                new MoveAction { Id = "move", Targets = [frame.Id], DeltaXMm = 10, DeltaYMm = 5 },
                new ResizeAction { Id = "resize", Targets = [frame.Id], WidthMm = 400, HeightMm = 600, KeepAspectRatio = false, Anchor = PositionAnchor.TopLeft },
                new SetFillAction { Id = "fill", Targets = [frame.Id], Color = "#FFEE00" },
                new SetOutlineAction { Id = "outline", Targets = ["name:Frame"], Color = "#FF0000", WidthMm = 3 },
                new SendToBackAction { Id = "back", Targets = [frame.Id] },
                new RotateAction { Id = "rotate", Targets = [badge.Id], AngleDegrees = 30 },
                new DuplicateAction { Id = "copies", Targets = [title.Id], OffsetYMm = 30, Count = 2 },
                new DistributeAction { Id = "spread", Targets = [title.Id, "@copies"], Direction = DistributeDirection.Vertical, SpacingMm = 5 },
                new RenameObjectAction { Id = "rename", Targets = ["@copies"], Name = "TitleCopy" },
                new CreateLayerAction { Id = "layer", Name = "Notes" },
                new MoveToLayerAction { Id = "tolayer", Targets = [Id("note")], Layer = "Notes" },
                new MoveAction { Id = "place", Targets = [Id("note")], ToXMm = 60, ToYMm = 300 },
                new UngroupAction { Id = "ungroup", Targets = [badge.Id] },
                new DeleteAction { Id = "delete", Targets = [Id("rule")] },
                new BringToFrontAction { Id = "front", Targets = [title.Id] },
            ],
        });
        Assert.Contains(title.Id, modified.ModifiedObjectIds);

        var after = (await inspector.InspectActiveDocumentAsync())!;
        var titleAfter = after.FindShape(title.Id)!; // same logical id as before the edits
        Assert.Equal("HELLO", titleAfter.Text);
        Assert.Equal(60, titleAfter.FontSizePt!.Value, 1);
        var frameAfter = after.FindShape(frame.Id)!;
        AssertBounds(frameAfter.Bounds, 30, 25, 400, 600);
        Assert.Equal("#FFEE00", frameAfter.Fill!.ColorHex);
        Assert.Equal("#FF0000", frameAfter.Outline!.ColorHex);
        Assert.Equal(3, frameAfter.Outline.WidthMm, 1);
        Assert.Null(after.FindShape(badge.Id));
        Assert.Null(after.FindShape(Id("rule")));
        Assert.Null(after.FindShape(Id("dot"))!.ParentGroupId);
        var copies = after.FindShapesByName("TitleCopy").OrderBy(shape => shape.Bounds.YMm).ToList();
        Assert.Equal(2, copies.Count);
        Assert.Equal(titleAfter.Bounds.BottomMm + 5, copies[0].Bounds.YMm, 1);
        Assert.Equal(copies[0].Bounds.BottomMm + 5, copies[1].Bounds.YMm, 1);
        var note = after.FindShape(Id("note"))!;
        Assert.Equal("Notes", note.LayerName);
        Assert.Equal(60, note.Bounds.XMm, 1);
        Assert.Equal(300, note.Bounds.YMm, 1);

        // A failing plan names the failed action and leaves the open document untouched.
        var failed = await executor.ExecuteAsync(new AutomationPlan
        {
            Name = "Failing plan",
            Actions =
            [
                new MoveAction { Id = "shift", Targets = [title.Id], DeltaXMm = 77 },
                new SetFillAction { Id = "paint", Targets = [frame.Id], Color = "#00FF00" },
                new SetTextAction { Id = "not-text", Targets = [frame.Id], Text = "a rectangle has no text" },
                new DeleteAction { Id = "never", Targets = [title.Id] },
            ],
        });
        output.WriteLine($"{failed.Summary} | rollback: {failed.RollbackNote}");
        Assert.Equal(PlanExecutionStatus.Failed, failed.Status);
        Assert.Equal("not-text", failed.FailedActionId);
        Assert.Equal("setText", failed.FailedActionType);
        Assert.Equal(3, failed.FailedActionIndex);
        Assert.Equal(["shift", "paint"], failed.CompletedActionIds);
        Assert.Contains("not text", failed.ErrorMessage);
        Assert.True(failed.RolledBack, failed.RollbackNote);
        var rolledBack = (await inspector.InspectActiveDocumentAsync())!;
        Assert.Equal(titleAfter.Bounds.XMm, rolledBack.FindShape(title.Id)!.Bounds.XMm, 2);
        Assert.Equal("#FFEE00", rolledBack.FindShape(frame.Id)!.Fill!.ColorHex);
        Assert.Equal(after.ShapeCount, rolledBack.ShapeCount);

        var missing = await executor.ExecuteAsync(new AutomationPlan
        {
            Name = "Missing target",
            Actions = [new MoveAction { Id = "ghost", Targets = ["shape_999999"], DeltaXMm = 1 }],
        });
        Assert.Equal("ghost", missing.FailedActionId);
        Assert.Contains("shape_999999", missing.ErrorMessage);

        // 8–9. Save CDR and export PDF/PNG/SVG.
        var cdrPath = Path.Combine(outputRoot, "operator-test.cdr");
        var outputs = await Run(executor, new AutomationPlan
        {
            Name = "Outputs",
            Actions =
            [
                new SaveDocumentAction { Id = "cdr", FilePath = cdrPath },
                new ExportPdfAction { Id = "pdf", FilePath = Path.Combine(outputRoot, "operator-test.pdf") },
                new ExportPngAction { Id = "png", FilePath = Path.Combine(outputRoot, "operator-test.png"), Dpi = 72 },
                new ExportSvgAction { Id = "svg", FilePath = Path.Combine(outputRoot, "operator-test.svg") },
            ],
        });
        Assert.Equal(4, outputs.ProducedFiles.Count);
        Assert.All(outputs.ProducedFiles, file => Assert.True(new FileInfo(file).Length > 0, file));
        var png = await new Storage.FileReferenceAnalyzer().AnalyzeAsync(
            Domain.References.ReferenceInput.FromFile(Path.Combine(outputRoot, "operator-test.png")));
        output.WriteLine($"PNG: {png.PixelWidth} x {png.PixelHeight} px");
        Assert.InRange(png.PixelHeight!.Value, 1500, 2100); // ~700 mm of artwork at 72 dpi
        Assert.InRange(png.PixelWidth!.Value, 1000, 1500);
        Assert.Contains("<svg", await File.ReadAllTextAsync(Path.Combine(outputRoot, "operator-test.svg")));

        // 10. Close, reopen the CDR and verify it — including that logical ids survived the round trip.
        await Run(executor, new AutomationPlan { Name = "Close", Actions = [new CloseDocumentAction { Id = "close" }] });
        await Run(executor, new AutomationPlan
        {
            Name = "Reopen",
            Target = DocumentTarget.NewDocument,
            Actions = [new OpenDocumentAction { Id = "open", FilePath = cdrPath }],
        });
        var reopened = (await inspector.InspectActiveDocumentAsync())!;
        Assert.Equal(cdrPath, reopened.FilePath, ignoreCase: true);
        Assert.Equal(500, reopened.ActivePage!.WidthMm, 1);
        Assert.Equal(700, reopened.ActivePage.HeightMm, 1);
        Assert.Equal(after.ShapeCount, reopened.ShapeCount);
        Assert.Equal("HELLO", reopened.FindShape(title.Id)!.Text);
        AssertBounds(reopened.FindShape(frame.Id)!.Bounds, 30, 25, 400, 600);
        Assert.Equal(
            after.AllShapes().Select(shape => shape.Id).Order(),
            reopened.AllShapes().Select(shape => shape.Id).Order());
        await Run(executor, new AutomationPlan { Name = "Close", Actions = [new CloseDocumentAction { Id = "close" }] });

        await File.WriteAllTextAsync(Path.Combine(outputRoot, "OperatorIntegration.json"), AutomationJson.Serialize(new
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            connection.Version,
            ShapesAfterEdit = after.ShapeCount,
            outputs.ProducedFiles,
            FailedPlan = new { failed.FailedActionId, failed.RolledBack, failed.RollbackNote },
            Snapshot = reopened,
            Log = log.ToArray(),
        }));
    }

    [Fact]
    [Trait("Category", "CorelIntegration")]
    public async Task Recipe_batch_produces_one_cdr_and_pdf_per_row()
    {
        if (!Enabled)
        {
            output.WriteLine("Set COREL_INTEGRATION=1 to run the installed-CorelDRAW batch test.");
            return;
        }

        var outputRoot = PrepareOutputFolder("batch");
        var service = fixture.Service!;
        var recipe = RecipeBuilder.FromPlan(
            AutomationTestData.DoorSignPlan(), "Employee Door Sign", [new RecipeVariableBinding("PERSON_NAME", "Ahmet Yılmaz")]);
        var job = new BatchJob
        {
            Name = "Door signs",
            RecipeId = recipe.Id,
            OutputFolder = outputRoot,
            OutputNamePattern = "{{PERSON_NAME}}",
            Rows =
            [
                new BatchRow(1, new Dictionary<string, string> { ["PERSON_NAME"] = "Mehmet Kaya" }),
                new BatchRow(2, new Dictionary<string, string> { ["PERSON_NAME"] = "Ayşe Öztürk" }),
                new BatchRow(3, new Dictionary<string, string> { ["PERSON_NAME"] = "Mehmet Kaya" }),
            ],
        };

        var inspector = new CorelDocumentInspector(service);
        var executor = new CorelActionExecutor(service);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;
        BatchResult result;
        try
        {
            result = await new BatchRunner(executor).RunAsync(job, recipe);
        }
        catch
        {
            await CloseTestDocuments(inspector, executor, userHadDocumentOpen);
            throw;
        }

        Assert.True(result.Success, string.Join(" | ", result.Items.Select(item => item.Error)));
        Assert.Equal(
            ["Ayse_Ozturk.cdr", "Ayse_Ozturk.pdf", "Mehmet_Kaya.cdr", "Mehmet_Kaya.pdf", "Mehmet_Kaya_002.cdr", "Mehmet_Kaya_002.pdf"],
            result.ProducedFiles.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.All(result.ProducedFiles, file => Assert.True(new FileInfo(file).Length > 0, file));

        // Every batch document was closed again, so nothing is left open.
        if (!userHadDocumentOpen)
        {
            Assert.Null(await inspector.InspectActiveDocumentAsync());
        }
    }

    [Fact]
    [Trait("Category", "CorelIntegration")]
    public async Task Typed_commands_flow_through_planner_plan_and_executor_into_coreldraw()
    {
        if (!Enabled)
        {
            output.WriteLine("Set COREL_INTEGRATION=1 to run the installed-CorelDRAW planner test.");
            return;
        }

        var service = fixture.Service!;
        var inspector = new CorelDocumentInspector(service);
        var executor = new CorelActionExecutor(service);
        var planner = new Domain.Planning.DeterministicCommandPlanner();
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;

        async Task<PlanExecutionResult> Say(string request)
        {
            var planning = await planner.PlanAsync(new Domain.Planning.PlanningRequest
            {
                UserRequest = request,
                Document = await inspector.InspectActiveDocumentAsync(),
            });
            Assert.True(planning.Success, string.Join(" | ", planning.UnrecognizedCommands));
            return await Run(executor, planning.Plan!);
        }

        try
        {
            await Say("Create a 500x700 mm document");
            var text = (await Say("Add text TEST in the center")).CreatedObjectIds.Single();
            var rectangle = (await Say("Add a rectangle 200x80 mm at 20,20")).CreatedObjectIds.Single();
            var placed = (await inspector.InspectActiveDocumentAsync())!;
            Assert.Equal(250, placed.FindShape(text)!.Bounds.CenterXMm, 0);
            Assert.Equal(350, placed.FindShape(text)!.Bounds.CenterYMm, 0);

            await Say($"Move {text} 10 mm right");
            var moved = (await inspector.InspectActiveDocumentAsync())!;
            Assert.Equal(placed.FindShape(text)!.Bounds.XMm + 10, moved.FindShape(text)!.Bounds.XMm, 1);
            Assert.Equal(placed.FindShape(text)!.Bounds.YMm, moved.FindShape(text)!.Bounds.YMm, 1);

            await Say($"Change {text} text to HELLO\nResize {rectangle} to 100x50 mm\nSet fill of {rectangle} to red");

            var document = (await inspector.InspectActiveDocumentAsync())!;
            Assert.Equal("HELLO", document.FindShape(text)!.Text);
            Assert.Equal((100, 50), (Math.Round(document.FindShape(rectangle)!.Bounds.WidthMm), Math.Round(document.FindShape(rectangle)!.Bounds.HeightMm)));
            Assert.Equal("#FF0000", document.FindShape(rectangle)!.Fill!.ColorHex);

            var table = (await Say("Create a table with 8 columns and 20 rows and center all text")).CreatedObjectIds.Single();
            Assert.Equal(ShapeKind.Table, (await inspector.InspectActiveDocumentAsync())!.FindShape(table)!.Type);
        }
        finally
        {
            await CloseTestDocuments(inspector, executor, userHadDocumentOpen);
        }
    }

    private async Task<PlanExecutionResult> Run(CorelActionExecutor executor, AutomationPlan plan)
    {
        var result = await executor.ExecuteAsync(plan);
        foreach (var action in result.ActionResults)
        {
            output.WriteLine($"  {(action.Success ? "ok  " : "FAIL")} {action.ActionId} ({action.ActionType}) {action.DurationMs:0} ms {action.Error ?? action.Message}");
        }

        Assert.True(result.Success, $"{plan.Name}: {result.Summary}\n{result.ErrorDetails}");
        return result;
    }

    /// <summary>
    /// Closes whatever a failed test left open so the hidden CorelDRAW can quit. Skipped entirely when the
    /// user already had a document open before the test, because their documents must never be closed.
    /// </summary>
    private static async Task CloseTestDocuments(CorelDocumentInspector inspector, CorelActionExecutor executor, bool userHadDocumentOpen)
    {
        if (userHadDocumentOpen)
        {
            return;
        }

        for (var attempt = 0; attempt < 10 && await inspector.InspectActiveDocumentAsync() is not null; attempt++)
        {
            await executor.ExecuteAsync(new AutomationPlan { Name = "Cleanup", Actions = [new CloseDocumentAction { Id = "close" }] });
        }
    }

    private static void AssertBounds(BoundsMm bounds, double x, double y, double width, double height)
    {
        Assert.Equal(x, bounds.XMm, 1);
        Assert.Equal(y, bounds.YMm, 1);
        Assert.Equal(width, bounds.WidthMm, 1);
        Assert.Equal(height, bounds.HeightMm, 1);
    }

    private static string PrepareOutputFolder(string name)
    {
        var root = Environment.GetEnvironmentVariable("COREL_SMOKE_OUTPUT")
                   ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "ai-operator"));
        var folder = Path.Combine(root, name);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        return Directory.CreateDirectory(folder).FullName;
    }
}
