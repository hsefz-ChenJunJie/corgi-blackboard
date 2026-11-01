using System.Windows;
using System.Windows.Input;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.App.Input;

public sealed class WpfPointerAdapter
{
    private readonly InputArbiter _arbiter;
    private long _sequence;

    public WpfPointerAdapter(InputArbiter arbiter) =>
        _arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));

    public bool CaptureMouse(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        HookLoss(element);
        return element.CaptureMouse();
    }

    public bool CaptureTouch(TouchDevice device, UIElement element)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(element);
        HookLoss(element);
        return device.Capture(element);
    }

    public bool CaptureStylus(StylusDevice device, UIElement element)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(element);
        HookLoss(element);
        return device.Capture(element);
    }

    public void Submit(
        UIElement coordinateRoot,
        long pointerId,
        long timestamp,
        PointerDeviceKind deviceKind,
        PointerAction action,
        Point physicalPosition,
        InputSurfaceKind surfaceKind,
        SurfaceId? surfaceId,
        ViewportTransform transform,
        bool isPromotedMouse = false)
    {
        ArgumentNullException.ThrowIfNull(coordinateRoot);
        var dip = coordinateRoot.PointFromScreen(physicalPosition);
        var sample = new PointerSample(
            pointerId,
            _sequence++,
            timestamp,
            new ScreenPoint(dip.X, dip.Y),
            deviceKind,
            action,
            surfaceKind,
            surfaceId,
            transform,
            isPromotedMouse);
        _arbiter.Process(sample);
    }

    public void LostCaptureOrFocus() => _arbiter.LostCaptureOrFocus();

    public void Interrupt(InputCancellationReason reason) => _arbiter.Interrupt(reason);

    private void HookLoss(UIElement element)
    {
        UnhookLoss(element);
        element.LostMouseCapture += OnLostMouseCapture;
        element.LostTouchCapture += OnLostTouchCapture;
        element.LostStylusCapture += OnLostStylusCapture;
        element.LostKeyboardFocus += OnLostFocus;
    }

    private void UnhookLoss(UIElement element)
    {
        element.LostMouseCapture -= OnLostMouseCapture;
        element.LostTouchCapture -= OnLostTouchCapture;
        element.LostStylusCapture -= OnLostStylusCapture;
        element.LostKeyboardFocus -= OnLostFocus;
    }

    private void OnLostMouseCapture(object? sender, MouseEventArgs args) => OnCaptureLoss(sender);
    private void OnLostTouchCapture(object? sender, TouchEventArgs args) => OnCaptureLoss(sender);
    private void OnLostStylusCapture(object? sender, StylusEventArgs args) => OnCaptureLoss(sender);
    private void OnLostFocus(object? sender, KeyboardFocusChangedEventArgs args) => OnLoss(sender, _arbiter.LostFocus);

    private void OnCaptureLoss(object? sender) => OnLoss(sender, _arbiter.LostCapture);

    private void OnLoss(object? sender, Action interrupt)
    {
        if (sender is UIElement element)
        {
            UnhookLoss(element);
        }

        interrupt();
    }
}
