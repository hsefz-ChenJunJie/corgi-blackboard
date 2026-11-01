using System.Collections.Immutable;
using CorgiBlackboard.Core.Coordinates;

namespace CorgiBlackboard.Core.Model;

public readonly record struct ElementTransform
{
    public static ElementTransform Identity => new(1, 1, 0, 0, 0);

    public ElementTransform(double scaleX, double scaleY, double rotationDegrees, double translateX, double translateY)
    {
        if (!double.IsFinite(scaleX) || scaleX == 0
            || !double.IsFinite(scaleY) || scaleY == 0
            || !double.IsFinite(rotationDegrees)
            || !double.IsFinite(translateX) || !double.IsFinite(translateY))
        {
            throw new ArgumentOutOfRangeException(nameof(scaleX), "Element transform values must be finite and scales nonzero.");
        }

        ScaleX = scaleX;
        ScaleY = scaleY;
        RotationDegrees = rotationDegrees;
        TranslateX = translateX;
        TranslateY = translateY;
    }

    public double ScaleX { get; }
    public double ScaleY { get; }
    public double RotationDegrees { get; }
    public double TranslateX { get; }
    public double TranslateY { get; }

    public bool IsValid =>
        double.IsFinite(ScaleX) && ScaleX != 0
        && double.IsFinite(ScaleY) && ScaleY != 0
        && double.IsFinite(RotationDegrees)
        && double.IsFinite(TranslateX) && double.IsFinite(TranslateY);
}

public readonly record struct StrokeStyle
{
    public StrokeStyle(uint argb, double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Stroke width must be finite and positive.");
        }

        Argb = argb;
        Width = width;
    }

    public uint Argb { get; }
    public double Width { get; }
}

public abstract record Element
{
    protected Element(ElementId id, ElementTransform transform)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Element ID must not be empty.", nameof(id));
        }

        Id = id;
        Transform = transform;
    }

    public ElementId Id { get; }
    public ElementTransform Transform { get; }
}

public sealed record StrokeElement : Element
{
    public StrokeElement(
        ElementId id,
        ImmutableArray<PagePoint> points,
        StrokeStyle style,
        ElementTransform transform,
        ElementId? sourceElementId = null) : base(id, transform)
    {
        if (points.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A stroke must contain at least one point.", nameof(points));
        }

        Points = points;
        Style = style;
        SourceElementId = sourceElementId;
    }

    public ImmutableArray<PagePoint> Points { get; }
    public StrokeStyle Style { get; }
    public ElementId? SourceElementId { get; }
    public StrokeBounds Bounds => CalculateBounds(Points, Style, Transform);

    private static StrokeBounds CalculateBounds(
        ImmutableArray<PagePoint> points,
        StrokeStyle style,
        ElementTransform transform)
    {
        if (!transform.IsValid)
        {
            throw new ArgumentException("Stroke transform must be valid.", nameof(transform));
        }

        var radians = transform.RotationDegrees * (Math.PI / 180);
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var radius = style.Width / 2;
        var extentX = radius * Math.Sqrt(
            Math.Pow(cosine * transform.ScaleX, 2)
            + Math.Pow(sine * transform.ScaleY, 2));
        var extentY = radius * Math.Sqrt(
            Math.Pow(sine * transform.ScaleX, 2)
            + Math.Pow(cosine * transform.ScaleY, 2));
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;

        foreach (var point in points)
        {
            var scaledX = point.X * transform.ScaleX;
            var scaledY = point.Y * transform.ScaleY;
            var x = (scaledX * cosine) - (scaledY * sine) + transform.TranslateX;
            var y = (scaledX * sine) + (scaledY * cosine) + transform.TranslateY;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return new StrokeBounds(
            minX - extentX,
            minY - extentY,
            maxX + extentX,
            maxY + extentY);
    }
}

public readonly record struct StrokeBounds(double Left, double Top, double Right, double Bottom);
