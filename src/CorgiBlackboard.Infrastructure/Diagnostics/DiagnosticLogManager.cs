using System.Text.Json;
using System.Text.Json.Nodes;

namespace CorgiBlackboard.Infrastructure.Diagnostics;

public sealed class DiagnosticLogManager(string rootDirectory, Func<DateTimeOffset>? utcNow = null)
{
    private readonly Func<DateTimeOffset> _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    private string PolicyPath => Path.Combine(rootDirectory, "retention.json");
    internal Action? BeforeDelete { get; init; }

    public static bool HasIncompleteInitialization(string sessionDirectory) =>
        Guid.TryParseExact(Path.GetFileName(sessionDirectory), "N", out var id) &&
        Directory.GetParent(sessionDirectory) is { } parent &&
        File.Exists(DiagnosticInitialization.ClaimPath(parent.FullName, id));

    public DiagnosticRetentionPolicy LoadPolicy()
    {
        Directory.CreateDirectory(rootDirectory);
        using var guard = DiagnosticDirectoryAccess.Enter(rootDirectory);
        if (!File.Exists(PolicyPath)) return new();
        try
        {
            var policy = JsonSerializer.Deserialize<DiagnosticRetentionPolicy>(File.ReadAllText(PolicyPath));
            return policy is { RetentionDays: >= 1 and <= 36500, MaximumBytes: > 0 } ? policy : new();
        }
        catch (JsonException) { return new(); }
    }

    public void SavePolicy(DiagnosticRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.RetentionDays is < 1 or > 36500 || policy.MaximumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(policy));
        Directory.CreateDirectory(rootDirectory);
        using var guard = DiagnosticDirectoryAccess.Enter(rootDirectory);
        var temporary = PolicyPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(policy));
        File.Move(temporary, PolicyPath, true);
    }

    public DiagnosticRetentionResult ApplyRetention()
    {
        var policy = LoadPolicy();
        return ApplyRetentionCore(policy);
    }

    public DiagnosticRetentionResult ApplyRetention(int days, long maximumBytes = 2L * 1024 * 1024 * 1024)
    {
        var policy = new DiagnosticRetentionPolicy(days, maximumBytes);
        SavePolicy(policy);
        return ApplyRetentionCore(policy);
    }

    public bool SetPinned(string sessionDirectory, bool pinned, out string message)
    {
        using var guard = DiagnosticDirectoryAccess.Enter(sessionDirectory);
        if (DiagnosticSession.TryGetLive(sessionDirectory, out var live))
        {
            live.Pin(pinned);
        }
        else
        {
            var metadata = DiagnosticMetadata.Read(sessionDirectory);
            if (metadata is null || DiagnosticDirectoryAccess.IsActive(sessionDirectory, metadata) ||
                DiagnosticDirectoryAccess.IsLeased(sessionDirectory))
            {
                message = "会话正忙或元数据不可读，无法修改固定状态。";
                return false;
            }
            metadata["pinned"] = pinned;
            DiagnosticMetadata.Write(sessionDirectory, metadata);
        }
        message = pinned ? "会话已固定。" : "会话已取消固定。";
        return true;
    }

    public bool TryDeleteSession(string sessionDirectory, out string message)
    {
        using var guard = DiagnosticDirectoryAccess.Enter(sessionDirectory);
        if (!Directory.Exists(sessionDirectory))
        {
            message = "会话不存在。";
            return false;
        }
        var reason = ProtectionReason(sessionDirectory, DiagnosticMetadata.Read(sessionDirectory));
        if (reason is not null)
        {
            message = ProtectionMessage(reason);
            return false;
        }
        BeforeDelete?.Invoke();
        try
        {
            Directory.Delete(sessionDirectory, true);
            message = "会话已删除。";
            return true;
        }
        catch (IOException ex) { message = $"删除失败：{ex.Message}"; return false; }
        catch (UnauthorizedAccessException ex) { message = $"删除失败：{ex.Message}"; return false; }
    }

    public IDisposable Lease(string sessionDirectory) => DiagnosticDirectoryAccess.Lease(sessionDirectory);

    public IReadOnlyList<RecoveredSession> RecoverAbandonedSessions() =>
        DiagnosticRecovery.Recover(rootDirectory);

    private DiagnosticRetentionResult ApplyRetentionCore(DiagnosticRetentionPolicy policy)
    {
        using var rootGuard = DiagnosticDirectoryAccess.Enter(rootDirectory);
        var sessions = Directory.EnumerateDirectories(rootDirectory).Select(ReadSession)
            .OfType<SessionInfo>().OrderBy(info => info.LastActivity).ToArray();
        var total = sessions.Sum(info => info.Bytes);
        var deleted = new List<string>();
        foreach (var session in sessions)
        {
            var expired = (_utcNow() - session.LastActivity).TotalDays > policy.RetentionDays;
            if (!expired && total <= policy.MaximumBytes) break;
            if (TryDeleteSession(session.Path, out _))
            {
                total -= session.Bytes;
                deleted.Add(session.Path);
            }
        }
        // Re-read after deletion: pin, lease and activity may have changed since enumeration.
        var retained = Directory.EnumerateDirectories(rootDirectory).Select(ReadSession).OfType<SessionInfo>().ToArray();
        total = retained.Sum(info => info.Bytes);
        var protectedItems = total > policy.MaximumBytes
            ? retained.ToDictionary(info => info.Path, info => info.Bytes, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, long>();
        var reasons = total > policy.MaximumBytes
            ? retained.ToDictionary(info => info.Path, info => info.Reason ?? "delete-failed", StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>();
        return new(total, policy.MaximumBytes, deleted, protectedItems) { ProtectionReasons = reasons };
    }

    private static SessionInfo? ReadSession(string path)
    {
        using var guard = DiagnosticDirectoryAccess.Enter(path);
        if (!Directory.Exists(path)) return null;
        var metadata = DiagnosticMetadata.Read(path);
        var last = metadata?["lastActivityUtc"] is JsonValue value && value.TryGetValue<DateTimeOffset>(out var activity)
            ? activity : new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        return new(path, DirectoryBytes(path), last, ProtectionReason(path, metadata));
    }

    private static string? ProtectionReason(string path, JsonObject? metadata)
    {
        if (HasIncompleteInitialization(path)) return "initializing";
        if (DiagnosticDirectoryAccess.IsActive(path, metadata)) return "active";
        if (DiagnosticDirectoryAccess.IsLeased(path)) return "export-leased";
        if (metadata is null) return "unreadable-metadata";
        if (DiagnosticMetadata.Boolean(metadata, "pinned")) return "pinned";
        return null;
    }

    public static string ProtectionMessage(string reason) => reason switch
    {
        "initializing" => "会话初始化未完成，等待安全恢复。",
        "active" => "活动会话受保护。",
        "export-leased" => "会话正在导出。",
        "pinned" => "会话已固定。",
        "unreadable-metadata" => "会话元数据不可读。",
        _ => "会话删除失败。"
    };

    private static long DirectoryBytes(string path)
    {
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { bytes += new FileInfo(file).Length; }
            catch (FileNotFoundException) { /* A metadata replacement or lease release completed. */ }
        }
        return bytes;
    }
    private sealed record SessionInfo(string Path, long Bytes, DateTimeOffset LastActivity, string? Reason);
}
