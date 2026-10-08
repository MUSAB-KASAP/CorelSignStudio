using System.Text.Json;
using System.Text.Json.Serialization;
using CorelSignStudio.Corel;
using CorelSignStudio.Storage;
using CorelSignStudio.Templates;

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
        var artifactRoot = Path.Combine(solutionRoot, "artifacts", "milestone-2");
        var outputRoot = Path.Combine(solutionRoot, "output");
        Directory.CreateDirectory(artifactRoot);
        Directory.CreateDirectory(outputRoot);

        var reportPath = Path.Combine(artifactRoot, "Milestone2Smoke.json");
        var failurePath = Path.Combine(artifactRoot, "Milestone2Smoke.failure.json");
        var logLines = new List<string>();
        var checks = RunContractChecks();

        try
        {
            var assetCatalog = new FileSystemAssetCatalog(Path.Combine(solutionRoot, "assets", "icons"));
            var template = new ProhibitionSignTemplate();
            var design = template.CreateDesign(new SignTemplateParameters(
                500, 700, "BU ALANA", "GİRMEK", "YASAKTIR", "no-entry-hand"));
            var paths = new OutputNameGenerator().CreateAvailablePaths(
                outputRoot,
                ["BU ALANA", "GİRMEK", "YASAKTIR"],
                design.WidthMm,
                design.HeightMm,
                includeCdr: true,
                includePdf: true);

            await using var service = new CorelAutomationService(assetCatalog, logLines.Add);
            var connection = await service.ConnectAsync(visible: false);
            await service.RenderDesignAsync(design);
            var saveResult = await service.SaveCdrAsync(paths.CdrPath!);
            await service.ExportPdfAsync(paths.PdfPath!);
            var reopened = await service.OpenCdrAsync(paths.CdrPath!);
            await service.CloseAsync();

            if (Math.Abs(reopened.WidthMm - 500) > 0.05 || Math.Abs(reopened.HeightMm - 700) > 0.05)
            {
                throw new InvalidDataException($"Reopened CDR page was {reopened.WidthMm} x {reopened.HeightMm} mm.");
            }

            var report = new
            {
                Success = true,
                TimestampUtc = DateTimeOffset.UtcNow,
                ContractChecks = checks,
                Connection = connection,
                Design = new { design.WidthMm, design.HeightMm, ElementCount = design.Elements.Count },
                Save = saveResult,
                PdfPath = paths.PdfPath,
                Reopened = reopened,
                CdrBytes = new FileInfo(paths.CdrPath!).Length,
                PdfBytes = new FileInfo(paths.PdfPath!).Length,
                Log = logLines,
            };

            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions));
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
                ContractChecks = checks,
                Exception = exception.ToString(),
                Log = logLines,
            };
            await File.WriteAllTextAsync(failurePath, JsonSerializer.Serialize(failure, JsonOptions));
            return 1;
        }
    }

    private static IReadOnlyList<string> RunContractChecks()
    {
        var completed = new List<string>();
        var designTests = new DesignSpecTests();
        designTests.Validate_accepts_all_supported_element_types();
        completed.Add(nameof(designTests.Validate_accepts_all_supported_element_types));
        designTests.Validate_rejects_duplicate_element_ids();
        completed.Add(nameof(designTests.Validate_rejects_duplicate_element_ids));
        designTests.Validate_rejects_elements_outside_page_bounds();
        completed.Add(nameof(designTests.Validate_rejects_elements_outside_page_bounds));

        var templateTests = new TemplateTests();
        templateTests.Prohibition_template_creates_a_500_by_700_design();
        completed.Add(nameof(templateTests.Prohibition_template_creates_a_500_by_700_design));
        templateTests.Prohibition_template_contains_required_text_and_svg_asset();
        completed.Add(nameof(templateTests.Prohibition_template_contains_required_text_and_svg_asset));
        templateTests.Prohibition_template_keeps_every_element_inside_page_bounds();
        completed.Add(nameof(templateTests.Prohibition_template_keeps_every_element_inside_page_bounds));

        var storageTests = new StorageTests();
        storageTests.Asset_catalog_resolves_registered_svg_by_id();
        completed.Add(nameof(storageTests.Asset_catalog_resolves_registered_svg_by_id));
        storageTests.Output_name_is_safe_and_transliterates_turkish_characters();
        completed.Add(nameof(storageTests.Output_name_is_safe_and_transliterates_turkish_characters));
        storageTests.Output_paths_use_a_shared_version_suffix_when_a_name_exists();
        completed.Add(nameof(storageTests.Output_paths_use_a_shared_version_suffix_when_a_name_exists));
        return completed;
    }
}
