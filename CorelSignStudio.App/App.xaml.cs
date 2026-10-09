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
using CorelSignStudio.Domain.Production;
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

        // A source checkout keeps its files in the repository; an installed copy uses the user's profile.
        var paths = AppPaths.Resolve();
        var dataFolder = paths.DataFolder;
        var outputFolder = paths.OutputFolder;
        var assetCatalog = new FileSystemAssetCatalog(Path.Combine(AppContext.BaseDirectory, "assets", "icons"));
        var logger = new FileLogWriter(paths.LogFolder);
        DispatcherUnhandledException += (_, args) =>
        {
            // Never show a raw exception: the details go to the log, the user gets one plain sentence.
            logger.Write("Unhandled exception.", args.Exception);
            MessageBox.Show(Ui.T("Error.Unexpected"), Ui.T("App.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
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
        var corel = _corelService;
        var inspector = new CorelDocumentInspector(corel);
        var executor = new CorelActionExecutor(corel);
        var installedFonts = System.Windows.Media.Fonts.SystemFontFamilies.Select(family => family.Source).ToArray();

        // JPG/PNG/PDF/SVG are read locally; CDR is read by CorelDRAW itself once it is connected.
        var referencePreviews = CompositeReferencePreviewRenderer.CreateDefault(new CorelReferencePreviewRenderer(corel));
        var viewModel = new OperatorViewModel(new OperatorServices(
            ConnectCorel: async () => (await corel.ConnectAsync(visible: true)).Version,
            Inspector: inspector,
            Executor: executor,
            Planner: planner,
            Ai: aiRuntime,
            Recipes: new JsonRecipeStore(Path.Combine(dataFolder, "recipes")),
            Assets: new JsonAssetLibrary(Path.Combine(dataFolder, "assets")),
            History: new JsonExecutionHistoryStore(Path.Combine(dataFolder, "history")),
            // The same file, page and request is not sent to the provider twice in one session.
            Vision: new CachingReferenceVisionAnalyzer(new AiReferenceAnalyzer(() => aiRuntime.Client, referencePreviews)),
            Reconstruction: new ReferenceReconstructionPlanner(
                new InstalledFontResolver(installedFonts, supportsText: FontCoverage.Supports),
                new SkiaReferenceImageCropper(_referenceTemp)),
            ReferenceAnalyzer: new FileReferenceAnalyzer(),
            ReferencePlanBuilder: new ImportReferencePlanBuilder(),

            // Measured comparison always; a vision model's opinion is added when a provider is configured.
            Comparison: new VisualComparisonService(visual: new AiVisualComparer(() => aiRuntime.Client)),
            Corrections: new VisualCorrectionPlanner(),
            PagePreview: new CorelPagePreviewRenderer(corel),
            ReferencePreviews: referencePreviews,
            Preflight: new DesignPreflightService(),
            InstalledFonts: installedFonts,
            PdfPageCount: PdfPages.Count,
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
            viewModel.ReviewTabIndex = 1;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            window.SaveScreenshot(Path.Combine(folder, "0-operator-comparison.png"));
            viewModel.ReviewTabIndex = 0;
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

/// <summary>Whether an installed font can actually draw a text (Turkish letters, Arabic, …).</summary>
public static class FontCoverage
{
    public static bool Supports(string fontFamily, string text)
    {
        try
        {
            var typeface = new System.Windows.Media.Typeface(fontFamily);
            if (!typeface.TryGetGlyphTypeface(out var glyphs))
            {
                return false;
            }

            foreach (var rune in text.EnumerateRunes())
            {
                if (!System.Text.Rune.IsWhiteSpace(rune) && !System.Text.Rune.IsControl(rune) && !glyphs.CharacterToGlyphMap.ContainsKey(rune.Value))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}
