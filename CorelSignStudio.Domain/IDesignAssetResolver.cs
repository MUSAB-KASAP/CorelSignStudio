namespace CorelSignStudio.Domain;

public interface IDesignAssetResolver
{
    string ResolveAssetPath(string assetKey);
}

public interface IDesignAssetCatalog : IDesignAssetResolver
{
    IReadOnlyList<DesignAssetMetadata> GetAssets();
    DesignAssetMetadata GetAsset(string assetKey);
}

public sealed record DesignAssetMetadata(string Id, string Name, string Category, string File);

