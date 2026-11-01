using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using CorgiBlackboard.Core.Diagnostics;
using CorgiBlackboard.Infrastructure.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CorgiBlackboard.Infrastructure.Tests;

public sealed class FeedbackConcurrencyTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PausedCopyPreservesEveryRotatedFileWhileWriterAndRetentionProceed()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var live = new DiagnosticSession(new DiagnosticOptions
        {
            RootDirectory = temp.Path, MaximumFileBytes = 1800
        }, () => new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero), () => 17);
        var history = temp.History();
        using var historyPreparation = new DiagnosticLogManager(temp.Path).Lease(history);
        for (var index = 0; index < 6; index++)
            Assert.True(live.TryRecord(new DiagnosticEvent("before-" + index)));
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var destination = Path.Combine(temp.Path, "rotated.zip");
        var exporting = new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [history, live.DirectoryPath],
            CopyProgressAsync: async (_, token) =>
            {
                paused.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            }));
        var expected = new Dictionary<string, byte[]>();
        try
        {
            await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var file in Directory.GetFiles(live.DirectoryPath, "events-*.jsonl"))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                expected.Add(Path.GetFileName(file), bytes.ToArray());
            }
            Assert.True(expected.Count > 1);
            Assert.Equal(7L, live.Sequence);
            for (var index = 0; index < 12; index++)
                Assert.True(live.TryRecord(new DiagnosticEvent("later-" + index)));
            await live.FlushAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(20L, live.Sequence);
            Assert.True(Directory.GetFiles(live.DirectoryPath, "events-*.jsonl").Length > expected.Count);
            historyPreparation.Dispose();
            var eligible = temp.History();
            var manager = new DiagnosticLogManager(temp.Path);
            var retention = manager.ApplyRetention(14, 1);
            Assert.Contains(eligible, retention.DeletedSessions);
            Assert.DoesNotContain(history, retention.DeletedSessions);
            Assert.DoesNotContain(live.DirectoryPath, retention.DeletedSessions);
            Assert.Equal("export-leased", retention.ProtectionReasons[history]);
            Assert.Equal("active", retention.ProtectionReasons[live.DirectoryPath]);
            Assert.True(retention.ExcessBytes > 0);
            Assert.False(manager.TryDeleteSession(history, out _));
            Assert.False(File.Exists(destination));
            Assert.Single(Directory.GetFiles(temp.Path, "*.tmp"));
        }
        finally
        {
            release.TrySetResult();
            await exporting.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var result = await exporting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.Incomplete);
        using var archive = ZipFile.OpenRead(destination);
        var manifest = await FeedbackBoundaryTests.ReadManifest(archive);
        var session = manifest["sessions"]!.AsArray().Single(node => node!["sessionId"]!.GetValue<Guid>() == live.Id)!;
        Assert.Equal(7L, session["cutoffSequence"]!.GetValue<long>());
        Assert.Equal(expected.Count, session["files"]!.AsArray().Count);
        foreach (var file in session["files"]!.AsArray())
        {
            var name = file!["name"]!.GetValue<string>();
            var bytes = expected[name];
            var actual = FeedbackBoundaryTests.ReadBytes(archive.GetEntry($"sessions/{live.Id:N}/{name}")!);
            Assert.Equal(bytes, actual);
            Assert.Equal(bytes.LongLength, file["cutoffBytes"]!.GetValue<long>());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), file["sha256"]!.GetValue<string>());
        }
        var combined = FeedbackBoundaryTests.ReadBytes(archive.GetEntry($"sessions/{live.Id:N}/events.jsonl")!);
        Assert.Equal(expected.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value).ToArray(), combined);
        output.WriteLine("MANUAL_ARCHIVE_MANIFEST " + manifest.ToJsonString());
    }

    [Fact]
    public async Task CancellationAfterCopiedBytesRemovesTemporaryArchiveAndReleasesLeaseForRetry()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var directory = temp.History();
        var log = Path.Combine(directory, "events-0001.jsonl");
        File.WriteAllText(log, new JsonObject { ["sequence"] = 1, ["payload"] = new string('x', 200000) }.ToJsonString() + "\n");
        var original = File.ReadAllBytes(log);
        var destination = Path.Combine(temp.Path, "cancel.zip");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var copied = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var exporting = new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [directory],
                BytesCopiedAsync: async (bytes, token) =>
                {
                    copied.TrySetResult(bytes);
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                }), cancellation.Token);
            try
            {
                Assert.InRange(await copied.Task.WaitAsync(TimeSpan.FromSeconds(10)), 1, original.Length - 1);
                Assert.Single(Directory.GetFiles(temp.Path, "*.tmp"));
                Assert.False(new DiagnosticLogManager(temp.Path).TryDeleteSession(directory, out _));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporting.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                cancellation.Cancel();
                release.TrySetResult();
                try { await exporting.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
            }
            Assert.False(File.Exists(destination));
            Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
            Assert.Empty(Directory.GetFiles(directory, ".lease-*"));
            Assert.Equal(original, File.ReadAllBytes(log));
        }
        Assert.False((await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [directory]))).Incomplete);
        Assert.True(new DiagnosticLogManager(temp.Path).TryDeleteSession(directory, out _));
    }

    [Fact]
    public async Task PublicationRefusalPreservesDestinationAndLogsThenAllowsRetry()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var directory = temp.History();
        var source = File.ReadAllBytes(Path.Combine(directory, "events-0001.jsonl"));
        var destination = Path.Combine(temp.Path, "locked.zip");
        File.WriteAllBytes(destination, [1, 2, 3, 4]);
        using (var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [directory])));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(destination));
        }
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
        Assert.Empty(Directory.GetFiles(directory, ".lease-*"));
        Assert.Equal(source, File.ReadAllBytes(Path.Combine(directory, "events-0001.jsonl")));
        await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(destination, [directory]));
        using var archive = ZipFile.OpenRead(destination);
        Assert.NotNull(archive.GetEntry("manifest.json"));
    }

    [Fact]
    public async Task WriterGrowthAppliesChangedPolicyWithoutResettingSession()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions { RootDirectory = temp.Path });
        var old = temp.History();
        new DiagnosticLogManager(temp.Path).SavePolicy(new DiagnosticRetentionPolicy(36500, 1));
        var applied = new TaskCompletionSource<DiagnosticRetentionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.RetentionApplied += result => applied.TrySetResult(result);
        Assert.True(session.TryRecord(new DiagnosticEvent("growth")));
        var result = await applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(old, result.DeletedSessions);
        Assert.True(result.ExcessBytes > 0);
        Assert.Equal("active", result.ProtectionReasons[session.DirectoryPath]);
        Assert.Equal(1L, session.Sequence);
        Assert.True(session.TryRecord(new DiagnosticEvent("after-policy")));
    }
}
