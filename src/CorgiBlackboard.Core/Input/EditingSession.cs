using System.Collections.Immutable;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Ink;
using CorgiBlackboard.Core.Model;
using CorgiBlackboard.Core.Operations;

namespace CorgiBlackboard.Core.Input;

/// <summary>
/// Composes <see cref="InputArbiter"/> with real pending <see cref="Workspace"/> transactions:
/// a single-contact ink gesture keeps one pending <see cref="ContentOperation"/>, an eraser
/// gesture keeps one pending <see cref="EraseOperation"/>, and only a legal release commits.
/// Any cancellation drops the pending operation, so committed content is never touched.
/// </summary>
/// <remarks>
/// Composition seam: the app root owns exactly one <see cref="Workspace"/>, one arbiter and one
/// session. Every content surface (the board now, document pages later) submits samples to that
/// arbiter with its own <see cref="SurfaceId"/> and transform, reports leave/transform changes via
/// <see cref="GuardContact"/> and surface-local cancellations via <see cref="InterruptSurface"/>,
/// renders <see cref="Workspace.GetElements"/> on <see cref="ContentChanged"/>, and renders the
/// pending preview on <see cref="PendingChanged"/>. Tool settings change only through <see cref="Select"/>.
/// </remarks>
public sealed class EditingSession
{
    /// <summary>One wheel notch (delta 120) pans the board by 48 DIP.</summary>
    public const double WheelNotchDip = 48;
    public const double WheelNotchDelta = 120;

    private readonly InputArbiter _arbiter;
    private readonly List<PagePoint> _inkPoints = [];
    private readonly List<ElementId> _inkFragmentIds = [];
    private SurfaceId? _gestureSurface;
    private ElementId _inkId;
    private StrokeStyle _inkStyle;
    private ContentOperation? _pendingInk;
    private EraseGesture? _eraseGesture;
    private EraseOperation? _pendingErase;

    public EditingSession(Workspace workspace, InputArbiter arbiter, ToolSettings settings)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _arbiter = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        _arbiter.SetTool(settings.Tool);
        _arbiter.EditingStarted += OnStarted;
        _arbiter.EditingSampled += (sample, _) => OnSampled(sample);
        _arbiter.EditingCommitted += (_, _) => Commit();
        _arbiter.EditingCancelled += (_, _) => Discard();
        _arbiter.NavigationDelta += OnNavigation;
    }

    public Workspace Workspace { get; }
    public ToolSettings Settings { get; private set; }

    /// <summary>Surface of the in-progress gesture preview, or null when nothing is pending.</summary>
    public SurfaceId? PendingSurfaceId => _pendingInk?.SurfaceId ?? _pendingErase?.SurfaceId;

    /// <summary>Committed strokes the pending preview replaces (hidden while pending).</summary>
    public ImmutableArray<StrokeElement> PendingBefore =>
        _pendingErase?.Before ?? ImmutableArray<StrokeElement>.Empty;

    /// <summary>Strokes the pending preview would commit.</summary>
    public ImmutableArray<StrokeElement> PendingAfter =>
        _pendingInk?.After.Cast<StrokeElement>().ToImmutableArray()
        ?? _pendingErase?.After ?? ImmutableArray<StrokeElement>.Empty;

    public event Action? PendingChanged;
    public event Action<SurfaceId>? ContentChanged;
    public event Action? CameraChanged;
    public event Action<SurfaceId>? ReadingChanged;

    /// <summary>World-to-root transform of the board whose layout origin is at (originX, originY) DIP.</summary>
    public ViewportTransform BoardTransform(double originX, double originY)
    {
        var camera = Workspace.Camera.Transform;
        return new ViewportTransform(camera.Scale, camera.OffsetX + originX, camera.OffsetY + originY);
    }

    /// <summary>Applies the shared toolbar selection; a tool switch cancels a pending edit.</summary>
    public void Select(ToolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        _arbiter.SetTool(settings.Tool);
    }

    /// <summary>
    /// Reports where a content contact is before its sample is submitted. Leaving the starting
    /// surface or a changed surface transform cancels the pending edit for the locked surface.
    /// </summary>
    public void GuardContact(SurfaceId surfaceId, bool insideSurface, ViewportTransform transform)
    {
        if (_arbiter.State != InputArbiterState.Editing || _arbiter.LockedSurfaceId != surfaceId)
        {
            return;
        }

        if (!insideSurface)
        {
            _arbiter.Interrupt(InputCancellationReason.LeftSurface);
        }
        else if (transform != _arbiter.LockedTransform)
        {
            _arbiter.Interrupt(InputCancellationReason.LayoutChanged);
        }
    }

    /// <summary>Cancels the current gesture only when it is locked to <paramref name="surfaceId"/>.</summary>
    public void InterruptSurface(SurfaceId surfaceId, InputCancellationReason reason)
    {
        if (_arbiter.LockedSurfaceId == surfaceId)
        {
            _arbiter.Interrupt(reason);
        }
    }

    /// <summary>
    /// Board wheel navigation: 120 delta pans 48 DIP, positive delta moves content down (or right
    /// with <paramref name="horizontal"/>). Fractional deltas are kept as doubles.
    /// </summary>
    public void Wheel(int delta, bool horizontal)
    {
        if (delta == 0)
        {
            return;
        }

        InterruptSurface(Workspace.BoardSurfaceId, InputCancellationReason.Wheel);
        var distance = delta * WheelNotchDip / WheelNotchDelta;
        if (horizontal)
        {
            Workspace.Camera.Pan(distance, 0);
        }
        else
        {
            Workspace.Camera.Pan(0, distance);
        }

        CameraChanged?.Invoke();
    }

    private void OnNavigation(SurfaceId surfaceId, double deltaX, double deltaY)
    {
        if (surfaceId != Workspace.BoardSurfaceId)
        {
            var document = Workspace.Documents.Values.Single(document => document.PageSurfaceIds.Contains(surfaceId));
            new ImageViewport(Workspace, document.Id).Scroll(deltaX, deltaY);
            ReadingChanged?.Invoke(surfaceId);
            return;
        }

        Workspace.Camera.Pan(deltaX, deltaY);
        CameraChanged?.Invoke();
    }

    private void OnStarted(PointerSample sample, InputTool tool)
    {
        var surfaceId = sample.SurfaceId!.Value;
        SurfaceId? lockedPage = surfaceId == Workspace.BoardSurfaceId ? null : surfaceId;
        var scale = _arbiter.LockedTransform.Scale;
        var point = _arbiter.LockedTransform.ToPage(sample.Position);
        _gestureSurface = surfaceId;
        if (tool == InputTool.Ink)
        {
            _inkId = ElementId.New();
            _inkStyle = new StrokeStyle(Settings.InkArgb, Settings.InkWidth / scale);
            _inkPoints.Add(point);
            RebuildInk(lockedPage);
        }
        else
        {
            _eraseGesture = new EraseGesture(Workspace, surfaceId, lockedPage, point, Settings.EraserDiameter / 2 / scale);
            _pendingErase = _eraseGesture.CreateOperation();
        }

        PendingChanged?.Invoke();
    }

    private void OnSampled(PointerSample sample)
    {
        var point = _arbiter.LockedTransform.ToPage(sample.Position);
        if (_eraseGesture is { } gesture)
        {
            if (!gesture.Extend(point))
            {
                return;
            }

            _pendingErase?.Cancel();
            _pendingErase = gesture.CreateOperation();
        }
        else
        {
            if (_inkPoints[^1] == point)
            {
                return;
            }

            _inkPoints.Add(point);
            var surfaceId = _gestureSurface!.Value;
            RebuildInk(surfaceId == Workspace.BoardSurfaceId ? null : surfaceId);
        }

        PendingChanged?.Invoke();
    }

    private void RebuildInk(SurfaceId? lockedPage)
    {
        _pendingInk?.Cancel();
        if (lockedPage is { } page)
        {
            var document = Workspace.Documents.Values.Single(document => document.PageSurfaceIds.Contains(page));
            var paths = StrokeGeometry.ClipAndNormalize([.. _inkPoints], document.PixelWidth, document.PixelHeight);
            while (_inkFragmentIds.Count < paths.Length)
                _inkFragmentIds.Add(_inkFragmentIds.Count == 0 ? _inkId : ElementId.New());
            var strokes = paths.Select((points, index) =>
                new StrokeElement(_inkFragmentIds[index], points, _inkStyle, ElementTransform.Identity)).ToImmutableArray<Element>();
            _pendingInk = strokes.IsEmpty ? null
                : new ContentOperation(Workspace, ContentOperationId.New(), page, [], strokes);
            return;
        }
        var stroke = new StrokeElement(_inkId, [.. _inkPoints], _inkStyle, ElementTransform.Identity);
        _pendingInk = Workspace.BeginStroke(_gestureSurface!.Value, lockedPage, stroke);
    }

    private void Commit()
    {
        var surfaceId = _gestureSurface;
        var ink = _pendingInk;
        var erase = _pendingErase;
        ClearPending();
        ink?.Commit();
        erase?.Commit();
        if (surfaceId is { } committed && (ink is not null || erase is not null))
        {
            ContentChanged?.Invoke(committed);
        }

        PendingChanged?.Invoke();
    }

    private void Discard()
    {
        _pendingInk?.Cancel();
        _pendingErase?.Cancel();
        ClearPending();
        PendingChanged?.Invoke();
    }

    private void ClearPending()
    {
        _pendingInk = null;
        _pendingErase = null;
        _eraseGesture = null;
        _inkPoints.Clear();
        _inkFragmentIds.Clear();
        _gestureSurface = null;
    }
}
