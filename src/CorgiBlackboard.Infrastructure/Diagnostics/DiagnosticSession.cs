using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using CorgiBlackboard.Core.Diagnostics;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

public sealed partial class DiagnosticSession : IAsyncDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DiagnosticSession> LiveSessions = new(StringComparer.OrdinalIgnoreCase);
    private const int SchemaVersion = 1;
    private const int MaximumGapRanges = 256;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly DiagnosticOptions _options;
    private readonly DiagnosticBuildIdentity _buildIdentity;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<long> _ticks;
    private readonly Channel<DiagnosticSessionRecord> _queue;
    private readonly Task _writer;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<DiagnosticSessionSnapshot>> _flushes = new();
    private readonly object _gate = new();
    private readonly object _metadataGate = new();
    private readonly List<GapRange> _gaps = [];
    private long _sequence;
    private bool _incomplete;
    private bool _degraded;
    private bool _unknownTail;
    private bool _closed;
    private bool _gapPersistQueued;
    private Task? _gapPersistTask;
    private Task? _disposeTask;
    private bool _pinned;
    private long _metadataRevision;
    private long _persistedMetadataRevision = -1;
    private long _gapRevision;
    private FileStream? _stream;
    private string? _currentPath;
    private string? _activeMarker;
    private readonly Dictionary<string, long> _writtenFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly DiagnosticLogManager _manager;
    private readonly string _checklistText;
    private int _fileIndex;
    private readonly Stopwatch _monotonic = Stopwatch.StartNew();

    public Guid Id { get; } = Guid.NewGuid();
    public string DirectoryPath { get; }
    public bool IsIncomplete { get { lock (_gate) return _incomplete; } }
    public bool IsDegraded { get { lock (_gate) return _degraded; } }
    public bool IsPinned { get { lock (_gate) return _pinned; } }
    public long Sequence => Interlocked.Read(ref _sequence);
    public string Channel => _buildIdentity.ReleaseChannel;
    public string BuildVersion => _buildIdentity.BuildVersion;
    public string ChecklistText => _checklistText;
    public event Action<string>? Degraded;
    public event Action<DiagnosticRetentionResult>? RetentionApplied;
    public DiagnosticRetentionResult? Retention { get; private set; }

    public Task<DiagnosticSessionSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default) =>
        QueueCheckpointAsync(cancellationToken);

    public DiagnosticSession(DiagnosticOptions? options = null, Func<DateTimeOffset>? utcNow = null, Func<long>? monotonicTicks = null)
    {
        _options = options ?? new();
        if (_options.QueueCapacity < 1 || _options.MaximumFileBytes < 1) throw new ArgumentOutOfRangeException(nameof(options));
        _buildIdentity = DiagnosticBuildIdentity.FromAssembly(typeof(DiagnosticSession).Assembly);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _ticks = monotonicTicks ?? (() => _monotonic.ElapsedTicks);
        _checklistText = _options.ChecklistText ?? DiagnosticChecklist.Read();
        _sessionStartedUtc = _lastActivityUtc = _utcNow();
        var root = _options.RootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CorgiBlackboard", "Logs");
        _manager = new DiagnosticLogManager(root, _utcNow);
        _manager.RecoverAbandonedSessions();
        Retention = _manager.ApplyRetention();
        using var initialization = new DiagnosticInitialization(root, Id);
        DirectoryPath = initialization.DirectoryPath;
        var activeMarker = Path.Combine(DirectoryPath, ".active.lock");
        File.WriteAllText(activeMarker, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteMetadata("session.json", new
        {
            schemaVersion = SchemaVersion, sessionId = Id, initializationClaim = Id,
            channel = Channel, buildVersion = _buildIdentity.BuildVersion,
            gitHash = _buildIdentity.GitHash, processId = Environment.ProcessId, startedUtc = _sessionStartedUtc,
            lastActivityUtc = _lastActivityUtc, operatingSystem = DiagnosticRuntimeIdentity.OperatingSystem,
            dpi = DiagnosticRuntimeIdentity.Dpi, deviceId = DiagnosticRuntimeIdentity.DeviceId,
            architecture = DiagnosticRuntimeIdentity.Architecture, runtime = Environment.Version.ToString(), checklist = _checklistText,
            incomplete = false, active = true, pinned = false, unknownTail = false, knownGaps = Array.Empty<object>()
        });
        initialization.Complete();
        _queue = System.Threading.Channels.Channel.CreateBounded<DiagnosticSessionRecord>(new BoundedChannelOptions(_options.QueueCapacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        _writer = Task.Run(WriterLoop);
        _activeMarker = activeMarker;
        LiveSessions[Path.GetFullPath(DirectoryPath)] = this;
    }

    public bool TryRecord(DiagnosticEvent diagnosticEvent)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        if (Channel == "Stable" && (diagnosticEvent.Severity is not (DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            || diagnosticEvent.Kind is DiagnosticKind.PointerSample or DiagnosticKind.PointerTransition))
            return true;
        lock (_gate)
        {
            if (_closed) return false;
            _lastActivityUtc = _utcNow();
            var sequence = ++_sequence;
            var record = new DiagnosticSessionRecord(SchemaVersion, Id, sequence, _utcNow(), _ticks(),
                _buildIdentity.BuildVersion, Channel, _buildIdentity.GitHash,
                DiagnosticRuntimeIdentity.OperatingSystem, DiagnosticRuntimeIdentity.Dpi,
                DiagnosticRuntimeIdentity.DeviceId, diagnosticEvent.Name, diagnosticEvent.Kind, diagnosticEvent.Severity,
                diagnosticEvent.OperationId, diagnosticEvent.SurfaceId, diagnosticEvent.WindowId,
                diagnosticEvent.DeviceId, diagnosticEvent.PointerSamples?.ToArray(),
                diagnosticEvent.Data?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                diagnosticEvent.Asset,
                diagnosticEvent.DurationMilliseconds, diagnosticEvent.Exception);
            if (_queue.Writer.TryWrite(record)) return true;
            AddGapLocked(sequence, "queue-full", unknownTail: false, "Diagnostics degraded: queue full");
            return false;
        }
    }

    public void Pin(bool pinned)
    {
        using var mutation = DiagnosticDirectoryAccess.Enter(DirectoryPath);
        MetadataSnapshot snapshot;
        lock (_gate)
        {
            _pinned = pinned;
            snapshot = Metadata(_incomplete);
        }
        PersistMetadata(snapshot);
    }

    public IDisposable AcquireExportLease() => DiagnosticDirectoryAccess.Lease(DirectoryPath);

    public async Task FlushAsync(CancellationToken cancellationToken = default) =>
        _ = await QueueCheckpointAsync(cancellationToken).ConfigureAwait(false);

    private async Task<DiagnosticSessionSnapshot> QueueCheckpointAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<DiagnosticSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        while (true)
        {
            var queued = false;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                var sequence = ++_sequence;
                var control = CreateControl("flush", sequence);
                _flushes[sequence] = completion;
                if (_queue.Writer.TryWrite(control))
                {
                    queued = true;
                }
                else
                {
                    _flushes.TryRemove(sequence, out _);
                    AddGapLocked(sequence, "flush-queue-full", unknownTail: false, "Diagnostics degraded: flush queue full");
                }
            }
            if (queued) break;
            if (!await _queue.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Diagnostics writer is closed.");
        }
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _closed = true;
            var seq = ++_sequence;
            _disposeTask = CloseSessionAsync(CreateControl("session-end", seq, DiagnosticKind.SessionEnd));
            return new ValueTask(_disposeTask);
        }
    }

    private async Task CloseSessionAsync(DiagnosticSessionRecord endRecord)
    {
        try
        {
            while (!_queue.Writer.TryWrite(endRecord))
            {
                if (!await _queue.Writer.WaitToWriteAsync().ConfigureAwait(false)) break;
            }
            _queue.Writer.TryComplete();
            await _writer.ConfigureAwait(false);
            Task? gapPersist;
            lock (_gate) gapPersist = _gapPersistTask;
            if (gapPersist is not null) await gapPersist.ConfigureAwait(false);
        }
        finally
        {
            using var mutation = DiagnosticDirectoryAccess.Enter(DirectoryPath);
            MetadataSnapshot snapshot;
            lock (_gate)
            {
                snapshot = Metadata(_incomplete, active: false);
            }
            try { PersistMetadata(snapshot); }
            catch (Exception ex)
            {
                lock (_gate) { _incomplete = true; _unknownTail = true; SetDegraded("Diagnostics degraded: metadata flush failure: " + ex.GetType().Name); }
            }
            try { _stream?.Flush(true); }
            catch (Exception ex)
            {
                lock (_gate) { _incomplete = true; _unknownTail = true; SetDegraded("Diagnostics degraded: flush failure: " + ex.GetType().Name); }
            }
            finally
            {
                try { _stream?.Dispose(); } catch { }
                _stream = null;
                try { if (_activeMarker is not null) File.Delete(_activeMarker); }
                catch (Exception ex) { SetDegraded("Diagnostics degraded: active marker cleanup failure: " + ex.GetType().Name); }
                _activeMarker = null;
                LiveSessions.TryRemove(Path.GetFullPath(DirectoryPath), out _);
            }
        }
    }

    public static bool TryGetLive(string directoryPath, out DiagnosticSession session) =>
        LiveSessions.TryGetValue(Path.GetFullPath(directoryPath), out session!);

    private DiagnosticSessionRecord CreateControl(string name, long? seq = null, DiagnosticKind kind = DiagnosticKind.Event) =>
        new(SchemaVersion, Id, seq ?? ++_sequence, _utcNow(), _ticks(), _buildIdentity.BuildVersion, Channel,
            _buildIdentity.GitHash, DiagnosticRuntimeIdentity.OperatingSystem, DiagnosticRuntimeIdentity.Dpi, DiagnosticRuntimeIdentity.DeviceId,
            name, kind, DiagnosticSeverity.Information, null, null, null, null, null, null, null, null, null);

    private DateTimeOffset _sessionStartedUtc;
    private DateTimeOffset _lastActivityUtc;

    private DiagnosticSessionSnapshot CaptureCheckpointLocked(long cutoffSequence)
    {
        var files = _writtenFiles.Select(file => new DiagnosticSnapshotFile(file.Key, file.Value)).ToArray();
        return new(Id, cutoffSequence, files,
            _gaps.Where(gap => gap.Start <= cutoffSequence)
                .Select(gap => new DiagnosticSnapshotGap(gap.Start, Math.Min(gap.End, cutoffSequence), gap.Reason)).ToArray(),
            _incomplete, _unknownTail, Channel, BuildVersion, _buildIdentity.GitHash);
    }
}

public sealed record DiagnosticSnapshotFile(string Path, long Length);
public sealed record DiagnosticSnapshotGap(long Start, long End, string Reason);
public sealed record DiagnosticSessionSnapshot(
    Guid SessionId,
    long CutoffSequence,
    IReadOnlyList<DiagnosticSnapshotFile> Files,
    IReadOnlyList<DiagnosticSnapshotGap> Gaps,
    bool Incomplete,
    bool UnknownTail,
    string Channel,
    string BuildVersion,
    string GitHash);
