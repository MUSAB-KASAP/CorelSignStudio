namespace CorelSignStudio.Domain;

public abstract record DesignElement
{
    public required string Id { get; init; }

    public required double XMm { get; init; }

    public required double YMm { get; init; }

    public FillStyle? Fill { get; init; }

    public StrokeStyle? Stroke { get; init; }

    public virtual void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            throw new DesignValidationException("Every design element must have an id.");
        }

        ValidateFinite(XMm, nameof(XMm));
        ValidateFinite(YMm, nameof(YMm));
        Stroke?.Validate();
        Fill?.Validate();
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

    public required double FontSizeMm { get; init; }

    public bool Bold { get; init; }

    public override void Validate()
    {
        base.Validate();
        if (string.IsNullOrWhiteSpace(Text))
        {
            throw new DesignValidationException($"Text element '{Id}' cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(FontFamily))
        {
            throw new DesignValidationException($"Text element '{Id}' must specify a font family.");
        }

        ValidatePositive(FontSizeMm, nameof(FontSizeMm));
    }
}

public sealed record RectangleElement : DesignElement
{
    public required double WidthMm { get; init; }

    public required double HeightMm { get; init; }

    public double CornerRadiusMm { get; init; }

    public override void Validate()
    {
        base.Validate();
        ValidatePositive(WidthMm, nameof(WidthMm));
        ValidatePositive(HeightMm, nameof(HeightMm));
        ValidateFinite(CornerRadiusMm, nameof(CornerRadiusMm));
        if (CornerRadiusMm < 0)
        {
            throw new DesignValidationException("CornerRadiusMm cannot be negative.");
        }
    }
}

public sealed record EllipseElement : DesignElement
{
    public required double WidthMm { get; init; }

    public required double HeightMm { get; init; }

    public override void Validate()
    {
        base.Validate();
        ValidatePositive(WidthMm, nameof(WidthMm));
        ValidatePositive(HeightMm, nameof(HeightMm));
    }
}

public sealed record LineElement : DesignElement
{
    public required double EndXMm { get; init; }

    public required double EndYMm { get; init; }

    public override void Validate()
    {
        base.Validate();
        ValidateFinite(EndXMm, nameof(EndXMm));
        ValidateFinite(EndYMm, nameof(EndYMm));
    }
}

public sealed record SvgElement : DesignElement
{
    public required string AssetKey { get; init; }

    public required double WidthMm { get; init; }

    public required double HeightMm { get; init; }

    public override void Validate()
    {
        base.Validate();
        ValidateAsset();
    }

    private void ValidateAsset()
    {
        if (string.IsNullOrWhiteSpace(AssetKey))
        {
            throw new DesignValidationException($"SVG element '{Id}' must specify an asset key.");
        }

        ValidatePositive(WidthMm, nameof(WidthMm));
        ValidatePositive(HeightMm, nameof(HeightMm));
    }
}

public sealed record ImageElement : DesignElement
{
    public required string AssetKey { get; init; }

    public required double WidthMm { get; init; }

    public required double HeightMm { get; init; }

    public override void Validate()
    {
        base.Validate();
        if (string.IsNullOrWhiteSpace(AssetKey))
        {
            throw new DesignValidationException($"Image element '{Id}' must specify an asset key.");
        }

        ValidatePositive(WidthMm, nameof(WidthMm));
        ValidatePositive(HeightMm, nameof(HeightMm));
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

