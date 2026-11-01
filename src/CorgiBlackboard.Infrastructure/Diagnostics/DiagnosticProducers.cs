using System.Globalization;
using CorgiBlackboard.Core.Diagnostics;
using CorgiBlackboard.Core.Input;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

public static class DiagnosticProducers
{
    public static DiagnosticEvent ToolSelected(ToolSettings settings) =>
        new("ToolSelected", Data: new Dictionary<string, string>
        {
            ["tool"] = settings.Tool.ToString(),
            ["inkArgb"] = settings.InkArgb.ToString("X8", CultureInfo.InvariantCulture),
            ["inkWidthDip"] = settings.InkWidth.ToString("R", CultureInfo.InvariantCulture),
            ["eraserDiameterDip"] = settings.EraserDiameter.ToString("R", CultureInfo.InvariantCulture)
        });

    public static DiagnosticEvent ImageLoaded(DiagnosticAsset asset, TimeSpan elapsed) =>
        new("ImageLoaded", DiagnosticKind.Asset, Asset: asset, DurationMilliseconds: elapsed.TotalMilliseconds);

    public static DiagnosticEvent Failure(string name, Exception error, TimeSpan elapsed) =>
        new(name, Severity: DiagnosticSeverity.Error, DurationMilliseconds: elapsed.TotalMilliseconds,
            Exception: SafeException(error, 0));

    // Exception messages, stack traces and Data may contain paths, credentials or document content.
    // Retain the bounded type chain only, including for unhandled exceptions.
    private static DiagnosticException SafeException(Exception error, int depth) =>
        new(error.GetType().FullName ?? error.GetType().Name, "", null,
            depth < 7 && error.InnerException is { } inner ? SafeException(inner, depth + 1) : null);

    public static DiagnosticEvent Performance(TimeSpan elapsed) =>
        new("SessionPerformance", DurationMilliseconds: elapsed.TotalMilliseconds,
            Data: new Dictionary<string, string>
            {
                ["managedBytes"] = GC.GetTotalMemory(false).ToString(CultureInfo.InvariantCulture),
                ["gen0Collections"] = GC.CollectionCount(0).ToString(CultureInfo.InvariantCulture),
                ["gen2Collections"] = GC.CollectionCount(2).ToString(CultureInfo.InvariantCulture),
                ["threadPoolThreads"] = ThreadPool.ThreadCount.ToString(CultureInfo.InvariantCulture)
            });
}
