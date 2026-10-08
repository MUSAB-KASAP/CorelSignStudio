using CorelSignStudio.Storage;

namespace CorelSignStudio.Tests;

public sealed class StorageTests
{
    [Fact]
    public void Asset_catalog_resolves_registered_svg_by_id()
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var catalog = new FileSystemAssetCatalog(Path.Combine(solutionRoot, "assets", "icons"));
        var asset = catalog.GetAsset("no-entry-hand");
        var path = catalog.ResolveAssetPath(asset.Id);

        Assert.Equal("Girmek Yasaktır", asset.Name);
        Assert.Equal("Prohibition", asset.Category);
        Assert.True(File.Exists(path));
        Assert.Equal(".svg", Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Output_name_is_safe_and_transliterates_turkish_characters()
    {
        var name = new OutputNameGenerator().BuildBaseName(["BU ALANA", "GİRMEK", "YASAKTIR"], 500, 700);
        Assert.Equal("BU_ALANA_GIRMEK_YASAKTIR_500x700", name);
    }

    [Fact]
    public void Output_paths_use_a_shared_version_suffix_when_a_name_exists()
    {
        var outputFolder = Path.Combine(Path.GetTempPath(), "CorelSignStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputFolder);
        File.WriteAllText(Path.Combine(outputFolder, "BU_ALANA_GIRMEK_YASAKTIR_500x700.cdr"), "existing");

        var paths = new OutputNameGenerator().CreateAvailablePaths(
            outputFolder, ["BU ALANA", "GİRMEK", "YASAKTIR"], 500, 700, includeCdr: true, includePdf: true);

        Assert.Equal("BU_ALANA_GIRMEK_YASAKTIR_500x700_002", paths.BaseName);
        Assert.EndsWith("_002.cdr", paths.CdrPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("_002.pdf", paths.PdfPath, StringComparison.OrdinalIgnoreCase);
    }
}
