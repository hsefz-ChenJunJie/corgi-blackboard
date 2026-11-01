using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

internal sealed record FeedbackSnapshot(Guid Id, long Cutoff, IReadOnlyList<FeedbackSnapshotFile> Files,
    bool Incomplete, bool UnknownTail, JsonArray Gaps, JsonObject? Metadata)
{
    internal static async Task<FeedbackSnapshot> CaptureAsync(string directory, DiagnosticSession? live, CancellationToken cancellationToken)
    {
        if (live is not null)
        {
            var checkpoint = await live.CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return new(checkpoint.SessionId, checkpoint.CutoffSequence,
                checkpoint.Files.Select(file => new FeedbackSnapshotFile(file.Path, file.Length)).ToArray(),
                checkpoint.Incomplete || checkpoint.UnknownTail, checkpoint.UnknownTail,
                new JsonArray(checkpoint.Gaps.Select(gap => (JsonNode)new JsonObject
                {
                    ["start"] = gap.Start, ["end"] = gap.End, ["reason"] = gap.Reason
                }).ToArray()), DiagnosticMetadata.Read(directory));
        }
        var metadata = DiagnosticMetadata.Read(directory);
        var damaged = metadata is null;
        var gaps = metadata?["knownGaps"] is JsonArray array ? (JsonArray)array.DeepClone() : new JsonArray();
        if (metadata?["knownGaps"] is not null and not JsonArray) damaged = true;
        long cutoff = 0;
        var files = new List<FeedbackSnapshotFile>();
        foreach (var path in DiagnosticMetadata.EventFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = DiagnosticHistory.Inspect(path, cutoff);
            cutoff = prefix.Cutoff;
            damaged |= prefix.Damaged;
            files.Add(new(path, prefix.Length));
        }
        var unknownTail = DiagnosticMetadata.Boolean(metadata, "unknownTail", true) || damaged;
        var incomplete = DiagnosticMetadata.Boolean(metadata, "incomplete", true) ||
            DiagnosticMetadata.Boolean(metadata, "abnormalEnd") || unknownTail || gaps.Count > 0;
        if (damaged)
        {
            gaps.Add(new JsonObject { ["reason"] = "malformed-history" });
            // Persist only damage flags. Never replace missing historical identity with today's identity.
            metadata ??= new JsonObject();
            metadata["incomplete"] = true;
            metadata["unknownTail"] = true;
            using var guard = DiagnosticDirectoryAccess.Enter(directory);
            DiagnosticMetadata.Write(directory, metadata);
        }
        var id = Guid.TryParse(DiagnosticMetadata.Text(metadata, "sessionId"), out var parsed) ? parsed
            : Guid.TryParse(Path.GetFileName(directory), out parsed) ? parsed
            : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(directory)).AsSpan(0, 16));
        return new(id, cutoff, files, incomplete, unknownTail, gaps, metadata);
    }

    internal JsonObject Manifest()
    {
        var result = new JsonObject
        {
            ["sessionId"] = Id, ["cutoffSequence"] = Cutoff, ["incomplete"] = Incomplete,
            ["unknownTail"] = UnknownTail, ["gaps"] = Gaps.DeepClone(),
            ["files"] = new JsonArray(Files.Select(file => (JsonNode)new JsonObject
            {
                ["name"] = Path.GetFileName(file.Path), ["cutoffBytes"] = file.Length, ["sha256"] = file.Sha256
            }).ToArray())
        };
        foreach (var key in new[] { "buildVersion", "channel", "gitHash", "operatingSystem", "architecture", "runtime", "dpi", "deviceId", "checklist" })
            result[key] = DiagnosticMetadata.Text(Metadata, key);
        return result;
    }
}

internal sealed record FeedbackSnapshotFile(string Path, long Length)
{
    internal string? Sha256 { get; set; }
}
