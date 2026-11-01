using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.Core.Documents;

public sealed class ImageViewport(Workspace workspace, DocumentId documentId)
{
    public event Action? Changed;

    public ViewportTransform Transform(double originX = 0, double originY = 0)
    {
        var viewport = workspace.Layout.ContentViewport(documentId);
        var page = workspace.Reading.CurrentPages[documentId];
        var offset = workspace.Reading.Offsets.GetValueOrDefault(page);
        return new ViewportTransform(viewport.Scale,
            originX - offset.X * viewport.Scale, originY - offset.Y * viewport.Scale);
    }

    public void Scroll(double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
            throw new ArgumentOutOfRangeException(nameof(deltaX));
        var document = workspace.Documents[documentId];
        var viewport = workspace.Layout.ContentViewport(documentId);
        var page = workspace.Reading.CurrentPages[documentId];
        var offset = workspace.Reading.Offsets.GetValueOrDefault(page);
        var filled = workspace.Layout.Windows[documentId].IsFilled;
        var x = filled ? 0 : Math.Clamp(offset.X - deltaX / viewport.Scale,
            0, Math.Max(0, document.PixelWidth - viewport.Width));
        var y = Math.Clamp(offset.Y - deltaY / viewport.Scale,
            0, Math.Max(0, document.PixelHeight - viewport.Height / viewport.Scale));
        if ((x, y) != offset)
        {
            workspace.Reading.SetOffset(page, x, y);
            Changed?.Invoke();
        }
    }

    public void Wheel(int delta, bool horizontal) =>
        Scroll(horizontal ? delta * 48d / 120 : 0, horizontal ? 0 : delta * 48d / 120);

}
