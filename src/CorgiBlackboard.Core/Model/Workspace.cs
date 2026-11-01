using System.Collections.Immutable;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Operations;

namespace CorgiBlackboard.Core.Model;

public enum SurfaceKind
{
    Board,
    DocumentPage
}

public sealed record Surface(SurfaceId Id, SurfaceKind Kind, ImmutableArray<ElementId> ElementIds);

public sealed record Asset
{
    public Asset(AssetId id, string mediaType, long length)
    {
        if (id.Value == Guid.Empty || string.IsNullOrWhiteSpace(mediaType) || length < 0)
        {
            throw new ArgumentException("Asset identity, media type, and length must be valid.");
        }

        Id = id;
        MediaType = mediaType;
        Length = length;
    }

    public AssetId Id { get; }
    public string MediaType { get; }
    public long Length { get; }
}

public sealed record ImageDocument
{
    public ImageDocument(
        DocumentId id,
        AssetId assetId,
        double pixelWidth,
        double pixelHeight,
        ImmutableArray<SurfaceId> pageSurfaceIds)
    {
        if (id.Value == Guid.Empty || assetId.Value == Guid.Empty
            || !double.IsFinite(pixelWidth) || pixelWidth <= 0
            || !double.IsFinite(pixelHeight) || pixelHeight <= 0
            || pageSurfaceIds.IsDefault || pageSurfaceIds.Length != 1 || pageSurfaceIds[0].Value == Guid.Empty)
        {
            throw new ArgumentException("An image document must have valid dimensions, identity, and exactly one page surface.");
        }

        Id = id;
        AssetId = assetId;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        PageSurfaceIds = pageSurfaceIds;
    }

    public DocumentId Id { get; }
    public AssetId AssetId { get; }
    public double PixelWidth { get; }
    public double PixelHeight { get; }
    public ImmutableArray<SurfaceId> PageSurfaceIds { get; }
}

public sealed class ReadingState
{
    private readonly Workspace _workspace;
    private readonly Dictionary<DocumentId, SurfaceId> _currentPages = [];
    private readonly Dictionary<SurfaceId, (double X, double Y)> _offsets = [];

    internal ReadingState(Workspace workspace) => _workspace = workspace;

    public long Revision { get; private set; }
    public event Action<SurfaceId>? Changed;
    public IReadOnlyDictionary<DocumentId, SurfaceId> CurrentPages => _currentPages;
    public IReadOnlyDictionary<SurfaceId, (double X, double Y)> Offsets => _offsets;

    public void SetCurrentPage(ImageDocument document, SurfaceId pageSurfaceId)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!_workspace.OwnsPage(document.Id, pageSurfaceId))
        {
            throw new ArgumentException("Current page must belong to the document.", nameof(pageSurfaceId));
        }

        _currentPages[document.Id] = pageSurfaceId;
        Revision++;
        Changed?.Invoke(pageSurfaceId);
    }

    public void SetOffset(SurfaceId surfaceId, double x, double y)
    {
        if (!_workspace.OwnsSurface(surfaceId))
        {
            throw new ArgumentException("Reading offsets require a workspace-owned surface.", nameof(surfaceId));
        }

        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Reading offsets must be finite.");
        }

        _offsets[surfaceId] = (x, y);
        Revision++;
        Changed?.Invoke(surfaceId);
    }

    internal void Remove(ImageDocument document)
    {
        _currentPages.Remove(document.Id);
        Revision++;
        foreach (var page in document.PageSurfaceIds)
        {
            _offsets.Remove(page);
            Changed?.Invoke(page);
        }
    }
}

public sealed class BoardCamera
{
    public ViewportTransform Transform { get; private set; } = new(1, 0, 0);
    public long Revision { get; private set; }
    public event Action? Changed;

    public void Pan(double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
        {
            throw new ArgumentOutOfRangeException(nameof(deltaX), "Camera movement must be finite.");
        }

        Transform = new ViewportTransform(
            Transform.Scale,
            Transform.OffsetX + deltaX,
            Transform.OffsetY + deltaY);
        Revision++;
        Changed?.Invoke();
    }
}

public sealed partial class Workspace
{
    private readonly Dictionary<SurfaceId, Surface> _surfaces = [];
    private readonly Dictionary<AssetId, Asset> _assets = [];
    private readonly Dictionary<DocumentId, ImageDocument> _documents = [];
    private readonly Dictionary<ElementId, Element> _elements = [];
    private readonly Dictionary<SurfaceId, List<Element>> _content = [];

    public Workspace()
    {
        Id = WorkspaceId.New();
        BoardSurfaceId = SurfaceId.New();
        _surfaces.Add(BoardSurfaceId, new Surface(BoardSurfaceId, SurfaceKind.Board, []));
        _content.Add(BoardSurfaceId, []);
        Reading = new ReadingState(this);
        Layout = new WindowLayoutState(this);
    }

    public WorkspaceId Id { get; }
    public SurfaceId BoardSurfaceId { get; }
    public long ContentRevision { get; private set; }
    public long ResourceRevision { get; private set; }
    public BoardCamera Camera { get; } = new();
    public WindowLayoutState Layout { get; }
    public ReadingState Reading { get; }
    public IReadOnlyDictionary<SurfaceId, Surface> Surfaces => _surfaces;
    public IReadOnlyDictionary<AssetId, Asset> Assets => _assets;
    public IReadOnlyDictionary<DocumentId, ImageDocument> Documents => _documents;
    public event Action<ContentOperation>? ContentChanged;

    public void AddAsset(Asset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!_assets.TryAdd(asset.Id, asset))
        {
            throw new InvalidOperationException("Asset ID already exists.");
        }

        ResourceRevision++;
    }

    public IReadOnlyList<Element> GetElements(SurfaceId surfaceId)
    {
        if (!_content.TryGetValue(surfaceId, out var elements))
        {
            throw new ArgumentException("Surface does not belong to this workspace.", nameof(surfaceId));
        }

        return elements.AsReadOnly();
    }

    public IReadOnlyList<Element> GetDocumentElements(DocumentId documentId, SurfaceId pageSurfaceId)
    {
        if (!OwnsPage(documentId, pageSurfaceId))
        {
            throw new ArgumentException("Page surface does not belong to the document.", nameof(pageSurfaceId));
        }

        return GetElements(pageSurfaceId);
    }

    public ContentOperation BeginStroke(SurfaceId surfaceId, SurfaceId? lockedPageSurfaceId, StrokeElement stroke)
        => BeginElement(surfaceId, lockedPageSurfaceId, stroke);

    public ContentOperation BeginElement(SurfaceId surfaceId, SurfaceId? lockedPageSurfaceId, Element element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_content.ContainsKey(surfaceId))
        {
            throw new ArgumentException("Surface does not belong to this workspace.", nameof(surfaceId));
        }

        if (surfaceId != BoardSurfaceId)
        {
            if (lockedPageSurfaceId != surfaceId
                || !_documents.Values.Any(document => document.PageSurfaceIds.Contains(surfaceId)))
            {
                throw new ArgumentException("Document edits must lock the starting page surface.", nameof(lockedPageSurfaceId));
            }
        }
        else if (lockedPageSurfaceId is not null)
        {
            throw new ArgumentException("Board edits do not have a document page.", nameof(lockedPageSurfaceId));
        }

        if (_elements.ContainsKey(element.Id))
        {
            throw new ArgumentException("Element ID already exists.", nameof(element));
        }

        ValidateElement(element);
        return new ContentOperation(this, ContentOperationId.New(), surfaceId, [], [element]);
    }

    internal void Apply(ContentOperation operation)
    {
        if (!_content.TryGetValue(operation.SurfaceId, out var surfaceContent))
        {
            throw new InvalidOperationException("Operation surface no longer belongs to this workspace.");
        }

        foreach (var before in operation.Before)
        {
            if (!_elements.ContainsKey(before.Id) || !surfaceContent.Contains(before))
            {
                throw new InvalidOperationException("Operation before-state does not match workspace content.");
            }
        }

        foreach (var after in operation.After)
        {
            ValidateElement(after);
            if (_elements.ContainsKey(after.Id) && operation.Before.All(before => before.Id != after.Id))
            {
                throw new InvalidOperationException("Operation would duplicate an element ID.");
            }
        }

        foreach (var before in operation.Before)
        {
            surfaceContent.Remove(before);
            _elements.Remove(before.Id);
        }

        foreach (var after in operation.After)
        {
            surfaceContent.Add(after);
            _elements.Add(after.Id, after);
        }

        _surfaces[operation.SurfaceId] = _surfaces[operation.SurfaceId] with
        {
            ElementIds = surfaceContent.Select(element => element.Id).ToImmutableArray()
        };
        ContentRevision++;
        ContentChanged?.Invoke(operation);
    }

    internal bool OwnsSurface(SurfaceId surfaceId) => _surfaces.ContainsKey(surfaceId);

    internal bool OwnsPage(DocumentId documentId, SurfaceId surfaceId) =>
        _documents.TryGetValue(documentId, out var document)
        && document.PageSurfaceIds.Contains(surfaceId)
        && _surfaces.TryGetValue(surfaceId, out var surface)
        && surface.Kind == SurfaceKind.DocumentPage;

    private static void ValidateElement(Element element)
    {
        if (!element.Transform.IsValid)
        {
            throw new ArgumentException("Element transform must be valid.", nameof(element));
        }
    }
}
