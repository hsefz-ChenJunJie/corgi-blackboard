using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.App.Documents;

public partial class ImageWindow : UserControl
{
    private readonly ImageWindowHost _host;
    private bool _applyingState;

    internal ImageWindow(ImageWindowHost host, ImageDocument document, string title, FrameworkElement content)
    {
        _host = host;
        Document = document;
        Title = title;
        InitializeComponent();
        TitleText.Text = title;
        TitleGrip.ToolTip = title;
        ContentSlot.Child = content;
        Identify(this, "Window", title);
        Identify(TitleGrip, "Title", title);
        Identify(ContentSlot, "Content", title);
        Identify(MinimizeButton, "Minimize", "最小化 " + title);
        Identify(FillButton, "Fill", "铺满 " + title);
        Identify(CloseButton, "Close", "关闭 " + title);
        TitleGrip.Tag = new WindowChromeTarget(this, null);
        var edges = new[]
        {
            (ResizeN, WindowResizeEdge.Top), (ResizeS, WindowResizeEdge.Bottom),
            (ResizeW, WindowResizeEdge.Left), (ResizeE, WindowResizeEdge.Right),
            (ResizeNW, WindowResizeEdge.TopLeft), (ResizeNE, WindowResizeEdge.TopRight),
            (ResizeSW, WindowResizeEdge.BottomLeft), (ResizeSE, WindowResizeEdge.BottomRight)
        };
        foreach (var (element, edge) in edges)
        {
            element.Tag = new WindowChromeTarget(this, edge);
            Identify(element, element.Name, "调整资料窗口大小");
        }
        MinimizeButton.Click += (_, _) => _host.Minimize(Document.Id);
        CloseButton.Click += (_, _) => _host.Close(Document.Id);
        // UIA TogglePattern calls OnToggle directly; Click alone does not observe it.
        FillButton.Checked += OnFillChanged;
        FillButton.Unchecked += OnFillChanged;
    }

    public ImageDocument Document { get; }
    public string Title { get; }
    internal void DetachContent() => ContentSlot.Child = null;

    internal void Apply(ImageWindowLayout layout, WindowRectangle rectangle)
    {
        _applyingState = true;
        try
        {
            Width = rectangle.Width;
            Height = rectangle.Height;
            Canvas.SetLeft(this, rectangle.X);
            Canvas.SetTop(this, rectangle.Y);
            Panel.SetZIndex(this, layout.IsFilled ? int.MaxValue : layout.ZOrder);
            Visibility = layout.IsMinimized ? Visibility.Collapsed : Visibility.Visible;
            Frame.Margin = new Thickness(layout.IsFilled ? 0 : WindowLayoutState.ResizeBorder);
            ResizeEdges.Visibility = layout.IsFilled ? Visibility.Collapsed : Visibility.Visible;
            TitleGrip.IsEnabled = !layout.IsFilled;
            FillButton.IsChecked = layout.IsFilled;
        }
        finally { _applyingState = false; }
    }

    private void OnFillChanged(object sender, RoutedEventArgs args)
    {
        if (!_applyingState)
            _host.SetFilled(Document.Id, FillButton.IsChecked == true);
    }

    private void Identify(DependencyObject element, string suffix, string name)
    {
        AutomationProperties.SetAutomationId(element, $"Document.{Document.Id.Value:D}.{suffix}");
        AutomationProperties.SetName(element, name);
    }
}

internal sealed record WindowChromeTarget(ImageWindow Window, WindowResizeEdge? Edge);
