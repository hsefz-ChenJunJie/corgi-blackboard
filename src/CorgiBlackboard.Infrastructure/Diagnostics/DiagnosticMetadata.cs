using System.Text.Json;
using System.Text.Json.Nodes;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

internal static class DiagnosticMetadata
{
    internal static JsonObject? Read(string directory)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "session.json"))) as JsonObject; }
        catch (JsonException) { return null; }
        catch (FileNotFoundException) { return null; }
    }

    internal static bool Boolean(JsonObject? metadata, string name, bool fallback = false) =>
        metadata?[name] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : fallback;

    internal static string Text(JsonObject? metadata, string name) =>
        metadata?[name] is JsonValue value && value.TryGetValue<string>(out var result) ? result : "unknown";

    internal static void Write(string directory, JsonObject metadata)
    {
        var path = Path.Combine(directory, "session.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, metadata.ToJsonString());
        File.Move(temporary, path, true);
    }

    internal static IEnumerable<string> EventFiles(string directory) =>
        Directory.EnumerateFiles(directory, "events-*.jsonl")
            .Where(path => IsEventFile(Path.GetFileName(path)))
            .OrderBy(path => long.Parse(Path.GetFileNameWithoutExtension(path).AsSpan(7),
                System.Globalization.CultureInfo.InvariantCulture));

    private static bool IsEventFile(string name)
    {
        if (!name.StartsWith("events-", StringComparison.Ordinal) || !name.EndsWith(".jsonl", StringComparison.Ordinal))
            return false;
        var number = name.AsSpan(7, name.Length - 13);
        return number.Length >= 4 && long.TryParse(number, out var index) && index > 0 &&
            number.SequenceEqual(index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture).AsSpan());
    }
}
