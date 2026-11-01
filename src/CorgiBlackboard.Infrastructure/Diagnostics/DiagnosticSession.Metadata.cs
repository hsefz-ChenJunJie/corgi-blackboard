using System.Text.Json;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

public sealed partial class DiagnosticSession
{
    private MetadataSnapshot Metadata(bool incomplete, bool? pinned = null, bool? active = null)
    {
        lock (_gate)
        {
            var revision = ++_metadataRevision;
            return new MetadataSnapshot(revision, new
            {
                schemaVersion = SchemaVersion, metadataRevision = revision, sessionId = Id, channel = Channel, buildVersion = _buildIdentity.BuildVersion,
                gitHash = _buildIdentity.GitHash, processId = Environment.ProcessId, startedUtc = _sessionStartedUtc,
                lastActivityUtc = _lastActivityUtc, operatingSystem = DiagnosticRuntimeIdentity.OperatingSystem,
                dpi = DiagnosticRuntimeIdentity.Dpi, deviceId = DiagnosticRuntimeIdentity.DeviceId,
                architecture = DiagnosticRuntimeIdentity.Architecture, runtime = Environment.Version.ToString(), checklist = _checklistText,
                incomplete = incomplete || _incomplete, active = active ?? !_closed, pinned = pinned ?? _pinned,
                knownGaps = _gaps.Select(gap => new { start = gap.Start, end = gap.End, reason = gap.Reason }).ToArray(),
                unknownTail = _unknownTail
            });
        }
    }

    private void SetDegraded(string message)
    {
        if (_degraded) return;
        _degraded = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Degraded?.Invoke(message); } catch { }
        });
    }

    private void AddGapLocked(long sequence, string reason, bool unknownTail, string degradedMessage)
    {
        _gapRevision++;
        _incomplete = true;
        _unknownTail |= unknownTail;
        var start = sequence;
        var end = sequence;
        var reasons = new HashSet<string>(StringComparer.Ordinal) { reason };
        var insertAt = _gaps.Count;
        for (var i = 0; i < _gaps.Count;)
        {
            var existing = _gaps[i];
            if (existing.End < start - 1) { i++; continue; }
            if (end < existing.Start - 1) { insertAt = i; break; }
            start = Math.Min(start, existing.Start);
            end = Math.Max(end, existing.End);
            foreach (var existingReason in existing.Reason.Split(';', StringSplitOptions.RemoveEmptyEntries))
                reasons.Add(existingReason);
            _gaps.RemoveAt(i);
            insertAt = Math.Min(insertAt, i);
        }
        if (_gaps.Count < MaximumGapRanges)
            _gaps.Insert(Math.Min(insertAt, _gaps.Count), new GapRange(start, end, string.Join(';', reasons.Order(StringComparer.Ordinal))));
        else
            _unknownTail = true;
        SetDegraded(degradedMessage);
        QueueGapPersistence();
    }

    private void QueueGapPersistence()
    {
        if (_gapPersistQueued) return;
        _gapPersistQueued = true;
        _gapPersistTask = Task.Run(() =>
        {
            while (true)
            {
                MetadataSnapshot snapshot;
                long gapRevision;
                lock (_gate)
                {
                    gapRevision = _gapRevision;
                    snapshot = Metadata(true);
                }
                try { PersistMetadata(snapshot); }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _unknownTail = true;
                        SetDegraded("Diagnostics degraded: gap metadata failure: " + ex.GetType().Name);
                        _gapPersistQueued = false;
                    }
                    return;
                }
                lock (_gate)
                {
                    if (gapRevision == _gapRevision)
                    {
                        _gapPersistQueued = false;
                        return;
                    }
                }
            }
        });
    }

    private void PersistMetadata(MetadataSnapshot snapshot)
    {
        lock (_metadataGate)
        {
            if (snapshot.Revision < _persistedMetadataRevision) return;
            WriteMetadata("session.json", snapshot.Value);
            _persistedMetadataRevision = snapshot.Revision;
        }
    }

    private void WriteMetadata(string name, object value)
    {
        lock (_metadataGate)
        {
            var target = Path.Combine(DirectoryPath, name);
            var temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temp, target, true);
        }
    }

    private sealed record GapRange(long Start, long End, string Reason);
    private sealed record MetadataSnapshot(long Revision, object Value);
}
