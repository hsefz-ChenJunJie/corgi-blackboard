using System.Text.Json;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

public sealed partial class DiagnosticSession
{
    private async Task WriterLoop()
    {
        await foreach (var record in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (_options.BeforeWriteAsync is not null) await _options.BeforeWriteAsync(CancellationToken.None).ConfigureAwait(false);
                var json = JsonSerializer.Serialize(record, JsonOptions) + "\n";
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                if (bytes.Length > _options.MaximumFileBytes)
                {
                    lock (_gate) AddGapLocked(record.Sequence, "record-exceeds-file-limit", unknownTail: false, "Diagnostics degraded: record exceeds file limit");
                    if (_flushes.TryRemove(record.Sequence, out var oversized))
                        oversized.TrySetException(new IOException("诊断文件上限不足以写入快照屏障。"));
                    continue;
                }
                if (_stream is not null && _stream.Length + bytes.Length > _options.MaximumFileBytes) CloseFile();
                _stream ??= OpenFile();
                await _stream.WriteAsync(bytes).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
                _writtenFiles[_currentPath!] = _stream.Position;
                ApplyRetentionAfterWrite();
                if (record.Name == "flush")
                {
                    _stream.Flush(true);
                    DiagnosticSessionSnapshot snapshot;
                    lock (_gate) snapshot = CaptureCheckpointLocked(record.Sequence);
                    if (_flushes.TryRemove(record.Sequence, out var waiter)) waiter.TrySetResult(snapshot);
                }
                bool hasGaps;
                lock (_gate) hasGaps = _gaps.Count > 0;
                if (hasGaps && record.Name != "flush")
                {
                    PersistMetadata(Metadata(true));
                }
            }
            catch (Exception ex)
            {
                if (record.Name == "flush" && _flushes.TryRemove(record.Sequence, out var waiter)) waiter.TrySetException(ex);
                MetadataSnapshot snapshot;
                lock (_gate)
                {
                    AddGapLocked(record.Sequence, "sink-write-or-flush-failure", unknownTail: true,
                        "Diagnostics degraded: write/flush failure: " + ex.GetType().Name);
                    snapshot = Metadata(true);
                }
                try { PersistMetadata(snapshot); } catch { }
                try { _stream?.Dispose(); } catch { }
                _stream = null;
            }
        }
        try { _stream?.Flush(true); }
        catch (Exception ex)
        {
            MetadataSnapshot snapshot;
            lock (_gate)
            {
                _unknownTail = true;
                _incomplete = true;
                SetDegraded("Diagnostics degraded: final flush failure: " + ex.GetType().Name);
                snapshot = Metadata(true);
            }
            try { PersistMetadata(snapshot); } catch { }
        }
        CloseFile();
    }

    private FileStream OpenFile()
    {
        var path = Path.Combine(DirectoryPath, $"events-{++_fileIndex:D4}.jsonl");
        _currentPath = path;
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private void CloseFile()
    {
        _stream?.Flush(true);
        _stream?.Dispose();
        _stream = null;
        _currentPath = null;
    }

    private void ApplyRetentionAfterWrite()
    {
        try
        {
            Retention = _manager.ApplyRetention();
            RetentionApplied?.Invoke(Retention);
        }
        catch (IOException ex) { SetDegraded("Diagnostics degraded: retention failure: " + ex.GetType().Name); }
        catch (UnauthorizedAccessException ex) { SetDegraded("Diagnostics degraded: retention failure: " + ex.GetType().Name); }
    }
}
