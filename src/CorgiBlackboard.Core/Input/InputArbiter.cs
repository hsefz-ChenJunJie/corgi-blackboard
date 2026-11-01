using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.Core.Input;

public sealed partial class InputArbiter
{
    private readonly Dictionary<long, PointerSample> _contacts = [];
    private InputTool _tool;
    private long? _editingPointer;
    private long? _navigationFirst;
    private long? _navigationSecond;
    private ScreenPoint _navigationCentroid;
    private InputTool _gestureTool;
    private SurfaceId? _startSurfaceId;
    private ViewportTransform _startTransform;

    public InputArbiterState State { get; private set; } = InputArbiterState.Idle;
    public InputTool Tool => _tool;
    public SurfaceId? LockedSurfaceId => _startSurfaceId;
    public ViewportTransform LockedTransform => _startTransform;

    public event Action<PointerSample, InputTool>? EditingStarted;
    public event Action<PointerSample, InputTool>? EditingSampled;
    public event Action<PointerSample, InputTool>? EditingCommitted;
    public event Action<InputTool, InputCancellationReason>? EditingCancelled;
    public event Action<SurfaceId, double, double>? NavigationDelta;

    public void SetTool(InputTool tool)
    {
        if (_tool == tool)
        {
            return;
        }

        _tool = tool;
        Interrupt(InputCancellationReason.ToolChanged);
    }

    public void LostCapture() => Interrupt(InputCancellationReason.CaptureLost);

    public void LostFocus() => Interrupt(InputCancellationReason.FocusLost);

    public void LostCaptureOrFocus() => LostFocus();

    private void Down(PointerSample sample)
    {
        if (_contacts.ContainsKey(sample.PointerId))
        {
            return;
        }

        _contacts.Add(sample.PointerId, sample);
        if (State == InputArbiterState.AwaitAllReleased)
        {
            return;
        }

        if (State == InputArbiterState.Idle)
        {
            if (_contacts.Count > 1)
            {
                State = InputArbiterState.AwaitAllReleased;
                return;
            }

            if (sample.SurfaceKind == InputSurfaceKind.Content)
            {
                _editingPointer = sample.PointerId;
                _startSurfaceId = sample.SurfaceId;
                _startTransform = sample.Transform;
                _gestureTool = _tool;
                State = InputArbiterState.Editing;
                EditingStarted?.Invoke(sample, _gestureTool);
            }

            return;
        }

        if (State != InputArbiterState.Editing)
        {
            return;
        }

        var first = _contacts[_editingPointer!.Value];
        var eligible = first.SurfaceKind == InputSurfaceKind.Content
            && sample.SurfaceKind == InputSurfaceKind.Content
            && first.SurfaceId == sample.SurfaceId
            && first.SurfaceId == _startSurfaceId;
        CancelEdit(InputCancellationReason.SecondContact);
        if (!eligible || _startSurfaceId is null)
        {
            State = InputArbiterState.AwaitAllReleased;
            return;
        }

        _navigationFirst = first.PointerId;
        _navigationSecond = sample.PointerId;
        _navigationCentroid = Midpoint(first.Position, sample.Position);
        State = InputArbiterState.Navigating;
    }

    private void Move(PointerSample sample)
    {
        if (!_contacts.ContainsKey(sample.PointerId))
        {
            return;
        }

        _contacts[sample.PointerId] = sample;
        if (State == InputArbiterState.Editing && sample.PointerId == _editingPointer)
        {
            EditingSampled?.Invoke(sample, _gestureTool);
        }
        else if (State == InputArbiterState.Navigating
            && (sample.PointerId == _navigationFirst || sample.PointerId == _navigationSecond))
        {
            var centroid = Midpoint(
                _contacts[_navigationFirst!.Value].Position,
                _contacts[_navigationSecond!.Value].Position);
            NavigationDelta?.Invoke(_startSurfaceId!.Value,
                centroid.X - _navigationCentroid.X,
                centroid.Y - _navigationCentroid.Y);
            _navigationCentroid = centroid;
        }
    }

    private void Up(PointerSample sample)
    {
        if (!_contacts.ContainsKey(sample.PointerId))
        {
            return;
        }

        _contacts.Remove(sample.PointerId);
        if (State == InputArbiterState.Editing && sample.PointerId == _editingPointer)
        {
            EditingSampled?.Invoke(sample, _gestureTool);
            State = InputArbiterState.Commit;
            Observed?.Invoke(new InputObservation(InputObservationKind.Transition,
                InputArbiterState.Editing, State, _contacts.Count, _contacts.Count, Tool, _startSurfaceId));
            EditingCommitted?.Invoke(sample, _gestureTool);
            ClearGesture();
            State = _contacts.Count == 0 ? InputArbiterState.Idle : InputArbiterState.AwaitAllReleased;
        }
        else if (State == InputArbiterState.Navigating
            && (sample.PointerId == _navigationFirst || sample.PointerId == _navigationSecond))
        {
            _navigationFirst = null;
            _navigationSecond = null;
            State = InputArbiterState.AwaitAllReleased;
        }
        else if (State == InputArbiterState.AwaitAllReleased && _contacts.Count == 0)
        {
            ClearGesture();
            State = InputArbiterState.Idle;
        }
        else if (State == InputArbiterState.Idle && _contacts.Count == 0)
        {
            ClearGesture();
        }
    }

    private void CancelPointer(long pointerId)
    {
        if (!_contacts.Remove(pointerId))
        {
            return;
        }

        if (State == InputArbiterState.Editing)
        {
            CancelEdit(InputCancellationReason.PointerCancelled);
        }

        _navigationFirst = null;
        _navigationSecond = null;
        State = _contacts.Count == 0 ? InputArbiterState.Idle : InputArbiterState.AwaitAllReleased;
        if (_contacts.Count == 0)
        {
            ClearGesture();
        }
    }

    private void CancelEdit(InputCancellationReason reason)
    {
        var before = State;
        State = InputArbiterState.Cancel;
        EditingCancelled?.Invoke(_gestureTool, reason);
        Observed?.Invoke(new InputObservation(InputObservationKind.Transition,
            before, State, _contacts.Count, _contacts.Count, Tool, _startSurfaceId, Reason: reason));
        _editingPointer = null;
    }

    private void ClearGesture()
    {
        _editingPointer = null;
        _navigationFirst = null;
        _navigationSecond = null;
        _startSurfaceId = null;
        _startTransform = default;
        _gestureTool = default;
    }

    private static ScreenPoint Midpoint(ScreenPoint first, ScreenPoint second) =>
        new((first.X + second.X) / 2, (first.Y + second.Y) / 2);
}
