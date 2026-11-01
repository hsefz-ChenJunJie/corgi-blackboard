using CorgiBlackboard.Infrastructure.Diagnostics;
using Xunit;

namespace CorgiBlackboard.UiTests;

public sealed class DiagnosticInitializationTests
{
    private static readonly Guid SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherSessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    [Fact]
    public void Preview_lists_only_owned_initialization_files_when_metadata_was_never_published()
    {
        // Given an exclusively claimed initialization with partial metadata and a malformed PID marker.
        var entries = new[] { ".active.lock", "session.json.tmp" };
        var snapshot = new DiagnosticInitializationSnapshot(SessionId, null, entries);
        // When the read-only cleanup preview runs.
        var preview = DiagnosticInitializationPlan.Preview(snapshot);
        // Then it targets precisely the initial files without mutating the snapshot.
        Assert.Equal(entries, preview);
        Assert.NotSame(entries, preview);
    }

    [Theory]
    [InlineData("events-0001.jsonl")]
    [InlineData(".lease-export")]
    [InlineData("lesson.png")]
    [InlineData("subdirectory/")]
    [InlineData("../session.json")]
    public void Preview_refuses_cleanup_when_history_or_unowned_entries_exist(string entry)
    {
        // Given an owned claim whose directory has acquired something outside initialization.
        var snapshot = new DiagnosticInitializationSnapshot(SessionId, null, [".active.lock", entry]);
        // When previewing deletion, then no target is authorized.
        Assert.Null(DiagnosticInitializationPlan.Preview(snapshot));
    }

    [Fact]
    public void Preview_refuses_cleanup_when_metadata_belongs_to_another_session()
    {
        // Given a claim and unrelated historical metadata.
        var snapshot = new DiagnosticInitializationSnapshot(SessionId, OtherSessionId, ["session.json"]);
        // When previewing deletion, then the historical directory is protected.
        Assert.Null(DiagnosticInitializationPlan.Preview(snapshot));
    }

    [Fact]
    public void Preview_refuses_cleanup_when_existing_metadata_has_no_initialization_claim()
    {
        // Given existing unreadable metadata, rather than a partial temporary file.
        var snapshot = new DiagnosticInitializationSnapshot(SessionId, null, ["session.json"]);
        // When previewing deletion, then no deletion is permitted.
        Assert.Null(DiagnosticInitializationPlan.Preview(snapshot));
    }

    [Fact]
    public void Preview_includes_matching_metadata_when_initialization_failed_after_its_write()
    {
        // Given a failed initialization that had written its own metadata.
        var id = SessionId;
        var snapshot = new DiagnosticInitializationSnapshot(id, id, [".active.lock", "session.json", "session.json.tmp"]);
        // When the read-only preview runs.
        var preview = DiagnosticInitializationPlan.Preview(snapshot);
        // Then exactly those initial files are eligible.
        Assert.Equal(snapshot.Entries, preview);
    }
}
