using System.Windows;
using System.Windows.Media;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.App.Rendering;

/// <summary>
/// Draws one stroke in world DIP as a frozen round-capped <see cref="StreamGeometry"/>, or a dot for
/// a single-point stroke. Element transforms are identity in phase one: ink and the Core eraser both
/// work on raw stroke points.
/// </summary>
internal static class StrokeRenderer
{
    private static readonly Dictionary<uint, SolidColorBrush> BrushCache = [];

    public static void Draw(DrawingContext context, StrokeElement stroke)
    {
        var brush = Brush(stroke.Style.Argb);
        var points = stroke.Points;
        var first = new Point(points[0].X, points[0].Y);
        if (points.All(point => point == points[0]))
        {
            var radius = stroke.Style.Width / 2;
            context.DrawEllipse(brush, null, first, radius, radius);
            return;
        }

        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(first, isFilled: false, isClosed: false);
            for (var index = 1; index < points.Length; index++)
            {
                sink.LineTo(new Point(points[index].X, points[index].Y), isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        var pen = new Pen(brush, stroke.Style.Width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static SolidColorBrush Brush(uint argb)
    {
        if (!BrushCache.TryGetValue(argb, out var brush))
        {
            brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            BrushCache.Add(argb, brush);
        }

        return brush;
    }
}
