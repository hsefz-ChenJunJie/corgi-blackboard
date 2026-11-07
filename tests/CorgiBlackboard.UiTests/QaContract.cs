using System.Text.Json;
using System.Text.Json.Nodes;

namespace CorgiBlackboard.UiTests;

internal static class QaContract
{
    internal static readonly string[] Scenarios =
    [
        "ShellLayout", "BoardInkErase", "TouchCancel", "ImageWindows",
        "ImageFitRestore", "DiagnosticsExport", "HarnessFailure", "ReleaseSmoke"
    ];
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static int ExitCode(bool assertionsPassed, bool cleanupPassed, bool evidenceComplete) =>
        assertionsPassed && cleanupPassed && evidenceComplete ? 0 : 1;

    internal static (int X, int Y) Physical(double left, double top, double xDip, double yDip, uint dpi)
    {
        Require(dpi > 0 && new[] { left, top, xDip, yDip }.All(double.IsFinite), "Invalid physical coordinate transform.");
        return (checked((int)Math.Round(left + xDip * dpi / 96d)),
            checked((int)Math.Round(top + yDip * dpi / 96d)));
    }

    internal static JsonObject ParseState(string status)
    {
        var state = JsonNode.Parse(status)?.AsObject() ?? throw new InvalidDataException("Missing QA state.");
        Require(state["schemaVersion"]?.GetValue<int>() == 1, "Unknown QA snapshot schema.");
        Require(state["channel"]?.GetValue<string>() == "Test", "QA observation requires Test channel.");
        Require(state["gitHash"]?.GetValue<string>() is { Length: 40 or 64 } hash && hash.All(Uri.IsHexDigit),
            "Missing source commit in executable.");
        Require(!string.IsNullOrWhiteSpace(state["version"]?.GetValue<string>()), "Missing executable build version.");
        return state;
    }

    internal static long Number(JsonNode node, string key) => node[key]!.GetValue<long>();
    internal static double Value(JsonNode node, string key) => node[key]!.GetValue<double>();
    internal static bool Flag(JsonNode node, string key) => node[key]!.GetValue<bool>();

    internal static bool GestureStarted(JsonNode state) => state["state"]!.GetValue<string>() == "Editing";
}
