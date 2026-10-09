using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CorelSignStudio.Domain.Ai.Vision;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.Production;
using CorelSignStudio.Domain.References;

namespace CorelSignStudio.App;

/// <summary>
/// What a reconstruction left behind for the later steps: the reference it came from, what was found on
/// it, the plan that rebuilt it and the objects that plan was meant to create. Comparison, automatic
/// improvement and the production check all work from this.
/// </summary>
public sealed record ReconstructionContext(
    ReferenceInput Reference,
    ReferenceAnalysis Analysis,
    AutomationPlan Plan,
    IReadOnlyList<ExpectedObject> Expected,
    PhysicalSize Size,
    int PageNumber,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// True when the document still contains at least one object this reconstruction created. A document
    /// with none of them is some other document, and comparing it with this reference would be meaningless.
    /// </summary>
    public bool Matches(DocumentSnapshot document) =>
        Expected.Any(expected => document.FindShapesByName(expected.ShapeName).Any());
}

// Comparison with the reference, bounded automatic improvement, the production check, cancellation,
// PDF page selection and the About texts.
public sealed partial class OperatorViewModel
{
    private readonly Dictionary<string, int> _pdfPageCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _pdfSelectedPages = new(StringComparer.OrdinalIgnoreCase);
    private ReconstructionContext? _pendingContext;
    private ReconstructionContext? _context;
    private CancellationTokenSource? _operationCancellation;
    private string _comparisonTitle = Ui.T("Compare.Title.None");
    private string _comparisonText = Ui.T("Compare.Hint");
    private string _preflightTitle = Ui.T("Preflight.Title.None");
    private string _preflightText = Ui.T("Preflight.Hint");
    private bool? _isProductionReady;
    private int _reviewTabIndex;
    private int _analysisPage;
    private ImageSource? _pdfPagePreview;

    public AsyncRelayCommand CompareCommand { get; }
    public AsyncRelayCommand ImproveCommand { get; }
    public AsyncRelayCommand PreflightCommand { get; }
    public AsyncRelayCommand PreviewPdfPageCommand { get; }

    /// <summary>Cancels whichever long operation is running: planning, analysis, comparison, improvement or a batch.</summary>
    public RelayCommand CancelCommand { get; }

    // ---- State ---------------------------------------------------------------------------------

    /// <summary>The reconstruction the open document was built from, when there is one.</summary>
    public ReconstructionContext? ReconstructionContext => _context;

    public bool HasComparisonContext => _context is { Expected.Count: > 0 };

    /// <summary>0 = activity, 1 = comparison, 2 = production check.</summary>
    public int ReviewTabIndex { get => _reviewTabIndex; set => SetProperty(ref _reviewTabIndex, value); }

    public string ComparisonTitle { get => _comparisonTitle; private set => SetProperty(ref _comparisonTitle, value); }
    public string ComparisonText { get => _comparisonText; private set => SetProperty(ref _comparisonText, value); }
    public string PreflightTitle { get => _preflightTitle; private set => SetProperty(ref _preflightTitle, value); }
    public string PreflightText { get => _preflightText; private set => SetProperty(ref _preflightText, value); }

    /// <summary>Null until a production check has run; false while any blocking error remains.</summary>
    public bool? IsProductionReady { get => _isProductionReady; private set => SetProperty(ref _isProductionReady, value); }

    public VisualComparisonResult? LastComparison { get; private set; }
    public ImprovementResult? LastImprovement { get; private set; }
    public PreflightResult? LastPreflight { get; private set; }

    public string AboutName => Ui.T("App.Title");
    public string AboutVersion => Ui.F("About.Version", AppInfo.Version);

    // ---- PDF pages -----------------------------------------------------------------------------

    private int PdfPageCountOf(ReferenceInput? reference) =>
        reference is not null && _pdfPageCounts.TryGetValue(reference.FilePath, out var count) ? count : 1;

    /// <summary>True when the reference that would be analysed is a PDF with more than one page.</summary>
    public bool HasPdfPages => PdfPageCountOf(ReferenceToAnalyze) > 1;

    public string PdfPagesText => Ui.F("Reference.PdfPages", PdfPageCountOf(ReferenceToAnalyze));

    public IReadOnlyList<int> PdfPageNumbers => Enumerable.Range(1, PdfPageCountOf(ReferenceToAnalyze)).ToArray();

    /// <summary>The page to analyse. Empty until the user picks one: a page is never chosen silently.</summary>
    public int? SelectedPdfPage
    {
        get => ReferenceToAnalyze is { } reference && _pdfSelectedPages.TryGetValue(reference.FilePath, out var page) ? page : null;
        set
        {
            if (ReferenceToAnalyze is not { } reference || value == SelectedPdfPage)
            {
                return;
            }

            if (value is { } page && page >= 1 && page <= PdfPageCountOf(reference))
            {
                _pdfSelectedPages[reference.FilePath] = page;
            }
            else
            {
                _pdfSelectedPages.Remove(reference.FilePath);
            }

            PdfPagePreview = null;
            OnPropertyChanged();
            RaiseCommandStates();
        }
    }

    public ImageSource? PdfPagePreview
    {
        get => _pdfPagePreview;
        private set
        {
            if (SetProperty(ref _pdfPagePreview, value))
            {
                OnPropertyChanged(nameof(HasPdfPagePreview));
            }
        }
    }

    public bool HasPdfPagePreview => _pdfPagePreview is not null;

    private void RegisterPdfPages(ReferenceInput reference)
    {
        if (!string.Equals(Path.GetExtension(reference.FilePath), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var count = _services.PdfPageCount(reference.FilePath);
        _pdfPageCounts[reference.FilePath] = count;
        if (count > 1)
        {
            AddLog(Ui.F("Log.PdfPages", reference.FileName, count));
        }
    }

    private void RaisePdfPageState()
    {
        PdfPagePreview = null;
        OnPropertyChanged(nameof(HasPdfPages));
        OnPropertyChanged(nameof(PdfPagesText));
        OnPropertyChanged(nameof(PdfPageNumbers));
        OnPropertyChanged(nameof(SelectedPdfPage));
    }

    /// <summary>A page written in the request ("3. sayfayı yap") wins and is shown in the selector.</summary>
    private int? ResolvePage(ReferenceInput reference, string request)
    {
        var count = PdfPageCountOf(reference);
        if (count <= 1)
        {
            return null;
        }

        if (AiReferenceAnalyzer.FindPageNumber(request) is { } asked)
        {
            if (asked <= count && ReferenceEquals(reference, ReferenceToAnalyze))
            {
                SelectedPdfPage = asked;
            }

            return asked;
        }

        return _pdfSelectedPages.TryGetValue(reference.FilePath, out var selected) ? selected : null;
    }

    private Task PreviewPdfPageAsync() => RunCancellableAsync(Ui.T("Status.PreviewingPage"), async token =>
    {
        var reference = ReferenceToAnalyze!;
        var preview = await _services.ReferencePreviews!.RenderAsync(
            reference, new ReferencePreviewOptions { PageNumber = SelectedPdfPage!.Value, MaxLongEdgePixels = 700 }, token);
        PdfPagePreview = ToImage(preview.Bytes);
    });

    private static ImageSource? ToImage(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        var image = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    // ---- Reconstruction context ------------------------------------------------------------------

    private void RememberPendingReconstruction(ReconstructionResult result, IReadOnlyList<string> warnings)
    {
        _pendingContext = result is { IsReady: true, Size: { } size }
            ? new ReconstructionContext(_analysisReference!, _analysis!, result.Plan!, ExpectedObjects.FromPlan(result.Plan!), size, Math.Max(1, _analysis!.PageNumber), warnings)
            : null;
    }

    /// <summary>Called after a plan ran successfully: keeps, replaces or drops the reconstruction context.</summary>
    private void UpdateContextAfterExecution(AutomationPlan plan)
    {
        if (ReferenceEquals(_pendingContext?.Plan, plan))
        {
            SetContext(_pendingContext);
            _pendingContext = null;
        }
        else if (plan.Target == DocumentTarget.NewDocument || plan.Actions.Any(action => action.OpensDocument))
        {
            // Another document is in front now; the old reference no longer describes it.
            SetContext(null);
        }
    }

    private void SetContext(ReconstructionContext? context)
    {
        _context = context;
        LastComparison = null;
        LastImprovement = null;
        ComparisonTitle = Ui.T("Compare.Title.None");
        ComparisonText = Ui.T(context is null ? "Compare.Hint" : "Compare.Ready");
        OnPropertyChanged(nameof(HasComparisonContext));
        OnPropertyChanged(nameof(ReconstructionContext));
        RaiseCommandStates();
    }

    /// <summary>Guards against comparing an unrelated document with a reference it was not built from.</summary>
    private bool ContextFitsDocument(ReconstructionContext context)
    {
        if (_snapshot is null)
        {
            ComparisonTitle = Ui.T("Compare.Title.None");
            ComparisonText = Ui.T("Compare.NoDocument");
        }
        else if (!context.Matches(_snapshot))
        {
            ComparisonTitle = Ui.T("Compare.Title.None");
            ComparisonText = Ui.F("Compare.Stale", _snapshot.Title, context.Reference.FileName);
        }
        else
        {
            return true;
        }

        AddLog(ComparisonText);
        ReviewTabIndex = 1;
        return false;
    }

    // ---- Comparison ------------------------------------------------------------------------------

    private Task CompareWithReferenceAsync() => RunCancellableAsync(Ui.T("Status.Comparing"), async token =>
    {
        var context = _context!;
        await EnsureConnectedAsync();
        await RefreshSnapshotAsync();
        if (!ContextFitsDocument(context))
        {
            return;
        }

        var (reference, output) = await RenderComparisonImagesAsync(context, token);
        var result = await _services.Comparison.CompareAsync(
            new VisualComparisonRequest { Expected = context.Expected, Document = _snapshot!, Size = context.Size, ReferenceImage = reference, OutputImage = output },
            token);
        token.ThrowIfCancellationRequested();

        LastComparison = result;
        LastImprovement = null;
        ComparisonTitle = Ui.F("Compare.Similarity", Percent(result.Similarity));
        ComparisonText = DescribeComparison(result);
        ReviewTabIndex = 1;
        AddLog(Ui.F("Log.Compared", Percent(result.Similarity), result.Differences.Count(difference => difference.Severity != DifferenceSeverity.Info)));
        StatusMessage = Ui.T("Status.Compared");
    });

    /// <summary>
    /// The two pictures a vision model compares. They are only produced when an AI provider is configured;
    /// without one the comparison is purely measured and nothing is rendered or sent anywhere.
    /// </summary>
    private async Task<(ReferencePreview? Reference, ReferencePreview? Output)> RenderComparisonImagesAsync(ReconstructionContext context, CancellationToken token)
    {
        if (!_services.Ai.IsConfigured || _services.ReferencePreviews is null || _services.PagePreview is null)
        {
            return (null, null);
        }

        try
        {
            var reference = await _services.ReferencePreviews.RenderAsync(context.Reference, new ReferencePreviewOptions { PageNumber = context.PageNumber }, token);
            var output = await _services.PagePreview.RenderActivePageAsync(cancellationToken: token);
            return (reference, output);
        }
        catch (ReferencePreviewException exception)
        {
            // The measured comparison still stands without the pictures.
            _services.FileLog.Write("Comparison preview could not be rendered.", exception);
            return (null, null);
        }
    }

    private static string Percent(double similarity) => Math.Round(Math.Clamp(similarity, 0, 1) * 100).ToString("0", Msg.Culture);

    private static string DescribeComparison(VisualComparisonResult result)
    {
        var lines = new List<string>();
        if (result.Matches.Count > 0)
        {
            lines.Add(Ui.T("Compare.Section.Matches"));
            lines.AddRange(result.Matches.Select(match => "✓ " + match));
            lines.Add("");
        }

        var measured = result.Differences.Where(difference => difference.Kind != DifferenceKind.Visual).ToList();
        var seen = result.Differences.Where(difference => difference.Kind == DifferenceKind.Visual).ToList();
        if (measured.Count > 0)
        {
            lines.Add(Ui.T("Compare.Section.Differences"));
            lines.AddRange(measured.Select(difference => (difference.Severity == DifferenceSeverity.Info ? "• " : "⚠ ") + difference.Description));
            lines.Add("");
        }

        if (seen.Count > 0)
        {
            lines.Add(Ui.T("Compare.Section.Visual"));
            lines.AddRange(seen.Select(difference => "⚠ " + difference.Description));
            lines.Add("");
        }

        if (measured.Count == 0 && seen.Count == 0)
        {
            lines.Add(Ui.T("Compare.NoDifferences"));
            lines.Add("");
        }

        if (result.Notes.Count > 0)
        {
            lines.Add(Ui.T("Compare.Section.Notes"));
            lines.AddRange(result.Notes.Select(note => "• " + note));
            lines.Add("");
        }

        lines.Add(Ui.T(result.AiUsed ? "Compare.AiUsed" : "Compare.AiNotUsed"));
        return string.Join(Environment.NewLine, lines);
    }

    // ---- Automatic improvement ---------------------------------------------------------------------

    private Task ImproveAutomaticallyAsync() => RunCancellableAsync(Ui.T("Status.Improving"), async token =>
    {
        var context = _context!;
        await EnsureConnectedAsync();
        await RefreshSnapshotAsync();
        if (!ContextFitsDocument(context))
        {
            return;
        }

        var options = new ImprovementOptions();
        var (reference, _) = await RenderComparisonImagesAsync(context, token);
        var loop = new VisualImprovementLoop(_services.Inspector, _services.Executor, _services.Comparison, _services.Corrections, _services.PagePreview);
        var progress = new Progress<ImprovementPass>(pass =>
        {
            StatusMessage = Ui.F("Status.Busy", DescribePass(pass, options));
            AddLog(DescribePass(pass, options));
        });

        AddLog(Ui.F("Log.ImproveStarted", options.MaxPasses));
        var result = await loop.RunAsync(
            new ImprovementRequest { Expected = context.Expected, Size = context.Size, ReferenceImage = reference }, options, progress, token);
        await RefreshSnapshotAsync();

        LastImprovement = result;
        LastComparison = result.FinalComparison;
        var stop = Msg.Get("Improve.Stop." + result.StopReason);
        var lines = new List<string>
        {
            Ui.T(result.StopReason == ImprovementStopReason.Cancelled ? "Improve.Cancelled" : "Improve.Finished"),
            "",
            Ui.F("Improve.Initial", Percent(result.InitialSimilarity)),
            Ui.F("Improve.Final", Percent(result.FinalSimilarity)),
            Ui.F("Improve.Passes", result.Passes.Count),
            stop,
        };
        if (result.Error is not null)
        {
            lines.Add(result.Error);
        }

        if (result.Passes.Count > 0)
        {
            lines.Add("");
            foreach (var pass in result.Passes)
            {
                lines.Add(DescribePass(pass, options));
                lines.AddRange(pass.Corrections.Select(correction => "   ✓ " + correction));
                lines.AddRange(pass.Warnings.Select(warning => "   ⚠ " + warning));
            }
        }

        if (result.FinalComparison is { } final)
        {
            lines.Add("");
            lines.Add(DescribeComparison(final));
        }

        ComparisonTitle = Ui.F("Compare.Similarity", Percent(result.FinalSimilarity));
        ComparisonText = string.Join(Environment.NewLine, lines);
        ReviewTabIndex = 1;
        AddLog(Ui.F("Log.Improved", Percent(result.InitialSimilarity), Percent(result.FinalSimilarity), result.Passes.Count, stop));
        StatusMessage = result.StopReason == ImprovementStopReason.Cancelled ? Ui.T("Status.Cancelled") : Ui.T("Status.Improved");
    });

    private static string DescribePass(ImprovementPass pass, ImprovementOptions options) =>
        Ui.F("Improve.Pass", pass.Number, options.MaxPasses, Percent(pass.SimilarityBefore), Percent(pass.SimilarityAfter));

    // ---- Production check ----------------------------------------------------------------------------

    private Task RunPreflightAsync() => RunBusyAsync(Ui.T("Status.Preflight"), async () =>
    {
        await EnsureConnectedAsync();
        await RefreshSnapshotAsync();

        // The reference only says something about the document that was built from it.
        var context = _context is not null && _snapshot is not null && _context.Matches(_snapshot) ? _context : null;

        // Outputs are checked for a plan that is still waiting; the files of a plan that already ran exist by design.
        var waiting = _plan is not null && !ReferenceEquals(_plan, _lastSuccessfulPlan) ? _plan : null;
        var inputs = new List<string>();
        var outputs = new List<string>();
        foreach (var plan in new[] { waiting, context?.Plan, _lastSuccessfulPlan }.Where(plan => plan is not null).Distinct())
        {
            var files = DesignPreflightService.FilesOf(plan!);
            inputs.AddRange(files.Inputs);
            if (ReferenceEquals(plan, waiting))
            {
                outputs.AddRange(files.Outputs);
            }
        }

        var result = _services.Preflight.Check(new PreflightRequest
        {
            Document = _snapshot,
            ExpectedSize = context?.Size,
            Analysis = context?.Analysis,
            ReconstructionWarnings = context?.Warnings ?? [],
            ReferencedFiles = inputs,
            OutputPaths = outputs,
            InstalledFonts = _services.InstalledFonts.Count > 0 ? _services.InstalledFonts : null,
        });

        LastPreflight = result;
        IsProductionReady = result.CanProduce;
        PreflightTitle = Ui.T(!result.CanProduce ? "Preflight.Verdict.Blocked" : result.IsClean ? "Preflight.Verdict.Ready" : "Preflight.Verdict.Warnings");
        PreflightText = DescribePreflight(result);
        ReviewTabIndex = 2;
        AddLog(Ui.F("Log.Preflight", result.Errors.Count, result.Warnings.Count));
        StatusMessage = PreflightTitle;
    });

    private static string DescribePreflight(PreflightResult result)
    {
        var lines = new List<string>();
        void Section(string titleKey, string mark, IReadOnlyList<string> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            if (lines.Count > 0)
            {
                lines.Add("");
            }

            lines.Add(Ui.T(titleKey));
            lines.AddRange(items.Select(item => mark + " " + item));
        }

        Section("Preflight.Section.Errors", "❌", result.Errors);
        Section("Preflight.Section.Warnings", "⚠", result.Warnings);
        Section("Preflight.Section.Info", "✓", result.Info);
        return string.Join(Environment.NewLine, lines);
    }

    // ---- Cancellation --------------------------------------------------------------------------------

    /// <summary>
    /// Runs a long operation that the user can cancel. Cancelling ends it with a plain status message and
    /// leaves every command usable again; edits already sent to CorelDRAW are rolled back by the executor.
    /// </summary>
    private Task RunCancellableAsync(string status, Func<CancellationToken, Task> work) => RunBusyAsync(status, async () =>
    {
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        CancelCommand.RaiseCanExecuteChanged();
        try
        {
            await work(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Ui.T("Status.Cancelled");
            AddLog(Ui.T("Status.Cancelled"));
        }
        finally
        {
            _operationCancellation = null;
            CancelCommand.RaiseCanExecuteChanged();
        }
    });
}
