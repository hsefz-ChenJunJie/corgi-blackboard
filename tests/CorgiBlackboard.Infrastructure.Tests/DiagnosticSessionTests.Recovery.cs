using System.Text.Json;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CorgiBlackboard.Core.Diagnostics;
using CorgiBlackboard.Infrastructure.Diagnostics;
using Xunit;

namespace CorgiBlackboard.Infrastructure.Tests;

public sealed partial class DiagnosticSessionTests
{
    [Fact]
    public async Task RefusedRotationFileAfterPersistedHistoryRetainsFaultThroughRecovery()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions
        {
            RootDirectory = temp.Path,
            MaximumFileBytes = 4096
        });
        Assert.True(session.TryRecord(new DiagnosticEvent("persisted-history")));
        await session.FlushAsync();
        var historyPath = Path.Combine(session.DirectoryPath, "events-0001.jsonl");
        byte[] historyBytes;
        using (var historyReader = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var snapshot = new MemoryStream())
        {
            historyReader.CopyTo(snapshot);
            historyBytes = snapshot.ToArray();
        }
        Assert.Contains("persisted-history", System.Text.Encoding.UTF8.GetString(historyBytes));

        // A directory at the next file path makes FileStream.CreateNew fail in
        // the actual filesystem, after serialization and rotation have occurred.
        var refusedPath = Path.Combine(session.DirectoryPath, "events-0002.jsonl");
        Directory.CreateDirectory(refusedPath);
        var degraded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Degraded += message => degraded.TrySetResult(message);
        Assert.True(session.TryRecord(new DiagnosticEvent("refused-file",
            Data: new Dictionary<string, string> { ["payload"] = new string('x', 3000) })));
        Assert.Contains("Diagnostics degraded", await degraded.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(session.IsDegraded);
        Assert.True(session.IsIncomplete);
        Assert.Equal(historyBytes, File.ReadAllBytes(historyPath));
        Assert.Contains(ReadRecords(session.DirectoryPath), record => record.GetProperty("name").GetString() == "persisted-history");

        Directory.Delete(refusedPath);
        Assert.True(session.TryRecord(new DiagnosticEvent("sink-recovered")));
        await session.FlushAsync();
        await session.DisposeAsync();
        Assert.False(File.Exists(Path.Combine(session.DirectoryPath, ".active.lock")));
        var manifestPath = Path.Combine(session.DirectoryPath, "session.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        Assert.True(manifest["incomplete"]!.GetValue<bool>());
        Assert.True(manifest["unknownTail"]!.GetValue<bool>());
        var gapsBeforeRecovery = manifest["knownGaps"]!.ToJsonString();
        Assert.Equal(3, manifest["knownGaps"]![0]!["start"]!.GetValue<long>());
        Assert.Equal(3, manifest["knownGaps"]![0]!["end"]!.GetValue<long>());
        var records = ReadRecords(session.DirectoryPath);
        Assert.DoesNotContain(records, record => record.GetProperty("name").GetString() == "refused-file");
        Assert.Contains(records, record => record.GetProperty("name").GetString() == "sink-recovered");
        Assert.Equal("session-end", records[^1].GetProperty("name").GetString());

        // Recreate abandoned-process metadata from this actual failed session.
        // Recovery must preserve its fault provenance rather than clear it.
        var lastActivity = manifest["lastActivityUtc"]!.GetValue<DateTimeOffset>();
        manifest["active"] = true;
        manifest["processId"] = int.MaxValue;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        File.WriteAllText(Path.Combine(session.DirectoryPath, ".active.lock"), int.MaxValue.ToString());
        var recovered = new DiagnosticLogManager(temp.Path).RecoverAbandonedSessions();
        Assert.Single(recovered);
        Assert.Equal(lastActivity, recovered[0].LastActivityUtc);
        var recoveredManifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
        Assert.True(recoveredManifest["incomplete"]!.GetValue<bool>());
        Assert.True(recoveredManifest["unknownTail"]!.GetValue<bool>());
        Assert.True(recoveredManifest["abnormalEnd"]!.GetValue<bool>());
        Assert.Equal(gapsBeforeRecovery, recoveredManifest["knownGaps"]!.ToJsonString());
        Assert.Equal(historyBytes, File.ReadAllBytes(historyPath));
        Assert.Equal(records.Length, ReadRecords(session.DirectoryPath).Length);
        Assert.Empty(new DiagnosticLogManager(temp.Path).RecoverAbandonedSessions());
    }

    [Fact]
    public async Task RecoveryPreservesMalformedTailAndMarksSessionIncomplete()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var sessionDirectory = CreateSession(temp.Path, "crashed", DateTimeOffset.UtcNow.AddMinutes(-10), active: true, processId: int.MaxValue);
        File.WriteAllText(Path.Combine(sessionDirectory, "events-0001.jsonl"), "{\"sequence\":1}\n{\"sequence\":");
        var recovered = new DiagnosticLogManager(temp.Path).RecoverAbandonedSessions();
        Assert.Single(recovered);
        Assert.True(recovered[0].Incomplete);
        var tail = File.ReadAllText(Path.Combine(sessionDirectory, "events-0001.jsonl"));
        Assert.EndsWith("{\"sequence\":", tail);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(sessionDirectory, "session.json")));
        Assert.True(metadata.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("abnormalEnd").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("unknownTail").GetBoolean());
    }

    [Fact]
    public void RecoveryPreservesExistingUnknownTailAndLastActivity()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var lastActivity = new DateTimeOffset(2025, 7, 9, 11, 12, 13, TimeSpan.Zero);
        var sessionDirectory = CreateSession(temp.Path, "stale-with-tail", lastActivity, active: true, processId: int.MaxValue, unknownTail: true);
        File.WriteAllText(Path.Combine(sessionDirectory, "events-0001.jsonl"), "{\"sequence\":1}\n");

        var recovered = new DiagnosticLogManager(temp.Path).RecoverAbandonedSessions();

        Assert.Single(recovered);
        Assert.Equal(lastActivity, recovered[0].LastActivityUtc);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(sessionDirectory, "session.json")));
        Assert.True(manifest.RootElement.GetProperty("unknownTail").GetBoolean());
        Assert.Equal(lastActivity, manifest.RootElement.GetProperty("lastActivityUtc").GetDateTimeOffset());
    }

}
