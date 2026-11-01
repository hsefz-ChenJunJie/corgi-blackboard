namespace CorgiBlackboard.Infrastructure.Diagnostics;

internal sealed record DiagnosticInitializationSnapshot(
    Guid SessionId, Guid? MetadataClaimId, IReadOnlyList<string> Entries);

internal static class DiagnosticInitializationPlan
{
    // Read-only target preview. A claim is required by the caller before taking this snapshot.
    // Only initial metadata/marker files are ours; history, leases and unknown entries block cleanup.
    internal static IReadOnlyList<string>? Preview(DiagnosticInitializationSnapshot snapshot)
    {
        if (snapshot.SessionId == Guid.Empty ||
            snapshot.MetadataClaimId is { } metadataId && metadataId != snapshot.SessionId)
            return null;
        if (snapshot.Entries.Contains("session.json") && snapshot.MetadataClaimId is null)
            return null;
        if (snapshot.Entries.Any(name => name is not (".active.lock" or "session.json.tmp" or "session.json")))
            return null;
        return snapshot.Entries.ToArray();
    }
}
