using System.Text;
using CorelSignStudio.Domain.Ai;
using CorelSignStudio.Domain.Ai.Vision;
using CorelSignStudio.Domain.Assets;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.References;
using CorelSignStudio.Imaging;
using SkiaSharp;

namespace CorelSignStudio.Tests;

/// <summary>Synthetic reference files, drawn or written by the tests themselves — nothing copyrighted or confidential.</summary>
internal static class ReferenceFixtures
{
    public static string Folder { get; } = TestFolders.Create();

    /// <summary>A prohibition sign: white ground, black frame, red ring and bar.</summary>
    public static string ProhibitionSignPng(int width = 500, int height = 700, string name = "yasak-levhasi.png")
    {
        // Tests run in parallel and share this fixture, so it is written exactly once.
        lock (Gate)
        {
            var existing = Path.Combine(Folder, name);
            return File.Exists(existing) ? existing : DrawProhibitionSign(width, height, name);
        }
    }

    private static readonly object Gate = new();

    private static string DrawProhibitionSign(int width, int height, string name)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var frame = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = width * 0.012f, IsAntialias = true };
            canvas.DrawRect(width * 0.04f, height * 0.03f, width * 0.92f, height * 0.94f, frame);
            using var red = new SKPaint { Color = new SKColor(0xD8, 0x20, 0x2A), Style = SKPaintStyle.Stroke, StrokeWidth = width * 0.06f, IsAntialias = true };
            canvas.DrawCircle(width * 0.5f, height * 0.3f, width * 0.27f, red);
            canvas.DrawLine(width * 0.31f, height * 0.165f, width * 0.69f, height * 0.435f, red);
        }

        return Save(bitmap, name, SKEncodedImageFormat.Png);
    }

    public static string SolidImage(int width, int height, string name, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(0, 0, width / 2f, height / 2f, paint); // top-left quadrant blue
        }

        return Save(bitmap, name, format);
    }

    public static string Svg(string name, string sizeAttributes) =>
        Write(name, $"""<svg xmlns="http://www.w3.org/2000/svg" {sizeAttributes}><rect width="10" height="10" fill="red"/></svg>""");

    /// <summary>A minimal, valid PDF with the given page sizes in points.</summary>
    public static string Pdf(string name, params (double Width, double Height)[] pages)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };
        var kids = string.Join(" ", pages.Select((_, index) => $"{3 + index} 0 R"));
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Length} >>");
        objects.AddRange(pages.Select(page => FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Width} {page.Height}] >>")));

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(builder.Length);
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }

        var xref = builder.Length;
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(offset.ToString("0000000000", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        var path = Path.Combine(Folder, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(builder.ToString()));
        return path;
    }

    public static string Write(string name, string content)
    {
        var path = Path.Combine(Folder, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string Save(SKBitmap bitmap, string name, SKEncodedImageFormat format)
    {
        var path = Path.Combine(Folder, name);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 92);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    // ---- Scripted model answers --------------------------------------------------------------

    public static string Answer(string elementsJson, string background = "#FFFFFF", string modifications = "[]", string warnings = "[]", double confidence = 0.91) =>
        $$"""{"status":"ready","summary":"Beyaz zeminli bir yasak levhası.","clarificationQuestion":"","confidence":{{confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"warnings":{{warnings}},"appliedModifications":{{modifications}},"backgroundColor":"{{background}}","elements":{{elementsJson}}}""";

    /// <summary>The end-to-end sign from the specification: frame, prohibition sign and three lines of text.</summary>
    public const string SignElements = """
        [
         {"key":"e1","parentKey":"","kind":"rectangle","label":"Çerçeve","bounds":{"x":0.04,"y":0.03,"width":0.92,"height":0.94},"zIndex":1,"fillColor":"#FFFFFF","outlineColor":"#000000","outlineWidthRatio":0.012,"cornerRadiusRatio":0.03,"strategy":"nativeShape","confidence":0.95},
         {"key":"e2","parentKey":"","kind":"prohibitionSign","label":"Yasak işareti","bounds":{"x":0.2,"y":0.1,"width":0.6,"height":0.4285714},"zIndex":2,"outlineColor":"#D8202A","outlineWidthRatio":0.06,"strategy":"nativeShape","confidence":0.95},
         {"key":"e3","parentKey":"g1","kind":"text","label":"Satır 1","bounds":{"x":0.22,"y":0.57,"width":0.56,"height":0.07},"zIndex":5,"text":"BU ALANA","textConfidence":0.98,"fontFamilyGuess":"Arial","fontConfidence":0.9,"bold":true,"textAlignment":"center","direction":"ltr","fillColor":"#D8202A","strategy":"text","confidence":0.95},
         {"key":"e4","parentKey":"g1","kind":"text","label":"Satır 2","bounds":{"x":0.28,"y":0.68,"width":0.44,"height":0.07},"zIndex":6,"text":"GİRMEK","textConfidence":0.98,"fontFamilyGuess":"Arial","fontConfidence":0.9,"bold":true,"textAlignment":"center","direction":"ltr","fillColor":"#D8202A","strategy":"text","confidence":0.95},
         {"key":"e5","parentKey":"g1","kind":"text","label":"Satır 3","bounds":{"x":0.2,"y":0.79,"width":0.6,"height":0.08},"zIndex":7,"text":"YASAKTIR","textConfidence":0.98,"fontFamilyGuess":"Arial","fontConfidence":0.9,"bold":true,"textAlignment":"center","direction":"ltr","fillColor":"#D8202A","strategy":"text","confidence":0.95},
         {"key":"g1","parentKey":"","kind":"group","label":"Yazı bloğu","bounds":{"x":0.2,"y":0.57,"width":0.6,"height":0.3},"zIndex":8,"strategy":"nativeShape","confidence":0.9}
        ]
        """;

    public static AiReferenceAnalyzer Analyzer(IAiClient? client, IReferencePreviewRenderer? renderer = null) =>
        new(() => client, renderer ?? CompositeReferencePreviewRenderer.CreateDefault(), delay: (_, _) => Task.CompletedTask);

    public static async Task<ReferenceAnalysis> Analyze(string elementsJson, string userRequest = "", string? path = null)
    {
        var result = await Analyzer(new FakeVisionClient(Answer(elementsJson))).AnalyzeAsync(
            new ReferenceAnalysisRequest { Reference = ReferenceInput.FromFile(path ?? ProhibitionSignPng()), UserRequest = userRequest });
        Assert.True(result.IsReady, result.UserMessage);
        return result.Analysis!;
    }
}

/// <summary>A scripted multimodal client.</summary>
internal sealed class FakeVisionClient(params object[] script) : IAiClient
{
    private readonly FakeAiClient _inner = new(script);

    public List<AiRequest> Requests => _inner.Requests;

    public string ProviderId => "fake-vision";

    public string Model => "fake-vision-model";

    public bool SupportsImages => true;

    public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default) => _inner.CompleteAsync(request, cancellationToken);
}

public sealed class DimensionAndGeometryTests
{
    [Theory]
    [InlineData("Bunun aynısını 500x700 mm olarak yap", 500, 700)]
    [InlineData("50x70 cm yap", 500, 700)]
    [InlineData("50 cm x 70 cm", 500, 700)]
    [InlineData("1,2 x 2 m afiş", 1200, 2000)]
    [InlineData("500 × 700", 500, 700)]
    [InlineData("12,5x7.5mm etiket", 12.5, 7.5)]
    [InlineData("50 cm x 700 mm", 500, 700)]
    public void Sizes_in_free_text_are_normalised_to_millimetres(string text, double width, double height)
    {
        Assert.True(DimensionParser.TryParse(text, out var size));
        Assert.Equal((width, height), (size.WidthMm, size.HeightMm));
    }

    [Theory]
    [InlineData("Bunun aynısını yap")]
    [InlineData("shape_001'i 10 mm sağa taşı")]
    [InlineData("3 sütun 4 satır tablo")]
    [InlineData("")]
    [InlineData(null)]
    public void Text_without_a_size_yields_none(string? text) => Assert.False(DimensionParser.TryParse(text, out _));

    [Fact]
    public void Normalised_bounds_validate_and_clamp()
    {
        Assert.True(new NormalizedBounds(0.04, 0.03, 0.92, 0.94).IsValid);
        Assert.True(new NormalizedBounds(0, 0, 1.01, 1).IsValid); // tiny overshoot is tolerated…
        Assert.Equal(new NormalizedBounds(0, 0, 1, 1), new NormalizedBounds(0, 0, 1.01, 1).Clamp()); // …and clamped
        Assert.False(new NormalizedBounds(-0.2, 0, 0.5, 0.5).IsValid);
        Assert.False(new NormalizedBounds(0.5, 0.5, 0.8, 0.2).IsValid);
        Assert.False(new NormalizedBounds(0, 0, 0, 0.5).IsValid);
        Assert.False(new NormalizedBounds(0, 0, double.NaN, 0.5).IsValid);
        Assert.Equal((0.5, 0.5), (new NormalizedBounds(0.25, 0.25, 0.5, 0.5).CenterX, new NormalizedBounds(0.25, 0.25, 0.5, 0.5).CenterY));
    }
}

public sealed class ReferencePreviewTests
{
    [Fact]
    public async Task Large_images_are_downscaled_with_aspect_kept_and_the_original_untouched()
    {
        var path = ReferenceFixtures.SolidImage(4000, 3000, "buyuk.jpg", SKEncodedImageFormat.Jpeg);
        var before = File.ReadAllBytes(path);

        var preview = await new ImageReferencePreviewRenderer().RenderAsync(ReferenceInput.FromFile(path), new ReferencePreviewOptions());

        Assert.Equal((1568, 1176), (preview.WidthPixels, preview.HeightPixels));
        Assert.Equal((4000, 3000), (preview.OriginalWidthPixels, preview.OriginalHeightPixels));
        Assert.Equal("image/png", preview.MimeType);
        Assert.Null(preview.PhysicalSize); // pixels are not millimetres
        Assert.Equal(before, File.ReadAllBytes(path));
        using var decoded = SKBitmap.Decode(preview.Bytes);
        Assert.Equal((1568, 1176), (decoded.Width, decoded.Height));
        Assert.True(decoded.GetPixel(100, 100).Blue > 200 && decoded.GetPixel(100, 100).Red < 60);   // top-left stays blue
        Assert.True(decoded.GetPixel(1500, 1100).Red > 200);                                          // bottom-right stays white
    }

    [Fact]
    public async Task Small_images_are_not_upscaled()
    {
        var preview = await new ImageReferencePreviewRenderer().RenderAsync(
            ReferenceInput.FromFile(ReferenceFixtures.SolidImage(320, 200, "kucuk.png")), new ReferencePreviewOptions());

        Assert.Equal((320, 200), (preview.WidthPixels, preview.HeightPixels));
    }

    [Theory]
    [InlineData(SKEncodedOrigin.RightTop, 30, 40, 29, 0)]      // rotated 90° clockwise: top-left moves to top-right
    [InlineData(SKEncodedOrigin.BottomRight, 40, 30, 39, 29)]  // rotated 180°
    [InlineData(SKEncodedOrigin.LeftBottom, 30, 40, 0, 39)]    // rotated 90° counter-clockwise
    [InlineData(SKEncodedOrigin.TopRight, 40, 30, 39, 0)]      // mirrored
    public void Exif_orientation_is_applied(SKEncodedOrigin origin, int expectedWidth, int expectedHeight, int markerX, int markerY)
    {
        using var source = new SKBitmap(40, 30);
        source.Erase(SKColors.White);
        source.SetPixel(0, 0, SKColors.Red); // marker in the stored top-left corner

        using var upright = SkiaImagesAccessor.Reorient(source, origin);

        Assert.Equal((expectedWidth, expectedHeight), (upright.Width, upright.Height));
        Assert.Equal(SKColors.Red, upright.GetPixel(markerX, markerY));
    }

    [Fact]
    public async Task Pdf_page_size_is_a_reliable_physical_size()
    {
        // 500 x 700 mm in points.
        var path = ReferenceFixtures.Pdf("tabela.pdf", (1417.32, 1984.25));

        var preview = await new PdfReferencePreviewRenderer().RenderAsync(ReferenceInput.FromFile(path), new ReferencePreviewOptions());

        Assert.Equal(1, preview.PageCount);
        Assert.Equal(500, preview.PhysicalSize!.WidthMm, 1);
        Assert.Equal(700, preview.PhysicalSize.HeightMm, 1);
        Assert.InRange(preview.HeightPixels, 1400, 1568);
        Assert.True(preview.Bytes.Length > 100);
    }

    [Fact]
    public async Task Multi_page_pdf_reports_its_pages_and_rejects_a_missing_one()
    {
        var path = ReferenceFixtures.Pdf("cok-sayfa.pdf", (595, 842), (842, 595), (595, 842));
        var renderer = new PdfReferencePreviewRenderer();

        var second = await renderer.RenderAsync(ReferenceInput.FromFile(path), new ReferencePreviewOptions { PageNumber = 2 });

        Assert.Equal((3, 2), (second.PageCount, second.PageNumber));
        Assert.True(second.WidthPixels > second.HeightPixels); // the landscape page
        Assert.Equal(297, second.PhysicalSize!.WidthMm, 0);
        var exception = await Assert.ThrowsAsync<ReferencePreviewException>(() =>
            renderer.RenderAsync(ReferenceInput.FromFile(path), new ReferencePreviewOptions { PageNumber = 7 }));
        Assert.Equal("Bu dosyada 7. sayfa yok; dosya 3 sayfa içeriyor.", exception.Message);
    }

    [Theory]
    [InlineData("""width="500mm" height="700mm" viewBox="0 0 500 700" """, 500d, 700d)]
    [InlineData("""width="50cm" height="70cm" """, 500d, 700d)]
    [InlineData("""width="10in" height="5in" """, 254d, 127d)]
    [InlineData("""width="500" height="700" """, null, null)]            // unitless: not a physical size
    [InlineData("""width="500px" height="700px" """, null, null)]
    [InlineData("""viewBox="0 0 24 24" """, null, null)]
    public async Task Svg_size_is_physical_only_in_real_units(string attributes, double? width, double? height)
    {
        var preview = await new SvgReferenceInfoReader().RenderAsync(
            ReferenceInput.FromFile(ReferenceFixtures.Svg($"s{Math.Abs(attributes.GetHashCode())}.svg", attributes)), new ReferencePreviewOptions());

        Assert.Equal((width, height), (preview.PhysicalSize?.WidthMm, preview.PhysicalSize?.HeightMm));
        Assert.Empty(preview.Bytes); // vectors are reused, not rasterised
    }

    [Fact]
    public async Task Unreadable_files_give_a_turkish_message()
    {
        var broken = ReferenceFixtures.Write("bozuk.png", "this is not an image");

        var exception = await Assert.ThrowsAsync<ReferencePreviewException>(() =>
            new ImageReferencePreviewRenderer().RenderAsync(ReferenceInput.FromFile(broken), new ReferencePreviewOptions()));

        Assert.Equal("Referans dosyası açılamadı veya önizlemesi oluşturulamadı: bozuk.png", exception.Message);
    }

    [Fact]
    public void Cropped_components_go_to_a_temp_folder_that_is_cleaned_up()
    {
        var path = ReferenceFixtures.SolidImage(400, 200, "kirpma.png");
        string folder, cropped;
        using (var store = new ReferenceTempStore())
        {
            folder = store.Folder;
            cropped = new SkiaReferenceImageCropper(store).Crop(ReferenceInput.FromFile(path), new NormalizedBounds(0, 0, 0.5, 0.5), "ref_003")!;

            Assert.StartsWith(folder, cropped);
            Assert.EndsWith("kirpma_ref_003.png", cropped);
            using var image = SKBitmap.Decode(cropped);
            Assert.Equal((200, 100), (image.Width, image.Height));
            Assert.True(image.GetPixel(100, 50).Blue > 200);
            Assert.Null(new SkiaReferenceImageCropper(store).Crop(ReferenceInput.FromFile(@"C:\yok\a.pdf"), new NormalizedBounds(0, 0, 1, 1), "ref_001"));
        }

        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(path)); // the original reference is never deleted
    }
}

internal static class SkiaImagesAccessor
{
    public static SKBitmap Reorient(SKBitmap source, SKEncodedOrigin origin) =>
        (SKBitmap)typeof(ImageReferencePreviewRenderer).Assembly.GetType("CorelSignStudio.Imaging.SkiaImages")!
            .GetMethod("Reorient")!.Invoke(null, [source, origin])!;
}

public sealed class ReferenceAnalysisTests
{
    [Fact]
    public async Task A_bitmap_is_sent_to_the_vision_model_and_becomes_structured_elements()
    {
        var client = new FakeVisionClient(ReferenceFixtures.Answer(ReferenceFixtures.SignElements));
        var reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng());

        var result = await ReferenceFixtures.Analyzer(client).AnalyzeAsync(
            new ReferenceAnalysisRequest { Reference = reference, UserRequest = "Bunun aynısını 500x700 mm olarak CorelDRAW'da yap." });

        Assert.True(result.IsReady, result.UserMessage);
        Assert.Equal("Referans analiz edildi: 6 öğe bulundu.", result.UserMessage);
        var analysis = result.Analysis!;
        Assert.Equal(["ref_001", "ref_002", "ref_003", "ref_004", "ref_005", "ref_006"], analysis.Elements.Select(element => element.Id));
        Assert.Equal(ReferenceElementKind.Rectangle, analysis.Elements[0].Kind);
        Assert.Equal(new NormalizedBounds(0.04, 0.03, 0.92, 0.94), analysis.Elements[0].Bounds);
        Assert.Equal(("#FFFFFF", "#000000"), (analysis.Elements[0].FillColor, analysis.Elements[0].OutlineColor));
        Assert.Equal(["BU ALANA", "GİRMEK", "YASAKTIR"], analysis.Elements.Where(element => element.Kind == ReferenceElementKind.Text).Select(element => element.Text));
        Assert.Null(analysis.PhysicalSize);                       // a PNG has no trustworthy physical size
        Assert.Equal((500, 700), (analysis.PixelWidth, analysis.PixelHeight));
        Assert.Equal(500d / 700, analysis.AspectRatio!.Value, 3);
        Assert.Equal(0.91, analysis.Confidence);
        Assert.Contains("#D8202A", analysis.Colors);
        Assert.Equal(["Arial"], analysis.Fonts);

        // Group membership is resolved to the stable ids.
        var group = analysis.Elements.Single(element => element.Kind == ReferenceElementKind.Group);
        Assert.Equal(3, analysis.Elements.Count(element => element.ParentId == group.Id));

        // Exactly one image went out — the selected reference — together with the policy and the request.
        var sent = Assert.Single(client.Requests);
        var image = Assert.Single(sent.Images);
        Assert.Equal(("yasak-levhasi.png", "image/png", 500, 700), (image.FileName, image.MimeType, image.WidthPixels, image.HeightPixels));
        Assert.Contains("Never invent text", sent.SystemPrompt);
        Assert.Contains("Do not state or guess a physical size", sent.SystemPrompt);
        Assert.Contains("needsUserAsset", sent.SystemPrompt);
        Assert.Contains("Arabic", sent.SystemPrompt);
        Assert.EndsWith("Bunun aynısını 500x700 mm olarak CorelDRAW'da yap.", sent.UserMessage);
        Assert.NotNull(sent.JsonSchema);
    }

    [Fact]
    public async Task Turkish_and_arabic_text_is_preserved_exactly()
    {
        const string Turkish = "ÇIKIŞ ĞÜ İŞLEM şöı\\nİkinci satır";
        const string Arabic = "ممنوع الدخول";
        var analysis = await ReferenceFixtures.Analyze($$"""
            [{"key":"a","kind":"text","label":"TR","bounds":{"x":0.1,"y":0.1,"width":0.8,"height":0.2},"zIndex":1,"text":"{{Turkish}}","textConfidence":0.95,"direction":"ltr","strategy":"text","confidence":0.9},
             {"key":"b","kind":"text","label":"AR","bounds":{"x":0.1,"y":0.5,"width":0.8,"height":0.1},"zIndex":2,"text":"{{Arabic}}","textConfidence":0.9,"direction":"rtl","strategy":"text","confidence":0.9}]
            """);

        Assert.Equal("ÇIKIŞ ĞÜ İŞLEM şöı\nİkinci satır", analysis.Elements[0].Text);
        Assert.Equal(Arabic, analysis.Elements[1].Text);
        Assert.Equal(ReferenceTextDirection.RightToLeft, analysis.Elements[1].Direction);
        Assert.False(analysis.Elements[0].TextUncertain);
    }

    [Fact]
    public async Task Uncertain_text_is_flagged_not_silently_accepted()
    {
        var result = await ReferenceFixtures.Analyzer(new FakeVisionClient(ReferenceFixtures.Answer(
            """[{"key":"a","kind":"text","bounds":{"x":0.1,"y":0.1,"width":0.5,"height":0.1},"zIndex":1,"text":"TEL: 0532 ??? ?? ??","textConfidence":0.35,"strategy":"text","confidence":0.5}]""",
            confidence: 0.55))).AnalyzeAsync(new ReferenceAnalysisRequest { Reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng()) });

        var analysis = result.Analysis!;
        Assert.True(analysis.Elements[0].TextUncertain);
        Assert.Equal(0.35, analysis.Elements[0].TextConfidence);
        Assert.Equal("ref_001 öğesindeki metin net okunamadı (“TEL: 0532 ??? ?? ??”); uygulamadan önce kontrol edin.", Assert.Single(analysis.Warnings));
        Assert.Equal(0.55, analysis.Confidence);
    }

    [Fact]
    public async Task Tables_logos_and_z_order_are_represented_structurally()
    {
        var analysis = await ReferenceFixtures.Analyze("""
            [{"key":"t","kind":"table","label":"Liste","bounds":{"x":0.1,"y":0.4,"width":0.8,"height":0.4},"zIndex":9,"table":{"rows":3,"columns":4,"cells":[["No","Ad","Soyad","Bölüm"],["1","Ayşe","Öztürk","Üretim"]],"cellAlignment":"center","hasHeaderRow":true,"mergedCellsNote":""},"strategy":"table","confidence":0.9},
             {"key":"l","kind":"logo","label":"Firma logosu","bounds":{"x":0.7,"y":0.05,"width":0.2,"height":0.1},"zIndex":3,"strategy":"needsUserAsset","assetHint":"acme logo","confidence":0.8},
             {"key":"bg","kind":"rectangle","label":"Zemin","bounds":{"x":0,"y":0,"width":1,"height":1},"zIndex":0,"fillColor":"#ffee00","strategy":"nativeShape","confidence":0.9},
             {"key":"p","kind":"pictogram","bounds":{"x":0.1,"y":0.05,"width":0.2,"height":0.2},"zIndex":5,"strategy":"","confidence":0.7}]
            """);

        Assert.Equal(["Zemin", "Firma logosu", null, "Liste"], analysis.Elements.Select(element => element.Label));       // back to front
        Assert.Equal([1, 2, 3, 4], analysis.Elements.Select(element => element.ZIndex));
        Assert.Equal("#FFEE00", analysis.Elements[0].FillColor);                                                         // colours normalised
        Assert.Equal((ReferenceElementKind.Logo, ReconstructionStrategy.NeedsUserAsset, "acme logo"), (analysis.Elements[1].Kind, analysis.Elements[1].Strategy, analysis.Elements[1].AssetHint));
        Assert.Equal(ReconstructionStrategy.UnsupportedComplexArtwork, analysis.Elements[2].Strategy);                   // sensible default
        var table = analysis.Elements[3].Table!;
        Assert.Equal((3, 4, true, "center"), (table.Rows, table.Columns, table.HasHeaderRow, table.CellAlignment));
        Assert.Equal("Öztürk", table.Cells[1][2]);
    }

    [Theory]
    [InlineData("""[{"key":"a","kind":"rectangle","bounds":{"x":-0.3,"y":0,"width":0.5,"height":0.5},"zIndex":1,"strategy":"nativeShape","confidence":1}]""", "geçersiz bir konum veya ölçü")]
    [InlineData("""[{"key":"a","kind":"rectangle","bounds":{"x":0.6,"y":0,"width":0.9,"height":0.5},"zIndex":1,"strategy":"nativeShape","confidence":1}]""", "geçersiz bir konum veya ölçü")]
    [InlineData("""[{"key":"a","kind":"ellipse","bounds":{"x":0.1,"y":0.1,"width":0,"height":0.5},"zIndex":1,"strategy":"nativeShape","confidence":1}]""", "geçersiz bir konum veya ölçü")]
    [InlineData("""[{"key":"a","kind":"ellipse","bounds":{"x":0.1,"y":0.1,"width":"wide","height":0.5},"zIndex":1,"strategy":"nativeShape","confidence":1}]""", "geçersiz bir konum veya ölçü")]
    [InlineData("""[{"key":"a","kind":"hologram","bounds":{"x":0.1,"y":0.1,"width":0.5,"height":0.5},"zIndex":1,"strategy":"nativeShape","confidence":1}]""", "tanınmayan bir öğe türü var (hologram)")]
    [InlineData("""[{"key":"a","kind":"rectangle","bounds":{"x":0.1,"y":0.1,"width":0.5,"height":0.5},"zIndex":1,"fillColor":"red","strategy":"nativeShape","confidence":1}]""", "geçersiz bir renk değeri var (red)")]
    [InlineData("""[{"key":"a","kind":"text","bounds":{"x":0.1,"y":0.1,"width":0.5,"height":0.1},"zIndex":1,"text":"","strategy":"text","confidence":1}]""", "geçersiz bir konum veya ölçü")]
    [InlineData("""[{"key":"a","kind":"polygon","bounds":{"x":0.1,"y":0.1,"width":0.5,"height":0.5},"zIndex":1,"points":[[0.1,0.1],[0.6,0.6]],"strategy":"nativeShape","confidence":1}]""", "geçersiz bir konum veya ölçü")]
    [InlineData("""[{"key":"a","kind":"table","bounds":{"x":0.1,"y":0.1,"width":0.5,"height":0.5},"zIndex":1,"table":{"rows":1,"columns":2,"cells":[["a","b","c"]]},"strategy":"table","confidence":1}]""", "geçersiz bir konum veya ölçü")]
    [InlineData("[]", "yeniden oluşturulabilecek bir öğe bulamadı")]
    public async Task Invalid_geometry_kinds_colours_and_tables_reject_the_analysis(string elements, string expected)
    {
        var answer = ReferenceFixtures.Answer(elements);
        var client = new FakeVisionClient(answer, answer);

        var result = await ReferenceFixtures.Analyzer(client).AnalyzeAsync(
            new ReferenceAnalysisRequest { Reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng()) });

        Assert.Equal(AiPlanningStatus.Invalid, result.Status);
        Assert.Null(result.Analysis);
        Assert.Contains(expected, result.UserMessage);
        Assert.Equal(2, client.Requests.Count); // one repair attempt, then stop
        Assert.Contains("previous answer was rejected", client.Requests[1].UserMessage);
    }

    [Theory]
    [InlineData("Maalesef bu görseli çözemedim.")]
    [InlineData("{\"status\":\"ready\",\"elements\":[{")]
    public async Task Malformed_answers_are_rejected(string answer)
    {
        var result = await ReferenceFixtures.Analyzer(new FakeVisionClient(answer, answer)).AnalyzeAsync(
            new ReferenceAnalysisRequest { Reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng()) });

        Assert.Equal(AiPlanningStatus.Invalid, result.Status);
        Assert.Equal("Yapay zekânın görsel analizi geçerli biçimde değildi. Lütfen yeniden deneyin.", result.UserMessage);
    }

    [Fact]
    public async Task Cannot_analyze_and_clarification_answers_are_passed_on()
    {
        var reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng());
        var cannot = await ReferenceFixtures.Analyzer(new FakeVisionClient(
            """{"status":"cannot_analyze","summary":"Görsel bir manzara fotoğrafı; tasarım öğesi içermiyor.","clarificationQuestion":"","confidence":0.9,"warnings":[],"appliedModifications":[],"backgroundColor":"","elements":[]}"""))
            .AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference });
        Assert.Equal(AiPlanningStatus.Invalid, cannot.Status);
        Assert.Equal("Bu görsel analiz edilemedi: Görsel bir manzara fotoğrafı; tasarım öğesi içermiyor.", cannot.UserMessage);

        var ask = await ReferenceFixtures.Analyzer(new FakeVisionClient(
            """{"status":"needs_clarification","summary":"","clarificationQuestion":"Sayfada iki ayrı tabela var. Hangisini yeniden oluşturayım: soldaki mi, sağdaki mi?","confidence":0.4,"warnings":[],"appliedModifications":[],"backgroundColor":"","elements":[]}"""))
            .AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference });
        Assert.Equal(AiPlanningStatus.NeedsClarification, ask.Status);
        Assert.StartsWith("Sayfada iki ayrı tabela var.", ask.ClarificationQuestion);
    }

    [Fact]
    public async Task Vector_references_are_reused_and_never_uploaded()
    {
        var client = new FakeVisionClient();
        var svg = await ReferenceFixtures.Analyzer(client).AnalyzeAsync(new ReferenceAnalysisRequest
        {
            Reference = ReferenceInput.FromFile(ReferenceFixtures.Svg("logo.svg", """width="500mm" height="700mm" """)),
        });
        var pdf = await ReferenceFixtures.Analyzer(client).AnalyzeAsync(new ReferenceAnalysisRequest
        {
            Reference = ReferenceInput.FromFile(ReferenceFixtures.Pdf("tek-sayfa.pdf", (1417.32, 1984.25))),
        });
        var cdr = await ReferenceFixtures.Analyzer(client).AnalyzeAsync(new ReferenceAnalysisRequest
        {
            Reference = ReferenceInput.FromFile(ReferenceFixtures.Write("eski.cdr", "not parsed")),
        });

        Assert.Empty(client.Requests);
        foreach (var result in new[] { svg, pdf, cdr })
        {
            Assert.True(result.IsReady, result.UserMessage);
            Assert.True(result.Analysis!.CanReuseVectorContent);
            Assert.Equal(ReconstructionStrategy.ReuseVector, Assert.Single(result.Analysis.Elements).Strategy);
            Assert.Contains("yeniden çizilmeyecek", result.UserMessage);
        }

        Assert.Equal(new PhysicalSize(500, 700), svg.Analysis!.PhysicalSize);
        Assert.Equal(500, pdf.Analysis!.PhysicalSize!.WidthMm, 1);
        Assert.Null(cdr.Analysis!.PhysicalSize); // without CorelDRAW a CDR's size is unknown, and no parser is faked
    }

    [Fact]
    public async Task Multi_page_pdf_asks_which_page_then_analyses_that_page()
    {
        var reference = ReferenceInput.FromFile(ReferenceFixtures.Pdf("katalog.pdf", (595, 842), (842, 595)));
        var client = new FakeVisionClient(ReferenceFixtures.Answer(
            """[{"key":"a","kind":"rectangle","bounds":{"x":0.1,"y":0.1,"width":0.8,"height":0.8},"zIndex":1,"outlineColor":"#000000","strategy":"nativeShape","confidence":0.9}]"""));

        var first = await ReferenceFixtures.Analyzer(client).AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference, UserRequest = "Bu PDF'deki tabelayı yeniden oluştur." });
        Assert.Equal(AiPlanningStatus.NeedsClarification, first.Status);
        Assert.Equal("Bu PDF 2 sayfa içeriyor. Hangi sayfayı yeniden oluşturayım? Örneğin: 2. sayfa", first.ClarificationQuestion);
        Assert.Empty(client.Requests);

        var second = await ReferenceFixtures.Analyzer(client).AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference, UserRequest = "İkinci sayfayı yeniden oluştur." });
        Assert.True(second.IsReady, second.UserMessage);
        Assert.Equal((2, 2), (second.Analysis!.PageNumber, second.Analysis.PageCount));
        Assert.Equal(297, second.Analysis.PhysicalSize!.WidthMm, 0);  // that page's real size
        Assert.False(second.Analysis.CanReuseVectorContent);
        Assert.Contains(second.Analysis.Warnings, warning => warning.Contains("özgün vektörleri kullanılmaz"));
        Assert.True(Assert.Single(client.Requests).Images[0].WidthPixels > client.Requests[0].Images[0].HeightPixels);
    }

    [Theory]
    [InlineData("2. sayfayı yap", 2)]
    [InlineData("sayfa 3", 3)]
    [InlineData("page 4 please", 4)]
    [InlineData("İkinci sayfayı mı yeniden oluşturayım", 2)]
    [InlineData("Bunun aynısını yap", null)]
    public void Page_numbers_are_found_in_requests(string text, int? expected) => Assert.Equal(expected, AiReferenceAnalyzer.FindPageNumber(text));

    [Fact]
    public async Task Provider_problems_are_reported_in_turkish_without_uploading_twice()
    {
        var reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng());

        var notConfigured = await ReferenceFixtures.Analyzer(null).AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference });
        Assert.Equal(AiPlanningStatus.ProviderError, notConfigured.Status);
        Assert.StartsWith("Yapay zekâ yapılandırılmamış.", notConfigured.UserMessage);

        var textOnly = await new AiReferenceAnalyzer(() => new FakeAiClient(), CompositeReferencePreviewRenderer.CreateDefault())
            .AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference });
        Assert.Equal("Seçili yapay zekâ sağlayıcısı görsel analizini desteklemiyor.", textOnly.UserMessage);

        var denied = new FakeVisionClient(new AiClientException(AiErrorKind.InvalidApiKey, "401"));
        var failed = await ReferenceFixtures.Analyzer(denied).AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference });
        Assert.Equal(AiPlanningStatus.ProviderError, failed.Status);
        Assert.StartsWith("API anahtarı geçersiz", failed.UserMessage);
        Assert.Single(denied.Requests);

        var flaky = new FakeVisionClient(new AiClientException(AiErrorKind.RateLimited, "429"), ReferenceFixtures.Answer(ReferenceFixtures.SignElements));
        Assert.True((await ReferenceFixtures.Analyzer(flaky).AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference })).IsReady);
        Assert.Equal(2, flaky.Requests.Count);
    }

    [Fact]
    public async Task Image_bytes_are_never_logged_even_in_debug_mode()
    {
        var lines = new List<string>();
        var logging = new LoggingAiClient(new FakeVisionClient(ReferenceFixtures.Answer(ReferenceFixtures.SignElements)), lines.Add, () => true);
        var reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng());

        var result = await new AiReferenceAnalyzer(() => logging, CompositeReferencePreviewRenderer.CreateDefault())
            .AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference });

        Assert.True(result.IsReady);
        Assert.True(logging.SupportsImages);
        var imageLine = Assert.Single(lines, line => line.StartsWith("AI request image:", StringComparison.Ordinal));
        Assert.Matches(@"yasak-levhasi\.png \(image/png, 500x700 px, \d+ bytes\)$", imageLine);
        var base64Start = Convert.ToBase64String(File.ReadAllBytes(reference.FilePath))[..40];
        Assert.DoesNotContain(lines, line => line.Contains(base64Start) || line.Contains("iVBORw0KGgo"));
    }

    [Fact]
    public void Vision_schema_lists_every_kind_and_strategy()
    {
        var schema = ReferenceVisionPrompt.BuildResponseSchema();

        Assert.All(ReferenceVisionPrompt.KindNames, kind => Assert.Contains($"\"{kind}\"", schema));
        Assert.All(ReferenceVisionPrompt.StrategyNames, strategy => Assert.Contains($"\"{strategy}\"", schema));
        Assert.DoesNotContain("\"unknown\"", schema);
        Assert.Contains("prohibitionSign", ReferenceVisionPrompt.Build());
        Assert.DoesNotContain("{KINDS}", ReferenceVisionPrompt.Build());
    }
}

public sealed class ReferenceReconstructionTests
{
    private static readonly ReferenceReconstructionPlanner Planner = new(new InstalledFontResolver(["Arial", "Arial Black", "Bahnschrift"]));

    private static async Task<(ReconstructionResult Result, ReferenceInput Reference)> Reconstruct(
        string elements, string request, ReferenceReconstructionPlanner? planner = null, IReadOnlyList<Asset>? assets = null, string? path = null)
    {
        var reference = ReferenceInput.FromFile(path ?? ReferenceFixtures.ProhibitionSignPng());
        var analysis = (await ReferenceFixtures.Analyzer(new FakeVisionClient(ReferenceFixtures.Answer(elements)))
            .AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference, UserRequest = request })).Analysis!;
        return ((planner ?? Planner).Plan(new ReconstructionRequest { Reference = reference, Analysis = analysis, UserRequest = request, Assets = assets ?? [] }), reference);
    }

    [Fact]
    public async Task End_to_end_sign_becomes_a_valid_plan_of_editable_objects()
    {
        var (result, _) = await Reconstruct(ReferenceFixtures.SignElements, "Bunun aynısını 500x700 mm olarak CorelDRAW'da yap.");

        Assert.True(result.IsReady, result.UserMessage);
        var plan = result.Plan!;
        Assert.True(plan.Validate().IsValid, plan.Validate().ToString());
        Assert.Equal(new PhysicalSize(500, 700), result.Size);
        Assert.Equal(DocumentTarget.NewDocument, plan.Target);

        // Correct page size.
        var document = Assert.IsType<CreateDocumentAction>(plan.Actions[0]);
        Assert.Equal((500, 700), (document.WidthMm, document.HeightMm));

        // The frame: 0.04/0.03/0.92/0.94 of 500 x 700 mm.
        var frame = Assert.IsType<CreateRectangleAction>(plan.Actions[1]);
        Assert.Equal((20, 21, 460, 658), (Math.Round(frame.XMm), Math.Round(frame.YMm), Math.Round(frame.WidthMm), Math.Round(frame.HeightMm)));
        Assert.Equal(("#FFFFFF", "#000000", 6d, 15d), (frame.FillColor, frame.OutlineColor, frame.OutlineWidthMm, frame.CornerRadiusMm));

        // Prohibition geometry: a red ring and a red diagonal bar, grouped as one editable component.
        var ring = plan.Actions.OfType<CreateEllipseAction>().Single();
        var bar = plan.Actions.OfType<CreateLineAction>().Single();
        Assert.Equal(("#D8202A", 30d), (ring.OutlineColor, ring.OutlineWidthMm));
        Assert.Null(ring.FillColor);
        Assert.Equal((115, 85, 270, 270), (Math.Round(ring.XMm), Math.Round(ring.YMm), Math.Round(ring.WidthMm), Math.Round(ring.HeightMm))); // outer edge on 100..400 mm
        Assert.Equal(("#D8202A", 30d), (bar.OutlineColor, bar.OutlineWidthMm));
        Assert.True(bar.X1Mm < bar.X2Mm && bar.Y1Mm < bar.Y2Mm);
        Assert.Equal(250, (bar.X1Mm + bar.X2Mm) / 2, 3); // centred on the ring
        Assert.Contains(plan.Actions.OfType<GroupAction>(), group => group.Targets.SequenceEqual(["@ref_002", "@ref_002_bar"]));

        // Three editable texts, in the right place, red and bold.
        var texts = plan.Actions.OfType<CreateTextAction>().ToList();
        Assert.Equal(["BU ALANA", "GİRMEK", "YASAKTIR"], texts.Select(text => text.Text));
        Assert.All(texts, text => Assert.Equal(("#D8202A", true, "Arial", PositionAnchor.Center, TextAlignment.Center), (text.FillColor, text.Bold, text.FontFamily, text.Anchor, text.Alignment)));
        Assert.Equal((250, 423.5), (texts[0].XMm, texts[0].YMm));       // centre of 0.22..0.78 × 0.57..0.64
        Assert.True(texts[0].YMm < texts[1].YMm && texts[1].YMm < texts[2].YMm);
        Assert.InRange(texts[0].FontSizePt!.Value, 150, 250);            // a 49 mm tall capital line
        var fits = plan.Actions.OfType<ResizeAction>().ToList();
        Assert.Equal([280d, 220d, 300d], fits.Select(fit => fit.WidthMm));  // each text is fitted to its analysed width
        Assert.All(fits, fit => Assert.True(fit.KeepAspectRatio));

        // Useful grouping: the three lines together — and never the whole document.
        var block = plan.Actions.OfType<GroupAction>().Single(group => group.Name == "Yazı bloğu");
        Assert.Equal(["@ref_003", "@ref_004", "@ref_005"], block.Targets);
        Assert.DoesNotContain(plan.Actions.OfType<GroupAction>(), group => group.Targets.Count >= 5);

        // Back-to-front creation order, with no reordering or destructive actions and nothing imported.
        var created = plan.Actions.Where(action => action.CreatesObjects && action is not GroupAction).Select(action => action.Id).ToList();
        Assert.Equal(["ref_001", "ref_002", "ref_002_bar", "ref_003", "ref_004", "ref_005"], created);
        Assert.DoesNotContain(plan.Actions, action => action is BringToFrontAction or SendToBackAction or DeleteAction or ImportFileAction);
        Assert.False(plan.HasDestructiveActions);
        Assert.Equal("15 işlem hazırlandı: referans 500 × 700 mm boyutunda yeniden oluşturulacak.", result.UserMessage);
        Assert.Equal(plan.ToJson(), AutomationPlan.FromJson(plan.ToJson()).ToJson());
    }

    [Fact]
    public async Task Missing_physical_size_asks_instead_of_guessing()
    {
        var (result, _) = await Reconstruct(ReferenceFixtures.SignElements, "Bu JPG'deki tasarımı vektörel ve düzenlenebilir olarak oluştur.");

        Assert.Equal(AiPlanningStatus.NeedsClarification, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal("Bu tasarımın gerçek ölçüsü nedir? Örneğin 500 × 700 mm.", result.ClarificationQuestion);
    }

    [Theory]
    [InlineData("50x70 cm yap", 500, 700, 20, 460)]
    [InlineData("1000x1400 mm yap", 1000, 1400, 40, 920)]
    [InlineData("250 x 350 mm", 250, 350, 10, 230)]
    public async Task Explicit_size_scales_all_geometry_exactly(string request, double width, double height, double frameX, double frameWidth)
    {
        var (result, _) = await Reconstruct(ReferenceFixtures.SignElements, request);

        Assert.Equal(new PhysicalSize(width, height), result.Size);
        var frame = result.Plan!.Actions.OfType<CreateRectangleAction>().First();
        Assert.Equal((frameX, frameWidth), (Math.Round(frame.XMm, 3), Math.Round(frame.WidthMm, 3)));
        Assert.Empty(result.Warnings.Where(warning => warning.Contains("en-boy oranı")));
    }

    [Fact]
    public async Task A_different_aspect_ratio_is_honoured_and_reported()
    {
        var (result, _) = await Reconstruct(ReferenceFixtures.SignElements, "Bunu 700x500 mm yap");

        Assert.True(result.IsReady);
        Assert.Equal((700, 500), (((CreateDocumentAction)result.Plan!.Actions[0]).WidthMm, ((CreateDocumentAction)result.Plan.Actions[0]).HeightMm));
        Assert.Contains(result.Warnings, warning => warning.StartsWith("İstenen ölçünün en-boy oranı (1,40) referansınkinden (0,71) farklı", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_users_stated_size_beats_the_files_own_size()
    {
        var reference = ReferenceInput.FromFile(ReferenceFixtures.Pdf("a4.pdf", (595.28, 841.89)));
        var analysis = (await ReferenceFixtures.Analyzer(null).AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference })).Analysis!;

        var fromFile = Planner.Plan(new ReconstructionRequest { Reference = reference, Analysis = analysis, UserRequest = "Bu PDF'deki tabelayı Corel'de yeniden oluştur." });
        var stated = Planner.Plan(new ReconstructionRequest { Reference = reference, Analysis = analysis, UserRequest = "Bunu 420x594 mm yap" });

        Assert.Equal(210, fromFile.Size!.WidthMm, 0);
        Assert.Equal(297, fromFile.Size.HeightMm, 0);
        Assert.Equal(new PhysicalSize(420, 594), stated.Size);
    }

    [Fact]
    public async Task Vector_references_are_imported_not_redrawn()
    {
        var reference = ReferenceInput.FromFile(ReferenceFixtures.Svg("tabela.svg", """width="500mm" height="700mm" """));
        var analysis = (await ReferenceFixtures.Analyzer(null).AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference })).Analysis!;

        var result = Planner.Plan(new ReconstructionRequest { Reference = reference, Analysis = analysis, UserRequest = "Bunun aynısını yap" });

        Assert.True(result.IsReady, result.UserMessage);
        Assert.Equal(["createDocument", "importFile", "align"], result.Plan!.Actions.Select(action => action.TypeName));
        Assert.Empty(result.Warnings); // same proportions: nothing to report

        var square = Planner.Plan(new ReconstructionRequest { Reference = reference, Analysis = analysis, UserRequest = "Bunu 600x600 mm yap" });
        Assert.Contains(square.Warnings, warning => warning.Contains("oranı korunarak sayfaya sığdırıldı"));
        Assert.Equal((600d, 600d), (((ImportFileAction)square.Plan!.Actions[1]).FitWidthMm, ((ImportFileAction)square.Plan.Actions[1]).FitHeightMm));
        var import = (ImportFileAction)result.Plan.Actions[1];
        Assert.Equal((reference.FilePath, 0d, 0d, 500d, 700d), (import.FilePath, import.XMm, import.YMm, import.FitWidthMm, import.FitHeightMm));
        Assert.DoesNotContain(result.Plan.Actions, action => action is CreateRectangleAction or CreateTextAction);
    }

    [Fact]
    public async Task Tables_become_native_editable_tables()
    {
        var (result, _) = await Reconstruct("""
            [{"key":"t","kind":"table","label":"Liste","bounds":{"x":0.1,"y":0.2,"width":0.8,"height":0.4},"zIndex":1,"table":{"rows":3,"columns":4,"cells":[["No","Ad","Soyad","Bölüm"],["1","Ayşe","Öztürk","Üretim"]],"cellAlignment":"center","hasHeaderRow":true,"mergedCellsNote":"Son satır birleştirilmiş olabilir"},"strategy":"table","confidence":0.9}]
            """, "Bu referanstaki tabloyu editable olarak yap, 400x300 mm");

        var table = Assert.IsType<CreateTableAction>(result.Plan!.Actions[1]);
        Assert.Equal((4, 3, TextAlignment.Center), (table.Columns, table.Rows, table.CellAlignment));
        Assert.Equal((40, 60, 320, 120), (table.XMm, table.YMm, table.WidthMm, table.HeightMm));
        Assert.Equal("Öztürk", table.Cells![1][2]);
        Assert.Contains(result.Warnings, warning => warning.Contains("birleştirilmiş hücreler") && warning.Contains("Son satır"));
        Assert.DoesNotContain(result.Plan.Actions, action => action is ImportFileAction);
    }

    [Fact]
    public async Task Polygons_lines_backgrounds_and_rotation_use_native_shapes()
    {
        var (result, _) = await Reconstruct("""
            [{"key":"tri","kind":"polygon","label":"Uyarı üçgeni","bounds":{"x":0.2,"y":0.1,"width":0.6,"height":0.4},"zIndex":1,"points":[[0.5,0.1],[0.8,0.5],[0.2,0.5]],"fillColor":"#FFFF00","outlineColor":"#000000","outlineWidthRatio":0.02,"strategy":"nativeShape","confidence":0.9},
             {"key":"ln","kind":"line","bounds":{"x":0.1,"y":0.6,"width":0.8,"height":0},"zIndex":2,"line":{"x1":0.1,"y1":0.6,"x2":0.9,"y2":0.6},"outlineColor":"#0000FF","outlineWidthRatio":0.01,"strategy":"nativeShape","confidence":0.9},
             {"key":"ar","kind":"arrow","bounds":{"x":0.1,"y":0.7,"width":0.3,"height":0.1},"zIndex":3,"strategy":"nativeShape","confidence":0.8},
             {"key":"rc","kind":"rectangle","label":"Eğik kutu","bounds":{"x":0.6,"y":0.7,"width":0.2,"height":0.1},"zIndex":4,"rotation":15,"fillColor":"#00A651","strategy":"nativeShape","confidence":0.9}]
            """, "200x200 mm yap", path: ReferenceFixtures.SolidImage(200, 200, "kare.png"));
        var plan = result.Plan!;
        Assert.True(result.IsReady, result.UserMessage);

        var triangle = plan.Actions.OfType<CreatePolygonAction>().Single();
        Assert.Equal([[100d, 20d], [160d, 100d], [40d, 100d]], triangle.PointsMm.Select(point => point.ToArray()));
        Assert.Equal(("#FFFF00", "#000000", 4d, true), (triangle.FillColor, triangle.OutlineColor, triangle.OutlineWidthMm, triangle.Closed));

        var lines = plan.Actions.OfType<CreateLineAction>().ToList();
        Assert.Equal((20d, 120d, 180d, 120d, "#0000FF", 2d), (lines[0].X1Mm, lines[0].Y1Mm, lines[0].X2Mm, lines[0].Y2Mm, lines[0].OutlineColor, lines[0].OutlineWidthMm));
        Assert.Contains(result.Warnings, warning => warning.StartsWith("ref_003: ok, uçsuz bir çizgi", StringComparison.Ordinal));

        var rotate = plan.Actions.OfType<RotateAction>().Single();
        Assert.Equal(["@ref_004"], rotate.Targets);
        Assert.Equal(15d, rotate.AngleDegrees);
        var filled = plan.Actions.OfType<CreateRectangleAction>().Single();
        Assert.Equal(("#00A651", 0d), (filled.FillColor, filled.OutlineWidthMm)); // no outline was seen, so none is drawn
    }

    [Fact]
    public async Task A_coloured_background_is_drawn_first()
    {
        var reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng());
        var analysis = (await ReferenceFixtures.Analyzer(new FakeVisionClient(ReferenceFixtures.Answer(
            """[{"key":"a","kind":"text","bounds":{"x":0.1,"y":0.4,"width":0.8,"height":0.1},"zIndex":1,"text":"DİKKAT","textConfidence":0.9,"fillColor":"#000000","strategy":"text","confidence":0.9}]""",
            background: "#FFD400"))).AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference })).Analysis!;

        var plan = Planner.Plan(new ReconstructionRequest { Reference = reference, Analysis = analysis, UserRequest = "500x700 mm" }).Plan!;

        var background = Assert.IsType<CreateRectangleAction>(plan.Actions[1]);
        Assert.Equal((0d, 0d, 500d, 700d, "#FFD400", "Arka plan"), (background.XMm, background.YMm, background.WidthMm, background.HeightMm, background.FillColor, background.Name));
        Assert.IsType<CreateTextAction>(plan.Actions[2]);
    }

    private const string LogoAndArt = """
        [{"key":"l","kind":"logo","label":"Firma logosu","bounds":{"x":0.7,"y":0.05,"width":0.2,"height":0.1},"zIndex":1,"strategy":"needsUserAsset","assetHint":"acme logo","confidence":0.8},
         {"key":"p","kind":"pictogram","label":"El","bounds":{"x":0.3,"y":0.3,"width":0.4,"height":0.3},"zIndex":2,"strategy":"unsupportedComplexArtwork","confidence":0.7},
         {"key":"f","kind":"photo","label":"Ürün fotoğrafı","bounds":{"x":0.1,"y":0.7,"width":0.3,"height":0.2},"zIndex":3,"strategy":"importImage","confidence":0.9},
         {"key":"s","kind":"icon","label":"Sigara içilmez","bounds":{"x":0.5,"y":0.7,"width":0.2,"height":0.14},"zIndex":4,"strategy":"useAsset","assetHint":"sigara yasak","confidence":0.8}]
        """;

    [Fact]
    public async Task Logos_and_complex_art_are_handled_honestly_with_placeholders()
    {
        var (result, _) = await Reconstruct(LogoAndArt, "500x700 mm olarak yap");

        Assert.True(result.IsReady, result.UserMessage);
        var placeholders = result.Plan!.Actions.OfType<CreateRectangleAction>().ToList();
        Assert.Equal(
            ["YER TUTUCU: Firma logosu", "YER TUTUCU: El", "YER TUTUCU: Ürün fotoğrafı", "YER TUTUCU: Sigara içilmez"],
            placeholders.Select(placeholder => placeholder.Name));
        Assert.All(placeholders, placeholder => Assert.Equal(("#FF00FF", null), (placeholder.OutlineColor, placeholder.FillColor)));
        Assert.Equal((350, 35, 100, 70), (placeholders[0].XMm, placeholders[0].YMm, placeholders[0].WidthMm, Math.Round(placeholders[0].HeightMm)));
        Assert.Contains("ref_001 (Firma logosu) için kaynak dosya gerekli; yerine bir yer tutucu çerçeve eklendi.", result.Warnings);
        Assert.Contains("ref_002 (El) güvenilir biçimde yeniden çizilemeyecek kadar karmaşık; yerine bir yer tutucu çerçeve eklendi.", result.Warnings);

        // Nothing was drawn as a pile of invented shapes, and the whole reference was not pasted in as a bitmap.
        Assert.DoesNotContain(result.Plan.Actions, action => action is ImportFileAction or CreatePolygonAction or CreateEllipseAction);
    }

    [Fact]
    public async Task Assets_and_cropped_bitmaps_are_used_when_available()
    {
        using var store = new ReferenceTempStore();
        var planner = new ReferenceReconstructionPlanner(new InstalledFontResolver(["Arial"]), new SkiaReferenceImageCropper(store));
        Asset[] assets =
        [
            new() { Id = "sigara", Name = "Sigara içilmez", Category = "Yasak", FilePath = @"C:\varlik\sigara-yasak.svg", FileType = "svg", Tags = ["sigara", "yasak"] },
            new() { Id = "baska", Name = "Yangın tüpü", Category = "Bilgi", FilePath = @"C:\varlik\yangin.svg", FileType = "svg" },
        ];

        var (result, reference) = await Reconstruct(LogoAndArt, "Logoyu görsel olarak kullan, 500x700 mm yap", planner, assets);

        var imports = result.Plan!.Actions.OfType<ImportFileAction>().ToList();
        Assert.Equal(2, imports.Count);
        var photo = imports.Single(import => import.Id == "ref_003");
        Assert.StartsWith(store.Folder, photo.FilePath);                       // a crop of just that component…
        Assert.NotEqual(reference.FilePath, photo.FilePath);                   // …never the whole reference
        Assert.Equal((50d, 490d, 150d, 140d), (photo.XMm, photo.YMm, photo.FitWidthMm, photo.FitHeightMm));
        Assert.Equal(@"C:\varlik\sigara-yasak.svg", imports.Single(import => import.Id == "ref_004").FilePath);
        Assert.Contains(result.Warnings, warning => warning.Contains("varlık kütüphanesindeki “Sigara içilmez” kullanılacak"));
        Assert.Contains(result.Warnings, warning => warning.StartsWith("ref_003 (Ürün fotoğrafı) referanstaki görselden kesilip", StringComparison.Ordinal));
        Assert.Equal(2, result.Plan.Actions.OfType<CreateRectangleAction>().Count()); // logo and pictogram stay honest placeholders
    }

    [Fact]
    public async Task Requested_modifications_arrive_through_the_analysis()
    {
        var reference = ReferenceInput.FromFile(ReferenceFixtures.ProhibitionSignPng());
        const string Request = "Bunun aynısını 500x700 mm yap ama alttaki YASAKTIR yazısını GİRİLMEZ olarak değiştir.";
        var client = new FakeVisionClient(ReferenceFixtures.Answer(
            ReferenceFixtures.SignElements.Replace("\"YASAKTIR\"", "\"GİRİLMEZ\"", StringComparison.Ordinal), modifications: """["YASAKTIR → GİRİLMEZ"]"""));

        var analysis = (await ReferenceFixtures.Analyzer(client).AnalyzeAsync(new ReferenceAnalysisRequest { Reference = reference, UserRequest = Request })).Analysis!;
        var plan = Planner.Plan(new ReconstructionRequest { Reference = reference, Analysis = analysis, UserRequest = Request }).Plan!;

        Assert.Contains(Request, client.Requests[0].UserMessage);
        Assert.Equal(["YASAKTIR → GİRİLMEZ"], analysis.AppliedModifications);
        Assert.Equal(["BU ALANA", "GİRMEK", "GİRİLMEZ"], plan.Actions.OfType<CreateTextAction>().Select(text => text.Text));
        Assert.Equal(Request, plan.UserRequest);
    }

    [Fact]
    public async Task Fonts_are_never_claimed_exact_without_confidence()
    {
        var (result, _) = await Reconstruct("""
            [{"key":"a","kind":"text","bounds":{"x":0.1,"y":0.1,"width":0.8,"height":0.1},"zIndex":1,"text":"KESİN","textConfidence":0.9,"fontFamilyGuess":"Arial Black","fontConfidence":0.9,"strategy":"text","confidence":0.9},
             {"key":"b","kind":"text","bounds":{"x":0.1,"y":0.3,"width":0.8,"height":0.1},"zIndex":2,"text":"OLASI","textConfidence":0.9,"fontFamilyGuess":"Arial","fontConfidence":0.4,"strategy":"text","confidence":0.9},
             {"key":"c","kind":"text","bounds":{"x":0.1,"y":0.5,"width":0.8,"height":0.1},"zIndex":3,"text":"BENZER","textConfidence":0.9,"fontFamilyGuess":"DIN 1451","fontConfidence":0.9,"strategy":"text","confidence":0.9},
             {"key":"d","kind":"text","bounds":{"x":0.1,"y":0.7,"width":0.8,"height":0.1},"zIndex":4,"text":"YEDEK","textConfidence":0.9,"fontFamilyGuess":"Özel Firma Fontu","fontConfidence":0.9,"strategy":"text","confidence":0.9},
             {"key":"e","kind":"text","bounds":{"x":0.1,"y":0.85,"width":0.8,"height":0.06},"zIndex":5,"text":"ممنوع الدخول","textConfidence":0.9,"direction":"rtl","strategy":"text","confidence":0.9}]
            """, "500x700 mm");

        Assert.Equal(["Arial Black", "Arial", "Bahnschrift", "Arial", "Arial"], result.Plan!.Actions.OfType<CreateTextAction>().Select(text => text.FontFamily));
        Assert.DoesNotContain(result.Warnings, warning => warning.StartsWith("ref_001", StringComparison.Ordinal));             // exact: nothing to report
        Assert.Contains("ref_002: yazı tipi yaklaşık eşleştirildi (Arial).", result.Warnings);                                  // installed but unsure
        Assert.Contains("ref_003: yazı tipi yaklaşık eşleştirildi (Bahnschrift).", result.Warnings);                            // known look-alike
        Assert.Contains("ref_004: yazı tipi belirlenemedi; Arial kullanıldı.", result.Warnings);                                // fallback
        Assert.Contains(result.Warnings, warning => warning.StartsWith("ref_005: sağdan sola yazılan metin", StringComparison.Ordinal));
        Assert.Equal("ممنوع الدخول", result.Plan.Actions.OfType<CreateTextAction>().Last().Text);

        var resolver = new InstalledFontResolver(["Arial"]);
        Assert.Equal(new FontResolution("Arial", FontMatchKind.Exact), resolver.Resolve("arial", 0.95));
        Assert.Equal(new FontResolution("Arial", FontMatchKind.Likely), resolver.Resolve("Arial", null));
        Assert.Equal(new FontResolution("Arial", FontMatchKind.Likely), resolver.Resolve("Helvetica", 0.99));
        Assert.Equal(new FontResolution("Arial", FontMatchKind.Fallback), resolver.Resolve(null, 0.99));
    }

    [Fact]
    public async Task Reconstruction_plans_contain_only_known_actions_and_plan_local_targets()
    {
        var (result, _) = await Reconstruct(ReferenceFixtures.SignElements, "500x700 mm");

        Assert.All(result.Plan!.Actions, action => Assert.Contains(action.TypeName, ActionTypes.AllNames));
        var targets = result.Plan.Actions.SelectMany(action => action.TargetRefs).ToList();
        Assert.NotEmpty(targets);
        Assert.All(targets, target => Assert.True(TargetRef.IsActionRef(target), target)); // never an invented shape_NNN
        var ids = result.Plan.Actions.Select(action => action.Id).ToHashSet();
        Assert.All(targets, target => Assert.Contains(TargetRef.ActionId(target), ids));
        Assert.Equal(["ref_002", "ref_002_bar", "ref_002_sign"], result.ElementActions["ref_002"]);
    }

    [Fact]
    public async Task The_plan_runs_through_the_existing_execution_engine()
    {
        var (result, _) = await Reconstruct(ReferenceFixtures.SignElements, "500x700 mm");
        var session = new FakeAutomationSession();

        var execution = PlanExecutionEngine.Run(result.Plan!, session);

        Assert.True(execution.Success, execution.Summary);
        Assert.Equal(result.Plan!.Actions.Count, session.Executed.Count);
    }
}
