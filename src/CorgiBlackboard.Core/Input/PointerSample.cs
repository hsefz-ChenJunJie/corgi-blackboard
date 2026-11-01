using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.Core.Input;

public enum PointerDeviceKind
{
    Mouse,
    Stylus,
    Touch
}

public enum PointerAction
{
    Down,
    Move,
    Up,
    Cancel
}

public enum InputSurfaceKind
{
    Chrome,
    Content
}

public enum InputTool
{
    Ink,
    Eraser
}

public enum InputCancellationReason
{
    SecondContact,
    PointerCancelled,
    CaptureLost,
    FocusLost,
    Wheel,
    Resize,
    DpiChanged,
    FitChanged,
    LayoutChanged,
    ToolChanged,
    External,
    LeftSurface
}

public readonly record struct PointerSample(
    long PointerId,
    long Sequence,
    long Timestamp,
    ScreenPoint Position,
    PointerDeviceKind DeviceKind,
    PointerAction Action,
    InputSurfaceKind SurfaceKind,
    SurfaceId? SurfaceId,
    ViewportTransform Transform,
    bool IsPromotedMouse = false)
{
    public PointerSample Validate()
    {
        if (PointerId < 0 || Sequence < 0 || Timestamp < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PointerId), "Pointer identity, sequence, and timestamp must be nonnegative.");
        }

        if (SurfaceKind == InputSurfaceKind.Content && SurfaceId is null)
        {
            throw new ArgumentException("Content samples require a surface identity.", nameof(SurfaceId));
        }

        if (SurfaceKind == InputSurfaceKind.Content
            && (!double.IsFinite(Transform.Scale) || Transform.Scale <= 0
                || !double.IsFinite(Transform.OffsetX) || !double.IsFinite(Transform.OffsetY)))
        {
            throw new ArgumentException("Content samples require a valid viewport transform.", nameof(Transform));
        }

        return this;
    }
}

public enum InputArbiterState
{
    Idle,
    Editing,
    Commit,
    Cancel,
    Navigating,
    AwaitAllReleased
}
