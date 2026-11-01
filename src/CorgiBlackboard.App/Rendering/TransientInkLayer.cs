using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.App.Rendering;

/// <summary>Overlay for the in-progress gesture preview; it never holds committed content.</summary>
public sealed class TransientInkLayer : FrameworkElement
{
    private readonly DrawingVisual _visual = new();

    public TransientInkLayer()
    {
        AddVisualChild(_visual);
        IsHitTestVisible = false;
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _visual : throw new ArgumentOutOfRangeException(nameof(index));

    public void Show(ImmutableArray<StrokeElement> strokes)
    {
        using var context = _visual.RenderOpen();
        foreach (var stroke in strokes)
        {
            StrokeRenderer.Draw(context, stroke);
        }
    }
}
