using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using CorgiBlackboard.App.Documents;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Infrastructure.Images;
using CorgiBlackboard.TestFixtures;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using Xunit;

namespace CorgiBlackboard.UiTests;

[Collection("Image desktop")]
public sealed class ImageViewportTests(ImageDesktopRuntime runtime)
{
    [Fact]
    [Trait("Category", "Desktop")]
    public async Task Decoded_grid_accepts_real_mouse_ink_and_restores_marker_after_fill()
    {
        var directory = Path.Combine(Path.GetTempPath(), "corgi-image-desktop-" + Guid.NewGuid());
        ImageFixtureFactory.Create(directory);
        try
        {
            using var host = new ImageWindowDesktopHost(runtime);
            var path = Path.Combine(directory, "long-grid-800x2400.png");
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
            var opened = await host.Read(w => w.OpenImageAsync(path));
            Assert.Equal(opened, host.Read(w => w.ImageOpenActions[^1]));
            Assert.Equal(new long[] { 1, 2, 3 }, host.Read(w => w.ImageOpenActions.Select(action => action.OpenOrder).ToArray()));
            Assert.Equal(3, host.Read(w => w.ImageOpenActions.Select(action => action.DocumentId).Distinct().Count()));
            host.Run(w =>
            {
                FindImage(w, opened.SurfaceId).Viewport.Scroll(0, -600);
                w.UpdateLayout();
            });
            using var automation = new UIA3Automation();
            var root = automation.FromHandle(host.Handle);
            var content = root.FindFirstDescendant(cf => cf.ByAutomationId($"Document.{opened.DocumentId.Value:D}.Content"));
            Assert.NotNull(content);
            var start = host.Read(w => FindImage(w, opened.SurfaceId).PointToScreen(new Point(200, 100)));
            var end = host.Read(w => FindImage(w, opened.SurfaceId).PointToScreen(new Point(220, 100)));
            using var committed = new ManualResetEventSlim();
            void OnCommit(PointerSample sample, InputTool tool) => committed.Set();
            host.Run(w => w.Input.EditingCommitted += OnCommit);
            try
            {
                Mouse.MoveTo((int)Math.Round(start.X), (int)Math.Round(start.Y));
                Mouse.Down(MouseButton.Left);
                try { Mouse.MoveTo((int)Math.Round(end.X), (int)Math.Round(end.Y)); }
                finally { Mouse.Up(MouseButton.Left); }
                Assert.True(committed.Wait(TimeSpan.FromSeconds(5)), "No real image pointer commit.");
            }
            finally { host.Run(w => w.Input.EditingCommitted -= OnCommit); }
            var stroke = host.Read(w => Assert.IsType<CorgiBlackboard.Core.Model.StrokeElement>(Assert.Single(w.Workspace.GetElements(opened.SurfaceId))));
            Assert.InRange(stroke.Points[0].Y, 699, 701);
            var fill = root.FindFirstDescendant(cf => cf.ByAutomationId($"Document.{opened.DocumentId.Value:D}.Fill"));
            Assert.NotNull(fill);
            host.Observe(opened.DocumentId, WindowLayoutAction.FillEntered, () => fill.Patterns.Toggle.Pattern.Toggle());
            host.Observe(opened.DocumentId, WindowLayoutAction.FillExited, () => fill.Patterns.Toggle.Pattern.Toggle());
            Assert.Equal((0d, 600d), host.Read(w => w.Workspace.Reading.Offsets[opened.SurfaceId]));
            Assert.Same(stroke, host.Read(w => Assert.Single(w.Workspace.GetElements(opened.SurfaceId))));
            Assert.Equal(new ScreenPoint(200, 100),
                host.Read(w => FindImage(w, opened.SurfaceId).Viewport.Transform().ToScreen(new PagePoint(200, 700))));
            Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
            host.Run(w => w.Documents.Close(opened.DocumentId));
            File.Move(path, path + ".moved");
            File.Delete(path + ".moved");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static ImageContent FindImage(DependencyObject root, CorgiBlackboard.Core.Model.SurfaceId id)
    {
        if (root is ImageContent image && image.SurfaceId == id) return image;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindOrNull(VisualTreeHelper.GetChild(root, i), id);
            if (found is not null) return found;
        }
        throw new InvalidOperationException("Decoded image view missing.");
    }

    private static ImageContent? FindOrNull(DependencyObject root, CorgiBlackboard.Core.Model.SurfaceId id)
    {
        if (root is ImageContent image && image.SurfaceId == id) return image;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindOrNull(VisualTreeHelper.GetChild(root, i), id) is { } found) return found;
        return null;
    }
}
