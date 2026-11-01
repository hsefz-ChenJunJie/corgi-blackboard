using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Ink;
using CorgiBlackboard.Core.Model;
using Xunit;

namespace CorgiBlackboard.Core.Tests;

public sealed class EraseGestureTests
{
    [Fact]
    public void Incremental_board_gesture_matches_one_shot_erase_after_every_sample()
    {
        var workspace = new Workspace();
        var board = workspace.BoardSurfaceId;
        var upper = Commit(workspace, board, null, Stroke((0, 40), (200, 40)));
        var lower = Commit(workspace, board, null, Stroke((0, 120), (200, 120)));
        var far = Commit(workspace, board, null, Stroke((0, 300), (200, 300)));
        var dot = Commit(workspace, board, null, Stroke((300, 180)));
        PagePoint[] path = [new(40, 0), new(40, 160), new(160, 0), new(300, 180), new(400, 400)];

        var final = AssertMatchesOneShot(workspace, board, null, path, 6);

        Assert.Equal(new[] { upper.Id, lower.Id, dot.Id }, final.Before.Select(stroke => stroke.Id));
        Assert.DoesNotContain(final.After, fragment => fragment.SourceElementId == far.Id || fragment.SourceElementId == dot.Id);
        Assert.Equal(4, workspace.ContentRevision);
    }

    [Fact]
    public void Incremental_page_gesture_matches_one_shot_erase_with_page_clipping()
    {
        var workspace = new Workspace();
        var asset = new Asset(AssetId.New(), "image/png", 1);
        workspace.AddAsset(asset);
        var page = workspace.AddImageDocument(asset.Id, 100, 100).PageSurfaceIds[0];
        var crossing = Commit(workspace, page, page, Stroke((-20, 50), (120, 50)));
        Commit(workspace, page, page, Stroke((10, 10), (30, 10)));
        Commit(workspace, page, page, Stroke((150, 50), (180, 50)));
        PagePoint[] path = [new(50, 0), new(50, 30), new(50, 60), new(50, 100), new(170, 60)];

        var final = AssertMatchesOneShot(workspace, page, page, path, 6);

        Assert.Equal(crossing.Id, Assert.Single(final.Before).Id);
        Assert.Equal(2, final.After.Length);
        Assert.Equal(0, final.After[0].Points[0].X, 9);
        Assert.Equal(100, final.After[1].Points[^1].X, 9);
        Assert.All(final.After.SelectMany(fragment => fragment.Points), point => Assert.Equal(50, point.Y, 9));
    }

    [Fact]
    public void Fragments_keep_original_lineage_stable_ids_and_stay_pending_until_commit()
    {
        var workspace = new Workspace();
        var board = workspace.BoardSurfaceId;
        var line = Commit(workspace, board, null, Stroke((0, 0), (200, 0)));
        var gesture = new EraseGesture(workspace, board, null, new PagePoint(50, -20), 5);

        Assert.True(gesture.Extend(new PagePoint(50, 20)));
        var firstCut = gesture.CreateOperation()!;
        var firstIds = firstCut.After.Select(fragment => fragment.Id).ToArray();
        Assert.False(gesture.Extend(new PagePoint(50, 20)));
        Assert.False(gesture.Extend(new PagePoint(206, 20)));
        Assert.False(gesture.Extend(new PagePoint(207, 7)));
        Assert.Equal(firstIds, gesture.CreateOperation()!.After.Select(fragment => fragment.Id));

        Assert.False(gesture.Extend(new PagePoint(150, 20)));
        Assert.True(gesture.Extend(new PagePoint(150, -20)));
        var operation = gesture.CreateOperation()!;

        Assert.Same(line, Assert.Single(operation.Before));
        Assert.Equal(3, operation.After.Length);
        Assert.All(operation.After, fragment => Assert.Equal(line.Id, fragment.SourceElementId));
        Assert.Equal(4, operation.After.Select(fragment => fragment.Id).Append(line.Id).Distinct().Count());
        Assert.Same(line, Assert.Single(workspace.GetElements(board)));
        Assert.Equal(1, workspace.ContentRevision);

        operation.Commit();

        Assert.Equal(operation.After, workspace.GetElements(board));
        Assert.Equal(2, workspace.ContentRevision);
        Assert.Throws<InvalidOperationException>(firstCut.Commit);
        Assert.Equal(2, workspace.ContentRevision);
    }

    [Fact]
    public void Tangent_or_distant_sweeps_create_no_operation()
    {
        var workspace = new Workspace();
        var board = workspace.BoardSurfaceId;
        Commit(workspace, board, null, Stroke((0, 0), (100, 0)));
        var gesture = new EraseGesture(workspace, board, null, new PagePoint(50, 8), 6);

        Assert.False(gesture.Extend(new PagePoint(60, 8)));
        Assert.False(gesture.Extend(new PagePoint(60, 400)));
        Assert.Null(gesture.CreateOperation());
        Assert.Null(EraseOperation.Begin(workspace, board, null, gesture.Path, 6));
    }

    private static EraseOperation AssertMatchesOneShot(
        Workspace workspace, SurfaceId surface, SurfaceId? lockedPage, PagePoint[] path, double radius)
    {
        var revision = workspace.ContentRevision;
        var gesture = new EraseGesture(workspace, surface, lockedPage, path[0], radius);
        EraseOperation? actual = null;
        for (var count = 1; count <= path.Length; count++)
        {
            if (count > 1)
            {
                gesture.Extend(path[count - 1]);
            }

            actual = gesture.CreateOperation();
            var expected = EraseOperation.Begin(workspace, surface, lockedPage, path[..count], radius);
            AssertEquivalent(expected, actual);
            expected?.Cancel();
        }

        Assert.Equal(revision, workspace.ContentRevision);
        return Assert.IsType<EraseOperation>(actual);
    }

    private static void AssertEquivalent(EraseOperation? expected, EraseOperation? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.SurfaceId, actual.SurfaceId);
        Assert.Equal(expected.Before.Select(stroke => stroke.Id), actual.Before.Select(stroke => stroke.Id));
        Assert.Equal(expected.After.Length, actual.After.Length);
        for (var index = 0; index < expected.After.Length; index++)
        {
            var wanted = expected.After[index];
            var fragment = actual.After[index];
            Assert.Equal(wanted.SourceElementId, fragment.SourceElementId);
            Assert.Equal(wanted.Style, fragment.Style);
            Assert.Equal(wanted.Points.Length, fragment.Points.Length);
            for (var point = 0; point < wanted.Points.Length; point++)
            {
                Assert.Equal(wanted.Points[point].X, fragment.Points[point].X, 9);
                Assert.Equal(wanted.Points[point].Y, fragment.Points[point].Y, 9);
            }
        }
    }

    private static StrokeElement Commit(Workspace workspace, SurfaceId surface, SurfaceId? lockedPage, StrokeElement stroke)
    {
        workspace.BeginStroke(surface, lockedPage, stroke).Commit();
        return stroke;
    }

    private static StrokeElement Stroke(params (double X, double Y)[] points) =>
        new(ElementId.New(), [.. points.Select(point => new PagePoint(point.X, point.Y))],
            new StrokeStyle(0xFFFFFFFF, 4), ElementTransform.Identity);
}
