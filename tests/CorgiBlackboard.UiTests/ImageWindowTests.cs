using System.Drawing;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Xunit;

namespace CorgiBlackboard.UiTests;

[Collection("Image desktop")]
public sealed class ImageWindowTests(ImageDesktopRuntime runtime)
{
    [Fact]
    [Trait("Category", "Desktop")]
    public void Real_host_moves_resizes_restores_hands_off_fill_and_releases_resources()
    {
        using var host = new ImageWindowDesktopHost(runtime);
        using var automation = new UIA3Automation();
        var window = automation.FromHandle(host.Handle);
        var first = host.First.DocumentId;
        var second = host.Second.DocumentId;
        var secondBefore = host.Read(w => w.Workspace.Layout.Windows[second]);
        var before = host.Read(w => w.Workspace.Layout.Windows[first].NormalRectangle);

        host.Observe(first, WindowLayoutAction.Moved, () => Drag(Find(window, first, "Title"), 50, 30));
        var moved = host.Read(w => w.Workspace.Layout.Windows[first].NormalRectangle);
        Assert.True(moved.X > before.X && moved.Y > before.Y);
        Assert.Equal(secondBefore, host.Read(w => w.Workspace.Layout.Windows[second]));

        host.Observe(first, WindowLayoutAction.Resized, () => Drag(Find(window, first, "ResizeSE"), -90, -70));
        var resized = host.Read(w => w.Workspace.Layout.Windows[first].NormalRectangle);
        Assert.True(resized.Width < moved.Width && resized.Height < moved.Height);
        host.ObservePointerRelease(() => Drag(Find(window, first, "Content"), 30, 30));
        Assert.Equal(resized, host.Read(w => w.Workspace.Layout.Windows[first].NormalRectangle));

        host.Run(w => w.Workspace.Reading.SetOffset(host.First.SurfaceId, 0, 600));
        host.Observe(first, WindowLayoutAction.FillEntered, () => Toggle(Find(window, first, "Fill"), ToggleState.On));
        host.Run(w => w.Workspace.Reading.SetOffset(host.First.SurfaceId, 0, 1800));
        host.Observe(first, WindowLayoutAction.Minimized, () => Find(window, first, "Minimize").Patterns.Invoke.Pattern.Invoke());
        Assert.Null(host.Read(w => w.Workspace.Layout.FilledWindow));
        Assert.False(host.FirstLease.Disposed);
        host.Observe(second, WindowLayoutAction.FillEntered, () => Toggle(Find(window, second, "Fill"), ToggleState.On));

        // Use the actual toolbar menu and the stable restore ID, rather than calling Restore in the test.
        using (var appeared = new ManualResetEventSlim())
        using (automation.GetDesktop().RegisterStructureChangedEvent(TreeScope.Subtree, (_, _, _) =>
        {
            if (automation.GetDesktop().FindFirstDescendant(cf =>
                cf.ByAutomationId($"Document.{first.Value:D}.Restore")) is not null)
                appeared.Set();
        }))
        {
            var list = window.FindFirstDescendant(cf => cf.ByAutomationId("MinimizedDocuments"));
            Assert.NotNull(list);
            list.Patterns.Invoke.Pattern.Invoke();
            Assert.True(appeared.Wait(TimeSpan.FromSeconds(5)), "Document menu did not appear.");
        }
        host.Run(w => w.UpdateLayout());
        var restore = automation.GetDesktop().FindFirstDescendant(cf =>
            cf.ByAutomationId($"Document.{first.Value:D}.Restore"));
        Assert.NotNull(restore);
        host.Observe(first, WindowLayoutAction.Restored, () => restore.Patterns.Invoke.Pattern.Invoke());
        Assert.Equal(first, host.Read(w => w.Workspace.Layout.FilledWindow));
        Assert.False(host.Read(w => w.Workspace.Layout.Windows[second].IsFilled));
        Assert.Equal((0d, 1800d), host.Read(w => w.Workspace.Reading.Offsets[host.First.SurfaceId]));
        host.Observe(first, WindowLayoutAction.FillExited, () => Toggle(Find(window, first, "Fill"), ToggleState.Off));
        Assert.Equal((0d, 600d), host.Read(w => w.Workspace.Reading.Offsets[host.First.SurfaceId]));
        Assert.Equal(resized, host.Read(w => w.Workspace.Layout.Windows[first].NormalRectangle));

        host.Run(w => w.Workspace.Camera.Pan(1200, -2400));
        Assert.Equal(resized, host.Read(w => w.Workspace.Layout.Windows[first].NormalRectangle));
        var contentBounds = Find(window, first, "Content").BoundingRectangle;
        try
        {
            host.HoldPendingEdit(() =>
            {
                Mouse.MoveTo((int)(contentBounds.Left + 30), (int)(contentBounds.Top + 30));
                Mouse.Down(MouseButton.Left);
            });
            host.Observe(first, WindowLayoutAction.Closed, () => Find(window, first, "Close").Patterns.Invoke.Pattern.Invoke());
        }
        finally { Mouse.Up(MouseButton.Left); }
        Assert.True(host.Read(_ => host.EditCancelled));
        Assert.Equal(InputArbiterState.Idle, host.Read(w => w.Input.State));
        Assert.False(host.Read(w => w.IsMouseCaptureWithin));
        Assert.True(host.FirstLease.Disposed);
        Assert.False(host.Read(w => w.Workspace.Documents.ContainsKey(first)));
        Assert.True(host.Read(w => w.Workspace.Documents.ContainsKey(second)));
        Assert.Equal(0, host.Read(w => w.Workspace.ContentRevision));

        host.Observe(second, WindowLayoutAction.Resized, () => Drag(Find(window, second, "ResizeSE"), -2000, -2000));
        var minimum = host.Read(w => w.Workspace.Layout.Windows[second].NormalRectangle);
        Assert.Equal(240, minimum.Width);
        Assert.Equal(180, minimum.Height);
    }

    private static AutomationElement Find(AutomationElement root, DocumentId id, string suffix) =>
        root.FindFirstDescendant(cf => cf.ByAutomationId($"Document.{id.Value:D}.{suffix}"))
        ?? throw new InvalidOperationException($"Missing Document.{id.Value:D}.{suffix}");

    private static void Toggle(AutomationElement element, ToggleState expected)
    {
        using var signal = new ManualResetEventSlim();
        using var subscription = element.RegisterPropertyChangedEvent(TreeScope.Element,
            (_, _, value) => { if (Convert.ToInt32(value) == (int)expected) signal.Set(); },
            element.Automation.PropertyLibrary.Toggle.ToggleState);
        element.Patterns.Toggle.Pattern.Toggle();
        Assert.True(signal.Wait(TimeSpan.FromSeconds(5)), "No fill toggle event.");
        Assert.Equal(expected, element.Patterns.Toggle.Pattern.ToggleState.Value);
    }

    private static void Drag(AutomationElement element, int dx, int dy)
    {
        var bounds = element.BoundingRectangle;
        var start = new Point((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
        Mouse.MoveTo(start);
        Mouse.Down(MouseButton.Left);
        try { Mouse.MoveTo(start.X + dx, start.Y + dy); }
        finally { Mouse.Up(MouseButton.Left); }
    }
}
