using CorelSignStudio.Domain;

namespace CorelSignStudio.Templates;

public sealed class ProhibitionSignTemplate : ISignTemplate
{
    private const double BaseWidthMm = 500;
    private const double BaseHeightMm = 700;

    public string Id => "prohibition-no-entry";
    public string Name => "BU ALANA / GİRMEK / YASAKTIR";
    public SignCategory Category => SignCategory.Prohibition;
    public double DefaultWidthMm => BaseWidthMm;
    public double DefaultHeightMm => BaseHeightMm;
    public string DefaultPictogramAssetId => "no-entry-hand";

    public DesignSpec CreateDesign(SignTemplateParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.WidthMm <= 0 || parameters.HeightMm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Template dimensions must be positive.");
        }

        var scaleX = parameters.WidthMm / BaseWidthMm;
        var scaleY = parameters.HeightMm / BaseHeightMm;
        var scale = Math.Min(scaleX, scaleY);
        double X(double value) => value * scaleX;
        double Y(double value) => value * scaleY;
        double S(double value) => value * scale;

        var design = new DesignSpec
        {
            WidthMm = parameters.WidthMm,
            HeightMm = parameters.HeightMm,
            Elements =
            [
                new RectangleElement
                {
                    Id = "background", XMm = 0, YMm = 0,
                    WidthMm = parameters.WidthMm, HeightMm = parameters.HeightMm,
                    ZIndex = 0, Fill = new FillStyle("#FFFFFF"),
                },
                new RectangleElement
                {
                    Id = "outer-border", XMm = X(10), YMm = Y(10),
                    WidthMm = X(480), HeightMm = Y(680), ZIndex = 1,
                    Stroke = new StrokeStyle("#111111", S(2.4)),
                },
                new SvgElement
                {
                    Id = "prohibition-symbol", XMm = X(74), YMm = Y(42),
                    WidthMm = X(352), HeightMm = Y(352), ZIndex = 2,
                    AssetKey = parameters.PictogramAssetId,
                },
                CreateText("text-1", parameters.Text1, X(36), Y(407), X(428), Y(67), S(57), 3),
                CreateText("text-2", parameters.Text2, X(36), Y(478), X(428), Y(82), S(73), 4),
                CreateText("text-3", parameters.Text3, X(36), Y(563), X(428), Y(88), S(73), 5),
            ],
        };

        design.Validate();
        return design;
    }

    private static TextElement CreateText(string id, string text, double x, double y, double width, double height, double fontSizePt, int zIndex) =>
        new()
        {
            Id = id, XMm = x, YMm = y, WidthMm = width, HeightMm = height, ZIndex = zIndex,
            Text = text.Trim(), FontFamily = "Arial", FontSizePt = fontSizePt,
            FontWeight = TextFontWeight.Black,
            HorizontalAlignment = ElementHorizontalAlignment.Center,
            VerticalAlignment = ElementVerticalAlignment.Center,
            Fill = new FillStyle("#111111"),
        };
}

