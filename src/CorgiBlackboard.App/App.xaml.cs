using System.Windows;
using CorgiBlackboard.App.Diagnostics;
using CorgiBlackboard.Infrastructure.Diagnostics;

namespace CorgiBlackboard.App;

public partial class App : Application
{
    private DiagnosticStartup<DiagnosticSession>? _diagnostics;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _diagnostics = DiagnosticStartup<DiagnosticSession>.Start(() => new DiagnosticSession(DesktopQaLogOptions()));
        StartDiagnosticCapture();
        MainWindow = new MainWindow(_diagnostics);
        MainWindow.SizeChanged += (_, _) =>
        {
            var window = (MainWindow)MainWindow;
            window.Toolbar.MaxHeight = Math.Max(54, window.ActualHeight * 0.45);
        };
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopDiagnosticCapture();
        DiagnosticSessionShutdown.ShutdownAsync(_diagnostics?.Session).GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
