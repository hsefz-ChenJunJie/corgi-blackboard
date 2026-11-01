namespace CorgiBlackboard.Infrastructure.Diagnostics;

public sealed record DiagnosticOptions
{
    public string? RootDirectory { get; init; }
    public int QueueCapacity { get; init; } = 4096;
    public long MaximumFileBytes { get; init; } = 32L * 1024 * 1024;
    public int RetentionDays { get; init; } = 14;
    public long RetentionBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public string? ChecklistText { get; init; }
    public Func<CancellationToken, ValueTask>? BeforeWriteAsync { get; init; }
}

public sealed record DiagnosticRetentionResult(long Bytes, long LimitBytes, IReadOnlyList<string> DeletedSessions, IReadOnlyDictionary<string, long> ProtectedOverage)
{
    public long ExcessBytes => Math.Max(0, Bytes - LimitBytes);
    public IReadOnlyDictionary<string, string> ProtectionReasons { get; init; } = new Dictionary<string, string>();
}
