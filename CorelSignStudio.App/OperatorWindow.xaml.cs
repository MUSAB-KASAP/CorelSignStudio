using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CorelSignStudio.App;

public partial class OperatorWindow : Window
{
    private readonly OperatorViewModel _viewModel;

    public OperatorWindow(OperatorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Logs.CollectionChanged += ScrollLogToEnd;
    }

    /// <summary>Renders the window to a PNG; used to review the UI without a person at the screen.</summary>
    public void SaveScreenshot(string path)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var content = (FrameworkElement)Content;
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void OnReferenceDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnReferenceDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            _viewModel.AddReferences(files);
        }

        e.Handled = true;
    }

    // A PasswordBox cannot be data-bound, so the typed key is handed over here and cleared immediately.
    private void OnSaveAiSettings(object sender, RoutedEventArgs e)
    {
        _viewModel.SaveAiSettings(ApiKeyBox.Password);
        ApiKeyBox.Clear();
    }

    private void OnRemoveAiKey(object sender, RoutedEventArgs e)
    {
        ApiKeyBox.Clear();
        _viewModel.RemoveAiKey();
    }

    private void ScrollLogToEnd(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        // Deferred: scrolling inside the change notification can run before the list itself has processed
        // the change, which WPF reports as an inconsistent ItemsControl.
        Dispatcher.BeginInvoke(() =>
        {
            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
}

public sealed class ZeroToVisibleConverter : IValueConverter
{
    public static ZeroToVisibleConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class JoinConverter : IValueConverter
{
    public static JoinConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IEnumerable<string> items ? string.Join(", ", items) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibleConverter : IValueConverter
{
    public static BoolToVisibleConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PositiveToVisibleConverter : IValueConverter
{
    public static PositiveToVisibleConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
