namespace CorgiBlackboard.Core.Input;

/// <summary>
/// The shared toolbar selection used by every content surface. Widths are in DIP; a gesture
/// snapshots the settings when it starts, so changes affect new content only.
/// </summary>
public sealed record ToolSettings
{
    public ToolSettings(InputTool tool, uint inkArgb, double inkWidth, double eraserDiameter)
    {
        if (!Enum.IsDefined(tool))
        {
            throw new ArgumentOutOfRangeException(nameof(tool));
        }

        if (!double.IsFinite(inkWidth) || inkWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inkWidth), "Ink width must be finite and positive.");
        }

        if (!double.IsFinite(eraserDiameter) || eraserDiameter <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eraserDiameter), "Eraser diameter must be finite and positive.");
        }

        Tool = tool;
        InkArgb = inkArgb;
        InkWidth = inkWidth;
        EraserDiameter = eraserDiameter;
    }

    public InputTool Tool { get; }
    public uint InkArgb { get; }
    public double InkWidth { get; }
    public double EraserDiameter { get; }
}
