using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CorgiBlackboard.App.Input;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Input;

namespace CorgiBlackboard.App.Views;

/// <summary>
/// The infinite board: committed ink as retained vector visuals, a transient layer for the pending
/// gesture, and one camera translation in double world DIP. Input goes through the shared context.
/// </summary>
public partial class BoardSurface : UserControl
{
    private bool _attached;
    private bool _statusQueued;

    public BoardSurface() => InitializeComponent();

    /// <summary>Connects the board to the window's shared editing context; call once.</summary>
    public void Attach(EditingContext context, UIElement coordinateRoot)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(coordinateRoot);
        if (_attached)
        {
            throw new InvalidOperationException("The board is already attached.");
        }

        _attached = true;
        var board = context.Workspace.BoardSurfaceId;
        var session = context.Session;
        _ = new ContentInputBinding(this, coordinateRoot, context, board, () =>
        {
            var origin = TranslatePoint(default, coordinateRoot);
            return session.BoardTransform(origin.X, origin.Y);
        });
        session.ContentChanged += surface =>
        {
            if (surface == board)
            {
                SyncContent(context);
            }
        };
        session.PendingChanged += () => ShowPending(context);
        session.CameraChanged += () => SyncCamera(context);
        MouseWheel += (_, e) =>
        {
            e.Handled = true;
            session.Wheel(e.Delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        };
        SizeChanged += (_, _) => session.InterruptSurface(board, InputCancellationReason.Resize);
        SyncContent(context);
        SyncCamera(context);
        PublishStatus(context);
    }

    private void SyncContent(EditingContext context)
    {
        CommittedInk.Sync(context.Workspace.GetElements(context.Workspace.BoardSurfaceId));
        QueueStatus(context);
    }

    private void ShowPending(EditingContext context)
    {
        var session = context.Session;
        var onBoard = session.PendingSurfaceId == context.Workspace.BoardSurfaceId;
        CommittedInk.Hide(onBoard ? session.PendingBefore : []);
        TransientInk.Show(onBoard ? session.PendingAfter : []);
        QueueStatus(context);
    }

    private void SyncCamera(EditingContext context)
    {
        var camera = context.Workspace.Camera.Transform;
        CameraTranslation.X = camera.OffsetX;
        CameraTranslation.Y = camera.OffsetY;
        QueueStatus(context);
    }

    private void QueueStatus(EditingContext context)
    {
        if (_statusQueued)
        {
            return;
        }

        _statusQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _statusQueued = false;
            PublishStatus(context);
        }, DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// Publishes the machine-readable UIA ItemStatus
    /// "strokes=N;contentRevision=R;cameraX=X;cameraY=Y;pending=0|1" (invariant culture) after
    /// rendering work settles, for automation clients and desktop QA.
    /// </summary>
    private void PublishStatus(EditingContext context)
    {
        var camera = context.Workspace.Camera.Transform;
        var pending = context.Session.PendingSurfaceId == context.Workspace.BoardSurfaceId ? 1 : 0;
        var status = string.Create(CultureInfo.InvariantCulture,
            $"strokes={CommittedInk.StrokeCount};contentRevision={context.Workspace.ContentRevision};cameraX={camera.OffsetX};cameraY={camera.OffsetY};pending={pending}");
        var previous = AutomationProperties.GetItemStatus(this);
        if (status == previous)
        {
            return;
        }

        AutomationProperties.SetItemStatus(this, status);
        UIElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(
            AutomationElementIdentifiers.ItemStatusProperty, previous, status);
    }
}
