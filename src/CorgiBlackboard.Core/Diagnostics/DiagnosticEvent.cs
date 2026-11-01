namespace CorgiBlackboard.Core.Diagnostics;

public enum DiagnosticSeverity { Trace, Information, Warning, Error }
public enum DiagnosticKind { Event, PointerSample, PointerTransition, Content, Layout, Asset, Gap, SessionEnd }

public sealed record DiagnosticPointerSample(string DeviceId, string State, double X, double Y, long DeviceTimestamp);
public sealed record DiagnosticAsset(string DisplayName, string MediaType, long Length, string Sha256);
public sealed record DiagnosticException(string Type, string Message, string? Stack, DiagnosticException? Inner);

public sealed record DiagnosticEvent(
    string Name,
    DiagnosticKind Kind = DiagnosticKind.Event,
    DiagnosticSeverity Severity = DiagnosticSeverity.Information,
    string? OperationId = null,
    string? SurfaceId = null,
    string? WindowId = null,
    string? DeviceId = null,
    IReadOnlyList<DiagnosticPointerSample>? PointerSamples = null,
    IReadOnlyDictionary<string, string>? Data = null,
    DiagnosticAsset? Asset = null,
    double? DurationMilliseconds = null,
    DiagnosticException? Exception = null)
{
    public static DiagnosticException? FromException(Exception? exception) =>
        exception is null ? null : new(exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message, exception.StackTrace, FromException(exception.InnerException));
}
