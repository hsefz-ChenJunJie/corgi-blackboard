using System.Windows.Threading;
using Xunit;

namespace CorgiBlackboard.UiTests;

[CollectionDefinition("Image desktop", DisableParallelization = true)]
public sealed class ImageDesktopCollection : ICollectionFixture<ImageDesktopRuntime>;

// WPF permits only one Application per process. The collection owns one dispatcher;
// each test still owns and closes its own windows.
public sealed class ImageDesktopRuntime : IDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    public Dispatcher Dispatcher { get; }

    public ImageDesktopRuntime()
    {
        _thread = new Thread(() =>
        {
            try
            {
                var app = new CorgiBlackboard.App.App();
                app.InitializeComponent();
                app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
                _ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
                _exited.SetResult();
            }
            catch (Exception error)
            {
                _ready.TrySetException(error);
                _exited.TrySetException(error);
            }
        }) { IsBackground = true, Name = "Image desktop acceptance" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Dispatcher = _ready.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
        _exited.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "Owned image dispatcher did not exit.");
    }
}
