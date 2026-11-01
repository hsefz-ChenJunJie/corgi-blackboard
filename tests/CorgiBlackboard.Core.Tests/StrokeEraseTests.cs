using System.Collections.Immutable;
using CorgiBlackboard.Core.Ink;
using CorgiBlackboard.Core.Model;
using CorgiBlackboard.Core.Coordinates;
using Xunit;

namespace CorgiBlackboard.Core.Tests;

public sealed partial class StrokeEraseTests
{
    [Fact]
    public void Swept_eraser_splits_line_at_expected_centerline_boundaries()
    {
        var original = Stroke(new PagePoint(0, 0), new PagePoint(100, 0));
        var result = StrokeGeometry.Erase(original, [new PagePoint(50, -10), new PagePoint(50, 10)], 8);

        Assert.Equal(2, result.Length);
        AssertClose(40, result[0].Points[^1].X);
        AssertClose(60, result[1].Points[0].X);
        AssertFragments(original, result);
    }

    [Fact]
    public void Tangent_sweep_does_not_create_ghost_fragment()
    {
        var original = Stroke(new PagePoint(0, 0), new PagePoint(100, 0));
        var result = StrokeGeometry.Erase(original, [new PagePoint(50, 10), new PagePoint(50, 20)], 8);
        Assert.Same(original, Assert.Single(result));
    }

    [Fact]
    public void Repeated_zero_length_points_do_not_bridge_erased_interval()
    {
        var original = Stroke(new PagePoint(0, 0), new PagePoint(0, 0),
            new PagePoint(50, 0), new PagePoint(50, 0), new PagePoint(100, 0));
        var result = StrokeGeometry.Erase(original,
            [new PagePoint(50, -10), new PagePoint(50, 10)], 8);

        Assert.Equal(2, result.Length);
        Assert.Equal(new PagePoint(0, 0), result[0].Points[0]);
        Assert.Equal(new PagePoint(40, 0), result[0].Points[^1]);
        Assert.Equal(new PagePoint(60, 0), result[1].Points[0]);
        Assert.Equal(new PagePoint(100, 0), result[1].Points[^1]);
    }

    [Fact]
    public void Page_clipping_interpolates_boundary_crossings_before_erasing()
    {
        var original = Stroke(new PagePoint(-20, 20), new PagePoint(120, 20));
        var result = StrokeGeometry.Erase(original,
            [new PagePoint(50, 10), new PagePoint(50, 30)], 8, 100, 100);

        Assert.Equal(2, result.Length);
        Assert.Equal(new PagePoint(0, 20), result[0].Points[0]);
        Assert.Equal(new PagePoint(40, 20), result[0].Points[^1]);
        Assert.Equal(new PagePoint(60, 20), result[1].Points[0]);
        Assert.Equal(new PagePoint(100, 20), result[1].Points[^1]);
    }

    [Fact]
    public void Unbounded_board_erase_preserves_negative_stroke_coordinates()
    {
        var workspace = new Workspace();
        var stroke = Stroke(new PagePoint(-100, 0), new PagePoint(-20, 0));
        workspace.BeginStroke(workspace.BoardSurfaceId, null, stroke).Commit();

        var erase = EraseOperation.Begin(workspace, workspace.BoardSurfaceId, null,
            [new PagePoint(-60, -10), new PagePoint(-60, 10)], 8);

        Assert.NotNull(erase);
        Assert.Equal(2, erase!.After.Length);
        Assert.Equal(new PagePoint(-100, 0), erase.After[0].Points[0]);
        Assert.Equal(new PagePoint(-70, 0), erase.After[0].Points[^1]);
        Assert.Equal(new PagePoint(-50, 0), erase.After[1].Points[0]);
        Assert.Equal(new PagePoint(-20, 0), erase.After[1].Points[^1]);
    }

    [Fact]
    public void Unbounded_stroke_crossing_zero_keeps_its_unhit_negative_portion()
    {
        var stroke = Stroke(new PagePoint(-20, 0), new PagePoint(20, 0));
        var result = StrokeGeometry.Erase(stroke, [new PagePoint(10, 0)], 8);

        var retained = Assert.Single(result);
        Assert.Equal(new PagePoint(-20, 0), retained.Points[0]);
        Assert.Equal(new PagePoint(0, 0), retained.Points[^1]);
    }

    [Fact]
    public void Single_point_at_exact_combined_radius_is_erased_inclusively()
    {
        var point = new StrokeElement(ElementId.New(), [new PagePoint(0, 0)],
            new StrokeStyle(0xFF123456, 4), ElementTransform.Identity);

        Assert.Empty(StrokeGeometry.Erase(point, [new PagePoint(10, 0)], 8));
    }

    [Fact]
    public void Stationary_tangent_with_repeated_endpoints_is_a_no_op()
    {
        var workspace = new Workspace();
        var stroke = Stroke(new PagePoint(0, 0), new PagePoint(0, 0),
            new PagePoint(100, 0), new PagePoint(100, 0));
        workspace.BeginStroke(workspace.BoardSurfaceId, null, stroke).Commit();
        var revision = workspace.ContentRevision;
        var tangent = new PagePoint(50, 10);

        var operation = EraseOperation.Begin(workspace, workspace.BoardSurfaceId, null,
            [tangent, tangent], 8);

        Assert.Null(operation);
        Assert.Equal(revision, workspace.ContentRevision);
        Assert.Same(stroke, Assert.Single(workspace.GetElements(workspace.BoardSurfaceId)));
    }

    [Fact]
    public void Fit_scale_half_converts_sweep_radius_and_tangent_to_page_units()
    {
        var fit = new ViewportTransform(0.5, 0, 0);
        var stroke = Stroke(new PagePoint(0, 0), new PagePoint(100, 0));
        var sweepStart = fit.ToPage(new ScreenPoint(25, -5));
        var sweepEnd = fit.ToPage(new ScreenPoint(25, 5));
        var split = StrokeGeometry.Erase(stroke, [sweepStart, sweepEnd], 4 / fit.Scale);

        Assert.Equal(2, split.Length);
        AssertClose(40, split[0].Points[^1].X);
        AssertClose(60, split[1].Points[0].X);

        var tangent = fit.ToPage(new ScreenPoint(25, 5));
        Assert.Same(stroke, Assert.Single(StrokeGeometry.Erase(stroke, [tangent], 4 / fit.Scale)));
    }

    [Fact]
    public void Point_stroke_can_be_fully_covered_or_preserved()
    {
        var point = Stroke(new PagePoint(10, 10));
        Assert.Empty(StrokeGeometry.Erase(point, [new PagePoint(10, 10)], 1));
        Assert.Same(point, Assert.Single(StrokeGeometry.Erase(point, [new PagePoint(20, 20)], 1)));
    }

    [Fact]
    public void Full_cover_and_no_hit_are_handled()
    {
        var original = Stroke(new PagePoint(0, 0), new PagePoint(100, 0));
        Assert.Empty(StrokeGeometry.Erase(original, [new PagePoint(-1, 0), new PagePoint(101, 0)], 10));
        Assert.Same(original, Assert.Single(StrokeGeometry.Erase(original, [new PagePoint(50, 100)], 2)));
    }

    [Fact]
    public void Repeated_points_produce_finite_fragments()
    {
        var original = Stroke(new PagePoint(0, 0), new PagePoint(0, 0),
            new PagePoint(50, 0), new PagePoint(50, 0), new PagePoint(100, 0));
        var result = StrokeGeometry.Erase(original, [new PagePoint(50, -10), new PagePoint(50, 10)], 8);
        Assert.Equal(2, result.Length);
        Assert.All(result.SelectMany(fragment => fragment.Points), p =>
        {
            Assert.True(double.IsFinite(p.X));
            Assert.True(double.IsFinite(p.Y));
        });
    }

    [Fact]
    public void Erase_is_limited_to_start_page_and_cancellation_restores_content()
    {
        var workspace = new Workspace();
        var asset = new Asset(AssetId.New(), "image/png", 8);
        workspace.AddAsset(asset);
        var first = workspace.AddImageDocument(asset.Id, 100, 100);
        var second = workspace.AddImageDocument(asset.Id, 100, 100);
        var page = first.PageSurfaceIds.Single();
        var otherPage = second.PageSurfaceIds.Single();
        var stroke = Stroke(new PagePoint(0, 20), new PagePoint(100, 20));
        var otherStroke = Stroke(new PagePoint(0, 20), new PagePoint(100, 20));
        workspace.BeginStroke(page, page, stroke).Commit();
        workspace.BeginStroke(otherPage, otherPage, otherStroke).Commit();
        var registeredAsset = workspace.Assets[asset.Id];
        var erase = EraseOperation.Begin(workspace, page, page,
            [new PagePoint(50, 10), new PagePoint(50, 30)], 8);

        Assert.NotNull(erase);
        Assert.Same(stroke, Assert.Single(workspace.GetElements(page)));
        Assert.Same(otherStroke, Assert.Single(workspace.GetElements(otherPage)));
        Assert.Same(stroke, Assert.Single(erase!.Before));
        Assert.Equal(2, erase.After.Length);
        erase.Cancel();
        Assert.Same(stroke, Assert.Single(workspace.GetElements(page)));
        Assert.Same(otherStroke, Assert.Single(workspace.GetElements(otherPage)));
        Assert.Same(registeredAsset, workspace.Assets[asset.Id]);
        Assert.Null(EraseOperation.Begin(workspace, page, page, [new PagePoint(50, 90)], 1));
    }

    [Fact]
    public void Second_contact_cancel_keeps_before_content_and_single_pending_operation()
    {
        var workspace = new Workspace();
        var stroke = Stroke(new PagePoint(0, 0), new PagePoint(100, 0));
        workspace.BeginStroke(workspace.BoardSurfaceId, null, stroke).Commit();
        var pending = EraseOperation.Begin(workspace, workspace.BoardSurfaceId, null,
            [new PagePoint(50, -10), new PagePoint(50, 10)], 8);

        Assert.NotNull(pending);
        Assert.Single(workspace.GetElements(workspace.BoardSurfaceId));
        Assert.Equal(1, workspace.ContentRevision);
        pending!.Cancel();
        Assert.Same(stroke, Assert.Single(workspace.GetElements(workspace.BoardSurfaceId)));
        Assert.Equal(1, workspace.ContentRevision);
    }

    [Fact]
    public void Commit_applies_all_fragments_as_one_content_revision()
    {
        var workspace = new Workspace();
        var stroke = Stroke(new PagePoint(0, 0), new PagePoint(100, 0));
        workspace.BeginStroke(workspace.BoardSurfaceId, null, stroke).Commit();
        var revision = workspace.ContentRevision;
        var erase = EraseOperation.Begin(workspace, workspace.BoardSurfaceId, null,
            [new PagePoint(50, -10), new PagePoint(50, 10)], 8);

        Assert.NotNull(erase);
        Assert.Equal(2, erase!.After.Length);
        erase.Commit();

        Assert.Equal(revision + 1, workspace.ContentRevision);
        Assert.Equal(2, workspace.GetElements(workspace.BoardSurfaceId).Count);
        Assert.DoesNotContain(stroke, workspace.GetElements(workspace.BoardSurfaceId));
    }

    private static StrokeElement Stroke(params PagePoint[] points) => new(
        ElementId.New(), points.ToImmutableArray(), new StrokeStyle(0xFF123456, 4), ElementTransform.Identity);

    private static void AssertFragments(StrokeElement original, ImmutableArray<StrokeElement> fragments)
    {
        Assert.All(fragments, fragment =>
        {
            Assert.Equal(original.Style, fragment.Style);
            Assert.Equal(original.Transform, fragment.Transform);
            Assert.Equal(original.Id, fragment.SourceElementId);
        });
    }

    private static void AssertClose(double expected, double actual) =>
        Assert.InRange(Math.Abs(expected - actual), 0, 1e-7);
}
