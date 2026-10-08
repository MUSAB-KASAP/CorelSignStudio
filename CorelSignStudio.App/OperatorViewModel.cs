using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CorelSignStudio.Corel;
using CorelSignStudio.Domain.Assets;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Batch;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Planning;
using CorelSignStudio.Domain.Recipes;
using CorelSignStudio.Domain.References;
using CorelSignStudio.Storage;

namespace CorelSignStudio.App;

public sealed record ShapeRow(string Id, string Type, string Name, string Text, string X, string Y, string Width, string Height, string Layer, string Group);

public sealed record BatchPreviewRow(int Row, string OutputName, string Status);

public sealed record HistoryRow(ExecutionHistoryEntry Entry, string Time, string Name, string Result);

public sealed class RecipeVariableRow(RecipeVariable variable) : ObservableObject
{
    private string _value = variable.DefaultValue ?? "";

    public string Name { get; } = variable.Name;
    public string Type { get; } = variable.Type.ToString();
    public string Description { get; } = variable.Description ?? (variable.IsRequired ? "Required" : "");
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}

/// <summary>Everything the operator window needs from the outside world.</summary>
public sealed record OperatorServices(
    CorelAutomationService Corel,
    ICorelDocumentInspector Inspector,
    ICorelActionExecutor Executor,
    ICommandPlanner Planner,
    IRecipeStore Recipes,
    IAssetLibrary Assets,
    IExecutionHistoryStore History,
    IReferenceAnalyzer ReferenceAnalyzer,
    IReferencePlanBuilder ReferencePlanBuilder,
    IDesktopShellService Shell,
    FileLogWriter FileLog,
    string DataFolder,
    string DefaultOutputFolder);

/// <summary>
/// View model of the operator control centre. It never touches COM: it talks to CorelDRAW only through
/// the inspector/executor abstractions and shows plans for review before anything is executed.
/// </summary>
public sealed class OperatorViewModel : ObservableObject
{
    private readonly OperatorServices _services;
    private readonly List<AsyncRelayCommand> _asyncCommands = [];
    private readonly List<RelayCommand> _commands = [];

    private bool _isBusy;
    private bool _isConnected;
    private string _connectionText = "Not connected";
    private string _activeDocumentText = "No document inspected yet. Press Inspect Document.";
    private string _statusMessage = "Ready";
    private string _request = "";
    private string _planMessage = "No plan yet. Type a request and press Prepare Plan.";
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
    private string _batchCsvPath = "";
    private string _batchNamePattern = "";
    private bool _batchCdr = true;
    private bool _batchPdf = true;
    private bool _batchPng;
    private bool _batchSvg;
    private double _batchPercent;
    private string _batchStatus = "Choose a recipe and a CSV file.";
    private CancellationTokenSource? _batchCancellation;
    private Asset? _selectedAsset;
    private string _assetSearch = "";
    private string _assetCategory = "";
    private string _assetTags = "";
    private HistoryRow? _selectedHistory;

    public OperatorViewModel(OperatorServices services)
    {
        _services = services;
        _outputFolder = services.DefaultOutputFolder;

        ConnectCommand = Async(ConnectAsync);
        InspectCommand = Async(InspectAsync);
        PreparePlanCommand = Async(PreparePlanAsync);
        ExecuteCommand = Async(ExecutePlanAsync, () => _plan is not null);
        SaveRecipeCommand = Command(SaveRecipe, () => (_lastSuccessfulPlan ?? _plan) is not null && !string.IsNullOrWhiteSpace(RecipeName));
        ClearPlanCommand = Command(() => SetPlan(null, "Plan cleared."), () => _plan is not null);
        AddReferenceCommand = Command(BrowseReferences);
        RemoveReferenceCommand = Command(() => References.Remove(SelectedReference!), () => SelectedReference is not null);
        InsertShapeIdCommand = Command(InsertSelectedShapeId, () => SelectedShape is not null);

        UseRecipeCommand = Command(UseSelectedRecipe, () => SelectedRecipe is not null);
        DeleteRecipeCommand = Command(DeleteSelectedRecipe, () => SelectedRecipe is not null);

        BrowseBatchCsvCommand = Command(BrowseBatchCsv);
        PreviewBatchCommand = Command(() => PreviewBatch(), () => BatchRecipe is not null);
        RunBatchCommand = Async(RunBatchAsync, () => BatchRecipe is not null);
        CancelBatchCommand = new RelayCommand(() => _batchCancellation?.Cancel(), () => _batchCancellation is not null);

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
        AddLog("Corel AI Operator is ready. CorelDRAW stays your design workspace; this window sends it instructions.");
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
    public ObservableCollection<Asset> Assets { get; } = [];
    public ObservableCollection<HistoryRow> HistoryRows { get; } = [];
    public IReadOnlyList<string> ExampleCommands => DeterministicCommandPlanner.Examples;

    // ---- Commands ----------------------------------------------------------------------------

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand InspectCommand { get; }
    public AsyncRelayCommand PreparePlanCommand { get; }
    public AsyncRelayCommand ExecuteCommand { get; }
    public RelayCommand SaveRecipeCommand { get; }
    public RelayCommand ClearPlanCommand { get; }
    public RelayCommand AddReferenceCommand { get; }
    public RelayCommand RemoveReferenceCommand { get; }
    public RelayCommand InsertShapeIdCommand { get; }
    public RelayCommand UseRecipeCommand { get; }
    public RelayCommand DeleteRecipeCommand { get; }
    public RelayCommand BrowseBatchCsvCommand { get; }
    public RelayCommand PreviewBatchCommand { get; }
    public AsyncRelayCommand RunBatchCommand { get; }
    public RelayCommand CancelBatchCommand { get; }
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
    public int SelectedTabIndex { get => _selectedTabIndex; set => SetProperty(ref _selectedTabIndex, value); }
    public bool RollbackOnFailure { get => _rollbackOnFailure; set => SetProperty(ref _rollbackOnFailure, value); }
    public string OutputFolder { get => _outputFolder; set => SetProperty(ref _outputFolder, value); }
    public string DataFolder => _services.DataFolder;
    public string PlannerName => _services.Planner.Name;
    public string LogPath => _services.FileLog.LogPath;

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
        ? "The CSV's first line must contain the recipe's variable names."
        : BatchRecipe.Variables.Count == 0
            ? "This recipe has no variables; every row produces the same result."
            : "CSV columns expected: " + string.Join(", ", BatchRecipe.Variables.Select(variable => variable.Name));

    public string BatchCsvPath { get => _batchCsvPath; set => SetProperty(ref _batchCsvPath, value); }
    public string BatchNamePattern { get => _batchNamePattern; set => SetProperty(ref _batchNamePattern, value); }
    public bool BatchCdr { get => _batchCdr; set => SetProperty(ref _batchCdr, value); }
    public bool BatchPdf { get => _batchPdf; set => SetProperty(ref _batchPdf, value); }
    public bool BatchPng { get => _batchPng; set => SetProperty(ref _batchPng, value); }
    public bool BatchSvg { get => _batchSvg; set => SetProperty(ref _batchSvg, value); }
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
                    References.Add(reference);
                    AddLog($"Reference added: {reference.FileName} ({reference.FileType}{(reference.IsVector ? ", vector — can be reused directly" : ", bitmap")}).");
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
        await RunBusyAsync("Connecting to CorelDRAW", EnsureConnectedAsync);
    }

    private async Task EnsureConnectedAsync()
    {
        // Always (re)connect: it is cheap, and it recovers when the user closed and reopened CorelDRAW.
        var connection = await _services.Corel.ConnectAsync(visible: true);
        IsConnected = true;
        ConnectionText = $"Connected — CorelDRAW {connection.Version}";
    }

    private Task InspectAsync() => RunBusyAsync("Inspecting the active document", async () =>
    {
        await EnsureConnectedAsync();
        await RefreshSnapshotAsync();
        AddLog(_snapshot is null
            ? "CorelDRAW has no open document."
            : $"Inspected '{_snapshot.Title}': {_snapshot.ShapeCount} object(s).");
    });

    private async Task RefreshSnapshotAsync()
    {
        _snapshot = await _services.Inspector.InspectActiveDocumentAsync();
        Shapes.Clear();
        if (_snapshot is null)
        {
            ActiveDocumentText = "No document is open in CorelDRAW.";
            return;
        }

        var page = _snapshot.ActivePage;
        ActiveDocumentText =
            $"Active document: {_snapshot.Title}   |   {Mm(page?.WidthMm ?? 0)} x {Mm(page?.HeightMm ?? 0)} mm   |   {_snapshot.ShapeCount} object(s)" +
            (_snapshot.SelectedShapeIds.Count > 0 ? $"   |   {_snapshot.SelectedShapeIds.Count} selected" : "");
        foreach (var shape in _snapshot.AllShapes())
        {
            Shapes.Add(new ShapeRow(
                shape.Id,
                shape.Type.ToString(),
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

    private Task PreparePlanAsync() => RunBusyAsync("Preparing a plan", async () =>
    {
        if (string.IsNullOrWhiteSpace(Request))
        {
            if (References.Count == 0)
            {
                SetPlan(null, "Type what CorelDRAW should do, or attach a reference file.");
                return;
            }

            // No instruction, only files: the non-AI baseline is to bring the references into CorelDRAW.
            var actions = new List<CorelAction>();
            foreach (var reference in References)
            {
                var analysis = await _services.ReferenceAnalyzer.AnalyzeAsync(reference);
                foreach (var note in analysis.Notes)
                {
                    AddLog($"{reference.FileName}: {note}");
                }

                var import = _services.ReferencePlanBuilder.BuildPlan(reference, analysis).Actions[0];
                actions.Add(import is ImportFileAction file ? file with { Id = $"import{actions.Count + 1}" } : import);
            }

            SetPlan(
                new AutomationPlan
                {
                    Name = "Import reference files",
                    Target = actions[0].OpensDocument ? DocumentTarget.NewDocument : DocumentTarget.ActiveDocument,
                    Actions = actions,
                    ReferenceFiles = References.ToArray(),
                },
                "No instruction was typed, so the plan imports the attached reference files.");
            return;
        }

        var result = await _services.Planner.PlanAsync(new PlanningRequest
        {
            UserRequest = Request,
            Document = _snapshot,
            References = References.ToArray(),
        });
        var message = result.Message ?? "";
        if (result.UnrecognizedCommands.Count > 0)
        {
            message += " Not understood: " + string.Join(" | ", result.UnrecognizedCommands) +
                       ". (The built-in planner only knows a few test commands; the AI planner will replace it.)";
        }

        SetPlan(result.Plan, message);
    });

    private void SetPlan(AutomationPlan? plan, string message)
    {
        _plan = plan;
        ProposedOperations.Clear();
        if (plan is not null)
        {
            for (var index = 0; index < plan.Actions.Count; index++)
            {
                var action = plan.Actions[index];
                ProposedOperations.Add($"{index + 1}. {ActionDescriber.Describe(action)}{(action.IsDestructive ? "   ⚠ asks for confirmation" : "")}");
            }

            var validation = plan.Validate();
            if (!validation.IsValid)
            {
                message += " Problems: " + string.Join("; ", validation.Errors);
            }
        }

        PlanMessage = message;
        StatusMessage = plan is null ? "No plan" : $"Plan ready: {plan.Actions.Count} operation(s)";
        RaiseCommandStates();
    }

    private Task ExecutePlanAsync() => RunBusyAsync("Executing in CorelDRAW", async () =>
    {
        var plan = _plan!;
        if (plan.HasDestructiveActions && !_services.Shell.Confirm(
                "This plan deletes objects or closes a document without saving:\n\n" +
                string.Join("\n", plan.Actions.Where(action => action.IsDestructive).Select(ActionDescriber.Describe)) +
                "\n\nContinue?",
                "Confirm destructive operations"))
        {
            AddLog("Execution cancelled by the user.");
            return;
        }

        await EnsureConnectedAsync();
        AddLog($"Executing '{plan.Name}' ({plan.Actions.Count} operation(s))…");
        var progress = new Progress<ActionProgress>(report =>
        {
            if (report.Result is { } step)
            {
                AddLog($"  {(step.Success ? "✓" : "✗")} {report.Index}/{report.Total} {ActionDescriber.Describe(report.Action)}" +
                       (step.Error is null ? "" : $" — {step.Error}"));
            }
        });

        var result = await _services.Executor.ExecuteAsync(plan, new ExecutionOptions { RollbackOnFailure = RollbackOnFailure }, progress);
        _services.History.Append(new ExecutionHistoryEntry { Plan = plan, Result = result });
        RefreshHistory();

        if (result.Success)
        {
            _lastSuccessfulPlan = plan;
            if (string.IsNullOrWhiteSpace(RecipeName))
            {
                RecipeName = plan.Name;
            }

            AddLog($"Done in {result.Duration.TotalSeconds:0.0} s. {result.Summary}");
            foreach (var file in result.ProducedFiles)
            {
                AddLog($"  File: {file}");
            }

            StatusMessage = "Completed";
        }
        else
        {
            AddLog($"Failed: {result.Summary}");
            if (result.RollbackNote is not null)
            {
                AddLog($"  {result.RollbackNote}");
            }

            _services.FileLog.Write($"Plan '{plan.Name}' failed: {result.ErrorDetails}");
            StatusMessage = "Failed — see Activity";
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
                    throw new ArgumentException($"Variable line '{line}' must look like NAME = value.");
                }

                bindings.Add(new RecipeVariableBinding(line[..separator].Trim(), line[(separator + 1)..].Trim()));
            }

            var recipe = BuildRecipe(plan, RecipeName.Trim(), bindings);
            var existing = _services.Recipes.FindByName(recipe.Name);
            if (existing is not null)
            {
                if (!_services.Shell.Confirm($"A recipe named '{recipe.Name}' already exists. Replace it?", "Replace recipe"))
                {
                    return;
                }

                recipe = recipe with { Id = existing.Id };
            }

            _services.Recipes.Save(recipe);
            RefreshRecipes();
            AddLog($"Saved recipe '{recipe.Name}' with {recipe.Variables.Count} variable(s)" +
                   (recipe.Variables.Count > 0 ? ": " + string.Join(", ", recipe.Variables.Select(variable => variable.Name)) : "") + ".");
        }
        catch (ArgumentException exception)
        {
            AddLog("Recipe not saved: " + exception.Message);
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
            "Choose reference files",
            "Design and image files|*.cdr;*.pdf;*.svg;*.jpg;*.jpeg;*.png|All files|*.*",
            multiple: true));

    private void InsertSelectedShapeId()
    {
        Request = string.IsNullOrWhiteSpace(Request) ? SelectedShape!.Id : $"{Request.TrimEnd()} {SelectedShape!.Id}";
        SelectedTabIndex = 0;
    }

    // ---- Recipes -----------------------------------------------------------------------------

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
            SetPlan(plan, $"Plan prepared from recipe '{SelectedRecipe.Name}'. Review it, then execute.");
            SelectedTabIndex = 0;
        }
        catch (RecipeVariableException exception)
        {
            AddLog(exception.Message);
            StatusMessage = "Recipe needs more values — see Activity";
        }
    }

    private void DeleteSelectedRecipe()
    {
        var recipe = SelectedRecipe!;
        if (_services.Shell.Confirm($"Delete the recipe '{recipe.Name}'?", "Delete recipe"))
        {
            _services.Recipes.Delete(recipe.Id);
            RefreshRecipes();
            AddLog($"Deleted recipe '{recipe.Name}'.");
        }
    }

    // ---- Batch -------------------------------------------------------------------------------

    private void BrowseBatchCsv()
    {
        var file = _services.Shell.BrowseForFiles("Choose a CSV file", "CSV files|*.csv;*.txt|All files|*.*", multiple: false).FirstOrDefault();
        if (file is not null)
        {
            BatchCsvPath = file;
            PreviewBatch();
        }
    }

    private (BatchJob Job, IReadOnlyList<BatchItem> Items)? PreviewBatch()
    {
        BatchRows.Clear();
        BatchPercent = 0;
        try
        {
            if (BatchRecipe is null)
            {
                BatchStatus = "Choose a recipe first.";
                return null;
            }

            if (!File.Exists(BatchCsvPath))
            {
                BatchStatus = "Choose a CSV file.";
                return null;
            }

            var formats = new List<OutputFormat>();
            if (BatchCdr) formats.Add(OutputFormat.Cdr);
            if (BatchPdf) formats.Add(OutputFormat.Pdf);
            if (BatchPng) formats.Add(OutputFormat.Png);
            if (BatchSvg) formats.Add(OutputFormat.Svg);
            if (formats.Count == 0)
            {
                BatchStatus = "Choose at least one output format.";
                return null;
            }

            var job = new BatchJob
            {
                Name = $"{BatchRecipe.Name} batch",
                RecipeId = BatchRecipe.Id,
                Rows = CsvBatchReader.ReadFile(BatchCsvPath),
                OutputFolder = OutputFolder,
                OutputNamePattern = string.IsNullOrWhiteSpace(BatchNamePattern) ? null : BatchNamePattern.Trim(),
                Formats = formats,
            };
            var items = BatchExpander.Expand(job, BatchRecipe);
            foreach (var item in items)
            {
                BatchRows.Add(new BatchPreviewRow(item.RowNumber, item.OutputBaseName ?? "", item.IsValid ? "Ready" : "Problem: " + item.Error));
            }

            BatchStatus = $"{items.Count} row(s): {items.Count(item => item.IsValid)} ready, {items.Count(item => !item.IsValid)} with problems. Output: {OutputFolder}";
            return (job, items);
        }
        catch (Exception exception) when (exception is IOException or FormatException or ArgumentException)
        {
            BatchStatus = "Could not read the batch data: " + exception.Message;
            return null;
        }
    }

    private Task RunBatchAsync() => RunBusyAsync("Running batch", async () =>
    {
        if (PreviewBatch() is not { } batch)
        {
            return;
        }

        await EnsureConnectedAsync();
        using var cancellation = new CancellationTokenSource();
        _batchCancellation = cancellation;
        CancelBatchCommand.RaiseCanExecuteChanged();
        try
        {
            var recipe = BatchRecipe!;
            AddLog($"Batch '{batch.Job.Name}' started: {batch.Items.Count} row(s).");
            var progress = new Progress<BatchProgress>(report =>
            {
                BatchPercent = report.Percent;
                BatchStatus = $"{report.Completed} of {report.Total} done — {report.Succeeded} ok, {report.Failed} failed" +
                              (report.CurrentOutputName is null ? "" : $" — working on {report.CurrentOutputName}");
            });

            var result = await new BatchRunner(_services.Executor).RunAsync(batch.Job, recipe, progress, cancellationToken: cancellation.Token);

            BatchRows.Clear();
            foreach (var item in result.Items)
            {
                BatchRows.Add(new BatchPreviewRow(item.RowNumber, item.OutputBaseName ?? "", item.Success ? "Done" : "Failed: " + item.Error));
            }

            BatchPercent = 100;
            BatchStatus = $"Finished in {result.Duration.TotalSeconds:0.0} s: {result.Succeeded} ok, {result.Failed} failed" +
                          (result.Cancelled ? " (cancelled)" : "") + $". Output: {OutputFolder}";
            AddLog($"Batch finished: {result.Succeeded} ok, {result.Failed} failed, {result.ProducedFiles.Count()} file(s).");
        }
        finally
        {
            _batchCancellation = null;
            CancelBatchCommand.RaiseCanExecuteChanged();
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
        foreach (var file in _services.Shell.BrowseForFiles("Add files to the asset library", "All files|*.*", multiple: true))
        {
            try
            {
                var asset = _services.Assets.Add(file, category: AssetCategory, tags: tags);
                AddLog($"Asset added: {asset.Name} ({asset.Category}).");
            }
            catch (IOException exception)
            {
                AddLog($"Asset not added: {exception.Message}");
            }
        }

        RefreshAssets();
    }

    private void RemoveSelectedAsset()
    {
        var asset = SelectedAsset!;
        if (_services.Shell.Confirm($"Remove '{asset.Name}' from the asset library?", "Remove asset"))
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
                Name = $"Place {asset.Name}",
                Actions = [new ImportFileAction { Id = "asset", FilePath = asset.FilePath, Name = asset.Name }],
            },
            $"Plan prepared to place the asset '{asset.Name}' in the active document.");
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
                entry.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                entry.Plan.Name,
                entry.Result.Summary));
        }
    }

    private void ReloadHistoryPlan()
    {
        var entry = SelectedHistory!.Entry;

        // A fresh id keeps the new run distinguishable from the original in the history.
        SetPlan(entry.Plan with { Id = Guid.NewGuid().ToString("N"), CreatedUtc = DateTimeOffset.UtcNow },
            $"Plan reloaded from {SelectedHistory.Time}. Object ids may have changed since then — inspect the document if a step fails.");
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
        StatusMessage = status + "…";
        try
        {
            await work();
            if (StatusMessage == status + "…")
            {
                StatusMessage = "Ready";
            }
        }
        catch (Exception exception)
        {
            if (exception is CorelAutomationException)
            {
                IsConnected = false;
                ConnectionText = "Not connected";
            }

            StatusMessage = "Something went wrong — see Activity";
            AddLog("Error: " + exception.Message);
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

    private void AddLog(string message)
    {
        Logs.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        _services.FileLog.Write(message);
    }

    private static string Mm(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
