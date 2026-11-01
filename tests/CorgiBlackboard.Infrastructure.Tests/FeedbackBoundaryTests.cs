using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CorgiBlackboard.Core.Diagnostics;
using CorgiBlackboard.Infrastructure.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CorgiBlackboard.Infrastructure.Tests;

public sealed class FeedbackBoundaryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LivePinSurvivesWriterFaultAndShutdown()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var writes = 0;
        await using var session = new DiagnosticSession(new DiagnosticOptions
        {
            RootDirectory = temp.Path,
            BeforeWriteAsync = _ => Interlocked.Increment(ref writes) == 1
                ? ValueTask.FromException(new IOException("synthetic refusal"))
                : ValueTask.CompletedTask
        });
        var manager = new DiagnosticLogManager(temp.Path);
        Assert.True(manager.SetPinned(session.DirectoryPath, true, out _));
        Assert.True(session.TryRecord(new DiagnosticEvent("refused")));
        await DiagnosticSessionShutdown.ShutdownAsync(session).WaitAsync(TimeSpan.FromSeconds(10));
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")))!;
        Assert.True(metadata["pinned"]!.GetValue<bool>());
        Assert.True(metadata["incomplete"]!.GetValue<bool>());
        Assert.Empty(manager.ApplyRetention(14, 1).DeletedSessions);
    }

    [Fact]
    public async Task ProtectedSessionsBelowQuotaDoNotReportOverage()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path });
        var result = new DiagnosticLogManager(temp.Path).ApplyRetention();
        Assert.True(result.Bytes < result.LimitBytes);
        Assert.Empty(result.ProtectedOverage);
    }

    [Fact]
    public async Task NewSessionAutomaticallyAppliesPersistedPolicy()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var old = temp.History();
        var manager = new DiagnosticLogManager(temp.Path);
        manager.SavePolicy(new DiagnosticRetentionPolicy(1, long.MaxValue));
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path });
        Assert.False(Directory.Exists(old));
        Assert.True(session.TryRecord(new DiagnosticEvent("still-capturing")));
    }

    [Fact]
    public async Task AppendedMalformedTailCannotChangeCheckpointCompleteness()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var directory = temp.History();
        var path = Path.Combine(directory, "events-0001.jsonl");
        var expected = File.ReadAllBytes(path);
        var destination = Path.Combine(temp.Path, "bounded.zip");
        var result = await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(
            destination, [directory], CopyProgressAsync: (_, _) =>
            {
                File.AppendAllText(path, "{\"sequence\":");
                return ValueTask.CompletedTask;
            }));
        using var archive = ZipFile.OpenRead(destination);
        var manifest = await ReadManifest(archive);
        Assert.False(result.Incomplete);
        Assert.False(manifest["incomplete"]!.GetValue<bool>());
        Assert.False(manifest["sessions"]![0]!["incomplete"]!.GetValue<bool>());
        Assert.Equal(expected, ReadBytes(archive.Entries.Single(entry => entry.FullName.EndsWith("/events.jsonl", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task SeededForbiddenJsonlIsNeverReadOrArchived()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var directory = temp.History();
        foreach (var name in new[] { "credentials.jsonl", "workspace.jsonl", "environment-variables.jsonl", "events-backup.jsonl", "events-0001.jsonl.bak" })
            File.WriteAllText(Path.Combine(directory, name), "SYNTHETIC_NOT_A_REAL_SECRET");
        var destination = Path.Combine(temp.Path, "allowlist.zip");
        await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [directory]));
        using var archive = ZipFile.OpenRead(destination);
        var id = Path.GetFileName(directory);
        Assert.Equal(new[] { "classroom-checklist.md", "environment.json", "manifest.json",
                $"sessions/{id}/events-0001.jsonl", $"sessions/{id}/events.jsonl" }.Order(),
            archive.Entries.Select(entry => entry.FullName).Order());
    }

    [Fact]
    public async Task ForeignActiveSessionIsRefusedBeforeArchiveCreation()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var directory = temp.History(active: true);
        var destination = Path.Combine(temp.Path, "foreign.zip");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [directory])));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        Assert.Empty(Directory.GetFiles(directory, ".lease-*"));
    }

    internal static async Task<JsonNode> ReadManifest(ZipArchive archive)
    {
        using var stream = archive.GetEntry("manifest.json")!.Open();
        return (await JsonNode.ParseAsync(stream))!;
    }

    [Fact]
    public async Task EarlyCancellationPreservesCancellationWhenDestinationParentDoesNotExist()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var directory = temp.History();
        var destination = Path.Combine(temp.Path, "not-created", "cancelled.zip");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [directory]), cancellation.Token));

        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
        Assert.Empty(Directory.GetFiles(directory, ".lease-*"));
    }

    internal static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        return bytes.ToArray();
    }
}

internal sealed class DiagnosticTestDirectory(ITestOutputHelper output) : IDisposable
{
    public string Path { get; } = Create(output);
    private static string Create(ITestOutputHelper output)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cbb-task10-attempt3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        output.WriteLine("CREATED " + path);
        return path;
    }

    public string History(bool active = false)
    {
        var id = Guid.NewGuid();
        var path = System.IO.Path.Combine(Path, id.ToString("N"));
        Directory.CreateDirectory(path);
        File.WriteAllText(System.IO.Path.Combine(path, "session.json"), new JsonObject
        {
            ["sessionId"] = id, ["active"] = active, ["processId"] = Environment.ProcessId,
            ["lastActivityUtc"] = DateTimeOffset.UtcNow.AddDays(-30), ["pinned"] = false,
            ["incomplete"] = false, ["unknownTail"] = false
        }.ToJsonString());
        File.WriteAllText(System.IO.Path.Combine(path, "events-0001.jsonl"), "{\"sequence\":1,\"name\":\"history\"}\n");
        return path;
    }

    public void Dispose()
    {
        var files = Directory.GetFiles(Path, "*", SearchOption.AllDirectories);
        output.WriteLine("CLEANUP " + Path + " FILES " + string.Join(", ", files.Select(file => System.IO.Path.GetRelativePath(Path, file))));
        Directory.Delete(Path, true);
        output.WriteLine("REMOVED " + Path);
    }
}
