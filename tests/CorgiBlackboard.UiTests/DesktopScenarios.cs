using System.Drawing;
using System.Text.Json.Nodes;
using static CorgiBlackboard.UiTests.QaContract;

namespace CorgiBlackboard.UiTests;

internal static partial class DesktopScenarios
{
    internal static void Run(string scenario, NativeDesktopSession app)
    {
        switch (scenario)
        {
            case "ShellLayout": ShellLayout(app); break;
            case "BoardInkErase": BoardInkErase(app); break;
            case "TouchCancel": TouchCancel(app); break;
            case "ImageWindows": ImageWindows(app); break;
            case "ImageFitRestore": ImageFitRestore(app); break;
            case "DiagnosticsExport": DiagnosticsExport(app); break;
            case "HarnessFailure": app.Find("IntentionallyMissingControl"); break;
            case "ReleaseSmoke": ReleaseSmoke(app); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    private static void ReleaseSmoke(NativeDesktopSession app)
    {
        var version = app.Snapshot["version"]!.GetValue<string>();
        Require(version.StartsWith("0.1.0-test.", StringComparison.Ordinal), "ReleaseSmoke requires the versioned Test package.");
        foreach (var name in new[] { "TEST-CHECKLIST.md", "THIRD-PARTY-NOTICES.md" })
            Require(File.Exists(Path.Combine(app.PackageDirectory, name)), "Package missing " + name);
        ShellLayout(app);
        BoardInkErase(app);
        ImageFitRestore(app);
        DiagnosticsExport(app);
    }

    private static void ShellLayout(NativeDesktopSession app)
    {
        foreach (var size in new[] { (1024, 768), (1920, 1080) })
        {
            app.Resize(size.Item1, size.Item2);
            var shell = app.Window.BoundingRectangle;
            foreach (var id in new[] { "BoardSurface", "Tool.Pen", "Tool.Eraser", "Tool.Color.White",
                "Tool.Color.Red", "Tool.Color.Yellow", "Tool.Color.Green", "Tool.Color.Blue",
                "Tool.Width.2", "Tool.Width.4", "Tool.Width.8", "Tool.Width.12",
                "Tool.EraserSize.12", "Tool.EraserSize.24", "Tool.EraserSize.48",
                "OpenImage", "Diagnostics.Open", "MinimizedDocuments" })
            {
                var control = app.Find(id);
                var rect = control.BoundingRectangle;
                Require(!control.IsOffscreen && shell.Contains(rect) && !string.IsNullOrWhiteSpace(control.Name),
                    "Shell target hidden, clipped or unnamed: " + id);
                Require(rect.Width / app.Scale >= 44 && rect.Height / app.Scale >= 44, "Target below 44 DIP: " + id);
            }
            foreach (var id in new[] { "Tool.Eraser", "Tool.Pen", "Tool.Color.Red", "Tool.Color.White",
                "Tool.Width.8", "Tool.Width.4", "Tool.EraserSize.48", "Tool.EraserSize.24" })
                app.Select(id);
            foreach (var group in new[]
            {
                new[] { "Tool.Pen", "Tool.Eraser" },
                new[] { "Tool.Color.White", "Tool.Color.Red", "Tool.Color.Yellow", "Tool.Color.Green", "Tool.Color.Blue" },
                new[] { "Tool.Width.2", "Tool.Width.4", "Tool.Width.8", "Tool.Width.12" },
                new[] { "Tool.EraserSize.12", "Tool.EraserSize.24", "Tool.EraserSize.48" }
            })
            {
                Require(group.Count(id => app.Find(id).Patterns.Toggle.Pattern.ToggleState.Value == FlaUI.Core.Definitions.ToggleState.On) == 1,
                    "Tool group does not have exactly one selection: " + string.Join(", ", group));
            }
            app.Stage($"shell-{size.Item1}x{size.Item2}");
        }
    }

    private static JsonArray Board(JsonNode state) => state["board"]!.AsArray();
    private static void Unchanged(JsonNode before, JsonNode after)
    {
        Require(Number(before, "contentRevision") == Number(after, "contentRevision") &&
            JsonNode.DeepEquals(before["board"], after["board"]) &&
            !Flag(after, "pending") && Number(after, "pendingAfter") == 0, "Cancelled edit changed committed IDs/points/style or left a preview.");
    }
    private static void Camera(JsonNode state, double x, double y)
    {
        var camera = state["camera"]!;
        Require(Math.Abs(Value(camera, "x") - x) <= 0.75 && Math.Abs(Value(camera, "y") - y) <= 0.75,
            $"Navigation delta differs from ({x},{y}) DIP.");
        Require(Value(camera, "scale") == 1, "Board zoom changed.");
    }
    private static bool State(JsonNode state, string name) => state["state"]!.GetValue<string>() == name;
    private static void BoardInkErase(NativeDesktopSession app)
    {
        app.Resize((int)Math.Round(1024 * app.Scale), (int)Math.Round(768 * app.Scale));
        app.Select("Tool.Pen");
        app.Select("Tool.Color.Red");
        var revision = Number(app.Snapshot, "contentRevision");
        app.Stroke("BoardSurface", (100, 300), (400, 300));
        var drawn = app.Snapshot;
        Require(Board(drawn).Count >= 1 && Number(drawn, "contentRevision") == revision + 1, "Ink not committed exactly once.");
        app.Pixel("BoardSurface", 150, 300, Color.FromArgb(229, 83, 83));
        app.Select("Tool.Eraser");
        app.Stroke("BoardSurface", (250, 260), (250, 340));
        var erased = app.Snapshot;
        Require(Board(erased).Count == Board(drawn).Count + 1, "Local erase did not split one stroke into two.");
        app.Pixel("BoardSurface", 250, 300, Color.Black);
        app.Pixel("BoardSurface", 150, 300, Color.FromArgb(229, 83, 83));
        app.Stage("board-erased");
        var x = Value(erased["camera"]!, "x");
        var y = Value(erased["camera"]!, "y");
        app.Wheel("BoardSurface", -1, state => Value(state["camera"]!, "y") == y - 48);
        app.Wheel("BoardSurface", 1, state => Value(state["camera"]!, "y") == y);
        Camera(app.Snapshot, x, y);
        Unchanged(erased, app.Snapshot);
    }

    // Exact plan sequence, once per selected tool; synthetic touch verifies selected pen/eraser,
    // not the physical pen driver. Every injected frame has a pre-registered bounded UIA wait.
    private static void TouchCancel(NativeDesktopSession app)
    {
        app.Resize((int)Math.Round(1024 * app.Scale), (int)Math.Round(768 * app.Scale));
        app.Select("Tool.Color.Red");
        foreach (var tool in new[] { "Tool.Pen", "Tool.Eraser" })
        {
            app.Select("Tool.Pen");
            app.Stroke("BoardSurface", (80, 100), (400, 100));
            app.Select(tool);
            var before = app.Snapshot;
            var a = app.Screen("BoardSurface", 100, 100);
            var moved = app.Screen("BoardSurface", 200, 100);
            var b = app.Screen("BoardSurface", 200, 200);
            app.Touch(state => Flag(state, "pending"), (1, a, NativeTouch.Down));
            app.Touch(state => Flag(state, "pending"), (1, moved, NativeTouch.Move));
            if (tool == "Tool.Eraser")
                Require(Number(app.Snapshot, "pendingBefore") > 0, "Eraser cancellation did not first hit an existing stroke.");
            app.Touch(state => !Flag(state, "pending") && State(state, "Navigating"), (2, b, NativeTouch.Down));
            Unchanged(before, app.Snapshot);
            a = app.Screen("BoardSurface", 250, 130);
            b = app.Screen("BoardSurface", 250, 230);
            var x = Value(before["camera"]!, "x");
            var y = Value(before["camera"]!, "y");
            app.Touch(state => Math.Abs(Value(state["camera"]!, "x") - x - 50) < 0.75 &&
                Math.Abs(Value(state["camera"]!, "y") - y - 30) < 0.75,
                (1, a, NativeTouch.Move), (2, b, NativeTouch.Move));
            app.Touch(state => State(state, "AwaitAllReleased"), (2, b, NativeTouch.Up));
            a = app.Screen("BoardSurface", 260, 130);
            app.Touch(state => State(state, "AwaitAllReleased"), (1, a, NativeTouch.Move));
            app.Touch(state => State(state, "Idle"), (1, a, NativeTouch.Up));
            Unchanged(before, app.Snapshot);
            Camera(app.Snapshot, x + 50, y + 30);
            app.Pixel("BoardSurface", 200, 130, Color.FromArgb(229, 83, 83));
            app.Stage(tool == "Tool.Pen" ? "touch-pen-cancelled" : "touch-eraser-cancelled");
        }
        TouchAdversarial(app);
    }

    private static void TouchAdversarial(NativeDesktopSession app)
    {
        app.Select("Tool.Pen");
        var before = app.Snapshot;
        var x = Value(before["camera"]!, "x");
        var y = Value(before["camera"]!, "y");
        var a = app.Screen("BoardSurface", 100, 200);
        var b = app.Screen("BoardSurface", 300, 300);
        var third = app.Screen("BoardSurface", 400, 400);
        app.Touch(state => Flag(state, "pending"), (1, a, NativeTouch.Down));
        app.Touch(state => State(state, "Navigating"), (2, b, NativeTouch.Down));
        app.Touch(state => State(state, "Navigating"), (3, third, NativeTouch.Down));
        third = app.Screen("BoardSurface", 450, 450);
        app.Touch(state => State(state, "Navigating"), (3, third, NativeTouch.Move));
        Camera(app.Snapshot, x, y);
        // Change distance while preserving the original pair's centroid; no pinch zoom.
        a = app.Screen("BoardSurface", 50, 150);
        b = app.Screen("BoardSurface", 350, 350);
        app.Touch(state => State(state, "Navigating"), (1, a, NativeTouch.Move), (2, b, NativeTouch.Move));
        Camera(app.Snapshot, x, y);
        app.Touch(state => State(state, "AwaitAllReleased"), (1, a, NativeTouch.Up));
        app.Touch(state => State(state, "AwaitAllReleased"), (2, b, NativeTouch.Up));
        app.Touch(state => State(state, "Idle"), (3, third, NativeTouch.Up));
        Unchanged(before, app.Snapshot);

        // Chrome-first/content-second is a release barrier, never a fresh edit/navigation.
        var chrome = app.Find("Tool.Width.4").BoundingRectangle;
        var c = new Point(chrome.Left + chrome.Width / 2, chrome.Top + chrome.Height / 2);
        app.Touch(state => !Flag(state, "pending"), (1, c, NativeTouch.Down));
        app.Touch(state => State(state, "AwaitAllReleased"), (2, b, NativeTouch.Down));
        app.Touch(state => State(state, "AwaitAllReleased"), (2, b, NativeTouch.Up));
        app.Touch(state => State(state, "Idle"), (1, c, NativeTouch.Up));
        Unchanged(before, app.Snapshot);
        Camera(app.Snapshot, x, y);

        app.Select("Tool.Eraser");
        app.Move(app.Screen("BoardSurface", 200, 130));
        app.Act(app.Down, state => Flag(state, "pending"));
        var wheelBefore = app.Snapshot;
        app.Wheel("BoardSurface", -1, state => !Flag(state, "pending") && State(state, "AwaitAllReleased"));
        app.Act(app.Up, state => State(state, "Idle"));
        Unchanged(wheelBefore, app.Snapshot);
        app.Select("Tool.Pen");
        var resizeBefore = app.Snapshot;
        app.Move(app.Screen("BoardSurface", 120, 200));
        app.Act(app.Down, state => Flag(state, "pending"));
        app.Resize((int)Math.Round(1280 * app.Scale), (int)Math.Round(900 * app.Scale));
        Require(!Flag(app.Snapshot, "pending"), "Resize did not cancel pending ink.");
        app.Act(app.Up, state => State(state, "Idle"));
        Unchanged(resizeBefore, app.Snapshot);
        app.Stroke("BoardSurface", (100, 460), (300, 460));
        Require(Number(app.Snapshot, "contentRevision") == Number(resizeBefore, "contentRevision") + 1, "Release barrier prevented next valid gesture.");
        var image = OpenImage(app, "small-grid-200x100.png");
        var crossBefore = app.Snapshot;
        a = app.Screen("BoardSurface", 700, 200);
        b = app.Screen($"Document.{image}.Content", 80, 60);
        app.Touch(state => Flag(state, "pending"), (1, a, NativeTouch.Down));
        app.Touch(state => State(state, "AwaitAllReleased") && !Flag(state, "pending"), (2, b, NativeTouch.Down));
        app.Touch(state => State(state, "AwaitAllReleased"), (2, b, NativeTouch.Up));
        app.Touch(state => State(state, "Idle"), (1, a, NativeTouch.Up));
        Unchanged(crossBefore, app.Snapshot);
        Require(JsonNode.DeepEquals(crossBefore["documents"], app.Snapshot["documents"]), "Cross-surface contacts changed image content/layout.");
        app.Act(() => app.Click($"Document.{image}.Close"), state => Documents(state).Count == 0);
    }
}
