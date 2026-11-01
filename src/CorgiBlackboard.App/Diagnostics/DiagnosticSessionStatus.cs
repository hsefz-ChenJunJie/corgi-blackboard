using System.Text.Json;

namespace CorgiBlackboard.App.Diagnostics;

internal static class DiagnosticSessionStatus
{
    internal static bool IsAbnormal(JsonElement metadata, string? activeMarker) =>
        activeMarker is not null && !int.TryParse(activeMarker, out _)
        || metadata.TryGetProperty("abnormalEnd", out var abnormal) && abnormal.GetBoolean()
        || metadata.TryGetProperty("incomplete", out var incomplete) && incomplete.GetBoolean();
}
