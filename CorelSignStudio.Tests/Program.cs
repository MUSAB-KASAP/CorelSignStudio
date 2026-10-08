using System.Text.Json;
using System.Text.Json.Serialization;
using CorelSignStudio.Corel;
using CorelSignStudio.Domain;

namespace CorelSignStudio.Tests;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [STAThread]
    private static async Task<int> Main()
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var outputRoot = Path.Combine(solutionRoot, "artifacts", "corel-smoke");
        Directory.CreateDirectory(outputRoot);

        var cdrPath = Path.Combine(outputRoot, "CorelConnectionSmoke.cdr");
        var pdfPath = Path.Combine(outputRoot, "CorelConnectionSmoke.pdf");
        var reportPath = Path.Combine(outputRoot, "CorelConnectionSmoke.json");
        var failurePath = Path.Combine(outputRoot, "CorelConnectionSmoke.failure.json");
        var logLines = new List<string>();

        try
        {
            await using var service = new CorelAutomationService(log: logLines.Add);
            var connection = await service.ConnectAsync(visible: false);

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

            var report = new
            {
                Success = true,
                TimestampUtc = DateTimeOffset.UtcNow,
                Connection = connection,
                Save = saveResult,
                CdrBytes = new FileInfo(cdrPath).Length,
                PdfBytes = new FileInfo(pdfPath).Length,
                Log = logLines,
            };

            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(report, JsonOptions));

            if (File.Exists(failurePath))
            {
                File.Delete(failurePath);
            }

            return 0;
        }
        catch (Exception exception)
        {
            var failure = new
            {
                Success = false,
                TimestampUtc = DateTimeOffset.UtcNow,
                Exception = exception.ToString(),
                Log = logLines,
            };
            await File.WriteAllTextAsync(
                failurePath,
                JsonSerializer.Serialize(failure, JsonOptions));
            return 1;
        }
    }
}
