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
    public async Task FeedbackExportContainsOnlyAllowlistedFilesAndParseableManifest()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path });
        Assert.True(session.TryRecord(new DiagnosticEvent("first", Data: new Dictionary<string, string> { ["value"] = "one" })));
        await session.FlushAsync();
        await session.DisposeAsync();
        var destination = Path.Combine(temp.Path, "owned", "feedback.zip");

        var result = await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(
            destination, [session.DirectoryPath], "打开并书写", "出现笔迹", "笔迹消失"));

        Assert.Equal(destination, result.DestinationPath);
        Assert.True(File.Exists(destination));
        using var archive = ZipFile.OpenRead(destination);
        var names = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name).ToArray();
        Assert.Contains("manifest.json", names);
        Assert.Contains("environment.json", names);
        Assert.Contains("classroom-checklist.md", names);
        Assert.Contains(names, name => name.EndsWith("/events.jsonl", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("screenshot", StringComparison.OrdinalIgnoreCase)
            || name.Contains("workspace", StringComparison.OrdinalIgnoreCase)
            || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || name.Contains("envvar", StringComparison.OrdinalIgnoreCase));
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        var sessionManifest = manifest.RootElement.GetProperty("sessions")[0];
        Assert.Equal(3, sessionManifest.GetProperty("cutoffSequence").GetInt64());
        Assert.False(sessionManifest.GetProperty("incomplete").GetBoolean());
        var events = archive.Entries.Single(entry => entry.FullName.EndsWith("/events.jsonl", StringComparison.Ordinal));
        using var reader = new StreamReader(events.Open());
        var eventLines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { 1L, 2L, 3L }, eventLines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("sequence").GetInt64()));
    }

    [Fact]
    public async Task CancelledFeedbackExportLeavesNoDestinationOrTemporaryArchive()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path });
        Assert.True(session.TryRecord(new DiagnosticEvent("event")));
        await session.DisposeAsync();
        var destination = Path.Combine(temp.Path, "cancelled.zip");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [session.DirectoryPath]), cancellation.Token));

        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LiveExportUsesWriterBarrierAndExcludesLaterSequence()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path });
        Assert.True(session.TryRecord(new DiagnosticEvent("before-export")));
        var destination = Path.Combine(temp.Path, "live.zip");

        await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [session.DirectoryPath]));
        Assert.True(session.TryRecord(new DiagnosticEvent("after-export")));
        await session.DisposeAsync();

        using var archive = ZipFile.OpenRead(destination);
        var events = archive.Entries.Single(entry => entry.FullName.EndsWith("/events.jsonl", StringComparison.Ordinal));
        using var reader = new StreamReader(events.Open());
        var names = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("name").GetString()).ToArray();
        Assert.Contains("before-export", names);
        Assert.DoesNotContain("after-export", names);
    }

    [Fact]
    public async Task ExportManifestMatchesWholeRecordBytesAtWriterOrderedCutoff()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path });
        Assert.True(session.TryRecord(new DiagnosticEvent("before-export")));
        await session.FlushAsync();

        var destination = Path.Combine(temp.Path, "exact.zip");
        await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [session.DirectoryPath]));

        using var archive = ZipFile.OpenRead(destination);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        var file = manifest.RootElement.GetProperty("sessions")[0].GetProperty("files")[0];
        var archiveEntry = archive.Entries.Single(entry => entry.FullName.EndsWith("/events-0001.jsonl", StringComparison.Ordinal));
        using var archiveBytes = new MemoryStream();
        archiveEntry.Open().CopyTo(archiveBytes);

        Assert.Equal(archiveBytes.Length, file.GetProperty("cutoffBytes").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(archiveBytes.ToArray())), file.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task HistoricalMalformedTailMakesPublishedManifestStickyIncomplete()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var directory = CreateSession(temp.Path, "historical-malformed", DateTimeOffset.UtcNow.AddDays(-2), active: false);
        File.WriteAllText(Path.Combine(directory, "events-0001.jsonl"), "{\"sequence\":1}\n{\"sequence\":");

        var destination = Path.Combine(temp.Path, "malformed.zip");
        var result = await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [directory]));

        Assert.True(result.Incomplete);
        using var archive = ZipFile.OpenRead(destination);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Assert.True(manifest.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.True(manifest.RootElement.GetProperty("sessions")[0].GetProperty("incomplete").GetBoolean());
    }

    [Fact]
    public async Task MixedHistoricalProvenanceRemainsPerSessionAndEnvironmentBecomesUnknown()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var first = CreateSession(temp.Path, "history-one", DateTimeOffset.UtcNow.AddDays(-2), active: false);
        var second = CreateSession(temp.Path, "history-two", DateTimeOffset.UtcNow.AddDays(-1), active: false);
        WriteHistoricalRecord(first, "OS-one", "0.1.0-test.1", "checklist-one");
        WriteHistoricalRecord(second, "OS-two", "0.1.0-test.2", "checklist-two");

        var destination = Path.Combine(temp.Path, "mixed.zip");
        await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [first, second]));

        using var archive = ZipFile.OpenRead(destination);
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        var sessions = manifest.RootElement.GetProperty("sessions");
        Assert.Equal("OS-one", sessions[0].GetProperty("operatingSystem").GetString());
        Assert.Equal("OS-two", sessions[1].GetProperty("operatingSystem").GetString());
        using var environment = JsonDocument.Parse(archive.GetEntry("environment.json")!.Open());
        Assert.Equal("unknown", environment.RootElement.GetProperty("operatingSystem").GetString());
        var checklist = new StreamReader(archive.GetEntry("classroom-checklist.md")!.Open()).ReadToEnd();
        Assert.Contains("checklist-one", checklist);
        Assert.Contains("checklist-two", checklist);
    }

}
