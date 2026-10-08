using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CorelSignStudio.App;

public partial class MainWindow : Window
{
    private bool _captured;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Preview.AssetResolver = viewModel.AssetResolver;
        ContentRendered += CaptureMilestoneScreenshots;
    }

    private void CaptureMilestoneScreenshots(object? sender, EventArgs e)
    {
        if (_captured)
        {
            return;
        }

        _captured = true;
        try
        {
            var folder = Path.Combine(ProjectPaths.FindProjectRoot(), "artifacts", "screenshots");
            Directory.CreateDirectory(folder);
            SaveVisual(this, Path.Combine(folder, "application.png"));
            SaveVisual(Preview, Path.Combine(folder, "preview.png"));
        }
        catch
        {
            // Screenshot capture is a non-critical milestone artifact and must never block the UI.
        }
    }

    private static void SaveVisual(FrameworkElement visual, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
