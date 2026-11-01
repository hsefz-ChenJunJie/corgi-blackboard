namespace CorgiBlackboard.Core.Model;

public sealed partial class Workspace
{
    public ImageDocument AddImageDocument(AssetId assetId, double pixelWidth, double pixelHeight)
    {
        if (!_assets.ContainsKey(assetId))
            throw new ArgumentException("The image asset is not registered in this workspace.", nameof(assetId));

        var pageId = SurfaceId.New();
        var document = new ImageDocument(DocumentId.New(), assetId, pixelWidth, pixelHeight, [pageId]);
        _documents.Add(document.Id, document);
        _surfaces.Add(pageId, new Surface(pageId, SurfaceKind.DocumentPage, []));
        _content.Add(pageId, []);
        Reading.SetCurrentPage(document, pageId);
        return document;
    }

    // Resource lifetime is separate from stroke history. A future close command must retain its own undo data.
    public void CloseDocument(DocumentId documentId)
    {
        if (!_documents.TryGetValue(documentId, out var document))
            throw new ArgumentException("Document does not belong to this workspace.", nameof(documentId));

        if (Layout.Windows.ContainsKey(documentId))
            Layout.Close(documentId);
        Reading.Remove(document);
        foreach (var page in document.PageSurfaceIds)
        {
            foreach (var element in _content[page])
                _elements.Remove(element.Id);
            _content.Remove(page);
            _surfaces.Remove(page);
        }
        _documents.Remove(documentId);
        if (_documents.Values.All(other => other.AssetId != document.AssetId))
            _assets.Remove(document.AssetId);
        ResourceRevision++;
    }
}
