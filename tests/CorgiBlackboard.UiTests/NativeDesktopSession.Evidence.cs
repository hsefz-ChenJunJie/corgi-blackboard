using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core.Definitions;

namespace CorgiBlackboard.UiTests;

internal sealed partial class NativeDesktopSession
{
    internal void CaptureEvidence()
    {
        QaContract.Require(DwmFlush() == 0, "DwmFlush failed.");
        var foreground = Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Window)) ?? Window;
        foreground.CaptureToFile(Path.Combine(evidence, "screen.png"));
        var elements = Window.FindAllDescendants().Prepend(Window).Select(element => new
        {
            id = element.Properties.AutomationId.ValueOrDefault, name = element.Properties.Name.ValueOrDefault,
            type = element.Properties.ControlType.ValueOrDefault.ToString(),
            physicalPixels = element.Properties.BoundingRectangle.ValueOrDefault,
            isOffscreen = element.Properties.IsOffscreen.ValueOrDefault, status = element.Properties.ItemStatus.ValueOrDefault
        }).ToArray();
        File.WriteAllText(Path.Combine(evidence, "uia.json"), JsonSerializer.Serialize(elements, QaContract.Json));
        File.WriteAllText(Path.Combine(evidence, "snapshot.json"), Snapshot.ToJsonString(QaContract.Json));
    }

    internal void Stage(string name)
    {
        QaContract.Require(DwmFlush() == 0, "DwmFlush failed.");
        var foreground = Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Window)) ?? Window;
        foreground.CaptureToFile(Path.Combine(evidence, name + ".png"));
        File.WriteAllText(Path.Combine(evidence, name + ".json"), Snapshot.ToJsonString(QaContract.Json));
        trace(new { capture = name + ".png", observedRevision = QaContract.Number(Snapshot, "revision") });
    }

    internal void Pixel(string id, double x, double y, Color expected)
    {
        QaContract.Require(DwmFlush() == 0, "DwmFlush failed.");
        var point = Screen(id, x, y);
        using var capture = FlaUI.Core.Capturing.Capture.Rectangle(new Rectangle(point.X, point.Y, 1, 1));
        var actual = capture.Bitmap.GetPixel(0, 0);
        QaContract.Require(Math.Abs(actual.R - expected.R) <= 24 && Math.Abs(actual.G - expected.G) <= 24 &&
            Math.Abs(actual.B - expected.B) <= 24, $"Rendered pixel {actual} differs from {expected} at {point}.");
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
