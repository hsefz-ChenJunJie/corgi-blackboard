using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.Core.Input;

public enum InputObservationKind { Sample, Transition, Interruption }
public enum PointerDisposition { Accepted, Invalid, PromotedMouse, DuplicateDown, Orphan }

public readonly record struct InputObservation(
    InputObservationKind Kind,
    InputArbiterState Before,
    InputArbiterState After,
    int ContactsBefore,
    int ContactsAfter,
    InputTool Tool,
    SurfaceId? SurfaceId,
    PointerSample? Sample = null,
    PointerDisposition? Disposition = null,
    InputCancellationReason? Reason = null);

public sealed partial class InputArbiter
{
    // Platform-neutral observations at the entry shared by content adapters and window chrome.
    public event Action<InputObservation>? Observed;

    public void Process(PointerSample sample)
    {
        var before = State;
        var contacts = _contacts.Count;
        var surface = sample.SurfaceId ?? _startSurfaceId;
        var disposition = PointerDisposition.Invalid;
        try
        {
            sample.Validate();
            disposition = sample.IsPromotedMouse ? PointerDisposition.PromotedMouse : sample.Action switch
            {
                PointerAction.Down when _contacts.ContainsKey(sample.PointerId) => PointerDisposition.DuplicateDown,
                PointerAction.Down => PointerDisposition.Accepted,
                _ when _contacts.ContainsKey(sample.PointerId) => PointerDisposition.Accepted,
                _ => PointerDisposition.Orphan
            };
            if (disposition != PointerDisposition.Accepted) return;
            switch (sample.Action)
            {
                case PointerAction.Down: Down(sample); break;
                case PointerAction.Move: Move(sample); break;
                case PointerAction.Up: Up(sample); break;
                case PointerAction.Cancel: CancelPointer(sample.PointerId); break;
            }
        }
        finally
        {
            Observed?.Invoke(new InputObservation(InputObservationKind.Sample, before, State,
                contacts, _contacts.Count, Tool, surface, sample, disposition));
        }
    }

    public void Interrupt() => Interrupt(InputCancellationReason.External);

    public void Interrupt(InputCancellationReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        var before = State;
        var contacts = _contacts.Count;
        var surface = _startSurfaceId;
        if (State == InputArbiterState.Editing) CancelEdit(reason);
        _navigationFirst = null;
        _navigationSecond = null;
        State = _contacts.Count == 0 ? InputArbiterState.Idle : InputArbiterState.AwaitAllReleased;
        Observed?.Invoke(new InputObservation(InputObservationKind.Interruption, before, State,
            contacts, _contacts.Count, Tool, surface, Reason: reason));
    }
}
