namespace CorelSignStudio.Domain.Assets;

/// <summary>
/// A reusable file: a logo, icon, traffic sign, symbol, customer artwork, CorelDRAW file or font
/// description. Categories and tags are free text so the library is not tied to any one kind of job.
/// </summary>
public sealed record Asset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Category { get; init; } = "General";
    public required string FilePath { get; init; }

    /// <summary>Lower-case extension without the dot, for example <c>svg</c>.</summary>
    public required string FileType { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public DateTimeOffset AddedUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool Matches(AssetQuery query)
    {
        if (query.Category is not null && !string.Equals(Category, query.Category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.FileType is not null && !string.Equals(FileType, query.FileType.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Tags.Any(tag => !Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query.Text))
        {
            return true;
        }

        return query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(term =>
            Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            Category.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
            Metadata.Values.Any(value => value.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed record AssetQuery
{
    public string? Text { get; init; }
    public string? Category { get; init; }
    public string? FileType { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public interface IAssetLibrary
{
    IReadOnlyList<Asset> GetAll();

    Asset? Get(string id);

    IReadOnlyList<Asset> Search(AssetQuery query);

    IReadOnlyList<string> GetCategories();

    /// <summary>Registers a file, copying it into the library.</summary>
    Asset Add(string sourceFilePath, string? name = null, string? category = null, IEnumerable<string>? tags = null,
        IReadOnlyDictionary<string, string>? metadata = null);

    void Update(Asset asset);

    bool Remove(string id);
}
