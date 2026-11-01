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
    public async Task QueueFullCreatesStickyGapAndDegradedStatus()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeCount = 0;
        await using var session = new DiagnosticSession(new DiagnosticOptions
        {
            RootDirectory = temp.Path,
            QueueCapacity = 1,
            BeforeWriteAsync = async _ =>
            {
                var count = Interlocked.Increment(ref writeCount);
                if (count == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                else if (count == 2)
                {
                    throw new IOException("injected writer failure after accepted history");
                }
            }
        });
        Task? close = null;
        try
        {
            Assert.True(session.TryRecord(new DiagnosticEvent("first")));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.TryRecord(new DiagnosticEvent("queued")));
            var callReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rejected = Task.Run(() => callReturned.TrySetResult(session.TryRecord(new DiagnosticEvent("rejected"))));
            await callReturned.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(await rejected);
            Assert.False(await callReturned.Task);
            Assert.True(session.IsDegraded);
            Assert.True(session.IsIncomplete);
            close = session.DisposeAsync().AsTask();
            Assert.False(close.IsCompleted);
            Assert.True(File.Exists(Path.Combine(session.DirectoryPath, ".active.lock")));
        }
        finally
        {
            release.TrySetResult();
            if (close is null) await session.DisposeAsync();
            else await close.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(File.Exists(Path.Combine(session.DirectoryPath, ".active.lock")));
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")));
        Assert.True(metadata.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("unknownTail").GetBoolean());
        var gaps = metadata.RootElement.GetProperty("knownGaps");
        Assert.Single(gaps.EnumerateArray());
        Assert.Equal(2, gaps[0].GetProperty("start").GetInt64());
        Assert.Equal(3, gaps[0].GetProperty("end").GetInt64());
        Assert.Contains("queue-full", gaps[0].GetProperty("reason").GetString());
        Assert.Contains("sink-write-or-flush-failure", gaps[0].GetProperty("reason").GetString());
        var records = ReadRecords(session.DirectoryPath);
        Assert.Contains(records, r => r.GetProperty("name").GetString() == "first");
        Assert.Equal("session-end", records[^1].GetProperty("name").GetString());
        Assert.Equal(new long[] { 1, 4 }, records.Select(r => r.GetProperty("sequence").GetInt64()));
    }

    [Fact]
    public async Task CancelledFlushReservationPersistsGapAndCloseMarker()
    {
        using var temp = new DiagnosticTestDirectory(output);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        await using var session = new DiagnosticSession(new DiagnosticOptions
        {
            RootDirectory = temp.Path,
            QueueCapacity = 1,
            BeforeWriteAsync = async _ =>
            {
                if (Interlocked.Increment(ref call) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            }
        });
        Task? close = null;
        try
        {
            Assert.True(session.TryRecord(new DiagnosticEvent("first")));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.TryRecord(new DiagnosticEvent("queued")));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.FlushAsync(cancellation.Token));
            close = session.DisposeAsync().AsTask();
            Assert.False(close.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            if (close is null) await session.DisposeAsync();
            else await close.WaitAsync(TimeSpan.FromSeconds(5));
        }
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")));
        Assert.Equal(3, metadata.RootElement.GetProperty("knownGaps")[0].GetProperty("start").GetInt64());
        Assert.Equal(3, metadata.RootElement.GetProperty("knownGaps")[0].GetProperty("end").GetInt64());
        Assert.Equal("session-end", ReadRecords(session.DirectoryPath)[^1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task WriterFailureAfterFlushedHistoryPreservesHistoryAndRecordsGap()
    {
        using var temp = new DiagnosticTestDirectory(output);
        await using var session = new DiagnosticSession(new DiagnosticOptions
        {
            RootDirectory = temp.Path,
            BeforeWriteAsync = _ => Interlocked.Increment(ref _writeCount) == 3
                ? ValueTask.FromException(new IOException("simulated writer refusal"))
                : ValueTask.CompletedTask
        });
        var degraded = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Degraded += message => degraded.TrySetResult(message);
        Assert.True(session.TryRecord(new DiagnosticEvent("history")));
        await session.FlushAsync();
        Assert.True(session.TryRecord(new DiagnosticEvent("will-fail")));
        Assert.Contains("Diagnostics degraded", await degraded.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await session.DisposeAsync();
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.DirectoryPath, "session.json")));
        Assert.True(metadata.RootElement.GetProperty("incomplete").GetBoolean());
        Assert.True(metadata.RootElement.GetProperty("unknownTail").GetBoolean());
        Assert.Contains(ReadRecords(session.DirectoryPath), r => r.GetProperty("name").GetString() == "history");
        Assert.DoesNotContain(ReadRecords(session.DirectoryPath), r => r.GetProperty("name").GetString() == "will-fail");
        Assert.Equal(3, metadata.RootElement.GetProperty("knownGaps")[0].GetProperty("start").GetInt64());
        Assert.Equal(3, metadata.RootElement.GetProperty("knownGaps")[0].GetProperty("end").GetInt64());
    }

}
