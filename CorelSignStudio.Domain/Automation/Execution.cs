using System.Diagnostics;
using CorelSignStudio.Domain.Localization;

namespace CorelSignStudio.Domain.Automation;

public enum PlanExecutionStatus
{
    Succeeded,
    ValidationFailed,
    Failed,
    Cancelled,
}

public sealed record ExecutionOptions
{
    /// <summary>Undo the plan's changes to an existing document when an action fails.</summary>
    public bool RollbackOnFailure { get; init; } = true;
}

/// <summary>What a single action did.</summary>
public sealed record ActionOutcome
{
    public static ActionOutcome Empty { get; } = new();

    public IReadOnlyList<string> CreatedObjectIds { get; init; } = [];
    public IReadOnlyList<string> ModifiedObjectIds { get; init; } = [];
    public IReadOnlyList<string> ProducedFiles { get; init; } = [];
    public string? Message { get; init; }
}

public sealed record ActionExecutionResult
{
    public required string ActionId { get; init; }
    public required string ActionType { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string> CreatedObjectIds { get; init; } = [];
    public IReadOnlyList<string> ModifiedObjectIds { get; init; } = [];
    public IReadOnlyList<string> ProducedFiles { get; init; } = [];
    public double DurationMs { get; init; }
}

public sealed record PlanExecutionResult
{
    public required string PlanId { get; init; }
    public required PlanExecutionStatus Status { get; init; }
    public bool Success => Status == PlanExecutionStatus.Succeeded;
    public IReadOnlyList<string> CompletedActionIds { get; init; } = [];
    public string? FailedActionId { get; init; }
    public string? FailedActionType { get; init; }

    /// <summary>1-based position of the failed action in the plan.</summary>
    public int? FailedActionIndex { get; init; }

    public string? ErrorMessage { get; init; }
    public string? ErrorDetails { get; init; }
    public IReadOnlyList<PlanValidationError> ValidationErrors { get; init; } = [];
    public IReadOnlyList<string> CreatedObjectIds { get; init; } = [];
    public IReadOnlyList<string> ModifiedObjectIds { get; init; } = [];
    public IReadOnlyList<string> ProducedFiles { get; init; } = [];
    public IReadOnlyList<ActionExecutionResult> ActionResults { get; init; } = [];
    public TimeSpan Duration { get; init; }

    /// <summary>True when the document changes made before the failure were undone.</summary>
    public bool RolledBack { get; init; }

    /// <summary>Explains what was or was not undone after a failure.</summary>
    public string? RollbackNote { get; init; }

    public string Summary => Status switch
    {
        PlanExecutionStatus.Succeeded => Msg.Format("Execution.Summary.Succeeded", CompletedActionIds.Count),
        PlanExecutionStatus.ValidationFailed => Msg.Format("Execution.Summary.ValidationFailed", string.Join("; ", ValidationErrors)),
        PlanExecutionStatus.Cancelled => Msg.Format("Execution.Summary.Cancelled", CompletedActionIds.Count),
        _ => Msg.Format("Execution.Summary.Failed", FailedActionIndex, FailedActionId, ErrorMessage),
    };
}

public sealed record ActionProgress(int Index, int Total, CorelAction Action, ActionExecutionResult? Result);

/// <summary>Runs a plan against the design application.</summary>
public interface ICorelActionExecutor
{
    Task<PlanExecutionResult> ExecuteAsync(
        AutomationPlan plan,
        ExecutionOptions? options = null,
        IProgress<ActionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record RollbackOutcome(bool RolledBack, string Note);

/// <summary>
/// The application-specific half of plan execution: applies one action at a time and can undo.
/// <see cref="PlanExecutionEngine"/> owns ordering, timing and result reporting, so every backend
/// (CorelDRAW today, test fakes, other hosts later) reports failures identically.
/// </summary>
public interface IAutomationSession
{
    void Begin(AutomationPlan plan);

    ActionOutcome Execute(CorelAction action);

    /// <summary>Called once after the last action succeeded.</summary>
    void Complete();

    /// <summary>Called once after an action failed, when rollback was requested.</summary>
    RollbackOutcome Rollback();
}

public static class PlanExecutionEngine
{
    public static PlanExecutionResult Run(
        AutomationPlan plan,
        IAutomationSession session,
        ExecutionOptions? options = null,
        IProgress<ActionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(session);
        options ??= new ExecutionOptions();

        var validation = plan.Validate();
        if (!validation.IsValid)
        {
            return new PlanExecutionResult
            {
                PlanId = plan.Id,
                Status = PlanExecutionStatus.ValidationFailed,
                ValidationErrors = validation.Errors,
                ErrorMessage = Msg.Get("Execution.NotExecuted"),
                ErrorDetails = validation.ToString(),
            };
        }

        var stopwatch = Stopwatch.StartNew();
        var results = new List<ActionExecutionResult>();
        var created = new List<string>();
        var modified = new List<string>();
        var files = new List<string>();

        PlanExecutionResult Build(PlanExecutionStatus status) => new()
        {
            PlanId = plan.Id,
            Status = status,
            CompletedActionIds = results.Where(result => result.Success).Select(result => result.ActionId).ToArray(),
            CreatedObjectIds = created.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ModifiedObjectIds = modified.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ProducedFiles = files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ActionResults = results.ToArray(),
            Duration = stopwatch.Elapsed,
        };

        PlanExecutionResult Fail(CorelAction? action, int index, Exception exception, bool begun)
        {
            var rollback = begun && options.RollbackOnFailure
                ? TryRollback(session)
                : new RollbackOutcome(false, Msg.Get(begun ? "Execution.RollbackNotRequested" : "Execution.NothingChanged"));
            return Build(PlanExecutionStatus.Failed) with
            {
                FailedActionId = action?.Id,
                FailedActionType = action?.TypeName,
                FailedActionIndex = action is null ? null : index + 1,
                ErrorMessage = Describe(exception),
                ErrorDetails = exception.ToString(),
                RolledBack = rollback.RolledBack,
                RollbackNote = rollback.Note,
            };
        }

        try
        {
            session.Begin(plan);
        }
        catch (Exception exception)
        {
            return Fail(null, -1, exception, begun: false);
        }

        for (var index = 0; index < plan.Actions.Count; index++)
        {
            var action = plan.Actions[index];
            if (cancellationToken.IsCancellationRequested)
            {
                var rollback = options.RollbackOnFailure
                    ? TryRollback(session)
                    : new RollbackOutcome(false, Msg.Get("Execution.RollbackNotRequested"));
                return Build(PlanExecutionStatus.Cancelled) with
                {
                    ErrorMessage = Msg.Get("Execution.Cancelled"),
                    RolledBack = rollback.RolledBack,
                    RollbackNote = rollback.Note,
                };
            }

            progress?.Report(new ActionProgress(index + 1, plan.Actions.Count, action, null));
            var actionWatch = Stopwatch.StartNew();
            try
            {
                var outcome = session.Execute(action) ?? ActionOutcome.Empty;
                var result = new ActionExecutionResult
                {
                    ActionId = action.Id,
                    ActionType = action.TypeName,
                    Success = true,
                    Message = outcome.Message,
                    CreatedObjectIds = outcome.CreatedObjectIds,
                    ModifiedObjectIds = outcome.ModifiedObjectIds,
                    ProducedFiles = outcome.ProducedFiles,
                    DurationMs = actionWatch.Elapsed.TotalMilliseconds,
                };
                results.Add(result);
                created.AddRange(outcome.CreatedObjectIds);
                modified.AddRange(outcome.ModifiedObjectIds);
                files.AddRange(outcome.ProducedFiles);
                progress?.Report(new ActionProgress(index + 1, plan.Actions.Count, action, result));
            }
            catch (Exception exception)
            {
                var result = new ActionExecutionResult
                {
                    ActionId = action.Id,
                    ActionType = action.TypeName,
                    Success = false,
                    Error = Describe(exception),
                    DurationMs = actionWatch.Elapsed.TotalMilliseconds,
                };
                results.Add(result);
                progress?.Report(new ActionProgress(index + 1, plan.Actions.Count, action, result));
                return Fail(action, index, exception, begun: true);
            }
        }

        try
        {
            session.Complete();
        }
        catch (Exception exception)
        {
            return Build(PlanExecutionStatus.Failed) with
            {
                ErrorMessage = Msg.Format("Execution.CompleteFailed", Describe(exception)),
                ErrorDetails = exception.ToString(),
                RollbackNote = Msg.Get("Execution.ChangesKept"),
            };
        }

        return Build(PlanExecutionStatus.Succeeded);
    }

    private static RollbackOutcome TryRollback(IAutomationSession session)
    {
        try
        {
            return session.Rollback();
        }
        catch (Exception exception)
        {
            return new RollbackOutcome(false, Msg.Format("Execution.RollbackFailed", Describe(exception)));
        }
    }

    private static string Describe(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? exception.GetType().Name : exception.Message;
}
