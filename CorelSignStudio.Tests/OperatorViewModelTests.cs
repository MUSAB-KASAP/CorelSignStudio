using ClosedXML.Excel;
using CorelSignStudio.AI;
using CorelSignStudio.App;
using CorelSignStudio.Domain.Ai;
using CorelSignStudio.Domain.Ai.Vision;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Planning;
using CorelSignStudio.Domain.Production;
using CorelSignStudio.Domain.Recipes;
using CorelSignStudio.Domain.References;
using CorelSignStudio.Storage;

namespace CorelSignStudio.Tests;

/// <summary>
/// CorelDRAW for the view model: nothing is open at first; running a plan that opens a document puts the
/// objects that plan creates on the page; every other plan edits what is there.
/// </summary>
internal sealed class FakeCorel : ICorelDocumentInspector, ICorelActionExecutor
{
    public FakeDesignWorld? World { get; set; }
    public List<AutomationPlan> Executed { get; } = [];

    public Task<DocumentSnapshot?> InspectActiveDocumentAsync(CancellationToken cancellationToken = default) =>
        World is null ? Task.FromResult<DocumentSnapshot?>(null) : World.InspectActiveDocumentAsync(cancellationToken);

    public Task<PlanExecutionResult> ExecuteAsync(AutomationPlan plan, ExecutionOptions? options = null, IProgress<ActionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Executed.Add(plan);
        if (plan.Target == DocumentTarget.NewDocument || plan.Actions.Any(action => action.OpensDocument))
        {
            World = FakeDesignWorld.From(ExpectedObjects.FromPlan(plan));
            var page = plan.Actions.OfType<CreateDocumentAction>().FirstOrDefault();
            if (page is not null)
            {
                World.Size = new PhysicalSize(page.WidthMm, page.HeightMm);
            }

            return Task.FromResult(new PlanExecutionResult { PlanId = plan.Id, Status = PlanExecutionStatus.Succeeded, CompletedActionIds = plan.Actions.Select(action => action.Id).ToArray() });
        }

        return World!.ExecuteAsync(plan, options, progress, cancellationToken);
    }
}

internal sealed class FakeShell : IDesktopShellService
{
    public string? BrowseForFolder(string initialFolder) => null;
    public void OpenFolder(string path) { }
    public void OpenFile(string path) { }
    public IReadOnlyList<string> BrowseForFiles(string title, string filter, bool multiple) => [];
    public bool Confirm(string message, string title) => true;
}

/// <summary>Records what the view model asked for; optionally never answers until cancelled.</summary>
internal sealed class RecordingVision(IReferenceVisionAnalyzer? inner = null) : IReferenceVisionAnalyzer
{
    public List<ReferenceAnalysisRequest> Requests { get; } = [];
    public bool Hang { get; set; }

    public async Task<ReferenceAnalysisResult> AnalyzeAsync(ReferenceAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        return inner is not null
            ? await inner.AnalyzeAsync(request, cancellationToken)
            : new ReferenceAnalysisResult { Status = AiPlanningStatus.Invalid, UserMessage = "yok" };
    }
}

internal sealed class OperatorHarness
{
    public FakeCorel Corel { get; } = new();
    public RecordingVision Vision { get; }
    public OperatorViewModel ViewModel { get; }
    public JsonRecipeStore Recipes { get; }
    public string Folder { get; } = TestFolders.Create();

    public OperatorHarness(
        bool scriptedVision = true,
        int pdfPages = 1,
        ICorelDocumentInspector? inspector = null,
        ICorelActionExecutor? executor = null,
        ICorelPagePreviewRenderer? pagePreview = null,
        IReferencePreviewRenderer? referencePreviews = null,
        Func<Task<string>>? connect = null,
        Func<string, int>? pdfPageCount = null)
    {
        object[] answers = Enumerable.Repeat((object)ReferenceFixtures.Answer(ReferenceFixtures.SignElements), 6).ToArray();
        Vision = new RecordingVision(scriptedVision ? ReferenceFixtures.Analyzer(new FakeVisionClient(answers)) : null);
        Recipes = new JsonRecipeStore(Path.Combine(Folder, "recipes"));
        ViewModel = new OperatorViewModel(new OperatorServices(
            ConnectCorel: connect ?? (() => Task.FromResult("27.0")),
            Inspector: inspector ?? Corel,
            Executor: executor ?? Corel,
            Planner: new PlannerRouter(new DeterministicCommandPlanner(), () => null, () => PlannerMode.Deterministic),
            Ai: new AiRuntime(new AiSettingsStore(Path.Combine(Folder, "ai.json"), new DpapiSecretProtector(), _ => null), _ => { }),
            Recipes: Recipes,
            Assets: new JsonAssetLibrary(Path.Combine(Folder, "assets")),
            History: new JsonExecutionHistoryStore(Path.Combine(Folder, "history")),
            Vision: Vision,
            Reconstruction: new ReferenceReconstructionPlanner(new InstalledFontResolver(["Arial"])),
            ReferenceAnalyzer: new FileReferenceAnalyzer(),
            ReferencePlanBuilder: new ImportReferencePlanBuilder(),
            Comparison: new VisualComparisonService(),
            Corrections: new VisualCorrectionPlanner(),
            PagePreview: pagePreview,
            ReferencePreviews: referencePreviews,
            Preflight: new DesignPreflightService(),
            InstalledFonts: ["Arial"],
            PdfPageCount: pdfPageCount ?? (_ => pdfPages),
            Shell: new FakeShell(),
            FileLog: new FileLogWriter(Path.Combine(Folder, "logs")),
            DataFolder: Folder,
            DefaultOutputFolder: Path.Combine(Folder, "output")));
    }

    /// <summary>Runs an asynchronous command the way a button does and waits until the view model is idle again.</summary>
    public async Task Run(AsyncRelayCommand command)
    {
        Assert.True(command.CanExecute(null), "the command is disabled");
        command.Execute(null);
        await Idle();
    }

    public async Task Idle()
    {
        for (var waited = 0; ViewModel.IsBusy && waited < 180000; waited += 10)
        {
            await Task.Delay(10);
        }

        Assert.False(ViewModel.IsBusy, "the view model stayed busy");
    }

    /// <summary>Reference → analysis → plan → executed in the fake CorelDRAW.</summary>
    public async Task Reconstruct()
    {
        ViewModel.AddReferences([ReferenceFixtures.ProhibitionSignPng()]);
        ViewModel.Request = "Bunun aynısını 500x700 mm olarak yap.";
        await Run(ViewModel.PreparePlanCommand);
        await Run(ViewModel.ExecuteCommand);
    }
}

public sealed class OperatorComparisonTests
{
    [Fact]
    public async Task Review_commands_are_disabled_until_there_is_something_to_review()
    {
        var harness = new OperatorHarness();
        var viewModel = harness.ViewModel;

        Assert.False(viewModel.CompareCommand.CanExecute(null));
        Assert.False(viewModel.ImproveCommand.CanExecute(null));
        Assert.False(viewModel.PreflightCommand.CanExecute(null));
        Assert.False(viewModel.CancelCommand.CanExecute(null));
        Assert.False(viewModel.HasComparisonContext);

        // An open document alone enables the production check, but not a comparison: there is no reference.
        harness.Corel.World = FakeDesignWorld.From([]);
        await harness.Run(viewModel.InspectCommand);
        Assert.True(viewModel.PreflightCommand.CanExecute(null));
        Assert.False(viewModel.CompareCommand.CanExecute(null));
        Assert.False(viewModel.ImproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Reconstruction_context_is_kept_after_the_plan_ran()
    {
        var harness = new OperatorHarness();
        var viewModel = harness.ViewModel;

        viewModel.AddReferences([ReferenceFixtures.ProhibitionSignPng()]);
        viewModel.Request = "Bunun aynısını 500x700 mm olarak yap.";
        await harness.Run(viewModel.PreparePlanCommand);
        Assert.False(viewModel.HasComparisonContext); // a plan that has not run describes no document yet

        await harness.Run(viewModel.ExecuteCommand);

        var context = viewModel.ReconstructionContext!;
        Assert.True(viewModel.HasComparisonContext);
        Assert.Equal(new PhysicalSize(500, 700), context.Size);
        Assert.Equal(6, context.Expected.Count);
        Assert.Equal(1, context.PageNumber);
        Assert.Equal("yasak-levhasi.png", context.Reference.FileName);
        Assert.Same(harness.Corel.Executed[^1], context.Plan);
        Assert.True(viewModel.CompareCommand.CanExecute(null));
        Assert.True(viewModel.ImproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Comparison_shows_similarity_matches_and_measured_differences()
    {
        var harness = new OperatorHarness();
        var viewModel = harness.ViewModel;
        await harness.Reconstruct();

        await harness.Run(viewModel.CompareCommand);
        Assert.Equal("Benzerlik: %100", viewModel.ComparisonTitle);
        Assert.Contains("Doğru:", viewModel.ComparisonText);
        Assert.Contains("✓", viewModel.ComparisonText);
        Assert.Contains("yapılmadı", viewModel.ComparisonText); // no AI configured: measured only, and it says so
        Assert.Equal(1, viewModel.ReviewTabIndex);
        Assert.DoesNotContain("{", viewModel.ComparisonText);   // never raw JSON

        harness.Corel.World!.Change(V1Fixtures.Title, shape => shape with { Bounds = shape.Bounds with { YMm = shape.Bounds.YMm + 20 } });
        await harness.Run(viewModel.CompareCommand);

        Assert.True(viewModel.LastComparison!.Similarity < 0.98);
        Assert.Contains("Farklar:", viewModel.ComparisonText);
        Assert.Contains("aşağıda", viewModel.ComparisonText);
        Assert.NotEqual("Benzerlik: %100", viewModel.ComparisonTitle);
    }

    [Fact]
    public async Task Automatic_improvement_reports_passes_and_the_stop_reason()
    {
        var harness = new OperatorHarness();
        var viewModel = harness.ViewModel;
        await harness.Reconstruct();
        harness.Corel.World!.Change(V1Fixtures.Title, shape => shape with { Bounds = shape.Bounds with { YMm = shape.Bounds.YMm + 20 } });
        var plansBefore = harness.Corel.Executed.Count;

        await harness.Run(viewModel.ImproveCommand);

        var result = viewModel.LastImprovement!;
        Assert.Equal(ImprovementStopReason.TargetReached, result.StopReason);
        Assert.InRange(result.Passes.Count, 1, 3);
        Assert.True(result.FinalSimilarity > result.InitialSimilarity);
        Assert.Equal(plansBefore + result.Passes.Count, harness.Corel.Executed.Count);
        Assert.Contains("Otomatik iyileştirme tamamlandı.", viewModel.ComparisonText);
        Assert.Contains("Başlangıç: %", viewModel.ComparisonText);
        Assert.Contains("Sonuç: %100", viewModel.ComparisonText);
        Assert.Contains("Geçiş 1 / 3: %", viewModel.ComparisonText);
        Assert.Contains(CorelSignStudio.Domain.Localization.Msg.Get("Improve.Stop.TargetReached"), viewModel.ComparisonText);
        Assert.Equal("Benzerlik: %100", viewModel.ComparisonTitle);
        Assert.True(viewModel.ImproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_unrelated_document_is_not_compared_with_a_stale_reference()
    {
        var harness = new OperatorHarness();
        var viewModel = harness.ViewModel;
        await harness.Reconstruct();

        // The user brings some other document to the front.
        harness.Corel.World = FakeDesignWorld.From([new ExpectedObject { ShapeName = "Başka bir şey", ActionId = "x", CenterXMm = 50, CenterYMm = 50, WidthMm = 10, HeightMm = 10 }]);
        await harness.Run(viewModel.CompareCommand);

        Assert.Null(viewModel.LastComparison);
        Assert.Contains("oluşturulmuş görünmüyor", viewModel.ComparisonText);

        var executed = harness.Corel.Executed.Count;
        await harness.Run(viewModel.ImproveCommand);
        Assert.Equal(executed, harness.Corel.Executed.Count); // and nothing is "corrected" in it either
    }

    [Fact]
    public async Task Creating_another_document_drops_the_reconstruction_context()
    {
        var harness = new OperatorHarness();
        var viewModel = harness.ViewModel;
        await harness.Reconstruct();
        Assert.True(viewModel.HasComparisonContext);

        viewModel.References.Clear();
        viewModel.Request = "500x700 mm belge oluştur";
        await harness.Run(viewModel.PreparePlanCommand);
        await harness.Run(viewModel.ExecuteCommand);

        Assert.False(viewModel.HasComparisonContext);
        Assert.False(viewModel.CompareCommand.CanExecute(null));
        Assert.False(viewModel.ImproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancelling_a_long_operation_restores_the_window()
    {
        var harness = new OperatorHarness();
        var viewModel = harness.ViewModel;
        harness.Vision.Hang = true;
        viewModel.AddReferences([ReferenceFixtures.ProhibitionSignPng()]);

        viewModel.AnalyzeReferenceCommand.Execute(null);
        Assert.True(viewModel.IsBusy);
        Assert.True(viewModel.CancelCommand.CanExecute(null));
        Assert.False(viewModel.PreparePlanCommand.CanExecute(null));

        viewModel.CancelCommand.Execute(null);
        await harness.Idle();

        Assert.Equal("İşlem iptal edildi.", viewModel.StatusMessage);
        Assert.False(viewModel.CancelCommand.CanExecute(null));
        Assert.True(viewModel.AnalyzeReferenceCommand.CanExecute(null));
        Assert.True(viewModel.PreparePlanCommand.CanExecute(null));
        Assert.False(viewModel.HasAnalysis);
    }
}

public sealed class OperatorPreflightTests
{
    private static ShapeSnapshot Bitmap(string name, int pixels, double sizeMm, double rotation = 0) => new()
    {
        Id = LogicalShapeId.FromNativeId(900 + pixels),
        NativeId = 900 + pixels,
        Type = ShapeKind.Bitmap,
        Name = name,
        Bounds = new BoundsMm(20, 20, sizeMm, sizeMm),
        LayerName = "Katman 1",
        RotationDegrees = rotation,
        Bitmap = pixels > 0 ? new BitmapInfo(pixels, pixels) : null,
    };

    [Theory]
    [InlineData(300, 25.4, 300)]
    [InlineData(1000, 254, 100)]
    [InlineData(591, 100, 150.114)]
    public void Effective_dpi_is_pixels_per_inch_of_placed_size(int pixels, double millimetres, double expected) =>
        Assert.Equal(expected, BitmapInfo.EffectiveDpi(pixels, millimetres)!.Value, 2);

    [Fact]
    public void Effective_dpi_is_unknown_without_pixels_or_for_a_skewed_placement()
    {
        Assert.Null(BitmapInfo.EffectiveDpi(0, 100));
        Assert.Null(BitmapInfo.EffectiveDpi(100, 0));
        Assert.Null(Bitmap("a", 0, 100).EffectiveDpi);
        Assert.Null(Bitmap("b", 1000, 100, rotation: 30).EffectiveDpi);
        Assert.Equal(254, Bitmap("c", 1000, 100, rotation: 90).EffectiveDpi!.Value.X, 0);
    }

    [Fact]
    public void Low_resolution_bitmaps_are_warned_about_and_good_ones_are_not()
    {
        var world = FakeDesignWorld.From([]);
        world.Add(Bitmap("Bulanık", 300, 100));     // 76 DPI
        world.Add(Bitmap("Orta", 700, 100));        // 178 DPI
        world.Add(Bitmap("Net", 1200, 100));        // 305 DPI
        world.Add(Bitmap("Bilinmeyen", 0, 100));

        var result = new DesignPreflightService().Check(new PreflightRequest { Document = world.Snapshot() });

        Assert.True(result.CanProduce);
        Assert.Contains(result.Warnings, warning => warning.Contains("Bulanık") && warning.Contains("çok düşük") && warning.Contains("76 DPI"));
        Assert.Contains(result.Warnings, warning => warning.Contains("Orta") && warning.Contains("düşük") && !warning.Contains("çok düşük") && warning.Contains("178 DPI"));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("Net"));
        Assert.Contains(result.Info, info => info.Contains("Net") && info.Contains("yeterli"));
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("Bilinmeyen"));
        Assert.Contains(result.Info, info => info.Contains("Bilinmeyen") && info.Contains("belirlenemedi")); // unknown, not guessed
    }

    [Fact]
    public async Task Production_check_shows_errors_warnings_and_information_in_the_window()
    {
        var harness = new OperatorHarness();
        var viewModel = harness.ViewModel;
        await harness.Reconstruct();
        Assert.Null(viewModel.IsProductionReady);

        await harness.Run(viewModel.PreflightCommand);
        Assert.True(viewModel.IsProductionReady);
        Assert.Contains("Bilgi", viewModel.PreflightText);
        Assert.Contains("500 × 700", viewModel.PreflightText);
        Assert.Equal(2, viewModel.ReviewTabIndex);

        harness.Corel.World!.Add(Bitmap("Fotoğraf", 300, 100));
        await harness.Run(viewModel.PreflightCommand);
        Assert.True(viewModel.IsProductionReady);
        Assert.Equal("Üretilebilir — uyarıları gözden geçirin", viewModel.PreflightTitle);
        Assert.Contains("Uyarılar", viewModel.PreflightText);
        Assert.Contains("⚠", viewModel.PreflightText);
        Assert.Contains("DPI", viewModel.PreflightText);

        // The page no longer has the size the reference was rebuilt at: that blocks production.
        harness.Corel.World.Size = new PhysicalSize(400, 700);
        await harness.Run(viewModel.PreflightCommand);
        Assert.False(viewModel.IsProductionReady);
        Assert.Equal("Üretime uygun değil — hataları giderin", viewModel.PreflightTitle);
        Assert.Contains("Hatalar", viewModel.PreflightText);
        Assert.Contains("❌", viewModel.PreflightText);
        Assert.True(viewModel.LastPreflight!.Errors.Count > 0);
    }
}

public sealed class OperatorBatchTests
{
    private static string Workbook()
    {
        var path = Path.Combine(TestFolders.Create(), "personel.xlsx");
        using var workbook = new XLWorkbook();
        var staff = workbook.AddWorksheet("Personel");
        staff.Cell(1, 1).Value = "PERSON_NAME";
        staff.Cell(1, 2).Value = "SERIAL";
        for (var row = 0; row < 7; row++)
        {
            staff.Cell(row + 2, 1).Value = "Kişi " + (row + 1);
            staff.Cell(row + 2, 2).SetValue((row + 1).ToString("000"));
        }

        var guests = workbook.AddWorksheet("Misafir");
        guests.Cell(1, 1).Value = "PERSON_NAME";
        guests.Cell(2, 1).Value = "Şükrü Öğüt";
        guests.Cell(3, 1).Value = "Ayşe Çağlar";
        workbook.SaveAs(path);
        return path;
    }

    private static OperatorHarness HarnessWithRecipe()
    {
        var harness = new OperatorHarness();
        harness.Recipes.Save(RecipeBuilder.FromPlan(AutomationTestData.DoorSignPlan(), "Kapı İsimliği", [new RecipeVariableBinding("PERSON_NAME", "Ahmet Yılmaz")]));
        harness.ViewModel.RefreshRecipesForTests();
        return harness;
    }

    [Fact]
    public void A_batch_cannot_start_without_recipe_data_rows_and_a_format()
    {
        var empty = new OperatorHarness().ViewModel;
        Assert.False(empty.RunBatchCommand.CanExecute(null)); // no recipe

        var viewModel = HarnessWithRecipe().ViewModel;
        Assert.NotNull(viewModel.BatchRecipe);
        Assert.False(viewModel.RunBatchCommand.CanExecute(null)); // no data file

        var headerOnly = Path.Combine(TestFolders.Create(), "bos.csv");
        File.WriteAllText(headerOnly, "PERSON_NAME\n");
        viewModel.BatchDataPath = headerOnly;
        Assert.False(viewModel.RunBatchCommand.CanExecute(null)); // no rows

        viewModel.BatchDataPath = Workbook();
        Assert.True(viewModel.RunBatchCommand.CanExecute(null));

        viewModel.BatchCdr = false;
        viewModel.BatchPdf = false;
        Assert.False(viewModel.RunBatchCommand.CanExecute(null)); // no output format
        viewModel.BatchPng = true;
        Assert.True(viewModel.RunBatchCommand.CanExecute(null));
    }

    [Fact]
    public void An_excel_file_offers_its_worksheets_and_the_chosen_one_is_read()
    {
        var viewModel = HarnessWithRecipe().ViewModel;

        viewModel.BatchDataPath = Workbook();

        Assert.True(viewModel.HasWorksheets);
        Assert.Equal(["Personel", "Misafir"], viewModel.WorksheetNames);
        Assert.Equal("Personel", viewModel.SelectedWorksheet);
        Assert.Equal(7, viewModel.BatchRecordCount);
        Assert.Contains("7 kayıt bulundu", viewModel.BatchDataSummary);
        Assert.Contains("Çalışma Sayfası: Personel", viewModel.BatchDataSummary);
        Assert.Contains("PERSON_NAME, SERIAL", viewModel.BatchDataSummary);
        Assert.Equal(5, viewModel.BatchSampleRows.Count); // only the first five are shown
        Assert.Contains("001", viewModel.BatchSampleRows[0]); // leading zeros survive

        viewModel.SelectedWorksheet = "Misafir";
        Assert.Equal(2, viewModel.BatchRecordCount);
        Assert.Contains("Çalışma Sayfası: Misafir", viewModel.BatchDataSummary);
        Assert.Contains("Şükrü Öğüt", viewModel.BatchSampleRows[0]);

        // The preview — what a run would produce — uses the chosen sheet too.
        viewModel.PreviewBatchCommand.Execute(null);
        Assert.Equal(2, viewModel.BatchRows.Count);
    }

    [Fact]
    public async Task The_chosen_worksheet_is_what_the_batch_runs()
    {
        var harness = HarnessWithRecipe();
        var viewModel = harness.ViewModel;
        viewModel.BatchDataPath = Workbook();
        viewModel.SelectedWorksheet = "Misafir";

        await harness.Run(viewModel.RunBatchCommand);

        var texts = harness.Corel.Executed.SelectMany(plan => plan.Actions.OfType<CreateTextAction>()).Select(text => text.Text).ToArray();
        Assert.Equal(["Şükrü Öğüt", "Ayşe Çağlar"], texts);
        Assert.False(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void Csv_still_works_and_has_no_worksheet_selector()
    {
        var viewModel = HarnessWithRecipe().ViewModel;
        var csv = Path.Combine(TestFolders.Create(), "personel.csv");
        File.WriteAllText(csv, "PERSON_NAME;SERIAL\nAhmet Yılmaz;001\nAyşe Öztürk;002\n", new System.Text.UTF8Encoding(true));

        viewModel.BatchDataPath = csv;

        Assert.False(viewModel.HasWorksheets);
        Assert.Null(viewModel.SelectedWorksheet);
        Assert.Equal(2, viewModel.BatchRecordCount);
        Assert.Contains("2 kayıt bulundu", viewModel.BatchDataSummary);
        Assert.DoesNotContain("Çalışma Sayfası", viewModel.BatchDataSummary);
        viewModel.PreviewBatchCommand.Execute(null);
        Assert.Equal(2, viewModel.BatchRows.Count);
        Assert.True(viewModel.RunBatchCommand.CanExecute(null));
    }

    [Fact]
    public void An_unreadable_data_file_is_explained_in_turkish()
    {
        var viewModel = HarnessWithRecipe().ViewModel;
        var broken = Path.Combine(TestFolders.Create(), "bozuk.xlsx");
        File.WriteAllText(broken, "bu bir excel dosyası değil");

        viewModel.BatchDataPath = broken;

        Assert.Equal(0, viewModel.BatchRecordCount);
        Assert.Contains("Excel dosyası okunamadı", viewModel.BatchDataSummary);
        Assert.DoesNotContain("Exception", viewModel.BatchDataSummary);
        Assert.False(viewModel.RunBatchCommand.CanExecute(null));
    }
}

public sealed class OperatorPdfPageTests
{
    private static string Pdf(string name = "katalog.pdf")
    {
        var path = Path.Combine(TestFolders.Create(), name);
        File.WriteAllText(path, "%PDF-1.4");
        return path;
    }

    [Fact]
    public void A_multi_page_pdf_asks_for_a_page_and_never_picks_one_itself()
    {
        var viewModel = new OperatorHarness(scriptedVision: false, pdfPages: 6).ViewModel;

        viewModel.AddReferences([Pdf()]);

        Assert.True(viewModel.HasPdfPages);
        Assert.Equal("PDF 6 sayfa içeriyor.", viewModel.PdfPagesText);
        Assert.Equal([1, 2, 3, 4, 5, 6], viewModel.PdfPageNumbers);
        Assert.Null(viewModel.SelectedPdfPage);
        Assert.False(viewModel.PreviewPdfPageCommand.CanExecute(null));

        viewModel.SelectedPdfPage = 9; // out of range: ignored
        Assert.Null(viewModel.SelectedPdfPage);
        viewModel.SelectedPdfPage = 3;
        Assert.Equal(3, viewModel.SelectedPdfPage);
    }

    [Fact]
    public void A_single_page_pdf_or_an_image_has_no_page_selector()
    {
        var viewModel = new OperatorHarness(scriptedVision: false, pdfPages: 1).ViewModel;
        viewModel.AddReferences([Pdf(), ReferenceFixtures.ProhibitionSignPng()]);
        Assert.False(viewModel.HasPdfPages);
        viewModel.SelectedReference = viewModel.References[1];
        Assert.False(viewModel.HasPdfPages);
    }

    [Fact]
    public async Task The_selected_page_is_the_page_that_is_analysed()
    {
        var harness = new OperatorHarness(scriptedVision: false, pdfPages: 6);
        var viewModel = harness.ViewModel;
        viewModel.AddReferences([Pdf()]);

        viewModel.Request = "Bunun aynısını 500x700 mm olarak yap.";
        await harness.Run(viewModel.PreparePlanCommand);
        Assert.Null(harness.Vision.Requests[^1].PageNumber); // nothing chosen: the analyzer asks, no page is assumed

        viewModel.SelectedPdfPage = 3;
        viewModel.Request = "Bunun aynısını 500x700 mm olarak yap.";
        await harness.Run(viewModel.PreparePlanCommand);
        Assert.Equal(3, harness.Vision.Requests[^1].PageNumber);
    }

    [Fact]
    public async Task A_page_written_in_the_request_wins_and_is_shown_in_the_selector()
    {
        var harness = new OperatorHarness(scriptedVision: false, pdfPages: 6);
        var viewModel = harness.ViewModel;
        viewModel.AddReferences([Pdf()]);
        viewModel.SelectedPdfPage = 5;

        viewModel.Request = "Bu pdf'in 3. sayfasını 500x700 mm olarak yeniden çiz.";
        await harness.Run(viewModel.PreparePlanCommand);

        Assert.Equal(3, harness.Vision.Requests[^1].PageNumber);
        Assert.Equal(3, viewModel.SelectedPdfPage);
    }
}

public sealed class VersionAndReleaseTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Fact]
    public void The_version_is_set_once_and_the_application_reads_it_back()
    {
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Build.props"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(props, "<Version>1\\.0\\.0</Version>"));

        Assert.Equal("1.0.0", AppInfo.Version);
        Assert.Equal(new Version(1, 0, 0, 0), typeof(AppInfo).Assembly.GetName().Version);

        var viewModel = new OperatorHarness().ViewModel;
        Assert.Equal("Sürüm 1.0.0", viewModel.AboutVersion);
        Assert.Equal("Corel AI Operatörü", viewModel.AboutName);

        // No source file repeats the number.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "CorelSignStudio.App"), "*.*", SearchOption.TopDirectoryOnly)
                     .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".xaml", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("1.0.0", File.ReadAllText(file));
        }

        Assert.Contains("{Binding AboutVersion}", File.ReadAllText(Path.Combine(Root, "CorelSignStudio.App", "OperatorWindow.xaml")));
    }

    [Fact]
    public void An_installed_copy_keeps_its_files_in_the_user_profile()
    {
        var installed = AppPaths.Resolve(Path.Combine(Path.GetTempPath(), "CorelAI-Operator-" + Guid.NewGuid().ToString("N")), @"C:\Users\x\AppData\Local", @"C:\Users\x\Documents");
        Assert.False(installed.IsDevelopment);
        Assert.Equal(@"C:\Users\x\AppData\Local\CorelSignStudio\data", installed.DataFolder);
        Assert.Equal(@"C:\Users\x\AppData\Local\CorelSignStudio\logs", installed.LogFolder);
        Assert.Equal(@"C:\Users\x\Documents\Corel AI Operatörü", installed.OutputFolder);

        var development = AppPaths.Resolve(AppContext.BaseDirectory, @"C:\a", @"C:\b");
        Assert.True(development.IsDevelopment);
        Assert.Equal(Path.Combine(Root, "data"), development.DataFolder);
    }

    [Fact]
    public void Publish_and_installer_scripts_exist_and_ship_no_user_data()
    {
        var publish = File.ReadAllText(Path.Combine(Root, "build", "publish.ps1"));
        Assert.Contains("win-x64", publish);
        Assert.Contains("--self-contained", publish);
        Assert.Contains("CorelAI-Operator", publish);
        Assert.Contains("$PSScriptRoot", publish); // finds the repository itself

        var installer = File.ReadAllText(Path.Combine(Root, "build", "installer.iss"));
        Assert.Contains("Corel AI Operatörü", installer);
        Assert.Contains("CorelAI-Operator", installer);
        Assert.Contains("[Icons]", installer);
        Assert.Contains("desktopicon", installer);
        Assert.Contains("#ifndef AppVersion", installer); // the version is passed in by the publish script
        Assert.DoesNotContain("ai-settings", installer);
        Assert.DoesNotContain("LOCALAPPDATA", installer.ToUpperInvariant().Replace("{LOCALAPPDATA}", ""));
    }
}

public sealed class FontCoverageTests
{
    private const string Arabic = "ممنوع الدخول";

    [Fact]
    public void Glyph_coverage_is_read_from_the_installed_font()
    {
        Assert.True(FontCoverage.Supports("Arial", "GİRİŞ ÇIKIŞ ğüşöçı"));
        Assert.True(FontCoverage.Supports("Arial", Arabic));
        Assert.False(FontCoverage.Supports("Impact", Arabic));
        Assert.False(FontCoverage.Supports("Böyle Bir Yazı Tipi Yok 123", "abc") && FontCoverage.Supports("Böyle Bir Yazı Tipi Yok 123", Arabic));
    }

    [Fact]
    public void Arabic_text_falls_back_to_a_font_that_can_draw_it()
    {
        var installed = System.Windows.Media.Fonts.SystemFontFamilies.Select(family => family.Source).ToArray();
        var resolver = new InstalledFontResolver(installed, supportsText: FontCoverage.Supports);

        var resolution = resolver.Resolve("Impact", 0.9, Arabic);

        Assert.NotEqual("Impact", resolution.FontFamily);
        Assert.True(FontCoverage.Supports(resolution.FontFamily, Arabic), resolution.FontFamily);
        Assert.True(resolution.MissingGlyphs); // the preferred font was rejected because it cannot draw the text

        // Latin text keeps the font that was asked for.
        Assert.Equal("Impact", resolver.Resolve("Impact", 0.9, "GİRMEK YASAKTIR").FontFamily);
    }
}
