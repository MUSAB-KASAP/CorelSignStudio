using CorelSignStudio.Domain;

namespace CorelSignStudio.Tests;

public sealed class DesignSpecTests
{
    [Fact]
    public void Validate_accepts_all_supported_element_types()
    {
        var design = new DesignSpec
        {
            WidthMm = 500,
            HeightMm = 700,
            Elements =
            [
                new TextElement { Id = "text", XMm = 10, YMm = 10, WidthMm = 100, HeightMm = 30, Text = "TEST", FontFamily = "Arial", FontSizePt = 24 },
                new RectangleElement { Id = "rectangle", XMm = 10, YMm = 50, WidthMm = 100, HeightMm = 50 },
                new EllipseElement { Id = "ellipse", XMm = 10, YMm = 120, WidthMm = 50, HeightMm = 50 },
                new LineElement { Id = "line", XMm = 0, YMm = 0, WidthMm = 50, HeightMm = 50 },
                new SvgElement { Id = "svg", XMm = 10, YMm = 200, AssetKey = "warning", WidthMm = 80, HeightMm = 80 },
                new ImageElement { Id = "image", XMm = 10, YMm = 300, AssetKey = "photo", WidthMm = 80, HeightMm = 80 },
            ],
        };

        design.Validate();
    }

    [Fact]
    public void Validate_rejects_duplicate_element_ids()
    {
        var design = new DesignSpec
        {
            WidthMm = 500,
            HeightMm = 700,
            Elements =
            [
                new RectangleElement { Id = "same", XMm = 0, YMm = 0, WidthMm = 10, HeightMm = 10 },
                new EllipseElement { Id = "same", XMm = 20, YMm = 20, WidthMm = 10, HeightMm = 10 },
            ],
        };

        Assert.Throws<DesignValidationException>(design.Validate);
    }

    [Fact]
    public void Validate_rejects_elements_outside_page_bounds()
    {
        var design = new DesignSpec
        {
            WidthMm = 500,
            HeightMm = 700,
            Elements =
            [new RectangleElement { Id = "outside", XMm = 490, YMm = 10, WidthMm = 20, HeightMm = 20 }],
        };

        Assert.Throws<DesignValidationException>(design.Validate);
    }
}

