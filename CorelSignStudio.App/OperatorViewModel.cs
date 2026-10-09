using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CorelSignStudio.AI;
using CorelSignStudio.Corel;
using CorelSignStudio.Domain.Ai;
using CorelSignStudio.Domain.Ai.Vision;
using CorelSignStudio.Domain.Assets;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Batch;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.Planning;
using CorelSignStudio.Domain.Production;
using CorelSignStudio.Domain.Recipes;
using CorelSignStudio.Domain.References;
using CorelSignStudio.Storage;

namespace CorelSignStudio.App;

public sealed record ShapeRow(string Id, string Type, string Name, string Text, string X, string Y, string Width, string Height, string Layer, string Group);

public sealed record BatchPreviewRow(int Row, string OutputName, string Status);

public sealed record PlannerModeOption(PlannerMode Mode, string DisplayName);

public sealed record HistoryRow(ExecutionHistoryEntry Entry, string Time, string Name, string Result);

public sealed class RecipeVariableRow(RecipeVariable variable) : ObservableObject
{
    private string _value = variable.DefaultValue ?? "";

    public string Name { get; } = variable.Name;
    public string Type { get; } = Ui.T("Recipe.Type." + variable.Type);
    public string Description { get; } = variable.Description ?? (variable.IsRequired ? Ui.T("Recipe.Required") : "");
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}

/// <summary>
/// Everything the operator window needs from the outside world. <paramref name="ConnectCorel"/> connects to
/// CorelDRAW and returns its version.
/// </summary>
public sealed record OperatorServices(
    Func<Task<string>> ConnectCorel,
    ICorelDocumentInspector Inspector,
    ICorelActionExecutor Executor,
    PlannerRouter Planner,
    AiRuntime Ai,
    IRecipeStore Recipes,
    IAssetLibrary Assets,
    IExecutionHistoryStore History,
    IReferenceVisionAnalyzer Vision,
    IReferenceReconstructionPlanner Reconstruction,
    IReferenceAnalyzer ReferenceAnalyzer,
    IReferencePlanBuilder ReferencePlanBuilder,
    IVisualComparisonService Comparison,
    IVisualCorrectionPlanner Corrections,
    ICorelPagePreviewRenderer? PagePreview,
    IReferencePreviewRenderer? ReferencePreviews,
    IDesignPreflightService Preflight,
    IReadOnlyCollection<string> InstalledFonts,
    Func<string, int> PdfPageCount,
    IDesktopShellService Shell,
    FileLogWriter FileLog,
    string DataFolder,
    string DefaultOutputFolder);

/// <summary>
/// View model of the operator control centre. It never touches COM: it talks to CorelDRAW only through
/// the inspector/executor abstractions and shows plans for review before anything is executed.
/// </summary>
public sealed partial class OperatorViewModel : ObservableObject
{
    private readonly OperatorServices _services;
    private readonly List<AsyncRelayCommand> _asyncCommands = [];
    private readonly List<RelayCommand> _commands = [];

    private bool _isBusy;
    private bool _isConnected;
    private string _connectionText = Ui.T("Connection.NotConnected");
    private string _activeDocumentText = Ui.T("Document.NotInspected");
    private string _statusMessage = Ui.T("Status.Ready");
    private string _request = "";
    private string _planMessage = Ui.T("Plan.None");
    private string _recipeName = "";
    private string _recipeVariables = "";
    private int _selectedTabIndex;
    private bool _rollbackOnFailure = true;
    private string _outputFolder;
    private DocumentSnapshot? _snapshot;
    private AutomationPlan? _plan;
    private AutomationPlan? _lastSuccessfulPlan;
    private ReferenceInput? _selectedReference;
    private ShapeRow? _selectedShape;
    private Recipe? _selectedRecipe;
    private Recipe? _batchRecipe;
    private string _batchDataPath = "";
    private string? _selectedWorksheet;
    private string _batchDataSummary = "";
    private IReadOnlyList<BatchRow> _batchData = [];
    private string _batchNamePattern = "";
    private bool _batchCdr = true;
    private bool _batchPdf = true;
    private bool _batchPng;
    private bool _batchSvg;
    private double _batchPercent;
    private string _batchStatus = Ui.T("Batch.Status.Initial");
    private Asset? _selectedAsset;
    private string _assetSearch = "";
    private string _assetCategory = "";
    private string _assetTags = "";
    private HistoryRow? _selectedHistory;
    private AiProviderInfo _selectedAiProvider;
    private string _aiModel;
    private PlannerModeOption _selectedPlannerMode;
    private bool _aiDebugLogging;
    private string _aiTestResult = "";
    private readonly List<PlanningTurn> _conversation = [];
    private ReferenceAnalysis? _analysis;
    private ReferenceInput? _analysisReference;
    private string _analysisRequestText = "";
    private string _pendingReferenceRequest = "";
    private string _analysisSummary = "";

    public OperatorViewModel(OperatorServices services)
    {
        _services = services;
        _outputFolder = services.DefaultOutputFolder;
        var aiSettings = services.Ai.Settings;
        _selectedAiProvider = services.Ai.Providers.FirstOrDefault(provider => provider.Id == aiSettings.Provider) ?? services.Ai.Providers[0];
        _aiModel = aiSettings.Model;
        _selectedPlannerMode = PlannerModeOptions.First(option => option.Mode == aiSettings.PlannerMode);
        _aiDebugLogging = aiSettings.DebugLogging;
        TestAiCommand = Async(TestAiConnectionAsync);

        ConnectCommand = Async(ConnectAsync);
        InspectCommand = Async(InspectAsync);
        CancelCommand = new RelayCommand(() => _operationCancellation?.Cancel(), () => _operationCancellation is not null);
        PreparePlanCommand = Async(PreparePlanAsync);
        AnalyzeReferenceCommand = Async(
            () => RunCancellableAsync(Ui.T("Status.Analyzing"), async token => { await AnalyzeReferenceAsync(CombinedReferenceRequest(), force: true, token); }),
            () => References.Count > 0);
        References.CollectionChanged += (_, _) =>
        {
            ClearAnalysis();
            RaisePdfPageState();
            RaiseCommandStates();
        };
        ExecuteCommand = Async(ExecutePlanAsync, () => _plan is not null);
        CompareCommand = Async(CompareWithReferenceAsync, () => HasComparisonContext && _snapshot is not null);
        ImproveCommand = Async(ImproveAutomaticallyAsync, () => HasComparisonContext && _snapshot is not null);
        PreflightCommand = Async(RunPreflightAsync, () => _snapshot is not null);
        PreviewPdfPageCommand = Async(PreviewPdfPageAsync, () => HasPdfPages && SelectedPdfPage is not null && _services.ReferencePreviews is not null);
        SaveRecipeCommand = Command(SaveRecipe, () => (_lastSuccessfulPlan ?? _plan) is not null && !string.IsNullOrWhiteSpace(RecipeName));
        ClearPlanCommand = Command(
            () =>
            {
                _conversation.Clear();
                SetPlan(null, Ui.T("Plan.Cleared"));
            },
            () => _plan is not null || _conversation.Count > 0);
        AddReferenceCommand = Command(BrowseReferences);
        RemoveReferenceCommand = Command(() => References.Remove(SelectedReference!), () => SelectedReference is not null);
        InsertShapeIdCommand = Command(InsertSelectedShapeId, () => SelectedShape is not null);

        UseRecipeCommand = Command(UseSelectedRecipe, () => SelectedRecipe is not null);
        DeleteRecipeCommand = Command(DeleteSelectedRecipe, () => SelectedRecipe is not null);

        BrowseBatchDataCommand = Command(BrowseBatchData);
        PreviewBatchCommand = Command(() => PreviewBatch(), () => BatchRecipe is not null);
        RunBatchCommand = Async(RunBatchAsync, () => BatchRecipe is not null && _batchData.Count > 0 && BatchFormats().Count > 0);

        AddAssetCommand = Command(AddAssets);
        RemoveAssetCommand = Command(RemoveSelectedAsset, () => SelectedAsset is not null);
        PlaceAssetCommand = Command(PlaceSelectedAsset, () => SelectedAsset is not null);

        ReloadHistoryPlanCommand = Command(ReloadHistoryPlan, () => SelectedHistory is not null);
        RefreshHistoryCommand = Command(RefreshHistory);

        BrowseOutputCommand = Command(BrowseOutput);
        OpenOutputCommand = new RelayCommand(OpenOutputFolder);
        OpenDataFolderCommand = new RelayCommand(() => TryShell(() => _services.Shell.OpenFolder(_services.DataFolder)));
        OpenLegacyToolCommand = new RelayCommand(() => OpenLegacyToolRequested?.Invoke());

        RefreshRecipes();
        RefreshAssets();
        RefreshHistory();
        AddLog(Ui.T("Log.Ready"));
    }

    public event Action? OpenLegacyToolRequested;

    // ---- Collections -------------------------------------------------------------------------

    public ObservableCollection<ReferenceInput> References { get; } = [];
    public ObservableCollection<string> ProposedOperations { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<ShapeRow> Shapes { get; } = [];
    public ObservableCollection<Recipe> Recipes { get; } = [];
    public ObservableCollection<RecipeVariableRow> RecipeVariableRows { get; } = [];
    public ObservableCollection<BatchPreviewRow> BatchRows { get; } = [];
    public ObservableCollection<string> WorksheetNames { get; } = [];
    public ObservableCollection<string> BatchSampleRows { get; } = [];
    public ObservableCollection<Asset> Assets { get; } = [];
    public ObservableCollection<HistoryRow> HistoryRows { get; } = [];
    public IReadOnlyList<string> ExampleCommands => DeterministicCommandPlanner.Examples;

    // ---- Commands ----------------------------------------------------------------------------

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand InspectCommand { get; }
    public AsyncRelayCommand PreparePlanCommand { get; }
    public AsyncRelayCommand AnalyzeReferenceCommand { get; }
    public AsyncRelayCommand ExecuteCommand { get; }
    public RelayCommand SaveRecipeCommand { get; }
    public RelayCommand ClearPlanCommand { get; }
    public RelayCommand AddReferenceCommand { get; }
    public RelayCommand RemoveReferenceCommand { get; }
    public RelayCommand InsertShapeIdCommand { get; }
    public RelayCommand UseRecipeCommand { get; }
    public RelayCommand DeleteRecipeCommand { get; }
    public RelayCommand BrowseBatchDataCommand { get; }
    public RelayCommand PreviewBatchCommand { get; }
    public AsyncRelayCommand RunBatchCommand { get; }
    public AsyncRelayCommand TestAiCommand { get; }
    public RelayCommand AddAssetCommand { get; }
    public RelayCommand RemoveAssetCommand { get; }
    public RelayCommand PlaceAssetCommand { get; }
    public RelayCommand ReloadHistoryPlanCommand { get; }
    public RelayCommand RefreshHistoryCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenLegacyToolCommand { get; }

    // ---- State -------------------------------------------------------------------------------

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }
    public string ConnectionText { get => _connectionText; private set => SetProperty(ref _connectionText, value); }
    public string ActiveDocumentText { get => _activeDocumentText; private set => SetProperty(ref _activeDocumentText, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string Request { get => _request; set => SetProperty(ref _request, value); }
    public string PlanMessage { get => _planMessage; private set => SetProperty(ref _planMessage, value); }

    /// <summary>What the analyzer found in the reference, for review before anything is planned.</summary>
    public string AnalysisSummary
    {
        get => _analysisSummary;
        private set
        {
            if (SetProperty(ref _analysisSummary, value))
            {
                OnPropertyChanged(nameof(HasAnalysis));
            }
        }
    }

    public bool HasAnalysis => !string.IsNullOrWhiteSpace(AnalysisSummary);
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }
    public bool RollbackOnFailure { get => _rollbackOnFailure; set => SetProperty(ref _rollbackOnFailure, value); }
    public string OutputFolder { get => _outputFolder; set => SetProperty(ref _outputFolder, value); }
    public string DataFolder => _services.DataFolder;
    public string PlannerName => _services.Planner.ActivePlannerName;

    // ---- AI settings ---------------------------------------------------------------------------

    public IReadOnlyList<AiProviderInfo> AiProviderOptions => _services.Ai.Providers;

    public IReadOnlyList<PlannerModeOption> PlannerModeOptions { get; } =
    [
        new(PlannerMode.Ai, Msg.Get("Ai.Mode.Ai")),
        new(PlannerMode.Deterministic, Msg.Get("Ai.Mode.Deterministic")),
    ];

    public AiProviderInfo SelectedAiProvider { get => _selectedAiProvider; set => SetProperty(ref _selectedAiProvider, value); }
    public string AiModel { get => _aiModel; set => SetProperty(ref _aiModel, value); }
    public PlannerModeOption SelectedPlannerMode { get => _selectedPlannerMode; set => SetProperty(ref _selectedPlannerMode, value); }
    public bool AiDebugLogging { get => _aiDebugLogging; set => SetProperty(ref _aiDebugLogging, value); }
    public string AiTestResult { get => _aiTestResult; private set => SetProperty(ref _aiTestResult, value); }

    public string AiKeyStatus => _services.Ai.KeySource switch
    {
        ApiKeySource.Stored => Ui.T("Settings.Ai.Key.Stored"),
        ApiKeySource.EnvironmentVariable => Ui.F("Settings.Ai.Key.Environment", _services.Ai.Providers.First(provider => provider.Id == _services.Ai.Settings.Provider).ApiKeyEnvironmentVariable),
        _ => Ui.T("Settings.Ai.Key.None"),
    };

    public string AiSettingsPathText => Ui.F("Settings.Ai.StoredAt", _services.Ai.SettingsFilePath);

    /// <summary>Called by the window with the content of the password box (which cannot be data-bound).</summary>
    public void SaveAiSettings(string? typedApiKey)
    {
        try
        {
            _services.Ai.Save(
                new AiSettings
                {
                    Provider = SelectedAiProvider.Id,
                    Model = string.IsNullOrWhiteSpace(AiModel) ? SelectedAiProvider.DefaultModel : AiModel.Trim(),
                    PlannerMode = SelectedPlannerMode.Mode,
                    DebugLogging = AiDebugLogging,
                },
                typedApiKey);
            AiModel = _services.Ai.Settings.Model;
            AiTestResult = Ui.T("Settings.Ai.Saved");
            AddLog(Ui.F("Log.AiSettingsSaved", PlannerNameAfterRefresh()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            AiTestResult = Ui.T("Settings.Ai.SaveFailed");
            _services.FileLog.Write("Saving AI settings failed.", exception);
        }
    }

    public void RemoveAiKey()
    {
        _services.Ai.RemoveStoredApiKey();
        AiTestResult = Ui.T("Settings.Ai.KeyRemoved");
        PlannerNameAfterRefresh();
    }

    private string PlannerNameAfterRefresh()
    {
        OnPropertyChanged(nameof(AiKeyStatus));
        OnPropertyChanged(nameof(PlannerName));
        return PlannerName;
    }

    private Task TestAiConnectionAsync() => RunBusyAsync(Ui.T("Status.AiTesting"), async () =>
    {
        AiTestResult = Ui.T("Settings.Ai.Testing");
        try
        {
            var response = await _services.Ai.TestConnectionAsync();
            AiTestResult = Ui.F("Settings.Ai.TestOk", response.Model ?? _services.Ai.Settings.Model);
        }
        catch (AiClientException exception)
        {
            // The user gets the plain explanation; the technical reason goes to the log file.
            AiTestResult = exception.UserMessage;
            _services.FileLog.Write($"AI connection test failed ({exception.Kind}).", exception);
        }
    });
    public string LogPath => _services.FileLog.LogPath;
    public string LogFileText => Ui.F("Settings.LogFile", _services.FileLog.LogPath);

    public string RecipeName
    {
        get => _recipeName;
        set
        {
            if (SetProperty(ref _recipeName, value))
            {
                RaiseCommandStates();
            }
        }
    }

    /// <summary>One "VARIABLE = value from the plan" per line.</summary>
    public string RecipeVariables { get => _recipeVariables; set => SetProperty(ref _recipeVariables, value); }

    public ReferenceInput? SelectedReference
    {
        get => _selectedReference;
        set
        {
            if (SetProperty(ref _selectedReference, value))
            {
                RaisePdfPageState();
                RaiseCommandStates();
            }
        }
    }

    public ShapeRow? SelectedShape
    {
        get => _selectedShape;
        set
        {
            if (SetProperty(ref _selectedShape, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public Recipe? SelectedRecipe
    {
        get => _selectedRecipe;
        set
        {
            if (SetProperty(ref _selectedRecipe, value))
            {
                RecipeVariableRows.Clear();
                foreach (var variable in value?.Variables ?? [])
                {
                    RecipeVariableRows.Add(new RecipeVariableRow(variable));
                }

                RaiseCommandStates();
            }
        }
    }

    public Recipe? BatchRecipe
    {
        get => _batchRecipe;
        set
        {
            if (SetProperty(ref _batchRecipe, value))
            {
                OnPropertyChanged(nameof(BatchRecipeHint));
                RaiseCommandStates();
            }
        }
    }

    public string BatchRecipeHint => BatchRecipe is null
        ? Ui.T("Batch.Hint.NoRecipe")
        : BatchRecipe.Variables.Count == 0
            ? Ui.T("Batch.Hint.NoVariables")
            : Ui.F("Batch.Hint.Columns", string.Join(", ", BatchRecipe.Variables.Select(variable => variable.Name)));

    /// <summary>The batch data file: CSV or Excel (.xlsx).</summary>
    public string BatchDataPath
    {
        get => _batchDataPath;
        set
        {
            if (SetProperty(ref _batchDataPath, value))
            {
                LoadWorksheets();
            }
        }
    }

    /// <summary>The worksheet rows are read from; null for CSV.</summary>
    public string? SelectedWorksheet
    {
        get => _selectedWorksheet;
        set
        {
            if (SetProperty(ref _selectedWorksheet, value))
            {
                LoadBatchData();
            }
        }
    }

    public bool HasWorksheets => WorksheetNames.Count > 0;
    public string BatchDataSummary { get => _batchDataSummary; private set => SetProperty(ref _batchDataSummary, value); }
    public int BatchRecordCount => _batchData.Count;
    public string BatchNamePattern { get => _batchNamePattern; set => SetProperty(ref _batchNamePattern, value); }
    public bool BatchCdr { get => _batchCdr; set => SetFormat(ref _batchCdr, value); }
    public bool BatchPdf { get => _batchPdf; set => SetFormat(ref _batchPdf, value); }
    public bool BatchPng { get => _batchPng; set => SetFormat(ref _batchPng, value); }
    public bool BatchSvg { get => _batchSvg; set => SetFormat(ref _batchSvg, value); }

    private void SetFormat(ref bool field, bool value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (SetProperty(ref field, value, name))
        {
            RaiseCommandStates();
        }
    }
    public double BatchPercent { get => _batchPercent; private set => SetProperty(ref _batchPercent, value); }
    public string BatchStatus { get => _batchStatus; private set => SetProperty(ref _batchStatus, value); }

    public Asset? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (SetProperty(ref _selectedAsset, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string AssetSearch
    {
        get => _assetSearch;
        set
        {
            if (SetProperty(ref _assetSearch, value))
            {
                RefreshAssets();
            }
        }
    }

    public string AssetCategory { get => _assetCategory; set => SetProperty(ref _assetCategory, value); }
    public string AssetTags { get => _assetTags; set => SetProperty(ref _assetTags, value); }

    public HistoryRow? SelectedHistory
    {
        get => _selectedHistory;
        set
        {
            if (SetProperty(ref _selectedHistory, value))
            {
                RaiseCommandStates();
            }
        }
    }

    // ---- Operator ----------------------------------------------------------------------------

    /// <summary>Adds dropped or browsed files as references; unsupported files are reported, not ignored silently.</summary>
    public void AddReferences(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                var reference = ReferenceInput.FromFile(path);
                if (References.All(existing => !string.Equals(existing.FilePath, reference.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    RegisterPdfPages(reference);
                    References.Add(reference);
                    AddLog(Ui.F("Log.ReferenceAdded", reference.FileName, reference.FileTypeLabel, Ui.T(reference.IsVector ? "Log.ReferenceVector" : "Log.ReferenceBitmap")));
                }
            }
            catch (NotSupportedException exception)
            {
                AddLog(exception.Message);
            }
        }
    }

    private async Task ConnectAsync()
    {
        await RunBusyAsync(Ui.T("Status.Connecting"), EnsureConnectedAsync);
    }

    private async Task EnsureConnectedAsync()
    {
        // Always (re)connect: it is cheap, and it recovers when the user closed and reopened CorelDRAW.
        var version = await _services.ConnectCorel();
        IsConnected = true;
        ConnectionText = Ui.F("Connection.Connected", version);
    }

    private Task InspectAsync() => RunBusyAsync(Ui.T("Status.Inspecting"), async () =>
    {
        await EnsureConnectedAsync();
        await RefreshSnapshotAsync();
        AddLog(_snapshot is null
            ? Ui.T("Log.NoDocument")
            : Ui.F("Log.Inspected", _snapshot.Title, _snapshot.ShapeCount));
    });

    private async Task RefreshSnapshotAsync()
    {
        _snapshot = await _services.Inspector.InspectActiveDocumentAsync();
        Shapes.Clear();
        RaiseCommandStates();
        if (_snapshot is null)
        {
            ActiveDocumentText = Ui.T("Document.NoneOpen");
            return;
        }

        var page = _snapshot.ActivePage;
        ActiveDocumentText =
            Ui.F("Document.Summary", _snapshot.Title, Mm(page?.WidthMm ?? 0), Mm(page?.HeightMm ?? 0), _snapshot.ShapeCount) +
            (_snapshot.SelectedShapeIds.Count > 0 ? Ui.F("Document.SummarySelected", _snapshot.SelectedShapeIds.Count) : "");
        foreach (var shape in _snapshot.AllShapes())
        {
            Shapes.Add(new ShapeRow(
                shape.Id,
                Msg.Get("ShapeKind." + shape.Type),
                shape.Name ?? "",
                shape.Text?.ReplaceLineEndings(" ") ?? "",
                Mm(shape.Bounds.XMm),
                Mm(shape.Bounds.YMm),
                Mm(shape.Bounds.WidthMm),
                Mm(shape.Bounds.HeightMm),
                shape.LayerName,
                shape.ParentGroupId ?? ""));
        }
    }

    // ---- Reference reconstruction --------------------------------------------------------------

    /// <summary>The request including the part typed before a clarification question was asked.</summary>
    private string CombinedReferenceRequest() => string.Join("\n", new[] { _pendingReferenceRequest, Request }.Where(text => !string.IsNullOrWhiteSpace(text))).Trim();

    private ReferenceInput? ReferenceToAnalyze => SelectedReference ?? References.FirstOrDefault();

    private bool WantsReconstruction(string request) =>
        ReferenceToAnalyze is not null &&
        (_pendingReferenceRequest.Length > 0 || ReconstructionIntent().IsMatch(request.ToLower(Msg.Culture)));

    private void ClearAnalysis()
    {
        _analysis = null;
        _analysisReference = null;
        _analysisRequestText = "";
        _analysisPage = 0;
        _pendingReferenceRequest = "";
        AnalysisSummary = "";
    }

    /// <summary>Analyses the selected reference. Nothing is uploaded before this is called.</summary>
    /// <returns>True when a usable analysis is available.</returns>
    private async Task<bool> AnalyzeReferenceAsync(string request, bool force, CancellationToken token)
    {
        var reference = ReferenceToAnalyze;
        if (reference is null)
        {
            SetPlan(null, Ui.T("Plan.NoReference"));
            return false;
        }

        // A size ("500x700 mm") does not change what is on the reference, so it never forces a new analysis.
        var content = SizeText().Replace(request, " ").Trim();
        var page = ResolvePage(reference, request);
        if (!force && _analysis is not null && ReferenceEquals(_analysisReference, reference) && content == _analysisRequestText && (page ?? 0) == _analysisPage)
        {
            return true;
        }

        StatusMessage = Ui.F("Status.Busy", Ui.T("Status.Analyzing"));

        // "Referansı Analiz Et" always asks again; planning reuses an identical analysis from this session.
        var result = await _services.Vision.AnalyzeAsync(
            new ReferenceAnalysisRequest { Reference = reference, UserRequest = request, PageNumber = page, BypassCache = force }, token);
        token.ThrowIfCancellationRequested();
        if (result.Diagnostics.TechnicalError is { } technical)
        {
            _services.FileLog.Write($"Reference analysis failed ({result.Diagnostics.ErrorKind}): {technical}");
        }

        if (result.Status == AiPlanningStatus.NeedsClarification)
        {
            _pendingReferenceRequest = request;
            SetPlan(null, Ui.F("Plan.Clarification", result.ClarificationQuestion));
            AddLog(Ui.F("Log.AiAsks", result.ClarificationQuestion));
            Request = "";
            StatusMessage = Ui.T("Status.NeedsAnswer");
            return false;
        }

        if (!result.IsReady)
        {
            _analysis = null;
            AnalysisSummary = "";
            SetPlan(null, result.UserMessage);
            AddLog(result.UserMessage);
            StatusMessage = Ui.T("Status.NoPlan");
            return false;
        }

        _analysis = result.Analysis;
        _analysisReference = reference;
        _analysisRequestText = content;
        _analysisPage = page ?? 0;
        AnalysisSummary = DescribeAnalysis(result.Analysis!, request);
        AddLog(Ui.F("Log.Analyzed", reference.FileName, result.Analysis!.Elements.Count));
        StatusMessage = Ui.T("Status.Analyzed");
        return true;
    }

    private async Task ReconstructFromReferenceAsync(CancellationToken token)
    {
        var request = CombinedReferenceRequest();
        if (!await AnalyzeReferenceAsync(request, force: false, token))
        {
            return;
        }

        var result = _services.Reconstruction.Plan(new ReconstructionRequest
        {
            Reference = _analysisReference!,
            Analysis = _analysis!,
            UserRequest = request,
            Assets = _services.Assets.GetAll(),
        });

        if (result.Status == AiPlanningStatus.NeedsClarification)
        {
            // Production information is never guessed: remember the request and wait for the answer.
            _pendingReferenceRequest = request;
            SetPlan(null, Ui.F("Plan.Question", result.ClarificationQuestion));
            AddLog(result.ClarificationQuestion!);
            Request = "";
            StatusMessage = Ui.T("Status.NeedsAnswer");
            return;
        }

        _pendingReferenceRequest = "";
        var warnings = _analysis!.Warnings.Concat(result.Warnings).Distinct().ToList();
        var message = result.UserMessage + (warnings.Count > 0 ? Ui.F("Plan.Warnings", string.Join(" • ", warnings)) : "");
        SetPlan(result.Plan, message);
        RememberPendingReconstruction(result, warnings);
        if (!result.IsReady)
        {
            AddLog(result.UserMessage);
        }
    }

    private static string DescribeAnalysis(ReferenceAnalysis analysis, string request)
    {
        var lines = new List<string> { Ui.F("Analysis.Source", analysis.FileName) };
        if (analysis.PageCount > 1)
        {
            lines.Add(Ui.F("Analysis.Page", analysis.PageNumber, analysis.PageCount));
        }

        // The size the user wrote wins over the file's own; with neither, it must be asked for.
        lines.Add(DimensionParser.TryParse(request, out var stated)
            ? Ui.F("Analysis.SizeFromRequest", $"{Msg.Number(stated.WidthMm)} × {Msg.Number(stated.HeightMm)} mm")
            : analysis.PhysicalSize is { } size
                ? Ui.F("Analysis.Size", $"{Msg.Number(size.WidthMm)} × {Msg.Number(size.HeightMm)} mm")
                : Ui.T("Analysis.SizeNeeded"));

        if (analysis.CanReuseVectorContent)
        {
            lines.Add(Ui.T("Analysis.Vector"));
        }
        else
        {
            lines.Add(Ui.T("Analysis.Detected"));
            lines.AddRange(analysis.Elements.Where(element => element.Kind != ReferenceElementKind.Group)
                .GroupBy(element => element.Kind).OrderByDescending(group => group.Count())
                .Select(group => Ui.F("Analysis.Count", group.Count(), Msg.Get("Vision.Kind." + group.Key))));

            var texts = analysis.Elements.Where(element => element.Kind == ReferenceElementKind.Text).ToList();
            if (texts.Count > 0)
            {
                lines.Add(Ui.T("Analysis.Texts"));
                lines.AddRange(texts.Select(text => Ui.F("Analysis.TextLine", text.Text!.ReplaceLineEndings(" / "), text.TextUncertain ? Ui.T("Analysis.TextUncertain") : "")));
            }

            var special = analysis.Elements.Where(element => element.Strategy is ReconstructionStrategy.NeedsUserAsset
                or ReconstructionStrategy.UnsupportedComplexArtwork or ReconstructionStrategy.ImportImage or ReconstructionStrategy.UseAsset).ToList();
            if (special.Count > 0)
            {
                lines.Add(Ui.T("Analysis.Special"));
                lines.AddRange(special.Select(element => Ui.F("Analysis.SpecialLine", element.Label ?? Msg.Get("Vision.Kind." + element.Kind), Msg.Get("Vision.Strategy." + element.Strategy))));
            }
        }

        if (analysis.Confidence is { } confidence)
        {
            lines.Add(Ui.F("Analysis.Confidence", Math.Round(confidence * 100)));
        }

        if (analysis.AppliedModifications.Count > 0)
        {
            lines.Add(Ui.T("Analysis.Modifications"));
            lines.AddRange(analysis.AppliedModifications.Select(change => Ui.F("Analysis.Line", change)));
        }

        var notes = analysis.Warnings.Concat(analysis.Notes).ToList();
        if (notes.Count > 0)
        {
            lines.Add(Ui.T("Analysis.Warnings"));
            lines.AddRange(notes.Select(note => Ui.F("Analysis.Line", note)));
        }

        return string.Join(Environment.NewLine, lines);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"aynısı|aynisi|yeniden\s+(?:çiz|oluştur|yap)|referans(?:taki|ı|i)|bu\s+(?:görsel|jpg|png|pdf|svg|cdr|resim|tabela|levha|tasarım)\w*.*(?:oluştur|çiz|yap)|vektör\w*\s+.*oluştur|editable|recreate|reproduce|redraw")]
    private static partial System.Text.RegularExpressions.Regex ReconstructionIntent();

    [System.Text.RegularExpressions.GeneratedRegex(@"\d+(?:[.,]\d+)?\s*(?:mm|cm|m)?\s*[x×*]\s*\d+(?:[.,]\d+)?\s*(?:mm|cm|m)?(?![\w])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SizeText();

    private Task PreparePlanAsync() => RunCancellableAsync(Ui.T(_services.Planner.IsAiActive && !string.IsNullOrWhiteSpace(Request) ? "Status.AiPlanning" : "Status.Planning"), async token =>
    {
        if (WantsReconstruction(Request))
        {
            await ReconstructFromReferenceAsync(token);
            return;
        }

        if (string.IsNullOrWhiteSpace(Request))
        {
            if (References.Count == 0)
            {
                SetPlan(null, Ui.T("Plan.TypeOrAttach"));
                return;
            }

            // No instruction, only files: the non-AI baseline is to bring the references into CorelDRAW.
            var actions = new List<CorelAction>();
            foreach (var reference in References)
            {
                var analysis = await _services.ReferenceAnalyzer.AnalyzeAsync(reference);
                foreach (var note in analysis.Notes)
                {
                    AddLog(Ui.F("Log.ReferenceNote", reference.FileName, note));
                }

                var import = _services.ReferencePlanBuilder.BuildPlan(reference, analysis).Actions[0];
                actions.Add(import is ImportFileAction file ? file with { Id = $"import{actions.Count + 1}" } : import);
            }

            SetPlan(
                new AutomationPlan
                {
                    Name = Ui.T("Plan.ImportReferencesName"),
                    Target = actions[0].OpensDocument ? DocumentTarget.NewDocument : DocumentTarget.ActiveDocument,
                    Actions = actions,
                    ReferenceFiles = References.ToArray(),
                },
                Ui.T("Plan.ImportReferences"));
            return;
        }

        var asked = Request.Trim();
        var result = await _services.Planner.PlanAsync(new PlanningRequest
        {
            UserRequest = asked,
            Document = _snapshot,
            References = References.ToArray(),
            Conversation = _conversation.ToArray(),
        }, token);
        token.ThrowIfCancellationRequested();

        if (result.Diagnostics.TechnicalError is { } technical)
        {
            _services.FileLog.Write($"AI planning failed ({result.Diagnostics.ErrorKind}): {technical}");
        }

        switch (result.Status)
        {
            case AiPlanningStatus.NeedsClarification:
                // Keep the exchange so the answer the user types next is planned in context.
                _conversation.Add(new PlanningTurn(asked, result.ClarificationQuestion));
                SetPlan(null, Ui.F("Plan.Clarification", result.ClarificationQuestion));
                AddLog(Ui.F("Log.AiAsks", result.ClarificationQuestion));
                Request = "";
                StatusMessage = Ui.T("Status.NeedsAnswer");
                break;

            case AiPlanningStatus.Ready:
                _conversation.Clear();
                SetPlan(result.Plan, Compose(result));
                break;

            default:
                _conversation.Clear();
                SetPlan(null, Compose(result));
                AddLog(result.UserMessage);
                StatusMessage = Ui.T("Status.NoPlan");
                break;
        }
    });

    /// <summary>Message line above the plan: outcome, the planner's explanation, then any warnings.</summary>
    private static string Compose(AiPlanningResult result)
    {
        var message = result.UserMessage;
        if (!string.IsNullOrWhiteSpace(result.Explanation) && result.IsReady)
        {
            message += " " + result.Explanation.Trim();
        }

        if (result.Warnings.Count > 0)
        {
            message += Ui.F("Plan.Warnings", string.Join(" • ", result.Warnings));
        }

        return message;
    }

    private void SetPlan(AutomationPlan? plan, string message)
    {
        _plan = plan;
        if (!ReferenceEquals(_pendingContext?.Plan, plan))
        {
            _pendingContext = null;
        }

        ProposedOperations.Clear();
        if (plan is not null)
        {
            for (var index = 0; index < plan.Actions.Count; index++)
            {
                var action = plan.Actions[index];
                ProposedOperations.Add($"{index + 1}. {ActionDescriber.Describe(action)}{(action.IsDestructive ? Ui.T("Plan.NeedsConfirmation") : "")}");
            }

            var validation = plan.Validate();
            if (!validation.IsValid)
            {
                message += Ui.F("Plan.Problems", string.Join("; ", validation.Errors));
            }
        }

        PlanMessage = message;
        StatusMessage = plan is null ? Ui.T("Status.NoPlan") : Ui.F("Status.PlanReady", plan.Actions.Count);
        RaiseCommandStates();
    }

    private Task ExecutePlanAsync() => RunCancellableAsync(Ui.T("Status.Executing"), async token =>
    {
        var plan = _plan!;
        if (plan.HasDestructiveActions && !_services.Shell.Confirm(
                Ui.F("Confirm.Destructive", string.Join("\n", plan.Actions.Where(action => action.IsDestructive).Select(ActionDescriber.Describe))),
                Ui.T("Confirm.DestructiveTitle")))
        {
            AddLog(Ui.T("Log.ExecutionCancelled"));
            return;
        }

        await EnsureConnectedAsync();
        AddLog(Ui.F("Log.Executing", plan.Name, plan.Actions.Count));
        var progress = new Progress<ActionProgress>(report =>
        {
            if (report.Result is { } step)
            {
                AddLog(Ui.F("Log.Step", step.Success ? "✓" : "✗", report.Index, report.Total, ActionDescriber.Describe(report.Action)) +
                       (step.Error is null ? "" : Ui.F("Log.StepError", step.Error)));
            }
        });

        var result = await _services.Executor.ExecuteAsync(plan, new ExecutionOptions { RollbackOnFailure = RollbackOnFailure }, progress, token);
        _services.History.Append(new ExecutionHistoryEntry { Plan = plan, Result = result });
        RefreshHistory();

        if (result.Success)
        {
            _lastSuccessfulPlan = plan;
            UpdateContextAfterExecution(plan);
            if (string.IsNullOrWhiteSpace(RecipeName))
            {
                RecipeName = plan.Name;
            }

            AddLog(Ui.F("Log.Done", result.Duration.TotalSeconds.ToString("0.0", Msg.Culture), result.Summary));
            foreach (var file in result.ProducedFiles)
            {
                AddLog(Ui.F("Log.File", file));
            }

            StatusMessage = Ui.T("Status.Completed");
        }
        else
        {
            AddLog(Ui.F("Log.Failed", result.Summary));
            if (result.RollbackNote is not null)
            {
                AddLog(Ui.F("Log.Detail", result.RollbackNote));
            }

            _services.FileLog.Write($"Plan '{plan.Name}' failed: {result.ErrorDetails}");
            StatusMessage = Ui.T(result.Status == PlanExecutionStatus.Cancelled ? "Status.Cancelled" : "Status.Failed");
        }

        await RefreshSnapshotAsync();
    });

    private void SaveRecipe()
    {
        var plan = _lastSuccessfulPlan ?? _plan!;
        try
        {
            var bindings = new List<RecipeVariableBinding>();
            foreach (var line in RecipeVariables.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0 || separator == line.Length - 1)
                {
                    throw new ArgumentException(Ui.F("Recipe.VariableLineInvalid", line));
                }

                bindings.Add(new RecipeVariableBinding(line[..separator].Trim(), line[(separator + 1)..].Trim()));
            }

            var recipe = BuildRecipe(plan, RecipeName.Trim(), bindings);
            var existing = _services.Recipes.FindByName(recipe.Name);
            if (existing is not null)
            {
                if (!_services.Shell.Confirm(Ui.F("Confirm.ReplaceRecipe", recipe.Name), Ui.T("Confirm.ReplaceRecipeTitle")))
                {
                    return;
                }

                recipe = recipe with { Id = existing.Id };
            }

            _services.Recipes.Save(recipe);
            RefreshRecipes();
            AddLog(Ui.F("Log.RecipeSaved", recipe.Name, recipe.Variables.Count,
                recipe.Variables.Count > 0 ? ": " + string.Join(", ", recipe.Variables.Select(variable => variable.Name)) : ""));
        }
        catch (ArgumentException exception)
        {
            AddLog(Ui.F("Log.RecipeNotSaved", exception.Message));
        }
    }

    /// <summary>A value is bound as text when it appears in the plan's texts, otherwise as a number.</summary>
    private static Recipe BuildRecipe(AutomationPlan plan, string name, List<RecipeVariableBinding> bindings)
    {
        var typed = new List<RecipeVariableBinding>();
        foreach (var binding in bindings)
        {
            try
            {
                RecipeBuilder.FromPlan(plan, name, [binding]);
                typed.Add(binding);
            }
            catch (ArgumentException) when (VariableSubstitution.TryParseNumber(binding.Literal, out _))
            {
                typed.Add(binding with { Type = RecipeVariableType.Number });
            }
        }

        return RecipeBuilder.FromPlan(plan, name, typed);
    }

    private void BrowseReferences() =>
        AddReferences(_services.Shell.BrowseForFiles(
            Ui.T("Dialog.ReferencesTitle"),
            Ui.T("Dialog.ReferencesFilter"),
            multiple: true));

    private void InsertSelectedShapeId()
    {
        Request = string.IsNullOrWhiteSpace(Request) ? SelectedShape!.Id : $"{Request.TrimEnd()} {SelectedShape!.Id}";
        SelectedTabIndex = 0;
    }

    // ---- Recipes -----------------------------------------------------------------------------

    /// <summary>Reloads the recipe list after the store changed outside the window (used by tests).</summary>
    public void RefreshRecipesForTests() => RefreshRecipes();

    private void RefreshRecipes()
    {
        var selectedId = SelectedRecipe?.Id;
        var batchId = BatchRecipe?.Id;
        Recipes.Clear();
        foreach (var recipe in _services.Recipes.GetAll())
        {
            Recipes.Add(recipe);
        }

        SelectedRecipe = Recipes.FirstOrDefault(recipe => recipe.Id == selectedId) ?? Recipes.FirstOrDefault();
        BatchRecipe = Recipes.FirstOrDefault(recipe => recipe.Id == batchId) ?? Recipes.FirstOrDefault();
    }

    private void UseSelectedRecipe()
    {
        try
        {
            var values = RecipeVariableRows
                .Where(row => !string.IsNullOrWhiteSpace(row.Value))
                .ToDictionary(row => row.Name, row => row.Value);
            var plan = SelectedRecipe!.Instantiate(values);
            SetPlan(plan, Ui.F("Plan.FromRecipe", SelectedRecipe.Name));
            SelectedTabIndex = 0;
        }
        catch (RecipeVariableException exception)
        {
            AddLog(exception.Message);
            StatusMessage = Ui.T("Status.RecipeNeedsValues");
        }
    }

    private void DeleteSelectedRecipe()
    {
        var recipe = SelectedRecipe!;
        if (_services.Shell.Confirm(Ui.F("Confirm.DeleteRecipe", recipe.Name), Ui.T("Confirm.DeleteRecipeTitle")))
        {
            _services.Recipes.Delete(recipe.Id);
            RefreshRecipes();
            AddLog(Ui.F("Log.RecipeDeleted", recipe.Name));
        }
    }

    // ---- Batch -------------------------------------------------------------------------------

    private void BrowseBatchData()
    {
        var file = _services.Shell.BrowseForFiles(Ui.T("Dialog.BatchDataTitle"), Ui.T("Dialog.BatchDataFilter"), multiple: false).FirstOrDefault();
        if (file is not null)
        {
            BatchDataPath = file;
            PreviewBatch();
        }
    }

    /// <summary>Lists the worksheets of an Excel file (none for CSV) and loads the first one.</summary>
    private void LoadWorksheets()
    {
        WorksheetNames.Clear();
        _selectedWorksheet = null;
        try
        {
            if (File.Exists(BatchDataPath))
            {
                foreach (var name in BatchDataReaders.For(BatchDataPath).GetSheetNames(BatchDataPath))
                {
                    WorksheetNames.Add(name);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or FormatException or ArgumentException or UnauthorizedAccessException)
        {
            // Reported by LoadBatchData below, which fails on the same file for the same reason.
            _services.FileLog.Write("Reading the worksheet list failed.", exception);
        }

        _selectedWorksheet = WorksheetNames.FirstOrDefault();
        OnPropertyChanged(nameof(HasWorksheets));
        OnPropertyChanged(nameof(SelectedWorksheet));
        LoadBatchData();
    }

    /// <summary>Reads the rows of the chosen file and worksheet and summarises them; only five rows are shown.</summary>
    private bool LoadBatchData()
    {
        _batchData = [];
        BatchSampleRows.Clear();
        var loaded = false;
        try
        {
            if (!File.Exists(BatchDataPath))
            {
                BatchDataSummary = "";
            }
            else
            {
                _batchData = BatchDataReaders.For(BatchDataPath).Read(BatchDataPath, SelectedWorksheet);
                var columns = _batchData.Count > 0 ? _batchData[0].Values.Keys.ToArray() : [];
                var lines = new List<string> { Ui.F("Batch.Data.Records", _batchData.Count) };
                if (SelectedWorksheet is not null)
                {
                    lines.Add(Ui.F("Batch.Data.Sheet", SelectedWorksheet));
                }

                if (columns.Length > 0)
                {
                    lines.Add(Ui.F("Batch.Data.Variables", string.Join(", ", columns)));
                }

                BatchDataSummary = string.Join(Environment.NewLine, lines);
                foreach (var row in _batchData.Take(5))
                {
                    BatchSampleRows.Add(Ui.F("Batch.Data.Row", row.RowNumber, string.Join("  •  ", row.Values.Select(pair => $"{pair.Key}: {pair.Value}"))));
                }

                loaded = true;
            }
        }
        catch (Exception exception) when (exception is IOException or FormatException or ArgumentException or UnauthorizedAccessException)
        {
            BatchDataSummary = Ui.F("Batch.Status.ReadError", exception.Message);
            BatchStatus = BatchDataSummary;
            _services.FileLog.Write("Reading batch data failed.", exception);
        }

        OnPropertyChanged(nameof(BatchRecordCount));
        RaiseCommandStates();
        return loaded;
    }

    private List<OutputFormat> BatchFormats()
    {
        var formats = new List<OutputFormat>();
        if (BatchCdr) formats.Add(OutputFormat.Cdr);
        if (BatchPdf) formats.Add(OutputFormat.Pdf);
        if (BatchPng) formats.Add(OutputFormat.Png);
        if (BatchSvg) formats.Add(OutputFormat.Svg);
        return formats;
    }

    private (BatchJob Job, IReadOnlyList<BatchItem> Items)? PreviewBatch()
    {
        BatchRows.Clear();
        BatchPercent = 0;
        try
        {
            if (BatchRecipe is null)
            {
                BatchStatus = Ui.T("Batch.Status.ChooseRecipe");
                return null;
            }

            if (!File.Exists(BatchDataPath))
            {
                BatchStatus = Ui.T("Batch.Status.ChooseData");
                return null;
            }

            // Read again: the file may have been edited since it was chosen.
            if (!LoadBatchData())
            {
                return null;
            }

            if (_batchData.Count == 0)
            {
                BatchStatus = Ui.T("Batch.Status.NoRows");
                return null;
            }

            var formats = BatchFormats();
            if (formats.Count == 0)
            {
                BatchStatus = Ui.T("Batch.Status.ChooseFormat");
                return null;
            }

            var job = new BatchJob
            {
                Name = Ui.F("Batch.JobName", BatchRecipe.Name),
                RecipeId = BatchRecipe.Id,
                Rows = _batchData,
                OutputFolder = OutputFolder,
                OutputNamePattern = string.IsNullOrWhiteSpace(BatchNamePattern) ? null : BatchNamePattern.Trim(),
                Formats = formats,
            };
            var items = BatchExpander.Expand(job, BatchRecipe);
            foreach (var item in items)
            {
                BatchRows.Add(new BatchPreviewRow(item.RowNumber, item.OutputBaseName ?? "", item.IsValid ? Ui.T("Batch.Row.Ready") : Ui.F("Batch.Row.Problem", item.Error)));
            }

            BatchStatus = Ui.F("Batch.Status.Preview", items.Count, items.Count(item => item.IsValid), items.Count(item => !item.IsValid), OutputFolder);
            return (job, items);
        }
        catch (Exception exception) when (exception is IOException or FormatException or ArgumentException)
        {
            BatchStatus = Ui.F("Batch.Status.ReadError", exception.Message);
            return null;
        }
    }

    private Task RunBatchAsync() => RunCancellableAsync(Ui.T("Status.RunningBatch"), async token =>
    {
        if (PreviewBatch() is not { } batch)
        {
            return;
        }

        await EnsureConnectedAsync();
        var recipe = BatchRecipe!;
        AddLog(Ui.F("Log.BatchStarted", batch.Job.Name, batch.Items.Count));
        var progress = new Progress<BatchProgress>(report =>
        {
            BatchPercent = report.Percent;
            BatchStatus = Ui.F("Batch.Status.Progress", report.Completed, report.Total, report.Succeeded, report.Failed) +
                          (report.CurrentOutputName is null ? "" : Ui.F("Batch.Status.Current", report.CurrentOutputName));
        });

        var result = await new BatchRunner(_services.Executor).RunAsync(batch.Job, recipe, progress, cancellationToken: token);

        BatchRows.Clear();
        foreach (var item in result.Items)
        {
            BatchRows.Add(new BatchPreviewRow(item.RowNumber, item.OutputBaseName ?? "", item.Success ? Ui.T("Batch.Row.Done") : Ui.F("Batch.Row.Failed", item.Error)));
        }

        BatchPercent = 100;
        BatchStatus = Ui.F("Batch.Status.Finished", result.Duration.TotalSeconds.ToString("0.0", Msg.Culture), result.Succeeded, result.Failed,
            result.Cancelled ? Ui.T("Batch.Status.Cancelled") : "", OutputFolder);
        AddLog(Ui.F("Log.BatchFinished", result.Succeeded, result.Failed, result.ProducedFiles.Count()));
        if (result.Cancelled)
        {
            StatusMessage = Ui.T("Status.Cancelled");
        }
    });

    // ---- Assets ------------------------------------------------------------------------------

    private void RefreshAssets()
    {
        Assets.Clear();
        foreach (var asset in _services.Assets.Search(new AssetQuery { Text = AssetSearch }))
        {
            Assets.Add(asset);
        }
    }

    private void AddAssets()
    {
        var tags = AssetTags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var file in _services.Shell.BrowseForFiles(Ui.T("Dialog.AssetsTitle"), Ui.T("Dialog.AllFilesFilter"), multiple: true))
        {
            try
            {
                var asset = _services.Assets.Add(file, category: AssetCategory, tags: tags);
                AddLog(Ui.F("Log.AssetAdded", asset.Name, asset.Category));
            }
            catch (IOException exception)
            {
                AddLog(Ui.F("Log.AssetNotAdded", exception.Message));
            }
        }

        RefreshAssets();
    }

    private void RemoveSelectedAsset()
    {
        var asset = SelectedAsset!;
        if (_services.Shell.Confirm(Ui.F("Confirm.RemoveAsset", asset.Name), Ui.T("Confirm.RemoveAssetTitle")))
        {
            _services.Assets.Remove(asset.Id);
            RefreshAssets();
        }
    }

    private void PlaceSelectedAsset()
    {
        var asset = SelectedAsset!;
        SetPlan(
            new AutomationPlan
            {
                Name = Ui.F("Plan.PlaceAssetName", asset.Name),
                Actions = [new ImportFileAction { Id = "asset", FilePath = asset.FilePath, Name = asset.Name }],
            },
            Ui.F("Plan.PlaceAsset", asset.Name));
        SelectedTabIndex = 0;
    }

    // ---- History and settings ----------------------------------------------------------------

    private void RefreshHistory()
    {
        HistoryRows.Clear();
        foreach (var entry in _services.History.GetRecent(200))
        {
            HistoryRows.Add(new HistoryRow(
                entry,
                entry.TimestampUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", Msg.Culture),
                entry.Plan.Name,
                entry.Result.Summary));
        }
    }

    private void ReloadHistoryPlan()
    {
        var entry = SelectedHistory!.Entry;

        // A fresh id keeps the new run distinguishable from the original in the history.
        SetPlan(entry.Plan with { Id = Guid.NewGuid().ToString("N"), CreatedUtc = DateTimeOffset.UtcNow },
            Ui.F("Plan.Reloaded", SelectedHistory.Time));
        Request = entry.Plan.UserRequest ?? Request;
        SelectedTabIndex = 0;
    }

    private void BrowseOutput()
    {
        var selected = _services.Shell.BrowseForFolder(OutputFolder);
        if (!string.IsNullOrWhiteSpace(selected))
        {
            OutputFolder = selected;
        }
    }

    private void OpenOutputFolder() => TryShell(() =>
    {
        Directory.CreateDirectory(OutputFolder);
        _services.Shell.OpenFolder(OutputFolder);
    });

    private void TryShell(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AddLog(exception.Message);
        }
    }

    // ---- Infrastructure ----------------------------------------------------------------------

    private async Task RunBusyAsync(string status, Func<Task> work)
    {
        IsBusy = true;
        var busyText = Ui.F("Status.Busy", status);
        StatusMessage = busyText;
        try
        {
            await work();
            if (StatusMessage == busyText)
            {
                StatusMessage = Ui.T("Status.Ready");
            }
        }
        catch (Exception exception)
        {
            if (exception is CorelAutomationException)
            {
                IsConnected = false;
                ConnectionText = Ui.T("Connection.NotConnected");
            }

            // The user sees a plain explanation; the technical exception goes to the log file only.
            StatusMessage = Ui.T("Status.Error");
            AddLog(Ui.F("Log.Error", FriendlyMessage(exception)));
            _services.FileLog.Write(status + " failed.", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private AsyncRelayCommand Async(Func<Task> execute, Func<bool>? canExecute = null)
    {
        var command = new AsyncRelayCommand(execute, () => !IsBusy && (canExecute?.Invoke() ?? true));
        _asyncCommands.Add(command);
        return command;
    }

    private RelayCommand Command(Action execute, Func<bool>? canExecute = null)
    {
        var command = new RelayCommand(execute, () => !IsBusy && (canExecute?.Invoke() ?? true));
        _commands.Add(command);
        return command;
    }

    private void RaiseCommandStates()
    {
        foreach (var command in _asyncCommands)
        {
            command.RaiseCanExecuteChanged();
        }

        foreach (var command in _commands)
        {
            command.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Turns an exception into a sentence a non-technical user can act on.</summary>
    private static string FriendlyMessage(Exception exception) => exception switch
    {
        CorelAutomationException { Operation: "SaveCdr" } => Ui.T("Error.CdrNotSaved"),
        CorelAutomationException { Operation: "ExportPdf" } => Ui.T("Error.PdfNotExported"),
        CorelAutomationException { Operation: "Connect" } => Ui.T("Error.CorelNotConnected"),
        CorelAutomationException { InnerException: InvalidOperationException } => Ui.T("Error.CorelNotConnected"),
        CorelAutomationException => Ui.T("Error.CorelBusy"),
        FileNotFoundException notFound => notFound.FileName is null ? notFound.Message : Ui.F("Error.FileNotFound", notFound.FileName),
        UnauthorizedAccessException => Ui.T("Error.NoWriteAccess"),
        IOException => Ui.F("Error.FileAccess", exception.Message),
        ReferencePreviewException => exception.Message,
        AiClientException ai => ai.UserMessage,
        System.Net.Http.HttpRequestException => Ui.T("Error.AiUnreachable"),
        System.Text.Json.JsonException => Ui.T("Error.DataUnreadable"),

        // These are raised by the application itself with messages from the Turkish catalogue.
        RecipeVariableException or NotSupportedException or FormatException or ArgumentException => exception.Message,
        _ => Ui.T("Error.Unexpected"),
    };

    private void AddLog(string message)
    {
        Logs.Add($"{DateTime.Now.ToString("HH:mm:ss", Msg.Culture)}  {message}");
        _services.FileLog.Write(message);
    }

    private static string Mm(double value) => Msg.Number(value);
}
