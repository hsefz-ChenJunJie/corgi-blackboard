using System.Windows;
using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.App.Input;

namespace CorgiBlackboard.App.Documents;

// Root preview handlers see a second contact even when it starts in content or the toolbar.
internal sealed class DocumentChromeInput : IDisposable
{
    private readonly ImageWindowHost _host;
    private readonly Window _root;
    private readonly InputArbiter _input;
    private readonly Dictionary<long, PointerSample> _contacts = [];
    private readonly HashSet<long> _chromeContacts = [];
    private WindowChromeTarget? _gesture;
    private long _gestureId;
    private Point _previous;
    private long _sequence;
    private bool _releasing;

    public DocumentChromeInput(ImageWindowHost host, Window root, InputArbiter input)
    {
        _host = host;
        _root = root;
        _input = input;
        root.PreviewMouseDown += MouseDown;
        root.PreviewMouseMove += MouseMove;
        root.PreviewMouseUp += MouseUp;
        root.PreviewStylusDown += StylusDown;
        root.PreviewStylusMove += StylusMove;
        root.PreviewStylusUp += StylusUp;
        root.LostMouseCapture += LostMouse;
        root.LostStylusCapture += LostStylus;
    }

    private bool Down(long id, Point position, PointerDeviceKind kind, object source)
    {
        var sample = Sample(id, position, kind, PointerAction.Down);
        _contacts[id] = sample;
        if (_contacts.Count > 1)
            ReleaseGesture();
        var (window, target, content) = Locate(source as DependencyObject);
        if (content)
        {
            if (window is not null) _host.Activate(window.Document.Id);
            return false;
        }
        _chromeContacts.Add(id);
        _input.Process(sample);
        if (window is not null && target is not null && _contacts.Count == 1
            && _input.State == InputArbiterState.Idle
            && !_host.Workspace.Layout.Windows[window.Document.Id].IsFilled)
        {
            _host.Activate(window.Document.Id);
            _gesture = target;
            _gestureId = id;
            _previous = position;
            return true;
        }
        return false;
    }

    private void Move(long id, Point position, PointerDeviceKind kind)
    {
        if (_chromeContacts.Contains(id))
            _input.Process(Sample(id, position, kind, PointerAction.Move));
        if (_gesture is { } gesture && _gestureId == id)
        {
            _host.Drag(gesture, position - _previous);
            _previous = position;
        }
    }

    private void Up(long id, Point position, PointerDeviceKind kind)
    {
        _contacts.Remove(id);
        if (_chromeContacts.Remove(id) || _input.State == InputArbiterState.AwaitAllReleased)
            _input.Process(Sample(id, position, kind, PointerAction.Up));
        if (_gestureId == id) ReleaseGesture();
    }

    private PointerSample Sample(long id, Point p, PointerDeviceKind kind, PointerAction action) =>
        new(id, _sequence++, Stopwatch.GetTimestamp(), new ScreenPoint(p.X, p.Y), kind, action,
            InputSurfaceKind.Chrome, null, new ViewportTransform(1, 0, 0));

    private static (ImageWindow? Window, WindowChromeTarget? Target, bool Content) Locate(DependencyObject? source)
    {
        WindowChromeTarget? target = null;
        var content = false;
        while (source is not null)
        {
            if (source is FrameworkElement element)
            {
                var id = AutomationProperties.GetAutomationId(element);
                content |= id == "BoardSurface" || id.EndsWith(".Content", StringComparison.Ordinal);
                if (element.Tag is WindowChromeTarget grip) target = grip;
                if (element is ImageWindow window) return (window, target, content);
            }
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return (null, null, content);
    }

    private void MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice is null && e.ChangedButton == MouseButton.Left
            && Down(ContentInputBinding.MousePointerId, e.GetPosition(_root), PointerDeviceKind.Mouse, e.OriginalSource))
        {
            _root.CaptureMouse();
            e.Handled = true;
        }
    }
    private void MouseMove(object sender, MouseEventArgs e)
    {
        if (e.StylusDevice is null) Move(ContentInputBinding.MousePointerId, e.GetPosition(_root), PointerDeviceKind.Mouse);
    }
    private void MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.StylusDevice is null && e.ChangedButton == MouseButton.Left)
            Up(ContentInputBinding.MousePointerId, e.GetPosition(_root), PointerDeviceKind.Mouse);
    }
    private void StylusDown(object sender, StylusDownEventArgs e)
    {
        if (Down(ContentInputBinding.StylusPointerId(e.StylusDevice), e.GetPosition(_root), Kind(e), e.OriginalSource))
        {
            e.StylusDevice.Capture(_root);
            e.Handled = true;
        }
    }
    private void StylusMove(object sender, StylusEventArgs e)
    {
        Move(ContentInputBinding.StylusPointerId(e.StylusDevice), e.GetPosition(_root), Kind(e));
    }
    private void StylusUp(object sender, StylusEventArgs e)
    {
        Up(ContentInputBinding.StylusPointerId(e.StylusDevice), e.GetPosition(_root), Kind(e));
    }

    private static PointerDeviceKind Kind(StylusEventArgs args) =>
        args.StylusDevice.TabletDevice.Type == TabletDeviceType.Touch ? PointerDeviceKind.Touch : PointerDeviceKind.Stylus;

    private void LostMouse(object sender, MouseEventArgs e) => CancelObserved(ContentInputBinding.MousePointerId);
    private void LostStylus(object sender, StylusEventArgs e) => CancelObserved(ContentInputBinding.StylusPointerId(e.StylusDevice));

    private void CancelObserved(long id)
    {
        if (_releasing || !_contacts.Remove(id, out var sample)) return;
        _chromeContacts.Remove(id);
        _input.Process(sample with { Action = PointerAction.Cancel, Sequence = _sequence++ });
        if (_gesture is not null && _gestureId == id) ReleaseGesture();
    }

    private void ReleaseGesture()
    {
        if (_gesture is null) return;
        _gesture = null;
        _root.ReleaseMouseCapture();
        _root.ReleaseStylusCapture();
        _root.ReleaseAllTouchCaptures();
    }

    public void Release()
    {
        if (_releasing) return;
        _releasing = true;
        try
        {
            ReleaseGesture();
            // Closing or hiding content removes its event source. Cancel its observed contacts too,
            // so the shared arbiter cannot retain a sequence whose Up will never reach that source.
            foreach (var sample in _contacts.Values)
                _input.Process(sample with { Action = PointerAction.Cancel, Sequence = _sequence++ });
            _chromeContacts.Clear();
            _contacts.Clear();
        }
        finally { _releasing = false; }
    }

    public void Dispose()
    {
        Release();
        _root.PreviewMouseDown -= MouseDown;
        _root.PreviewMouseMove -= MouseMove;
        _root.PreviewMouseUp -= MouseUp;
        _root.PreviewStylusDown -= StylusDown;
        _root.PreviewStylusMove -= StylusMove;
        _root.PreviewStylusUp -= StylusUp;
        _root.LostMouseCapture -= LostMouse;
        _root.LostStylusCapture -= LostStylus;
    }
}
