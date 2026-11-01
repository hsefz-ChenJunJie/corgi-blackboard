using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace CorgiBlackboard.Core.Tests;

public sealed class WindowLayoutStateMachineTests(ITestOutputHelper output)
{
    [Fact]
    public void Exhaustive_four_step_interleavings_preserve_slot_content_and_screen_bounds()
    {
        var transitions = 0;
        // Eight user actions, four positions: all 4096 traces, not a timing-dependent random walk.
        for (var trace = 0; trace < 4096; trace++)
        {
            var (workspace, first, second) = WindowLayoutTests.Create();
            var stroke = new StrokeElement(ElementId.New(), [new PagePoint(12, 22), new PagePoint(33, 44)],
                new StrokeStyle(0xFFFFFFFF, 4), ElementTransform.Identity);
            workspace.BeginStroke(first.PageSurfaceIds[0], first.PageSurfaceIds[0], stroke).Commit();
            var contentRevision = workspace.ContentRevision;
            var code = trace;
            for (var step = 0; step < 4; step++, code /= 8)
            {
                var command = code % 8;
                var id = command % 2 == 0 ? first.Id : second.Id;
                var state = workspace.Layout.Windows[id];
                switch (command / 2)
                {
                    case 0:
                        if (state.IsMinimized) workspace.Layout.Restore(id);
                        else workspace.Layout.Minimize(id);
                        break;
                    case 1:
                        if (state.IsMinimized) workspace.Layout.Restore(id);
                        if (workspace.Layout.Windows[id].IsFilled) workspace.Layout.ExitFill(id);
                        else workspace.Layout.EnterFill(id);
                        break;
                    case 2:
                        workspace.Layout.SetTeachingArea(step % 2 == 0 ? 120 : 1024, step % 2 == 0 ? 80 : 680);
                        break;
                    case 3:
                        if (!state.IsMinimized && !state.IsFilled)
                            workspace.Layout.Move(id, step % 2 == 0 ? -9000 : 9000, 9000);
                        workspace.Camera.Pan(-100000, 300000);
                        break;
                }
                var area = workspace.Layout.TeachingArea!.Value;
                Assert.InRange(workspace.Layout.Windows.Values.Count(x => x.IsFilled && !x.IsMinimized), 0, 1);
                Assert.Equal(contentRevision, workspace.ContentRevision);
                Assert.Same(stroke, Assert.Single(workspace.GetElements(first.PageSurfaceIds[0])));
                foreach (var window in workspace.Layout.Windows.Values)
                {
                    var rectangle = window.NormalRectangle;
                    Assert.InRange(rectangle.X, 0, area.Width - rectangle.Width);
                    Assert.InRange(rectangle.Y, 0, area.Height - rectangle.Height);
                    Assert.InRange(rectangle.Width, Math.Min(240, area.Width), area.Width);
                    Assert.InRange(rectangle.Height, Math.Min(180, area.Height), area.Height);
                    Assert.Equal(window.IsMinimized, workspace.Layout.MinimizedWindows.Contains(window.DocumentId));
                }
                transitions++;
            }
        }
        output.WriteLine($"Observed 4096 traces and {transitions} state transitions; exclusive slot, screen bounds, content identity and revision held.");
        Assert.Equal(16384, transitions);
    }
}
