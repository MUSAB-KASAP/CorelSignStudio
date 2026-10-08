namespace CorelSignStudio.Domain;

public interface IDesignAssetResolver
{
    string ResolveAssetPath(string assetKey);
}

