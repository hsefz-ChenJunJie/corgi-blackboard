namespace CorgiBlackboard.Core.Model;

public readonly record struct WorkspaceId(Guid Value)
{
    public static WorkspaceId New() => new(Guid.NewGuid());
}

public readonly record struct SurfaceId(Guid Value)
{
    public static SurfaceId New() => new(Guid.NewGuid());
}

public readonly record struct DocumentId(Guid Value)
{
    public static DocumentId New() => new(Guid.NewGuid());
}

public readonly record struct AssetId(Guid Value)
{
    public static AssetId New() => new(Guid.NewGuid());
}

public readonly record struct ElementId(Guid Value)
{
    public static ElementId New() => new(Guid.NewGuid());
}

public readonly record struct ContentOperationId(Guid Value)
{
    public static ContentOperationId New() => new(Guid.NewGuid());
}
