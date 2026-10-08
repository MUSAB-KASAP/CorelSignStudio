using CorelSignStudio.Domain;

namespace CorelSignStudio.Storage;

public sealed class FileSystemAssetResolver(string assetRoot) : IDesignAssetResolver
{
    private readonly string _assetRoot = Path.GetFullPath(assetRoot);

    public string ResolveAssetPath(string assetKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetKey);

        var path = Path.GetFullPath(Path.Combine(_assetRoot, assetKey));
        var rootPrefix = _assetRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _assetRoot
            : _assetRoot + Path.DirectorySeparatorChar;

        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Asset key resolves outside the configured asset root.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Design asset '{assetKey}' was not found.", path);
        }

        return path;
    }
}

