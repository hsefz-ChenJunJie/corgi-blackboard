using System.Collections.Immutable;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Model;
using CorgiBlackboard.Core.Operations;
using Xunit;

namespace CorgiBlackboard.Core.Tests;

public sealed partial class OwnershipCoordinateTests
{
    [Theory]
    [InlineData(-18.25, -42.5, 1, 0, 0)]
    [InlineData(-1200000.125, 950000.75, 1.75, -1875000.5, 240000.25)]
    [InlineData(810.125, -405.75, 1.5, 19.375, -50.625)]
    public void Board_coordinates_round_trip_through_distant_and_scaled_viewports(
        double x, double y, double scale, double offsetX, double offsetY)
    {
        var point = new BoardPoint(x, y);
        var viewport = new ViewportTransform(scale, offsetX, offsetY);

        var actual = viewport.ToBoard(viewport.ToScreen(point));

        Assert.InRange(Math.Abs(actual.X - x), 0, 1e-7);
        Assert.InRange(Math.Abs(actual.Y - y), 0, 1e-7);
    }

    [Fact]
    public void Page_coordinates_round_trip_at_dpi_and_fit_scales()
    {
        var point = new PagePoint(-1_250_000.125, 720_000.75);
        var dpiViewport = new ViewportTransform(1.5, 240.25, -96.5);
        var fitViewport = new ViewportTransform(0.375, -12.75, 48.125);

        AssertPointClose(point, dpiViewport.ToPage(dpiViewport.ToScreen(point)));
        AssertPointClose(point, fitViewport.ToPage(fitViewport.ToScreen(point)));
    }

    [Fact]
    public void Default_viewport_rejects_board_and_page_forward_conversions()
    {
        var viewport = default(ViewportTransform);

        Assert.Throws<InvalidOperationException>(() =>
            viewport.ToScreen(new BoardPoint(-12.5, 8.25)));
        Assert.Throws<InvalidOperationException>(() =>
            viewport.ToScreen(new PagePoint(-12.5, 8.25)));
    }

    [Fact]
    public void Camera_pan_changes_only_camera_not_content_or_window_layout()
    {
        var workspace = new Workspace();
        var asset = new Asset(AssetId.New(), "image/png", 32);
        workspace.AddAsset(asset);
        var document = workspace.AddImageDocument(asset.Id, 800, 600);
        var rectangle = new WindowRectangle(33, 44, 500, 400);
        workspace.Layout.SetTeachingArea(1024, 680);
        workspace.Layout.Open(document.Id, rectangle);
        var layoutRevision = workspace.Layout.Revision;
        var contentRevision = workspace.ContentRevision;

        workspace.Camera.Pan(-310.5, 880.25);

        Assert.Equal(contentRevision, workspace.ContentRevision);
        Assert.Equal(layoutRevision, workspace.Layout.Revision);
        Assert.Equal(rectangle, workspace.Layout.Windows[document.Id].NormalRectangle);
    }

    [Fact]
    public void Pending_stroke_locks_its_starting_surface_and_commits_one_content_change()
    {
        var workspace = new Workspace();
        var asset = new Asset(AssetId.New(), "image/png", 64);
        workspace.AddAsset(asset);
        var document = workspace.AddImageDocument(asset.Id, 100, 200);
        var pageId = document.PageSurfaceIds.Single();
        var stroke = CreateStroke();
        var pending = workspace.BeginStroke(pageId, pageId, stroke);

        Assert.True(pending.IsPending);
        Assert.Empty(workspace.GetDocumentElements(document.Id, pageId));
        Assert.Equal(0, workspace.ContentRevision);

        pending.Commit();

        Assert.False(pending.IsPending);
        Assert.Equal(1, workspace.ContentRevision);
        Assert.Same(stroke, Assert.Single(workspace.GetDocumentElements(document.Id, pageId)));
        Assert.Contains(stroke.Id, workspace.Surfaces[pageId].ElementIds);
        Assert.Throws<InvalidOperationException>(pending.Commit);
    }

    [Fact]
    public void Cancelled_pending_content_does_not_change_surface_or_revision()
    {
        var workspace = new Workspace();
        var pending = workspace.BeginStroke(
            workspace.BoardSurfaceId, null, CreateStroke());

        pending.Cancel();

        Assert.False(pending.IsPending);
        Assert.Empty(workspace.GetElements(workspace.BoardSurfaceId));
        Assert.Empty(workspace.Surfaces[workspace.BoardSurfaceId].ElementIds);
        Assert.Equal(0, workspace.ContentRevision);
        Assert.Throws<InvalidOperationException>(pending.Cancel);
    }

    [Fact]
    public void Normal_and_filled_image_layouts_reference_same_page_content()
    {
        var workspace = new Workspace();
        var asset = new Asset(AssetId.New(), "image/png", 12);
        workspace.AddAsset(asset);
        var document = workspace.AddImageDocument(asset.Id, 64, 32);
        var pageId = document.PageSurfaceIds.Single();
        workspace.BeginStroke(pageId, pageId, CreateStroke()).Commit();
        workspace.Layout.SetTeachingArea(1024, 680);
        workspace.Layout.Open(document.Id, new WindowRectangle(20, 25, 400, 300));
        var normalElements = workspace.GetDocumentElements(document.Id, pageId);

        workspace.Layout.EnterFill(document.Id);
        var filledElements = workspace.GetDocumentElements(document.Id, pageId);

        Assert.Same(normalElements[0], filledElements[0]);
        Assert.True(workspace.Reading.CurrentPages.TryGetValue(document.Id, out var currentPage));
        Assert.Equal(pageId, currentPage);
    }

    [Fact]
    public void Invalid_surface_page_coordinates_and_transforms_do_not_change_revisions()
    {
        var workspace = new Workspace();
        var document = workspace.AddImageDocument(
            AddAsset(workspace), 200, 100);
        var contentRevision = workspace.ContentRevision;
        var resourceRevision = workspace.ResourceRevision;
        var layoutRevision = workspace.Layout.Revision;
        var readingRevision = workspace.Reading.Revision;
        var validPageId = document.PageSurfaceIds.Single();
        var unknownSurface = SurfaceId.New();
        var pointCountBefore = workspace.GetElements(validPageId).Count;

        Assert.Throws<ArgumentException>(() => workspace.BeginStroke(
            unknownSurface, unknownSurface, CreateStroke()));
        Assert.Throws<ArgumentException>(() => workspace.BeginStroke(
            validPageId, SurfaceId.New(), CreateStroke()));
        Assert.Throws<ArgumentException>(() => workspace.GetDocumentElements(
            document.Id, SurfaceId.New()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoardPoint(double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScreenPoint(0, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PagePoint(double.NegativeInfinity, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportTransform(0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportTransform(double.NaN, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ViewportTransform(1, 0, double.PositiveInfinity));

        Assert.Equal(contentRevision, workspace.ContentRevision);
        Assert.Equal(resourceRevision, workspace.ResourceRevision);
        Assert.Equal(layoutRevision, workspace.Layout.Revision);
        Assert.Equal(readingRevision, workspace.Reading.Revision);
        Assert.Equal(pointCountBefore, workspace.GetElements(validPageId).Count);
    }

    [Fact]
    public void Invalid_page_and_nonfinite_reading_state_are_rejected_without_revision()
    {
        var workspace = new Workspace();
        var assetId = AddAsset(workspace);
        var document = workspace.AddImageDocument(assetId, 40, 20);
        var revision = workspace.Reading.Revision;

        Assert.Throws<ArgumentException>(() => workspace.Reading.SetCurrentPage(document, SurfaceId.New()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            workspace.Reading.SetOffset(document.PageSurfaceIds[0], double.NaN, 0));

        Assert.Equal(revision, workspace.Reading.Revision);
    }

    [Fact]
    public void Reading_offset_rejects_unowned_surface_without_changing_offsets_or_revision()
    {
        var workspace = new Workspace();
        var page = workspace.AddImageDocument(AddAsset(workspace), 40, 20).PageSurfaceIds[0];
        workspace.Reading.SetOffset(page, -30.5, 80.25);
        var revision = workspace.Reading.Revision;
        var offsetsBefore = workspace.Reading.Offsets.ToDictionary(pair => pair.Key, pair => pair.Value);

        Assert.Throws<ArgumentException>(() =>
            workspace.Reading.SetOffset(SurfaceId.New(), 12, 34));

        Assert.Equal(revision, workspace.Reading.Revision);
        Assert.Equal(offsetsBefore.Count, workspace.Reading.Offsets.Count);
        Assert.Equal(offsetsBefore[page], workspace.Reading.Offsets[page]);
    }

    [Fact]
    public void Default_transform_is_rejected_at_begin_stroke_and_apply_without_mutation()
    {
        var workspace = new Workspace();
        var surfaceId = workspace.BoardSurfaceId;
        var invalidStroke = CreateStroke(transform: default(ElementTransform));
        var revision = workspace.ContentRevision;

        Assert.Throws<ArgumentException>(() => workspace.BeginStroke(surfaceId, null, invalidStroke));

        var operation = new ContentOperation(
            workspace,
            ContentOperationId.New(),
            surfaceId,
            [],
            [invalidStroke]);
        Assert.Throws<ArgumentException>(() => workspace.Apply(operation));

        Assert.Equal(revision, workspace.ContentRevision);
        Assert.Empty(workspace.GetElements(surfaceId));
        Assert.Empty(workspace.Surfaces[surfaceId].ElementIds);
    }

    [Fact]
    public void Stroke_bounds_include_transformed_centerline_and_style_radius()
    {
        var stroke = CreateStroke(
            points: ImmutableArray.Create(new PagePoint(0, 0), new PagePoint(2, 4)),
            style: new StrokeStyle(0xFF112233, 2),
            transform: new ElementTransform(2, 3, 90, 10, -4));

        AssertClose(-5, stroke.Bounds.Left);
        AssertClose(-6, stroke.Bounds.Top);
        AssertClose(13, stroke.Bounds.Right);
        AssertClose(2, stroke.Bounds.Bottom);
    }

    [Fact]
    public void Camera_movement_never_creates_a_content_operation()
    {
        var workspace = new Workspace();
        var before = workspace.ContentRevision;

        workspace.Camera.Pan(400, -800);

        Assert.Equal(before, workspace.ContentRevision);
        Assert.Empty(workspace.GetElements(workspace.BoardSurfaceId));
    }

    private static AssetId AddAsset(Workspace workspace)
    {
        var asset = new Asset(AssetId.New(), "image/png", 1);
        workspace.AddAsset(asset);
        return asset.Id;
    }

    private static StrokeElement CreateStroke(
        ImmutableArray<PagePoint>? points = null,
        StrokeStyle? style = null,
        ElementTransform? transform = null) => new(
        ElementId.New(),
        points ?? ImmutableArray.Create(new PagePoint(-2, 3), new PagePoint(4, 5)),
        style ?? new StrokeStyle(0xFF112233, 2),
        transform ?? ElementTransform.Identity);

    private static void AssertPointClose(PagePoint expected, PagePoint actual)
    {
        Assert.InRange(Math.Abs(actual.X - expected.X), 0, 1e-7);
        Assert.InRange(Math.Abs(actual.Y - expected.Y), 0, 1e-7);
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(Math.Abs(actual - expected), 0, 1e-12);
}
