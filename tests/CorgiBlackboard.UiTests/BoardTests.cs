using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Xunit;

namespace CorgiBlackboard.UiTests;

/// <summary>
/// Post-release desktop acceptance for the board. Real mouse, wheel and touch input drives the released
/// app; every wait is on the board's UIA ItemStatus property-changed event with a bounded timeout.
/// </summary>
public sealed class BoardTests
{
    private static readonly Color Red = Color.FromArgb(0xE5, 0x53, 0x53);

    [Fact]
    public void Mouse_ink_erase_and_wheel_navigation_roundtrip_keep_world_content()
    {
        using var app = BoardApp.Launch("mouse-wheel-roundtrip");
        app.Find("Tool.Color.Red").Click();
        var drawn = app.Act(() => app.MouseStroke((100, 100), (400, 100)), state => state.Revision == 1 && !state.Pending);
        Assert.Equal(1, drawn.Strokes);
        app.AssertPixel(app.World(250, 100), Red);

        app.Find("Tool.Eraser").Click();
        var erased = app.Act(() => app.MouseStroke((250, 60), (250, 140)), state => state.Revision == 2 && !state.Pending);
        Assert.Equal(2, erased.Strokes);
        app.AssertPixel(app.World(250, 100), Color.Black);
        app.AssertPixel(app.World(150, 100), Red);

        Mouse.MoveTo(app.Screen((300, 300)));
        app.Act(() => Mouse.Scroll(-1), state => state.CameraY == -48);
        app.AssertPixel(app.World(150, 100), Red);
        using (Keyboard.Pressing(VirtualKeyShort.SHIFT))
        {
            app.Act(() => Mouse.Scroll(1), state => state.CameraX == 48);
        }

        app.AssertPixel(app.World(150, 100), Red);
        app.AssertPixel(app.World(250, 100), Color.Black);
        app.SaveEvidence("board.png");
        app.Act(() => Mouse.Scroll(1), state => state.CameraY == 0);
        BoardState back;
        using (Keyboard.Pressing(VirtualKeyShort.SHIFT))
        {
            back = app.Act(() => Mouse.Scroll(-1), state => state.CameraX == 0);
        }

        Assert.Equal(erased, back);
        app.AssertPixel(app.World(150, 100), Red);
        app.AssertPixel(app.World(250, 100), Color.Black);
    }

    [Theory]
    [InlineData("Tool.Pen")]
    [InlineData("Tool.Eraser")]
    public void Second_touch_contact_cancels_pending_edit_and_pans_without_phantom_ink(string tool)
    {
        using var app = BoardApp.Launch($"second-touch-{tool}");
        app.Find("Tool.Color.Red").Click();
        var drawn = app.Act(() => app.MouseStroke((100, 200), (500, 200)), state => state.Revision == 1 && !state.Pending);
        app.Find(tool).Click();
        var first = app.Screen((300, 140));
        var dragged = app.Screen((300, 260));
        var second = app.Screen((420, 260));
        app.Act(() => { Inject(Down(0, first)); Inject(Update(0, dragged)); }, state => state.Pending);
        var cancelled = app.Act(() => Inject(Update(0, dragged), Down(1, second)), state => !state.Pending);
        Assert.Equal(drawn, cancelled);

        var shift = new Size((int)Math.Round(20 * app.Scale), (int)Math.Round(10 * app.Scale));
        var a = dragged;
        var b = second;
        for (var step = 0; step < 3; step++)
        {
            a += shift;
            b += shift;
            Inject(Update(0, a), Update(1, b));
        }

        var expectedX = 3 * shift.Width / app.Scale;
        var expectedY = 3 * shift.Height / app.Scale;
        var panned = app.Act(() => { }, state =>
            Math.Abs(state.CameraX - expectedX) <= 0.5 && Math.Abs(state.CameraY - expectedY) <= 0.5);
        Inject(Up(0, a), Update(1, b));
        Inject(Up(1, b));
        Assert.Equal(drawn with { CameraX = panned.CameraX, CameraY = panned.CameraY }, panned);

        app.Find("Tool.Pen").Click();
        var start = app.Screen((100, 420));
        var end = app.Screen((300, 420));
        app.Act(() => { Inject(Down(2, start)); Inject(Update(2, end)); }, state => state.Pending);
        var after = app.Act(() => Inject(Up(2, end)), state => !state.Pending);
        Assert.Equal(panned with { Strokes = 2, Revision = 2 }, after);
        app.AssertPixel(app.World(300, 200), Red);
        app.AssertPixel(app.World(300, 160), Color.Black);
        app.AssertPixel((200, 420), Red);
    }

    [Theory]
    [InlineData("focus")]
    [InlineData("leave")]
    public void Focus_loss_or_leaving_the_board_cancels_the_mouse_edit_until_release(string interrupt)
    {
        using var app = BoardApp.Launch($"cancel-{interrupt}");
        app.Find("Tool.Color.Red").Click();
        var before = app.Act(() => app.MouseStroke((100, 300), (400, 300)), state => state.Revision == 1 && !state.Pending);
        app.Act(() =>
        {
            Mouse.MoveTo(app.Screen((100, 150)));
            Mouse.Down(MouseButton.Left);
            Mouse.MoveTo(app.Screen((200, 160)));
        }, state => state.Pending);
        var cancelled = app.Act(() =>
        {
            if (interrupt == "focus")
            {
                app.Find("Tool.Width.8").Focus();
            }
            else
            {
                Mouse.MoveTo(app.AboveBoard());
            }
        }, state => !state.Pending);
        Assert.Equal(before, cancelled);
        Mouse.MoveTo(app.Screen((300, 170)));
        Mouse.Up(MouseButton.Left);

        app.Act(() =>
        {
            Mouse.MoveTo(app.Screen((100, 450)));
            Mouse.Down(MouseButton.Left);
            Mouse.MoveTo(app.Screen((300, 450)));
        }, state => state.Pending);
        var after = app.Act(() => Mouse.Up(MouseButton.Left), state => !state.Pending);
        Assert.Equal(before with { Strokes = 2, Revision = 2 }, after);
        app.AssertPixel(app.World(200, 160), Color.Black);
        app.AssertPixel(app.World(250, 165), Color.Black);
        app.AssertPixel(app.World(200, 450), Red);
    }

    private const uint PointerTypeTouch = 2;
    private const uint FlagInRange = 0x2;
    private const uint FlagInContact = 0x4;
    private const uint FlagDown = 0x10000;
    private const uint FlagUpdate = 0x20000;
    private const uint FlagUp = 0x40000;
    private const uint TouchMaskAll = 0x7;
    private const uint TouchFeedbackNone = 3;
    private static readonly Lazy<bool> TouchInjectionReady = new(() => InitializeTouchInjection(10, TouchFeedbackNone));

    private static void Inject(params PointerTouchInfo[] contacts)
    {
        Assert.True(TouchInjectionReady.Value, "InitializeTouchInjection failed.");
        Assert.True(InjectTouchInput((uint)contacts.Length, contacts), $"InjectTouchInput failed: {Marshal.GetLastPInvokeError()}");
    }

    private static PointerTouchInfo Down(uint id, Point point) => Contact(id, point, FlagDown | FlagInRange | FlagInContact);

    private static PointerTouchInfo Update(uint id, Point point) => Contact(id, point, FlagUpdate | FlagInRange | FlagInContact);

    private static PointerTouchInfo Up(uint id, Point point) => Contact(id, point, FlagUp);

    private static PointerTouchInfo Contact(uint id, Point point, uint flags) => new()
    {
        PointerInfo = new PointerInfo { PointerType = PointerTypeTouch, PointerId = id, PointerFlags = flags, PixelLocation = point },
        TouchMask = TouchMaskAll,
        ContactArea = new NativeRect(point.X - 2, point.Y - 2, point.X + 2, point.Y + 2),
        Orientation = 90,
        Pressure = 32000
    };

    private sealed record BoardState(int Strokes, long Revision, double CameraX, double CameraY, bool Pending)
    {
        public static BoardState Parse(string? status)
        {
            Assert.False(string.IsNullOrEmpty(status), "The board has no UIA ItemStatus.");
            var fields = status!.Split(';').Select(field => field.Split('=')).ToDictionary(pair => pair[0], pair => pair[1]);
            return new BoardState(
                int.Parse(fields["strokes"], CultureInfo.InvariantCulture),
                long.Parse(fields["contentRevision"], CultureInfo.InvariantCulture),
                double.Parse(fields["cameraX"], CultureInfo.InvariantCulture),
                double.Parse(fields["cameraY"], CultureInfo.InvariantCulture),
                fields["pending"] == "1");
        }
    }

    private sealed class BoardApp : IDisposable
    {
        private readonly Process _process;
        private readonly UIA3Automation _automation;
        private readonly string _name;
        private readonly string _exe;
        private readonly string _evidence;

        private BoardApp(Process process, UIA3Automation automation, string name, string exe, string evidence)
        {
            _process = process;
            _automation = automation;
            _name = name;
            _exe = exe;
            _evidence = evidence;
            Window = automation.FromHandle(process.MainWindowHandle).AsWindow();
            Board = Find("BoardSurface");
            Dpi = GetDpiForWindow(process.MainWindowHandle);
            Assert.True(Dpi > 0);
            Window.SetForeground();
        }

        public Window Window { get; }
        public AutomationElement Board { get; }
        public uint Dpi { get; }
        public double Scale => Dpi / 96d;
        public BoardState State => BoardState.Parse(Board.Properties.ItemStatus.ValueOrDefault);

        public static BoardApp Launch(string name)
        {
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            var exe = Environment.GetEnvironmentVariable("CORGI_BLACKBOARD_UI_EXE")
                ?? Path.Combine(root, "src/CorgiBlackboard.App/bin/Release/net10.0-windows/CorgiBlackboard.App.exe");
            var evidence = Environment.GetEnvironmentVariable("CORGI_BLACKBOARD_UI_EVIDENCE_DIR")
                ?? Path.Combine(Path.GetTempPath(), "corgi-board-evidence-" + Guid.NewGuid());
            Directory.CreateDirectory(evidence);
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            start.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../.."));
            var process = Process.Start(start)!;
            Console.WriteLine($"Owned WPF PID: {process.Id}");
            Assert.True(process.WaitForInputIdle(10000), "WPF did not reach its first input-idle event.");
            process.Refresh();
            Assert.NotEqual(IntPtr.Zero, process.MainWindowHandle);
            var automation = new UIA3Automation
            {
                ConnectionTimeout = TimeSpan.FromSeconds(5),
                TransactionTimeout = TimeSpan.FromSeconds(5)
            };
            return new BoardApp(process, automation, name, exe, evidence);
        }

        public AutomationElement Find(string id)
        {
            var element = Window.FindFirstDescendant(cf => cf.ByAutomationId(id));
            Assert.True(element is not null, $"Missing UIA AutomationId: {id}");
            return element!;
        }

        /// <summary>Runs an input action, then waits for ItemStatus events until the board state matches.</summary>
        public BoardState Act(Action action, Func<BoardState, bool> done)
        {
            using var changed = new AutoResetEvent(false);
            using var subscription = Board.RegisterPropertyChangedEvent(TreeScope.Element,
                (_, _, _) => changed.Set(), _automation.PropertyLibrary.Element.ItemStatus);
            action();
            var clock = Stopwatch.StartNew();
            while (true)
            {
                var state = State;
                if (done(state))
                {
                    return state;
                }

                var remaining = TimeSpan.FromSeconds(10) - clock.Elapsed;
                Assert.True(remaining > TimeSpan.Zero && changed.WaitOne(remaining),
                    $"Board status did not reach the expected state; last: {state}");
            }
        }

        public void MouseStroke((double X, double Y) from, (double X, double Y) to)
        {
            Mouse.MoveTo(Screen(from));
            Mouse.Down(MouseButton.Left);
            Mouse.MoveTo(Screen(((from.X + to.X) / 2, (from.Y + to.Y) / 2)));
            Mouse.MoveTo(Screen(to));
            Mouse.Up(MouseButton.Left);
        }

        /// <summary>Physical screen pixel of a board-local DIP position.</summary>
        public Point Screen((double X, double Y) board)
        {
            var bounds = Board.BoundingRectangle;
            return new Point(
                bounds.Left + (int)Math.Round(board.X * Scale),
                bounds.Top + (int)Math.Round(board.Y * Scale));
        }

        public Point AboveBoard()
        {
            var bounds = Board.BoundingRectangle;
            return new Point(bounds.Left + (bounds.Width / 2), bounds.Top - (int)Math.Round(10 * Scale));
        }

        /// <summary>Board-local DIP position at which a world point is shown under the current camera.</summary>
        public (double X, double Y) World(double x, double y)
        {
            var state = State;
            return (x + state.CameraX, y + state.CameraY);
        }

        public void AssertPixel((double X, double Y) board, Color expected)
        {
            Assert.Equal(0, DwmFlush());
            var point = Screen(board);
            using var capture = Capture.Rectangle(new Rectangle(point.X, point.Y, 1, 1));
            var actual = capture.Bitmap.GetPixel(0, 0);
            Assert.True(Math.Abs(actual.R - expected.R) <= 24 && Math.Abs(actual.G - expected.G) <= 24
                && Math.Abs(actual.B - expected.B) <= 24,
                $"Pixel at board DIP {board} is {actual}, expected {expected}.");
        }

        public void SaveEvidence(string fileName)
        {
            Assert.Equal(0, DwmFlush());
            var capture = Path.Combine(_evidence, fileName);
            Board.CaptureToFile(capture);
            File.WriteAllText(Path.ChangeExtension(capture, ".json"), JsonSerializer.Serialize(new
            {
                capturedUtc = DateTimeOffset.UtcNow, test = _name, pid = _process.Id, dpi = Dpi, scale = Scale,
                status = Board.Properties.ItemStatus.ValueOrDefault, boardPhysicalPixels = Board.BoundingRectangle,
                exeSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_exe))),
                screenshotSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(capture))),
                uia = "FlaUI UIA3", os = Environment.OSVersion.ToString()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        public void Dispose()
        {
            var closed = false;
            try
            {
                Window.Close();
                closed = _process.WaitForExit(10000);
            }
            finally
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(10000);
                }

                File.WriteAllText(Path.Combine(_evidence, $"board-ui-{_name}.json"), JsonSerializer.Serialize(new
                {
                    ownedPid = _process.Id, executable = _exe, exited = _process.HasExited, graceful = closed,
                    processExitCode = _process.HasExited ? _process.ExitCode : (int?)null
                }, new JsonSerializerOptions { WriteIndented = true }));
                _automation.Dispose();
                _process.Dispose();
            }
        }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InitializeTouchInjection(uint maxCount, uint feedbackMode);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InjectTouchInput(uint count, [In] PointerTouchInfo[] contacts);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public uint PointerType;
        public uint PointerId;
        public uint FrameId;
        public uint PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr TargetWindow;
        public Point PixelLocation;
        public Point HimetricLocation;
        public Point PixelLocationRaw;
        public Point HimetricLocationRaw;
        public uint Time;
        public uint HistoryCount;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerTouchInfo
    {
        public PointerInfo PointerInfo;
        public uint TouchFlags;
        public uint TouchMask;
        public NativeRect ContactArea;
        public NativeRect ContactAreaRaw;
        public uint Orientation;
        public uint Pressure;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
}
