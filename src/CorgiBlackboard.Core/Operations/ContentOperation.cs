using System.Collections.Immutable;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.Core.Operations;

public sealed class ContentOperation
{
    private readonly Workspace _workspace;
    private bool _completed;

    internal ContentOperation(
        Workspace workspace,
        ContentOperationId id,
        SurfaceId surfaceId,
        ImmutableArray<Element> before,
        ImmutableArray<Element> after)
    {
        _workspace = workspace;
        Id = id;
        SurfaceId = surfaceId;
        Before = before;
        After = after;
    }

    public ContentOperationId Id { get; }
    public SurfaceId SurfaceId { get; }
    public ImmutableArray<Element> Before { get; }
    public ImmutableArray<Element> After { get; }
    public bool IsPending => !_completed;

    public void Commit()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Content operation has already completed.");
        }

        _workspace.Apply(this);
        _completed = true;
    }

    public void Cancel()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Content operation has already completed.");
        }

        _completed = true;
    }
}
