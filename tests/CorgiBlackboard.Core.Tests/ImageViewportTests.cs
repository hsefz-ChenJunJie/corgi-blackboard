using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;
using Xunit;

namespace CorgiBlackboard.Core.Tests;

public sealed class ImageViewportTests
{
    [Theory]
    [InlineData(InputTool.Ink)]
    [InlineData(InputTool.Eraser)]
    public void Page_navigation_cancels_preview_and_changes_only_locked_page_offset(InputTool tool)
    {
        var workspace = new Workspace();
        var asset = new Asset(AssetId.New(), "image/png", 1);
        workspace.AddAsset(asset);
        var document = workspace.AddImageDocument(asset.Id, 800, 2400);
        var other = workspace.AddImageDocument(asset.Id, 800, 2400);
        var page = document.PageSurfaceIds[0];
        workspace.Layout.SetTeachingArea(400, 348);
        workspace.Layout.Open(document.Id, new WindowRectangle(0, 0, 320, 300));
        workspace.Layout.Open(other.Id, new WindowRectangle(0, 0, 320, 300));
        workspace.Layout.EnterFill(document.Id);
        workspace.Reading.SetOffset(page, 0, 600);
        var viewport = new ImageViewport(workspace, document.Id);
        var arbiter = new InputArbiter();
        var session = new EditingSession(workspace, arbiter, new ToolSettings(InputTool.Ink, 0xFFFF0000, 4, 24));
        void Send(long id, PointerAction action, double x, double y, SurfaceId? target = null) =>
            arbiter.Process(new PointerSample(id, 0, 0, new ScreenPoint(x, y),
                PointerDeviceKind.Touch, action, InputSurfaceKind.Content, target ?? page, viewport.Transform()));
        Send(1, PointerAction.Down, 20, 50);
        Send(1, PointerAction.Up, 180, 50);
        var original = Assert.IsType<StrokeElement>(Assert.Single(workspace.GetElements(page)));
        Assert.Equal(new PagePoint(40, 700), original.Points[0]);
        Assert.Equal(8, original.Style.Width);
        session.Select(new ToolSettings(tool, 0xFF00FF00, 4, 24));
        Send(2, PointerAction.Down, 100, 30);
        Send(2, PointerAction.Move, 100, 70);
        Send(3, PointerAction.Down, 200, 70);
        Assert.Empty(session.PendingAfter);
        Send(2, PointerAction.Move, 50, 20);
        Send(3, PointerAction.Move, 250, 20);
        Assert.Equal((0d, 700d), workspace.Reading.Offsets[page]);
        Assert.Equal(0.5, viewport.Transform().Scale);
        Assert.Equal((0d, 0d), workspace.Reading.Offsets.GetValueOrDefault(other.PageSurfaceIds[0]));
        Assert.Equal(new ViewportTransform(1, 0, 0), workspace.Camera.Transform);
        Send(3, PointerAction.Up, 250, 20);
        Send(2, PointerAction.Move, 70, 40, other.PageSurfaceIds[0]);
        Send(2, PointerAction.Up, 70, 40);
        Assert.Same(original, Assert.Single(workspace.GetElements(page)));
        Assert.Equal(1, workspace.ContentRevision);
        Assert.Empty(workspace.GetElements(other.PageSurfaceIds[0]));
        Assert.Equal(InputArbiterState.Idle, arbiter.State);
    }

    [Fact]
    public void Crossing_image_bounds_clips_segments_without_joining_outside_excursions()
    {
        var workspace = new Workspace();
        var asset = new Asset(AssetId.New(), "image/png", 1);
        workspace.AddAsset(asset);
        var document = workspace.AddImageDocument(asset.Id, 200, 100);
        var arbiter = new InputArbiter();
        var session = new EditingSession(workspace, arbiter, new ToolSettings(InputTool.Ink, 0xFFFF0000, 4, 24));
        var page = document.PageSurfaceIds[0];
        void Send(PointerAction action, double x, double y) =>
            arbiter.Process(new PointerSample(1, 0, 0, new ScreenPoint(x, y),
                PointerDeviceKind.Touch, action, InputSurfaceKind.Content, page, new ViewportTransform(1, 0, 0)));
        Send(PointerAction.Down, -20, 25);
        Send(PointerAction.Move, 220, 25);
        Send(PointerAction.Move, 220, 75);
        Send(PointerAction.Move, -20, 75);
        Assert.Empty(workspace.GetElements(document.PageSurfaceIds[0]));
        Assert.Equal(2, session.PendingAfter.Length);
        Assert.Equal(new PagePoint(0, 25), session.PendingAfter[0].Points[0]);
        Assert.Equal(new PagePoint(200, 25), session.PendingAfter[0].Points[^1]);
        Assert.Equal(new PagePoint(200, 75), session.PendingAfter[1].Points[0]);
        Assert.Equal(new PagePoint(0, 75), session.PendingAfter[1].Points[^1]);
        Send(PointerAction.Cancel, -20, 75);
        Assert.Equal(0, workspace.ContentRevision);
        Assert.Empty(workspace.GetElements(document.PageSurfaceIds[0]));
        Send(PointerAction.Down, 10, 20);
        Send(PointerAction.Up, 30, 40);
        Assert.Equal(1, workspace.ContentRevision);
        Assert.Empty(workspace.GetElements(workspace.BoardSurfaceId));
        Assert.Equal(new PagePoint(30, 40), Assert.IsType<StrokeElement>(Assert.Single(workspace.GetElements(page))).Points[^1]);
    }

    [Fact]
    public void Marker_stays_in_intrinsic_coordinates_across_resize_fill_and_restore()
    {
        var workspace = new Workspace();
        var asset = new Asset(AssetId.New(), "image/png", 1);
        workspace.AddAsset(asset);
        var document = workspace.AddImageDocument(asset.Id, 800, 2400);
        var page = document.PageSurfaceIds[0];
        workspace.Layout.SetTeachingArea(400, 348);
        workspace.Layout.Open(document.Id, new WindowRectangle(0, 0, 320, 300));
        var viewport = new ImageViewport(workspace, document.Id);
        viewport.Scroll(0, -600);
        var marker = new PagePoint(200, 700);
        Assert.Equal(new ScreenPoint(200, 100), viewport.Transform().ToScreen(marker));

        workspace.Layout.Resize(document.Id, WindowResizeEdge.Right, 80, 0);
        Assert.Equal(1, viewport.Transform().Scale);
        Assert.Equal(new ScreenPoint(200, 100), viewport.Transform().ToScreen(marker));
        workspace.Layout.EnterFill(document.Id);
        Assert.Equal(0.5, viewport.Transform().Scale);
        Assert.Equal(new ScreenPoint(100, 50), viewport.Transform().ToScreen(marker));
        viewport.Scroll(0, -600);
        Assert.Equal((0d, 1800d), workspace.Reading.Offsets[page]);
        workspace.Layout.Minimize(document.Id);
        workspace.Layout.Restore(document.Id);
        Assert.Equal((0d, 1800d), workspace.Reading.Offsets[page]);
        workspace.Layout.ExitFill(document.Id);
        Assert.Equal((0d, 600d), workspace.Reading.Offsets[page]);
        Assert.Equal(marker, viewport.Transform().ToPage(new ScreenPoint(200, 100)));
        Assert.Equal(0, workspace.ContentRevision);
    }

    [Theory]
    [InlineData(200, 100, 0, 0)]
    [InlineData(2400, 800, 2096, 564)]
    public void Wheel_clamps_each_axis_and_preserves_fractional_delta(
        int width, int height, double maxX, double maxY)
    {
        var workspace = new Workspace();
        var asset = new Asset(AssetId.New(), "image/png", 1);
        workspace.AddAsset(asset);
        var document = workspace.AddImageDocument(asset.Id, width, height);
        workspace.Layout.SetTeachingArea(1024, 768);
        workspace.Layout.Open(document.Id, new WindowRectangle(0, 0, 320, 300));
        var viewport = new ImageViewport(workspace, document.Id);
        viewport.Wheel(-1, true);
        Assert.Equal(width > 304 ? 0.4 : 0, workspace.Reading.Offsets.GetValueOrDefault(document.PageSurfaceIds[0]).X);
        viewport.Scroll(-10000, -10000);
        Assert.Equal((maxX, maxY), workspace.Reading.Offsets.GetValueOrDefault(document.PageSurfaceIds[0]));
        viewport.Scroll(10000, 10000);
        Assert.Equal((0d, 0d), workspace.Reading.Offsets.GetValueOrDefault(document.PageSurfaceIds[0]));
    }
}
