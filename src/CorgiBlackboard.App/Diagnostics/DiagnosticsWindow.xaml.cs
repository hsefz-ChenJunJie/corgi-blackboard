using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using CorgiBlackboard.Infrastructure.Diagnostics;
using Microsoft.Win32;

namespace CorgiBlackboard.App.Diagnostics;

public partial class DiagnosticsWindow : Window
{
    private readonly DiagnosticSession _currentSession;
    private readonly string _rootDirectory;
    private readonly DiagnosticLogManager _manager;

    public DiagnosticsWindow(DiagnosticSession currentSession)
    {
        InitializeComponent();
        _currentSession = currentSession;
        _rootDirectory = Directory.GetParent(currentSession.DirectoryPath)!.FullName;
        _manager = new DiagnosticLogManager(_rootDirectory);
        var policy = _manager.LoadPolicy();
        RetentionDays.Text = policy.RetentionDays.ToString();
        RetentionMegabytes.Text = (policy.MaximumBytes / (1024d * 1024d)).ToString("0");
        _currentSession.Degraded += OnDegraded;
        _currentSession.RetentionApplied += OnRetentionApplied;
        Closed += (_, _) =>
        {
            _currentSession.Degraded -= OnDegraded;
            _currentSession.RetentionApplied -= OnRetentionApplied;
        };
        Refresh();
        if (_currentSession.Retention is { } retention) ShowRetention(retention);
    }

    private void OnDegraded(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            MessageText.Text = message;
            Refresh();
        });
    }

    private void OnRetentionApplied(DiagnosticRetentionResult result) =>
        Dispatcher.BeginInvoke(() => ShowRetention(result));

    private void ShowRetention(DiagnosticRetentionResult result)
    {
        DiskText.Text = $"日志占用：{result.Bytes:N0} 字节";
        OverageText.Text = result.ExcessBytes == 0 ? "" :
            $"超出额度 {result.ExcessBytes:N0} 字节：" +
            string.Join("；", result.ProtectionReasons.Select(pair =>
                $"{Path.GetFileName(pair.Key)} {DiagnosticLogManager.ProtectionMessage(pair.Value)}"));
    }

    private void Refresh()
    {
        IEnumerable<string> directories = Directory.Exists(_rootDirectory)
            ? Directory.EnumerateDirectories(_rootDirectory).OrderByDescending(path => Directory.GetLastWriteTimeUtc(path))
            : Array.Empty<string>();
        var items = directories.Select(path => SessionItem.Read(path, path.Equals(_currentSession.DirectoryPath, StringComparison.OrdinalIgnoreCase))).ToArray();
        SessionsList.ItemsSource = items;
        StatusText.Text = $"捕获状态：{(_currentSession.IsDegraded ? "降级" : "正常")}{(_currentSession.IsIncomplete ? "（存在缺口或未完成尾部）" : "")}";
        IdentityText.Text = $"版本：{_currentSession.BuildVersion} / {_currentSession.Channel} / 会话：{_currentSession.Id:N}";
        StatusText.Tag = _currentSession.IsDegraded ? "degraded" : "normal";
        DiskText.Text = $"日志占用：{items.Sum(item => item.Bytes):N0} 字节";
    }

    private void OpenFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = _rootDirectory, UseShellExecute = true }); }
        catch (Exception ex)
        {
            _currentSession.TryRecord(DiagnosticProducers.Failure("OpenLogFolderFailed", ex, TimeSpan.Zero));
            MessageText.Text = $"打开目录失败：{ex.Message}";
        }
    }

    private async void ExportSelected(object sender, RoutedEventArgs e)
    {
        var selected = SessionsList.SelectedItems.Cast<SessionItem>().ToArray();
        if (selected.Length == 0) { MessageText.Text = "请先选择要导出的会话。"; return; }
        var destination = OutputPath.Text;
        if (string.IsNullOrWhiteSpace(destination))
        {
            var dialog = new SaveFileDialog { Filter = "反馈包 (*.zip)|*.zip", FileName = "corgi-blackboard-feedback.zip", AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            destination = dialog.FileName;
            OutputPath.Text = destination;
        }
        try
        {
            MessageText.Tag = "exporting";
            var result = await new FeedbackExporter().ExportAsync(new FeedbackExportRequest(
                destination,
                selected.Select(item => item.Path).ToArray(),
                ReproductionSteps.Text,
                Expected.Text,
                Actual.Text,
                CoordinateDisclosure: "此反馈包包含已记录的触点坐标。"));
            MessageText.Text = result.Incomplete
                ? "反馈包已导出，但包含不完整日志；请把此状态告知开发者。"
                : "反馈包已导出，可离线附加给开发者。";
            MessageText.Tag = result.Incomplete ? "incomplete" : "complete";
        }
        catch (OperationCanceledException) { MessageText.Text = "导出已取消，未生成成功反馈包。"; MessageText.Tag = "cancelled"; }
        catch (Exception ex)
        {
            _currentSession.TryRecord(DiagnosticProducers.Failure("FeedbackExportFailed", ex, TimeSpan.Zero));
            MessageText.Text = $"导出失败：{ex.Message}"; MessageText.Tag = "failed";
        }
    }

    private void DeleteSelected(object sender, RoutedEventArgs e)
    {
        var selected = SessionsList.SelectedItems.Cast<SessionItem>().ToArray();
        if (selected.Length == 0) { MessageText.Text = "请先选择要删除的会话。"; return; }
        if (selected.Any(item => item.IsCurrent))
        {
            MessageText.Text = "当前活动会话受保护，不能删除。";
            MessageText.Tag = "protected";
            return;
        }
        var scope = string.Join(Environment.NewLine, selected.Select(item => item.Display));
        if (MessageBox.Show($"将删除以下 {selected.Length} 个会话：{Environment.NewLine}{scope}", "确认删除范围", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        foreach (var item in selected)
            if (!_manager.TryDeleteSession(item.Path, out var message)) MessageText.Text = message;
        Refresh();
    }

    private void TogglePinned(object sender, RoutedEventArgs e)
    {
        var selected = SessionsList.SelectedItems.Cast<SessionItem>().ToArray();
        if (selected.Length != 1) { MessageText.Text = "请选择一个会话固定或取消固定。"; return; }
        if (_manager.SetPinned(selected[0].Path, !selected[0].IsPinned, out var message)) Refresh();
        MessageText.Text = message;
    }

    private void ApplyRetention(object sender, RoutedEventArgs e)
    {
        if (!DiagnosticRetentionPolicy.TryParse(RetentionDays.Text, RetentionMegabytes.Text, out var policy))
        {
            MessageText.Text = "保留天数须为 1–36500 的整数，总额须为可表示的正数 MB。";
            MessageText.Tag = "invalid-retention";
            return;
        }
        try
        {
            _manager.SavePolicy(policy);
            var result = _manager.ApplyRetention();
            Refresh();
            ShowRetention(result);
            MessageText.Text = "保留设置已应用，当前会话采样未重置。";
            MessageText.Tag = "retention-applied";
        }
        catch (IOException ex)
        {
            _currentSession.TryRecord(DiagnosticProducers.Failure("RetentionFailed", ex, TimeSpan.Zero));
            MessageText.Text = $"保留设置失败：{ex.Message}"; MessageText.Tag = "failed";
        }
        catch (UnauthorizedAccessException ex)
        {
            _currentSession.TryRecord(DiagnosticProducers.Failure("RetentionFailed", ex, TimeSpan.Zero));
            MessageText.Text = $"保留设置失败：{ex.Message}"; MessageText.Tag = "failed";
        }
    }

    private sealed record SessionItem(
        string Path,
        string Display,
        long Bytes,
        bool IsCurrent,
        bool IsPinned,
        bool IsLeased)
    {
        public static SessionItem Read(string path, bool current)
        {
            var metadataPath = System.IO.Path.Combine(path, "session.json");
            var last = Directory.GetLastWriteTimeUtc(path);
            var pinned = false;
            var abnormal = !File.Exists(metadataPath) || DiagnosticLogManager.HasIncompleteInitialization(path);
            var leased = Directory.EnumerateFiles(path, ".lease-*").Any();
            if (File.Exists(metadataPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
                    var root = document.RootElement;
                    if (root.TryGetProperty("lastActivityUtc", out var activity)) last = activity.GetDateTimeOffset().UtcDateTime;
                    pinned = root.TryGetProperty("pinned", out var pin) && pin.GetBoolean();
                    var markerPath = System.IO.Path.Combine(path, ".active.lock");
                    abnormal |= DiagnosticSessionStatus.IsAbnormal(root,
                        File.Exists(markerPath) ? File.ReadAllText(markerPath) : null);
                }
                catch { abnormal = true; }
            }
            var bytes = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
            return new SessionItem(path, $"{last.ToLocalTime():yyyy-MM-dd HH:mm:ss}  {bytes:N0} 字节  {(abnormal ? "异常/未完成" : "正常")}  {(pinned ? "已固定" : "")}{(leased ? " 导出中" : "")}", bytes, current, pinned, leased);
        }
    }
}
