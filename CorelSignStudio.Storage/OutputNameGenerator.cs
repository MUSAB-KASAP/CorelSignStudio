using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CorelSignStudio.Storage;

public sealed record OutputPaths(string BaseName, string? CdrPath, string? PdfPath);

public sealed partial class OutputNameGenerator
{
    public OutputPaths CreateAvailablePaths(string outputFolder, IEnumerable<string> textLines, double widthMm, double heightMm, bool includeCdr, bool includePdf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentNullException.ThrowIfNull(textLines);
        Directory.CreateDirectory(outputFolder);

        var baseName = BuildBaseName(textLines, widthMm, heightMm);
        var candidate = baseName;
        var version = 2;
        while ((includeCdr && File.Exists(Path.Combine(outputFolder, candidate + ".cdr"))) ||
               (includePdf && File.Exists(Path.Combine(outputFolder, candidate + ".pdf"))))
        {
            candidate = $"{baseName}_{version:000}";
            version++;
        }

        return new OutputPaths(
            candidate,
            includeCdr ? Path.Combine(outputFolder, candidate + ".cdr") : null,
            includePdf ? Path.Combine(outputFolder, candidate + ".pdf") : null);
    }

    public string BuildBaseName(IEnumerable<string> textLines, double widthMm, double heightMm)
    {
        var safeText = Sanitize(string.Join("_", textLines.Where(line => !string.IsNullOrWhiteSpace(line))));
        var width = widthMm.ToString("0.##", CultureInfo.InvariantCulture);
        var height = heightMm.ToString("0.##", CultureInfo.InvariantCulture);
        return $"{safeText}_{width}x{height}";
    }

    public string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var mapped = value.Replace('ı', 'i').Replace('İ', 'I').Replace('ş', 's').Replace('Ş', 'S').Replace('ğ', 'g').Replace('Ğ', 'G');
        var normalized = mapped.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        var ascii = builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
        var safe = RepeatedUnderscores().Replace(UnsafeFileNameCharacters().Replace(ascii, "_"), "_").Trim('_', '.');
        return string.IsNullOrWhiteSpace(safe) ? "TABELA" : safe;
    }

    [GeneratedRegex("[^A-Z0-9]+")]
    private static partial Regex UnsafeFileNameCharacters();

    [GeneratedRegex("_+")]
    private static partial Regex RepeatedUnderscores();
}

