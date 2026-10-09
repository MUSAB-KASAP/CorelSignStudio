using System.IO;
using System.Reflection;

namespace CorelSignStudio.App;

/// <summary>Product facts read back from the assembly, so the version is written in exactly one place (Directory.Build.props).</summary>
public static class AppInfo
{
    public static string Version { get; } = ReadVersion(typeof(AppInfo).Assembly);

    public static string ReadVersion(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Build metadata (a "+commit" suffix) is not part of what the user is shown.
            return informational.Split('+')[0];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

/// <summary>
/// Where the application keeps its files. Run from a source checkout it uses the repository's own
/// <c>data</c>, <c>output</c> and <c>logs</c> folders; an installed or published copy must never write
/// next to its executable, so it uses the user's profile instead.
/// </summary>
public sealed record AppPaths(string DataFolder, string OutputFolder, string LogFolder, bool IsDevelopment)
{
    public const string ProfileFolderName = "CorelSignStudio";
    public const string DocumentsFolderName = "Corel AI Operatörü";

    public static AppPaths Resolve() => Resolve(
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    public static AppPaths Resolve(string baseDirectory, string localAppData, string documents)
    {
        if (FindSolutionRoot(baseDirectory) is { } root)
        {
            return new AppPaths(Path.Combine(root, "data"), Path.Combine(root, "output"), Path.Combine(root, "logs"), IsDevelopment: true);
        }

        var profile = Path.Combine(localAppData, ProfileFolderName);
        return new AppPaths(Path.Combine(profile, "data"), Path.Combine(documents, DocumentsFolderName), Path.Combine(profile, "logs"), IsDevelopment: false);
    }

    private static string? FindSolutionRoot(string start)
    {
        for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "CorelSignStudio.sln")))
            {
                return current.FullName;
            }
        }

        return null;
    }
}
