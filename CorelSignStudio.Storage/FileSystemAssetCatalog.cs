using System.Text.Json;
using CorelSignStudio.Domain;

namespace CorelSignStudio.Storage;

public sealed class FileSystemAssetCatalog : IDesignAssetCatalog
{
    private readonly string _assetRoot;
    private readonly IReadOnlyDictionary<string, DesignAssetMetadata> _assets;

    public FileSystemAssetCatalog(string assetRoot, string metadataFileName = "assets.json")
    {
        _assetRoot = Path.GetFullPath(assetRoot);
        var metadataPath = GetSafePath(metadataFileName);
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException("Asset metadata file was not found.", metadataPath);
        }

        using var stream = File.OpenRead(metadataPath);
        var assets = JsonSerializer.Deserialize<List<DesignAssetMetadata>>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        _assets = assets.ToDictionary(asset => asset.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<DesignAssetMetadata> GetAssets() => _assets.Values.OrderBy(asset => asset.Name).ToArray();

    public DesignAssetMetadata GetAsset(string assetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetKey);
        return _assets.TryGetValue(assetKey, out var asset)
            ? asset
            : throw new KeyNotFoundException($"Design asset '{assetKey}' is not registered.");
    }

    public string ResolveAssetPath(string assetKey)
    {
        var asset = GetAsset(assetKey);
        var path = GetSafePath(asset.File);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Design asset '{assetKey}' was not found.", path);
    }

    private string GetSafePath(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_assetRoot, relativePath));
        var rootPrefix = _assetRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _assetRoot
            : _assetRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Asset path resolves outside the configured asset root.");
        }

        return path;
    }
}

