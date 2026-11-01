using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Model;
using Xunit;

namespace CorgiBlackboard.Core.Tests;

public sealed class WindowLayoutTests
{
    [Fact]
    public void Minimized_fill_preserves_reading_and_explicit_exit_restores_normal_snapshot()
    {
        var (workspace, first, second) = Create();
        var layout = workspace.Layout;
        var normal = layout.Windows[first.Id];
        workspace.Reading.SetOffset(first.PageSurfaceIds[0], 12, 600);

        layout.EnterFill(first.Id);
        workspace.Reading.SetOffset(first.PageSurfaceIds[0], 0, 1800);
        layout.Minimize(first.Id);
        Assert.Null(layout.FilledWindow);
        layout.Restore(first.Id);

        Assert.Equal(first.Id, layout.FilledWindow);
        Assert.Equal((0d, 1800d), workspace.Reading.Offsets[first.PageSurfaceIds[0]]);
        layout.ExitFill(first.Id);
        Assert.Equal(normal.NormalRectangle, layout.Windows[first.Id].NormalRectangle);
        Assert.Equal(normal.ZOrder, layout.Windows[first.Id].ZOrder);
        Assert.Equal((12d, 600d), workspace.Reading.Offsets[first.PageSurfaceIds[0]]);
        Assert.False(layout.Windows[second.Id].IsFilled);
        Assert.Equal(0, workspace.ContentRevision);
    }

    [Fact]
    public void Restoring_filled_document_hands_off_current_owner_and_preserves_both_normal_snapshots()
    {
        var (workspace, first, second) = Create();
        var layout = workspace.Layout;
        workspace.Reading.SetOffset(first.PageSurfaceIds[0], 0, 600);
        workspace.Reading.SetOffset(second.PageSurfaceIds[0], 0, 320);
        var firstNormal = layout.Windows[first.Id];
        var secondNormal = layout.Windows[second.Id];
        layout.EnterFill(first.Id);
        workspace.Reading.SetOffset(first.PageSurfaceIds[0], 0, 1800);
        layout.Minimize(first.Id);
        layout.EnterFill(second.Id);
        workspace.Reading.SetOffset(second.PageSurfaceIds[0], 0, 1400);

        layout.Restore(first.Id);

        Assert.Equal(first.Id, layout.FilledWindow);
        Assert.Equal(secondNormal, layout.Windows[second.Id]);
        Assert.Equal((0d, 320d), workspace.Reading.Offsets[second.PageSurfaceIds[0]]);
        Assert.Equal((0d, 1800d), workspace.Reading.Offsets[first.PageSurfaceIds[0]]);
        layout.ExitFill(first.Id);
        Assert.Equal(firstNormal, layout.Windows[first.Id]);
        Assert.Equal((0d, 600d), workspace.Reading.Offsets[first.PageSurfaceIds[0]]);
    }

    [Fact]
    public void Second_fill_request_restores_old_owner_before_notifying_observers()
    {
        var (workspace, first, second) = Create();
        workspace.Reading.SetOffset(first.PageSurfaceIds[0], 0, 600);
        var before = workspace.Layout.Windows[first.Id];
        workspace.Layout.EnterFill(first.Id);
        workspace.Reading.SetOffset(first.PageSurfaceIds[0], 0, 1800);
        var actions = new List<WindowLayoutAction>();
        workspace.Layout.Changed += change =>
        {
            Assert.Single(workspace.Layout.Windows.Values, x => x.IsFilled && !x.IsMinimized);
            actions.Add(change.Action);
        };

        workspace.Layout.EnterFill(second.Id);

        Assert.Equal(before, workspace.Layout.Windows[first.Id]);
        Assert.Equal((0d, 600d), workspace.Reading.Offsets[first.PageSurfaceIds[0]]);
        Assert.Equal([WindowLayoutAction.FillExited, WindowLayoutAction.FillEntered], actions);
    }

    [Theory]
    [InlineData(WindowResizeEdge.Left, 60, 0, 84, 24, 420, 360)]
    [InlineData(WindowResizeEdge.Top, 0, 60, 24, 84, 480, 300)]
    [InlineData(WindowResizeEdge.Right, 60, 0, 24, 24, 540, 360)]
    [InlineData(WindowResizeEdge.Bottom, 0, 60, 24, 24, 480, 420)]
    [InlineData(WindowResizeEdge.TopLeft, 60, 60, 84, 84, 420, 300)]
    [InlineData(WindowResizeEdge.TopRight, 60, 60, 24, 84, 540, 300)]
    [InlineData(WindowResizeEdge.BottomLeft, 60, 60, 84, 24, 420, 420)]
    [InlineData(WindowResizeEdge.BottomRight, 60, 60, 24, 24, 540, 420)]
    public void Each_resize_edge_keeps_its_opposite_edge(
        WindowResizeEdge edge, double dx, double dy, double x, double y, double width, double height)
    {
        var (workspace, first, second) = Create();
        var other = workspace.Layout.Windows[second.Id];

        workspace.Layout.Resize(first.Id, edge, dx, dy);

        Assert.Equal(new WindowRectangle(x, y, width, height), workspace.Layout.Windows[first.Id].NormalRectangle);
        Assert.Equal(other, workspace.Layout.Windows[second.Id]);
    }

    [Fact]
    public void Shrinking_below_minimum_clamps_and_tiny_client_recovers()
    {
        var (workspace, first, _) = Create();
        workspace.Layout.Resize(first.Id, WindowResizeEdge.BottomRight, -10000, -10000);
        Assert.Equal(new WindowRectangle(24, 24, 240, 180), workspace.Layout.Windows[first.Id].NormalRectangle);

        workspace.Layout.SetTeachingArea(120, 80);

        Assert.Equal(new WindowRectangle(0, 0, 120, 80), workspace.Layout.Windows[first.Id].NormalRectangle);
        workspace.Layout.SetTeachingArea(1024, 680);
        Assert.Equal(new WindowRectangle(0, 0, 240, 180), workspace.Layout.Windows[first.Id].NormalRectangle);
    }

    [Fact]
    public void Movement_and_expansion_cannot_hide_window_outside_client()
    {
        var (workspace, first, _) = Create();
        workspace.Layout.Move(first.Id, 10000, -10000);
        Assert.Equal(new WindowRectangle(544, 0, 480, 360), workspace.Layout.Windows[first.Id].NormalRectangle);

        workspace.Layout.Resize(first.Id, WindowResizeEdge.BottomRight, 10000, 10000);

        Assert.Equal(new WindowRectangle(544, 0, 480, 680), workspace.Layout.Windows[first.Id].NormalRectangle);
    }

    [Fact]
    public void Fill_clamps_page_local_offset_using_width_fit_and_client_resize_notifies()
    {
        var (workspace, first, _) = Create();
        workspace.Layout.SetTeachingArea(400, 648);
        workspace.Reading.SetOffset(first.PageSurfaceIds[0], 200, 2000);

        workspace.Layout.EnterFill(first.Id);

        Assert.Equal((0d, 1200d), workspace.Reading.Offsets[first.PageSurfaceIds[0]]);
        var events = new List<WindowLayoutChange>();
        workspace.Layout.Changed += events.Add;
        workspace.Layout.SetTeachingArea(800, 648);
        Assert.Contains(events, e => e.DocumentId == first.Id);
        Assert.Equal(new WindowRectangle(0, 0, 800, 648), workspace.Layout.DisplayRectangle(first.Id));
    }

    [Fact]
    public void Closed_document_releases_last_asset_page_reading_and_window_without_content_revision()
    {
        var (workspace, first, second) = Create();
        workspace.Layout.EnterFill(first.Id);
        workspace.Layout.Minimize(first.Id);
        var revision = workspace.ContentRevision;

        workspace.CloseDocument(first.Id);

        Assert.False(workspace.Documents.ContainsKey(first.Id));
        Assert.False(workspace.Assets.ContainsKey(first.AssetId));
        Assert.False(workspace.Surfaces.ContainsKey(first.PageSurfaceIds[0]));
        Assert.False(workspace.Reading.CurrentPages.ContainsKey(first.Id));
        Assert.False(workspace.Layout.Windows.ContainsKey(first.Id));
        Assert.Empty(workspace.Layout.MinimizedWindows);
        Assert.True(workspace.Documents.ContainsKey(second.Id));
        Assert.Equal(revision, workspace.ContentRevision);
    }

    [Fact]
    public void Closing_one_document_preserves_shared_asset_until_last_reference_closes()
    {
        var (workspace, first, _) = Create();
        var shared = workspace.AddImageDocument(first.AssetId, 800, 2400);

        workspace.CloseDocument(first.Id);

        Assert.True(workspace.Assets.ContainsKey(shared.AssetId));
        workspace.CloseDocument(shared.Id);
        Assert.False(workspace.Assets.ContainsKey(shared.AssetId));
    }

    [Fact]
    public void Invalid_actions_are_atomic_and_do_not_advance_any_revision()
    {
        var (workspace, first, _) = Create();
        var before = workspace.Layout.Windows[first.Id];
        var revision = workspace.Layout.Revision;
        var reading = workspace.Reading.Revision;

        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Layout.Move(first.Id, double.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Layout.SetTeachingArea(0, 10));
        Assert.Throws<ArgumentException>(() => workspace.Layout.Open(DocumentId.New(), default));
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Layout.Resize(first.Id, (WindowResizeEdge)90, 1, 1));
        Assert.Throws<InvalidOperationException>(() => workspace.Layout.Restore(first.Id));
        Assert.Throws<InvalidOperationException>(() => workspace.Layout.Open(first.Id, new WindowRectangle(0, 0, 300, 200)));

        Assert.Equal(before, workspace.Layout.Windows[first.Id]);
        Assert.Equal(revision, workspace.Layout.Revision);
        Assert.Equal(reading, workspace.Reading.Revision);
    }

    internal static (Workspace Workspace, ImageDocument First, ImageDocument Second) Create()
    {
        var workspace = new Workspace();
        workspace.Layout.SetTeachingArea(1024, 680);
        var documents = new List<ImageDocument>();
        for (var i = 0; i < 2; i++)
        {
            var asset = new Asset(AssetId.New(), "image/png", 32);
            workspace.AddAsset(asset);
            var document = workspace.AddImageDocument(asset.Id, 800, 2400);
            workspace.Layout.Open(document.Id, new WindowRectangle(24 + i * 40, 24 + i * 40, 480, 360));
            documents.Add(document);
        }
        return (workspace, documents[0], documents[1]);
    }
}
