using System.Collections.Immutable;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.Core.Ink;

public static class StrokeGeometry
{
    public static ImmutableArray<StrokeElement> Erase(
        StrokeElement stroke,
        IReadOnlyList<PagePoint> eraserPath,
        double eraserRadius,
        double? pageWidth = null,
        double? pageHeight = null)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        ArgumentNullException.ThrowIfNull(eraserPath);
        if (!double.IsFinite(eraserRadius) || eraserRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(eraserRadius));
        if ((pageWidth.HasValue && (!double.IsFinite(pageWidth.Value) || pageWidth.Value <= 0))
            || (pageHeight.HasValue && (!double.IsFinite(pageHeight.Value) || pageHeight.Value <= 0)))
            throw new ArgumentOutOfRangeException(nameof(pageWidth));
        if (eraserPath.Count == 0)
            return [stroke];

        var paths = ClipAndNormalize(stroke.Points, pageWidth, pageHeight);
        if (paths.Length == 0)
            return [stroke];

        var radius = eraserRadius + (stroke.Style.Width / 2);
        var sweptSegments = GetSegments(eraserPath);
        var erasedPaths = new List<ImmutableArray<PagePoint>>(paths.Length);
        var changed = false;
        foreach (var points in paths)
        {
            var source = new StrokeElement(stroke.Id, points, stroke.Style, stroke.Transform, stroke.SourceElementId);
            var result = ErasePath(source, sweptSegments, radius);
            if (result.Length != 1 || !ReferenceEquals(result[0], source))
                changed = true;
            erasedPaths.AddRange(result.Select(fragment => fragment.Points));
        }

        if (!changed)
            return [stroke];

        return erasedPaths.Select(points => new StrokeElement(
            ElementId.New(), points, stroke.Style, stroke.Transform, stroke.Id)).ToImmutableArray();
    }

    private static ImmutableArray<StrokeElement> ErasePath(
        StrokeElement stroke,
        (PagePoint Start, PagePoint End)[] sweepSegments,
        double radius)
    {
        if (stroke.Points.Length == 1)
        {
            return sweepSegments.Any(segment =>
                DistanceSquared(stroke.Points[0], segment.Start, segment.End) <= radius * radius)
                ? []
                : [stroke];
        }

        var cuts = new List<(double Start, double End)>[stroke.Points.Length - 1];
        for (var i = 0; i < cuts.Length; i++)
        {
            cuts[i] = [];
            var a = stroke.Points[i];
            var b = stroke.Points[i + 1];
            foreach (var (start, end) in sweepSegments)
                if (TryCapsuleInterval(a, b, start, end, radius, out var enter, out var exit))
                    cuts[i].Add((enter, exit));
        }
        if (cuts.All(intervals => intervals.Count == 0))
            return [stroke];

        var output = ImmutableArray.CreateBuilder<StrokeElement>();
        var fragment = new List<PagePoint>();
        for (var i = 0; i < cuts.Length; i++)
        {
            var a = stroke.Points[i];
            var b = stroke.Points[i + 1];
            var intervals = Merge(cuts[i]);
            var cursor = 0d;
            foreach (var interval in intervals)
            {
                if (interval.Start > cursor)
                {
                    Add(fragment, Interpolate(a, b, cursor));
                    Add(fragment, Interpolate(a, b, interval.Start));
                    Flush();
                }
                cursor = Math.Max(cursor, interval.End);
            }
            if (cursor < 1)
            {
                Add(fragment, Interpolate(a, b, cursor));
                Add(fragment, b);
            }
            if (intervals.Count > 0 && intervals[^1].End >= 1)
                Flush();
        }
        Flush();
        return output.ToImmutable();

        void Add(List<PagePoint> points, PagePoint point)
        {
            if (points.Count == 0 || points[^1] != point)
                points.Add(point);
        }

        void Flush()
        {
            if (fragment.Count >= 2 && fragment.Any(point => point != fragment[0]))
            {
                output.Add(new StrokeElement(
                    ElementId.New(), fragment.ToImmutableArray(), stroke.Style,
                    stroke.Transform, stroke.Id));
            }
            fragment.Clear();
        }
    }

    internal static ImmutableArray<ImmutableArray<PagePoint>> ClipAndNormalize(
        ImmutableArray<PagePoint> points,
        double? width,
        double? height)
    {
        var normalized = ImmutableArray.CreateBuilder<PagePoint>(points.Length);
        foreach (var point in points)
            if (normalized.Count == 0 || normalized[^1] != point)
                normalized.Add(point);
        points = normalized.ToImmutable();

        var paths = ImmutableArray.CreateBuilder<ImmutableArray<PagePoint>>();
        var current = ImmutableArray.CreateBuilder<PagePoint>();
        if (points.Length == 1)
        {
            if (Inside(points[0], width, height))
                paths.Add([points[0]]);
            return paths.ToImmutable();
        }

        for (var i = 1; i < points.Length; i++)
        {
            if (!ClipSegment(points[i - 1], points[i], width, height, out var start, out var end))
            {
                Flush();
                continue;
            }
            if (current.Count > 0 && current[^1] != start)
                Flush();
            Add(start);
            Add(end);
            if (end != points[i])
                Flush();
        }
        Flush();
        return paths.ToImmutable();

        void Add(PagePoint point)
        {
            if (current.Count == 0 || current[^1] != point)
                current.Add(point);
        }

        void Flush()
        {
            if (current.Count == 1)
                paths.Add([current[0]]);
            else if (current.Count >= 2)
                paths.Add(current.ToImmutable());
            current.Clear();
        }
    }

    private static bool ClipSegment(PagePoint a, PagePoint b, double? width, double? height,
        out PagePoint start, out PagePoint end)
    {
        var lower = 0d;
        var upper = 1d;
        if ((width.HasValue && !ClipAxis(a.X, b.X - a.X, 0, width, ref lower, ref upper))
            || (height.HasValue && !ClipAxis(a.Y, b.Y - a.Y, 0, height, ref lower, ref upper)))
        {
            start = end = default;
            return false;
        }
        start = Interpolate(a, b, lower);
        end = Interpolate(a, b, upper);
        return true;
    }

    private static bool ClipAxis(double origin, double delta, double minimum, double? maximum,
        ref double lower, ref double upper)
    {
        if (delta == 0)
            return origin >= minimum && (!maximum.HasValue || origin <= maximum.Value);
        if (!maximum.HasValue)
        {
            var crossing = (minimum - origin) / delta;
            if (delta > 0)
                lower = Math.Max(lower, crossing);
            else
                upper = Math.Min(upper, crossing);
            return lower <= upper;
        }
        var first = (minimum - origin) / delta;
        var second = (maximum.Value - origin) / delta;
        if (first > second)
            (first, second) = (second, first);
        lower = Math.Max(lower, first);
        upper = Math.Min(upper, second);
        return lower <= upper;
    }

    private static bool Inside(PagePoint point, double? width, double? height) =>
        (!width.HasValue || point.X >= 0 && point.X <= width.Value)
        && (!height.HasValue || point.Y >= 0 && point.Y <= height.Value);

    private static (PagePoint Start, PagePoint End)[] GetSegments(IReadOnlyList<PagePoint> path) =>
        path.Count == 1
            ? [(path[0], path[0])]
            : Enumerable.Range(1, path.Count - 1)
                .Select(i => (path[i - 1], path[i])).ToArray();

    private static bool TryCapsuleInterval(PagePoint a, PagePoint b, PagePoint c, PagePoint d,
        double radius, out double start, out double end)
    {
        var length = Math.Sqrt(SquaredDistance(a, b));
        var ux = (b.X - a.X) / length;
        var uy = (b.Y - a.Y) / length;
        var nx = -uy;
        var ny = ux;
        var cAlong = (c.X - a.X) * ux + (c.Y - a.Y) * uy;
        var dAlong = (d.X - a.X) * ux + (d.Y - a.Y) * uy;
        var cAcross = (c.X - a.X) * nx + (c.Y - a.Y) * ny;
        var dAcross = (d.X - a.X) * nx + (d.Y - a.Y) * ny;
        var sweepX = dAlong - cAlong;
        var sweepY = dAcross - cAcross;
        var sweepLengthSquared = sweepX * sweepX + sweepY * sweepY;
        var candidates = new List<double> { 0, length, cAlong, dAlong };
        AddQuadraticRoots(candidates, 1, -2 * cAlong,
            cAlong * cAlong + cAcross * cAcross - radius * radius);
        AddQuadraticRoots(candidates, 1, -2 * dAlong,
            dAlong * dAlong + dAcross * dAcross - radius * radius);
        if (sweepLengthSquared > 0 && sweepY != 0)
        {
            var center = cAlong - cAcross * sweepX / sweepY;
            var halfWidth = radius * Math.Sqrt(sweepLengthSquared) / Math.Abs(sweepY);
            candidates.Add(center - halfWidth);
            candidates.Add(center + halfWidth);
        }
        candidates.Sort();
        start = double.PositiveInfinity;
        end = double.NegativeInfinity;
        for (var i = 0; i < candidates.Count - 1; i++)
        {
            var left = Math.Clamp(candidates[i], 0, length);
            var right = Math.Clamp(candidates[i + 1], 0, length);
            if (right - left < 1e-14)
                continue;
            var along = (left + right) / 2;
            double nearestAlong;
            double nearestAcross;
            if (sweepLengthSquared == 0)
            {
                nearestAlong = cAlong;
                nearestAcross = cAcross;
            }
            else
            {
                var amount = Math.Clamp(((along - cAlong) * sweepX - cAcross * sweepY)
                    / sweepLengthSquared, 0, 1);
                nearestAlong = cAlong + amount * sweepX;
                nearestAcross = cAcross + amount * sweepY;
            }
            var acrossDelta = nearestAcross;
            if (Math.Pow(along - nearestAlong, 2) + acrossDelta * acrossDelta < radius * radius)
            {
                start = Math.Min(start, left / length);
                end = Math.Max(end, right / length);
            }
        }
        return double.IsFinite(start);
    }

    private static void AddQuadraticRoots(List<double> values, double a, double b, double c)
    {
        var discriminant = b * b - 4 * a * c;
        if (discriminant < 0)
            return;
        var root = Math.Sqrt(discriminant);
        values.Add((-b - root) / (2 * a));
        values.Add((-b + root) / (2 * a));
    }

    private static List<(double Start, double End)> Merge(List<(double Start, double End)> cuts)
    {
        if (cuts.Count == 0)
            return [];
        cuts.Sort((left, right) => left.Start.CompareTo(right.Start));
        var result = new List<(double Start, double End)> { cuts[0] };
        for (var i = 1; i < cuts.Count; i++)
        {
            var last = result[^1];
            if (cuts[i].Start <= last.End)
                result[^1] = (last.Start, Math.Max(last.End, cuts[i].End));
            else
                result.Add(cuts[i]);
        }
        return result;
    }

    private static PagePoint Interpolate(PagePoint a, PagePoint b, double amount) =>
        new(a.X + (b.X - a.X) * amount, a.Y + (b.Y - a.Y) * amount);

    private static double DistanceSquared(PagePoint point, PagePoint a, PagePoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var length = dx * dx + dy * dy;
        var amount = length == 0 ? 0
            : Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / length, 0, 1);
        var x = point.X - (a.X + amount * dx);
        var y = point.Y - (a.Y + amount * dy);
        return x * x + y * y;
    }

    private static double SquaredDistance(PagePoint a, PagePoint b) =>
        Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2);
}
