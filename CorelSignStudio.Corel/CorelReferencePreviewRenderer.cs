using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.References;

namespace CorelSignStudio.Corel;

/// <summary>
/// CDR references are read with CorelDRAW itself — there is no attempt to parse the proprietary format.
/// The file is opened, its page size and page count are read, and it is closed again without saving.
/// CDR content is always reused (imported), so no raster preview is produced. Requires a connected
/// CorelDRAW; in environments without it (cloud, CI) CDR references simply have no known size.
/// </summary>
public sealed class CorelReferencePreviewRenderer(CorelAutomationService service) : IReferencePreviewRenderer
{
    public bool CanRender(ReferenceInput reference) => reference.FileType == ReferenceFileType.Cdr && service.IsConnected;

    public async Task<ReferencePreview> RenderAsync(ReferenceInput reference, ReferencePreviewOptions options, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(reference.FilePath))
        {
            throw new ReferencePreviewException(Msg.Format("Reference.NotFound", reference.FilePath));
        }

        try
        {
            return await service.RunAsync("ReferencePreview", applicationObject =>
            {
                dynamic application = applicationObject;
                object? previous = application.ActiveDocument;
                dynamic document = application.OpenDocument(reference.FilePath);
                try
                {
                    document.Unit = CorelShapes.UnitMillimeter;
                    dynamic page = document.ActivePage;
                    double width = page.SizeWidth;
                    double height = page.SizeHeight;
                    int pages = document.Pages.Count;
                    return new ReferencePreview
                    {
                        ReferenceId = reference.Id,
                        FileName = reference.FileName,
                        PageCount = pages,
                        WidthPixels = Math.Max(1, (int)Math.Round(width)),
                        HeightPixels = Math.Max(1, (int)Math.Round(height)),
                        MimeType = "application/x-coreldraw",
                        PhysicalSize = new PhysicalSize(Math.Round(width, 2), Math.Round(height, 2)),
                    };
                }
                finally
                {
                    document.Dirty = false;
                    document.Close();
                    if (previous is not null)
                    {
                        ((dynamic)previous).Activate();
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (CorelAutomationException exception)
        {
            throw new ReferencePreviewException(Msg.Format("Vision.PreviewFailed", reference.FileName), exception);
        }
    }
}
