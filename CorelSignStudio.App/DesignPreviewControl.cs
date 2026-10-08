using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using CorelSignStudio.Domain;

namespace CorelSignStudio.App;

public sealed class DesignPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty DesignProperty = DependencyProperty.Register(
        nameof(Design),
        typeof(DesignSpec),
        typeof(DesignPreviewControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public DesignSpec? Design
    {
        get => (DesignSpec?)GetValue(DesignProperty);
        set => SetValue(DesignProperty, value);
    }

    public IDesignAssetResolver? AssetResolver { get; set; }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(237, 240, 243)), null, new Rect(RenderSize));

        if (Design is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        const double padding = 28;
        var scale = Math.Min(
            Math.Max(0, ActualWidth - padding * 2) / Design.WidthMm,
            Math.Max(0, ActualHeight - padding * 2) / Design.HeightMm);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return;
        }

        var pageWidth = Design.WidthMm * scale;
        var pageHeight = Design.HeightMm * scale;
        var offsetX = (ActualWidth - pageWidth) / 2;
        var offsetY = (ActualHeight - pageHeight) / 2;
        var pageRect = new Rect(offsetX, offsetY, pageWidth, pageHeight);

        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 23, 33, 43)), null,
            new Rect(pageRect.X + 7, pageRect.Y + 9, pageRect.Width, pageRect.Height));
        drawingContext.DrawRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(199, 205, 212)), 1), pageRect);
        drawingContext.PushClip(new RectangleGeometry(pageRect));

        foreach (var element in Design.Elements.Where(item => item.Visible).OrderBy(item => item.ZIndex))
        {
            DrawElement(drawingContext, element, scale, offsetX, offsetY);
        }

        drawingContext.Pop();
    }

    private void DrawElement(DrawingContext context, DesignElement element, double scale, double offsetX, double offsetY)
    {
        var x = offsetX + element.XMm * scale;
        var y = offsetY + element.YMm * scale;
        var width = element.WidthMm * scale;
        var height = element.HeightMm * scale;
        var rect = new Rect(x, y, width, height);

        context.PushOpacity(element.Opacity);
        if (element.RotationDegrees != 0)
        {
            context.PushTransform(new RotateTransform(element.RotationDegrees, rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
        }

        var fill = BrushFor(element.Fill);
        var stroke = PenFor(element.Stroke, scale);
        switch (element)
        {
            case RectangleElement rectangle:
                context.DrawRoundedRectangle(fill, stroke, rect, rectangle.CornerRadiusMm * scale, rectangle.CornerRadiusMm * scale);
                break;
            case EllipseElement:
                context.DrawEllipse(fill, stroke, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
                break;
            case LineElement:
                context.DrawLine(stroke ?? new Pen(Brushes.Black, 1), new Point(x, y), new Point(x + width, y + height));
                break;
            case TextElement text:
                DrawText(context, text, rect, scale);
                break;
            case SvgElement svg:
                DrawSvg(context, svg, rect);
                break;
            case ImageElement image:
                DrawImage(context, image, rect);
                break;
        }

        if (element.RotationDegrees != 0)
        {
            context.Pop();
        }

        context.Pop();
    }

    private void DrawText(DrawingContext context, TextElement text, Rect rect, double scale)
    {
        var weight = text.FontWeight switch
        {
            TextFontWeight.Normal => FontWeights.Normal,
            TextFontWeight.SemiBold => FontWeights.SemiBold,
            TextFontWeight.Bold => FontWeights.Bold,
            _ => FontWeights.Black,
        };
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fontSize = text.FontSizePt * 96d / 72d * scale;
        var typeface = new Typeface(new FontFamily(text.FontFamily), FontStyles.Normal, weight, FontStretches.Normal);
        var brush = BrushFor(text.Fill) ?? Brushes.Black;
        FormattedText Build(double size) => new(
            text.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            pixelsPerDip);

        var formatted = Build(fontSize);

        var naturalWidth = formatted.WidthIncludingTrailingWhitespace;
        var naturalHeight = formatted.Height;
        if (naturalHeight > rect.Height || naturalWidth > rect.Width)
        {
            var fit = Math.Min(rect.Width / Math.Max(1, naturalWidth), rect.Height / Math.Max(1, naturalHeight));
            formatted = Build(fontSize * fit * 0.98);
        }

        var y = text.VerticalAlignment switch
        {
            ElementVerticalAlignment.Top => rect.Top,
            ElementVerticalAlignment.Bottom => rect.Bottom - formatted.Height,
            _ => rect.Top + (rect.Height - formatted.Height) / 2,
        };
        var x = text.HorizontalAlignment switch
        {
            ElementHorizontalAlignment.Left => rect.Left,
            ElementHorizontalAlignment.Right => rect.Right - formatted.WidthIncludingTrailingWhitespace,
            _ => rect.Left + (rect.Width - formatted.WidthIncludingTrailingWhitespace) / 2,
        };
        context.DrawText(formatted, new Point(x, y));
    }

    private void DrawSvg(DrawingContext context, SvgElement element, Rect target)
    {
        if (AssetResolver is null)
        {
            return;
        }

        var document = XDocument.Load(AssetResolver.ResolveAssetPath(element.AssetKey));
        var root = document.Root ?? throw new InvalidDataException("SVG root element is missing.");
        var viewBoxParts = (root.Attribute("viewBox")?.Value ?? "0 0 1 1")
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
        var source = new Rect(viewBoxParts[0], viewBoxParts[1], viewBoxParts[2], viewBoxParts[3]);
        var fit = Math.Min(target.Width / source.Width, target.Height / source.Height);
        var tx = target.Left + (target.Width - source.Width * fit) / 2 - source.X * fit;
        var ty = target.Top + (target.Height - source.Height * fit) / 2 - source.Y * fit;
        context.PushTransform(new MatrixTransform(fit, 0, 0, fit, tx, ty));

        foreach (var node in root.Descendants())
        {
            var fill = ParseBrush(node.Attribute("fill")?.Value);
            var stroke = ParseBrush(node.Attribute("stroke")?.Value);
            var strokeWidth = ParseDouble(node.Attribute("stroke-width")?.Value, 1);
            var pen = stroke is null ? null : new Pen(stroke, strokeWidth)
            {
                StartLineCap = ParseLineCap(node.Attribute("stroke-linecap")?.Value),
                EndLineCap = ParseLineCap(node.Attribute("stroke-linecap")?.Value),
            };

            switch (node.Name.LocalName)
            {
                case "circle":
                    context.DrawEllipse(fill, pen,
                        new Point(ParseDouble(node.Attribute("cx")?.Value), ParseDouble(node.Attribute("cy")?.Value)),
                        ParseDouble(node.Attribute("r")?.Value), ParseDouble(node.Attribute("r")?.Value));
                    break;
                case "rect":
                    context.DrawRectangle(fill, pen, new Rect(
                        ParseDouble(node.Attribute("x")?.Value), ParseDouble(node.Attribute("y")?.Value),
                        ParseDouble(node.Attribute("width")?.Value), ParseDouble(node.Attribute("height")?.Value)));
                    break;
                case "line":
                    context.DrawLine(pen ?? new Pen(Brushes.Black, 1),
                        new Point(ParseDouble(node.Attribute("x1")?.Value), ParseDouble(node.Attribute("y1")?.Value)),
                        new Point(ParseDouble(node.Attribute("x2")?.Value), ParseDouble(node.Attribute("y2")?.Value)));
                    break;
                case "path":
                    var data = node.Attribute("d")?.Value;
                    if (!string.IsNullOrWhiteSpace(data))
                    {
                        context.DrawGeometry(fill, pen, Geometry.Parse(data));
                    }
                    break;
            }
        }

        context.Pop();
    }

    private void DrawImage(DrawingContext context, ImageElement element, Rect target)
    {
        if (AssetResolver is null)
        {
            return;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(AssetResolver.ResolveAssetPath(element.AssetKey));
        bitmap.EndInit();
        bitmap.Freeze();
        context.DrawImage(bitmap, target);
    }

    private static Brush? BrushFor(FillStyle? fill) => fill is null ? null : ParseBrush(fill.ColorHex);

    private static Pen? PenFor(StrokeStyle? stroke, double scale) =>
        stroke is null ? null : new Pen(ParseBrush(stroke.ColorHex) ?? Brushes.Black, Math.Max(0.5, stroke.WidthMm * scale));

    private static Brush? ParseBrush(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    }

    private static double ParseDouble(string? value, double fallback = 0) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : fallback;

    private static PenLineCap ParseLineCap(string? value) =>
        string.Equals(value, "round", StringComparison.OrdinalIgnoreCase) ? PenLineCap.Round : PenLineCap.Flat;
}
