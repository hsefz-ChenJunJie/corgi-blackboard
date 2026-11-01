using System.Text.Json.Nodes;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

// The root claim exists only before publication. Its exclusive handle, not a PID string,
// prevents recovery from touching an initialization still owned by another process.
internal sealed class DiagnosticInitialization : IDisposable
{
    private const string Prefix = ".initializing-";
    private readonly string _root;
    private readonly Guid _id;
    private readonly IDisposable _guard;
    private FileStream? _claim;
    private bool _ownsClaim;
    private bool _completed;
    internal string DirectoryPath => Path.Combine(_root, _id.ToString("N"));

    internal DiagnosticInitialization(string root, Guid id)
    {
        _root = root;
        _id = id;
        _guard = DiagnosticDirectoryAccess.Enter(root);
        try
        {
            if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath))
                throw new IOException("The diagnostic initialization directory already exists.");
            _claim = new FileStream(ClaimPath(root, id), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            _ownsClaim = true;
            Directory.CreateDirectory(DirectoryPath);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static string ClaimPath(string root, Guid id) => Path.Combine(root, Prefix + id.ToString("N"));

    internal void Complete()
    {
        _claim?.Dispose();
        _claim = null;
        // Removal must succeed before the queue, writer and live session are published.
        File.Delete(ClaimPath(_root, _id));
        _completed = true;
    }

    public void Dispose()
    {
        try
        {
            _claim?.Dispose();
            _claim = null;
            if (_ownsClaim && !_completed && TryCleanup(_root, _id))
                File.Delete(ClaimPath(_root, _id));
        }
        catch (IOException) { /* Keep the claim for a later exclusive recovery attempt. */ }
        catch (UnauthorizedAccessException) { /* Preserve the original startup storage failure. */ }
        finally { _guard.Dispose(); }
    }

    internal static void Recover(string root)
    {
        using var guard = DiagnosticDirectoryAccess.Enter(root);
        foreach (var claimPath in Directory.EnumerateFiles(root, Prefix + "*"))
        {
            var name = Path.GetFileName(claimPath);
            if (!Guid.TryParseExact(name[Prefix.Length..], "N", out var id)) continue;
            try
            {
                if ((File.GetAttributes(claimPath) & FileAttributes.ReparsePoint) != 0) continue;
                // A live initializer holds FileShare.None until it completes or fails.
                using (var claim = new FileStream(claimPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (!TryCleanup(root, id)) continue;
                }
                File.Delete(claimPath);
            }
            catch (IOException) { /* Busy or inaccessible: retain the claim and all remaining data. */ }
            catch (UnauthorizedAccessException) { /* Retry only on a later explicit recovery pass. */ }
        }
    }

    private static bool TryCleanup(string root, Guid id)
    {
        var directory = Path.Combine(root, id.ToString("N"));
        if (!Directory.Exists(directory)) return true;
        using var guard = DiagnosticDirectoryAccess.Enter(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        if (entries.Any(path => (File.GetAttributes(path) &
            (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)) return false;
        var metadata = DiagnosticMetadata.Read(directory);
        Guid? metadataClaimId = Guid.TryParse(DiagnosticMetadata.Text(metadata, "sessionId"), out var sessionId) &&
            sessionId == id && Guid.TryParse(DiagnosticMetadata.Text(metadata, "initializationClaim"), out var claimId)
            ? claimId : null;
        var preview = DiagnosticInitializationPlan.Preview(new(id, metadataClaimId,
            entries.Select(path => Path.GetFileName(path)).ToArray()));
        if (preview is null) return false;
        try
        {
            foreach (var name in preview) File.Delete(Path.Combine(directory, name));
            // Non-recursive deletion refuses any new/unrecognized entry.
            Directory.Delete(directory);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                metadata ??= new JsonObject();
                metadata["sessionId"] = id;
                metadata["initializationClaim"] = id;
                metadata["active"] = false;
                metadata["incomplete"] = true;
                metadata["abnormalEnd"] = true;
                metadata["unknownTail"] = true;
                metadata["initializationFailed"] = true;
                DiagnosticMetadata.Write(directory, metadata);
            }
            catch (IOException) { /* The surviving claim also exposes incomplete initialization. */ }
            catch (UnauthorizedAccessException) { /* Do not replace the startup failure. */ }
            return false;
        }
    }
}
