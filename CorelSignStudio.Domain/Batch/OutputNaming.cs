using System.Globalization;
using System.Text;

namespace CorelSignStudio.Domain.Batch;

/// <summary>Makes arbitrary text safe to use as a Windows file name while keeping it readable.</summary>
public static class FileNameSanitizer
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? value, string fallback = "output", int maxLength = 120)
    {
        var mapped = (value ?? "")
            .Replace('ı', 'i').Replace('İ', 'I').Replace('ş', 's').Replace('Ş', 'S').Replace('ğ', 'g').Replace('Ğ', 'G');
        var builder = new StringBuilder(mapped.Length);
        foreach (var character in mapped.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '.' ? character : '_');
        }

        var collapsed = builder.ToString();
        while (collapsed.Contains("__", StringComparison.Ordinal))
        {
            collapsed = collapsed.Replace("__", "_", StringComparison.Ordinal);
        }

        collapsed = collapsed.Trim('_', '.', '-');
        if (collapsed.Length > maxLength)
        {
            collapsed = collapsed[..maxLength].Trim('_', '.', '-');
        }

        if (collapsed.Length == 0)
        {
            collapsed = fallback;
        }

        return ReservedNames.Contains(collapsed) ? "_" + collapsed : collapsed;
    }
}

/// <summary>
/// Hands out output base names that collide neither with files already on disk nor with names
/// handed out earlier by the same allocator (two batch rows with the same person name, for example).
/// </summary>
public sealed class OutputNameAllocator(Func<string, bool>? fileExists = null)
{
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="folder">Output folder.</param>
    /// <param name="desiredBaseName">Unsanitized name without extension.</param>
    /// <param name="extensions">Every extension that will be written with this base name, for example <c>.cdr</c> and <c>.pdf</c>.</param>
    /// <returns>A base name (no folder, no extension) that is free for all extensions.</returns>
    public string Allocate(string folder, string desiredBaseName, IReadOnlyCollection<string> extensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(extensions);

        var baseName = FileNameSanitizer.Sanitize(desiredBaseName);
        var candidate = baseName;
        for (var version = 2; IsTaken(folder, candidate, extensions); version++)
        {
            candidate = $"{baseName}_{version.ToString("000", CultureInfo.InvariantCulture)}";
        }

        _reserved.Add(Path.Combine(folder, candidate));
        return candidate;
    }

    private bool IsTaken(string folder, string candidate, IReadOnlyCollection<string> extensions)
    {
        var basePath = Path.Combine(folder, candidate);
        return _reserved.Contains(basePath) || extensions.Any(extension => _fileExists(basePath + extension));
    }
}
