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
        Assert.Contains("metin nesnesi değil", failed.ErrorMessage);
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

    /// <summary>
    /// Reference analysis (scripted, no AI call) → reconstruction plan → real CorelDRAW document → CDR and PDF.
    /// This is the Windows/CorelDRAW half of the reference-vision feature; it cannot run in the cloud.
    /// </summary>
    [Fact]
    [Trait("Category", "CorelIntegration")]
    public async Task A_reference_analysis_is_rebuilt_as_editable_objects_in_coreldraw()
    {
        if (!Enabled)
        {
            output.WriteLine("Set COREL_INTEGRATION=1 to run the installed-CorelDRAW reconstruction test.");
            return;
        }

        var outputRoot = PrepareOutputFolder("reconstruction");
        var service = fixture.Service!;
        var inspector = new CorelDocumentInspector(service);
        var executor = new CorelActionExecutor(service);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;

        const string Request = "Bunun aynısını 500x700 mm olarak CorelDRAW'da yap.";
        var elements = ReferenceFixtures.SignElements.TrimEnd().TrimEnd(']') + """
            ,{"key":"tri","parentKey":"","kind":"polygon","label":"Uyarı üçgeni","bounds":{"x":0.42,"y":0.9,"width":0.16,"height":0.06},"zIndex":9,"points":[[0.5,0.9],[0.58,0.96],[0.42,0.96]],"fillColor":"#FFD400","outlineColor":"#000000","outlineWidthRatio":0.004,"strategy":"nativeShape","confidence":0.9}
            ,{"key":"logo","parentKey":"","kind":"logo","label":"Firma logosu","bounds":{"x":0.72,"y":0.05,"width":0.2,"height":0.06},"zIndex":10,"strategy":"needsUserAsset","confidence":0.8}]
            """;
        var reference = Domain.References.ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng());
        var analysis = (await ReferenceFixtures.Analyzer(new FakeVisionClient(ReferenceFixtures.Answer(elements)))
            .AnalyzeAsync(new Domain.Ai.Vision.ReferenceAnalysisRequest { Reference = reference, UserRequest = Request })).Analysis!;
        var reconstruction = new Domain.References.ReferenceReconstructionPlanner(new Domain.References.InstalledFontResolver(["Arial"]))
            .Plan(new Domain.References.ReconstructionRequest { Reference = reference, Analysis = analysis, UserRequest = Request });
        Assert.True(reconstruction.IsReady, reconstruction.UserMessage);

        try
        {
            var execution = await Run(executor, reconstruction.Plan!);
            var document = (await inspector.InspectActiveDocumentAsync())!;
            output.WriteLine(document.ToText());

            Assert.Equal(500, document.ActivePage!.WidthMm, 1);
            Assert.Equal(700, document.ActivePage.HeightMm, 1);

            // Editable text objects, with the Turkish letters intact and fitted to the analysed width.
            var texts = document.AllShapes().Where(shape => shape.IsText).OrderBy(shape => shape.Bounds.YMm).ToList();
            Assert.Equal(["BU ALANA", "GİRMEK", "YASAKTIR"], texts.Select(text => text.Text));
            Assert.All(texts, text => Assert.Equal("#D8202A", text.Fill!.ColorHex));
            Assert.Equal(280, texts[0].Bounds.WidthMm, 0);
            Assert.Equal(110, texts[0].Bounds.XMm, 0);
            Assert.Equal(300, texts[2].Bounds.WidthMm, 0);
            Assert.InRange(texts[0].Bounds.CenterYMm, 415, 432);

            // Native shapes: frame, ring (ellipse), bar and triangle (curves) — nothing is a bitmap.
            var frame = document.FindShapesByName("Çerçeve (ref_001)").Single();
            Assert.Equal(ShapeKind.Rectangle, frame.Type);
            AssertBounds(frame.Bounds, 20, 21, 460, 658);
            var triangle = document.FindShapesByName("Uyarı üçgeni (ref_007)").Single();
            Assert.Equal(ShapeKind.Curve, triangle.Type);
            Assert.Equal("#FFD400", triangle.Fill!.ColorHex);
            AssertBounds(triangle.Bounds, 210, 630, 80, 42);
            Assert.DoesNotContain(document.AllShapes(), shape => shape.Type == ShapeKind.Bitmap);

            // The prohibition sign is one editable group of a ring and a bar; the three lines are another.
            var groups = document.AllShapes().Where(shape => shape.Type == ShapeKind.Group).ToList();
            var sign = groups.Single(group => group.Name == "Yasak işareti (ref_002)");
            Assert.Equal([ShapeKind.Ellipse, ShapeKind.Curve], sign.Children.Select(child => child.Type).Order());
            Assert.Equal(3, groups.Single(group => group.Name == "Yazı bloğu").Children.Count);

            // The logo is an honest, clearly named placeholder.
            var placeholder = document.FindShapesByName("YER TUTUCU: Firma logosu").Single();
            Assert.Equal(ShapeKind.Rectangle, placeholder.Type);
            Assert.Equal("#FF00FF", placeholder.Outline!.ColorHex);

            var files = await Run(executor, new AutomationPlan
            {
                Name = "Outputs",
                Actions =
                [
                    new SaveDocumentAction { Id = "cdr", FilePath = Path.Combine(outputRoot, "yeniden-olusturma.cdr") },
                    new ExportPdfAction { Id = "pdf", FilePath = Path.Combine(outputRoot, "yeniden-olusturma.pdf") },
                    new ExportPngAction { Id = "png", FilePath = Path.Combine(outputRoot, "yeniden-olusturma.png"), Dpi = 72 },
                ],
            });
            Assert.All(files.ProducedFiles, file => Assert.True(new FileInfo(file).Length > 0, file));
            Assert.Equal(reconstruction.Plan!.Actions.Count, execution.CompletedActionIds.Count);
        }
        finally
        {
            await CloseTestDocuments(inspector, executor, userHadDocumentOpen);
        }
    }

    /// <summary>
    /// comparison → correction plan → CorelDRAW executor, on the real application: a sign is built, then
    /// deliberately damaged (title 20 mm too low, ring 10 % too large); the measured comparison finds both,
    /// the bounded loop corrects them, and the full-page preview is rendered without touching the document.
    /// No AI is involved.
    /// </summary>
    [Fact]
    [Trait("Category", "CorelIntegration")]
    public async Task Measured_differences_are_corrected_in_coreldraw_and_the_page_preview_leaves_the_document_untouched()
    {
        if (!Enabled)
        {
            output.WriteLine("Set COREL_INTEGRATION=1 to run the installed-CorelDRAW correction test.");
            return;
        }

        var outputRoot = PrepareOutputFolder("correction");
        var service = fixture.Service!;
        var inspector = new CorelDocumentInspector(service);
        var executor = new CorelActionExecutor(service);
        var previewRenderer = new CorelPagePreviewRenderer(service);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;
        var (plan, expected, _) = await V1Fixtures.Sign();
        var size = new Domain.References.PhysicalSize(500, 700);

        try
        {
            // 1. Build the 500 x 700 mm sign.
            await Run(executor, plan);
            var built = (await inspector.InspectActiveDocumentAsync())!;
            var comparer = new Domain.References.VisualComparisonService();
            var faithful = await comparer.CompareAsync(new Domain.References.VisualComparisonRequest { Expected = expected, Document = built, Size = size });
            output.WriteLine($"After reconstruction: similarity {faithful.Similarity:0.000}; {string.Join(" | ", faithful.Differences.Select(difference => difference.Description))}");
            Assert.True(faithful.Similarity >= 0.98, "a fresh reconstruction should match what it was built from");

            // 2–3. Damage it on purpose.
            var title = built.FindShapesByName(V1Fixtures.Title).Single();
            var ring = built.FindShapesByName(V1Fixtures.Ring).Single(shape => shape.Type == ShapeKind.Ellipse);
            await Run(executor, new AutomationPlan
            {
                Name = "Damage",
                Actions =
                [
                    new MoveAction { Id = "low", Targets = [title.Id], DeltaYMm = 20 },
                    new ResizeAction { Id = "big", Targets = [ring.Id], ScalePercent = 110 },
                ],
            });

            // 4. Full-page preview: whole page, right proportions, and the document is exactly as before.
            var before = (await inspector.InspectActiveDocumentAsync())!;
            var preview = await previewRenderer.RenderActivePageAsync(1000);
            var after = (await inspector.InspectActiveDocumentAsync())!;
            Assert.Equal("image/png", preview.MimeType);
            Assert.Equal(1000, preview.HeightPixels);
            Assert.InRange(preview.WidthPixels, 712, 716);                      // 500 : 700
            Assert.Equal(size, preview.PhysicalSize);
            Assert.True(preview.Bytes.Length > 1000);
            Assert.Equal(before.AllShapes().Select(Describe), after.AllShapes().Select(Describe));
            await File.WriteAllBytesAsync(Path.Combine(outputRoot, "tam-sayfa-onizleme.png"), preview.Bytes);
            using (var image = SkiaSharp.SKBitmap.Decode(preview.Bytes))
            {
                var corner = image.GetPixel(2, 2);                               // page margin outside the frame: white, not transparent
                Assert.True(corner is { Red: > 240, Green: > 240, Blue: > 240, Alpha: 255 }, corner.ToString());
            }

            // 5. Compare: both defects are measured.
            var damaged = await comparer.CompareAsync(new Domain.References.VisualComparisonRequest { Expected = expected, Document = after, Size = size });
            output.WriteLine($"Damaged: similarity {damaged.Similarity:0.000}");
            foreach (var difference in damaged.Differences)
            {
                output.WriteLine("  - " + difference.Description);
            }

            var low = damaged.Differences.Single(difference => difference.Kind == Domain.References.DifferenceKind.Position && difference.ShapeName == V1Fixtures.Title);
            Assert.Equal(20, low.DeltaYMm, 0);
            Assert.Contains(damaged.Differences, difference => difference.Kind == Domain.References.DifferenceKind.Size && difference.ShapeName == V1Fixtures.Ring);
            Assert.True(damaged.Similarity < faithful.Similarity - 0.03);

            // 6–8. Bounded automatic improvement on the real document.
            var loop = new Domain.References.VisualImprovementLoop(inspector, executor, comparer, new Domain.References.VisualCorrectionPlanner(), previewRenderer);
            var improvement = await loop.RunAsync(new Domain.References.ImprovementRequest { Expected = expected, Size = size });
            foreach (var pass in improvement.Passes)
            {
                output.WriteLine($"Pass {pass.Number}: {pass.SimilarityBefore:0.000} -> {pass.SimilarityAfter:0.000} ({string.Join(" | ", pass.Corrections)})");
            }

            Assert.Equal(Domain.References.ImprovementStopReason.TargetReached, improvement.StopReason);
            Assert.InRange(improvement.Passes.Count, 1, 3);
            Assert.True(improvement.FinalSimilarity > damaged.Similarity);

            // 9. The geometry is back where the reference says it should be.
            var corrected = (await inspector.InspectActiveDocumentAsync())!;
            var titleAfter = corrected.FindShape(title.Id)!;
            Assert.Equal(title.Bounds.CenterYMm, titleAfter.Bounds.CenterYMm, 0);
            Assert.Equal("YASAKTIR", titleAfter.Text);
            var ringAfter = corrected.FindShape(ring.Id)!;
            Assert.Equal(270, ringAfter.Bounds.WidthMm, 0);
            Assert.Equal(250, ringAfter.Bounds.CenterXMm, 0);
            Assert.Equal(before.ShapeCount, corrected.ShapeCount);

            // 10. Production files.
            var files = await Run(executor, new AutomationPlan
            {
                Name = "Outputs",
                Actions =
                [
                    new SaveDocumentAction { Id = "cdr", FilePath = Path.Combine(outputRoot, "duzeltilmis.cdr") },
                    new ExportPdfAction { Id = "pdf", FilePath = Path.Combine(outputRoot, "duzeltilmis.pdf") },
                    new ExportPngAction { Id = "png", FilePath = Path.Combine(outputRoot, "duzeltilmis.png"), Dpi = 72 },
                ],
            });
            Assert.Equal(3, files.ProducedFiles.Count);
        }
        finally
        {
            await CloseTestDocuments(inspector, executor, userHadDocumentOpen);
        }

        static string Describe(ShapeSnapshot shape) =>
            FormattableString.Invariant($"{shape.Id}|{shape.Type}|{shape.Name}|{shape.Text}|{shape.Bounds.XMm:0.00}|{shape.Bounds.YMm:0.00}|{shape.Bounds.WidthMm:0.00}|{shape.Bounds.HeightMm:0.00}");
    }

    /// <summary>
    /// Things that were only assumed until they ran on the real application: the pixel size CorelDRAW
    /// reports for a placed bitmap (and so its effective DPI), Arabic text surviving the round trip, and a
    /// landscape full-page preview with artwork much smaller than the page.
    /// </summary>
    [Fact]
    [Trait("Category", "CorelIntegration")]
    public async Task Bitmap_resolution_arabic_text_and_a_landscape_page_preview_are_real()
    {
        if (!Enabled)
        {
            output.WriteLine("Set COREL_INTEGRATION=1 to run the installed-CorelDRAW production test.");
            return;
        }

        var outputRoot = PrepareOutputFolder("production");
        var service = fixture.Service!;
        var inspector = new CorelDocumentInspector(service);
        var executor = new CorelActionExecutor(service);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;
        const string Arabic = "ممنوع الدخول";

        static string Picture(string folder, string name, int pixels)
        {
            using var bitmap = new SkiaSharp.SKBitmap(pixels, pixels);
            using (var canvas = new SkiaSharp.SKCanvas(bitmap))
            {
                canvas.Clear(SkiaSharp.SKColors.SteelBlue);
                using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Orange };
                canvas.DrawCircle(pixels / 2f, pixels / 2f, pixels / 3f, paint);
            }

            var path = Path.Combine(folder, name);
            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
            return path;
        }

        var installed = System.Windows.Media.Fonts.SystemFontFamilies.Select(family => family.Source).ToArray();
        var font = new Domain.References.InstalledFontResolver(installed, supportsText: App.FontCoverage.Supports).Resolve("Impact", 0.9, Arabic);
        output.WriteLine($"Arabic font: asked for Impact, resolved to {font.FontFamily} ({font.Match}, missing glyphs in the asked font: {font.MissingGlyphs})");
        Assert.True(App.FontCoverage.Supports(font.FontFamily, Arabic));

        try
        {
            await Run(executor, new AutomationPlan
            {
                Name = "Production checks",
                Target = DocumentTarget.NewDocument,
                Actions =
                [
                    new CreateDocumentAction { Id = "doc", WidthMm = 300, HeightMm = 200 },
                    new ImportFileAction { Id = "low", FilePath = Picture(outputRoot, "dusuk.png", 200), Name = "Düşük çözünürlük", XMm = 20, YMm = 20, FitWidthMm = 100, FitHeightMm = 100 },
                    new ImportFileAction { Id = "high", FilePath = Picture(outputRoot, "yuksek.png", 1600), Name = "Yüksek çözünürlük", XMm = 150, YMm = 20, FitWidthMm = 100, FitHeightMm = 100 },
                    new CreateTextAction { Id = "arabic", Text = Arabic, Name = "Arapça", XMm = 20, YMm = 140, FontFamily = font.FontFamily, FontSizePt = 48 },
                ],
            });

            var document = (await inspector.InspectActiveDocumentAsync())!;

            // ---- Bitmap pixel size and effective DPI: dpi = pixels / (mm / 25.4)
            var low = document.FindShapesByName("Düşük çözünürlük").Single();
            var high = document.FindShapesByName("Yüksek çözünürlük").Single();
            output.WriteLine($"low : {low.Type} {low.Bitmap} at {low.Bounds.WidthMm:0.0} x {low.Bounds.HeightMm:0.0} mm -> {low.EffectiveDpi}");
            output.WriteLine($"high: {high.Type} {high.Bitmap} at {high.Bounds.WidthMm:0.0} x {high.Bounds.HeightMm:0.0} mm -> {high.EffectiveDpi}");
            Assert.Equal(ShapeKind.Bitmap, low.Type);
            Assert.Equal(new BitmapInfo(200, 200), low.Bitmap);
            Assert.Equal(new BitmapInfo(1600, 1600), high.Bitmap);
            Assert.Equal(100, low.Bounds.WidthMm, 0);
            Assert.Equal(50.8, low.EffectiveDpi!.Value.X, 0);
            Assert.Equal(406.4, high.EffectiveDpi!.Value.X, 0);

            var preflight = new Domain.Production.DesignPreflightService().Check(new Domain.Production.PreflightRequest { Document = document, InstalledFonts = installed });
            foreach (var line in preflight.Errors.Concat(preflight.Warnings).Concat(preflight.Info))
            {
                output.WriteLine("  preflight: " + line);
            }

            Assert.Contains(preflight.Warnings, warning => warning.Contains("Düşük çözünürlük") && warning.Contains("DPI"));
            Assert.DoesNotContain(preflight.Warnings, warning => warning.Contains("Yüksek çözünürlük"));
            Assert.DoesNotContain(preflight.Warnings, warning => warning.Contains("Arapça")); // its font is installed

            // ---- Arabic: the exact characters come back, in a font that has the glyphs
            var arabic = document.FindShapesByName("Arapça").Single();
            output.WriteLine($"arabic: '{arabic.Text}' in {arabic.FontFamily}, {arabic.Bounds.WidthMm:0.0} mm wide");
            Assert.Equal(Arabic, arabic.Text);
            Assert.Equal(font.FontFamily, arabic.FontFamily);
            Assert.True(arabic.Bounds.WidthMm > 20, "the text has no visible extent");

            // ---- Landscape page, artwork far smaller than the page
            var before = document.AllShapes().Select(shape => $"{shape.Id}|{shape.Bounds}").ToArray();
            var preview = await new CorelPagePreviewRenderer(service).RenderActivePageAsync(900);
            Assert.Equal((900, 600), (preview.WidthPixels, preview.HeightPixels)); // 300 : 200, the page — not the artwork's bounding box
            Assert.Equal(new Domain.References.PhysicalSize(300, 200), preview.PhysicalSize);
            await File.WriteAllBytesAsync(Path.Combine(outputRoot, "yatay-tam-sayfa.png"), preview.Bytes);
            using (var image = SkiaSharp.SKBitmap.Decode(preview.Bytes))
            {
                foreach (var (x, y) in new[] { (3, 3), (896, 3), (3, 596), (896, 596), (400, 570) })
                {
                    var pixel = image.GetPixel(x, y);
                    Assert.True(pixel is { Red: > 240, Green: > 240, Blue: > 240, Alpha: 255 }, $"({x},{y}) = {pixel}: empty page areas must be white");
                }

                var onPicture = image.GetPixel((int)(70 / 300.0 * 900), (int)(70 / 200.0 * 600)); // centre of the first picture
                Assert.False(onPicture is { Red: > 240, Green: > 240, Blue: > 240 }, $"artwork is missing from the preview: {onPicture}");
            }

            var after = (await inspector.InspectActiveDocumentAsync())!;
            Assert.Equal(before, after.AllShapes().Select(shape => $"{shape.Id}|{shape.Bounds}"));

            // Saved as CDR and PDF for a look by eye. (Reopening a CDR is covered by the operator-core test.)
            var files = await Run(executor, new AutomationPlan
            {
                Name = "Save",
                Actions = [new SaveDocumentAction { Id = "cdr", FilePath = Path.Combine(outputRoot, "arapca.cdr") }, new ExportPdfAction { Id = "pdf", FilePath = Path.Combine(outputRoot, "arapca.pdf") }],
            });
            Assert.Equal(2, files.ProducedFiles.Count);
        }
        finally
        {
            await CloseTestDocuments(inspector, executor, userHadDocumentOpen);
        }
    }

    /// <summary>
    /// The whole user workflow driven through the operator's view model — the same commands the buttons
    /// are bound to — against the real CorelDRAW: reference → analysis → plan → execute → compare →
    /// damage → compare → improve → production check → CDR/PDF → save as recipe → XLSX batch with
    /// CDR/PDF/PNG/SVG. The reference analysis is a scripted answer (no AI credential on this machine);
    /// everything after it is real.
    /// </summary>
    [Fact]
    [Trait("Category", "CorelIntegration")]
    public async Task The_operator_workflow_runs_end_to_end_through_the_view_model_in_coreldraw()
    {
        if (!Enabled)
        {
            output.WriteLine("Set COREL_INTEGRATION=1 to run the installed-CorelDRAW workflow test.");
            return;
        }

        var outputRoot = PrepareOutputFolder("workflow");
        var service = fixture.Service!;
        var inspector = new CorelDocumentInspector(service);
        var executor = new CorelActionExecutor(service);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;
        var harness = new OperatorHarness(
            inspector: inspector,
            executor: executor,
            pagePreview: new CorelPagePreviewRenderer(service),
            referencePreviews: Imaging.CompositeReferencePreviewRenderer.CreateDefault(),
            connect: async () => (await service.ConnectPreservingVisibilityAsync()).Version);
        var viewModel = harness.ViewModel;
        viewModel.OutputFolder = outputRoot;

        void Dump(string title, string text)
        {
            output.WriteLine("---- " + title);
            output.WriteLine(text);
        }

        try
        {
            // Reference → analysis → reviewable plan → CorelDRAW.
            viewModel.AddReferences([ReferenceFixtures.ProhibitionSignPng()]);
            viewModel.Request = "Bunun aynısını 500x700 mm olarak yap.";
            await harness.Run(viewModel.PreparePlanCommand);
            Assert.True(viewModel.HasAnalysis);
            Assert.True(viewModel.ProposedOperations.Count > 5);
            await harness.Run(viewModel.ExecuteCommand);
            Assert.True(viewModel.HasComparisonContext, string.Join("\n", viewModel.Logs));
            Assert.Equal(new Domain.References.PhysicalSize(500, 700), viewModel.ReconstructionContext!.Size);

            // Compare: a fresh reconstruction matches.
            await harness.Run(viewModel.CompareCommand);
            Dump("fresh", viewModel.ComparisonTitle + "\n" + viewModel.ComparisonText);
            Assert.True(viewModel.LastComparison!.Similarity >= 0.98);

            // Damage it: title 15 mm down, ring 10 % larger, one colour changed.
            var built = (await inspector.InspectActiveDocumentAsync())!;
            var title = built.FindShapesByName(V1Fixtures.Title).Single();
            var ring = built.FindShapesByName(V1Fixtures.Ring).Single(shape => shape.Type == ShapeKind.Ellipse);
            await Run(executor, new AutomationPlan
            {
                Name = "Damage",
                Actions =
                [
                    new MoveAction { Id = "low", Targets = [title.Id], DeltaYMm = 15 },
                    new ResizeAction { Id = "big", Targets = [ring.Id], ScalePercent = 110 },
                    new SetFillAction { Id = "colour", Targets = [title.Id], Color = "#1F5FBF" },
                ],
            });

            await harness.Run(viewModel.CompareCommand);
            Dump("damaged", viewModel.ComparisonTitle + "\n" + viewModel.ComparisonText);
            var damaged = viewModel.LastComparison!;
            Assert.Contains(damaged.Differences, difference => difference.Kind == Domain.References.DifferenceKind.Position && difference.ShapeName == V1Fixtures.Title);
            Assert.Contains(damaged.Differences, difference => difference.Kind == Domain.References.DifferenceKind.Size && difference.ShapeName == V1Fixtures.Ring);
            Assert.Contains(damaged.Differences, difference => difference.Kind == Domain.References.DifferenceKind.FillColor && difference.ShapeName == V1Fixtures.Title);
            Assert.Contains("aşağıda", viewModel.ComparisonText);
            Assert.False(damaged.AiUsed); // no credential: measured comparison only

            // Bounded automatic improvement.
            await harness.Run(viewModel.ImproveCommand);
            Dump("improved", viewModel.ComparisonTitle + "\n" + viewModel.ComparisonText);
            var improvement = viewModel.LastImprovement!;
            Assert.InRange(improvement.Passes.Count, 1, 3);
            Assert.True(improvement.FinalSimilarity > damaged.Similarity);
            Assert.True(improvement.FinalSimilarity >= 0.98);
            var repaired = (await inspector.InspectActiveDocumentAsync())!;
            Assert.Equal(title.Bounds.CenterYMm, repaired.FindShape(title.Id)!.Bounds.CenterYMm, 0);
            Assert.Equal(ring.Bounds.WidthMm, repaired.FindShape(ring.Id)!.Bounds.WidthMm, 0);

            // Production check.
            await harness.Run(viewModel.PreflightCommand);
            Dump("preflight", viewModel.PreflightTitle + "\n" + viewModel.PreflightText);
            Assert.True(viewModel.IsProductionReady);
            Assert.Empty(viewModel.LastPreflight!.Errors);

            // Save as a reusable automation (the last line of text becomes a variable).
            viewModel.RecipeName = "Yasak Levhası";
            viewModel.RecipeVariables = "SON_SATIR = YASAKTIR";
            Assert.True(viewModel.SaveRecipeCommand.CanExecute(null));
            viewModel.SaveRecipeCommand.Execute(null);
            var recipe = harness.Recipes.FindByName("Yasak Levhası");
            Assert.NotNull(recipe);
            Assert.Equal(["SON_SATIR"], recipe.Variables.Select(variable => variable.Name));

            // Files, through typed commands.
            var cdr = Path.Combine(outputRoot, "levha.cdr");
            var pdf = Path.Combine(outputRoot, "levha.pdf");
            viewModel.Request = $"Dosyayı {cdr} olarak kaydet\n{pdf} olarak PDF dışa aktar";
            await harness.Run(viewModel.PreparePlanCommand);
            await harness.Run(viewModel.ExecuteCommand);
            Assert.True(new FileInfo(cdr).Length > 1000);
            Assert.True(new FileInfo(pdf).Length > 1000);
            Assert.True(viewModel.HasComparisonContext); // saving does not make the reference stale

            await Run(executor, new AutomationPlan { Name = "Close", Actions = [new CloseDocumentAction { Id = "close" }] });

            // XLSX batch from the saved automation: two variations, four formats each.
            var workbookPath = Path.Combine(outputRoot, "varyasyonlar.xlsx");
            using (var workbook = new ClosedXML.Excel.XLWorkbook())
            {
                var other = workbook.AddWorksheet("Notlar");
                other.Cell(1, 1).Value = "SON_SATIR";
                other.Cell(2, 1).Value = "KULLANILMAZ";
                var sheet = workbook.AddWorksheet("Levhalar");
                sheet.Cell(1, 1).Value = "SON_SATIR";
                sheet.Cell(2, 1).Value = "YASAKTIR";
                sheet.Cell(3, 1).Value = "TEHLİKELİDİR";
                workbook.SaveAs(workbookPath);
            }

            viewModel.BatchRecipe = viewModel.Recipes.Single(candidate => candidate.Name == "Yasak Levhası");
            viewModel.BatchDataPath = workbookPath;
            Assert.Equal(["Notlar", "Levhalar"], viewModel.WorksheetNames);
            viewModel.SelectedWorksheet = "Levhalar";
            Assert.Equal(2, viewModel.BatchRecordCount);
            viewModel.BatchNamePattern = "levha_{{ROW}}";
            viewModel.BatchCdr = viewModel.BatchPdf = viewModel.BatchPng = viewModel.BatchSvg = true;
            await harness.Run(viewModel.RunBatchCommand);
            Dump("batch", viewModel.BatchStatus + "\n" + string.Join("\n", viewModel.BatchRows.Select(row => $"{row.Row} {row.OutputName} {row.Status}")));
            Assert.All(viewModel.BatchRows, row => Assert.Equal("Tamamlandı", row.Status));
            var produced = Directory.GetFiles(outputRoot, "levha_*").Select(Path.GetFileName).Order().ToArray();
            output.WriteLine(string.Join(", ", produced));
            Assert.Equal(8, produced.Length);
            Assert.All(new[] { ".cdr", ".pdf", ".png", ".svg" }, extension => Assert.Equal(2, produced.Count(name => name!.EndsWith(extension, StringComparison.OrdinalIgnoreCase))));
            Assert.NotEqual(File.ReadAllBytes(Path.Combine(outputRoot, "levha_001.png")), File.ReadAllBytes(Path.Combine(outputRoot, "levha_002.png"))); // each row really carries its own text

            foreach (var line in viewModel.Logs)
            {
                output.WriteLine(line);
            }
        }
        finally
        {
            await CloseTestDocuments(inspector, executor, userHadDocumentOpen);
        }
    }

    /// <summary>
    /// What "vector content is reused" means on the real application: a single-page PDF brought in through
    /// the import action arrives as editable vector objects, not as one flattened picture.
    /// </summary>
    [Fact]
    [Trait("Category", "CorelIntegration")]
    public async Task A_single_page_pdf_is_imported_as_editable_vector_objects()
    {
        if (!Enabled)
        {
            output.WriteLine("Set COREL_INTEGRATION=1 to run the installed-CorelDRAW PDF import test.");
            return;
        }

        var outputRoot = PrepareOutputFolder("pdf-import");
        var service = fixture.Service!;
        var inspector = new CorelDocumentInspector(service);
        var executor = new CorelActionExecutor(service);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;
        var pdf = Path.Combine(outputRoot, "kaynak.pdf");

        try
        {
            await Run(executor, new AutomationPlan
            {
                Name = "Source",
                Target = DocumentTarget.NewDocument,
                Actions =
                [
                    new CreateDocumentAction { Id = "doc", WidthMm = 200, HeightMm = 100 },
                    new CreateRectangleAction { Id = "frame", XMm = 10, YMm = 10, WidthMm = 180, HeightMm = 80, OutlineWidthMm = 2, FillColor = "#FFD400" },
                    new CreateEllipseAction { Id = "dot", XMm = 20, YMm = 30, WidthMm = 40, HeightMm = 40, FillColor = "#D8202A" },
                    new CreateTextAction { Id = "text", Text = "DİKKAT", XMm = 75, YMm = 35, FontFamily = "Arial", FontSizePt = 60 },
                    new ExportPdfAction { Id = "pdf", FilePath = pdf },
                    new CloseDocumentAction { Id = "close" },
                ],
            });

            await Run(executor, new AutomationPlan
            {
                Name = "Import",
                Target = DocumentTarget.NewDocument,
                Actions =
                [
                    new CreateDocumentAction { Id = "doc", WidthMm = 200, HeightMm = 100 },
                    new ImportFileAction { Id = "import", FilePath = pdf, Name = "PDF içeriği" },
                ],
            });

            var imported = (await inspector.InspectActiveDocumentAsync())!;
            var leaves = imported.AllShapes().Where(shape => shape.Type != ShapeKind.Group).ToArray();
            foreach (var shape in leaves)
            {
                output.WriteLine($"  {shape.Id} {shape.Type} '{shape.Text}' fill={shape.Fill?.ColorHex} {shape.Bounds.WidthMm:0.0} x {shape.Bounds.HeightMm:0.0} mm");
            }

            Assert.True(leaves.Length >= 3, "the PDF arrived as fewer objects than it was made of");
            Assert.DoesNotContain(leaves, shape => shape.Type == ShapeKind.Bitmap);            // not flattened
            Assert.Contains(leaves, shape => shape.Fill?.ColorHex is { } color && Domain.References.StructuralComparer.ColorDistance(color, "#D8202A") < 40);
            output.WriteLine(leaves.Any(shape => shape.IsText)
                ? "Text arrived as editable text."
                : "Text arrived as curves (vector, but no longer editable as text).");
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
