using System.Text;
using CorelSignStudio.Domain.Assets;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Batch;
using CorelSignStudio.Domain.Recipes;

namespace CorelSignStudio.Storage;

/// <summary>Stores each recipe as one readable JSON file, so recipes can be copied, shared and versioned.</summary>
public sealed class JsonRecipeStore : IRecipeStore
{
    private readonly string _folder;
    private readonly object _gate = new();

    public JsonRecipeStore(string folder)
    {
        _folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(_folder);
    }

    public IReadOnlyList<Recipe> GetAll()
    {
        lock (_gate)
        {
            var recipes = new List<Recipe>();
            foreach (var file in Directory.EnumerateFiles(_folder, "*.recipe.json"))
            {
                try
                {
                    recipes.Add(Recipe.FromJson(File.ReadAllText(file, Encoding.UTF8)));
                }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException)
                {
                    // A damaged file must not hide every other recipe.
                }
            }

            return recipes.OrderBy(recipe => recipe.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
    }

    public Recipe? Get(string id) => GetAll().FirstOrDefault(recipe => recipe.Id == id);

    public Recipe? FindByName(string name) =>
        GetAll().FirstOrDefault(recipe => string.Equals(recipe.Name, name, StringComparison.CurrentCultureIgnoreCase));

    public void Save(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        lock (_gate)
        {
            AtomicFile.WriteAllText(PathFor(recipe.Id), recipe.ToJson());
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var path = PathFor(id);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }

    private string PathFor(string id) => Path.Combine(_folder, FileNameSanitizer.Sanitize(id, "recipe") + ".recipe.json");
}

/// <summary>
/// A folder-based asset library: files are copied into <c>files/</c> and described in <c>library.json</c>.
/// </summary>
public sealed class JsonAssetLibrary : IAssetLibrary
{
    private readonly string _root;
    private readonly string _indexPath;
    private readonly object _gate = new();
    private List<Asset> _assets;

    public JsonAssetLibrary(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(Path.Combine(_root, "files"));
        _indexPath = Path.Combine(_root, "library.json");
        _assets = File.Exists(_indexPath)
            ? AutomationJson.Deserialize<List<Asset>>(File.ReadAllText(_indexPath, Encoding.UTF8))
            : [];
    }

    public IReadOnlyList<Asset> GetAll()
    {
        lock (_gate)
        {
            return _assets.OrderBy(asset => asset.Category).ThenBy(asset => asset.Name).ToArray();
        }
    }

    public Asset? Get(string id)
    {
        lock (_gate)
        {
            return _assets.FirstOrDefault(asset => string.Equals(asset.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<Asset> Search(AssetQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return GetAll().Where(asset => asset.Matches(query)).ToArray();
    }

    public IReadOnlyList<string> GetCategories() =>
        GetAll().Select(asset => asset.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();

    public Asset Add(
        string sourceFilePath,
        string? name = null,
        string? category = null,
        IEnumerable<string>? tags = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var source = Path.GetFullPath(sourceFilePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The asset file was not found.", source);
        }

        lock (_gate)
        {
            var displayName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(source) : name.Trim();
            var extension = Path.GetExtension(source).ToLowerInvariant();
            var baseId = FileNameSanitizer.Sanitize(displayName, "asset").ToLowerInvariant();
            var id = baseId;
            for (var suffix = 2; _assets.Any(asset => string.Equals(asset.Id, id, StringComparison.OrdinalIgnoreCase)); suffix++)
            {
                id = $"{baseId}-{suffix}";
            }

            var target = Path.Combine(_root, "files", id + extension);
            File.Copy(source, target, overwrite: true);

            var asset = new Asset
            {
                Id = id,
                Name = displayName,
                Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim(),
                FilePath = target,
                FileType = extension.TrimStart('.'),
                Tags = (tags ?? []).Select(tag => tag.Trim()).Where(tag => tag.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                Metadata = metadata ?? new Dictionary<string, string>(),
            };
            _assets.Add(asset);
            Persist();
            return asset;
        }
    }

    public void Update(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        lock (_gate)
        {
            var index = _assets.FindIndex(existing => string.Equals(existing.Id, asset.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new KeyNotFoundException($"Asset '{asset.Id}' is not in the library.");
            }

            _assets[index] = asset;
            Persist();
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var asset = _assets.FirstOrDefault(existing => string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                return false;
            }

            _assets.Remove(asset);
            Persist();

            // Only files the library itself copied are removed; never the user's originals.
            var filesFolder = Path.Combine(_root, "files") + Path.DirectorySeparatorChar;
            if (asset.FilePath.StartsWith(filesFolder, StringComparison.OrdinalIgnoreCase) && File.Exists(asset.FilePath))
            {
                File.Delete(asset.FilePath);
            }

            return true;
        }
    }

    private void Persist() => AtomicFile.WriteAllText(_indexPath, AutomationJson.Serialize(_assets));
}

/// <summary>Keeps one JSON file per executed plan.</summary>
public sealed class JsonExecutionHistoryStore : IExecutionHistoryStore
{
    private readonly string _folder;
    private readonly object _gate = new();

    public JsonExecutionHistoryStore(string folder)
    {
        _folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(_folder);
    }

    public void Append(ExecutionHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var name = $"{entry.TimestampUtc.UtcDateTime:yyyyMMdd-HHmmss-fff}_{entry.Id}.history.json";
            AtomicFile.WriteAllText(Path.Combine(_folder, name), AutomationJson.Serialize(entry));
        }
    }

    public IReadOnlyList<ExecutionHistoryEntry> GetRecent(int maxCount = 100)
    {
        lock (_gate)
        {
            var entries = new List<ExecutionHistoryEntry>();
            foreach (var file in Directory.EnumerateFiles(_folder, "*.history.json").OrderByDescending(Path.GetFileName).Take(maxCount))
            {
                try
                {
                    entries.Add(AutomationJson.Deserialize<ExecutionHistoryEntry>(File.ReadAllText(file, Encoding.UTF8)));
                }
                catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException)
                {
                    // Skip unreadable entries.
                }
            }

            return entries;
        }
    }
}

internal static class AtomicFile
{
    /// <summary>Writes through a temporary file so a crash never leaves a half-written JSON file behind.</summary>
    public static void WriteAllText(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }
}
