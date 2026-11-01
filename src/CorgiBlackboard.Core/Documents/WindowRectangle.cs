using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.Core.Documents;

public readonly record struct WindowRectangle
{
    public WindowRectangle(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || !double.IsFinite(width) || width <= 0
            || !double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Window bounds must be finite with positive dimensions.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
}

// The rectangle stays on the layout; this snapshot survives a filled window being minimized.
public sealed record NormalViewportSnapshot(int ZOrder, double OffsetX, double OffsetY);

public sealed record ImageWindowLayout(
    DocumentId DocumentId,
    WindowRectangle NormalRectangle,
    int ZOrder,
    bool IsMinimized,
    NormalViewportSnapshot? NormalSnapshot)
{
    public bool IsFilled => NormalSnapshot is not null;
}

public enum WindowResizeEdge { Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

public enum WindowLayoutAction { Opened, Activated, Moved, Resized, Minimized, Restored, FillEntered, FillExited, Clamped, Closed }

public sealed record WindowLayoutChange(WindowLayoutAction Action, DocumentId DocumentId, long Revision);
