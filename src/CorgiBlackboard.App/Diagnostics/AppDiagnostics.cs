using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CorgiBlackboard.Infrastructure.Diagnostics;

namespace CorgiBlackboard.App;

public partial class App
{
    private DispatcherTimer? _performanceTimer;
    private long _lastPerformance;

    private void StartDiagnosticCapture()
    {
        DispatcherUnhandledException += CaptureDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += CaptureDomainException;
        TaskScheduler.UnobservedTaskException += CaptureTaskException;
        _lastPerformance = Stopwatch.GetTimestamp();
        _performanceTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
            (_, _) => RecordPerformance(), Dispatcher);
        RecordPerformance();
    }

    private void StopDiagnosticCapture()
    {
        _performanceTimer?.Stop();
        DispatcherUnhandledException -= CaptureDispatcherException;
        AppDomain.CurrentDomain.UnhandledException -= CaptureDomainException;
        TaskScheduler.UnobservedTaskException -= CaptureTaskException;
        RecordPerformance();
    }

    private void RecordPerformance()
    {
        var now = Stopwatch.GetTimestamp();
        _diagnostics?.Session?.TryRecord(DiagnosticProducers.Performance(
            Stopwatch.GetElapsedTime(_lastPerformance, now)));
        _lastPerformance = now;
    }

    private void CaptureDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs args) =>
        CaptureUnhandled("DispatcherUnhandledException", args.Exception);

    private void CaptureDomainException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception error) CaptureUnhandled("DomainUnhandledException", error);
    }

    private void CaptureTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        CaptureUnhandled("UnobservedTaskException", args.Exception);

    private void CaptureUnhandled(string name, Exception error)
    {
        var session = _diagnostics?.Session;
        if (session is null) return;
        try
        {
            session.TryRecord(DiagnosticProducers.Failure(name, error, TimeSpan.Zero));
            // Best effort only: never mark the exception handled or wait indefinitely during a crash.
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            session.FlushAsync(timeout.Token).WaitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // The original fatal exception must retain its runtime behavior if capture fails.
        }
    }
}
