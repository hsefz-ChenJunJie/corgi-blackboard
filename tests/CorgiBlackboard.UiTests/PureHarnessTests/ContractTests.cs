using System.Text.Json.Nodes;
using Xunit;

namespace CorgiBlackboard.UiTests;

public sealed class ContractTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void Failed_assertion_cleanup_or_missing_evidence_cannot_pass(bool assertion, bool cleanup, bool evidence) =>
        Assert.NotEqual(0, QaContract.ExitCode(assertion, cleanup, evidence));

    [Fact]
    public void Complete_run_can_pass() => Assert.Equal(0, QaContract.ExitCode(true, true, true));

    [Theory]
    [InlineData(96u, 110, 220)]
    [InlineData(144u, 160, 320)]
    [InlineData(192u, 210, 420)]
    public void Dip_conversion_applies_scale_once_from_uia_physical_origin(uint dpi, int x, int y) =>
        Assert.Equal((x, y), QaContract.Physical(10, 20, 100, 200, dpi));

    [Fact]
    public void Negative_monitor_origins_are_preserved() =>
        Assert.Equal((-1770, 150), QaContract.Physical(-1920, 0, 100, 100, 144));

    [Fact]
    public void Nonfinite_coordinates_are_rejected() =>
        Assert.Throws<InvalidOperationException>(() => QaContract.Physical(0, 0, double.NaN, 0, 96));

    [Theory]
    [InlineData("Test", "unknown")]
    [InlineData("Test", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Unknown_build_identity_is_rejected(string channel, string commit)
    {
        var state = new JsonObject { ["schemaVersion"] = 1, ["channel"] = channel, ["gitHash"] = commit, ["version"] = "0.1.0-test.1" };
        Assert.Throws<InvalidOperationException>(() => QaContract.ParseState(state.ToJsonString()));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void Stable_channel_is_rejected_when_all_other_build_identity_fields_are_valid(int hashLength)
    {
        // Given a valid build identity which the Test channel accepts.
        var state = new JsonObject { ["schemaVersion"] = 1, ["channel"] = "Test",
            ["gitHash"] = new string('a', hashLength), ["version"] = "0.1.0-test.1" };
        QaContract.ParseState(state.ToJsonString());

        // When only the channel changes to Stable.
        state["channel"] = "Stable";

        // Then the channel gate independently rejects it.
        Assert.Throws<InvalidOperationException>(() => QaContract.ParseState(state.ToJsonString()));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void Exe_bound_snapshot_retains_its_source_identity(int hashLength)
    {
        var commit = new string('a', hashLength);
        var state = QaContract.ParseState(new JsonObject
        { ["schemaVersion"] = 1, ["channel"] = "Test", ["gitHash"] = commit, ["version"] = "0.1.0-test.1" }.ToJsonString());
        Assert.Equal(commit, state["gitHash"]!.GetValue<string>());
    }
}
