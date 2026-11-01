using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.Core.Documents;

public sealed partial class WindowLayoutState
{
    public void Move(DocumentId documentId, double deltaX, double deltaY)
    {
        RequireFinite(deltaX, deltaY);
        var window = RequireNormal(documentId);
        var (areaWidth, areaHeight) = RequireArea();
        var rectangle = window.NormalRectangle;
        Update(window, new WindowRectangle(
            ClampStart(rectangle.X + deltaX, rectangle.Width, areaWidth),
            ClampStart(rectangle.Y + deltaY, rectangle.Height, areaHeight),
            rectangle.Width, rectangle.Height), WindowLayoutAction.Moved);
    }

    public void Resize(DocumentId documentId, WindowResizeEdge edge, double deltaX, double deltaY)
    {
        if (!Enum.IsDefined(edge))
            throw new ArgumentOutOfRangeException(nameof(edge), "Unknown resize edge.");
        RequireFinite(deltaX, deltaY);
        var window = RequireNormal(documentId);
        var (areaWidth, areaHeight) = RequireArea();
        var rectangle = window.NormalRectangle;
        var (x, width) = edge switch
        {
            WindowResizeEdge.Left or WindowResizeEdge.TopLeft or WindowResizeEdge.BottomLeft =>
                MoveStartEdge(rectangle.X, rectangle.Width, deltaX, MinimumWidth),
            WindowResizeEdge.Right or WindowResizeEdge.TopRight or WindowResizeEdge.BottomRight =>
                MoveEndEdge(rectangle.X, rectangle.Width, deltaX, MinimumWidth, areaWidth),
            _ => (rectangle.X, rectangle.Width),
        };
        var (y, height) = edge switch
        {
            WindowResizeEdge.Top or WindowResizeEdge.TopLeft or WindowResizeEdge.TopRight =>
                MoveStartEdge(rectangle.Y, rectangle.Height, deltaY, MinimumHeight),
            WindowResizeEdge.Bottom or WindowResizeEdge.BottomLeft or WindowResizeEdge.BottomRight =>
                MoveEndEdge(rectangle.Y, rectangle.Height, deltaY, MinimumHeight, areaHeight),
            _ => (rectangle.Y, rectangle.Height),
        };
        Update(window, new WindowRectangle(x, y, width, height), WindowLayoutAction.Resized);
    }

    public WindowRectangle DisplayRectangle(DocumentId documentId)
    {
        var window = RequireWindow(documentId);
        var area = RequireArea();
        return window.IsFilled
            ? new WindowRectangle(0, 0, area.Width, area.Height)
            : window.NormalRectangle;
    }

    public (double Width, double Height, double Scale) ContentViewport(DocumentId documentId)
    {
        var window = RequireWindow(documentId);
        var rectangle = DisplayRectangle(documentId);
        var inset = window.IsFilled ? 0 : ResizeBorder;
        var width = Math.Max(0, rectangle.Width - 2 * inset);
        var height = Math.Max(0, rectangle.Height - 2 * inset - TitleBarHeight);
        var scale = window.IsFilled ? width / _workspace.Documents[documentId].PixelWidth : 1;
        return (width, height, scale);
    }

    private (double X, double Y) MaximumOffset(ImageWindowLayout window)
    {
        var document = _workspace.Documents[window.DocumentId];
        var viewport = ContentViewport(window.DocumentId);
        return (window.IsFilled ? 0 : Math.Max(0, document.PixelWidth - viewport.Width),
            Math.Max(0, document.PixelHeight - viewport.Height / viewport.Scale));
    }

    private WindowRectangle Fit(WindowRectangle rectangle)
    {
        var (areaWidth, areaHeight) = RequireArea();
        var width = Math.Clamp(rectangle.Width, Math.Min(MinimumWidth, areaWidth), areaWidth);
        var height = Math.Clamp(rectangle.Height, Math.Min(MinimumHeight, areaHeight), areaHeight);
        return new WindowRectangle(
            ClampStart(rectangle.X, width, areaWidth),
            ClampStart(rectangle.Y, height, areaHeight), width, height);
    }

    private static (double Start, double Length) MoveStartEdge(double start, double length, double delta, double minimum)
    {
        var applied = Math.Clamp(delta, -start, Math.Max(0, length - Math.Min(minimum, length)));
        return (start + applied, length - applied);
    }

    private static (double Start, double Length) MoveEndEdge(
        double start, double length, double delta, double minimum, double limit)
    {
        var applied = Math.Clamp(delta, Math.Min(minimum, length) - length, Math.Max(0, limit - start - length));
        return (start, length + applied);
    }

    private static double ClampStart(double start, double length, double limit) =>
        Math.Clamp(start, 0, Math.Max(0, limit - length));
}
