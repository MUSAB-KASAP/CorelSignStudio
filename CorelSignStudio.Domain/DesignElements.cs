namespace CorelSignStudio.Domain;

public enum TextFontWeight
{
    Normal,
    SemiBold,
    Bold,
    Black,
}

public enum ElementHorizontalAlignment
{
    Left,
    Center,
    Right,
}

public enum ElementVerticalAlignment
{
    Top,
    Center,
    Bottom,
}

public abstract record DesignElement
{
    public required string Id { get; init; }
    public required double XMm { get; init; }
    public required double YMm { get; init; }
    public required double WidthMm { get; init; }
    public required double HeightMm { get; init; }
    public double RotationDegrees { get; init; }
    public int ZIndex { get; init; }
    public bool Visible { get; init; } = true;
    public FillStyle? Fill { get; init; }
    public StrokeStyle? Stroke { get; init; }
    public double Opacity { get; init; } = 1;

    public virtual void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new DesignValidationException("Every design element must have an id.");
        }

        ValidateFinite(XMm, nameof(XMm));
        ValidateFinite(YMm, nameof(YMm));
        ValidateFinite(WidthMm, nameof(WidthMm));
        ValidateFinite(HeightMm, nameof(HeightMm));
        ValidateFinite(RotationDegrees, nameof(RotationDegrees));
        ValidateFinite(Opacity, nameof(Opacity));

        if (WidthMm < 0 || HeightMm < 0)
        {
            throw new DesignValidationException($"Element '{Id}' dimensions cannot be negative.");
        }

        if (Opacity is < 0 or > 1)
        {
            throw new DesignValidationException($"Element '{Id}' opacity must be between 0 and 1.");
        }

        Stroke?.Validate();
        Fill?.Validate();
    }

    protected void ValidatePositiveBounds()
    {
        ValidatePositive(WidthMm, nameof(WidthMm));
        ValidatePositive(HeightMm, nameof(HeightMm));
    }

    protected static void ValidatePositive(double value, string name)
    {
        ValidateFinite(value, name);
        if (value <= 0)
        {
            throw new DesignValidationException($"{name} must be greater than zero.");
        }
    }

    protected static void ValidateFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new DesignValidationException($"{name} must be finite.");
        }
    }
}

public sealed record TextElement : DesignElement
{
    public required string Text { get; init; }
    public required string FontFamily { get; init; }
    public required double FontSizePt { get; init; }
    public TextFontWeight FontWeight { get; init; } = TextFontWeight.Normal;
    public ElementHorizontalAlignment HorizontalAlignment { get; init; } = ElementHorizontalAlignment.Left;
    public ElementVerticalAlignment VerticalAlignment { get; init; } = ElementVerticalAlignment.Top;
    public double LetterSpacing { get; init; }

    public override void Validate()
    {
        base.Validate();
        ValidatePositiveBounds();
        if (string.IsNullOrWhiteSpace(Text))
        {
            throw new DesignValidationException($"Text element '{Id}' cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(FontFamily))
        {
            throw new DesignValidationException($"Text element '{Id}' must specify a font family.");
        }

        ValidatePositive(FontSizePt, nameof(FontSizePt));
        ValidateFinite(LetterSpacing, nameof(LetterSpacing));
    }
}

public sealed record RectangleElement : DesignElement
{
    public double CornerRadiusMm { get; init; }

    public override void Validate()
    {
        base.Validate();
        ValidatePositiveBounds();
        ValidateFinite(CornerRadiusMm, nameof(CornerRadiusMm));
        if (CornerRadiusMm < 0)
        {
            throw new DesignValidationException("CornerRadiusMm cannot be negative.");
        }
    }
}

public sealed record EllipseElement : DesignElement
{
    public override void Validate()
    {
        base.Validate();
        ValidatePositiveBounds();
    }
}

/// <summary>A line from (X, Y) to (X + Width, Y + Height), in millimetres.</summary>
public sealed record LineElement : DesignElement
{
    public override void Validate()
    {
        base.Validate();
        if (WidthMm == 0 && HeightMm == 0)
        {
            throw new DesignValidationException($"Line element '{Id}' must have a non-zero length.");
        }
    }
}

public sealed record SvgElement : DesignElement
{
    public required string AssetKey { get; init; }

    public override void Validate()
    {
        base.Validate();
        ValidatePositiveBounds();
        if (string.IsNullOrWhiteSpace(AssetKey))
        {
            throw new DesignValidationException($"SVG element '{Id}' must specify an asset key.");
        }
    }
}

public sealed record ImageElement : DesignElement
{
    public required string AssetKey { get; init; }

    public override void Validate()
    {
        base.Validate();
        ValidatePositiveBounds();
        if (string.IsNullOrWhiteSpace(AssetKey))
        {
            throw new DesignValidationException($"Image element '{Id}' must specify an asset key.");
        }
    }
}

public sealed record FillStyle(string ColorHex)
{
    public void Validate() => ColorValue.ValidateHex(ColorHex, nameof(ColorHex));
}

public sealed record StrokeStyle(string ColorHex, double WidthMm)
{
    public void Validate()
    {
        ColorValue.ValidateHex(ColorHex, nameof(ColorHex));
        if (!double.IsFinite(WidthMm) || WidthMm < 0)
        {
            throw new DesignValidationException("Stroke width must be a non-negative finite millimetre value.");
        }
    }
}

internal static class ColorValue
{
    public static void ValidateHex(string value, string name)
    {
        if (value.Length != 7 || value[0] != '#' || !int.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _))
        {
            throw new DesignValidationException($"{name} must use #RRGGBB format.");
        }
    }
}

