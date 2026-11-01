using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.App.Input;

/// <summary>
/// Routes real mouse, pen and touch contacts of one content element into the shared arbiter.
/// Stylus events are marked handled so WPF does not promote them; promoted mouse events that still
/// arrive are submitted as promoted and ignored by the arbiter. Contacts stay captured to the element
/// until their release. Leaving the element or a changed surface transform cancels a pending edit,
/// and an unexpected capture loss cancels that contact, so the release barrier cannot stall.
/// </summary>
public sealed class ContentInputBinding : IDisposable
{
    internal const long MousePointerId = 1;
    private const long StylusPointerBase = 1L << 32;

    private readonly UIElement _element;
    private readonly UIElement _root;
    private readonly WpfPointerAdapter _adapter;
    private readonly EditingSession _session;
    private readonly SurfaceId _surfaceId;
    private readonly Func<ViewportTransform> _transform;
    private readonly Dictionary<long, Contact> _contacts = [];

    /// <param name="element">The content element that receives and captures input.</param>
    /// <param name="coordinateRoot">The window-level element whose DIP space samples use.</param>
    /// <param name="context">The window's shared editing context.</param>
    /// <param name="surfaceId">The workspace surface edited through this element.</param>
    /// <param name="transform">Current surface-to-root transform of the element.</param>
    public ContentInputBinding(
        UIElement element,
        UIElement coordinateRoot,
        EditingContext context,
        SurfaceId surfaceId,
        Func<ViewportTransform> transform)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        _root = coordinateRoot ?? throw new ArgumentNullException(nameof(coordinateRoot));
        ArgumentNullException.ThrowIfNull(context);
        _adapter = context.Adapter;
        _session = context.Session;
        _surfaceId = surfaceId;
        _transform = transform ?? throw new ArgumentNullException(nameof(transform));
        element.MouseLeftButtonDown += OnMouseDown;
        element.MouseMove += OnMouseMove;
        element.MouseLeftButtonUp += OnMouseUp;
        element.LostMouseCapture += LostMouse;
        element.StylusDown += OnStylusDown;
        element.StylusMove += OnStylusMove;
        element.StylusUp += OnStylusUp;
        element.LostStylusCapture += LostStylus;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.StylusDevice is not null)
        {
            SubmitPromoted(PointerAction.Down, e.GetPosition(_root));
            return;
        }

        if (_contacts.ContainsKey(MousePointerId))
        {
            return;
        }

        _element.Focus();
        _adapter.CaptureMouse(_element);
        Begin(MousePointerId, PointerDeviceKind.Mouse, e.GetPosition(_root));
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.StylusDevice is not null)
        {
            SubmitPromoted(PointerAction.Move, e.GetPosition(_root));
        }
        else if (_contacts.ContainsKey(MousePointerId))
        {
            Move(MousePointerId, e.GetPosition(_root));
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (e.StylusDevice is not null)
        {
            SubmitPromoted(PointerAction.Up, e.GetPosition(_root));
        }
        else if (_contacts.ContainsKey(MousePointerId))
        {
            End(MousePointerId, e.GetPosition(_root));
            if (_element.IsMouseCaptured)
            {
                _element.ReleaseMouseCapture();
            }
        }
    }

    private void OnStylusDown(object sender, StylusDownEventArgs e)
    {
        e.Handled = true;
        var id = StylusPointerId(e.StylusDevice);
        if (_contacts.ContainsKey(id))
        {
            return;
        }

        if (_contacts.Count == 0)
        {
            _element.Focus();
        }

        _adapter.CaptureStylus(e.StylusDevice, _element);
        var kind = e.StylusDevice.TabletDevice?.Type == TabletDeviceType.Touch
            ? PointerDeviceKind.Touch
            : PointerDeviceKind.Stylus;
        Begin(id, kind, e.GetPosition(_root));
    }

    private void OnStylusMove(object sender, StylusEventArgs e)
    {
        var id = StylusPointerId(e.StylusDevice);
        if (!_contacts.ContainsKey(id))
        {
            return;
        }

        e.Handled = true;
        foreach (var point in e.GetStylusPoints(_root))
        {
            Move(id, new Point(point.X, point.Y));
        }
    }

    private void OnStylusUp(object sender, StylusEventArgs e)
    {
        var id = StylusPointerId(e.StylusDevice);
        if (!_contacts.ContainsKey(id))
        {
            return;
        }

        e.Handled = true;
        End(id, e.GetPosition(_root));
        if (ReferenceEquals(e.StylusDevice.Captured, _element))
        {
            e.StylusDevice.Capture(null);
        }
    }

    private void Begin(long id, PointerDeviceKind kind, Point position)
    {
        // A contact that starts outside the element (routed here by capture) is chrome for the arbiter.
        var onContent = IsInside(position);
        _contacts[id] = new Contact(kind, onContent, position);
        Submit(id, kind, PointerAction.Down, position, onContent);
    }

    private void Move(long id, Point position)
    {
        var contact = _contacts[id] with { Position = position };
        _contacts[id] = contact;
        Guard(contact, position);
        Submit(id, contact.Kind, PointerAction.Move, position, contact.OnContent);
    }

    private void End(long id, Point position)
    {
        _contacts.Remove(id, out var contact);
        Guard(contact, position);
        Submit(id, contact.Kind, PointerAction.Up, position, contact.OnContent);
    }

    private void CancelContact(long id)
    {
        if (!_contacts.Remove(id, out var contact))
        {
            return;
        }

        // While the window is being torn down there is no presentation source to map positions.
        if (PresentationSource.FromVisual(_root) is null)
        {
            return;
        }

        Submit(id, contact.Kind, PointerAction.Cancel, contact.Position, contact.OnContent);
    }

    private void Guard(Contact contact, Point position)
    {
        if (contact.OnContent)
        {
            _session.GuardContact(_surfaceId, IsInside(position), _transform());
        }
    }

    private void SubmitPromoted(PointerAction action, Point position) =>
        Submit(MousePointerId, PointerDeviceKind.Mouse, action, position, onContent: true, promoted: true);

    private void Submit(long id, PointerDeviceKind kind, PointerAction action, Point position, bool onContent, bool promoted = false) =>
        _adapter.Submit(
            _root,
            id,
            Stopwatch.GetTimestamp(),
            kind,
            action,
            _root.PointToScreen(position),
            onContent ? InputSurfaceKind.Content : InputSurfaceKind.Chrome,
            onContent ? _surfaceId : null,
            _transform(),
            promoted);

    private bool IsInside(Point rootPosition) =>
        _root.InputHitTest(rootPosition) is Visual hit
        && (ReferenceEquals(hit, _element) || _element.IsAncestorOf(hit));

    internal static long StylusPointerId(StylusDevice device) => StylusPointerBase + (uint)device.Id;

    private void LostMouse(object sender, MouseEventArgs args) => CancelContact(MousePointerId);
    private void LostStylus(object sender, StylusEventArgs args) => CancelContact(StylusPointerId(args.StylusDevice));

    public void Dispose()
    {
        foreach (var id in _contacts.Keys.ToArray()) CancelContact(id);
        _element.MouseLeftButtonDown -= OnMouseDown;
        _element.MouseMove -= OnMouseMove;
        _element.MouseLeftButtonUp -= OnMouseUp;
        _element.LostMouseCapture -= LostMouse;
        _element.StylusDown -= OnStylusDown;
        _element.StylusMove -= OnStylusMove;
        _element.StylusUp -= OnStylusUp;
        _element.LostStylusCapture -= LostStylus;
    }

    private readonly record struct Contact(PointerDeviceKind Kind, bool OnContent, Point Position);
}
