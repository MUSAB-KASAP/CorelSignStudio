using System.Buffers.Binary;
using CorelSignStudio.Domain.References;
using CorelSignStudio.Domain.Localization;

namespace CorelSignStudio.Storage;

/// <summary>
/// The no-AI reference analyzer: reports what can be known from the file itself (type, size, PNG pixel
/// dimensions, whether vector content can be reused). An AI vision analyzer implementing
/// <see cref="IReferenceAnalyzer"/> will fill in <see cref="ReferenceAnalysis.Elements"/> later.
/// </summary>
public sealed class FileReferenceAnalyzer : IReferenceAnalyzer
{
    public string Name => Msg.Get("Reference.Analyzer.Name");

    public bool CanAnalyze(ReferenceInput reference) => reference.FileType != ReferenceFileType.Unknown;

    public Task<ReferenceAnalysis> AnalyzeAsync(ReferenceInput reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var file = new FileInfo(reference.FilePath);
        if (!file.Exists)
        {
            throw new FileNotFoundException(Msg.Format("Reference.NotFound", reference.FilePath), reference.FilePath);
        }

        var notes = new List<string>();
        int? pixelWidth = null;
        int? pixelHeight = null;
        if (reference.FileType == ReferenceFileType.Png && TryReadPngSize(file.FullName, out var width, out var height))
        {
            pixelWidth = width;
            pixelHeight = height;
        }

        notes.Add(Msg.Get(reference.IsVector ? "Reference.Note.Vector" : "Reference.Note.Bitmap"));

        return Task.FromResult(new ReferenceAnalysis
        {
            ReferenceId = reference.Id,
            AnalyzerName = Name,
            FileType = reference.FileType,
            FileSizeBytes = file.Length,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            CanReuseVectorContent = reference.IsVector,
            Notes = notes,
        });
    }

    private static bool TryReadPngSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            Span<byte> header = stackalloc byte[24];
            using var stream = File.OpenRead(path);
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            if (!header[..8].SequenceEqual(signature))
            {
                return false;
            }

            width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
            height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
            return width > 0 && height > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
