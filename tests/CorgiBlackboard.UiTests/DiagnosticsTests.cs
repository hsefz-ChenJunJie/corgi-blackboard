using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using CorgiBlackboard.App.Diagnostics;
using CorgiBlackboard.Core.Diagnostics;
using CorgiBlackboard.Infrastructure.Diagnostics;
using Xunit;

namespace CorgiBlackboard.UiTests;

// Executable WPF interaction scenarios. Compile here; run on the post-release desktop only.
public sealed class DiagnosticsTests
{
    [Fact]
    public Task ExportButtonPublishesEnteredFeedbackAndCurrentSession() => OnDesktop(async fixture =>
    {
        fixture.Session.TryRecord(new DiagnosticEvent("classroom-action"));
        fixture.Text("ReproductionSteps").Text = "输入场景 A";
        fixture.Text("Expected").Text = "预期 B";
        fixture.Text("Actual").Text = "实际 C";
        var destination = Path.Combine(fixture.Root, "feedback.zip");
        fixture.Text("OutputPath").Text = destination;
        fixture.Sessions.SelectedIndex = 0;
        var complete = ObserveTag(fixture.Message, "complete");
        fixture.Click("Diagnostics.Export");
        await complete;
        using var archive = ZipFile.OpenRead(destination);
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        var manifest = await JsonNode.ParseAsync(manifestStream);
        Assert.Equal(fixture.Session.Id, manifest!["sessions"]![0]!["sessionId"]!.GetValue<Guid>());
        Assert.Equal("输入场景 A", manifest["reproduction"]!["steps"]!.GetValue<string>());
        Assert.Equal("预期 B", manifest["reproduction"]!["expected"]!.GetValue<string>());
        Assert.Equal("实际 C", manifest["reproduction"]!["actual"]!.GetValue<string>());
        Assert.False(manifest["incomplete"]!.GetValue<bool>());
        Assert.True(fixture.Window.IsVisible);
    });

    [Fact]
    public Task OpenWindowShowsWriterFaultAndIncompleteExport() => OnDesktop(async fixture =>
    {
        var degraded = ObserveTag((TextBlock)fixture.Window.FindName("StatusText"), "degraded");
        fixture.FailNextWrite = true;
        Assert.True(fixture.Session.TryRecord(new DiagnosticEvent("refused")));
        await degraded;
        fixture.Sessions.SelectedIndex = 0;
        var destination = Path.Combine(fixture.Root, "incomplete.zip");
        fixture.Text("OutputPath").Text = destination;
        var incomplete = ObserveTag(fixture.Message, "incomplete");
        fixture.Click("Diagnostics.Export");
        await incomplete;
        using var archive = ZipFile.OpenRead(destination);
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        var manifest = await JsonNode.ParseAsync(manifestStream);
        Assert.True(manifest!["incomplete"]!.GetValue<bool>());
        Assert.NotEmpty(manifest["sessions"]![0]!["gaps"]!.AsArray());
    });

    [Fact]
    public Task RefusedDestinationShowsFailureAndKeepsOriginalLogs() => OnDesktop(async fixture =>
    {
        var destination = Path.Combine(fixture.Root, "held.zip");
        File.WriteAllBytes(destination, [9, 8, 7]);
        using var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
        fixture.Text("OutputPath").Text = destination;
        fixture.Sessions.SelectedIndex = 0;
        var failed = ObserveTag(fixture.Message, "failed");
        fixture.Click("Diagnostics.Export");
        await failed;
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(destination));
        Assert.True(Directory.Exists(fixture.Session.DirectoryPath));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
    });

    [Fact]
    public Task CurrentDeletionAndMalformedRetentionAreRejectedByControls() => OnDesktop(async fixture =>
    {
        fixture.Sessions.SelectedIndex = 0;
        fixture.Click("Diagnostics.Delete");
        Assert.Equal("protected", fixture.Message.Tag);
        Assert.True(Directory.Exists(fixture.Session.DirectoryPath));
        fixture.Text("RetentionMegabytes").Text = "NaN";
        fixture.Click("Diagnostics.ApplyRetention");
        Assert.Equal("invalid-retention", fixture.Message.Tag);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.Session.DirectoryPath)!, "retention.json")));
        fixture.Sessions.SelectedIndex = 0;
        fixture.Click("Diagnostics.Pin");
        Assert.True(fixture.Session.IsPinned);
        await fixture.Session.FlushAsync();
        await DiagnosticSessionShutdown.ShutdownAsync(fixture.Session);
        var metadata = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Session.DirectoryPath, "session.json")))!;
        Assert.True(metadata["pinned"]!.GetValue<bool>());
    });

    private static async Task ObserveTag(FrameworkElement element, string expected)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptor = DependencyPropertyDescriptor.FromProperty(FrameworkElement.TagProperty, element.GetType());
        EventHandler handler = (_, _) => { if (Equals(element.Tag, expected)) signal.TrySetResult(); };
        descriptor.AddValueChanged(element, handler);
        try { await signal.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { descriptor.RemoveValueChanged(element, handler); }
    }

    private static Task OnDesktop(Func<DesktopFixture, Task> test)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await using (var fixture = new DesktopFixture())
                    {
                        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        fixture.Window.Loaded += (_, _) => loaded.TrySetResult();
                        fixture.Window.Show();
                        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        await test(fixture);
                    }
                    finished.TrySetResult();
                }
                catch (Exception ex) { finished.TrySetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return finished.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private sealed class DesktopFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cbb-desktop-diagnostics-" + Guid.NewGuid().ToString("N"));
        public DiagnosticSession Session { get; }
        public DiagnosticsWindow Window { get; }
        private int _failNextWrite;
        public bool FailNextWrite { set => Interlocked.Exchange(ref _failNextWrite, value ? 1 : 0); }
        public DesktopFixture()
        {
            Session = new DiagnosticSession(new DiagnosticOptions
            {
                RootDirectory = Path.Combine(Root, "logs"),
                BeforeWriteAsync = _ =>
                {
                    if (Interlocked.Exchange(ref _failNextWrite, 0) == 0) return ValueTask.CompletedTask;
                    return ValueTask.FromException(new IOException("synthetic desktop sink refusal"));
                }
            });
            Window = new DiagnosticsWindow(Session);
        }
        public TextBox Text(string name) => (TextBox)Window.FindName(name);
        public TextBlock Message => (TextBlock)Window.FindName("MessageText");
        public ListBox Sessions => (ListBox)Window.FindName("SessionsList");
        public void Click(string automationId) => FindButton(Window, automationId)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        private static Button? FindButton(DependencyObject parent, string automationId)
        {
            if (parent is Button button && AutomationProperties.GetAutomationId(button) == automationId) return button;
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
                if (FindButton(child, automationId) is { } found) return found;
            return null;
        }
        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await DiagnosticSessionShutdown.ShutdownAsync(Session).WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(Root, true);
        }
    }
}
