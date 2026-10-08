using CorelSignStudio.Domain;

namespace CorelSignStudio.Templates;

public enum SignCategory
{
    Prohibition,
    Warning,
    Mandatory,
    Information,
}

public sealed record SignTemplateParameters(
    double WidthMm,
    double HeightMm,
    string Text1,
    string Text2,
    string Text3,
    string PictogramAssetId);

public interface ISignTemplate
{
    string Id { get; }
    string Name { get; }
    SignCategory Category { get; }
    double DefaultWidthMm { get; }
    double DefaultHeightMm { get; }
    string DefaultPictogramAssetId { get; }
    DesignSpec CreateDesign(SignTemplateParameters parameters);
}

