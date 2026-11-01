using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CorgiBlackboard.App;
using CorgiBlackboard.App.Documents;
using CorgiBlackboard.App.Input;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;
using CorgiBlackboard.Core.Operations;
using Xunit;

namespace CorgiBlackboard.UiTests;

// Desktop-only fixture. Its labelled content exercises the real host; it does not claim image decoding.
internal sealed class ImageWindowDesktopHost : IDisposable
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private MainWindow _window = null!;
    private Border _firstContent = null!;
    public DocumentOpened First { get; private set; } = null!;
    public DocumentOpened Second { get; private set; } = null!;
    public IntPtr Handle { get; private set; }
    public CacheLease FirstLease { get; } = new();
    public bool EditCancelled { get; private set; }

    public ImageWindowDesktopHost(ImageDesktopRuntime runtime)
    {
        runtime.Dispatcher.Invoke(() =>
        {
            _window = new MainWindow();
            _window.ContentRendered += Ready;
            _window.Show();
        });
        _ready.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    }

    private void Ready(object? sender, EventArgs args)
    {
        _window.ContentRendered -= Ready;
        First = Open("布局测试资料 A（非解码图片）", FirstLease);
        Second = Open("布局测试资料 B（非解码图片）", new CacheLease());
        _window.Workspace.Layout.Move(Second.DocumentId, 520, 0);
        _window.UpdateLayout();
        Handle = new WindowInteropHelper(_window).Handle;
        _ready.SetResult();
    }

    private DocumentOpened Open(string title, CacheLease lease)
    {
        var workspace = _window.Workspace;
        var asset = new Asset(AssetId.New(), "image/png", 0);
        workspace.AddAsset(asset);
        var document = workspace.AddImageDocument(asset.Id, 800, 2400);
        var content = new Border
        {
            Background = Brushes.DarkSlateBlue,
            Child = new TextBlock { Text = title, Foreground = Brushes.White }
        };
        if (ReferenceEquals(lease, FirstLease)) _firstContent = content;
        return _window.Documents.Open(document, title, content, lease);
    }

    public T Read<T>(Func<MainWindow, T> read) => _window.Dispatcher.Invoke(() => read(_window));
    public void Run(Action<MainWindow> action) => _window.Dispatcher.Invoke(() => action(_window));

    public void Observe(DocumentId id, WindowLayoutAction action, Action trigger)
    {
        using var signal = new ManualResetEventSlim();
        void Changed(WindowLayoutChange change)
        {
            if (change.DocumentId == id && change.Action == action) signal.Set();
        }
        Run(window => window.Documents.LayoutChanged += Changed);
        try
        {
            trigger();
            Assert.True(signal.Wait(TimeSpan.FromSeconds(5)), $"No {action} event for {id}.");
            Run(window => window.UpdateLayout());
        }
        finally { Run(window => window.Documents.LayoutChanged -= Changed); }
    }

    public void ObservePointerRelease(Action trigger)
    {
        using var signal = new ManualResetEventSlim();
        System.Windows.Input.MouseButtonEventHandler handler = (_, _) => signal.Set();
        Run(window => window.PreviewMouseUp += handler);
        try
        {
            trigger();
            Assert.True(signal.Wait(TimeSpan.FromSeconds(5)), "No pointer release event.");
            Run(window => window.UpdateLayout());
        }
        finally { Run(window => window.PreviewMouseUp -= handler); }
    }

    public void HoldPendingEdit(Action press)
    {
        using var started = new ManualResetEventSlim();
        Run(window =>
        {
            window.Input.EditingStarted += (_, _) =>
            {
                started.Set();
            };
            window.Input.EditingCancelled += (_, _) =>
            {
                EditCancelled = true;
            };
            _ = new ContentInputBinding(_firstContent, window, window.Editing, First.SurfaceId, () =>
            {
                var origin = _firstContent.TranslatePoint(default, window);
                return new ViewportTransform(1, origin.X, origin.Y);
            });
        });
        press();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "The real content mouse Down did not start an edit.");
        Assert.True(Read(_ => _firstContent.IsMouseCaptured));
    }

    public void Dispose()
    {
        Run(window => window.Close());
        Assert.True(FirstLease.Disposed);
    }

    internal sealed class CacheLease : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose()
        {
            Assert.False(Disposed, "Content resource disposed twice.");
            Disposed = true;
        }
    }
}
