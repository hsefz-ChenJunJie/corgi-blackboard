using System.IO;

namespace CorgiBlackboard.App.Diagnostics;

internal sealed record DiagnosticStartup<T>(T? Session, Exception? Failure) where T : class
{
    public bool IsDegraded => Failure is not null;
    public string StatusText => IsDegraded
        ? "诊断日志已降级：本次会话未记录。教学功能可继续使用。请检查本机日志目录权限与磁盘空间，关闭程序后重试。"
        : Session is null ? "诊断日志未启用。教学功能可继续使用。" : "";

    public void OpenDiagnostics(Action<T> openSession, Action<string> showUnavailable)
    {
        if (Session is { } session) openSession(session);
        else showUnavailable(StatusText);
    }

    public static DiagnosticStartup<T> Start(Func<T> create)
    {
        try { return new(create(), null); }
        catch (IOException ex) { return new(null, ex); }
        catch (UnauthorizedAccessException ex) { return new(null, ex); }
    }
}
