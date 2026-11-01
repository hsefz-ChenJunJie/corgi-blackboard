using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.Core.Documents;

// Screen-fixed window layout in teaching-area DIPs; it never reads or changes the board camera or content.
public sealed partial class WindowLayoutState
{
    public const double MinimumWidth = 240;
    public const double MinimumHeight = 180;
    public const double ResizeBorder = 8;
    public const double TitleBarHeight = 48;

    private readonly Workspace _workspace;
    private readonly Dictionary<DocumentId, ImageWindowLayout> _windows = [];
    private readonly List<DocumentId> _minimized = [];
    private readonly List<(WindowLayoutAction Action, DocumentId DocumentId)> _pending = [];
    private int _topZOrder;

    internal WindowLayoutState(Workspace workspace) => _workspace = workspace;

    public event Action<WindowLayoutChange>? Changed;

    public long Revision { get; private set; }
    public IReadOnlyDictionary<DocumentId, ImageWindowLayout> Windows => _windows;
    public IReadOnlyList<DocumentId> MinimizedWindows => _minimized.AsReadOnly();
    public (double Width, double Height)? TeachingArea { get; private set; }
    public DocumentId? FilledWindow => FilledLayout()?.DocumentId;

    public void SetTeachingArea(double width, double height)
    {
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(height) || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The teaching area must be finite with positive dimensions.");
        }

        if (TeachingArea is { } area && area.Width == width && area.Height == height)
        {
            return;
        }

        TeachingArea = (width, height);
        foreach (var documentId in _windows.Keys.ToArray())
        {
            Normalize(documentId);
            _pending.Add((WindowLayoutAction.Clamped, documentId));
        }

        Commit();
    }

    public void Open(DocumentId documentId, WindowRectangle rectangle)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            throw new ArgumentException("A default rectangle cannot open a window.", nameof(rectangle));
        }

        if (!_workspace.Documents.ContainsKey(documentId))
        {
            throw new ArgumentException("Only workspace documents can open a window.", nameof(documentId));
        }

        if (_windows.ContainsKey(documentId))
        {
            throw new InvalidOperationException("The document already has a window.");
        }

        RequireArea();
        _windows.Add(documentId, new ImageWindowLayout(documentId, rectangle, NextZOrder(), false, null));
        Normalize(documentId);
        _pending.Add((WindowLayoutAction.Opened, documentId));
        Commit();
    }

    public void Activate(DocumentId documentId)
    {
        var window = RequireVisible(documentId);
        if (_windows.Values.All(other =>
            other.DocumentId == documentId || other.IsMinimized || other.ZOrder < window.ZOrder))
        {
            return;
        }

        _windows[documentId] = window with { ZOrder = NextZOrder() };
        _pending.Add((WindowLayoutAction.Activated, documentId));
        Commit();
    }

    // Minimizing keeps fill mode and the page-local fill offset but frees the visible fill slot.
    public void Minimize(DocumentId documentId)
    {
        var window = RequireVisible(documentId);
        _windows[documentId] = window with { IsMinimized = true };
        _minimized.Add(documentId);
        _pending.Add((WindowLayoutAction.Minimized, documentId));
        Commit();
    }

    public void Restore(DocumentId documentId)
    {
        var window = RequireWindow(documentId);
        if (!window.IsMinimized)
        {
            throw new InvalidOperationException("Only a minimized window can be restored.");
        }

        if (window.IsFilled)
        {
            ReleaseFillSlot();
        }

        _windows[documentId] = window with { IsMinimized = false, ZOrder = NextZOrder() };
        ClampOffset(documentId);
        _minimized.Remove(documentId);
        _pending.Add((WindowLayoutAction.Restored, documentId));
        Commit();
    }

    public void EnterFill(DocumentId documentId)
    {
        var window = RequireVisible(documentId);
        if (window.IsFilled)
        {
            throw new InvalidOperationException("The window is already filled.");
        }

        var zOrder = NextZOrder();
        ReleaseFillSlot();
        var (offsetX, offsetY) = Offset(documentId);
        _windows[documentId] = window with
        {
            ZOrder = zOrder,
            NormalSnapshot = new NormalViewportSnapshot(window.ZOrder, offsetX, offsetY),
        };
        ClampOffset(documentId);
        _pending.Add((WindowLayoutAction.FillEntered, documentId));
        Commit();
    }

    public void ExitFill(DocumentId documentId)
    {
        var window = RequireVisible(documentId);
        if (window.NormalSnapshot is not { } snapshot)
        {
            throw new InvalidOperationException("The window is not filled.");
        }

        LeaveFill(window, snapshot);
        Commit();
    }

    internal void Close(DocumentId documentId)
    {
        RequireWindow(documentId);
        _windows.Remove(documentId);
        _minimized.Remove(documentId);
        _pending.Add((WindowLayoutAction.Closed, documentId));
        Commit();
    }

    private void ReleaseFillSlot()
    {
        if (FilledLayout() is { NormalSnapshot: { } snapshot } holder)
        {
            LeaveFill(holder, snapshot);
        }
    }

    private void LeaveFill(ImageWindowLayout window, NormalViewportSnapshot snapshot)
    {
        _windows[window.DocumentId] = window with { ZOrder = snapshot.ZOrder, NormalSnapshot = null };
        ClampOffset(window.DocumentId, (snapshot.OffsetX, snapshot.OffsetY));
        _pending.Add((WindowLayoutAction.FillExited, window.DocumentId));
    }

    private void Update(ImageWindowLayout window, WindowRectangle rectangle, WindowLayoutAction action)
    {
        if (rectangle == window.NormalRectangle)
        {
            return;
        }

        _windows[window.DocumentId] = window with { NormalRectangle = rectangle };
        ClampOffset(window.DocumentId);
        _pending.Add((action, window.DocumentId));
        Commit();
    }

    private void Normalize(DocumentId documentId)
    {
        var window = _windows[documentId];
        var rectangle = Fit(window.NormalRectangle);
        if (rectangle != window.NormalRectangle)
        {
            _windows[documentId] = window with { NormalRectangle = rectangle };
        }

        ClampOffset(documentId);
    }

    private void ClampOffset(DocumentId documentId, (double X, double Y)? requested = null)
    {
        var current = Offset(documentId);
        var (x, y) = requested ?? current;
        var (maxX, maxY) = MaximumOffset(_windows[documentId]);
        var clamped = (X: Math.Clamp(x, 0, maxX), Y: Math.Clamp(y, 0, maxY));
        if (clamped == current)
        {
            return;
        }

        _workspace.Reading.SetOffset(_workspace.Reading.CurrentPages[documentId], clamped.X, clamped.Y);
    }

    private (double X, double Y) Offset(DocumentId documentId) =>
        _workspace.Reading.Offsets.TryGetValue(_workspace.Reading.CurrentPages[documentId], out var offset)
            ? offset
            : (0, 0);

    private void Commit()
    {
        Revision++;
        var changes = _pending.Select(change => new WindowLayoutChange(change.Action, change.DocumentId, Revision)).ToArray();
        _pending.Clear();
        foreach (var change in changes)
        {
            Changed?.Invoke(change);
        }
    }

    private ImageWindowLayout RequireWindow(DocumentId documentId) =>
        _windows.TryGetValue(documentId, out var window)
            ? window
            : throw new InvalidOperationException("The document has no open window.");

    private ImageWindowLayout RequireVisible(DocumentId documentId)
    {
        var window = RequireWindow(documentId);
        return window.IsMinimized ? throw new InvalidOperationException("The window is minimized.") : window;
    }

    private ImageWindowLayout RequireNormal(DocumentId documentId)
    {
        var window = RequireVisible(documentId);
        return window.IsFilled ? throw new InvalidOperationException("A filled window cannot be moved or resized.") : window;
    }

    private (double Width, double Height) RequireArea() =>
        TeachingArea ?? throw new InvalidOperationException("The teaching area size is not known yet.");

    private int NextZOrder() => _topZOrder = checked(_topZOrder + 1);

    private ImageWindowLayout? FilledLayout() =>
        _windows.Values.SingleOrDefault(window => window.IsFilled && !window.IsMinimized);

    private static void RequireFinite(double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
        {
            throw new ArgumentOutOfRangeException(nameof(deltaX), "Window movement must be finite.");
        }
    }
}
