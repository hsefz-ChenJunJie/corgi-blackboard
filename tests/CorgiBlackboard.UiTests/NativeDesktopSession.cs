using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace CorgiBlackboard.UiTests;

internal sealed partial class NativeDesktopSession(string exe, string temporary, string evidence, Action<object> trace) : IDisposable
{
    private UIA3Automation? _automation;
    private Process? _process;
    private bool _mouseDown;
    private readonly NativeTouch _touch = new(trace);
    internal Window Window { get; private set; } = null!;
    internal UIA3Automation Automation => _automation!;
    internal string Temporary => temporary;
    internal string PackageDirectory => Path.GetDirectoryName(exe)!;
    internal uint Dpi { get; private set; }
    internal double Scale => Dpi / 96d;
    internal int? OwnedPid => _process?.Id;
    internal bool Exited => _process is null || _process.HasExited;
    internal bool Graceful { get; private set; }
    internal int? ProcessExitCode => _process is { HasExited: true } ? _process.ExitCode : null;
    internal bool ContactsReleased => _touch.Released;
    internal bool MouseReleased => !_mouseDown;
    internal JsonObject Snapshot => QaContract.ParseState(Window.Properties.ItemStatus.Value);

    internal void Start()
    {
        _automation = new UIA3Automation
        {
            ConnectionTimeout = TimeSpan.FromSeconds(5), TransactionTimeout = TimeSpan.FromSeconds(5)
        };
        using var opened = new AutoResetEvent(false);
        using var subscription = Automation.GetDesktop().RegisterAutomationEvent(
            Automation.EventLibrary.Window.WindowOpenedEvent, TreeScope.Subtree, (_, _) => opened.Set());
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        start.Environment["CORGI_DESKTOP_QA"] = "1";
        start.Environment["CORGI_QA_ROOT"] = temporary;
        _process = Process.Start(start) ?? throw new InvalidOperationException("Executable failed to start.");
        trace(new { api = "Process.Start", pid = _process.Id, executable = exe });
        QaContract.Require(_process.WaitForInputIdle(15000), "Executable did not signal input idle.");
        var clock = Stopwatch.StartNew();
        AutomationElement? window = null;
        do
        {
            window = Automation.GetDesktop().FindFirstChild(cf => cf.ByProcessId(_process.Id));
            if (window is not null) break;
            var remaining = TimeSpan.FromSeconds(15) - clock.Elapsed;
            QaContract.Require(remaining > TimeSpan.Zero && opened.WaitOne(remaining), "No owned WindowOpened event.");
        } while (true);
        Window = window.AsWindow();
        Dpi = GetDpiForWindow(Window.Properties.NativeWindowHandle.Value);
        QaContract.Require(Dpi > 0, "GetDpiForWindow failed.");
        Window.SetForeground();
        Observe(Window, () => Window.Properties.ItemStatus.ValueOrDefault?.StartsWith('{') == true,
            () => { }, Automation.PropertyLibrary.Element.ItemStatus);
        _ = Snapshot;
    }

    internal AutomationElement Find(string id, AutomationElement? root = null) =>
        (root ?? Window).FindFirstDescendant(cf => cf.ByAutomationId(id))
        ?? throw new InvalidOperationException("Missing AutomationId=" + id);

    internal JsonObject Act(Action action, Func<JsonObject, bool> complete)
    {
        var revision = QaContract.Number(Snapshot, "revision");
        JsonObject? observed = null;
        Observe(Window, () =>
        {
            observed = Snapshot;
            return QaContract.Number(observed, "revision") > revision && complete(observed);
        }, action, Automation.PropertyLibrary.Element.ItemStatus);
        trace(new { observation = "completed", snapshot = observed });
        return observed!;
    }

    internal void Observe(AutomationElement element, Func<bool> complete, Action action, params FlaUI.Core.Identifiers.PropertyId[] properties)
    {
        using var changed = new AutoResetEvent(false);
        using var subscription = element.RegisterPropertyChangedEvent(TreeScope.Element, (_, _, _) => changed.Set(), properties);
        var clock = Stopwatch.StartNew();
        action();
        while (!complete())
        {
            var remaining = TimeSpan.FromSeconds(15) - clock.Elapsed;
            QaContract.Require(remaining > TimeSpan.Zero && changed.WaitOne(remaining), "Timed out awaiting observed UIA completion.");
        }
    }

    internal void Select(string id)
    {
        var element = Find(id);
        if (element.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On) return;
        Observe(element, () => element.Patterns.Toggle.Pattern.ToggleState.Value == ToggleState.On,
            () => Click(element), Automation.PropertyLibrary.Toggle.ToggleState);
    }

    internal void Structure(Action action, Func<bool> complete)
    {
        using var changed = new AutoResetEvent(false);
        using var subscription = Automation.GetDesktop().RegisterStructureChangedEvent(TreeScope.Subtree, (_, _, _) => changed.Set());
        var clock = Stopwatch.StartNew();
        action();
        while (!complete())
        {
            var remaining = TimeSpan.FromSeconds(15) - clock.Elapsed;
            QaContract.Require(remaining > TimeSpan.Zero && changed.WaitOne(remaining), "Timed out awaiting UIA structure event.");
        }
    }

    internal void SetValue(AutomationElement element, string value)
    {
        Observe(element, () => element.Patterns.Value.Pattern.Value.Value == value,
            () => element.Patterns.Value.Pattern.SetValue(value), Automation.PropertyLibrary.Value.Value);
        trace(new { api = "UIA.Value.SetValue", id = element.AutomationId, value });
    }

    internal void CloseDialog(Window dialog) =>
        Structure(() => dialog.Close(), () => Window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Window)) is null);

    internal void Drag(string id, double dx, double dy, Func<JsonObject, bool> complete)
    {
        var rect = Find(id).BoundingRectangle;
        var from = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        Move(from);
        Act(Down, _ => true);
        Act(() => Move(new Point(from.X + (int)Math.Round(dx * Scale), from.Y + (int)Math.Round(dy * Scale))), complete);
        Act(Up, _ => true);
    }

    internal Point Screen(string id, double xDip, double yDip)
    {
        var rect = Find(id).BoundingRectangle;
        QaContract.Require(xDip >= 0 && yDip >= 0 && xDip * Scale < rect.Width && yDip * Scale < rect.Height,
            $"Point outside UIA content rectangle: {id} ({xDip},{yDip}) DIP, {rect} physical pixels.");
        var point = QaContract.Physical(rect.Left, rect.Top, xDip, yDip, Dpi);
        trace(new { conversion = "content DIP to physical screen", id, rect, dpi = Dpi, xDip, yDip, physicalX = point.X, physicalY = point.Y });
        return new Point(point.X, point.Y);
    }

    internal void Click(string id) => Click(Find(id));
    internal void Click(AutomationElement element)
    {
        var rect = element.BoundingRectangle;
        Move(new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2));
        Down();
        Up();
    }
    internal void Move(Point point)
    {
        Mouse.MoveTo(point);
        trace(new { api = "FlaUI.Mouse.MoveTo", x = point.X, y = point.Y });
    }
    internal void Down()
    {
        _mouseDown = true;
        Mouse.Down(MouseButton.Left);
        trace(new { api = "FlaUI.Mouse.Down", button = "Left" });
    }
    internal void Up()
    {
        Mouse.Up(MouseButton.Left);
        _mouseDown = false;
        trace(new { api = "FlaUI.Mouse.Up", button = "Left" });
    }
    internal void Stroke(string id, (double X, double Y) from, (double X, double Y) to)
    {
        var before = QaContract.Number(Snapshot, "contentRevision");
        Move(Screen(id, from.X, from.Y));
        Act(Down, state => QaContract.Flag(state, "pending"));
        Act(() => Move(Screen(id, to.X, to.Y)), state => QaContract.Flag(state, "pending"));
        Act(Up, state => !QaContract.Flag(state, "pending") && QaContract.Number(state, "contentRevision") == before + 1);
    }
    internal void Touch(Func<JsonObject, bool> complete, params (uint Id, Point Position, uint Action)[] changes)
    {
        var inputRevision = QaContract.Number(Snapshot, "inputRevision");
        Act(() => _touch.Frame(changes), state => complete(state) && changes.All(change =>
        {
            var action = (change.Action & NativeTouch.Up) != 0 ? "Up" : change.Action == NativeTouch.Down ? "Down" : "Move";
            return state["observedInput"]!.AsArray().Any(observed => observed is not null &&
                QaContract.Number(observed, "revision") > inputRevision &&
                observed["action"]!.GetValue<string>() == action &&
                Math.Abs(QaContract.Value(observed, "x") - change.Position.X) <= 2 &&
                Math.Abs(QaContract.Value(observed, "y") - change.Position.Y) <= 2);
        }));
    }
    internal void Wheel(string id, double notches, Func<JsonObject, bool> complete)
    {
        Move(Screen(id, id == "BoardSurface" ? 700 : 100, id == "BoardSurface" ? 450 : 100));
        Act(() => { Mouse.Scroll(notches); trace(new { api = "FlaUI.Mouse.Scroll", notches }); }, complete);
    }

    internal void Resize(int width, int height)
    {
        var before = Window.BoundingRectangle;
        if (before.Width == width && before.Height == height) return;
        Act(() =>
        {
            QaContract.Require(SetWindowPos(Window.Properties.NativeWindowHandle.Value, IntPtr.Zero, before.X, before.Y, width, height, 0x14),
                "SetWindowPos failed.");
            trace(new { api = "SetWindowPos", width, height, units = "physical pixels" });
        }, _ => Window.BoundingRectangle.Width == width && Window.BoundingRectangle.Height == height);
    }

    internal void Cleanup(List<string> errors)
    {
        try { _touch.Release(); } catch (Exception error) { errors.Add("Release contacts: " + error); }
        try { if (_mouseDown) Up(); } catch (Exception error) { errors.Add("Release mouse: " + error); }
        if (_process is null) return;
        try
        {
            // Register before close; WaitForExitAsync subscribes to the actual process exit.
            var exited = _process.WaitForExitAsync();
            if (!_process.HasExited)
            {
                if (Window is not null) Window.Close();
                else _process.CloseMainWindow();
            }
            exited.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            Graceful = _process.ExitCode == 0;
        }
        catch (Exception error) { errors.Add("Graceful process exit: " + error); }
        finally
        {
            if (!_process.HasExited)
            {
                try
                {
                    var exited = _process.WaitForExitAsync();
                    _process.Kill(entireProcessTree: true);
                    exited.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                }
                catch (Exception error) { errors.Add("Owned process termination: " + error); }
            }
        }
    }
    public void Dispose() { _automation?.Dispose(); _process?.Dispose(); }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
