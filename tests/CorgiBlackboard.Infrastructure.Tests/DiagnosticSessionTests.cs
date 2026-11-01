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

public sealed partial class DiagnosticSessionTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private int _writeCount;

    [Fact]
    public async Task RotatesAtSizeBoundariesAndPreservesFileOrder()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        await using var session = new DiagnosticSession(new DiagnosticOptions
        {
            RootDirectory = temp.Path,
            MaximumFileBytes = 2600
        }, () => now, () => 42);
        const int eventCount = 24;
        for (var i = 0; i < eventCount; i++)
            Assert.True(session.TryRecord(new DiagnosticEvent($"event-{i}", Data: new Dictionary<string, string> { ["payload"] = new string('x', 380) })));
        await session.FlushAsync();
        await session.DisposeAsync();
        var files = Directory.GetFiles(session.DirectoryPath, "*.jsonl").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Assert.True(files.Length > 1);
        var byFile = files.Select(path => File.ReadAllLines(path).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray()).ToArray();
        Assert.Equal(eventCount + 2, byFile.Sum(records => records.Length));
        for (var i = 0; i < files.Length; i++)
        {
            var bytes = new FileInfo(files[i]).Length;
            Assert.InRange(bytes, 1, 2600);
            Assert.Equal(byFile[i].OrderBy(r => r.GetProperty("sequence").GetInt64()).Select(r => r.GetProperty("sequence").GetInt64()),
                byFile[i].Select(r => r.GetProperty("sequence").GetInt64()));
            if (i + 1 < files.Length)
            {
                var nextFirstRecordBytes = System.Text.Encoding.UTF8.GetByteCount(File.ReadLines(files[i + 1]).First() + "\n");
                Assert.True(bytes + nextFirstRecordBytes > 2600, "Rotation occurred before the next complete record crossed the limit.");
            }
        }
        var records = byFile.SelectMany(file => file).ToArray();
        Assert.Equal(Enumerable.Range(1, eventCount + 2).Select(i => (long)i), records.Select(r => r.GetProperty("sequence").GetInt64()));
        Assert.Equal("session-end", records[^1].GetProperty("name").GetString());
        Assert.All(records, r =>
        {
            Assert.Equal(1, r.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("Test", r.GetProperty("channel").GetString());
            Assert.Equal(now, r.GetProperty("utc").GetDateTimeOffset());
            Assert.Equal(42, r.GetProperty("monotonicTicks").GetInt64());
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("buildVersion").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("gitHash").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("operatingSystem").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("dpi").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("deviceId").GetString()));
        });
    }

    [Fact]
    public async Task ConcurrentProducersPersistInAssignedSequenceOrder()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path, QueueCapacity = 1024 });
        Parallel.For(0, 512, index =>
            Assert.True(session.TryRecord(new DiagnosticEvent($"parallel-{index}"))));
        await session.FlushAsync();
        await session.DisposeAsync();

        var events = ReadRecords(session.DirectoryPath)
            .Where(record => record.GetProperty("name").GetString()?.StartsWith("parallel-", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Equal(512, events.Length);
        Assert.Equal(events.OrderBy(record => record.GetProperty("sequence").GetInt64())
                .Select(record => record.GetProperty("sequence").GetInt64()),
            events.Select(record => record.GetProperty("sequence").GetInt64()));
        Assert.Equal(512, events.Select(record => record.GetProperty("sequence").GetInt64()).Distinct().Count());
    }

    [Fact]
    public async Task RetentionDeletesOldEligibleSessionsButProtectsActivePinnedAndLeased()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var now = new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        var old = CreateSession(temp.Path, "old", now.AddDays(-30), active: false);
        var active = CreateSession(temp.Path, "active", now.AddDays(-30), active: true);
        var pinned = CreateSession(temp.Path, "pinned", now.AddDays(-30), active: false, pinned: true);
        var leased = CreateSession(temp.Path, "leased", now.AddDays(-30), active: false);
        var manager = new DiagnosticLogManager(temp.Path, () => now);
        var firstLease = manager.Lease(leased);
        var secondLease = manager.Lease(leased);
        firstLease.Dispose();
        var result = manager.ApplyRetention(14, 1);
        Assert.Contains(old, result.DeletedSessions);
        Assert.True(Directory.Exists(active));
        Assert.True(Directory.Exists(pinned));
        Assert.True(Directory.Exists(leased));
        Assert.Contains(active, result.ProtectedOverage.Keys);
        Assert.Contains(pinned, result.ProtectedOverage.Keys);
        Assert.Contains(leased, result.ProtectedOverage.Keys);
        secondLease.Dispose();
        var afterFinalLease = manager.ApplyRetention(14, 1);
        Assert.Contains(leased, afterFinalLease.DeletedSessions);
    }

    [Fact]
    public async Task BuildAndRuntimeIdentityComeFromGeneratedSources()
    {
        var assembly = typeof(DiagnosticSession).Assembly;
        var metadataValues = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);
        Assert.Equal("Test", metadataValues["ReleaseChannel"]);
        Assert.False(string.IsNullOrWhiteSpace(metadataValues["BuildVersion"]));
        Assert.False(string.IsNullOrWhiteSpace(metadataValues["GitHash"]));
        Assert.DoesNotContain(typeof(DiagnosticOptions).GetProperties(), property => property.Name is "Channel" or "ReleaseChannel");

        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path });
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")));
        var root = manifest.RootElement;
        Assert.Equal(metadataValues["ReleaseChannel"], root.GetProperty("channel").GetString());
        Assert.Equal(metadataValues["BuildVersion"], root.GetProperty("buildVersion").GetString());
        Assert.Equal(metadataValues["GitHash"], root.GetProperty("gitHash").GetString());
        Assert.Equal(RuntimeInformation.OSDescription, root.GetProperty("operatingSystem").GetString());
        Assert.Equal(Environment.MachineName, root.GetProperty("deviceId").GetString());
        Assert.NotEqual("unknown", root.GetProperty("dpi").GetString());
        await session.DisposeAsync();
    }

    [Fact]
    public async Task FullRecordSchemaRoundTripsEveryDiagnosticField()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path });
        var samples = new[]
        {
            new DiagnosticPointerSample("stylus-1", "down", 12.5, 43.25, 100),
            new DiagnosticPointerSample("stylus-1", "move", 14.5, 45.25, 101)
        };
        var asset = new DiagnosticAsset("lesson.png", "image/png", 99, new string('a', 64));
        var exception = DiagnosticEvent.FromException(new InvalidOperationException("outer", new IOException("inner")));
        Assert.True(session.TryRecord(new DiagnosticEvent(
            "full-schema", DiagnosticKind.PointerTransition, DiagnosticSeverity.Warning,
            "operation-1", "surface-2", "window-3", "stylus-1", samples,
            new Dictionary<string, string> { ["content"] = "stroke", ["layout"] = "move" },
            asset, 2.75, exception)));
        await session.FlushAsync();
        await session.DisposeAsync();

        var json = ReadRecords(session.DirectoryPath).Single(record => record.GetProperty("name").GetString() == "full-schema");
        var parsed = JsonSerializer.Deserialize<DiagnosticSessionRecord>(json.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(parsed);
        Assert.Equal(1, parsed.SchemaVersion);
        Assert.Equal(session.Id, parsed.SessionId);
        Assert.Equal(1, parsed.Sequence);
        Assert.Equal("Test", parsed.Channel);
        Assert.Equal("operation-1", parsed.OperationId);
        Assert.Equal("surface-2", parsed.SurfaceId);
        Assert.Equal("window-3", parsed.WindowId);
        Assert.Equal("stylus-1", parsed.DeviceIdentifier);
        Assert.Equal(samples, parsed.PointerSamples);
        Assert.Equal("stroke", parsed.Data!["content"]);
        Assert.Equal("move", parsed.Data["layout"]);
        Assert.Equal(asset, parsed.Asset);
        Assert.Equal(2.75, parsed.DurationMilliseconds);
        Assert.Equal("System.InvalidOperationException", parsed.Exception!.Type);
        Assert.Equal("System.IO.IOException", parsed.Exception.Inner!.Type);
    }

    private static string CreateSession(string root, string id, DateTimeOffset activity, bool active, bool pinned = false, int processId = -1, bool unknownTail = false)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "payload"), new string('x', 128));
        File.WriteAllText(Path.Combine(directory, "session.json"), JsonSerializer.Serialize(new
        {
            sessionId = id, active, pinned, processId, lastActivityUtc = activity, incomplete = false, unknownTail
        }));
        return directory;
    }

    private static void WriteHistoricalRecord(string directory, string operatingSystem, string buildVersion, string checklist)
    {
        var metadataPath = Path.Combine(directory, "session.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
        metadata["operatingSystem"] = operatingSystem;
        metadata["buildVersion"] = buildVersion;
        metadata["checklist"] = checklist;
        File.WriteAllText(metadataPath, metadata.ToJsonString());
        File.WriteAllText(Path.Combine(directory, "events-0001.jsonl"), "{\"sequence\":1}\n");
    }

    private static JsonElement[] ReadRecords(string directory) =>
        Directory.GetFiles(directory, "*.jsonl").OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => File.ReadLines(path))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

}
