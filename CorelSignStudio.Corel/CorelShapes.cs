using System.Globalization;
using CorelSignStudio.Domain.Inspection;

namespace CorelSignStudio.Corel;

/// <summary>
/// Late-bound helpers shared by the inspector and the action executor. Everything here must run on
/// the dedicated STA thread. CorelDRAW measures Y upwards from the page's bottom edge; the rest of the
/// application measures Y downwards from the top edge, and <see cref="PageFrame"/> converts between them.
/// </summary>
internal static class CorelShapes
{
    public const int UnitMillimeter = 3;
    public const int ShapeTypeText = 6;
    public const int ShapeTypeGroup = 7;
    public const int ShapeTypeCustom = 21;

    private static readonly string[] ShapeTypeNames =
    [
        "NoShape", "Rectangle", "Ellipse", "Curve", "Polygon", "Bitmap", "Text", "Group", "Selection", "Guideline",
        "BlendGroup", "ExtrudeGroup", "OLEObject", "ContourGroup", "LinearDimension", "BevelGroup", "DropShadowGroup",
        "3DObject", "ArtisticMediaGroup", "Connector", "MeshFill", "Custom", "CustomEffectGroup", "Symbol",
        "HTMLFormObject", "HTMLActiveObject", "PerfectShape", "EPS",
    ];

    public static string LogicalId(dynamic shape) => LogicalShapeId.FromNativeId((int)shape.StaticID);

    public static string NativeTypeName(int type) =>
        type >= 0 && type < ShapeTypeNames.Length ? ShapeTypeNames[type] : type.ToString(CultureInfo.InvariantCulture);

    public static T Try<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Finds a shape anywhere in the document by its persistent StaticID.</summary>
    public static object? FindByStaticId(dynamic document, int staticId)
    {
        // The active page is by far the most common hit, so try it first.
        object? found = FindOnPage(document.ActivePage, staticId);
        if (found is not null)
        {
            return found;
        }

        dynamic pages = document.Pages;
        int count = pages.Count;
        for (var index = 1; index <= count; index++)
        {
            found = FindOnPage(pages[index], staticId);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static object? FindOnPage(dynamic page, int staticId)
    {
        try
        {
            // Verified against CorelDRAW 2026: an empty name and cdrNoShape (0) mean "no filter".
            object? shape = page.Shapes.FindShape("", 0, staticId, true);
            if (shape is not null)
            {
                return shape;
            }
        }
        catch
        {
            // Fall through to the manual walk below.
        }

        return Walk(page.Shapes, staticId);
    }

    private static object? Walk(dynamic shapes, int staticId)
    {
        int count = shapes.Count;
        for (var index = 1; index <= count; index++)
        {
            dynamic shape = shapes[index];
            if ((int)shape.StaticID == staticId)
            {
                return shape;
            }

            if ((int)shape.Type == ShapeTypeGroup)
            {
                object? nested = Walk(shape.Shapes, staticId);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    public static string? ReadColorHex(dynamic application, dynamic color)
    {
        try
        {
            string hex = color.HexValue;
            if (!string.IsNullOrWhiteSpace(hex))
            {
                return hex.ToUpperInvariant();
            }
        }
        catch
        {
            // Older colour models: convert a copy to RGB below.
        }

        try
        {
            dynamic copy = application.CreateColor();
            copy.CopyAssign(color);
            copy.ConvertToRGB();
            return string.Create(CultureInfo.InvariantCulture, $"#{(int)copy.RGBRed:X2}{(int)copy.RGBGreen:X2}{(int)copy.RGBBlue:X2}");
        }
        catch
        {
            return null;
        }
    }

    public static (int Red, int Green, int Blue) ParseRgb(string colorHex) =>
    (
        int.Parse(colorHex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(colorHex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(colorHex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
    );
}

/// <summary>The active page's rectangle in CorelDRAW document coordinates (millimetres).</summary>
internal readonly record struct PageFrame(double Left, double Top, double Width, double Height)
{
    public static PageFrame Of(dynamic page) =>
        new((double)page.LeftX, (double)page.TopY, (double)page.SizeWidth, (double)page.SizeHeight);

    public double Right => Left + Width;
    public double Bottom => Top - Height;

    /// <summary>Top-left-origin X → CorelDRAW X.</summary>
    public double ToCorelX(double xMm) => Left + xMm;

    /// <summary>Top-left-origin Y (downwards) → CorelDRAW Y (upwards).</summary>
    public double ToCorelY(double yMm) => Top - yMm;

    public BoundsMm BoundsOf(dynamic shape) => new(
        Math.Round((double)shape.LeftX - Left, 4),
        Math.Round(Top - (double)shape.TopY, 4),
        Math.Round((double)shape.SizeWidth, 4),
        Math.Round((double)shape.SizeHeight, 4));
}
