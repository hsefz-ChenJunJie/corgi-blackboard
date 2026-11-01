using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Xunit;

namespace CorgiBlackboard.UiTests;

public sealed class ShellTests
{
    [Fact]
    public void Shell_exposes_stable_touch_targets()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var exe = Environment.GetEnvironmentVariable("CORGI_BLACKBOARD_UI_EXE")
            ?? Path.Combine(root, "src/CorgiBlackboard.App/bin/Release/net10.0-windows/CorgiBlackboard.App.exe");
        var evidence = Path.Combine(root, ".omo/evidence/phase-one/task-6");
        Directory.CreateDirectory(evidence);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
        using var app = Process.Start(start)!;
        Console.WriteLine($"Owned WPF PID: {app.Id}");
        var closed = false;
        try
        {
            Assert.True(app.WaitForInputIdle(10000), "WPF did not reach its first input-idle event.");
            app.Refresh();
            Assert.NotEqual(IntPtr.Zero, app.MainWindowHandle);
            using var automation = new UIA3Automation();
            automation.ConnectionTimeout = TimeSpan.FromSeconds(5);
            automation.TransactionTimeout = TimeSpan.FromSeconds(5);
            var window = automation.FromHandle(app.MainWindowHandle).AsWindow();
            var dpi = GetDpiForWindow(app.MainWindowHandle);
            Assert.True(dpi > 0);
            Console.WriteLine($"Window found: PID {app.Id}, DPI {dpi}");
            var sizes = new[] { (1024, 768), (1920, 1080) };
            foreach (var (width, height) in sizes)
            {
                Assert.True(SetWindowPos(app.MainWindowHandle, new IntPtr(-1), 0, 0, width, height, 0x0040));
                Assert.True(app.WaitForInputIdle(10000));
                var targets = VerifyTargets(window, dpi);
                AssertState(window, "Tool.Pen", ToggleState.On);
                AssertState(window, "Tool.Color.White", ToggleState.On);
                AssertState(window, "Tool.Width.4", ToggleState.On);
                foreach (var (select, previous) in new[]
                {
                    ("Tool.Eraser", "Tool.Pen"), ("Tool.Pen", "Tool.Eraser"),
                    ("Tool.Color.Red", "Tool.Color.White"), ("Tool.Color.Yellow", "Tool.Color.Red"),
                    ("Tool.Color.Green", "Tool.Color.Yellow"), ("Tool.Color.Blue", "Tool.Color.Green"),
                    ("Tool.Color.White", "Tool.Color.Blue"), ("Tool.Width.2", "Tool.Width.4"),
                    ("Tool.Width.8", "Tool.Width.2"), ("Tool.Width.12", "Tool.Width.8"),
                    ("Tool.Width.4", "Tool.Width.12"), ("Tool.EraserSize.12", "Tool.EraserSize.24"),
                    ("Tool.EraserSize.48", "Tool.EraserSize.12"), ("Tool.EraserSize.24", "Tool.EraserSize.48")
                })
                {
                    SelectAndObserve(window, select);
                    AssertState(window, previous, ToggleState.Off);
                }
                DwmFlush();
                var capture = Path.Combine(evidence, $"shell-{width}x{height}-{dpi * 100 / 96}.png");
                window.CaptureToFile(capture);
                GetClientRect(app.MainWindowHandle, out var client);
                File.WriteAllText(Path.ChangeExtension(capture, ".json"), JsonSerializer.Serialize(new
                {
                    capturedUtc = DateTimeOffset.UtcNow, pid = app.Id, dpi, scale = dpi / 96d,
                    requestedOuterPhysicalPixels = new { width, height },
                    actualWindowPhysicalPixels = window.BoundingRectangle,
                    clientPhysicalPixels = client, targets, capture,
                    exeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe))),
                    screenshotSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(capture))),
                    toolSelectionEvents = 14, uia = "FlaUI UIA3", os = Environment.OSVersion.ToString()
                }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
            }
            window.Close();
            closed = app.WaitForExit(10000);
            Assert.True(closed, "WPF did not exit after UIA window Close.");
            Assert.Equal(0, app.ExitCode);
        }
        finally
        {
            if (!app.HasExited)
            {
                app.Kill(entireProcessTree: true);
                app.WaitForExit(10000);
            }
            File.WriteAllText(Path.Combine(evidence, "cleanup.json"), JsonSerializer.Serialize(new
            {
                ownedPid = app.Id, executable = exe, exited = app.HasExited, graceful = closed,
                processExitCode = app.HasExited ? app.ExitCode : (int?)null,
                retained = new[] { "worktree", "captures", "source", "evidence" }
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static object[] VerifyTargets(Window window, uint dpi)
    {
        var ids = new[]
        {
            "BoardSurface", "Tool.Pen", "Tool.Eraser", "Tool.Color.White", "Tool.Color.Red",
            "Tool.Color.Yellow", "Tool.Color.Green", "Tool.Color.Blue", "Tool.Width.2",
            "Tool.Width.4", "Tool.Width.8", "Tool.Width.12", "Tool.EraserSize.12",
            "Tool.EraserSize.24", "Tool.EraserSize.48", "OpenImage", "Diagnostics.Open",
            "MinimizedDocuments"
        };

        return ids.Select(id =>
        {
            var element = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
            Assert.True(element is not null, $"Missing UIA AutomationId: {id}");
            Assert.False(element!.IsOffscreen, $"{id} is offscreen");
            Assert.True(element.BoundingRectangle.Width * 96 / dpi >= 44 && element.BoundingRectangle.Height * 96 / dpi >= 44,
                $"{id} target is smaller than 44 DIP: {element.BoundingRectangle}");
            Assert.False(string.IsNullOrWhiteSpace(element.Name), $"{id} has no UIA name");
            return (object)new { id, element.Name, physicalPixels = element.BoundingRectangle };
        }).ToArray();
    }

    private static void AssertState(Window window, string id, ToggleState state) =>
        Assert.Equal(state, window.FindFirstDescendant(cf => cf.ByAutomationId(id))!.Patterns.Toggle.Pattern.ToggleState.Value);

    private static void SelectAndObserve(Window window, string id)
    {
        var element = window.FindFirstDescendant(cf => cf.ByAutomationId(id))!;
        using var signal = new ManualResetEventSlim();
        using var subscription = element.RegisterPropertyChangedEvent(TreeScope.Element,
            (_, _, value) => { if (Convert.ToInt32(value) == (int)ToggleState.On) signal.Set(); },
            window.Automation.PropertyLibrary.Toggle.ToggleState);
        element.Patterns.Toggle.Pattern.Toggle();
        Assert.True(signal.Wait(TimeSpan.FromSeconds(5)), $"No UIA ToggleState event for {id}");
        AssertState(window, id, ToggleState.On);
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
}
