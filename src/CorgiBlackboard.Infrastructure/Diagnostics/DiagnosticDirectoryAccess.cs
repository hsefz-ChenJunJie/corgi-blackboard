using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

internal static class DiagnosticDirectoryAccess
{
    // The mutex is held only during directory mutations, never across an await or ZIP copy.
    // Every process uses the same normalized path, including when the directory no longer exists.
    internal static IDisposable Enter(string directory, Action? contended = null)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();
        var name = "Local\\CorgiBlackboard.Diagnostics." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var mutex = new Mutex(false, name);
        try
        {
            try
            {
                if (!mutex.WaitOne(0))
                {
                    contended?.Invoke();
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(15)))
                        throw new IOException("诊断会话正忙，请稍后重试。");
                }
            }
            catch (AbandonedMutexException) { /* Ownership was transferred by the OS. */ }
            return new MutationLock(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    internal static IDisposable Lease(string directory, Action? contended = null)
    {
        using var mutation = Enter(directory, contended);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var path = Path.Combine(directory, ".lease-" + Guid.NewGuid().ToString("N"));
        // DeleteOnClose also removes the marker when an exporting process crashes.
        var handle = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            1, FileOptions.DeleteOnClose);
        return handle;
    }

    internal static bool IsActive(string directory, JsonObject? metadata) =>
        DiagnosticSession.TryGetLive(directory, out _) ||
        File.Exists(Path.Combine(directory, ".active.lock")) ||
        DiagnosticMetadata.Boolean(metadata, "active");

    internal static bool IsLeased(string directory) => Directory.EnumerateFiles(directory, ".lease-*").Any();
    private sealed class MutationLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
