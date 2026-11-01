using System.Collections.Immutable;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Ink;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;
using Xunit;

namespace CorgiBlackboard.Core.Tests;

public sealed class InputArbiterTests
{
    [Theory]
    [InlineData(InputTool.Ink)]
    [InlineData(InputTool.Eraser)]
    public void Second_contact_cancels_edit_then_navigates_fixed_pair_and_waits_for_all_up(InputTool tool)
    {
        var arbiter = new InputArbiter();
        arbiter.SetTool(tool);
        var surface = SurfaceId.New();
        var commits = 0;
        var cancels = 0;
        var edits = 0;
        var deltas = new List<(double X, double Y)>();
        arbiter.EditingCommitted += (_, _) => commits++;
        arbiter.EditingCancelled += (_, _) => cancels++;
        arbiter.EditingSampled += (_, _) => edits++;
        arbiter.NavigationDelta += (_, x, y) => deltas.Add((x, y));

        Send(arbiter, 1, PointerAction.Down, 100, 100, surface);
        Send(arbiter, 1, PointerAction.Move, 200, 100, surface);
        Send(arbiter, 2, PointerAction.Down, 200, 200, surface);
        Send(arbiter, 1, PointerAction.Move, 250, 130, surface);
        Send(arbiter, 2, PointerAction.Move, 250, 230, surface);
        Send(arbiter, 2, PointerAction.Up, 250, 230, surface);
        Send(arbiter, 1, PointerAction.Move, 260, 130, surface);
        Send(arbiter, 1, PointerAction.Up, 260, 130, surface);

        Assert.Equal(0, commits);
        Assert.Equal(1, cancels);
        Assert.Equal(1, edits);
        Assert.Equal(new[] { (25d, 15d), (25d, 15d) }, deltas);
        Assert.Equal(50, deltas.Sum(delta => delta.X));
        Assert.Equal(30, deltas.Sum(delta => delta.Y));
        Assert.Equal(InputArbiterState.Idle, arbiter.State);
    }

    [Fact]
    public void Third_contact_does_not_join_navigation_or_release_barrier_early()
    {
        var arbiter = new InputArbiter();
        var surface = SurfaceId.New();
        var deltas = new List<(double X, double Y)>();
        arbiter.NavigationDelta += (_, x, y) => deltas.Add((x, y));

        Send(arbiter, 1, PointerAction.Down, 10, 10, surface);
        Send(arbiter, 2, PointerAction.Down, 30, 10, surface);
        Send(arbiter, 3, PointerAction.Down, 500, 500, surface);
        Send(arbiter, 3, PointerAction.Move, 900, 900, surface);
        Send(arbiter, 2, PointerAction.Up, 30, 10, surface);
        Send(arbiter, 1, PointerAction.Up, 10, 10, surface);
        Assert.Equal(InputArbiterState.AwaitAllReleased, arbiter.State);
        Send(arbiter, 4, PointerAction.Down, 40, 40, surface);
        Assert.Equal(InputArbiterState.AwaitAllReleased, arbiter.State);
        Send(arbiter, 3, PointerAction.Up, 900, 900, surface);
        Assert.Equal(InputArbiterState.AwaitAllReleased, arbiter.State);
        Send(arbiter, 4, PointerAction.Up, 40, 40, surface);
        Assert.Equal(InputArbiterState.Idle, arbiter.State);
        Assert.Empty(deltas);
    }

    [Fact]
    public void Chrome_first_and_cross_surface_contacts_cancel_without_navigation()
    {
        var arbiter = new InputArbiter();
        var a = SurfaceId.New();
        var b = SurfaceId.New();
        var cancels = 0;
        var commits = 0;
        var pans = 0;
        arbiter.EditingCancelled += (_, _) => cancels++;
        arbiter.EditingCommitted += (_, _) => commits++;
        arbiter.NavigationDelta += (_, _, _) => pans++;

        Send(arbiter, 1, PointerAction.Down, 1, 1, a, InputSurfaceKind.Chrome);
        Send(arbiter, 2, PointerAction.Down, 2, 2, a);
        Send(arbiter, 2, PointerAction.Up, 2, 2, a);
        Send(arbiter, 1, PointerAction.Up, 1, 1, a, InputSurfaceKind.Chrome);
        Assert.Equal(InputArbiterState.Idle, arbiter.State);

        Send(arbiter, 5, PointerAction.Down, 5, 5, a);
        Send(arbiter, 6, PointerAction.Down, 6, 6, a, InputSurfaceKind.Chrome);
        Send(arbiter, 5, PointerAction.Move, 8, 8, a);
        Send(arbiter, 6, PointerAction.Up, 6, 6, a, InputSurfaceKind.Chrome);
        Send(arbiter, 5, PointerAction.Up, 8, 8, a);

        Send(arbiter, 3, PointerAction.Down, 10, 10, a);
        Send(arbiter, 4, PointerAction.Down, 20, 20, b);
        Send(arbiter, 3, PointerAction.Move, 30, 30, a);
        Send(arbiter, 4, PointerAction.Move, 40, 40, b);
        Send(arbiter, 3, PointerAction.Up, 30, 30, a);
        Send(arbiter, 4, PointerAction.Up, 40, 40, b);

        Assert.Equal(2, cancels);
        Assert.Equal(0, commits);
        Assert.Equal(0, pans);
        Assert.Equal(InputArbiterState.Idle, arbiter.State);
    }

    [Fact]
    public void Interrupt_and_orphan_events_cancel_pending_edit_and_gate_new_gesture()
    {
        var arbiter = new InputArbiter();
        var surface = SurfaceId.New();
        var cancels = 0;
        var starts = 0;
        arbiter.EditingCancelled += (_, _) => cancels++;
        arbiter.EditingStarted += (_, _) => starts++;

        Send(arbiter, 9, PointerAction.Move, 20, 20, surface);
        Send(arbiter, 9, PointerAction.Up, 20, 20, surface);
        Assert.Equal(0, starts);
        Send(arbiter, 1, PointerAction.Down, 10, 10, surface);
        arbiter.Interrupt(InputCancellationReason.Resize);
        Assert.Equal(1, cancels);
        Assert.Equal(InputArbiterState.AwaitAllReleased, arbiter.State);
        Send(arbiter, 2, PointerAction.Down, 20, 20, surface);
        Send(arbiter, 2, PointerAction.Up, 20, 20, surface);
        Assert.Equal(1, starts);
        Send(arbiter, 1, PointerAction.Up, 10, 10, surface);
        Send(arbiter, 3, PointerAction.Down, 30, 30, surface, promoted: true);
        Assert.Equal(1, starts);
    }

    [Fact]
    public void Third_contact_during_edit_cancels_and_never_creates_pending_fragments()
    {
        var (arbiter, transactions) = CreateTransactionHarness(InputTool.Ink);
        var snapshot = Capture(transactions.Workspace, transactions.SurfaceId);
        var revision = transactions.Workspace.ContentRevision;

        Send(arbiter, 1, PointerAction.Down, 10, 10, transactions.SurfaceId);
        Send(arbiter, 1, PointerAction.Move, 15, 15, transactions.SurfaceId);
        Assert.True(transactions.PendingFragmentCount > 0);
        Send(arbiter, 2, PointerAction.Down, 20, 20, transactions.SurfaceId);
        Send(arbiter, 3, PointerAction.Down, 30, 30, transactions.SurfaceId);
        Send(arbiter, 3, PointerAction.Move, 40, 40, transactions.SurfaceId);
        Assert.Contains(InputCancellationReason.SecondContact, transactions.CancellationReasons);
        AssertRollback(transactions, snapshot, revision);
        Send(arbiter, 2, PointerAction.Up, 20, 20, transactions.SurfaceId);
        Send(arbiter, 1, PointerAction.Up, 15, 15, transactions.SurfaceId);
        Assert.Equal(InputArbiterState.AwaitAllReleased, arbiter.State);
        Send(arbiter, 3, PointerAction.Up, 40, 40, transactions.SurfaceId);
        Assert.Equal(InputArbiterState.Idle, arbiter.State);

        Send(arbiter, 4, PointerAction.Down, 50, 50, transactions.SurfaceId);
        Send(arbiter, 4, PointerAction.Up, 50, 50, transactions.SurfaceId);
        Assert.Equal(1, transactions.CommitCount);
        Assert.Equal(revision + 1, transactions.Workspace.ContentRevision);
        Assert.Equal(0, transactions.PendingOperationCount);
        Assert.Equal(0, transactions.PendingFragmentCount);
        var committed = transactions.Workspace.GetElements(transactions.SurfaceId);
        Assert.Equal(2, committed.Count);
        AssertStableStroke(snapshot[0], Assert.IsType<StrokeElement>(Assert.Single(committed, element => element.Id == snapshot[0].Id)));
        Assert.Contains(committed, element =>
            element.Id == new ElementId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")));
    }

    [Fact]
    public void Editing_commit_locks_start_transform_and_promoted_mouse_is_ignored()
    {
        var arbiter = new InputArbiter();
        var surface = SurfaceId.New();
        var start = new ViewportTransform(2, 5, 7);
        var later = new ViewportTransform(1, 100, 100);
        var commits = 0;
        arbiter.EditingCommitted += (_, _) => commits++;

        Send(arbiter, 1, PointerAction.Down, 10, 20, surface, transform: start);
        Send(arbiter, 2, PointerAction.Down, 10, 20, surface, device: PointerDeviceKind.Mouse, promoted: true);
        Send(arbiter, 1, PointerAction.Move, 14, 24, surface, transform: later);
        Assert.Equal(start, arbiter.LockedTransform);
        Send(arbiter, 1, PointerAction.Up, 14, 24, surface, transform: later);
        Assert.Equal(1, commits);
    }

    [Theory]
    [InlineData(InputTool.Ink)]
    [InlineData(InputTool.Eraser)]
    public void Second_contact_cancels_real_pending_operation_without_changing_committed_stroke(InputTool tool)
    {
        var (arbiter, transactions) = CreateTransactionHarness(tool);
        var snapshot = Capture(transactions.Workspace, transactions.SurfaceId);
        var revision = transactions.Workspace.ContentRevision;

        Send(arbiter, 10, PointerAction.Down, 100, 100, transactions.SurfaceId);
        Send(arbiter, 10, PointerAction.Move, 200, 100, transactions.SurfaceId);
        Assert.Equal(1, transactions.PendingOperationCount);
        Assert.True(transactions.PendingFragmentCount > 0);
        Send(arbiter, 11, PointerAction.Down, 200, 200, transactions.SurfaceId);

        Assert.Contains(InputCancellationReason.SecondContact, transactions.CancellationReasons);
        Assert.Equal(0, transactions.CommitCount);
        AssertRollback(transactions, snapshot, revision);
    }

    [Fact]
    public void Pointer_action_cancel_rolls_back_real_pending_ink_operation()
    {
        var (arbiter, transactions) = CreateTransactionHarness(InputTool.Ink);
        var snapshot = Capture(transactions.Workspace, transactions.SurfaceId);
        var revision = transactions.Workspace.ContentRevision;

        Send(arbiter, 20, PointerAction.Down, 12, 13, transactions.SurfaceId);
        Send(arbiter, 20, PointerAction.Move, 24, 26, transactions.SurfaceId);
        Send(arbiter, 20, PointerAction.Cancel, 24, 26, transactions.SurfaceId);

        Assert.Contains(InputCancellationReason.PointerCancelled, transactions.CancellationReasons);
        Assert.Equal(0, transactions.CommitCount);
        Assert.Equal(InputArbiterState.Idle, arbiter.State);
        AssertRollback(transactions, snapshot, revision);
    }

    [Theory]
    [InlineData(InputCancellationReason.CaptureLost)]
    [InlineData(InputCancellationReason.FocusLost)]
    public void Capture_or_focus_loss_rolls_back_real_pending_ink_operation(InputCancellationReason reason)
    {
        var (arbiter, transactions) = CreateTransactionHarness(InputTool.Ink);
        var snapshot = Capture(transactions.Workspace, transactions.SurfaceId);
        var revision = transactions.Workspace.ContentRevision;

        Send(arbiter, 30, PointerAction.Down, 12, 13, transactions.SurfaceId);
        Send(arbiter, 30, PointerAction.Move, 24, 26, transactions.SurfaceId);
        Assert.True(transactions.PendingFragmentCount > 0);
        if (reason == InputCancellationReason.CaptureLost)
        {
            arbiter.LostCapture();
        }
        else
        {
            arbiter.LostFocus();
        }

        Assert.Contains(reason, transactions.CancellationReasons);
        Assert.Equal(0, transactions.CommitCount);
        Assert.Equal(InputArbiterState.AwaitAllReleased, arbiter.State);
        AssertRollback(transactions, snapshot, revision);
        Send(arbiter, 30, PointerAction.Up, 24, 26, transactions.SurfaceId);
        Assert.Equal(InputArbiterState.Idle, arbiter.State);
    }

    [Fact]
    public void Wheel_interrupt_rolls_back_real_pending_eraser_operation()
    {
        var (arbiter, transactions) = CreateTransactionHarness(InputTool.Eraser);
        var snapshot = Capture(transactions.Workspace, transactions.SurfaceId);
        var revision = transactions.Workspace.ContentRevision;

        Send(arbiter, 40, PointerAction.Down, 50, 0, transactions.SurfaceId);
        Send(arbiter, 40, PointerAction.Move, 50, 10, transactions.SurfaceId);
        Assert.True(transactions.PendingFragmentCount > 0);
        arbiter.Interrupt(InputCancellationReason.Wheel);

        Assert.Contains(InputCancellationReason.Wheel, transactions.CancellationReasons);
        Assert.Equal(0, transactions.CommitCount);
        Assert.Equal(InputArbiterState.AwaitAllReleased, arbiter.State);
        AssertRollback(transactions, snapshot, revision);
        Send(arbiter, 40, PointerAction.Up, 50, 10, transactions.SurfaceId);
    }

    [Theory]
    [InlineData(InputCancellationReason.DpiChanged)]
    [InlineData(InputCancellationReason.Resize)]
    public void Dpi_or_resize_interrupt_rolls_back_real_pending_ink_operation(InputCancellationReason reason)
    {
        var (arbiter, transactions) = CreateTransactionHarness(InputTool.Ink);
        var snapshot = Capture(transactions.Workspace, transactions.SurfaceId);
        var revision = transactions.Workspace.ContentRevision;

        Send(arbiter, 50, PointerAction.Down, 12, 13, transactions.SurfaceId);
        Send(arbiter, 50, PointerAction.Move, 24, 26, transactions.SurfaceId);
        Assert.True(transactions.PendingFragmentCount > 0);
        arbiter.Interrupt(reason);

        Assert.Contains(reason, transactions.CancellationReasons);
        Assert.Equal(0, transactions.CommitCount);
        Assert.Equal(InputArbiterState.AwaitAllReleased, arbiter.State);
        AssertRollback(transactions, snapshot, revision);
        Send(arbiter, 50, PointerAction.Up, 24, 26, transactions.SurfaceId);
    }

    [Fact]
    public void Tool_change_cancels_with_the_tool_locked_at_gesture_start()
    {
        var arbiter = new InputArbiter();
        var surface = SurfaceId.New();
        InputTool? cancelledTool = null;
        arbiter.EditingCancelled += (tool, _) => cancelledTool = tool;

        Send(arbiter, 1, PointerAction.Down, 10, 10, surface);
        arbiter.SetTool(InputTool.Eraser);

        Assert.Equal(InputTool.Ink, cancelledTool);
        Assert.Equal(InputArbiterState.AwaitAllReleased, arbiter.State);
    }

    private static (InputArbiter Arbiter, WorkspaceTransactions Transactions) CreateTransactionHarness(InputTool tool)
    {
        var workspace = new Workspace();
        var seed = CreateStroke(
            new ElementId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            new PagePoint(0, 0),
            new PagePoint(100, 0));
        workspace.BeginStroke(workspace.BoardSurfaceId, null, seed).Commit();
        var arbiter = new InputArbiter();
        if (tool != InputTool.Ink)
        {
            arbiter.SetTool(tool);
        }

        return (arbiter, new WorkspaceTransactions(arbiter, workspace, workspace.BoardSurfaceId));
    }

    private static StrokeElement CreateStroke(ElementId id, params PagePoint[] points) =>
        new(id, points.ToImmutableArray(), new StrokeStyle(0xFF315A77, 4), ElementTransform.Identity);

    private static StrokeSnapshot[] Capture(Workspace workspace, SurfaceId surfaceId) =>
        workspace.GetElements(surfaceId).Cast<StrokeElement>()
            .Select(element => new StrokeSnapshot(
                element.Id,
                element.Points.ToArray(),
                element.Style,
                element.Transform,
                element.SourceElementId))
            .ToArray();

    private static void AssertRollback(
        WorkspaceTransactions transactions,
        IReadOnlyList<StrokeSnapshot> snapshot,
        long revision)
    {
        Assert.Equal(0, transactions.PendingOperationCount);
        Assert.Equal(0, transactions.PendingFragmentCount);
        Assert.Equal(revision, transactions.Workspace.ContentRevision);
        var actual = transactions.Workspace.GetElements(transactions.SurfaceId);
        Assert.Equal(snapshot.Count, actual.Count);
        for (var index = 0; index < snapshot.Count; index++)
        {
            AssertStableStroke(snapshot[index], Assert.IsType<StrokeElement>(actual[index]));
        }
    }

    private static void AssertStableStroke(StrokeSnapshot expected, StrokeElement actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Points, actual.Points.ToArray());
        Assert.Equal(expected.Style, actual.Style);
        Assert.Equal(expected.Transform, actual.Transform);
        Assert.Equal(expected.SourceElementId, actual.SourceElementId);
    }

    private static void Send(
        InputArbiter arbiter,
        long id,
        PointerAction action,
        double x,
        double y,
        SurfaceId surface,
        InputSurfaceKind kind = InputSurfaceKind.Content,
        ViewportTransform? transform = null,
        PointerDeviceKind device = PointerDeviceKind.Touch,
        bool promoted = false) =>
        arbiter.Process(new PointerSample(
            id,
            id * 100 + (long)action,
            id * 1000 + (long)action,
            new ScreenPoint(x, y),
            device,
            action,
            kind,
            kind == InputSurfaceKind.Content ? surface : null,
            transform ?? new ViewportTransform(1, 0, 0),
            promoted));

    private sealed class WorkspaceTransactions
    {
        private readonly List<PendingWorkspaceOperation> _operations = [];

        public WorkspaceTransactions(InputArbiter arbiter, Workspace workspace, SurfaceId surfaceId)
        {
            Workspace = workspace;
            SurfaceId = surfaceId;
            arbiter.EditingStarted += (_, tool) => Begin(tool);
            arbiter.EditingCommitted += (_, _) => Commit();
            arbiter.EditingCancelled += (_, reason) => Cancel(reason);
        }

        public Workspace Workspace { get; }
        public SurfaceId SurfaceId { get; }
        public int CommitCount { get; private set; }
        public List<InputCancellationReason> CancellationReasons { get; } = [];
        public int PendingOperationCount => _operations.Count(operation => operation.IsPending());
        public int PendingFragmentCount => _operations
            .Where(operation => operation.IsPending())
            .Sum(operation => operation.FragmentCount);

        private void Begin(InputTool tool)
        {
            if (tool == InputTool.Ink)
            {
                var operation = Workspace.BeginStroke(SurfaceId, null, CreateStroke(
                    new ElementId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")),
                    new PagePoint(110, 115),
                    new PagePoint(125, 130)));
                _operations.Add(new PendingWorkspaceOperation(
                    () => operation.IsPending,
                    operation.After.Length,
                    operation.Commit,
                    operation.Cancel));
                return;
            }

            var erasure = EraseOperation.Begin(
                Workspace,
                SurfaceId,
                null,
                [new PagePoint(50, -10), new PagePoint(50, 10)],
                8) ?? throw new InvalidOperationException("The seeded stroke must produce pending eraser fragments.");
            _operations.Add(new PendingWorkspaceOperation(
                () => erasure.IsPending,
                erasure.After.Length,
                erasure.Commit,
                erasure.Cancel));
        }

        private void Commit()
        {
            var operation = _operations.LastOrDefault(candidate => candidate.IsPending())
                ?? throw new InvalidOperationException("Commit requires a pending workspace transaction.");
            operation.Commit();
            CommitCount++;
        }

        private void Cancel(InputCancellationReason reason)
        {
            CancellationReasons.Add(reason);
            var operation = _operations.LastOrDefault(candidate => candidate.IsPending())
                ?? throw new InvalidOperationException("Cancel requires a pending workspace transaction.");
            operation.Cancel();
        }
    }

    private sealed record PendingWorkspaceOperation(
        Func<bool> IsPending,
        int FragmentCount,
        Action Commit,
        Action Cancel);

    private sealed record StrokeSnapshot(
        ElementId Id,
        PagePoint[] Points,
        StrokeStyle Style,
        ElementTransform Transform,
        ElementId? SourceElementId);
}
