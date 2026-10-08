using System.IO;
using System.Windows;
using CorelSignStudio.Corel;
using CorelSignStudio.Storage;
using CorelSignStudio.Templates;

namespace CorelSignStudio.App;

public partial class App : Application
{
    private CorelAutomationService? _corelService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var projectRoot = ProjectPaths.FindProjectRoot();
        var assetCatalog = new FileSystemAssetCatalog(Path.Combine(AppContext.BaseDirectory, "assets", "icons"));
        var logger = new FileLogWriter(Path.Combine(projectRoot, "logs"));
        _corelService = new CorelAutomationService(assetCatalog, message => logger.Write(message));

        var viewModel = new MainViewModel(
            TemplateCatalog.CreateDefault(),
            assetCatalog,
            new OutputNameGenerator(),
            _corelService,
            new DesktopShellService(),
            logger,
            Path.Combine(projectRoot, "output"));

        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_corelService is not null)
        {
            _corelService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }
}
