using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.References;
using SkiaSharp;

namespace CorelSignStudio.Imaging;

/// <summary>
/// A per-session folder for derived files (cropped components). It lives under the system temp folder,
/// is removed on dispose, and leftovers of earlier sessions are swept on start. Originals are never touched.
/// </summary>
public sealed class ReferenceTempStore : IDisposable
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "CorelSignStudio", "reference-cache");

    public ReferenceTempStore()
    {
        SweepStale(TimeSpan.FromDays(2));
        Folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
    }

    public string Folder { get; }

    public string PathFor(string fileName) => Path.Combine(Folder, fileName);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // CorelDRAW may still hold an imported file open; the next start sweeps it.
        }
    }

    private static void SweepStale(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(Root))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > olderThan)
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

internal static class SkiaImages
{
    /// <summary>Decodes an image and applies its EXIF orientation, so "up" in the preview is up in the picture.</summary>
    public static SKBitmap DecodeUpright(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new ReferencePreviewException(Msg.Format("Vision.PreviewFailed", Path.GetFileName(path)));
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw new ReferencePreviewException(Msg.Format("Vision.PreviewFailed", Path.GetFileName(path)));
        }

        var origin = codec.EncodedOrigin;
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return bitmap;
        }

        using (bitmap)
        {
            return Reorient(bitmap, origin);
        }
    }

    public static SKBitmap Reorient(SKBitmap source, SKEncodedOrigin origin)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var target = new SKBitmap(new SKImageInfo(swap ? source.Height : source.Width, swap ? source.Width : source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        float w = source.Width, h = source.Height;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;                      // mirrored horizontally
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;             // rotated 180
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;                    // mirrored vertically
            case SKEncodedOrigin.LeftTop: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;                     // transposed
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;                 // rotate 90 clockwise
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, w); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break; // transverse
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(270); break;              // rotate 90 counter-clockwise
        }

        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return target;
    }

    /// <summary>Downscales (never upscales) to the given long edge with high-quality resampling, keeping the aspect ratio.</summary>
    public static SKBitmap FitLongEdge(SKBitmap source, int maxLongEdge)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (longEdge <= maxLongEdge)
        {
            return source.Copy();
        }

        var scale = (double)maxLongEdge / longEdge;
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)), SKColorType.Rgba8888, SKAlphaType.Premul);
        return source.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell))
               ?? throw new ReferencePreviewException(Msg.Format("Vision.PreviewFailed", "resize"));
    }

    /// <summary>PNG keeps flat colours and text edges exact, which is what sign artwork needs.</summary>
    public static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}

/// <summary>JPG and PNG references: EXIF-corrected, downscaled for analysis, original untouched.</summary>
public sealed class ImageReferencePreviewRenderer : IReferencePreviewRenderer
{
    public bool CanRender(ReferenceInput reference) => reference.FileType is ReferenceFileType.Jpeg or ReferenceFileType.Png;

    public Task<ReferencePreview> RenderAsync(ReferenceInput reference, ReferencePreviewOptions options, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (!File.Exists(reference.FilePath))
            {
                throw new ReferencePreviewException(Msg.Format("Reference.NotFound", reference.FilePath));
            }

            try
            {
                using var upright = SkiaImages.DecodeUpright(reference.FilePath);
                using var fitted = SkiaImages.FitLongEdge(upright, options.MaxLongEdgePixels);
                return new ReferencePreview
                {
                    ReferenceId = reference.Id,
                    FileName = reference.FileName,
                    WidthPixels = fitted.Width,
                    HeightPixels = fitted.Height,
                    OriginalWidthPixels = upright.Width,
                    OriginalHeightPixels = upright.Height,
                    MimeType = "image/png",
                    Bytes = SkiaImages.EncodePng(fitted),

                    // Deliberately no physical size: DPI tags in photos and screenshots are not trustworthy.
                    PhysicalSize = null,
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new ReferencePreviewException(Msg.Format("Vision.PreviewFailed", reference.FileName), exception);
            }
        }, cancellationToken);
}

/// <summary>
/// PDF pages through PDFtoImage (MIT) on PDFium (BSD-3/Apache-2.0): maintained, permissively licensed,
/// no commercial component. The page size comes from the PDF itself, so it is a reliable physical size.
/// </summary>
public sealed class PdfReferencePreviewRenderer : IReferencePreviewRenderer
{
    private const double MillimetresPerPoint = 25.4 / 72;

    public bool CanRender(ReferenceInput reference) => reference.FileType == ReferenceFileType.Pdf;

    public Task<ReferencePreview> RenderAsync(ReferenceInput reference, ReferencePreviewOptions options, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (!File.Exists(reference.FilePath))
            {
                throw new ReferencePreviewException(Msg.Format("Reference.NotFound", reference.FilePath));
            }

            try
            {
                var bytes = File.ReadAllBytes(reference.FilePath);
                var pageCount = PDFtoImage.Conversion.GetPageCount(bytes);
                if (options.PageNumber < 1 || options.PageNumber > pageCount)
                {
                    throw new ReferencePreviewException(Msg.Format("Vision.PageOutOfRange", options.PageNumber, pageCount));
                }

                var pageSize = PDFtoImage.Conversion.GetPageSize(bytes, page: options.PageNumber - 1);
                var longEdgePoints = Math.Max(pageSize.Width, pageSize.Height);
                var dpi = (int)Math.Clamp(Math.Round(options.MaxLongEdgePixels / (longEdgePoints / 72.0)), 24, 300);
                using var rendered = PDFtoImage.Conversion.ToImage(bytes, page: options.PageNumber - 1, options: new PDFtoImage.RenderOptions(Dpi: dpi, BackgroundColor: SKColors.White));
                using var fitted = SkiaImages.FitLongEdge(rendered, options.MaxLongEdgePixels);
                return new ReferencePreview
                {
                    ReferenceId = reference.Id,
                    FileName = reference.FileName,
                    PageNumber = options.PageNumber,
                    PageCount = pageCount,
                    WidthPixels = fitted.Width,
                    HeightPixels = fitted.Height,
                    OriginalWidthPixels = rendered.Width,
                    OriginalHeightPixels = rendered.Height,
                    MimeType = "image/png",
                    Bytes = SkiaImages.EncodePng(fitted),
                    PhysicalSize = new PhysicalSize(Math.Round(pageSize.Width * MillimetresPerPoint, 2), Math.Round(pageSize.Height * MillimetresPerPoint, 2)),
                };
            }
            catch (ReferencePreviewException)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new ReferencePreviewException(Msg.Format("Vision.PreviewFailed", reference.FileName), exception);
            }
        }, cancellationToken);
}

/// <summary>
/// SVG references are reused, not redrawn, so no raster preview is made: only the declared size is read.
/// A size in real units (mm, cm, in, pt) is a reliable physical size; px or unitless sizes are not.
/// </summary>
public sealed partial class SvgReferenceInfoReader : IReferencePreviewRenderer
{
    public bool CanRender(ReferenceInput reference) => reference.FileType == ReferenceFileType.Svg;

    public Task<ReferencePreview> RenderAsync(ReferenceInput reference, ReferencePreviewOptions options, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(reference.FilePath))
        {
            throw new ReferencePreviewException(Msg.Format("Reference.NotFound", reference.FilePath));
        }

        try
        {
            XElement root;
            using (var reader = XmlReader.Create(reference.FilePath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
            {
                root = XDocument.Load(reader).Root ?? throw new XmlException("empty document");
            }

            var width = Length(root.Attribute("width")?.Value);
            var height = Length(root.Attribute("height")?.Value);
            var viewBox = (root.Attribute("viewBox")?.Value ?? "").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            double boxWidth = 0, boxHeight = 0;
            if (viewBox.Length == 4)
            {
                double.TryParse(viewBox[2], NumberStyles.Float, CultureInfo.InvariantCulture, out boxWidth);
                double.TryParse(viewBox[3], NumberStyles.Float, CultureInfo.InvariantCulture, out boxHeight);
            }

            var pixelWidth = (int)Math.Round(boxWidth > 0 ? boxWidth : width.Value);
            var pixelHeight = (int)Math.Round(boxHeight > 0 ? boxHeight : height.Value);
            return Task.FromResult(new ReferencePreview
            {
                ReferenceId = reference.Id,
                FileName = reference.FileName,
                WidthPixels = Math.Max(1, pixelWidth),
                HeightPixels = Math.Max(1, pixelHeight),
                MimeType = "image/svg+xml",
                PhysicalSize = width.Millimetres is { } w && height.Millimetres is { } h && w > 0 && h > 0 ? new PhysicalSize(Math.Round(w, 2), Math.Round(h, 2)) : null,
            });
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            throw new ReferencePreviewException(Msg.Format("Vision.PreviewFailed", reference.FileName), exception);
        }
    }

    private static (double Value, double? Millimetres) Length(string? text)
    {
        var match = LengthPattern().Match(text ?? "");
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return (0, null);
        }

        double? millimetres = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "mm" => value,
            "cm" => value * 10,
            "in" => value * 25.4,
            "pt" => value * 25.4 / 72,
            "pc" => value * 25.4 / 6,
            _ => null, // px, %, em or no unit: not a physical size
        };
        return (value, millimetres);
    }

    [GeneratedRegex(@"^\s*(-?\d+(?:\.\d+)?)\s*([a-z%]*)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LengthPattern();
}

/// <summary>Picks the renderer for the file type. CDR needs CorelDRAW itself and is plugged in by the application.</summary>
public sealed class CompositeReferencePreviewRenderer(params IReferencePreviewRenderer[] renderers) : IReferencePreviewRenderer
{
    public static CompositeReferencePreviewRenderer CreateDefault(params IReferencePreviewRenderer[] additional) =>
        new([new ImageReferencePreviewRenderer(), new PdfReferencePreviewRenderer(), new SvgReferenceInfoReader(), .. additional]);

    public bool CanRender(ReferenceInput reference) => renderers.Any(renderer => renderer.CanRender(reference));

    public Task<ReferencePreview> RenderAsync(ReferenceInput reference, ReferencePreviewOptions options, CancellationToken cancellationToken = default) =>
        (renderers.FirstOrDefault(renderer => renderer.CanRender(reference))
         ?? throw new ReferencePreviewException(Msg.Format("Vision.PreviewFailed", reference.FileName)))
        .RenderAsync(reference, options, cancellationToken);
}

/// <summary>Cuts a component out of a JPG/PNG reference at full resolution into the session's temp folder.</summary>
public sealed class SkiaReferenceImageCropper(ReferenceTempStore store) : IReferenceImageCropper
{
    public string? Crop(ReferenceInput reference, NormalizedBounds bounds, string elementId)
    {
        if (reference.FileType is not (ReferenceFileType.Jpeg or ReferenceFileType.Png) || !File.Exists(reference.FilePath))
        {
            return null;
        }

        try
        {
            using var upright = SkiaImages.DecodeUpright(reference.FilePath);
            var clamped = bounds.Clamp();
            var area = SKRectI.Create(
                (int)Math.Floor(clamped.X * upright.Width),
                (int)Math.Floor(clamped.Y * upright.Height),
                Math.Max(1, (int)Math.Ceiling(clamped.Width * upright.Width)),
                Math.Max(1, (int)Math.Ceiling(clamped.Height * upright.Height)));
            area.Intersect(SKRectI.Create(0, 0, upright.Width, upright.Height));
            if (area.Width < 1 || area.Height < 1)
            {
                return null;
            }

            using var cropped = new SKBitmap(new SKImageInfo(area.Width, area.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (!upright.ExtractSubset(cropped, area))
            {
                return null;
            }

            var path = store.PathFor($"{Path.GetFileNameWithoutExtension(reference.FileName)}_{elementId}.png");
            File.WriteAllBytes(path, SkiaImages.EncodePng(cropped));
            return path;
        }
        catch (Exception exception) when (exception is ReferencePreviewException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>How many pages a PDF has, without rendering any of them.</summary>
public static class PdfPages
{
    /// <returns>The page count, or 1 when the file cannot be read as a PDF.</returns>
    public static int Count(string path)
    {
        try
        {
            return Math.Max(1, PDFtoImage.Conversion.GetPageCount(File.ReadAllBytes(path)));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return 1;
        }
    }
}
