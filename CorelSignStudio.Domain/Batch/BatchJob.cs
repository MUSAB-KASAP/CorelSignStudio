using System.Diagnostics;
using System.Globalization;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Recipes;
using CorelSignStudio.Domain.Localization;

namespace CorelSignStudio.Domain.Batch;

/// <summary>One record of batch data: variable name → value (for example one CSV/Excel row).</summary>
public sealed record BatchRow(int RowNumber, IReadOnlyDictionary<string, string> Values);

/// <summary>Runs one recipe once per row.</summary>
public sealed record BatchJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public required string RecipeId { get; init; }
    public IReadOnlyList<BatchRow> Rows { get; init; } = [];
    public required string OutputFolder { get; init; }

    /// <summary>
    /// File name (no extension) for each row; may use any row variable plus <c>{{ROW}}</c> (1-based,
    /// zero-padded). Defaults to the row's <c>OUTPUT_NAME</c> value, or "recipe name + row number".
    /// </summary>
    public string? OutputNamePattern { get; init; }

    /// <summary>Files to write for every row. The matching save/export actions are appended to each plan.</summary>
    public IReadOnlyList<OutputFormat> Formats { get; init; } = [OutputFormat.Cdr, OutputFormat.Pdf];

    /// <summary>Close each produced document after its files are written (recommended for large batches).</summary>
    public bool CloseDocumentAfterEachRow { get; init; } = true;

    /// <summary>Keep going when a row fails.</summary>
    public bool ContinueOnError { get; init; } = true;

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A row expanded into an executable plan, or the reason it could not be expanded.</summary>
public sealed record BatchItem
{
    public required int RowNumber { get; init; }
    public AutomationPlan? Plan { get; init; }
    public string? OutputBaseName { get; init; }
    public IReadOnlyList<string> OutputFiles { get; init; } = [];
    public string? Error { get; init; }

    public bool IsValid => Plan is not null && Error is null;
}

public static class BatchVariables
{
    public const string Row = "ROW";
    public const string RowCount = "ROW_COUNT";
    public const string OutputName = "OUTPUT_NAME";
    public const string OutputFolder = "OUTPUT_FOLDER";

    /// <summary>Full output path without extension.</summary>
    public const string OutputBase = "OUTPUT_BASE";
}

public static class BatchExpander
{
    /// <summary>Expands every row of <paramref name="job"/> into a plan. Never throws for a bad row.</summary>
    public static IReadOnlyList<BatchItem> Expand(BatchJob job, Recipe recipe, OutputNameAllocator? allocator = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(job.OutputFolder);

        allocator ??= new OutputNameAllocator();
        var outputFolder = Path.GetFullPath(job.OutputFolder);
        var extensions = job.Formats.Distinct().Select(ExtensionOf).ToArray();
        var padding = Math.Max(3, job.Rows.Count.ToString(CultureInfo.InvariantCulture).Length);
        var items = new List<BatchItem>(job.Rows.Count);

        for (var index = 0; index < job.Rows.Count; index++)
        {
            var row = job.Rows[index];
            try
            {
                var values = recipe.ResolveValues(row.Values);
                values[BatchVariables.Row] = (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(padding, '0');
                values[BatchVariables.RowCount] = job.Rows.Count.ToString(CultureInfo.InvariantCulture);

                var pattern = job.OutputNamePattern
                              ?? (values.ContainsKey(BatchVariables.OutputName)
                                  ? PlaceholderSyntax.Format(BatchVariables.OutputName)
                                  : $"{recipe.Name}_{PlaceholderSyntax.Format(BatchVariables.Row)}");
                var nameErrors = new List<string>();
                var desiredName = VariableSubstitution.Apply(pattern, values, nameErrors);
                if (nameErrors.Count > 0)
                {
                    throw new RecipeVariableException(recipe.Name, nameErrors);
                }

                var baseName = allocator.Allocate(outputFolder, desiredName, extensions);
                var basePath = Path.Combine(outputFolder, baseName);
                values[BatchVariables.OutputName] = baseName;
                values[BatchVariables.OutputFolder] = outputFolder;
                values[BatchVariables.OutputBase] = basePath;

                var plan = recipe.Instantiate(values);
                var actions = plan.Actions.ToList();
                var files = new List<string>();
                foreach (var format in job.Formats.Distinct())
                {
                    var path = basePath + ExtensionOf(format);
                    files.Add(path);
                    actions.Add(CreateOutputAction(format, path, UniqueId(actions, "batch_" + format.ToString().ToLowerInvariant())));
                }

                // Only documents the plan itself created are closed; the user's own open document never is.
                if (job.CloseDocumentAfterEachRow && plan.Target == DocumentTarget.NewDocument &&
                    actions.LastOrDefault() is not CloseDocumentAction)
                {
                    actions.Add(new CloseDocumentAction { Id = UniqueId(actions, "batch_close") });
                }

                plan = plan with
                {
                    Name = Msg.Format("Batch.PlanName", job.Name, row.RowNumber),
                    Actions = actions,
                    Outputs = job.Formats.Distinct().Select(format => new OutputRequirement(format, basePath + ExtensionOf(format))).ToArray(),
                };

                var validation = plan.Validate();
                if (!validation.IsValid)
                {
                    items.Add(new BatchItem { RowNumber = row.RowNumber, OutputBaseName = baseName, Error = validation.ToString() });
                    continue;
                }

                items.Add(new BatchItem { RowNumber = row.RowNumber, Plan = plan, OutputBaseName = baseName, OutputFiles = files });
            }
            catch (Exception exception) when (exception is RecipeVariableException or ArgumentException)
            {
                items.Add(new BatchItem { RowNumber = row.RowNumber, Error = exception.Message });
            }
        }

        return items;
    }

    public static string ExtensionOf(OutputFormat format) => format switch
    {
        OutputFormat.Cdr => ".cdr",
        OutputFormat.Pdf => ".pdf",
        OutputFormat.Png => ".png",
        OutputFormat.Svg => ".svg",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static CorelAction CreateOutputAction(OutputFormat format, string path, string id) => format switch
    {
        OutputFormat.Cdr => new SaveDocumentAction { Id = id, FilePath = path },
        OutputFormat.Pdf => new ExportPdfAction { Id = id, FilePath = path },
        OutputFormat.Png => new ExportPngAction { Id = id, FilePath = path },
        OutputFormat.Svg => new ExportSvgAction { Id = id, FilePath = path },
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static string UniqueId(List<CorelAction> actions, string desired)
    {
        var candidate = desired;
        for (var suffix = 2; actions.Any(action => action.Id == candidate); suffix++)
        {
            candidate = $"{desired}_{suffix}";
        }

        return candidate;
    }
}

public sealed record BatchItemResult
{
    public required int RowNumber { get; init; }
    public required bool Success { get; init; }
    public string? OutputBaseName { get; init; }
    public IReadOnlyList<string> ProducedFiles { get; init; } = [];
    public string? Error { get; init; }
    public PlanExecutionResult? Execution { get; init; }
}

public sealed record BatchProgress(int Total, int Completed, int Succeeded, int Failed, int? CurrentRowNumber, string? CurrentOutputName)
{
    public double Percent => Total == 0 ? 100 : Completed * 100.0 / Total;
}

public sealed record BatchResult
{
    public required string JobId { get; init; }
    public IReadOnlyList<BatchItemResult> Items { get; init; } = [];
    public bool Cancelled { get; init; }
    public bool StoppedOnError { get; init; }
    public TimeSpan Duration { get; init; }

    public int Total => Items.Count;
    public int Succeeded => Items.Count(item => item.Success);
    public int Failed => Items.Count(item => !item.Success);
    public bool Success => !Cancelled && !StoppedOnError && Failed == 0;
    public IEnumerable<string> ProducedFiles => Items.SelectMany(item => item.ProducedFiles);
}

/// <summary>Executes an expanded batch sequentially through any <see cref="ICorelActionExecutor"/>.</summary>
public sealed class BatchRunner(ICorelActionExecutor executor)
{
    public async Task<BatchResult> RunAsync(
        BatchJob job,
        Recipe recipe,
        IProgress<BatchProgress>? progress = null,
        OutputNameAllocator? allocator = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var items = BatchExpander.Expand(job, recipe, allocator);
        var results = new List<BatchItemResult>(items.Count);
        var cancelled = false;
        var stopped = false;

        BatchProgress Snapshot(BatchItem? current) => new(
            items.Count,
            results.Count,
            results.Count(result => result.Success),
            results.Count(result => !result.Success),
            current?.RowNumber,
            current?.OutputBaseName);

        foreach (var item in items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            progress?.Report(Snapshot(item));
            BatchItemResult result;
            if (!item.IsValid)
            {
                result = new BatchItemResult { RowNumber = item.RowNumber, Success = false, OutputBaseName = item.OutputBaseName, Error = item.Error };
            }
            else
            {
                var execution = await executor.ExecuteAsync(item.Plan!, cancellationToken: cancellationToken).ConfigureAwait(false);
                result = new BatchItemResult
                {
                    RowNumber = item.RowNumber,
                    Success = execution.Success,
                    OutputBaseName = item.OutputBaseName,
                    ProducedFiles = execution.ProducedFiles,
                    Error = execution.Success ? null : execution.Summary,
                    Execution = execution,
                };
                cancelled = execution.Status == PlanExecutionStatus.Cancelled;
            }

            results.Add(result);
            progress?.Report(Snapshot(null));
            if (cancelled)
            {
                break;
            }

            if (!result.Success && !job.ContinueOnError)
            {
                stopped = true;
                break;
            }
        }

        return new BatchResult
        {
            JobId = job.Id,
            Items = results,
            Cancelled = cancelled,
            StoppedOnError = stopped,
            Duration = stopwatch.Elapsed,
        };
    }
}
