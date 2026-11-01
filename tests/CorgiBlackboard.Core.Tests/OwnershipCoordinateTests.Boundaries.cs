using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Model;
using Xunit;

namespace CorgiBlackboard.Core.Tests;

public sealed partial class OwnershipCoordinateTests
{
    [Fact]
    public void Window_move_preserves_page_stroke_points_and_content_revision()
    {
        // Given
        var workspace = new Workspace();
        var asset = new Asset(AssetId.New(), "image/png", 32);
        workspace.AddAsset(asset);
        var document = workspace.AddImageDocument(asset.Id, 800, 600);
        var page = document.PageSurfaceIds.Single();
        workspace.BeginStroke(page, page, CreateStroke()).Commit();
        workspace.Layout.SetTeachingArea(1024, 680);
        workspace.Layout.Open(document.Id, new WindowRectangle(33, 44, 500, 400));
        var points = Assert.IsType<StrokeElement>(
            Assert.Single(workspace.GetDocumentElements(document.Id, page))).Points.ToArray();
        var contentRevision = workspace.ContentRevision;
        var layoutRevision = workspace.Layout.Revision;
        var readingRevision = workspace.Reading.Revision;
        var cameraRevision = workspace.Camera.Revision;

        // When
        workspace.Layout.Move(document.Id, 100, 75);

        // Then
        var stroke = Assert.IsType<StrokeElement>(
            Assert.Single(workspace.GetDocumentElements(document.Id, page)));
        Assert.Equal(points, stroke.Points.ToArray());
        Assert.Equal(contentRevision, workspace.ContentRevision);
        Assert.Equal(new WindowRectangle(133, 119, 500, 400),
            workspace.Layout.Windows[document.Id].NormalRectangle);
        Assert.Equal(layoutRevision + 1, workspace.Layout.Revision);
        Assert.Equal(readingRevision, workspace.Reading.Revision);
        Assert.Equal(cameraRevision, workspace.Camera.Revision);
    }
}
