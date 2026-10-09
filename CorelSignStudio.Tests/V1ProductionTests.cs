using ClosedXML.Excel;
using CorelSignStudio.Domain.Ai;
using CorelSignStudio.Domain.Ai.Vision;
using CorelSignStudio.Domain.Assets;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Batch;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Production;
using CorelSignStudio.Domain.Recipes;
using CorelSignStudio.Domain.References;
using CorelSignStudio.Imaging;
using CorelSignStudio.Storage;

namespace CorelSignStudio.Tests;

/// <summary>
/// An in-memory document that understands the correction actions, so comparison, correction planning and
/// the improvement loop can be exercised end to end without CorelDRAW.
/// </summary>
internal sealed class FakeDesignWorld : ICorelDocumentInspector, ICorelActionExecutor
{
    private readonly List<ShapeSnapshot> _shapes = [];
    private int _nextId = 1;

    public PhysicalSize Size { get; set; } = new(500, 700);

    /// <summary>1 applies corrections fully; 0.5 only half-way (a stubborn document); 0 not at all.</summary>
    public double Compliance { get; set; } = 1;

    public bool FailExecution { get; set; }
    public List<AutomationPlan> Executed { get; } = [];
    public bool HasDocument { get; set; } = true;

    public static FakeDesignWorld From(IEnumerable<ExpectedObject> expected)
    {
        var world = new FakeDesignWorld();
        foreach (var item in expected)
        {
            world._shapes.Add(new ShapeSnapshot
            {
                Id = LogicalShapeId.FromNativeId(world._nextId),
                NativeId = world._nextId++,
                Type = item.Text is not null ? ShapeKind.ArtisticText : ShapeKind.Rectangle,
                Name = item.ShapeName,
                Text = item.Text,
                Bounds = new BoundsMm(item.CenterXMm - (item.WidthMm / 2), item.CenterYMm - (Height(item) / 2), item.WidthMm, Height(item)),
                LayerName = "Katman 1",
                Fill = item.FillColor is null ? new FillInfo(FillKind.None) : new FillInfo(FillKind.Uniform, item.FillColor),
                Outline = item.OutlineColor is null ? new OutlineInfo(false) : new OutlineInfo(true, item.OutlineColor, 1),
            });
        }

        return world;

        static double Height(ExpectedObject item) => item.CompareHeight ? item.HeightMm : 40;
    }

    public ShapeSnapshot Shape(string name) => _shapes.First(shape => shape.Name == name);

    public void Change(string name, Func<ShapeSnapshot, ShapeSnapshot> change) => _shapes[_shapes.FindIndex(shape => shape.Name == name)] = change(Shape(name));

    public void Remove(string name) => _shapes.RemoveAll(shape => shape.Name == name);

    public void Add(ShapeSnapshot shape) => _shapes.Add(shape);

    public DocumentSnapshot Snapshot() => new()
    {
        Title = "test",
        Pages = [new PageSnapshot { Index = 1, WidthMm = Size.WidthMm, HeightMm = Size.HeightMm, Shapes = _shapes.ToArray() }],
    };

    public Task<DocumentSnapshot?> InspectActiveDocumentAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(HasDocument ? Snapshot() : null);

    public Task<PlanExecutionResult> ExecuteAsync(AutomationPlan plan, ExecutionOptions? options = null, IProgress<ActionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Executed.Add(plan);
        if (FailExecution)
        {
            return Task.FromResult(new PlanExecutionResult { PlanId = plan.Id, Status = PlanExecutionStatus.Failed, FailedActionId = plan.Actions[0].Id, FailedActionIndex = 1, ErrorMessage = "simulated", RolledBack = true });
        }

        foreach (var action in plan.Actions.OfType<TargetedAction>())
        {
            var index = _shapes.FindIndex(shape => shape.Id == action.Targets[0]);
            var shape = _shapes[index];
            var bounds = shape.Bounds;
            _shapes[index] = action switch
            {
                MoveAction move => shape with { Bounds = bounds with { XMm = bounds.XMm + (move.DeltaXMm * Compliance), YMm = bounds.YMm + (move.DeltaYMm * Compliance) } },
                ResizeAction resize => shape with { Bounds = Resized(bounds, resize) },
                SetTextAction text => Compliance > 0 ? shape with { Text = text.Text } : shape,
                SetFillAction fill => Compliance > 0 ? shape with { Fill = new FillInfo(FillKind.Uniform, fill.Color) } : shape,
                SetOutlineAction outline => Compliance > 0 ? shape with { Outline = new OutlineInfo(true, outline.Color, 1) } : shape,
                _ => shape,
            };
        }

        return Task.FromResult(new PlanExecutionResult { PlanId = plan.Id, Status = PlanExecutionStatus.Succeeded, CompletedActionIds = plan.Actions.Select(action => action.Id).ToArray() });
    }

    private BoundsMm Resized(BoundsMm bounds, ResizeAction resize)
    {
        var width = bounds.WidthMm + (((resize.WidthMm ?? bounds.WidthMm) - bounds.WidthMm) * Compliance);
        var height = resize.HeightMm is { } target
            ? bounds.HeightMm + ((target - bounds.HeightMm) * Compliance)
            : resize.KeepAspectRatio ? bounds.HeightMm * (width / bounds.WidthMm) : bounds.HeightMm;
        return new BoundsMm(bounds.CenterXMm - (width / 2), bounds.CenterYMm - (height / 2), width, height);
    }
}

internal static class V1Fixtures
{
    private static readonly ReferenceReconstructionPlanner Planner = new(new InstalledFontResolver(["Arial"]));

    /// <summary>The reconstruction plan of the standard sign at 500 × 700 mm, and what it expects.</summary>
    public static async Task<(AutomationPlan Plan, IReadOnlyList<ExpectedObject> Expected, ReferenceAnalysis Analysis)> Sign()
    {
        var analysis = await ReferenceFixtures.Analyze(ReferenceFixtures.SignElements);
        var plan = Planner.Plan(new ReconstructionRequest
        {
            Reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng()), Analysis = analysis, UserRequest = "500x700 mm",
        }).Plan!;
        return (plan, ExpectedObjects.FromPlan(plan), analysis);
    }

    public const string Title = "Satır 3 (ref_005)";
    public const string Ring = "Yasak işareti (ref_002)";
    public const string Frame = "Çerçeve (ref_001)";

    public static VisualComparisonRequest Request(IReadOnlyList<ExpectedObject> expected, FakeDesignWorld world) =>
        new() { Expected = expected, Document = world.Snapshot(), Size = new PhysicalSize(500, 700) };
}

public sealed class VisualComparisonTests
{
    [Fact]
    public async Task Expected_objects_are_read_from_the_reconstruction_plan()
    {
        var (_, expected, _) = await V1Fixtures.Sign();

        Assert.Equal(
            [V1Fixtures.Frame, V1Fixtures.Ring, V1Fixtures.Ring + " /", "Satır 1 (ref_003)", "Satır 2 (ref_004)", V1Fixtures.Title],
            expected.Select(item => item.ShapeName));
        var frame = expected[0];
        Assert.Equal((250d, 350d, 460d, 658d), (frame.CenterXMm, frame.CenterYMm, frame.WidthMm, frame.HeightMm));
        var title = expected[^1];
        Assert.Equal(("YASAKTIR", 300d, false, "#D8202A"), (title.Text, title.WidthMm, title.CompareHeight, title.FillColor));
        Assert.Equal((250d, 581d), (title.CenterXMm, title.CenterYMm));
        Assert.All(expected, item => Assert.False(item.IsPlaceholder));
    }

    [Fact]
    public async Task A_faithful_document_scores_one_with_no_differences()
    {
        var (_, expected, _) = await V1Fixtures.Sign();

        var result = new StructuralComparer().Compare(V1Fixtures.Request(expected, FakeDesignWorld.From(expected)));

        Assert.Equal(1, result.Similarity);
        Assert.Empty(result.Differences);
        Assert.Contains("Sayfa ölçüsü doğru (500 × 700 mm)", result.Matches);
        Assert.Contains(V1Fixtures.Title + " doğru", result.Matches);
        Assert.Equal(6, result.ComparedObjects);
        Assert.False(result.HasCorrectableDifferences);
    }

    [Fact]
    public async Task Position_size_text_and_colour_differences_are_measured()
    {
        var (_, expected, _) = await V1Fixtures.Sign();
        var world = FakeDesignWorld.From(expected);
        world.Change(V1Fixtures.Title, shape => shape with { Bounds = shape.Bounds with { YMm = shape.Bounds.YMm + 20 }, Text = "YASAKTIR!" });
        world.Change(V1Fixtures.Ring, shape => shape with { Bounds = new BoundsMm(shape.Bounds.XMm - 13.5, shape.Bounds.YMm - 13.5, 297, 297) });
        world.Change(V1Fixtures.Frame, shape => shape with { Outline = new OutlineInfo(true, "#00AA00", 6) });
        world.Change("Satır 1 (ref_003)", shape => shape with { Fill = new FillInfo(FillKind.Uniform, "#0000FF"), Bounds = shape.Bounds with { XMm = shape.Bounds.XMm - 7 } });

        var result = new StructuralComparer().Compare(V1Fixtures.Request(expected, world));

        var position = result.Differences.Single(difference => difference.Kind == DifferenceKind.Position && difference.ShapeName == V1Fixtures.Title);
        Assert.Equal((0d, 20d), (position.DeltaXMm, position.DeltaYMm));
        Assert.Equal("Satır 3 (ref_005) yaklaşık 20 mm aşağıda.", position.Description);
        Assert.Equal(world.Shape(V1Fixtures.Title).Id, position.ShapeId);

        var size = result.Differences.Single(difference => difference.Kind == DifferenceKind.Size);
        Assert.Equal((V1Fixtures.Ring, 270d, 270d), (size.ShapeName, size.ExpectedWidthMm, size.ExpectedHeightMm));
        Assert.Equal("Yasak işareti (ref_002) yaklaşık %10 büyük.", size.Description);

        var text = result.Differences.Single(difference => difference.Kind == DifferenceKind.Text);
        Assert.Equal("YASAKTIR", text.ExpectedText);
        Assert.Contains("“YASAKTIR!”, olması gereken “YASAKTIR”", text.Description);

        Assert.Equal("#000000", result.Differences.Single(difference => difference.Kind == DifferenceKind.OutlineColor).ExpectedColor);
        Assert.Equal("#D8202A", result.Differences.Single(difference => difference.Kind == DifferenceKind.FillColor).ExpectedColor);
        Assert.Equal("Satır 1 (ref_003) yaklaşık 7 mm solda.", result.Differences.Single(difference => difference.Kind == DifferenceKind.Position && difference.ShapeName!.StartsWith("Satır 1", StringComparison.Ordinal)).Description);

        Assert.InRange(result.Similarity, 0.6, 0.95);
        Assert.True(result.HasCorrectableDifferences);
        Assert.Contains("Satır 2 (ref_004) doğru", result.Matches);
    }

    [Fact]
    public async Task Small_deviations_within_tolerance_are_not_reported()
    {
        var (_, expected, _) = await V1Fixtures.Sign();
        var world = FakeDesignWorld.From(expected);
        world.Change(V1Fixtures.Title, shape => shape with { Bounds = shape.Bounds with { XMm = shape.Bounds.XMm + 0.6, WidthMm = 302 }, Fill = new FillInfo(FillKind.Uniform, "#D61E28") });

        Assert.Empty(new StructuralComparer().Compare(V1Fixtures.Request(expected, world)).Differences);
    }

    [Fact]
    public async Task Missing_objects_wrong_page_and_extras_lower_the_score()
    {
        var (_, expected, _) = await V1Fixtures.Sign();
        var world = FakeDesignWorld.From(expected);
        world.Remove(V1Fixtures.Title);
        world.Size = new PhysicalSize(210, 297);

        var result = new StructuralComparer().Compare(V1Fixtures.Request(expected, world));

        Assert.Equal("Satır 3 (ref_005) belgede bulunamadı.", result.Differences.Single(difference => difference.Kind == DifferenceKind.Missing).Description);
        Assert.Equal("Sayfa ölçüsü 210 × 297 mm, olması gereken 500 × 700 mm.", result.Differences.Single(difference => difference.Kind == DifferenceKind.PageSize).Description);
        Assert.True(result.Similarity < 0.5);
        Assert.Equal(441.67, StructuralComparer.ColorDistance("#000000", "#FFFFFF"), 1);
    }

    [Fact]
    public async Task Visual_ai_findings_are_parsed_strictly_and_added_as_advisory_differences()
    {
        var (_, expected, _) = await V1Fixtures.Sign();
        var world = FakeDesignWorld.From(expected);
        var image = new ReferencePreview { ReferenceId = "r", FileName = "a.png", WidthPixels = 10, HeightPixels = 14, MimeType = "image/png", Bytes = [1, 2, 3] };
        var client = new FakeVisionClient(
            """{"similarity":0.8,"differences":[{"object":"Satır 3 (ref_005)","severity":"minor","description":"Alt yazı referanstan daha ince görünüyor."},{"object":"uydurma","severity":"major","description":"El piktogramı eksik."},{"severity":"major"}],"notes":["Kırmızı ton çok yakın."]}""");
        var service = new VisualComparisonService(visual: new AiVisualComparer(() => client));

        var result = await service.CompareAsync(V1Fixtures.Request(expected, world) with { ReferenceImage = image, OutputImage = image });

        Assert.True(result.AiUsed);
        Assert.Equal(0.94, result.Similarity, 2); // 70% measured (1.0) + 30% visual (0.8)
        var visual = result.Differences.Where(difference => difference.Kind == DifferenceKind.Visual).ToList();
        Assert.Equal(2, visual.Count);
        Assert.Equal((V1Fixtures.Title, DifferenceSeverity.Minor, "ai"), (visual[0].ShapeName, visual[0].Severity, visual[0].Source));
        Assert.Null(visual[1].ShapeName); // a name that is not in the list is not trusted
        Assert.Contains("Kırmızı ton çok yakın.", result.Notes);
        Assert.Equal(2, client.Requests[0].Images.Count);
        Assert.Contains("Do not propose code", client.Requests[0].SystemPrompt);

        // Nothing visual is ever turned into a numeric correction.
        Assert.Null(new VisualCorrectionPlanner().Plan(result, world.Snapshot()).Plan);

        Assert.Null(AiVisualComparer.Parse("Görseller aynı.", V1Fixtures.Request(expected, world)));
        Assert.Null(AiVisualComparer.Parse("""{"differences":[]}""", V1Fixtures.Request(expected, world)));
    }

    [Fact]
    public async Task Without_images_or_a_model_the_measured_comparison_stands_alone()
    {
        var (_, expected, _) = await V1Fixtures.Sign();
        var world = FakeDesignWorld.From(expected);
        var failing = new FakeVisionClient(new AiClientException(AiErrorKind.Network, "down"));
        var image = new ReferencePreview { ReferenceId = "r", FileName = "a.png", WidthPixels = 1, HeightPixels = 1, MimeType = "image/png", Bytes = [1] };

        var noImages = await new VisualComparisonService(visual: new AiVisualComparer(() => failing)).CompareAsync(V1Fixtures.Request(expected, world));
        var providerDown = await new VisualComparisonService(visual: new AiVisualComparer(() => failing)).CompareAsync(V1Fixtures.Request(expected, world) with { ReferenceImage = image, OutputImage = image });

        Assert.False(noImages.AiUsed);
        Assert.Empty(failing.Requests.Take(0));
        Assert.Equal(1, providerDown.Similarity);
        Assert.Contains("Görsel karşılaştırma yapılamadı; yalnızca ölçülere göre karşılaştırıldı.", providerDown.Notes);
    }
}

public sealed class VisualCorrectionTests
{
    private static async Task<(IReadOnlyList<ExpectedObject> Expected, FakeDesignWorld World)> Skewed()
    {
        var (_, expected, _) = await V1Fixtures.Sign();
        var world = FakeDesignWorld.From(expected);
        world.Change(V1Fixtures.Title, shape => shape with { Bounds = shape.Bounds with { YMm = shape.Bounds.YMm + 20 } });
        world.Change(V1Fixtures.Ring, shape => shape with { Bounds = new BoundsMm(shape.Bounds.XMm - 13.5, shape.Bounds.YMm - 13.5, 297, 297) });
        return (expected, world);
    }

    private static VisualImprovementLoop Loop(FakeDesignWorld world) => new(world, world, new VisualComparisonService(), new VisualCorrectionPlanner());

    [Fact]
    public async Task Corrections_are_typed_actions_computed_from_measurements()
    {
        var (expected, world) = await Skewed();
        world.Change("Satır 1 (ref_003)", shape => shape with { Text = "BU ALAN", Fill = new FillInfo(FillKind.Uniform, "#0000FF") });
        var comparison = new StructuralComparer().Compare(V1Fixtures.Request(expected, world));

        var correction = new VisualCorrectionPlanner().Plan(comparison, world.Snapshot());

        var plan = correction.Plan!;
        Assert.True(plan.Validate().IsValid, plan.Validate().ToString());
        Assert.False(plan.HasDestructiveActions);
        Assert.Equal(DocumentTarget.ActiveDocument, plan.Target);
        Assert.IsType<ResizeAction>(plan.Actions[0]); // size first, then everything else
        var resize = plan.Actions.OfType<ResizeAction>().Single();
        Assert.Equal((270d, 270d, PositionAnchor.Center), (resize.WidthMm, resize.HeightMm, resize.Anchor));
        Assert.Equal([world.Shape(V1Fixtures.Ring).Id], resize.Targets);
        var move = plan.Actions.OfType<MoveAction>().Single();
        Assert.Equal((0d, -20d), (move.DeltaXMm, move.DeltaYMm));
        Assert.Equal([world.Shape(V1Fixtures.Title).Id], move.Targets);
        Assert.Equal("BU ALANA", plan.Actions.OfType<SetTextAction>().Single().Text);
        Assert.Equal("#D8202A", plan.Actions.OfType<SetFillAction>().Single().Color);
        Assert.Equal(4, correction.Corrections.Count);
        Assert.All(plan.Actions, action => Assert.Contains(action.TypeName, ActionTypes.AllNames));
    }

    [Fact]
    public async Task Missing_objects_are_reported_not_improvised()
    {
        var (_, expected, _) = await V1Fixtures.Sign();
        var world = FakeDesignWorld.From(expected);
        world.Remove(V1Fixtures.Title);
        var comparison = new StructuralComparer().Compare(V1Fixtures.Request(expected, world));

        var correction = new VisualCorrectionPlanner().Plan(comparison, world.Snapshot());

        Assert.Null(correction.Plan);
        Assert.Equal("Satır 3 (ref_005) belgede bulunamadı.", Assert.Single(correction.Unresolved));
    }

    [Fact]
    public async Task Loop_stops_as_soon_as_the_target_is_reached()
    {
        var (expected, world) = await Skewed();

        var result = await Loop(world).RunAsync(new ImprovementRequest { Expected = expected, Size = world.Size });

        Assert.Equal(ImprovementStopReason.TargetReached, result.StopReason);
        var pass = Assert.Single(result.Passes);
        Assert.Equal(1, pass.Number);
        Assert.True(pass.SimilarityBefore < 0.95);
        Assert.Equal(1, pass.SimilarityAfter);
        Assert.Equal(2, pass.Corrections.Count);
        Assert.True(result.InitialSimilarity < result.FinalSimilarity);
        Assert.Empty(result.FinalComparison!.Differences);
        Assert.Single(world.Executed);
        Assert.Equal(581, world.Shape(V1Fixtures.Title).Bounds.CenterYMm, 3);
        Assert.Equal(270, world.Shape(V1Fixtures.Ring).Bounds.WidthMm, 3);
    }

    [Fact]
    public async Task Loop_does_nothing_when_the_design_already_matches()
    {
        var (_, expected, _) = await V1Fixtures.Sign();
        var world = FakeDesignWorld.From(expected);

        var result = await Loop(world).RunAsync(new ImprovementRequest { Expected = expected, Size = world.Size });

        Assert.Equal(ImprovementStopReason.TargetReached, result.StopReason);
        Assert.Empty(result.Passes);
        Assert.Empty(world.Executed);
    }

    [Fact]
    public async Task Loop_never_exceeds_the_maximum_number_of_passes()
    {
        var (expected, world) = await Skewed();
        world.Change(V1Fixtures.Title, shape => shape with { Bounds = shape.Bounds with { YMm = shape.Bounds.YMm + 200 } });
        world.Compliance = 0.5; // every pass only gets half-way, so the target stays out of reach

        var result = await Loop(world).RunAsync(
            new ImprovementRequest { Expected = expected, Size = world.Size },
            new ImprovementOptions { MaxPasses = 3, TargetSimilarity = 0.999, MinimumImprovement = 0.0001 });

        Assert.Equal(ImprovementStopReason.MaxPassesReached, result.StopReason);
        Assert.Equal(3, result.Passes.Count);
        Assert.Equal(3, world.Executed.Count);
        Assert.Equal([1, 2, 3], result.Passes.Select(pass => pass.Number));
        Assert.All(result.Passes, pass => Assert.True(pass.SimilarityAfter > pass.SimilarityBefore));
    }

    [Fact]
    public async Task Loop_stops_when_a_pass_does_not_help()
    {
        var (expected, world) = await Skewed();
        world.Compliance = 0; // corrections are accepted but change nothing

        var result = await Loop(world).RunAsync(new ImprovementRequest { Expected = expected, Size = world.Size });

        Assert.Equal(ImprovementStopReason.NoImprovement, result.StopReason);
        Assert.Single(result.Passes);
        Assert.Equal(result.Passes[0].SimilarityBefore, result.Passes[0].SimilarityAfter);
    }

    [Fact]
    public async Task Loop_reports_failure_cancellation_missing_document_and_unfixable_designs()
    {
        var (expected, world) = await Skewed();
        var request = new ImprovementRequest { Expected = expected, Size = world.Size };

        world.FailExecution = true;
        var failed = await Loop(world).RunAsync(request);
        Assert.Equal(ImprovementStopReason.ExecutionFailed, failed.StopReason);
        Assert.Single(failed.Passes);
        Assert.Equal(failed.InitialSimilarity, failed.FinalSimilarity);

        world.FailExecution = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await Loop(world).RunAsync(request, cancellationToken: cancellation.Token);
        Assert.Equal(ImprovementStopReason.Cancelled, cancelled.StopReason);
        Assert.Empty(world.Executed.Skip(1));

        world.HasDocument = false;
        Assert.Equal(ImprovementStopReason.NoDocument, (await Loop(world).RunAsync(request)).StopReason);

        var (_, all, _) = await V1Fixtures.Sign();
        var incomplete = FakeDesignWorld.From(all);
        incomplete.Remove(V1Fixtures.Title);
        Assert.Equal(ImprovementStopReason.NoSafeCorrections, (await Loop(incomplete).RunAsync(new ImprovementRequest { Expected = all, Size = incomplete.Size })).StopReason);
    }

    [Fact]
    public void Every_stop_reason_has_a_turkish_explanation() =>
        Assert.All(Enum.GetNames<ImprovementStopReason>(), reason => Assert.True(Domain.Localization.Msg.Has("Improve.Stop." + reason), reason));
}

public sealed class ExcelBatchTests
{
    private static string Workbook(string name, Action<XLWorkbook> build)
    {
        var path = Path.Combine(TestFolders.Create(), name);
        using var workbook = new XLWorkbook();
        build(workbook);
        workbook.SaveAs(path);
        return path;
    }

    private static string StaffWorkbook() => Workbook("personel.xlsx", workbook =>
    {
        var sheet = workbook.AddWorksheet("Personel");
        string[][] rows =
        [
            ["PERSON_NAME", "Bölüm", "SERIAL", ""],
            ["Ahmet Yılmaz", "Üretim", "001", "yok sayılır"],
            ["", "", "", ""],
            ["Ayşe Öztürk", "Satış ve İhracat", "002", ""],
            ["Şükrü Çağlar", "İnsan Kaynakları", "", ""],
        ];
        for (var row = 0; row < rows.Length; row++)
        {
            for (var column = 0; column < rows[row].Length; column++)
            {
                sheet.Cell(row + 1, column + 1).Value = rows[row][column];
            }
        }

        sheet.Cell(5, 3).Value = 3;                 // a real number…
        sheet.Cell(5, 3).Style.NumberFormat.Format = "000"; // …shown by Excel as 003
        var other = workbook.AddWorksheet("Araçlar");
        other.Cell(1, 1).Value = "Plaka";
        other.Cell(2, 1).Value = "34 ABC 123";
    });

    [Fact]
    public void Excel_rows_become_batch_rows_with_turkish_text_and_displayed_values()
    {
        var reader = new ExcelBatchDataReader();
        var path = StaffWorkbook();

        var rows = reader.Read(path);

        Assert.True(reader.CanRead(path));
        Assert.Equal(3, rows.Count);
        Assert.Equal(("Ahmet Yılmaz", "Üretim", "001"), (rows[0].Values["PERSON_NAME"], rows[0].Values["BOLUM"], rows[0].Values["SERIAL"]));
        Assert.Equal("Satış ve İhracat", rows[1].Values["bolum"]);
        Assert.Equal(("Şükrü Çağlar", "003"), (rows[2].Values["PERSON_NAME"], rows[2].Values["SERIAL"])); // as displayed, leading zeros kept
        Assert.Equal([1, 2, 3], rows.Select(row => row.RowNumber));
        Assert.Equal(3, rows[0].Values.Count); // the unnamed fourth column is ignored
    }

    [Fact]
    public void A_worksheet_can_be_chosen_by_name()
    {
        var reader = new ExcelBatchDataReader();
        var path = StaffWorkbook();

        Assert.Equal(["Personel", "Araçlar"], reader.GetSheetNames(path));
        Assert.Equal("34 ABC 123", Assert.Single(reader.Read(path, "Araçlar")).Values["PLAKA"]);
        var exception = Assert.Throws<FormatException>(() => reader.Read(path, "Yok"));
        Assert.Equal("“Yok” adında bir çalışma sayfası yok. Bulunan sayfalar: Personel, Araçlar", exception.Message);
    }

    [Fact]
    public void Readers_are_chosen_by_extension_and_report_problems_in_turkish()
    {
        Assert.IsType<ExcelBatchDataReader>(BatchDataReaders.For("liste.XLSX"));
        Assert.IsType<CsvBatchDataReader>(BatchDataReaders.For("liste.csv"));
        Assert.Empty(new CsvBatchDataReader().GetSheetNames("liste.csv"));
        Assert.Equal("Bu veri dosyası türü desteklenmiyor: .xls. Desteklenenler: .csv, .xlsx", Assert.Throws<FormatException>(() => BatchDataReaders.For("eski.xls")).Message);

        var broken = Path.Combine(TestFolders.Create(), "bozuk.xlsx");
        File.WriteAllText(broken, "not a workbook");
        Assert.Equal("Excel dosyası okunamadı: bozuk.xlsx", Assert.Throws<FormatException>(() => new ExcelBatchDataReader().Read(broken)).Message);
        Assert.Empty(new ExcelBatchDataReader().Read(Workbook("bos.xlsx", workbook => workbook.AddWorksheet("Boş"))));
        Assert.Throws<FormatException>(() => new ExcelBatchDataReader().Read(Workbook("ayni.xlsx", workbook =>
        {
            var sheet = workbook.AddWorksheet("S");
            sheet.Cell(1, 1).Value = "Ad";
            sheet.Cell(1, 2).Value = "AD";
            sheet.Cell(2, 1).Value = "x";
        })));
    }

    [Fact]
    public void Excel_rows_expand_into_plans_with_duplicate_safe_names()
    {
        var recipe = RecipeBuilder.FromPlan(AutomationTestData.DoorSignPlan(), "Kapı İsimliği", [new RecipeVariableBinding("PERSON_NAME", "Ahmet Yılmaz")]);
        var path = Workbook("isimler.xlsx", workbook =>
        {
            var sheet = workbook.AddWorksheet("İsimler");
            sheet.Cell(1, 1).Value = "PERSON_NAME";
            sheet.Cell(2, 1).Value = "Ayşe Öztürk";
            sheet.Cell(3, 1).Value = "Ayşe Öztürk";
        });
        var job = new BatchJob
        {
            Name = "Toplu", RecipeId = recipe.Id, OutputFolder = @"C:\cikti", OutputNamePattern = "{{PERSON_NAME}}", Formats = [OutputFormat.Cdr, OutputFormat.Pdf, OutputFormat.Png, OutputFormat.Svg],
            Rows = BatchDataReaders.For(path).Read(path),
        };

        var items = BatchExpander.Expand(job, recipe, new OutputNameAllocator(_ => false));

        Assert.Equal(["Ayse_Ozturk", "Ayse_Ozturk_002"], items.Select(item => item.OutputBaseName));
        Assert.All(items, item => Assert.Equal("Ayşe Öztürk", item.Plan!.Actions.OfType<CreateTextAction>().Single().Text));
        Assert.Equal([".cdr", ".pdf", ".png", ".svg"], items[1].OutputFiles.Select(Path.GetExtension));
    }
}

public sealed class RecipeExpressionTests
{
    private static readonly Dictionary<string, string> Values = new() { ["WIDTH_MM"] = "500", ["HEIGHT_MM"] = "700", ["MARGIN"] = "12,5", ["ROW"] = "007", ["NAME"] = "Ayşe" };

    [Theory]
    [InlineData("{{WIDTH_MM / 2}}", "250")]
    [InlineData("{{HEIGHT_MM/2}}", "350")]
    [InlineData("{{ WIDTH_MM - 2 * MARGIN }}", "475")]
    [InlineData("{{(WIDTH_MM + HEIGHT_MM) / 4}}", "300")]
    [InlineData("{{-MARGIN * 2}}", "-25")]
    [InlineData("{{WIDTH_MM / 3}}", "166.6667")]
    [InlineData("{{ROW + 1}}", "8")]
    [InlineData("x={{WIDTH_MM/2}} y={{HEIGHT_MM/2}} {{NAME}}", "x=250 y=350 Ayşe")]
    public void Arithmetic_placeholders_are_evaluated(string text, string expected)
    {
        var errors = new List<string>();

        Assert.Equal(expected, VariableSubstitution.Apply(text, Values, errors));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("{{WIDTH_MM / 0}}", "division by zero")]
    [InlineData("{{WIDTH_MM / NAME}}", "is not a number")]
    [InlineData("{{WIDTH_MM / MISSING}}", "has no value")]
    [InlineData("{{WIDTH_MM +}}", "incomplete")]
    [InlineData("{{(WIDTH_MM}}", "closing parenthesis")]
    [InlineData("{{WIDTH_MM 2}}", "unexpected")]
    [InlineData("{{System.IO.File.Delete(1)}}", "variable 'System' has no value")]
    public void Invalid_or_unsafe_expressions_are_reported_and_left_untouched(string text, string expectedReason)
    {
        var errors = new List<string>();

        var result = VariableSubstitution.Apply(text, Values, errors);

        Assert.Equal(text, result);
        Assert.Contains(expectedReason, Assert.Single(errors));
        Assert.Contains("hesaplanamadı", errors[0]);
    }

    [Fact]
    public void Expressions_can_only_calculate()
    {
        Assert.False(PlaceholderExpression.TryEvaluate("1; 2", _ => null, out _, out _));
        Assert.False(PlaceholderExpression.TryEvaluate("\"a\" + 1", _ => null, out _, out _));
        Assert.False(PlaceholderExpression.TryEvaluate(new string('(', 100) + "1" + new string(')', 100), _ => null, out _, out var error));
        Assert.Contains("nested too deeply", error);
        Assert.True(PlaceholderExpression.TryEvaluate("2 * (3 + 4) - 10 / 4", _ => null, out var value, out _));
        Assert.Equal(11.5, value);
        Assert.Equal(["WIDTH_MM", "MARGIN"], PlaceholderExpression.Variables("WIDTH_MM - 2 * MARGIN"));
    }

    [Fact]
    public void A_recipe_can_centre_objects_from_the_page_size()
    {
        var plan = new AutomationPlan
        {
            Name = "Etiket",
            Target = DocumentTarget.NewDocument,
            Actions =
            [
                new CreateDocumentAction { Id = "doc", WidthMm = 100, HeightMm = 50 },
                new CreateTextAction { Id = "t", Text = "{{NAME}}", XMm = 50, YMm = 25, Anchor = PositionAnchor.Center },
                new CreateRectangleAction { Id = "r", XMm = 5, YMm = 5, WidthMm = 90, HeightMm = 40 },
            ],
        };
        var template = System.Text.Json.Nodes.JsonNode.Parse(plan.ToJson())!.AsObject();
        var actions = template["actions"]!.AsArray();
        actions[0]!["widthMm"] = "{{WIDTH_MM}}";
        actions[0]!["heightMm"] = "{{HEIGHT_MM}}";
        actions[1]!["xMm"] = "{{WIDTH_MM / 2}}";
        actions[1]!["yMm"] = "{{HEIGHT_MM / 2}}";
        actions[2]!["widthMm"] = "{{WIDTH_MM - 10}}";
        actions[2]!["heightMm"] = "{{HEIGHT_MM - 10}}";
        var recipe = new Recipe
        {
            Name = "Etiket", PlanTemplate = template,
            Variables = [new RecipeVariable { Name = "WIDTH_MM", Type = RecipeVariableType.Number }, new RecipeVariable { Name = "HEIGHT_MM", Type = RecipeVariableType.Number }, new RecipeVariable { Name = "NAME" }],
        };

        var filled = recipe.Instantiate(new Dictionary<string, string> { ["WIDTH_MM"] = "500", ["HEIGHT_MM"] = "700", ["NAME"] = "DİKKAT" });

        Assert.True(filled.Validate().IsValid, filled.Validate().ToString());
        var text = (CreateTextAction)filled.Actions[1];
        Assert.Equal((250d, 350d, "DİKKAT"), (text.XMm, text.YMm, text.Text));
        Assert.Equal((490d, 690d), (((CreateRectangleAction)filled.Actions[2]).WidthMm, ((CreateRectangleAction)filled.Actions[2]).HeightMm));

        var broken = Assert.Throws<RecipeVariableException>(() => recipe.Instantiate(new Dictionary<string, string> { ["WIDTH_MM"] = "geniş", ["HEIGHT_MM"] = "700", ["NAME"] = "x" }));
        Assert.Contains(broken.Errors, error => error.Contains("hesaplanamadı"));
    }

    [Fact]
    public void Variables_inside_expressions_are_discovered_and_validated()
    {
        var plan = new AutomationPlan { Name = "p", Actions = [new CreateTextAction { Id = "t", Text = "{{WIDTH_MM / 2}} — {{COMPANY}}" }] };

        Assert.Equal(["WIDTH_MM", "COMPANY"], RecipeBuilder.FromPlan(plan, "r").Variables.Select(variable => variable.Name));
        Assert.Equal(2, plan.Validate().Errors.Count); // unfilled variables still block execution
    }
}

public sealed class PreflightTests
{
    private static ShapeSnapshot Shape(int id, ShapeKind type, string? name, double x, double y, double w, double h, string? text = null, string? font = null) => new()
    {
        Id = LogicalShapeId.FromNativeId(id), NativeId = id, Type = type, Name = name, Text = text, FontFamily = font, Bounds = new BoundsMm(x, y, w, h), LayerName = "Katman 1",
    };

    private static DocumentSnapshot Document(params ShapeSnapshot[] shapes) => new()
    {
        Title = "tabela", Pages = [new PageSnapshot { Index = 1, WidthMm = 500, HeightMm = 700, Shapes = shapes }],
    };

    private static readonly DesignPreflightService Service = new(path => path.Contains("var", StringComparison.Ordinal), folder => !folder.Contains("yasak", StringComparison.Ordinal));

    [Fact]
    public void A_clean_design_passes_with_page_information()
    {
        var result = Service.Check(new PreflightRequest
        {
            Document = Document(Shape(1, ShapeKind.Rectangle, "Çerçeve", 20, 20, 460, 660), Shape(2, ShapeKind.ArtisticText, "Başlık", 100, 500, 300, 60, "YASAKTIR", "Arial")),
            ExpectedSize = new PhysicalSize(500, 700), InstalledFonts = ["Arial"], OutputPaths = [@"C:\cikti\is.cdr", @"C:\cikti\is.pdf"],
        });

        Assert.True(result.IsClean);
        Assert.True(result.CanProduce);
        Assert.Equal("Sayfa: 500 × 700 mm, 2 nesne", Assert.Single(result.Info));
    }

    [Fact]
    public void Blocking_problems_are_errors()
    {
        var result = Service.Check(new PreflightRequest
        {
            Document = Document(Shape(1, ShapeKind.Rectangle, "YER TUTUCU: Firma logosu", 350, 35, 100, 70)),
            ExpectedSize = new PhysicalSize(1000, 1400),
            OutputPaths = [@"C:\yasak\is.cdr", @"C:\cikti\a.pdf", @"C:\CIKTI\A.PDF"],
            ReferencedFiles = [@"C:\logo-yok.svg", @"C:\var\logo.svg"],
        });

        Assert.False(result.CanProduce);
        Assert.Contains("Firma logosu için kaynak dosya eklenmemiş; belgede hâlâ yer tutucu çerçeve var.", result.Errors);
        Assert.Contains("Sayfa ölçüsü 500 × 700 mm; bu iş için 1000 × 1400 mm olmalı.", result.Errors);
        Assert.Contains(@"Gerekli dosya bulunamadı: C:\logo-yok.svg", result.Errors);
        Assert.Contains(@"Çıktı klasörüne yazılamıyor: C:\yasak", result.Errors);
        Assert.Contains(result.Errors, error => error.StartsWith("Aynı çıktı dosyası birden fazla kez yazılacak", StringComparison.Ordinal));
        Assert.Equal(5, result.Errors.Count);

        Assert.Equal("CorelDRAW'da açık bir belge yok; önce belgeyi inceleyin.", Assert.Single(Service.Check(new PreflightRequest()).Errors));
    }

    [Fact]
    public async Task Risks_are_warnings_that_do_not_block()
    {
        var analysis = (await ReferenceFixtures.Analyzer(new FakeVisionClient(ReferenceFixtures.Answer(
            """[{"key":"a","kind":"text","bounds":{"x":0.1,"y":0.1,"width":0.5,"height":0.1},"zIndex":1,"text":"TEL 0532 ???","textConfidence":0.3,"strategy":"text","confidence":0.5}]""")))
            .AnalyzeAsync(new ReferenceAnalysisRequest { Reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng()) })).Analysis!;

        var result = Service.Check(new PreflightRequest
        {
            Document = Document(
                Shape(1, ShapeKind.ArtisticText, "Başlık", 380, 600, 200, 60, "ÇOK UZUN BAŞLIK", "Özel Font"),
                Shape(2, ShapeKind.Rectangle, null, -30, 10, 100, 50),
                Shape(3, ShapeKind.Ellipse, "Nokta", 10, 10, 0, 0),
                Shape(4, ShapeKind.Curve, "Çizgi", 10, 100, 200, 0),
                Shape(5, ShapeKind.ArtisticText, "Boş", 10, 200, 50, 10, " ", "Arial")),
            Analysis = analysis,
            InstalledFonts = ["Arial"],
            ReconstructionWarnings = ["ref_002: yazı tipi yaklaşık eşleştirildi (Arial)."],
            OutputPaths = [@"C:\var\eski.pdf"],
        });

        Assert.True(result.CanProduce);
        Assert.False(result.IsClean);
        Assert.Contains("Başlık (shape_001) metni sayfanın dışına taşıyor.", result.Warnings);
        Assert.Contains("Başlık (shape_001): “Özel Font” yazı tipi bu bilgisayarda yüklü değil.", result.Warnings);
        Assert.Contains("shape_002 sayfanın dışına taşıyor.", result.Warnings);
        Assert.Contains("Nokta (shape_003) nesnesinin boyutu sıfır.", result.Warnings);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("Çizgi")); // a horizontal line legitimately has no height
        Assert.Contains("Boş (shape_005) metni boş.", result.Warnings);
        Assert.Contains("Referanstan okunan metin kesin değil, kontrol edin: “TEL 0532 ???”", result.Warnings);
        Assert.Contains("ref_002: yazı tipi yaklaşık eşleştirildi (Arial).", result.Warnings);
        Assert.Contains(@"Var olan dosyanın üzerine yazılacak: C:\var\eski.pdf", result.Warnings);
    }

    [Fact]
    public void Plan_files_are_collected_for_preflight()
    {
        var plan = new AutomationPlan
        {
            Name = "p",
            Actions =
            [
                new ImportFileAction { Id = "i", FilePath = @"C:\in\logo.svg" },
                new SaveDocumentAction { Id = "s", FilePath = @"C:\out\a.cdr" },
                new ExportPdfAction { Id = "e", FilePath = @"C:\out\a.pdf" },
            ],
        };

        var (outputs, inputs) = DesignPreflightService.FilesOf(plan);

        Assert.Equal([@"C:\out\a.cdr", @"C:\out\a.pdf"], outputs);
        Assert.Equal([@"C:\in\logo.svg"], inputs);
    }
}

public sealed class AssetFontAndCacheTests
{
    private static Asset Asset(string id, string name, string type = "svg", string category = "Genel", params string[] tags) =>
        new() { Id = id, Name = name, Category = category, FilePath = $@"C:\varlik\{id}.{type}", FileType = type, Tags = tags };

    [Fact]
    public void A_semantic_hint_finds_the_real_asset()
    {
        Asset[] library =
        [
            Asset("acme", "ACME Logo"),
            Asset("beta", "Beta Yapı Logosu"),
            Asset("acme-png", "ACME Logo", "png"),
            Asset("sigara", "Sigara içilmez", category: "Yasak İşaretleri", tags: ["sigara", "yasak"]),
        ];

        Assert.Equal("acme", AssetMatcher.FindBest(library, "firma logosu acme")!.Asset.Id);             // the vector wins over the PNG
        Assert.Equal("beta", AssetMatcher.FindBest(library, "BETA YAPI", "Firma logosu")!.Asset.Id);
        Assert.Equal("sigara", AssetMatcher.FindBest(library, "sigara yasak işareti")!.Asset.Id);        // by tags, Turkish folded
        Assert.Null(AssetMatcher.FindBest(library, "firma logosu"));                                       // generic words alone select nothing
        Assert.Null(AssetMatcher.FindBest(library, "gamma holding logosu"));
        Assert.Null(AssetMatcher.FindBest(library, null, ""));
        Assert.Equal(["sirket", "logosu", "acme", "isik"], AssetMatcher.Tokens("Şirket Logosu (ACME) — IŞIK"));
    }

    [Fact]
    public async Task A_matching_library_asset_replaces_the_logo_placeholder()
    {
        var reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng());
        var analysis = await ReferenceFixtures.Analyze(
            """[{"key":"l","kind":"logo","label":"Firma logosu","bounds":{"x":0.7,"y":0.05,"width":0.2,"height":0.1},"zIndex":1,"strategy":"needsUserAsset","assetHint":"firma logosu acme","confidence":0.8}]""");
        var planner = new ReferenceReconstructionPlanner(new InstalledFontResolver(["Arial"]));

        var withAsset = planner.Plan(new ReconstructionRequest { Reference = reference, Analysis = analysis, UserRequest = "500x700 mm", Assets = [Asset("acme", "ACME Logo")] });
        var without = planner.Plan(new ReconstructionRequest { Reference = reference, Analysis = analysis, UserRequest = "500x700 mm" });

        var import = withAsset.Plan!.Actions.OfType<ImportFileAction>().Single();
        Assert.Equal((@"C:\varlik\acme.svg", 350d, 100d), (import.FilePath, import.XMm, import.FitWidthMm));
        Assert.DoesNotContain(withAsset.Plan.Actions, action => action is CreateRectangleAction);
        Assert.Contains(withAsset.Warnings, warning => warning.Contains("“ACME Logo” kullanılacak"));
        Assert.Equal("YER TUTUCU: Firma logosu", without.Plan!.Actions.OfType<CreateRectangleAction>().Single().Name);
    }

    [Fact]
    public void Fonts_without_the_needed_letters_are_replaced_and_never_called_exact()
    {
        // "Impact" is installed but (in this test) has neither Turkish nor Arabic letters; "Tahoma" has both.
        static bool Supports(string font, string text) => font != "Impact" || text.All(character => character < 128);
        var resolver = new InstalledFontResolver(["Impact", "Tahoma", "Arial"], supportsText: (font, text) => font switch
        {
            "Impact" => Supports(font, text),
            "Arial" => !InstalledFontResolver.ContainsArabic(text),
            _ => true,
        });

        Assert.Equal(new FontResolution("Impact", FontMatchKind.Exact), resolver.Resolve("Impact", 0.95, "EXIT"));
        Assert.Equal(new FontResolution("Arial", FontMatchKind.Fallback, MissingGlyphs: true), resolver.Resolve("Impact", 0.95, "GİRİŞ ÇIKIŞ"));
        Assert.Equal(new FontResolution("Tahoma", FontMatchKind.Fallback, MissingGlyphs: true), resolver.Resolve("Impact", 0.95, "ممنوع الدخول"));
        Assert.Equal(new FontResolution("Tahoma", FontMatchKind.Fallback, MissingGlyphs: true), resolver.Resolve(null, null, "ممنوع الدخول")); // default Arial cannot draw it here
        Assert.Equal(new FontResolution("Arial", FontMatchKind.Fallback), resolver.Resolve(null, null, "GİRİŞ"));
        Assert.True(InstalledFontResolver.ContainsArabic("مرحبا"));
        Assert.False(InstalledFontResolver.ContainsArabic("Merhaba ĞÜŞİÖÇ"));
    }

    [Fact]
    public async Task Glyph_fallback_is_explained_in_the_plan_warnings()
    {
        var reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng());
        var analysis = await ReferenceFixtures.Analyze(
            """[{"key":"a","kind":"text","bounds":{"x":0.1,"y":0.1,"width":0.8,"height":0.1},"zIndex":1,"text":"GİRİŞ","textConfidence":0.9,"fontFamilyGuess":"Impact","fontConfidence":0.9,"strategy":"text","confidence":0.9}]""");
        var planner = new ReferenceReconstructionPlanner(new InstalledFontResolver(["Impact", "Arial"], supportsText: (font, _) => font != "Impact"));

        var result = planner.Plan(new ReconstructionRequest { Reference = reference, Analysis = analysis, UserRequest = "500x700 mm" });

        Assert.Equal("Arial", result.Plan!.Actions.OfType<CreateTextAction>().Single().FontFamily);
        Assert.Contains("ref_001: seçilen yazı tipi bu metindeki harfleri içermiyor; Arial kullanıldı.", result.Warnings);
    }

    [Fact]
    public async Task The_same_unchanged_reference_is_sent_to_the_provider_only_once()
    {
        var client = new FakeVisionClient(
            ReferenceFixtures.Answer(ReferenceFixtures.SignElements), ReferenceFixtures.Answer(ReferenceFixtures.SignElements), ReferenceFixtures.Answer(ReferenceFixtures.SignElements));
        var cache = new CachingReferenceVisionAnalyzer(ReferenceFixtures.Analyzer(client));
        var path = ReferenceFixtures.ProhibitionSignPng();
        ReferenceAnalysisRequest Request(string text, bool bypass = false) => new() { Reference = ReferenceInput.FromFile(path), UserRequest = text, BypassCache = bypass };

        var first = await cache.AnalyzeAsync(Request("Bunun aynısını yap"));
        var sized = await cache.AnalyzeAsync(Request("Bunun aynısını 500x700 mm yap"));   // only a size was added: same content
        Assert.Single(client.Requests);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(first.Analysis!.Elements.Count, sized.Analysis!.Elements.Count);
        Assert.NotEqual(first.Analysis.ReferenceId, sized.Analysis.ReferenceId);          // the caller's own reference id is kept

        await cache.AnalyzeAsync(Request("Bunun aynısını yap ama YASAKTIR yerine GİRİLMEZ yaz")); // different content: asked again
        Assert.Equal(2, client.Requests.Count);

        await cache.AnalyzeAsync(Request("Bunun aynısını yap", bypass: true));                    // "Analizi Yenile"
        Assert.Equal(3, client.Requests.Count);

        cache.Clear();
        var failing = new CachingReferenceVisionAnalyzer(ReferenceFixtures.Analyzer(new FakeVisionClient("çöp", "çöp", "çöp", "çöp")));
        await failing.AnalyzeAsync(Request("x"));
        await failing.AnalyzeAsync(Request("x"));
        Assert.Equal(0, failing.Hits); // failures are never cached
    }
}

public sealed class DocumentationTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    [Theory]
    [InlineData("README.md")]
    [InlineData("README.tr.md")]
    public void Readme_does_not_contradict_the_current_capabilities(string file)
    {
        var text = File.ReadAllText(Path.Combine(Root, file));

        string[] stale =
        [
            "No external AI service is connected yet",
            "only imported as a backdrop",
            "needs the future AI analyzer",
            "Excel is a future reader",
            "görsel içeriği göremez",
            "yalnızca görsel olarak içe aktarılır",
            "henüz yoktur",
            "Excel daha sonra eklenecektir",
            "No automatic visual comparison yet",
            "cannot yet express formulas",
            "formül içeremez",
        ];
        Assert.All(stale, phrase => Assert.DoesNotContain(phrase, text, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("1.0", text);
    }
}
