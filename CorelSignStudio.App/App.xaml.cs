using System.IO;
using System.Windows;
using System.Windows.Threading;
using CorelSignStudio.Corel;
using CorelSignStudio.Domain.Planning;
using CorelSignStudio.Domain.References;
using CorelSignStudio.Storage;
using CorelSignStudio.Templates;

namespace CorelSignStudio.App;

public partial class App : Application
{
    private CorelAutomationService? _corelService;
    private MainWindow? _legacyWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var projectRoot = ProjectPaths.FindProjectRoot();
        var dataFolder = Path.Combine(projectRoot, "data");
        var outputFolder = Path.Combine(projectRoot, "output");
        var assetCatalog = new FileSystemAssetCatalog(Path.Combine(AppContext.BaseDirectory, "assets", "icons"));
        var logger = new FileLogWriter(Path.Combine(projectRoot, "logs"));
        var shell = new DesktopShellService();

        // The user works in the same CorelDRAW this application drives, so never quit it on exit.
        _corelService = new CorelAutomationService(assetCatalog, message => logger.Write(message)) { KeepApplicationOpen = true };

        // Composition root: swapping DeterministicCommandPlanner for an AI planner, or
        // FileReferenceAnalyzer for an AI vision analyzer, happens here and nowhere else.
        var viewModel = new OperatorViewModel(new OperatorServices(
            Corel: _corelService,
            Inspector: new CorelDocumentInspector(_corelService),
            Executor: new CorelActionExecutor(_corelService),
            Planner: new DeterministicCommandPlanner(),
            Recipes: new JsonRecipeStore(Path.Combine(dataFolder, "recipes")),
            Assets: new JsonAssetLibrary(Path.Combine(dataFolder, "assets")),
            History: new JsonExecutionHistoryStore(Path.Combine(dataFolder, "history")),
            ReferenceAnalyzer: new FileReferenceAnalyzer(),
            ReferencePlanBuilder: new ImportReferencePlanBuilder(),
            Shell: shell,
            FileLog: logger,
            DataFolder: dataFolder,
            DefaultOutputFolder: outputFolder));

        viewModel.OpenLegacyToolRequested += () =>
        {
            if (_legacyWindow is null)
            {
                _legacyWindow = new MainWindow(new MainViewModel(
                    TemplateCatalog.CreateDefault(),
                    assetCatalog,
                    new OutputNameGenerator(),
                    _corelService,
                    shell,
                    logger,
                    outputFolder));
                _legacyWindow.Closed += (_, _) => _legacyWindow = null;
                _legacyWindow.Show();
            }

            _legacyWindow.Activate();
        };

        var window = new OperatorWindow(viewModel);
        MainWindow = window;
        window.Show();

        // "--screenshots <folder>" renders every tab to a PNG and exits (used for unattended UI review).
        var screenshotIndex = Array.IndexOf(e.Args, "--screenshots");
        if (screenshotIndex >= 0 && screenshotIndex + 1 < e.Args.Length)
        {
            _ = CaptureTabsAndExitAsync(window, viewModel, e.Args[screenshotIndex + 1]);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_corelService is not null)
        {
            _corelService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }

    private async Task CaptureTabsAndExitAsync(OperatorWindow window, OperatorViewModel viewModel, string folder)
    {
        try
        {
            // Show a prepared plan in the first screenshot; planning needs no CorelDRAW connection.
            viewModel.Request = "Create a 500x700 mm document\nAdd text TEST in the center\nCreate a table with 8 columns and 20 rows and center all text\nDelete it\nMake the logo look premium";
            viewModel.PreparePlanCommand.Execute(null);
            string[] names = ["operator", "document", "recipes", "batch", "assets", "history", "settings"];
            for (var index = 0; index < names.Length; index++)
            {
                viewModel.SelectedTabIndex = index;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Task.Delay(150);
                window.SaveScreenshot(Path.Combine(folder, $"{index + 1}-{names[index]}.png"));
            }
        }
        finally
        {
            Shutdown();
        }
    }
}
