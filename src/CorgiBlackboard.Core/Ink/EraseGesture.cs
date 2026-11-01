using System.Collections.Immutable;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Model;
using CorgiBlackboard.Core.Operations;

namespace CorgiBlackboard.Core.Ink;

/// <summary>
/// Pending eraser state for one gesture. When the path grows, only strokes whose reach
/// overlaps the new segment are recomputed, each with <see cref="StrokeGeometry.Erase"/>
/// over the whole path, so the result equals <see cref="EraseOperation.Begin"/> for the
/// same path while per-sample work stays local. Nothing changes until the operation from
/// <see cref="CreateOperation"/> is committed.
/// </summary>
public sealed class EraseGesture
{
    private readonly Workspace _workspace;
    private readonly SurfaceId _surfaceId;
    private readonly ImmutableArray<StrokeElement> _strokes;
    private readonly (double Left, double Top, double Right, double Bottom)[] _reach;
    private readonly ImmutableArray<StrokeElement>?[] _results;
    private readonly List<PagePoint> _path = [];
    private readonly double _radius;
    private readonly double? _pageWidth;
    private readonly double? _pageHeight;

    public EraseGesture(
        Workspace workspace,
        SurfaceId surfaceId,
        SurfaceId? lockedPageSurfaceId,
        PagePoint start,
        double radius)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (surfaceId != workspace.BoardSurfaceId && lockedPageSurfaceId != surfaceId)
        {
            throw new ArgumentException("Document edits must lock the starting page surface.", nameof(lockedPageSurfaceId));
        }

        if (!double.IsFinite(radius) || radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius));
        }

        var page = workspace.Documents.Values
            .FirstOrDefault(document => document.PageSurfaceIds.Contains(surfaceId));
        _workspace = workspace;
        _surfaceId = surfaceId;
        _radius = radius;
        _pageWidth = page?.PixelWidth;
        _pageHeight = page?.PixelHeight;
        _strokes = workspace.GetElements(surfaceId).OfType<StrokeElement>().ToImmutableArray();
        _reach = new (double, double, double, double)[_strokes.Length];
        _results = new ImmutableArray<StrokeElement>?[_strokes.Length];
        for (var index = 0; index < _strokes.Length; index++)
        {
            _reach[index] = Reach(_strokes[index], radius);
        }

        _path.Add(start);
        Sweep(start, start);
    }

    public SurfaceId SurfaceId => _surfaceId;
    public IReadOnlyList<PagePoint> Path => _path;

    /// <summary>Appends a path point; returns true when the pending result changed.</summary>
    public bool Extend(PagePoint point)
    {
        var previous = _path[^1];
        if (previous == point)
        {
            return false;
        }

        _path.Add(point);
        return Sweep(previous, point);
    }

    /// <summary>A new pending operation for the current path, or null when nothing is hit.</summary>
    public EraseOperation? CreateOperation()
    {
        var before = ImmutableArray.CreateBuilder<StrokeElement>();
        var after = ImmutableArray.CreateBuilder<StrokeElement>();
        for (var index = 0; index < _strokes.Length; index++)
        {
            if (_results[index] is { } fragments)
            {
                before.Add(_strokes[index]);
                after.AddRange(fragments);
            }
        }

        if (before.Count == 0)
        {
            return null;
        }

        return new EraseOperation(_workspace, _surfaceId, before.ToImmutable(), after.ToImmutable());
    }

    private bool Sweep(PagePoint from, PagePoint to)
    {
        var left = Math.Min(from.X, to.X);
        var right = Math.Max(from.X, to.X);
        var top = Math.Min(from.Y, to.Y);
        var bottom = Math.Max(from.Y, to.Y);
        var changed = false;
        for (var index = 0; index < _strokes.Length; index++)
        {
            var reach = _reach[index];
            if (right < reach.Left || left > reach.Right || bottom < reach.Top || top > reach.Bottom)
            {
                continue;
            }

            var stroke = _strokes[index];
            var fragments = StrokeGeometry.Erase(stroke, _path, _radius, _pageWidth, _pageHeight);
            var untouched = fragments.Length == 1 && ReferenceEquals(fragments[0], stroke);
            var previous = _results[index];
            if (untouched)
            {
                changed |= previous is not null;
                _results[index] = null;
            }
            else if (previous is not { } kept || !SamePoints(kept, fragments))
            {
                // Keeping an equal previous result keeps fragment identities stable across samples.
                _results[index] = fragments;
                changed = true;
            }
        }

        return changed;
    }

    private static (double Left, double Top, double Right, double Bottom) Reach(StrokeElement stroke, double radius)
    {
        var left = double.PositiveInfinity;
        var top = double.PositiveInfinity;
        var right = double.NegativeInfinity;
        var bottom = double.NegativeInfinity;
        foreach (var point in stroke.Points)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }

        // Conservative broad phase: the narrow phase in StrokeGeometry decides every hit.
        var magnitude = Math.Max(Math.Max(Math.Abs(left), Math.Abs(right)), Math.Max(Math.Abs(top), Math.Abs(bottom)));
        var margin = radius + (stroke.Style.Width / 2) + (1e-6 * (1 + magnitude));
        return (left - margin, top - margin, right + margin, bottom + margin);
    }

    private static bool SamePoints(ImmutableArray<StrokeElement> first, ImmutableArray<StrokeElement> second)
    {
        if (first.Length != second.Length)
        {
            return false;
        }

        for (var index = 0; index < first.Length; index++)
        {
            if (!first[index].Points.SequenceEqual(second[index].Points))
            {
                return false;
            }
        }

        return true;
    }
}
