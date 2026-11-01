namespace CorgiBlackboard.Infrastructure.Diagnostics;

public static class DiagnosticSessionShutdown
{
    public static Task ShutdownAsync(DiagnosticSession? session) =>
        session is null ? Task.CompletedTask : session.DisposeAsync().AsTask();
}
