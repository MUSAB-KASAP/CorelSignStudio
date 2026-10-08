using System.Text.Json;
using System.Text.Json.Serialization;
using CorelSignStudio.Corel;
using CorelSignStudio.Domain;
using CorelSignStudio.Storage;
using CorelSignStudio.Templates;
using Xunit.Abstractions;

namespace CorelSignStudio.Tests;

public sealed class CorelDrawIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "CorelIntegration")]
    public async Task CorelDraw27_connects_on_sta_and_writes_real_cdr_and_pdf_files()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("COREL_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            output.WriteLine("Set COREL_INTEGRATION=1 to run the installed-CorelDRAW smoke test.");
            return;
        }

        var outputRoot = Environment.GetEnvironmentVariable("COREL_SMOKE_OUTPUT")
            ?? Path.Combine(AppContext.BaseDirectory, "corel-smoke");
        Directory.CreateDirectory(outputRoot);

        var cdrPath = Path.Combine(outputRoot, "BU_ALANA_GIRMEK_YASAKTIR_500x700.cdr");
        var pdfPath = Path.Combine(outputRoot, "BU_ALANA_GIRMEK_YASAKTIR_500x700.pdf");
        var reportPath = Path.Combine(outputRoot, "CorelConnectionSmoke.json");
        var logLines = new List<string>();

        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var assets = new FileSystemAssetCatalog(Path.Combine(solutionRoot, "assets", "icons"));
        await using var service = new CorelAutomationService(assets, message =>
        {
            logLines.Add(message);
            output.WriteLine(message);
        });

        var connection = await service.ConnectAsync(visible: false);
        Assert.Equal(ApartmentState.STA, connection.ApartmentState);
        Assert.Equal(CorelAutomationService.DefaultProgId, connection.ProgId);

        var design = new ProhibitionSignTemplate().CreateDesign(
            new SignTemplateParameters(500, 700, "BU ALANA", "GİRMEK", "YASAKTIR", "no-entry-hand"));
        await service.RenderDesignAsync(design);

        var saveResult = await service.SaveCdrAsync(cdrPath);
        await service.ExportPdfAsync(pdfPath);
        var reopened = await service.OpenCdrAsync(cdrPath);
        await service.CloseAsync();

        Assert.True(new FileInfo(cdrPath).Length > 0);
        Assert.True(new FileInfo(pdfPath).Length > 0);
        Assert.Contains("SaveAs", saveResult.RuntimeSignature, StringComparison.Ordinal);
        Assert.Equal(500, reopened.WidthMm, 1);
        Assert.Equal(700, reopened.HeightMm, 1);

        var report = new
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Connection = connection,
            Save = saveResult,
            CdrBytes = new FileInfo(cdrPath).Length,
            PdfBytes = new FileInfo(pdfPath).Length,
            Log = logLines,
        };

        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() },
            }));
    }
}
