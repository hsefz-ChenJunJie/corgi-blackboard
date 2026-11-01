using System.Collections.Immutable;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Model;
using CorgiBlackboard.Core.Operations;

namespace CorgiBlackboard.Core.Ink;

public sealed class EraseOperation
{
    private readonly ContentOperation _contentOperation;

    internal EraseOperation(
        Workspace workspace,
        SurfaceId surfaceId,
        ImmutableArray<StrokeElement> before,
        ImmutableArray<StrokeElement> after)
    {
        Before = before;
        After = after;
        _contentOperation = new ContentOperation(
            workspace, ContentOperationId.New(), surfaceId, [.. before], [.. after]);
    }

    public SurfaceId SurfaceId => _contentOperation.SurfaceId;
    public ImmutableArray<StrokeElement> Before { get; }
    public ImmutableArray<StrokeElement> After { get; }
    public bool IsPending => _contentOperation.IsPending;

    public void Commit() => _contentOperation.Commit();
    public void Cancel() => _contentOperation.Cancel();

    public static EraseOperation? Begin(
        Workspace workspace,
        SurfaceId surfaceId,
        SurfaceId? lockedPageSurfaceId,
        IReadOnlyList<PagePoint> path,
        double radius,
        double? pageWidth = null,
        double? pageHeight = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(path);
        if (surfaceId != workspace.BoardSurfaceId && lockedPageSurfaceId != surfaceId)
            throw new ArgumentException("Document edits must lock the starting page surface.", nameof(lockedPageSurfaceId));

        var page = workspace.Documents.Values
            .FirstOrDefault(document => document.PageSurfaceIds.Contains(surfaceId));
        var before = workspace.GetElements(surfaceId).OfType<StrokeElement>().ToImmutableArray();
        var affected = ImmutableArray.CreateBuilder<StrokeElement>();
        var after = ImmutableArray.CreateBuilder<StrokeElement>();
        foreach (var stroke in before)
        {
            var fragments = StrokeGeometry.Erase(
                stroke, path, radius, page?.PixelWidth ?? pageWidth, page?.PixelHeight ?? pageHeight);
            if (fragments.Length == 1 && ReferenceEquals(fragments[0], stroke))
                continue;
            affected.Add(stroke);
            after.AddRange(fragments);
        }
        if (affected.Count == 0)
            return null;

        return new EraseOperation(workspace, surfaceId, affected.ToImmutable(), after.ToImmutable());
    }
}
