using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using CorgiBlackboard.Infrastructure.Diagnostics;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.App;

// An opt-in, Test-channel, read-only observation seam. No command, replay or input bypass.
internal static class DesktopQa
{
    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("CORGI_DESKTOP_QA") == "1" &&
        typeof(DiagnosticSession).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(value => value.Key == "ReleaseChannel" && value.Value == "Test");
}

public partial class App
{
    private static DiagnosticOptions? DesktopQaLogOptions()
    {
        if (!DesktopQa.Enabled) return null;
        var root = Environment.GetEnvironmentVariable("CORGI_QA_ROOT")
            ?? throw new InvalidOperationException("QA requires an owned temporary root.");
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("corgi-desktop-qa-", StringComparison.Ordinal))
            throw new InvalidOperationException("QA logs must be isolated in a corgi-desktop-qa-* temporary directory.");
        return new DiagnosticOptions { RootDirectory = Path.Combine(full, "logs") };
    }
}

public partial class MainWindow
{
    private long _qaInputRevision;
    private long _qaRevision;
    private bool _qaQueued;
    private readonly Queue<object> _qaObserved = [];

    private void InitializeDesktopQa()
    {
        if (!DesktopQa.Enabled) return;
        InputManager.Current.PostProcessInput += QaInput;
        Editing.Session.PendingChanged += QueueQa;
        Editing.Session.ContentChanged += _ => QueueQa();
        Editing.Session.CameraChanged += QueueQa;
        Editing.Session.ReadingChanged += _ => QueueQa();
        Documents.LayoutChanged += _ => QueueQa();
        Documents.Opened += _ => QueueQa();
        SizeChanged += (_, _) => QueueQa();
        Closed += (_, _) => InputManager.Current.PostProcessInput -= QaInput;
        Loaded += (_, _) => QueueQa();
    }

    private void QaInput(object sender, ProcessInputEventArgs args)
    {
        _qaInputRevision++;
        if (args.StagingItem.Input is StylusEventArgs stylus &&
            stylus.RoutedEvent is { } routed &&
            (routed == Stylus.StylusDownEvent || routed == Stylus.StylusMoveEvent || routed == Stylus.StylusUpEvent))
        {
            var point = PointToScreen(stylus.GetPosition(this));
            _qaObserved.Enqueue(new
            {
                revision = _qaInputRevision, id = stylus.StylusDevice.Id,
                action = routed == Stylus.StylusDownEvent ? "Down" : routed == Stylus.StylusUpEvent ? "Up" : "Move",
                x = point.X, y = point.Y, source = "WPF routed stylus"
            });
            while (_qaObserved.Count > 32) _qaObserved.Dequeue();
        }
        QueueQa();
    }

    private void QueueQa()
    {
        if (_qaQueued) return;
        _qaQueued = true;
        // ContextIdle follows layout/render work. The runner also calls DwmFlush before capture.
        Dispatcher.InvokeAsync(() =>
        {
            _qaQueued = false;
            var metadata = typeof(DiagnosticSession).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToDictionary(value => value.Key, value => value.Value);
            var camera = Workspace.Camera.Transform;
            var status = JsonSerializer.Serialize(new
            {
                schemaVersion = 1, revision = ++_qaRevision, inputRevision = _qaInputRevision,
                observedInput = _qaObserved.ToArray(),
                channel = _diagnostics?.Channel, version = _diagnostics?.BuildVersion,
                gitHash = metadata.GetValueOrDefault("GitHash"), sessionId = _diagnostics?.Id,
                contentRevision = Workspace.ContentRevision, layoutRevision = Workspace.Layout.Revision,
                readingRevision = Workspace.Reading.Revision, state = Input.State.ToString(),
                camera = new { x = camera.OffsetX, y = camera.OffsetY, scale = camera.Scale },
                pending = Editing.Session.PendingSurfaceId is not null,
                pendingBefore = Editing.Session.PendingBefore.Length,
                pendingAfter = Editing.Session.PendingAfter.Length,
                board = QaContent(Workspace.BoardSurfaceId),
                documents = Workspace.Documents.Values.Select(document =>
                {
                    var layout = Workspace.Layout.Windows[document.Id];
                    var viewport = Workspace.Layout.ContentViewport(document.Id);
                    var offset = Workspace.Reading.Offsets.GetValueOrDefault(document.PageSurfaceIds[0]);
                    return new
                    {
                        id = document.Id.Value, assetId = document.AssetId.Value,
                        surfaceId = document.PageSurfaceIds[0].Value,
                        width = document.PixelWidth, height = document.PixelHeight,
                        filled = layout.IsFilled, minimized = layout.IsMinimized,
                        rectangle = layout.NormalRectangle, display = Workspace.Layout.DisplayRectangle(document.Id),
                        scale = viewport.Scale, viewportWidth = viewport.Width, viewportHeight = viewport.Height,
                        offsetX = offset.X, offsetY = offset.Y,
                        strokes = QaContent(document.PageSurfaceIds[0])
                    };
                }).ToArray()
            });
            var previous = AutomationProperties.GetItemStatus(this);
            AutomationProperties.SetItemStatus(this, status);
            UIElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(
                AutomationElementIdentifiers.ItemStatusProperty, previous, status);
        }, DispatcherPriority.ContextIdle);
    }

    // Preserve every element's identity/transform; only stroke elements expose ink-specific data.
    // This also composes with the verified task-12 Element base without serializing it as StrokeElement.
    private object[] QaContent(SurfaceId surface) => Workspace.GetElements(surface).Select(element => (object)new
    {
        id = element.Id.Value, kind = element.GetType().Name, transform = element.Transform,
        stroke = element is StrokeElement stroke
            ? new { points = stroke.Points, style = stroke.Style, sourceElementId = stroke.SourceElementId }
            : null
    }).ToArray();
}
