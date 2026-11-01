using System.Collections.Immutable;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Ink;
using CorgiBlackboard.Core.Model;
using Xunit;

namespace CorgiBlackboard.Core.Tests;

public sealed partial class StrokeEraseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Erase_preserves_same_page_nonstroke_identity_and_data(bool incremental)
    {
        // Given
        var workspace = new Workspace();
        var asset = new Asset(AssetId.New(), "image/png", 32);
        workspace.AddAsset(asset);
        var document = workspace.AddImageDocument(asset.Id, 100, 100);
        var page = document.PageSurfaceIds.Single();
        var fixture = new NonStrokeFixture(ElementId.New(),
            new ElementTransform(1, 1, 0, 50, 20), [3, 1, 4, 1, 5]);
        var insertion = workspace.BeginElement(page, page, fixture);
        insertion.Commit();
        var stroke = Stroke(new PagePoint(0, 20), new PagePoint(100, 20));
        workspace.BeginStroke(page, page, stroke).Commit();
        var untouched = Stroke(new PagePoint(0, 90), new PagePoint(100, 90));
        workspace.BeginStroke(page, page, untouched).Commit();
        var data = fixture.Data.ToArray();
        var transform = fixture.Transform;
        var revision = workspace.ContentRevision;
        var path = new[] { new PagePoint(50, 10), new PagePoint(50, 30) };

        // When
        EraseOperation? pending;
        if (incremental)
        {
            var gesture = new EraseGesture(workspace, page, page, path[0], 8);
            gesture.Extend(path[1]);
            pending = gesture.CreateOperation();
        }
        else
        {
            pending = EraseOperation.Begin(workspace, page, page, path, 8);
        }
        var erase = Assert.IsType<EraseOperation>(pending);
        erase.Commit();

        // Then
        var elements = workspace.GetDocumentElements(document.Id, page);
        Assert.Same(fixture, elements[0]);
        Assert.Equal(data, fixture.Data.ToArray());
        Assert.Equal(transform, fixture.Transform);
        Assert.Same(fixture, Assert.Single(insertion.After));
        Assert.Same(untouched, elements[1]);
        Assert.Same(stroke, Assert.Single(erase.Before));
        Assert.Equal(2, erase.After.Length);
        AssertClose(40, erase.After[0].Points[^1].X);
        AssertClose(60, erase.After[1].Points[0].X);
        AssertFragments(stroke, erase.After);
        Assert.Equal<Element>([fixture, untouched, .. erase.After], elements);
        Assert.Equal(elements.Select(element => element.Id), workspace.Surfaces[page].ElementIds);
        Assert.Equal(revision + 1, workspace.ContentRevision);
    }

    private sealed record NonStrokeFixture : Element
    {
        public NonStrokeFixture(ElementId id, ElementTransform transform, ImmutableArray<byte> data)
            : base(id, transform) => Data = data;

        public ImmutableArray<byte> Data { get; }
    }
}
