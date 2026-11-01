using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

internal static class DiagnosticRecovery
{
    internal static IReadOnlyList<RecoveredSession> Recover(string rootDirectory)
    {
        Directory.CreateDirectory(rootDirectory);
        DiagnosticInitialization.Recover(rootDirectory);
        var recovered = new List<RecoveredSession>();
        foreach (var path in Directory.EnumerateDirectories(rootDirectory))
        {
            using var guard = DiagnosticDirectoryAccess.Enter(path);
            if (DiagnosticSession.TryGetLive(path, out _) || !Directory.Exists(path)) continue;
            try
            {
                var metadata = DiagnosticMetadata.Read(path);
                var markerPath = Path.Combine(path, ".active.lock");
                if (!DiagnosticMetadata.Boolean(metadata, "active") && !File.Exists(markerPath)) continue;
                var pid = metadata?["processId"] is JsonValue value && value.TryGetValue<int>(out var parsed) ? parsed : -1;
                if (IsAlive(pid)) continue;
                if (File.Exists(markerPath) && (!int.TryParse(File.ReadAllText(markerPath), out var markerPid) || IsAlive(markerPid)))
                    continue;
                if (DiagnosticDirectoryAccess.IsLeased(path)) continue;
                metadata ??= new JsonObject();
                var last = metadata["lastActivityUtc"] is JsonValue date && date.TryGetValue<DateTimeOffset>(out var activity)
                    ? activity : new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                metadata["active"] = false;
                metadata["incomplete"] = true;
                metadata["abnormalEnd"] = true;
                metadata["lastActivityUtc"] = last;
                var malformed = DiagnosticMetadata.EventFiles(path).Any(file => DiagnosticHistory.Inspect(file).Damaged);
                metadata["unknownTail"] = DiagnosticMetadata.Boolean(metadata, "unknownTail") || malformed;
                DiagnosticMetadata.Write(path, metadata);
                File.Delete(markerPath);
                recovered.Add(new(path, true, last));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        return recovered;
    }

    private static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

public sealed record RecoveredSession(string DirectoryPath, bool Incomplete, DateTimeOffset LastActivityUtc);
