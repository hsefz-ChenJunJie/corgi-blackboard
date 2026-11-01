using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.App.Documents;

public sealed record DocumentOpened(DocumentId DocumentId, AssetId AssetId, SurfaceId SurfaceId, long OpenOrder);

// Owns WPF references only. The same workspace supplies board content, document identity and layout.
public sealed class ImageWindowHost : IDisposable
{
    private readonly Canvas _layer;
    private readonly Window _root;
    private readonly InputArbiter _input;
    private readonly Dictionary<DocumentId, (ImageWindow Window, IDisposable? Resource)> _windows = [];
    private readonly DocumentChromeInput _chrome;
    private long _openOrder;
    private bool _disposed;

    public ImageWindowHost(Workspace workspace, InputArbiter input, Canvas layer, Window root)
    {
        Workspace = workspace;
        _input = input;
        _layer = layer;
        _root = root;
        _chrome = new DocumentChromeInput(this, root, input);
        _layer.SizeChanged += OnSizeChanged;
        root.DpiChanged += OnDpiChanged;
        root.Deactivated += OnDeactivated;
        Workspace.Layout.Changed += OnLayoutChanged;
    }

    public Workspace Workspace { get; }
    public event Action<DocumentOpened>? Opened;
    public event Action<WindowLayoutChange>? LayoutChanged;

    // The caller supplies already-created content and transfers its optional cache/resource lifetime.
    // Image decoding and page rendering are supplied by the image-content composition.
    public DocumentOpened Open(ImageDocument document, string title, FrameworkElement content, IDisposable? resource = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Workspace.Documents.TryGetValue(document.Id, out var owned) || !ReferenceEquals(owned, document))
            throw new ArgumentException("The document must belong to the teaching workspace.", nameof(document));
        if (_windows.ContainsKey(document.Id) || content.Parent is not null)
            throw new InvalidOperationException("The document or its content already has a host.");
        UpdateArea();
        var window = new ImageWindow(this, document, title, content);
        _windows.Add(document.Id, (window, resource));
        _layer.Children.Add(window);
        var offset = 24 + (_openOrder % 8) * 24;
        Workspace.Layout.Open(document.Id, new WindowRectangle(offset, offset, 480, 360));
        var opened = new DocumentOpened(document.Id, document.AssetId, document.PageSurfaceIds[0], ++_openOrder);
        Opened?.Invoke(opened);
        return opened;
    }

    public void Minimize(DocumentId id)
    {
        Interrupt();
        Workspace.Layout.Minimize(id);
    }

    public void Restore(DocumentId id)
    {
        Interrupt();
        Workspace.Layout.Restore(id);
    }

    public void SetFilled(DocumentId id, bool filled)
    {
        if (Workspace.Layout.Windows[id].IsFilled == filled)
            return;
        Interrupt();
        if (filled) Workspace.Layout.EnterFill(id);
        else Workspace.Layout.ExitFill(id);
    }

    public void Close(DocumentId id)
    {
        Interrupt();
        var entry = _windows[id];
        _windows.Remove(id);
        _layer.Children.Remove(entry.Window);
        entry.Window.DetachContent();
        entry.Resource?.Dispose();
        Workspace.CloseDocument(id);
    }

    public ContextMenu CreateWindowMenu()
    {
        var menu = new ContextMenu { MaxHeight = 480 };
        foreach (var (id, entry) in _windows)
        {
            var minimized = Workspace.Layout.Windows[id].IsMinimized;
            var item = new MenuItem { Header = "管理 " + entry.Window.Title };
            var restore = new MenuItem { Header = (minimized ? "恢复 " : "显示 ") + entry.Window.Title };
            AutomationProperties.SetAutomationId(restore, $"Document.{id.Value:D}.Restore");
            restore.Click += (_, _) =>
            {
                if (Workspace.Layout.Windows[id].IsMinimized) Restore(id);
                else
                {
                    Interrupt();
                    if (Workspace.Layout.FilledWindow is { } owner && owner != id)
                        Workspace.Layout.ExitFill(owner);
                    Workspace.Layout.Activate(id);
                }
            };
            var fill = new MenuItem { Header = Workspace.Layout.Windows[id].IsFilled ? "退出铺满" : "铺满" };
            fill.Click += (_, _) =>
            {
                if (Workspace.Layout.Windows[id].IsMinimized) Restore(id);
                SetFilled(id, !Workspace.Layout.Windows[id].IsFilled);
            };
            var close = new MenuItem { Header = "关闭" };
            close.Click += (_, _) => Close(id);
            item.Items.Add(fill);
            item.Items.Add(close);
            menu.Items.Add(restore);
            menu.Items.Add(item);
        }
        if (_windows.Count == 0)
            menu.Items.Add(new MenuItem { Header = "没有打开的资料", IsEnabled = false });
        return menu;
    }

    internal void Drag(WindowChromeTarget target, Vector delta)
    {
        _input.Interrupt(InputCancellationReason.LayoutChanged);
        if (target.Edge is { } edge)
            Workspace.Layout.Resize(target.Window.Document.Id, edge, delta.X, delta.Y);
        else
            Workspace.Layout.Move(target.Window.Document.Id, delta.X, delta.Y);
    }

    internal void Activate(DocumentId id) => Workspace.Layout.Activate(id);

    private void Interrupt(InputCancellationReason reason = InputCancellationReason.LayoutChanged)
    {
        _input.Interrupt(reason);
        _chrome.Release();
        foreach (var entry in _windows.Values)
        {
            if (entry.Window.IsMouseCaptureWithin) Mouse.Capture(null);
            if (entry.Window.IsStylusCaptureWithin) Stylus.Capture(null);
            foreach (var touch in entry.Window.TouchesCapturedWithin.ToArray())
                touch.Capture(null);
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        Interrupt();
        if (args.NewSize.Width > 0 && args.NewSize.Height > 0)
            UpdateArea();
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs args) => Interrupt(InputCancellationReason.DpiChanged);
    private void OnDeactivated(object? sender, EventArgs args) => Interrupt(InputCancellationReason.FocusLost);

    private void UpdateArea()
    {
        if (_layer.ActualWidth <= 0 || _layer.ActualHeight <= 0)
            throw new InvalidOperationException("The teaching area must be arranged before opening a document.");
        Workspace.Layout.SetTeachingArea(_layer.ActualWidth, _layer.ActualHeight);
    }

    private void OnLayoutChanged(WindowLayoutChange change)
    {
        foreach (var (id, entry) in _windows)
            entry.Window.Apply(Workspace.Layout.Windows[id], Workspace.Layout.DisplayRectangle(id));
        LayoutChanged?.Invoke(change);
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var id in _windows.Keys.ToArray())
            Close(id);
        _chrome.Dispose();
        _layer.SizeChanged -= OnSizeChanged;
        _root.DpiChanged -= OnDpiChanged;
        _root.Deactivated -= OnDeactivated;
        Workspace.Layout.Changed -= OnLayoutChanged;
        _disposed = true;
    }
}
