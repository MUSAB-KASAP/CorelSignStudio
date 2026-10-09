using CorelSignStudio.AI;
using CorelSignStudio.App;
using CorelSignStudio.Corel;
using CorelSignStudio.Domain.Ai;
using CorelSignStudio.Domain.Ai.Vision;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.Planning;
using CorelSignStudio.Domain.Production;
using CorelSignStudio.Domain.Recipes;
using CorelSignStudio.Domain.References;
using CorelSignStudio.Imaging;
using SkiaSharp;
using Xunit.Abstractions;

namespace CorelSignStudio.Tests;

/// <summary>What the opt-in release tests need, and why one is not running. A test that cannot run is reported as skipped, never as passed.</summary>
internal static class AcceptanceEnvironment
{
    public const string NoCorel = "CorelDRAW tests are opt-in: set COREL_INTEGRATION=1 on a machine with CorelDRAW 2026.";
    public const string NoCredential = "SKIPPED — no configured AI credential (configure one under Ayarlar > Yapay Zekâ).";

    public static bool CorelEnabled => string.Equals(Environment.GetEnvironmentVariable("COREL_INTEGRATION"), "1", StringComparison.Ordinal);

    /// <summary>True when the application itself would find a usable key (encrypted store or the provider's environment variable).</summary>
    public static bool AiConfigured
    {
        get
        {
            try
            {
                var store = new AiSettingsStore(AiSettingsStore.DefaultFilePath, new DpapiSecretProtector());
                return store.IsConfigured(store.Load());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return false;
            }
        }
    }

    public static string OutputFolder(string name)
    {
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "acceptance", "work", name));
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }

        return Directory.CreateDirectory(folder).FullName;
    }
}

/// <summary>A test that drives the installed CorelDRAW. Skipped — visibly — unless COREL_INTEGRATION=1.</summary>
public sealed class CorelFactAttribute : FactAttribute
{
    public CorelFactAttribute()
    {
        if (!AcceptanceEnvironment.CorelEnabled)
        {
            Skip = AcceptanceEnvironment.NoCorel;
        }
    }
}

/// <summary>A test that makes a real request to the configured AI provider. Skipped — visibly — without a credential.</summary>
public sealed class RealAiFactAttribute : FactAttribute
{
    public RealAiFactAttribute(bool needsCorel = false)
    {
        if (!AcceptanceEnvironment.AiConfigured)
        {
            Skip = AcceptanceEnvironment.NoCredential;
        }
        else if (needsCorel && !AcceptanceEnvironment.CorelEnabled)
        {
            Skip = AcceptanceEnvironment.NoCorel;
        }
    }
}

/// <summary>
/// Release acceptance against the real, configured AI provider: connection, natural-language planning,
/// clarification, reference vision, visual comparison and the improvement loop. Only synthetic artwork is
/// sent. Nothing here runs — and nothing here counts as passed — without a credential the application
/// itself can read; the key is never printed.
/// </summary>
[Collection("CorelDRAW")]
public sealed class RealAiAcceptanceTests(CorelOperatorFixture fixture, ITestOutputHelper output) : IClassFixture<CorelOperatorFixture>
{
    private readonly List<string> _log = [];

    private (AiRuntime Runtime, AiSettingsStore Store) Ai()
    {
        var store = new AiSettingsStore(AiSettingsStore.DefaultFilePath, new DpapiSecretProtector());
        return (new AiRuntime(store, _log.Add), store);
    }

    private void AssertNoSecretInLogs(AiSettingsStore store)
    {
        var key = store.GetApiKey(store.Load().Provider)!;
        Assert.DoesNotContain(_log, line => line.Contains(key, StringComparison.Ordinal));
        Assert.DoesNotContain(_log, line => line.Contains("base64", StringComparison.OrdinalIgnoreCase) && line.Length > 2000);
    }

    private async Task<PlanExecutionResult> Run(CorelActionExecutor executor, AutomationPlan plan)
    {
        var result = await executor.ExecuteAsync(plan);
        Assert.True(result.Success, $"{plan.Name}: {result.Summary}\n{result.ErrorDetails}");
        return result;
    }

    private static async Task Close(CorelDocumentInspector inspector, CorelActionExecutor executor, bool userHadDocumentOpen)
    {
        for (var attempt = 0; !userHadDocumentOpen && attempt < 10 && await inspector.InspectActiveDocumentAsync() is not null; attempt++)
        {
            await executor.ExecuteAsync(new AutomationPlan { Name = "Cleanup", Actions = [new CloseDocumentAction { Id = "close" }] });
        }
    }

    [RealAiFact]
    public async Task The_configured_provider_answers_and_the_key_is_not_logged()
    {
        var (runtime, store) = Ai();

        var response = await runtime.TestConnectionAsync();

        output.WriteLine($"Provider {runtime.Settings.Provider}, model {response.Model ?? runtime.Settings.Model}");
        Assert.False(string.IsNullOrWhiteSpace(response.Content));
        AssertNoSecretInLogs(store);
    }

    [RealAiFact(needsCorel: true)]
    public async Task A_turkish_instruction_becomes_a_valid_plan_that_edits_only_what_was_asked()
    {
        var (runtime, store) = Ai();
        var inspector = new CorelDocumentInspector(fixture.Service!);
        var executor = new CorelActionExecutor(fixture.Service!);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;
        try
        {
            await Run(executor, new AutomationPlan
            {
                Name = "Planner document",
                Target = DocumentTarget.NewDocument,
                Actions =
                [
                    new CreateDocumentAction { Id = "doc", WidthMm = 300, HeightMm = 200 },
                    new CreateRectangleAction { Id = "frame", Name = "Çerçeve", XMm = 5, YMm = 5, WidthMm = 290, HeightMm = 190, OutlineWidthMm = 1 },
                    new CreateRectangleAction { Id = "logo", Name = "Logo", XMm = 20, YMm = 130, WidthMm = 60, HeightMm = 40, FillColor = "#1F5FBF" },
                    new CreateEllipseAction { Id = "dot", Name = "Kırmızı Daire", XMm = 30, YMm = 30, WidthMm = 30, HeightMm = 30, FillColor = "#D8202A" },
                    new CreateTextAction { Id = "title", Name = "Başlık", Text = "DİKKAT", XMm = 20, YMm = 80, FontFamily = "Arial", FontSizePt = 48 },
                ],
            });
            var before = (await inspector.InspectActiveDocumentAsync())!;
            var logo = before.FindShapesByName("Logo").Single();
            var title = before.FindShapesByName("Başlık").Single();
            var frame = before.FindShapesByName("Çerçeve").Single();
            var dot = before.FindShapesByName("Kırmızı Daire").Single();

            var result = await runtime.Planner!.PlanWithAiAsync(new PlanningRequest
            {
                UserRequest = "Logoyu yüzde 15 küçült, sağ üst tarafa taşı ve başlığı sayfanın ortasına hizala.",
                Document = before,
            });

            output.WriteLine($"{result.Status}: {result.UserMessage} {result.Explanation}");
            Assert.Equal(AiPlanningStatus.Ready, result.Status);
            var plan = result.Plan!;
            foreach (var action in plan.Actions)
            {
                output.WriteLine("  " + ActionDescriber.Describe(action)); // what the user reviews before anything runs
            }

            Assert.True(plan.Validate().IsValid, string.Join("; ", plan.Validate().Errors));
            Assert.False(plan.HasDestructiveActions);
            var known = before.AllShapes().Select(shape => shape.Id).ToHashSet();
            Assert.All(plan.Actions.OfType<TargetedAction>().SelectMany(action => action.Targets).Where(target => target.StartsWith("shape_", StringComparison.Ordinal)),
                target => Assert.Contains(target, known)); // no invented ids

            await Run(executor, plan);
            var after = (await inspector.InspectActiveDocumentAsync())!;
            var logoAfter = after.FindShape(logo.Id)!;
            Assert.Equal(logo.Bounds.WidthMm * 0.85, logoAfter.Bounds.WidthMm, 0);
            Assert.Equal(logo.Bounds.HeightMm * 0.85, logoAfter.Bounds.HeightMm, 0);
            Assert.True(logoAfter.Bounds.CenterXMm > 150 && logoAfter.Bounds.CenterYMm < 100, $"logo is at {logoAfter.Bounds}"); // top-right quadrant
            Assert.Equal(150, after.FindShape(title.Id)!.Bounds.CenterXMm, 0);
            Assert.Equal(frame.Bounds, after.FindShape(frame.Id)!.Bounds);
            Assert.Equal(dot.Bounds, after.FindShape(dot.Id)!.Bounds);
            AssertNoSecretInLogs(store);
        }
        finally
        {
            await Close(inspector, executor, userHadDocumentOpen);
        }
    }

    [RealAiFact(needsCorel: true)]
    public async Task An_ambiguous_instruction_is_asked_about_and_the_answer_resolves_it()
    {
        var (runtime, _) = Ai();
        var inspector = new CorelDocumentInspector(fixture.Service!);
        var executor = new CorelActionExecutor(fixture.Service!);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;
        try
        {
            await Run(executor, new AutomationPlan
            {
                Name = "Two logos",
                Target = DocumentTarget.NewDocument,
                Actions =
                [
                    new CreateDocumentAction { Id = "doc", WidthMm = 300, HeightMm = 200 },
                    new CreateRectangleAction { Id = "a", Name = "Firma Logosu", XMm = 15, YMm = 15, WidthMm = 60, HeightMm = 40, FillColor = "#1F5FBF" },
                    new CreateRectangleAction { Id = "b", Name = "Marka Logosu", XMm = 225, YMm = 145, WidthMm = 60, HeightMm = 40, FillColor = "#2E9E4F" },
                ],
            });
            var before = (await inspector.InspectActiveDocumentAsync())!;
            var topLeft = before.FindShapesByName("Firma Logosu").Single();
            var bottomRight = before.FindShapesByName("Marka Logosu").Single();
            const string Request = "Logoyu yüzde 20 küçült.";

            var first = await runtime.Planner!.PlanWithAiAsync(new PlanningRequest { UserRequest = Request, Document = before });
            output.WriteLine($"{first.Status}: {first.ClarificationQuestion}");
            Assert.Equal(AiPlanningStatus.NeedsClarification, first.Status); // it must not pick one at random
            Assert.False(string.IsNullOrWhiteSpace(first.ClarificationQuestion));
            Assert.Null(first.Plan);

            var second = await runtime.Planner.PlanWithAiAsync(new PlanningRequest
            {
                UserRequest = "Sol üstteki.",
                Document = before,
                Conversation = [new PlanningTurn(Request, first.ClarificationQuestion)],
            });
            output.WriteLine($"{second.Status}: {second.UserMessage}");
            Assert.Equal(AiPlanningStatus.Ready, second.Status);
            Assert.All(second.Plan!.Actions.OfType<TargetedAction>().SelectMany(action => action.Targets), target => Assert.DoesNotContain(bottomRight.Id, target));

            await Run(executor, second.Plan);
            var after = (await inspector.InspectActiveDocumentAsync())!;
            Assert.Equal(topLeft.Bounds.WidthMm * 0.8, after.FindShape(topLeft.Id)!.Bounds.WidthMm, 0);
            Assert.Equal(bottomRight.Bounds, after.FindShape(bottomRight.Id)!.Bounds);
        }
        finally
        {
            await Close(inspector, executor, userHadDocumentOpen);
        }
    }

    /// <summary>A clean synthetic sign: white ground, black border, red prohibition ring with a simple pictogram, three lines of text.</summary>
    internal static string SyntheticSign(string folder)
    {
        const int Width = 1000, Height = 1400;
        using var bitmap = new SKBitmap(Width, Height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var border = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 16, IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(40, 40, Width - 40, Height - 40), 30, 30, border);
            using var black = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            canvas.DrawCircle(500, 330, 55, black);                          // pictogram: a head …
            canvas.DrawRoundRect(new SKRect(440, 400, 560, 610), 20, 20, black); // … and a body
            using var red = new SKPaint { Color = new SKColor(0xD8, 0x20, 0x2A), Style = SKPaintStyle.Stroke, StrokeWidth = 60, IsAntialias = true };
            canvas.DrawCircle(500, 450, 290, red);
            canvas.DrawLine(295, 245, 705, 655, red);
            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
            using var font = new SKFont(typeface, 120);
            using var text = new SKPaint { Color = new SKColor(0xD8, 0x20, 0x2A), IsAntialias = true };
            var y = 930f;
            foreach (var line in new[] { "BU ALANA", "GİRMEK", "YASAKTIR" })
            {
                canvas.DrawText(line, Width / 2f, y, SKTextAlign.Center, font, text);
                y += 150;
            }
        }

        var path = Path.Combine(folder, "sentetik-levha.png");
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [RealAiFact(needsCorel: true)]
    public async Task A_synthetic_sign_is_analysed_rebuilt_compared_and_improved_with_the_real_model()
    {
        var (runtime, store) = Ai();
        var folder = AcceptanceEnvironment.OutputFolder("real-ai-vision");
        var service = fixture.Service!;
        var inspector = new CorelDocumentInspector(service);
        var executor = new CorelActionExecutor(service);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;
        var summary = new List<string>();
        void Note(string line)
        {
            summary.Add(line);
            output.WriteLine(line);
        }

        try
        {
            // ---- Vision: a real multimodal request.
            var reference = ReferenceInput.FromFile(SyntheticSign(folder));
            const string Request = "Bunun aynısını 500x700 mm olarak CorelDRAW'da yeniden oluştur.";
            var previews = CompositeReferencePreviewRenderer.CreateDefault();
            var analysed = await new AiReferenceAnalyzer(() => runtime.Client, previews).AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference, UserRequest = Request });
            Note($"Vision: {analysed.Status} — {analysed.UserMessage}");
            Assert.True(analysed.IsReady, analysed.UserMessage);
            var analysis = analysed.Analysis!;
            var texts = analysis.Elements.Where(element => element.Kind == ReferenceElementKind.Text).ToArray();
            foreach (var element in texts)
            {
                Note($"  text '{element.Text?.ReplaceLineEndings(" / ")}'{(element.TextUncertain ? " (uncertain)" : "")}");
            }

            var read = string.Join(" ", texts.Select(element => element.Text?.ReplaceLineEndings(" "))).ToUpper(Msg.Culture);
            foreach (var expected in new[] { "BU ALANA", "GİRMEK", "YASAKTIR" })
            {
                Assert.True(read.Contains(expected, StringComparison.Ordinal) || texts.Any(element => element.TextUncertain), $"'{expected}' was neither read nor flagged as uncertain; read: {read}");
            }

            // ---- Reconstruction: native, editable objects at exactly 500 x 700 mm.
            var fonts = System.Windows.Media.Fonts.SystemFontFamilies.Select(family => family.Source).ToArray();
            var reconstruction = new ReferenceReconstructionPlanner(new InstalledFontResolver(fonts, supportsText: FontCoverage.Supports))
                .Plan(new ReconstructionRequest { Reference = reference, Analysis = analysis, UserRequest = Request });
            Assert.True(reconstruction.IsReady, reconstruction.UserMessage);
            Assert.Equal(new PhysicalSize(500, 700), reconstruction.Size);
            Assert.True(reconstruction.Plan!.Validate().IsValid);
            await Run(executor, reconstruction.Plan);

            var built = (await inspector.InspectActiveDocumentAsync())!;
            Assert.Equal((500d, 700d), (Math.Round(built.ActivePage!.WidthMm, 1), Math.Round(built.ActivePage.HeightMm, 1)));
            var leaves = built.AllShapes().Where(shape => shape.Type != ShapeKind.Group).ToArray();
            Note($"Rebuilt: {leaves.Length} objects — {string.Join(", ", leaves.GroupBy(shape => shape.Type).Select(group => $"{group.Count()} {group.Key}"))}");
            Assert.DoesNotContain(leaves, shape => shape.Type == ShapeKind.Bitmap); // not flattened into a picture
            Assert.Contains(leaves, shape => shape.Type == ShapeKind.Ellipse);
            Assert.True(leaves.Count(shape => shape.IsText) >= 3, "the three lines are not editable text");

            var files = await Run(executor, new AutomationPlan
            {
                Name = "Outputs",
                Actions =
                [
                    new SaveDocumentAction { Id = "cdr", FilePath = Path.Combine(folder, "levha.cdr") },
                    new ExportPdfAction { Id = "pdf", FilePath = Path.Combine(folder, "levha.pdf") },
                    new ExportPngAction { Id = "png", FilePath = Path.Combine(folder, "levha.png"), Dpi = 72 },
                ],
            });
            Assert.Equal(3, files.ProducedFiles.Count);

            // ---- Damage it, then compare with the real model looking at both pictures.
            var expectedObjects = ExpectedObjects.FromPlan(reconstruction.Plan);
            var heading = leaves.Where(shape => shape.IsText).OrderBy(shape => shape.Bounds.CenterYMm).Last();
            var ring = leaves.Where(shape => shape.Type == ShapeKind.Ellipse).OrderByDescending(shape => shape.Bounds.WidthMm).First();
            await Run(executor, new AutomationPlan
            {
                Name = "Damage",
                Actions =
                [
                    new MoveAction { Id = "low", Targets = [heading.Id], DeltaYMm = 15 },
                    new ResizeAction { Id = "big", Targets = [ring.Id], ScalePercent = 110 },
                    new SetFillAction { Id = "colour", Targets = [heading.Id], Color = "#1F5FBF" },
                ],
            });

            var pagePreview = new CorelPagePreviewRenderer(service);
            var referenceImage = await previews.RenderAsync(reference, new ReferencePreviewOptions());
            var comparer = new VisualComparisonService(visual: new AiVisualComparer(() => runtime.Client));
            var damaged = await comparer.CompareAsync(new VisualComparisonRequest
            {
                Expected = expectedObjects, Document = (await inspector.InspectActiveDocumentAsync())!, Size = new PhysicalSize(500, 700),
                ReferenceImage = referenceImage, OutputImage = await pagePreview.RenderActivePageAsync(),
            });
            Note($"Comparison: similarity {damaged.Similarity:0.000}, AI used: {damaged.AiUsed}");
            foreach (var difference in damaged.Differences)
            {
                Note($"  [{difference.Kind}] {difference.Description}");
            }

            Assert.True(damaged.AiUsed, "the visual comparison did not reach the model");
            Assert.Contains(damaged.Differences, difference => difference.Kind == DifferenceKind.Position);
            Assert.Contains(damaged.Differences, difference => difference.Kind == DifferenceKind.Size);
            Assert.All(damaged.Differences.Where(difference => difference.Kind == DifferenceKind.Visual), difference => Assert.Null(difference.ShapeId)); // advisory only

            // ---- Bounded improvement with typed actions only.
            var loop = new VisualImprovementLoop(inspector, executor, comparer, new VisualCorrectionPlanner(), pagePreview);
            var improvement = await loop.RunAsync(new ImprovementRequest { Expected = expectedObjects, Size = new PhysicalSize(500, 700), ReferenceImage = referenceImage });
            Note($"Improvement: {improvement.InitialSimilarity:0.000} -> {improvement.FinalSimilarity:0.000}, {improvement.Passes.Count} pass(es), stop: {improvement.StopReason}");
            foreach (var pass in improvement.Passes)
            {
                Note($"  pass {pass.Number}: {string.Join(" | ", pass.Corrections)}");
            }

            Assert.InRange(improvement.Passes.Count, 1, 3);
            Assert.True(improvement.FinalSimilarity > improvement.InitialSimilarity);
            var repaired = (await inspector.InspectActiveDocumentAsync())!;
            Assert.Equal(leaves.Length, repaired.AllShapes().Count(shape => shape.Type != ShapeKind.Group)); // nothing was deleted
            Assert.Equal(heading.Bounds.CenterYMm, repaired.FindShape(heading.Id)!.Bounds.CenterYMm, 0);
            Assert.Equal(ring.Bounds.WidthMm, repaired.FindShape(ring.Id)!.Bounds.WidthMm, 0);
            AssertNoSecretInLogs(store);
        }
        finally
        {
            await File.WriteAllLinesAsync(Path.Combine(folder, "real-ai-summary.txt"), summary);
            await Close(inspector, executor, userHadDocumentOpen);
        }
    }
}

/// <summary>Release acceptance on the real CorelDRAW that needs no AI: production check cases, Arabic round trip, Excel and CSV batches.</summary>
[Collection("CorelDRAW")]
public sealed class CorelReleaseAcceptanceTests(CorelOperatorFixture fixture, ITestOutputHelper output) : IClassFixture<CorelOperatorFixture>
{
    private async Task<PlanExecutionResult> Run(CorelActionExecutor executor, AutomationPlan plan)
    {
        var result = await executor.ExecuteAsync(plan);
        Assert.True(result.Success, $"{plan.Name}: {result.Summary}\n{result.ErrorDetails}");
        return result;
    }

    private static async Task Close(CorelDocumentInspector inspector, CorelActionExecutor executor, bool userHadDocumentOpen)
    {
        for (var attempt = 0; !userHadDocumentOpen && attempt < 10 && await inspector.InspectActiveDocumentAsync() is not null; attempt++)
        {
            await executor.ExecuteAsync(new AutomationPlan { Name = "Cleanup", Actions = [new CloseDocumentAction { Id = "close" }] });
        }
    }

    private static string Picture(string folder, string name, int pixels)
    {
        using var bitmap = new SKBitmap(pixels, pixels);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SeaGreen);
        }

        var path = Path.Combine(folder, name);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [CorelFact]
    public async Task Production_check_cases_on_a_real_document()
    {
        var folder = AcceptanceEnvironment.OutputFolder("preflight");
        var inspector = new CorelDocumentInspector(fixture.Service!);
        var executor = new CorelActionExecutor(fixture.Service!);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;
        var preflight = new DesignPreflightService();
        var fonts = System.Windows.Media.Fonts.SystemFontFamilies.Select(family => family.Source).ToArray();
        void Dump(string title, PreflightResult result)
        {
            output.WriteLine($"---- {title}: {result.Errors.Count} error(s), {result.Warnings.Count} warning(s)");
            foreach (var line in result.Errors.Select(line => "E " + line).Concat(result.Warnings.Select(line => "W " + line)).Concat(result.Info.Select(line => "I " + line)))
            {
                output.WriteLine("  " + line);
            }
        }

        try
        {
            // A — a clean structured sign.
            await Run(executor, new AutomationPlan
            {
                Name = "Clean sign",
                Target = DocumentTarget.NewDocument,
                Actions =
                [
                    new CreateDocumentAction { Id = "doc", WidthMm = 600, HeightMm = 600 },
                    new CreateRectangleAction { Id = "frame", Name = "Çerçeve", XMm = 10, YMm = 10, WidthMm = 580, HeightMm = 580, OutlineWidthMm = 2 },
                    new CreateTextAction { Id = "text", Name = "Başlık", Text = "ÇIKIŞ", XMm = 40, YMm = 500, FontFamily = "Arial", FontSizePt = 90 },
                ],
            });
            var clean = preflight.Check(new PreflightRequest { Document = await inspector.InspectActiveDocumentAsync(), ExpectedSize = new PhysicalSize(600, 600), InstalledFonts = fonts });
            Dump("A clean", clean);
            Assert.True(clean.IsClean);

            // B — 500 px across 500 mm; C — 4000 px across 500 mm; D — a placeholder; F — an object outside the page.
            await Run(executor, new AutomationPlan
            {
                Name = "Problems",
                Actions =
                [
                    new ImportFileAction { Id = "low", FilePath = Picture(folder, "kucuk.png", 500), Name = "Küçük görsel", XMm = 20, YMm = 20, FitWidthMm = 500, FitHeightMm = 500 },
                    new ImportFileAction { Id = "high", FilePath = Picture(folder, "buyuk.png", 4000), Name = "Büyük görsel", XMm = 30, YMm = 30, FitWidthMm = 500, FitHeightMm = 500 },
                    new CreateRectangleAction { Id = "placeholder", Name = Msg.Format("Reconstruct.Placeholder", "Firma logosu"), XMm = 400, YMm = 400, WidthMm = 80, HeightMm = 50 },
                    new CreateRectangleAction { Id = "outside", Name = "Taşan kutu", XMm = 560, YMm = 300, WidthMm = 120, HeightMm = 40 },
                ],
            });
            var document = (await inspector.InspectActiveDocumentAsync())!;
            var missing = Path.Combine(folder, "olmayan-logo.svg");
            var result = preflight.Check(new PreflightRequest { Document = document, ExpectedSize = new PhysicalSize(600, 600), InstalledFonts = fonts, ReferencedFiles = [missing] }); // E — a missing file
            Dump("B-F problems", result);

            var low = document.FindShapesByName("Küçük görsel").Single();
            Assert.Equal(25.4, low.EffectiveDpi!.Value.X, 0);
            Assert.Contains(result.Warnings, warning => warning.Contains("Küçük görsel") && warning.Contains("çok düşük") && warning.Contains("25 DPI"));
            Assert.DoesNotContain(result.Warnings, warning => warning.Contains("Büyük görsel"));
            Assert.Contains(result.Info, info => info.Contains("Büyük görsel") && info.Contains("203 DPI"));
            Assert.Contains(result.Errors, error => error.Contains("Firma logosu"));
            Assert.Contains(result.Errors, error => error.Contains("olmayan-logo.svg"));
            Assert.Contains(result.Warnings, warning => warning.Contains("Taşan kutu"));
            Assert.False(result.CanProduce);
        }
        finally
        {
            await Close(inspector, executor, userHadDocumentOpen);
        }
    }

    [CorelFact]
    public async Task Arabic_text_survives_save_close_and_reopen()
    {
        const string Arabic = "ممنوع الدخول";
        var folder = AcceptanceEnvironment.OutputFolder("arabic");
        var inspector = new CorelDocumentInspector(fixture.Service!);
        var executor = new CorelActionExecutor(fixture.Service!);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;
        var fonts = System.Windows.Media.Fonts.SystemFontFamilies.Select(family => family.Source).ToArray();
        var font = new InstalledFontResolver(fonts, supportsText: FontCoverage.Supports).Resolve("Impact", 0.9, Arabic);
        var cdr = Path.Combine(folder, "arapca.cdr");
        try
        {
            await Run(executor, new AutomationPlan
            {
                Name = "Arabic",
                Target = DocumentTarget.NewDocument,
                Actions =
                [
                    new CreateDocumentAction { Id = "doc", WidthMm = 300, HeightMm = 100 },
                    new CreateTextAction { Id = "text", Name = "Arapça", Text = Arabic, XMm = 20, YMm = 60, FontFamily = font.FontFamily, FontSizePt = 60 },
                    new SaveDocumentAction { Id = "cdr", FilePath = cdr },
                    new CloseDocumentAction { Id = "close" },
                ],
            });
            await Run(executor, new AutomationPlan { Name = "Reopen", Target = DocumentTarget.NewDocument, Actions = [new OpenDocumentAction { Id = "open", FilePath = cdr }] });

            var reopened = (await inspector.InspectActiveDocumentAsync())!;
            var text = reopened.FindShapesByName("Arapça").Single();
            output.WriteLine($"'{text.Text}' in {text.FontFamily} after reopening");
            Assert.Equal(Arabic, text.Text);
            Assert.Equal(font.FontFamily, text.FontFamily);
            Assert.True(FontCoverage.Supports(text.FontFamily!, Arabic));
            await Run(executor, new AutomationPlan { Name = "Close", Actions = [new CloseDocumentAction { Id = "close" }] });
        }
        finally
        {
            await Close(inspector, executor, userHadDocumentOpen);
        }
    }

    [CorelFact]
    public async Task Excel_and_csv_batches_run_from_the_operator_flow()
    {
        var folder = AcceptanceEnvironment.OutputFolder("batch");
        var service = fixture.Service!;
        var inspector = new CorelDocumentInspector(service);
        var executor = new CorelActionExecutor(service);
        var userHadDocumentOpen = await inspector.InspectActiveDocumentAsync() is not null;
        var harness = new OperatorHarness(inspector: inspector, executor: executor, connect: async () => (await service.ConnectPreservingVisibilityAsync()).Version);
        var viewModel = harness.ViewModel;
        viewModel.OutputFolder = folder;

        var plan = new AutomationPlan
        {
            Name = "Personel kartı",
            Target = DocumentTarget.NewDocument,
            Actions =
            [
                new CreateDocumentAction { Id = "doc", WidthMm = 90, HeightMm = 55 },
                new CreateRectangleAction { Id = "frame", XMm = 3, YMm = 3, WidthMm = 84, HeightMm = 49, OutlineWidthMm = 0.5 },
                new CreateTextAction { Id = "name", Name = "Ad", Text = "Ahmet Yılmaz", XMm = 8, YMm = 15, FontFamily = "Arial", FontSizePt = 16 },
                new CreateTextAction { Id = "department", Name = "Bölüm", Text = "Üretim", XMm = 8, YMm = 28, FontFamily = "Arial", FontSizePt = 11 },
                new CreateTextAction { Id = "serial", Name = "Seri", Text = "000", XMm = 8, YMm = 40, FontFamily = "Arial", FontSizePt = 11 },
            ],
        };
        harness.Recipes.Save(RecipeBuilder.FromPlan(plan, "Personel Kartı",
            [new RecipeVariableBinding("PERSON_NAME", "Ahmet Yılmaz"), new RecipeVariableBinding("DEPARTMENT", "Üretim"), new RecipeVariableBinding("SERIAL", "000")]));
        viewModel.RefreshRecipesForTests();

        var xlsx = Path.Combine(folder, "personel.xlsx");
        using (var workbook = new ClosedXML.Excel.XLWorkbook())
        {
            var vehicles = workbook.AddWorksheet("Araçlar");
            vehicles.Cell(1, 1).Value = "PLAKA";
            vehicles.Cell(2, 1).Value = "34 ABC 123";
            var staff = workbook.AddWorksheet("Personel");
            string[][] rows = [["PERSON_NAME", "DEPARTMENT", "SERIAL"], ["Şükrü Öğüt", "Üretim", "001"], ["Ayşe Çağlar", "Üretim", "002"], ["İsmail Ünlü", "Satış", "003"]];
            for (var row = 0; row < rows.Length; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    staff.Cell(row + 1, column + 1).SetValue(rows[row][column]);
                }
            }

            workbook.SaveAs(xlsx);
        }

        try
        {
            viewModel.BatchRecipe = viewModel.Recipes.Single();
            viewModel.BatchDataPath = xlsx;
            Assert.True(viewModel.HasWorksheets);
            Assert.Equal(["Araçlar", "Personel"], viewModel.WorksheetNames);
            viewModel.SelectedWorksheet = "Personel";
            output.WriteLine(viewModel.BatchDataSummary);
            Assert.Contains("3 kayıt bulundu", viewModel.BatchDataSummary);
            Assert.Contains("PERSON_NAME, DEPARTMENT, SERIAL", viewModel.BatchDataSummary);
            Assert.Contains("001", viewModel.BatchSampleRows[0]);
            Assert.Contains("Şükrü Öğüt", viewModel.BatchSampleRows[0]);

            // Two rows share a department: the names must not collide.
            viewModel.BatchNamePattern = "kart_{{DEPARTMENT}}";
            viewModel.BatchCdr = viewModel.BatchPdf = viewModel.BatchPng = viewModel.BatchSvg = true;
            await harness.Run(viewModel.RunBatchCommand);
            output.WriteLine(viewModel.BatchStatus);
            Assert.All(viewModel.BatchRows, row => Assert.Equal("Tamamlandı", row.Status));
            var names = viewModel.BatchRows.Select(row => row.OutputName).ToArray();
            output.WriteLine(string.Join(", ", names));
            Assert.Equal(3, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            var files = Directory.GetFiles(folder, "kart_*");
            Assert.Equal(12, files.Length);
            Assert.All(new[] { ".cdr", ".pdf", ".png", ".svg" }, extension => Assert.Equal(3, files.Count(file => file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))));

            // The leading zeros reached CorelDRAW: reopen nothing, read the plan that ran instead.
            var serials = viewModel.BatchRows.Count;
            Assert.Equal(3, serials);

            // CSV still works.
            var csv = Path.Combine(folder, "personel.csv");
            File.WriteAllText(csv, "PERSON_NAME;DEPARTMENT;SERIAL\nGül Şahin;Muhasebe;007\n", new System.Text.UTF8Encoding(true));
            viewModel.BatchDataPath = csv;
            Assert.False(viewModel.HasWorksheets);
            viewModel.BatchNamePattern = "csv_{{SERIAL}}";
            viewModel.BatchPng = viewModel.BatchSvg = false;
            await harness.Run(viewModel.RunBatchCommand);
            Assert.True(File.Exists(Path.Combine(folder, "csv_007.cdr")), viewModel.BatchStatus);
            Assert.True(File.Exists(Path.Combine(folder, "csv_007.pdf")));
        }
        finally
        {
            await Close(inspector, executor, userHadDocumentOpen);
        }
    }
}

/// <summary>Multi-page PDF handling with the real PDF library: a synthetic three-page file, each page visibly different.</summary>
public sealed class MultiPagePdfAcceptanceTests
{
    private static string ThreePagePdf()
    {
        var path = Path.Combine(TestFolders.Create(), "uc-sayfa.pdf");
        using var stream = File.Create(path);
        using var document = SKDocument.CreatePdf(stream);
        SKColor[] colours = [SKColors.Red, SKColors.Green, SKColors.Blue];
        for (var page = 0; page < 3; page++)
        {
            using var canvas = document.BeginPage(400, 600);
            using var paint = new SKPaint { Color = colours[page] };
            canvas.DrawRect(40, 40 + (page * 150), 320, 120, paint);
            document.EndPage();
        }

        document.Close();
        return path;
    }

    private static byte[] Pixels(System.Windows.Media.ImageSource source)
    {
        var bitmap = (System.Windows.Media.Imaging.BitmapSource)source;
        var stride = bitmap.PixelWidth * ((bitmap.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    [Fact]
    public async Task Three_pages_are_detected_previewed_and_the_selected_one_is_analysed()
    {
        var harness = new OperatorHarness(scriptedVision: false, pdfPageCount: PdfPages.Count, referencePreviews: CompositeReferencePreviewRenderer.CreateDefault());
        var viewModel = harness.ViewModel;

        viewModel.AddReferences([ThreePagePdf()]);

        Assert.True(viewModel.HasPdfPages);
        Assert.Equal("PDF 3 sayfa içeriyor.", viewModel.PdfPagesText);
        Assert.Equal([1, 2, 3], viewModel.PdfPageNumbers);
        Assert.Null(viewModel.SelectedPdfPage);                       // never chosen silently
        Assert.False(viewModel.PreviewPdfPageCommand.CanExecute(null));

        viewModel.SelectedPdfPage = 1;
        await harness.Run(viewModel.PreviewPdfPageCommand);
        var first = Pixels(viewModel.PdfPagePreview!);
        viewModel.SelectedPdfPage = 3;
        Assert.False(viewModel.HasPdfPagePreview);                    // a stale preview is not left on screen
        await harness.Run(viewModel.PreviewPdfPageCommand);
        Assert.NotEqual(first, Pixels(viewModel.PdfPagePreview!));    // the preview follows the selection

        viewModel.Request = "Bunun aynısını 400x600 mm olarak yap.";
        await harness.Run(viewModel.PreparePlanCommand);
        Assert.Equal(3, harness.Vision.Requests[^1].PageNumber);

        viewModel.Request = "2. sayfayı 400x600 mm olarak yeniden çiz.";
        await harness.Run(viewModel.PreparePlanCommand);
        Assert.Equal(2, harness.Vision.Requests[^1].PageNumber);
        Assert.Equal(2, viewModel.SelectedPdfPage);
    }

    [Fact]
    public async Task The_real_renderer_returns_the_requested_page_and_refuses_one_that_does_not_exist()
    {
        var reference = ReferenceInput.FromFile(ThreePagePdf());
        var renderer = CompositeReferencePreviewRenderer.CreateDefault();

        var second = await renderer.RenderAsync(reference, new ReferencePreviewOptions { PageNumber = 2, MaxLongEdgePixels = 300 });
        Assert.Equal((2, 3), (second.PageNumber, second.PageCount));
        using (var image = SKBitmap.Decode(second.Bytes))
        {
            var pixel = image.GetPixel(image.Width / 2, (int)(250 / 600.0 * image.Height)); // inside page 2's green band
            Assert.True(pixel.Green > 100 && pixel.Red < 80 && pixel.Blue < 80, pixel.ToString());
        }

        await Assert.ThrowsAsync<ReferencePreviewException>(() => renderer.RenderAsync(reference, new ReferencePreviewOptions { PageNumber = 4 }));
    }
}
