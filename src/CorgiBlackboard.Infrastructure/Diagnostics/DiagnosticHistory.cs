using System.Text.Json;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

internal static class DiagnosticHistory
{
    internal sealed record Prefix(long Length, long Cutoff, bool Damaged);

    internal static Prefix Inspect(string path, long previousSequence = 0)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var remaining = stream.Length;
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        long completeBytes = 0;
        var cutoff = previousSequence;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) return new(completeBytes, cutoff, true);
            remaining -= read;
            var start = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != (byte)'\n') continue;
                line.Write(buffer, start, index - start);
                try
                {
                    using var document = JsonDocument.Parse(line.GetBuffer().AsMemory(0, checked((int)line.Length)));
                    var record = document.RootElement;
                    if (record.ValueKind != JsonValueKind.Object ||
                        !record.TryGetProperty("sequence", out var sequence) ||
                        sequence.ValueKind != JsonValueKind.Number ||
                        !sequence.TryGetInt64(out var next) || next <= cutoff)
                        return new(completeBytes, cutoff, true);
                    cutoff = next;
                }
                catch (JsonException) { return new(completeBytes, cutoff, true); }
                completeBytes += line.Length + 1;
                line.SetLength(0);
                start = index + 1;
            }
            line.Write(buffer, start, read - start);
        }
        return new(completeBytes, cutoff, line.Length > 0);
    }
}
