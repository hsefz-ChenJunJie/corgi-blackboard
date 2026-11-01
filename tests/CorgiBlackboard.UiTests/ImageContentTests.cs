using CorgiBlackboard.Infrastructure.Images;
using CorgiBlackboard.TestFixtures;
using Xunit;

namespace CorgiBlackboard.UiTests;

[Collection("Image desktop")]
public sealed class ImageContentTests(ImageDesktopRuntime runtime)
{
    [Fact]
    [Trait("Category", "Desktop")]
    public async Task Invalid_and_cancelled_imports_preserve_workspace_and_report_error()
    {
        var directory = Path.Combine(Path.GetTempPath(), "corgi-image-failure-" + Guid.NewGuid());
        ImageFixtureFactory.Create(directory);
        try
        {
            using var host = new ImageWindowDesktopHost(runtime);
            var before = host.Read(w => (w.Workspace.ContentRevision, w.Workspace.ResourceRevision,
                w.Workspace.Layout.Revision, w.Workspace.Reading.Revision, w.Workspace.Documents.Count,
                w.Workspace.Surfaces.Count, w.ImageOpenActions.Count));
            using var errorVisible = new ManualResetEventSlim();
            host.Run(w => w.ImageLoadFailed += _ => errorVisible.Set());
            await Assert.ThrowsAsync<ImageLoadException>(() =>
                host.Read(w => w.OpenImageAsync(Path.Combine(directory, "invalid.png"))));
            Assert.True(errorVisible.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(string.IsNullOrWhiteSpace(host.Read(w => w.ImageLoadError)));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                host.Read(w => w.OpenImageAsync(Path.Combine(directory, "long-grid-800x2400.png"), cancellation.Token)));
            Assert.Equal(before, host.Read(w => (w.Workspace.ContentRevision, w.Workspace.ResourceRevision,
                w.Workspace.Layout.Revision, w.Workspace.Reading.Revision, w.Workspace.Documents.Count,
                w.Workspace.Surfaces.Count, w.ImageOpenActions.Count)));
        }
        finally { Directory.Delete(directory, true); }
    }
}
