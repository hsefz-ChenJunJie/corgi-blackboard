using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

public sealed record FeedbackExportRequest(
    string DestinationPath,
    IReadOnlyList<string> SessionDirectories,
    string ReproductionSteps = "",
    string Expected = "",
    string Actual = "",
    string? ChecklistText = null,
    string CoordinateDisclosure = "此反馈包包含已记录的触点坐标。请在导出前确认可以分享这些坐标。",
    Func<string, CancellationToken, ValueTask>? CopyProgressAsync = null,
    Func<long, CancellationToken, ValueTask>? BytesCopiedAsync = null);

public sealed record FeedbackExportResult(string DestinationPath, bool Incomplete, IReadOnlyList<string> SessionIds);

public sealed class FeedbackExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<FeedbackExportResult> ExportAsync(FeedbackExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.DestinationPath) || request.SessionDirectories.Count == 0)
            throw new ArgumentException("请选择诊断会话和反馈包保存位置。", nameof(request));
        var destination = Path.GetFullPath(request.DestinationPath);
        var directories = request.SessionDirectories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (directories.Any(directory => destination.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("反馈包不能覆盖诊断会话目录中的文件。", nameof(request));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var leases = new List<IDisposable>();
        try
        {
            var snapshots = new List<FeedbackSnapshot>();
            foreach (var directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DiagnosticSession? live;
                using (DiagnosticDirectoryAccess.Enter(directory))
                {
                    if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
                    DiagnosticSession.TryGetLive(directory, out live!);
                    if (live is null && DiagnosticDirectoryAccess.IsActive(directory, DiagnosticMetadata.Read(directory)))
                        throw new InvalidOperationException("会话仍由其他进程捕获；请先在该进程中关闭会话再导出。");
                    leases.Add(DiagnosticDirectoryAccess.Lease(directory));
                }
                snapshots.Add(await FeedbackSnapshot.CaptureAsync(directory, live, cancellationToken).ConfigureAwait(false));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var snapshot in snapshots)
                    {
                        foreach (var file in snapshot.Files)
                        {
                            var entryName = $"sessions/{snapshot.Id:N}/{Path.GetFileName(file.Path)}";
                            if (request.CopyProgressAsync is not null)
                                await request.CopyProgressAsync(entryName, cancellationToken).ConfigureAwait(false);
                            await using var target = archive.CreateEntry(entryName, CompressionLevel.Fastest).Open();
                            file.Sha256 = await CopyRangeAsync(file, target, request.BytesCopiedAsync, cancellationToken).ConfigureAwait(false);
                        }
                        // Compatibility view is the same concatenation of bounded, complete records.
                        // No second parse of a growing source may change snapshot completeness.
                        await using var combined = archive.CreateEntry($"sessions/{snapshot.Id:N}/events.jsonl", CompressionLevel.Fastest).Open();
                        foreach (var file in snapshot.Files)
                            await CopyRangeAsync(file, combined, null, cancellationToken).ConfigureAwait(false);
                    }
                    await WriteEntryAsync(archive, "environment.json", BuildEnvironment(snapshots).ToJsonString(JsonOptions), cancellationToken).ConfigureAwait(false);
                    await WriteEntryAsync(archive, "classroom-checklist.md", BuildChecklist(snapshots, request.ChecklistText), cancellationToken).ConfigureAwait(false);
                    var manifest = new JsonObject
                    {
                        ["schemaVersion"] = 1, ["createdUtc"] = DateTimeOffset.UtcNow,
                        ["incomplete"] = snapshots.Any(snapshot => snapshot.Incomplete),
                        ["sessions"] = new JsonArray(snapshots.Select(snapshot => snapshot.Manifest()).ToArray()),
                        ["reproduction"] = new JsonObject
                        {
                            ["steps"] = request.ReproductionSteps, ["expected"] = request.Expected, ["actual"] = request.Actual,
                            ["coordinateDisclosure"] = request.CoordinateDisclosure
                        }
                    };
                    await WriteEntryAsync(archive, "manifest.json", manifest.ToJsonString(JsonOptions), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            return new(destination, snapshots.Any(snapshot => snapshot.Incomplete), snapshots.Select(snapshot => snapshot.Id.ToString("N")).ToArray());
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (DirectoryNotFoundException) { /* No archive was created before cancellation or refusal. */ }
            finally { foreach (var lease in leases) lease.Dispose(); }
        }
    }

    private static async Task<string> CopyRangeAsync(FeedbackSnapshotFile file, Stream target,
        Func<long, CancellationToken, ValueTask>? progress, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        var remaining = file.Length;
        while (remaining > 0)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("诊断日志快照已被截断。");
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            remaining -= count;
            if (progress is not null) await progress(file.Length - remaining, cancellationToken).ConfigureAwait(false);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static JsonObject BuildEnvironment(IReadOnlyList<FeedbackSnapshot> snapshots)
    {
        var result = new JsonObject();
        foreach (var key in new[] { "operatingSystem", "runtime", "architecture", "dpi", "deviceId" })
        {
            var values = snapshots.Select(snapshot => DiagnosticMetadata.Text(snapshot.Metadata, key)).Distinct(StringComparer.Ordinal).ToArray();
            result[key] = values.Length == 1 ? values[0] : "unknown";
        }
        result["sessions"] = new JsonArray(snapshots.Select(snapshot =>
        {
            var environment = new JsonObject { ["sessionId"] = snapshot.Id };
            foreach (var key in new[] { "operatingSystem", "runtime", "architecture", "dpi", "deviceId" })
                environment[key] = DiagnosticMetadata.Text(snapshot.Metadata, key);
            return (JsonNode)environment;
        }).ToArray());
        return result;
    }

    private static string BuildChecklist(IEnumerable<FeedbackSnapshot> snapshots, string? supplied)
    {
        var sections = snapshots.Select(snapshot =>
            $"## {snapshot.Id:N}\n{DiagnosticMetadata.Text(snapshot.Metadata, "checklist")}");
        return string.Join("\n\n", supplied is null ? sections : sections.Append("## Supplemental feedback\n" + supplied));
    }

    private static async Task WriteEntryAsync(ZipArchive archive, string name, string text, CancellationToken cancellationToken)
    {
        await using var stream = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
