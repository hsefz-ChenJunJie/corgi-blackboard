using CorgiBlackboard.App.Diagnostics;
using Xunit;

namespace CorgiBlackboard.UiTests;

public sealed class DiagnosticStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Storage_failure_retains_degraded_state_and_allows_startup_to_continue(bool accessDenied)
    {
        Exception failure = accessDenied ? new UnauthorizedAccessException("log access denied") : new IOException("log disk full");
        var startup = DiagnosticStartup<object>.Start(() => throw failure);

        Assert.True(startup.IsDegraded);
        Assert.Null(startup.Session);
        Assert.Same(failure, startup.Failure);
    }

    [Fact]
    public void Successful_initialization_preserves_the_session_for_shutdown()
    {
        var session = new object();
        var startup = DiagnosticStartup<object>.Start(() => session);

        Assert.False(startup.IsDegraded);
        Assert.Same(session, startup.Session);
        Assert.Null(startup.Failure);
    }

    [Fact]
    public void Programming_errors_are_not_treated_as_storage_failures()
    {
        var failure = new InvalidOperationException("invalid initialization");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            DiagnosticStartup<object>.Start(() => throw failure)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Diagnostics_action_explains_unavailable_capture_when_storage_initialization_failed(bool accessDenied)
    {
        // Given the same startup result supplied to the teaching window.
        Exception error = accessDenied ? new UnauthorizedAccessException("private-path") : new IOException("secret");
        var startup = DiagnosticStartup<object>.Start(() => throw error);
        var opened = false;
        string? shown = null;
        // When the toolbar uses the production action dispatcher.
        startup.OpenDiagnostics(_ => opened = true, message => shown = message);
        // Then the persistent degraded status also backs a useful unavailable action.
        Assert.True(startup.IsDegraded);
        Assert.False(opened);
        Assert.False(string.IsNullOrWhiteSpace(startup.StatusText));
        Assert.Equal(startup.StatusText, shown);
        Assert.DoesNotContain(error.Message, shown);
    }

    [Fact]
    public void Diagnostics_action_opens_the_existing_session_when_startup_succeeded()
    {
        // Given successful startup.
        var session = new object();
        var startup = DiagnosticStartup<object>.Start(() => session);
        object? opened = null;
        var unavailable = false;
        // When the toolbar dispatches.
        startup.OpenDiagnostics(value => opened = value, _ => unavailable = true);
        // Then the real session is selected and no degraded status is required.
        Assert.Same(session, opened);
        Assert.False(unavailable);
        Assert.False(startup.IsDegraded);
        Assert.Empty(startup.StatusText);
    }
}
