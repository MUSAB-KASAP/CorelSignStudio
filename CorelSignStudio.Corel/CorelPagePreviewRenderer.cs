using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.References;

namespace CorelSignStudio.Corel;

/// <summary>
/// Renders the whole active page — page rectangle, white background and all visible artwork — to a PNG
/// for visual comparison. It uses CorelDRAW's own export area option
/// (<c>StructExportOptions.ExportArea = Page.BoundingBox</c>, verified on CorelDRAW 2026), so nothing is
/// added to or changed in the document; the document's modified flag is restored afterwards.
/// </summary>
public sealed class CorelPagePreviewRenderer(CorelAutomationService service) : ICorelPagePreviewRenderer
{
    private const int FilterPng = 802;
    private const int ExportCurrentPage = 1;
    private const int ImageTypeRgb = 4;
    private const int AntiAliasingNormal = 1;

    public async Task<ReferencePreview> RenderActivePageAsync(int maxLongEdgePixels = 1568, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetTempPath(), "CorelSignStudio", "page-preview", Guid.NewGuid().ToString("N") + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var (width, height, pixelWidth, pixelHeight, title) = await service.RunAsync("PagePreview", applicationObject =>
            {
                dynamic application = applicationObject;
                object? documentObject = application.ActiveDocument;
                if (documentObject is null)
                {
                    throw new InvalidOperationException(Msg.Get("Corel.NoDocument"));
                }

                dynamic document = documentObject;
                document.Unit = CorelShapes.UnitMillimeter;
                dynamic page = document.ActivePage;
                double pageWidth = page.SizeWidth;
                double pageHeight = page.SizeHeight;
                var scale = maxLongEdgePixels / Math.Max(pageWidth, pageHeight);
                var targetWidth = Math.Max(1, (int)Math.Round(pageWidth * scale));
                var targetHeight = Math.Max(1, (int)Math.Round(pageHeight * scale));
                var dpi = Math.Max(1, (int)Math.Round(targetWidth / (pageWidth / 25.4)));

                bool wasDirty = document.Dirty;
                try
                {
                    dynamic options = application.CreateStructExportOptions();
                    options.ImageType = ImageTypeRgb;
                    options.AntiAliasingType = AntiAliasingNormal;
                    options.Transparent = false;          // an opaque, white page background
                    options.MaintainAspect = true;
                    options.Overwrite = true;
                    options.SizeX = targetWidth;
                    options.SizeY = targetHeight;
                    options.ResolutionX = dpi;
                    options.ResolutionY = dpi;
                    options.ExportArea = page.BoundingBox; // the page itself, not just the artwork on it
                    dynamic palette = application.CreateStructPaletteOptions();
                    dynamic filter = document.ExportEx(path, FilterPng, ExportCurrentPage, options, palette);
                    filter.Finish();
                }
                finally
                {
                    document.Dirty = wasDirty;
                }

                return (pageWidth, pageHeight, targetWidth, targetHeight, (string)document.Title);
            }, cancellationToken).ConfigureAwait(false);

            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                throw new ReferencePreviewException(Msg.Format("Vision.PreviewFailed", title));
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var (actualWidth, actualHeight) = ReadPngSize(bytes) ?? (pixelWidth, pixelHeight);
            return new ReferencePreview
            {
                ReferenceId = "output",
                FileName = title,
                WidthPixels = actualWidth,
                HeightPixels = actualHeight,
                OriginalWidthPixels = actualWidth,
                OriginalHeightPixels = actualHeight,
                MimeType = "image/png",
                Bytes = bytes,
                PhysicalSize = new PhysicalSize(Math.Round(width, 2), Math.Round(height, 2)),
            };
        }
        catch (CorelAutomationException exception)
        {
            throw new ReferencePreviewException(
                exception.InnerException is InvalidOperationException inner ? inner.Message : Msg.Get("Corel.ComFailure"), exception);
        }
        finally
        {
            try
            {
                File.Delete(path); // the picture only ever lives in memory after this
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Left for the temp folder's normal clean-up.
            }
        }
    }

    private static (int Width, int Height)? ReadPngSize(byte[] bytes)
    {
        if (bytes.Length < 24 || bytes[0] != 0x89 || bytes[1] != 0x50)
        {
            return null;
        }

        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }
}
