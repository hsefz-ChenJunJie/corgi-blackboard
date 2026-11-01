using System.Globalization;
using CorgiBlackboard.Core.Diagnostics;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;
using CorgiBlackboard.Core.Operations;
using CorgiBlackboard.Infrastructure.Diagnostics;

namespace CorgiBlackboard.App.Diagnostics;

// One window owns this subscription and its existing session. No QA flag or WPF dependency.
internal sealed class WorkspaceDiagnostics : IDisposable
{
    private readonly Workspace _workspace;
    private readonly InputArbiter _input;
    private readonly DiagnosticSession _session;

    internal WorkspaceDiagnostics(Workspace workspace, InputArbiter input, DiagnosticSession session)
    {
        _workspace = workspace;
        _input = input;
        _session = session;
        input.Observed += InputObserved;
        workspace.ContentChanged += ContentChanged;
        workspace.Reading.Changed += ReadingChanged;
        workspace.Layout.Changed += LayoutChanged;
        workspace.Camera.Changed += CameraChanged;
    }

    private void InputObserved(InputObservation observed)
    {
        var data = new Dictionary<string, string>
        {
            ["before"] = observed.Before.ToString(), ["after"] = observed.After.ToString(),
            ["contactsBefore"] = observed.ContactsBefore.ToString(CultureInfo.InvariantCulture),
            ["contactsAfter"] = observed.ContactsAfter.ToString(CultureInfo.InvariantCulture),
            ["tool"] = observed.Tool.ToString()
        };
        if (observed.Disposition is { } disposition) data["disposition"] = disposition.ToString();
        if (observed.Reason is { } reason) data["reason"] = reason.ToString();
        DiagnosticPointerSample[]? samples = null;
        string? device = null;
        if (observed.Sample is { } sample)
        {
            device = $"{sample.DeviceKind}:{sample.PointerId}";
            samples = [new(device, sample.Action.ToString(), sample.Position.X, sample.Position.Y, sample.Timestamp)];
            data["sourceSequence"] = sample.Sequence.ToString(CultureInfo.InvariantCulture);
            data["surfaceKind"] = sample.SurfaceKind.ToString();
            data["promotedMouse"] = sample.IsPromotedMouse.ToString();
            data["scale"] = sample.Transform.Scale.ToString("R", CultureInfo.InvariantCulture);
            data["offsetX"] = sample.Transform.OffsetX.ToString("R", CultureInfo.InvariantCulture);
            data["offsetY"] = sample.Transform.OffsetY.ToString("R", CultureInfo.InvariantCulture);
        }
        _session.TryRecord(new DiagnosticEvent(
            observed.Kind switch
            {
                InputObservationKind.Sample => "InputSample",
                InputObservationKind.Transition => "InputTransition",
                InputObservationKind.Interruption => "InputInterrupted",
                _ => throw new ArgumentOutOfRangeException(nameof(observed))
            },
            observed.Kind == InputObservationKind.Sample ? DiagnosticKind.PointerSample : DiagnosticKind.PointerTransition,
            SurfaceId: observed.SurfaceId?.Value.ToString("D"), DeviceId: device, PointerSamples: samples, Data: data));
    }

    private void ContentChanged(ContentOperation operation) =>
        _session.TryRecord(new DiagnosticEvent("ContentApplied", DiagnosticKind.Content,
            OperationId: operation.Id.Value.ToString("D"), SurfaceId: operation.SurfaceId.Value.ToString("D"),
            Data: new Dictionary<string, string>
            {
                ["contentRevision"] = _workspace.ContentRevision.ToString(CultureInfo.InvariantCulture),
                ["beforeIds"] = string.Join(",", operation.Before.Select(element => element.Id.Value)),
                ["afterIds"] = string.Join(",", operation.After.Select(element => element.Id.Value))
            }));

    private void ReadingChanged(SurfaceId surface)
    {
        var present = _workspace.Reading.Offsets.TryGetValue(surface, out var offset);
        _session.TryRecord(new DiagnosticEvent("ReadingChanged", DiagnosticKind.Layout, SurfaceId: surface.Value.ToString("D"),
            Data: new Dictionary<string, string>
            {
                ["readingRevision"] = _workspace.Reading.Revision.ToString(CultureInfo.InvariantCulture),
                ["hasOffset"] = present.ToString(),
                ["x"] = offset.X.ToString("R", CultureInfo.InvariantCulture),
                ["y"] = offset.Y.ToString("R", CultureInfo.InvariantCulture)
            }));
    }

    private void LayoutChanged(WindowLayoutChange change)
    {
        var data = new Dictionary<string, string>
        {
            ["action"] = change.Action.ToString(),
            ["layoutRevision"] = change.Revision.ToString(CultureInfo.InvariantCulture)
        };
        if (_workspace.Layout.Windows.TryGetValue(change.DocumentId, out var layout))
        {
            var rectangle = layout.NormalRectangle;
            data["x"] = rectangle.X.ToString("R", CultureInfo.InvariantCulture);
            data["y"] = rectangle.Y.ToString("R", CultureInfo.InvariantCulture);
            data["width"] = rectangle.Width.ToString("R", CultureInfo.InvariantCulture);
            data["height"] = rectangle.Height.ToString("R", CultureInfo.InvariantCulture);
            data["filled"] = layout.IsFilled.ToString();
            data["minimized"] = layout.IsMinimized.ToString();
        }
        _session.TryRecord(new DiagnosticEvent("WindowLayoutChanged", DiagnosticKind.Layout,
            WindowId: change.DocumentId.Value.ToString("D"), Data: data));
    }

    private void CameraChanged()
    {
        var camera = _workspace.Camera.Transform;
        _session.TryRecord(new DiagnosticEvent("BoardCameraChanged", DiagnosticKind.Layout,
            SurfaceId: _workspace.BoardSurfaceId.Value.ToString("D"), Data: new Dictionary<string, string>
            {
                ["cameraRevision"] = _workspace.Camera.Revision.ToString(CultureInfo.InvariantCulture),
                ["x"] = camera.OffsetX.ToString("R", CultureInfo.InvariantCulture),
                ["y"] = camera.OffsetY.ToString("R", CultureInfo.InvariantCulture),
                ["scale"] = camera.Scale.ToString("R", CultureInfo.InvariantCulture)
            }));
    }

    public void Dispose()
    {
        _input.Observed -= InputObserved;
        _workspace.ContentChanged -= ContentChanged;
        _workspace.Reading.Changed -= ReadingChanged;
        _workspace.Layout.Changed -= LayoutChanged;
        _workspace.Camera.Changed -= CameraChanged;
    }
}
