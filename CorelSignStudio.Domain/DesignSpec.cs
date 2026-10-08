namespace CorelSignStudio.Domain;

/// <summary>
/// Application-neutral sign description. The coordinate system starts at the
/// top-left of the page; all coordinates and physical sizes are millimetres.
/// </summary>
public sealed record DesignSpec
{
    public required double WidthMm { get; init; }

    public required double HeightMm { get; init; }

    public IReadOnlyList<DesignElement> Elements { get; init; } = [];

    public void Validate()
    {
        if (!double.IsFinite(WidthMm) || WidthMm <= 0)
        {
            throw new DesignValidationException("Design width must be a positive finite millimetre value.");
        }

        if (!double.IsFinite(HeightMm) || HeightMm <= 0)
        {
            throw new DesignValidationException("Design height must be a positive finite millimetre value.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in Elements)
        {
            element.Validate();
            if (!ids.Add(element.Id))
            {
                throw new DesignValidationException($"Element id '{element.Id}' is duplicated.");
            }
        }
    }
}

public sealed class DesignValidationException(string message) : Exception(message);

