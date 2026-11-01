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
    public async Task PausedCopyAllowsWriterProgressButExportsOnlyCheckpointBytes()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions
        {
            RootDirectory = temp.Path,
            MaximumFileBytes = 900
        });
        Assert.True(session.TryRecord(new DiagnosticEvent("before-export", Data: new Dictionary<string, string> { ["payload"] = "before" })));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var destination = Path.Combine(temp.Path, "paused.zip");
        var export = new FeedbackExporter().ExportAsync(new FeedbackExportRequest(
            destination,
            [session.DirectoryPath],
            CopyProgressAsync: async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.TryRecord(new DiagnosticEvent("after-export", Data: new Dictionary<string, string> { ["payload"] = "after" })));
            await session.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
            await export.WaitAsync(TimeSpan.FromSeconds(10));
        }
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
    public async Task MidCopyCancellationReleasesLeaseAndAllowsRetry()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var directory = CreateSession(temp.Path, "cancel-retry", DateTimeOffset.UtcNow.AddDays(-2), active: false);
        WriteHistoricalRecord(directory, "OS-one", "0.1.0-test.1", "checklist-one");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var destination = Path.Combine(temp.Path, "cancel-retry.zip");
        using var cancellation = new CancellationTokenSource();
        var export = new FeedbackExporter().ExportAsync(new FeedbackExportRequest(
            destination,
            [directory],
            CopyProgressAsync: async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var manager = new DiagnosticLogManager(temp.Path);
            Assert.False(manager.TryDeleteSession(directory, out _));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            try { await export.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.EnumerateFiles(directory, ".lease-*"));

        await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [directory]));
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public void RetentionPolicyPersistsSizePinAndProtectedOverage()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var manager = new DiagnosticLogManager(temp.Path, () => new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));
        var old = CreateSession(temp.Path, "policy-old", DateTimeOffset.UtcNow.AddDays(-30), active: false);
        File.WriteAllText(Path.Combine(old, "large"), new string('x', 4096));
        Assert.True(manager.SetPinned(old, true, out _));
        manager.SavePolicy(new DiagnosticRetentionPolicy(14, 1));

        var result = manager.ApplyRetention();

        Assert.True(File.Exists(Path.Combine(temp.Path, "retention.json")));
        Assert.Empty(result.DeletedSessions);
        Assert.Contains(old, result.ProtectedOverage.Keys);
        Assert.Equal(1, manager.LoadPolicy().MaximumBytes);
        Assert.True(manager.SetPinned(old, false, out _));
        Assert.Contains(old, manager.ApplyRetention().DeletedSessions);
    }

    [Fact]
    public void DeletionRechecksAuthoritativeActiveMarkerAfterRowsWereLoaded()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var directory = CreateSession(temp.Path, "stale-row", DateTimeOffset.UtcNow.AddDays(-30), active: false);
        File.WriteAllText(Path.Combine(directory, ".active.lock"), Environment.ProcessId.ToString());

        var manager = new DiagnosticLogManager(temp.Path);

        Assert.False(manager.TryDeleteSession(directory, out _));
        Assert.True(Directory.Exists(directory));
    }

}
