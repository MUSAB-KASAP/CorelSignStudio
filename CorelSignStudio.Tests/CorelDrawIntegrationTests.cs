using System.Text.Json;
using System.Text.Json.Serialization;
using CorelSignStudio.Corel;
using CorelSignStudio.Domain;
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

        var cdrPath = Path.Combine(outputRoot, "CorelConnectionSmoke.cdr");
        var pdfPath = Path.Combine(outputRoot, "CorelConnectionSmoke.pdf");
        var reportPath = Path.Combine(outputRoot, "CorelConnectionSmoke.json");
        var logLines = new List<string>();

        await using var service = new CorelAutomationService(log: message =>
        {
            logLines.Add(message);
            output.WriteLine(message);
        });

        var connection = await service.ConnectAsync(visible: false);
        Assert.Equal(ApartmentState.STA, connection.ApartmentState);
        Assert.Equal(CorelAutomationService.DefaultProgId, connection.ProgId);

        await service.RenderDesignAsync(new DesignSpec
        {
            WidthMm = 500,
            HeightMm = 700,
            Elements =
            [
                new RectangleElement
                {
                    Id = "smoke-border",
                    XMm = 10,
                    YMm = 10,
                    WidthMm = 480,
                    HeightMm = 680,
                },
            ],
        });

        var saveResult = await service.SaveCdrAsync(cdrPath);
        await service.ExportPdfAsync(pdfPath);
        await service.CloseAsync();

        Assert.True(new FileInfo(cdrPath).Length > 0);
        Assert.True(new FileInfo(pdfPath).Length > 0);
        Assert.Contains("SaveAs", saveResult.RuntimeSignature, StringComparison.Ordinal);

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
