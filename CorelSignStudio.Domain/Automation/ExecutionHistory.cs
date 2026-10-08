namespace CorelSignStudio.Domain.Automation;

/// <summary>A record of one executed plan, kept so jobs can be reviewed, repeated or turned into recipes.</summary>
public sealed record ExecutionHistoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public required AutomationPlan Plan { get; init; }
    public required PlanExecutionResult Result { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => $"{Plan.Name}: {Result.Summary}";
}

public interface IExecutionHistoryStore
{
    void Append(ExecutionHistoryEntry entry);

    /// <summary>Most recent first.</summary>
    IReadOnlyList<ExecutionHistoryEntry> GetRecent(int maxCount = 100);
}
