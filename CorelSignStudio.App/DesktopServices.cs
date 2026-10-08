using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace CorelSignStudio.App;

public interface IDesktopShellService
{
    string? BrowseForFolder(string initialFolder);
    void OpenFolder(string path);
    void OpenFile(string path);
}

public sealed class DesktopShellService : IDesktopShellService
{
    public string? BrowseForFolder(string initialFolder)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Çıktı klasörünü seçin",
            InitialDirectory = Directory.Exists(initialFolder) ? initialFolder : null,
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public void OpenFolder(string path) => Open(path);

    public void OpenFile(string path) => Open(path);

    private static void Open(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            throw new FileNotFoundException("Açılacak dosya veya klasör bulunamadı.", path);
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

public static class ProjectPaths
{
    public static string FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CorelSignStudio.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return @"D:\CorelSignStudio";
    }
}
