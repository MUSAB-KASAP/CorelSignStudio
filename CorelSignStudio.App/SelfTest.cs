using System.IO;
using System.Text;
using CorelSignStudio.AI;
using CorelSignStudio.Corel;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.References;
using CorelSignStudio.Imaging;
using CorelSignStudio.Storage;
using SkiaSharp;

namespace CorelSignStudio.App;

/// <summary>
/// "--selftest &lt;report file&gt; [--corel]": checks, inside this very executable, that everything a
/// published copy depends on actually loads and works — native imaging and PDF libraries, the Excel
/// reader, Windows key protection, the settings store and (with --corel) CorelDRAW automation. It is how
/// a release is smoke-tested without a person at the screen. Nothing is sent anywhere and no secret is
/// written to the report.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string reportPath, bool withCorel)
    {
        var lines = new List<string>();
        var failures = 0;
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "CorelSignStudio", "selftest-" + Guid.NewGuid().ToString("N"))).FullName;

        async Task Check(string name, Func<Task<string>> check)
        {
            try
            {
                lines.Add($"PASS  {name}: {await check()}");
            }
            catch (Exception exception)
            {
                failures++;
                lines.Add($"FAIL  {name}: {exception.GetType().Name}: {exception.Message}");
            }
        }

        var paths = AppPaths.Resolve();
        lines.Add($"Corel AI Operatörü {AppInfo.Version}");
        lines.Add($"Executable folder: {AppContext.BaseDirectory}");
        lines.Add($"Mode: {(paths.IsDevelopment ? "development (source checkout)" : "installed")}");
        lines.Add($"Data: {paths.DataFolder}");
        lines.Add($"Output: {paths.OutputFolder}");
        lines.Add($"Logs: {paths.LogFolder}");
        lines.Add("");

        await Check("Version", () => Task.FromResult(AppInfo.Version == "0.0.0" ? throw new InvalidOperationException("no version in the assembly") : AppInfo.Version));

        await Check("Turkish interface texts", () =>
        {
            var title = Ui.T("App.Title");
            var compare = Ui.T("Operator.Compare");
            return Task.FromResult(title.StartsWith('[') || compare.StartsWith('[') ? throw new InvalidOperationException("resources are missing") : $"{title} / {compare} / {Ui.F("About.Version", AppInfo.Version)}");
        });

        await Check("Bundled assets", () =>
        {
            var icons = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "assets", "icons"), "*.svg");
            return Task.FromResult(icons.Length > 0 ? $"{icons.Length} SVG icons" : throw new FileNotFoundException("no icons were published"));
        });

        var previews = CompositeReferencePreviewRenderer.CreateDefault();
        var pngPath = Path.Combine(work, "ornek.png");
        await Check("Image library (SkiaSharp, native)", async () =>
        {
            using (var bitmap = new SKBitmap(400, 300))
            {
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.White);
                using var paint = new SKPaint { Color = SKColors.Red };
                canvas.DrawCircle(200, 150, 90, paint);
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                await File.WriteAllBytesAsync(pngPath, data.ToArray());
            }

            var preview = await previews.RenderAsync(ReferenceInput.FromFile(pngPath), new ReferencePreviewOptions());
            return $"PNG preview {preview.WidthPixels} x {preview.HeightPixels}";
        });

        await Check("SVG rendering", async () =>
        {
            var svg = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "assets", "icons"), "*.svg")[0];
            var preview = await previews.RenderAsync(ReferenceInput.FromFile(svg), new ReferencePreviewOptions { MaxLongEdgePixels = 400 });
            return $"{Path.GetFileName(svg)} -> {preview.WidthPixels} x {preview.HeightPixels}";
        });

        await Check("PDF rendering (PDFium, native)", async () =>
        {
            var pdfPath = Path.Combine(work, "ornek.pdf");
            using (var stream = File.Create(pdfPath))
            using (var document = SKDocument.CreatePdf(stream))
            {
                for (var page = 0; page < 3; page++)
                {
                    using var canvas = document.BeginPage(595, 842);
                    using var paint = new SKPaint { Color = SKColors.Blue };
                    canvas.DrawRect(50, 50, 200 + (page * 50), 100, paint);
                    document.EndPage();
                }

                document.Close();
            }

            var pages = PdfPages.Count(pdfPath);
            var preview = await previews.RenderAsync(ReferenceInput.FromFile(pdfPath), new ReferencePreviewOptions { PageNumber = 2, MaxLongEdgePixels = 500 });
            return pages == 3 && preview.PageNumber == 2
                ? $"3 pages, page 2 rendered {preview.WidthPixels} x {preview.HeightPixels}, {preview.PhysicalSize?.WidthMm:0} x {preview.PhysicalSize?.HeightMm:0} mm"
                : throw new InvalidOperationException($"unexpected page count {pages}");
        });

        await Check("Excel reader (ClosedXML)", () =>
        {
            var xlsx = Path.Combine(work, "ornek.xlsx");
            using (var workbook = new ClosedXML.Excel.XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Personel");
                sheet.Cell(1, 1).Value = "AD";
                sheet.Cell(1, 2).Value = "SERI";
                sheet.Cell(2, 1).Value = "Şükrü Öğüt";
                sheet.Cell(2, 2).SetValue("001");
                workbook.AddWorksheet("Diğer").Cell(1, 1).Value = "X";
                workbook.SaveAs(xlsx);
            }

            var reader = BatchDataReaders.For(xlsx);
            var sheets = reader.GetSheetNames(xlsx);
            var rows = reader.Read(xlsx, "Personel");
            return Task.FromResult(sheets.Count == 2 && rows.Count == 1 && rows[0].Values["AD"] == "Şükrü Öğüt" && rows[0].Values["SERI"] == "001"
                ? $"{sheets.Count} worksheets, {rows.Count} row, Turkish text and leading zeros intact"
                : throw new InvalidOperationException("the workbook did not read back as written"));
        });

        await Check("Key protection (Windows DPAPI)", () =>
        {
            // A throw-away value in a throw-away file: the user's real settings are never touched.
            var probe = "probe-" + Guid.NewGuid().ToString("N");
            var file = Path.Combine(work, "ai-settings-probe.json");
            var store = new AiSettingsStore(file, new DpapiSecretProtector(), _ => null);
            store.SaveApiKey(AnthropicAiClient.Provider, probe);
            var readBack = new AiSettingsStore(file, new DpapiSecretProtector(), _ => null).GetApiKey(AnthropicAiClient.Provider);
            var onDisk = File.ReadAllText(file);
            return Task.FromResult(readBack == probe && !onDisk.Contains(probe, StringComparison.Ordinal)
                ? "stored encrypted and read back"
                : throw new InvalidOperationException("the value was not protected or did not round-trip"));
        });

        await Check("AI settings", () =>
        {
            var runtime = new AiRuntime(new AiSettingsStore(AiSettingsStore.DefaultFilePath, new DpapiSecretProtector()), _ => { });
            var settings = runtime.Settings;
            return Task.FromResult($"provider {settings.Provider}, model {settings.Model}, credential {(runtime.IsConfigured ? "configured" : "not configured")}");
        });

        await Check("Fonts and glyph coverage", () =>
        {
            var fonts = System.Windows.Media.Fonts.SystemFontFamilies.Count;
            return Task.FromResult(FontCoverage.Supports("Arial", "ĞÜŞİÖÇ ğüşıöç") && fonts > 10
                ? $"{fonts} font families; Arial draws Turkish letters"
                : throw new InvalidOperationException("font coverage could not be read"));
        });

        if (withCorel)
        {
            await using var corel = new CorelAutomationService(new FileSystemAssetCatalog(Path.Combine(AppContext.BaseDirectory, "assets", "icons")), _ => { }) { KeepApplicationOpen = true };
            var inspector = new CorelDocumentInspector(corel);
            var executor = new CorelActionExecutor(corel);
            var opened = false;

            await Check("CorelDRAW connection (COM)", async () => "CorelDRAW " + (await corel.ConnectAsync(visible: true)).Version);

            await Check("CorelDRAW automation", async () =>
            {
                var result = await executor.ExecuteAsync(new AutomationPlan
                {
                    Name = "Self test",
                    Target = DocumentTarget.NewDocument,
                    Actions =
                    [
                        new CreateDocumentAction { Id = "doc", WidthMm = 200, HeightMm = 100 },
                        new CreateRectangleAction { Id = "frame", XMm = 10, YMm = 10, WidthMm = 180, HeightMm = 80, OutlineWidthMm = 1 },
                        new CreateTextAction { Id = "text", Text = "GİRİŞ ÇIKIŞ", Name = "Sınama", XMm = 30, YMm = 35, FontFamily = "Arial", FontSizePt = 40 },
                    ],
                });
                opened = result.Success;
                if (!result.Success)
                {
                    throw new InvalidOperationException(result.Summary);
                }

                var document = await inspector.InspectActiveDocumentAsync() ?? throw new InvalidOperationException("no document after creating one");
                var text = document.FindShapesByName("Sınama").Single();
                return text.Text == "GİRİŞ ÇIKIŞ" ? $"{document.ShapeCount} objects, text read back '{text.Text}'" : throw new InvalidOperationException($"text came back as '{text.Text}'");
            });

            await Check("CorelDRAW full-page preview", async () =>
            {
                var preview = await new CorelPagePreviewRenderer(corel).RenderActivePageAsync(600);
                return preview is { WidthPixels: 600, HeightPixels: 300 } ? "600 x 300 for a 200 x 100 mm page" : throw new InvalidOperationException($"{preview.WidthPixels} x {preview.HeightPixels}");
            });

            await Check("CorelDRAW outputs (CDR, PDF)", async () =>
            {
                var result = await executor.ExecuteAsync(new AutomationPlan
                {
                    Name = "Self test outputs",
                    Actions =
                    [
                        new SaveDocumentAction { Id = "cdr", FilePath = Path.Combine(work, "sinama.cdr") },
                        new ExportPdfAction { Id = "pdf", FilePath = Path.Combine(work, "sinama.pdf") },
                    ],
                });
                return result.Success && result.ProducedFiles.All(File.Exists) ? $"{result.ProducedFiles.Count} files written" : throw new InvalidOperationException(result.Summary);
            });

            if (opened)
            {
                await executor.ExecuteAsync(new AutomationPlan { Name = "Self test cleanup", Actions = [new CloseDocumentAction { Id = "close" }] });
            }
        }
        else
        {
            lines.Add("SKIP  CorelDRAW: run with --corel to include it");
        }

        lines.Add("");
        lines.Add(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures})");
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left for the temp folder's normal clean-up.
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllLinesAsync(reportPath, lines, new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }
}
