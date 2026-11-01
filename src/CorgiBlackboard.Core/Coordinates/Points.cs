namespace CorgiBlackboard.Core.Coordinates;

public readonly record struct BoardPoint
{
    public BoardPoint(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Point coordinates must be finite.");
        }

        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }
}

public readonly record struct ScreenPoint
{
    public ScreenPoint(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Point coordinates must be finite.");
        }

        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }
}

public readonly record struct PagePoint
{
    public PagePoint(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Point coordinates must be finite.");
        }

        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }
}

public readonly record struct ViewportTransform
{
    public ViewportTransform(double scale, double offsetX, double offsetY)
    {
        if (!double.IsFinite(scale) || scale <= 0
            || !double.IsFinite(offsetX) || !double.IsFinite(offsetY))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Viewport values must be finite and scale must be positive.");
        }

        Scale = scale;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public double Scale { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }

    public ScreenPoint ToScreen(BoardPoint point)
    {
        Validate();
        return new((point.X * Scale) + OffsetX, (point.Y * Scale) + OffsetY);
    }

    public BoardPoint ToBoard(ScreenPoint point) =>
        new((point.X - OffsetX) / Scale, (point.Y - OffsetY) / Scale);

    public ScreenPoint ToScreen(PagePoint point)
    {
        Validate();
        return new((point.X * Scale) + OffsetX, (point.Y * Scale) + OffsetY);
    }

    public PagePoint ToPage(ScreenPoint point) =>
        new((point.X - OffsetX) / Scale, (point.Y - OffsetY) / Scale);

    private void Validate()
    {
        if (!double.IsFinite(Scale) || Scale <= 0
            || !double.IsFinite(OffsetX) || !double.IsFinite(OffsetY))
        {
            throw new InvalidOperationException("Viewport transform is invalid.");
        }
    }
}
