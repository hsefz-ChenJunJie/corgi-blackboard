using System.Text.Json.Nodes;
using CorgiBlackboard.Core.Diagnostics;
using CorgiBlackboard.Infrastructure.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CorgiBlackboard.Infrastructure.Tests;

public sealed class DiagnosticLifecycleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CompetingForeignLeaseCannotEnterBetweenDeletionCheckAndDelete()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var directory = temp.History();
        var checkedProtection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new DiagnosticLogManager(temp.Path)
        {
            BeforeDelete = () =>
            {
                checkedProtection.TrySetResult();
                releaseDelete.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            }
        };
        var deleting = Task.Run(() => manager.TryDeleteSession(directory, out _));
        try
        {
            await checkedProtection.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var child = new DiagnosticChild("lease", directory);
            output.WriteLine("OWNED_PID " + child.Process.Id);
            Assert.Equal("contended", await child.ReadLine());
            releaseDelete.TrySetResult();
            Assert.True(await deleting.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("missing", await child.ReadLine());
            Assert.Equal(2, await child.Exit());
            output.WriteLine("EXITED_PID " + child.Process.Id + " CODE 2");
        }
        finally
        {
            releaseDelete.TrySetResult();
            await deleting.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ForeignLeaseBlocksAnotherManagersDeletionUntilRelease()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var directory = temp.History();
        await using var child = new DiagnosticChild("lease", directory);
        output.WriteLine("OWNED_PID " + child.Process.Id);
        Assert.Equal("leased", await child.ReadLine());
        var manager = new DiagnosticLogManager(temp.Path);
        Assert.False(manager.TryDeleteSession(directory, out _));
        Assert.Empty(manager.ApplyRetention(1, 1).DeletedSessions);
        await child.Process.StandardInput.WriteLineAsync("release");
        Assert.Equal(0, await child.Exit());
        output.WriteLine("EXITED_PID " + child.Process.Id + " CODE 0");
        Assert.True(manager.TryDeleteSession(directory, out _));
    }

    [Fact]
    public async Task ForeignLiveSessionRefusesExportAndPinWithoutChangingLogs()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var child = new DiagnosticChild("session", temp.Path);
        output.WriteLine("OWNED_PID " + child.Process.Id);
        var directory = (await child.ReadLine())!;
        Assert.False(DiagnosticSession.TryGetLive(directory, out _));
        var before = ReadShared(Path.Combine(directory, "events-0001.jsonl"));
        var manager = new DiagnosticLogManager(temp.Path);
        Assert.False(manager.SetPinned(directory, true, out _));
        Assert.False(manager.TryDeleteSession(directory, out _));
        Assert.Empty(manager.RecoverAbandonedSessions());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FeedbackExporter().ExportAsync(new FeedbackExportRequest(Path.Combine(temp.Path, "refused.zip"), [directory])));
        Assert.Equal(before, ReadShared(Path.Combine(directory, "events-0001.jsonl")));
        Assert.Empty(Directory.GetFiles(directory, ".lease-*"));
        await child.Process.StandardInput.WriteLineAsync("close");
        Assert.Equal(0, await child.Exit());
        output.WriteLine("EXITED_PID " + child.Process.Id + " CODE 0");
        var result = await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(Path.Combine(temp.Path, "closed.zip"), [directory]));
        Assert.False(result.Incomplete);
    }

    [Fact]
    public async Task ShutdownWaitsForQueuedRecordsAndPersistsExactlyOneEndMarker()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var session = new DiagnosticSession(new DiagnosticOptions
        {
            RootDirectory = temp.Path,
            BeforeWriteAsync = async _ =>
            {
                if (Interlocked.Increment(ref count) != 1) return;
                entered.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        });
        Task? shutdown = null;
        try
        {
            Assert.True(session.TryRecord(new DiagnosticEvent("first")));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(session.TryRecord(new DiagnosticEvent("queued")));
            shutdown = DiagnosticSessionShutdown.ShutdownAsync(session);
            Assert.False(shutdown.IsCompleted);
            Assert.False(session.TryRecord(new DiagnosticEvent("after-close")));
            Assert.True(File.Exists(Path.Combine(session.DirectoryPath, ".active.lock")));
        }
        finally
        {
            release.TrySetResult();
            await (shutdown ?? DiagnosticSessionShutdown.ShutdownAsync(session)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await DiagnosticSessionShutdown.ShutdownAsync(session);
        var records = Directory.GetFiles(session.DirectoryPath, "events-*.jsonl")
            .SelectMany(File.ReadAllLines).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(new[] { "first", "queued", "session-end" }, records.Select(record => record["name"]!.GetValue<string>()));
        Assert.Equal(new long[] { 1, 2, 3 }, records.Select(record => record["sequence"]!.GetValue<long>()));
        Assert.False(File.Exists(Path.Combine(session.DirectoryPath, ".active.lock")));
        Assert.False(JsonNode.Parse(File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")))!["active"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e300")]
    [InlineData("8796093022208")]
    [InlineData("0")]
    [InlineData("-1")]
    public void InvalidRetentionSizeIsRejectedWithoutConversionException(string megabytes) =>
        Assert.False(DiagnosticRetentionPolicy.TryParse("14", megabytes, out _));

    [Fact]
    public void RetentionBoundaryConvertsMegabytesExactly()
    {
        Assert.True(DiagnosticRetentionPolicy.TryParse("14", "2048", out var policy));
        Assert.Equal(2147483648L, policy.MaximumBytes);
        Assert.False(DiagnosticRetentionPolicy.TryParse("2147483647", "1", out _));
    }

    private static byte[] ReadShared(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        return bytes.ToArray();
    }
}
