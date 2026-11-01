using System.Text.Json;
using CorgiBlackboard.App.Diagnostics;
using Xunit;

namespace CorgiBlackboard.UiTests;

public sealed class DiagnosticSessionStatusTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not-a-pid")]
    public void Status_is_abnormal_when_retained_marker_is_unresolved(string marker)
    {
        // Given readable pre-claim metadata that recovery retains with a malformed marker.
        using var metadata = JsonDocument.Parse("""
            {"sessionId":"11111111-1111-1111-1111-111111111111",
             "active":true,"processId":-1,"incomplete":false,"abnormalEnd":false,"pinned":false}
            """);
        // When projecting the retained session for the diagnostics list.
        var abnormal = DiagnosticSessionStatus.IsAbnormal(metadata.RootElement, marker);
        // Then it must not be presented as normally completed.
        Assert.True(abnormal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("123")]
    public void Status_preserves_normal_classification_when_marker_is_absent_or_readable(string? marker)
    {
        // Given an otherwise normal session, including an active session with a readable marker.
        using var metadata = JsonDocument.Parse("""{"active":true,"incomplete":false,"abnormalEnd":false}""");
        // When projecting its status without looking up any host process.
        var abnormal = DiagnosticSessionStatus.IsAbnormal(metadata.RootElement, marker);
        // Then the marker alone does not mark that session abnormal.
        Assert.False(abnormal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Status_preserves_abnormal_classification_when_metadata_records_unfinished_session(
        bool abnormalEnd, bool incomplete)
    {
        // Given each independently persisted abnormal status with no active marker.
        using var metadata = JsonDocument.Parse(JsonSerializer.Serialize(new { abnormalEnd, incomplete }));
        // When projecting the historical session.
        var abnormal = DiagnosticSessionStatus.IsAbnormal(metadata.RootElement, null);
        // Then the persisted status remains visible.
        Assert.True(abnormal);
    }
}
