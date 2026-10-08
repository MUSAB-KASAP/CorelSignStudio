using System.Globalization;
using System.Text;

namespace CorelSignStudio.Domain.Assets;

/// <summary>
/// Finds the library asset a description most likely refers to ("firma logosu acme" → "ACME Logo.svg").
/// Matching is on the user's own local library only — nothing is searched for or downloaded elsewhere.
/// </summary>
public static class AssetMatcher
{
    /// <summary>Words that describe what kind of thing it is, not which one; alone they never select an asset.</summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "logo", "logosu", "firma", "sirket", "simge", "ikon", "icon", "piktogram", "pictogram", "isaret", "isareti", "levha", "levhasi",
        "resim", "gorsel", "image", "sign", "symbol", "sembol", "amblem", "company", "the", "ve", "and", "icin",
    };

    private static readonly string[] VectorTypes = ["svg", "cdr", "pdf", "ai", "eps"];

    public sealed record Match(Asset Asset, double Score);

    /// <param name="assets">The library.</param>
    /// <param name="hints">Free text from the analysis: asset hint, label.</param>
    /// <returns>The best asset, or <c>null</c> when nothing matches on a specific word.</returns>
    public static Match? FindBest(IEnumerable<Asset> assets, params string?[] hints)
    {
        var words = hints.SelectMany(Tokens).Distinct(StringComparer.Ordinal).ToArray();
        var specific = words.Where(word => !GenericWords.Contains(word)).ToArray();
        if (specific.Length == 0)
        {
            return null;
        }

        Match? best = null;
        foreach (var asset in assets)
        {
            var name = Tokens(asset.Name).Concat(Tokens(asset.Id)).ToHashSet(StringComparer.Ordinal);
            var tags = asset.Tags.SelectMany(Tokens).ToHashSet(StringComparer.Ordinal);
            var category = Tokens(asset.Category).ToHashSet(StringComparer.Ordinal);
            var metadata = asset.Metadata.Values.SelectMany(Tokens).ToHashSet(StringComparer.Ordinal);

            double Score(string word, double weight) =>
                (name.Contains(word) ? 3 : tags.Contains(word) ? 2 : metadata.Contains(word) ? 1.5 : category.Contains(word) ? 1 : 0) * weight;

            var specificScore = specific.Sum(word => Score(word, 1));
            if (specificScore <= 0)
            {
                continue; // a shared generic word ("logo") is not evidence that this is the right logo
            }

            var score = specificScore
                        + words.Where(GenericWords.Contains).Sum(word => Score(word, 0.25))
                        + (VectorTypes.Contains(asset.FileType, StringComparer.OrdinalIgnoreCase) ? 0.5 : 0); // prefer editable sources
            if (best is null || score > best.Score)
            {
                best = new Match(asset, Math.Round(score, 2));
            }
        }

        return best;
    }

    /// <summary>Lower-case ASCII words: "Şirket Logosu (ACME)" → sirket, logosu, acme.</summary>
    public static IEnumerable<string> Tokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var folded = text.Replace('ı', 'i').Replace('İ', 'I').Normalize(NormalizationForm.FormD);
        var word = new StringBuilder();
        foreach (var character in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                word.Append(char.ToLowerInvariant(character));
            }
            else if (word.Length > 0)
            {
                if (word.Length >= 2)
                {
                    yield return word.ToString();
                }

                word.Clear();
            }
        }

        if (word.Length >= 2)
        {
            yield return word.ToString();
        }
    }
}
