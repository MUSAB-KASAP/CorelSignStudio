using CorelSignStudio.Domain;
using CorelSignStudio.Templates;

namespace CorelSignStudio.Tests;

public sealed class TemplateTests
{
    private readonly ProhibitionSignTemplate _template = new();

    [Fact]
    public void Prohibition_template_creates_a_500_by_700_design()
    {
        var design = CreateDefaultDesign();
        Assert.Equal(500, design.WidthMm);
        Assert.Equal(700, design.HeightMm);
        design.Validate();
    }

    [Fact]
    public void Prohibition_template_contains_required_text_and_svg_asset()
    {
        var design = CreateDefaultDesign();
        var texts = design.Elements.OfType<TextElement>().Select(element => element.Text).ToArray();
        Assert.Equal(["BU ALANA", "GİRMEK", "YASAKTIR"], texts);
        Assert.Contains(design.Elements, element => element is SvgElement { AssetKey: "no-entry-hand" });
    }

    [Fact]
    public void Prohibition_template_keeps_every_element_inside_page_bounds()
    {
        var design = CreateDefaultDesign();
        Assert.All(design.Elements, element =>
        {
            Assert.InRange(element.XMm, 0, design.WidthMm);
            Assert.InRange(element.YMm, 0, design.HeightMm);
            Assert.True(element.XMm + element.WidthMm <= design.WidthMm + 0.001);
            Assert.True(element.YMm + element.HeightMm <= design.HeightMm + 0.001);
        });
    }

    private DesignSpec CreateDefaultDesign() => _template.CreateDesign(
        new SignTemplateParameters(500, 700, "BU ALANA", "GİRMEK", "YASAKTIR", "no-entry-hand"));
}

