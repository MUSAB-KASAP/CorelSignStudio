using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Globalization;
using System.Windows.Markup;
using CorelSignStudio.AI;
using CorelSignStudio.Corel;
using CorelSignStudio.Domain.Ai.Vision;
using CorelSignStudio.Imaging;
using CorelSignStudio.Domain.Ai;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.Planning;
using CorelSignStudio.Domain.References;
using CorelSignStudio.Storage;
using CorelSignStudio.Templates;

namespace CorelSignStudio.App;

public partial class App : Application
{
    private CorelAutomationService? _corelService;
    private MainWindow? _legacyWindow;
    private ReferenceTempStore? _referenceTemp;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplyCulture(Msg.Culture);

        var projectRoot = ProjectPaths.FindProjectRoot();
        var dataFolder = Path.Combine(projectRoot, "data");
        var outputFolder = Path.Combine(projectRoot, "output");
        var assetCatalog = new FileSystemAssetCatalog(Path.Combine(AppContext.BaseDirectory, "assets", "icons"));
        var logger = new FileLogWriter(Path.Combine(projectRoot, "logs"));
        var shell = new DesktopShellService();

        // The user works in the same CorelDRAW this application drives, so never quit it on exit.
        _corelService = new CorelAutomationService(assetCatalog, message => logger.Write(message)) { KeepApplicationOpen = true };

        // AI settings live in the user's profile (never in the repository); the key is DPAPI-encrypted.
        var aiRuntime = new AiRuntime(
            new AiSettingsStore(AiSettingsStore.DefaultFilePath, new DpapiSecretProtector()),
            message => logger.Write(message));

        // Composition root. The router sends requests to the AI planner when one is configured and
        // selected, and to the built-in planner otherwise. Another provider is one more IAiClient.
        var planner = new PlannerRouter(
            new DeterministicCommandPlanner(),
            () => aiRuntime.Planner,
            () => aiRuntime.Settings.PlannerMode);
        _referenceTemp = new ReferenceTempStore();
        var viewModel = new OperatorViewModel(new OperatorServices(
            Corel: _corelService,
            Inspector: new CorelDocumentInspector(_corelService),
            Executor: new CorelActionExecutor(_corelService),
            Planner: planner,
            Ai: aiRuntime,
            Recipes: new JsonRecipeStore(Path.Combine(dataFolder, "recipes")),
            Assets: new JsonAssetLibrary(Path.Combine(dataFolder, "assets")),
            History: new JsonExecutionHistoryStore(Path.Combine(dataFolder, "history")),
            Vision: new AiReferenceAnalyzer(
                () => aiRuntime.Client,
                // JPG/PNG/PDF/SVG are read locally; CDR is read by CorelDRAW itself once it is connected.
                CompositeReferencePreviewRenderer.CreateDefault(new CorelReferencePreviewRenderer(_corelService))),
            Reconstruction: new ReferenceReconstructionPlanner(
                new InstalledFontResolver(System.Windows.Media.Fonts.SystemFontFamilies.Select(family => family.Source)),
                new SkiaReferenceImageCropper(_referenceTemp)),
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

    /// <summary>
    /// Makes the whole user interface follow one culture: resource lookups, number and date formatting,
    /// and WPF bindings. The default is Turkish (tr-TR); an English package only has to set
    /// <see cref="Msg.Culture"/> before this runs. Files and JSON always use the invariant culture.
    /// </summary>
    private static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_corelService is not null)
        {
            _corelService.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _referenceTemp?.Dispose();
        base.OnExit(e);
    }

    private async Task CaptureTabsAndExitAsync(OperatorWindow window, OperatorViewModel viewModel, string folder)
    {
        try
        {
            // Exercise the reference flow without CorelDRAW or an AI call: a vector reference is analysed
            // locally (it is never uploaded) and rebuilt by importing it at the requested size.
            var sample = Path.Combine(AppContext.BaseDirectory, "assets", "icons", "no-entry-hand.svg");
            if (File.Exists(sample))
            {
                viewModel.AddReferences([sample]);
                viewModel.Request = "Bunun aynısını 500x700 mm olarak CorelDRAW'da yap.";
                viewModel.PreparePlanCommand.Execute(null);
            }
            else
            {
                viewModel.Request = "500x700 mm belge oluştur\nOrtaya GİRİŞ YASAKTIR yaz";
                viewModel.PreparePlanCommand.Execute(null);
            }

            await Task.Delay(600);
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
