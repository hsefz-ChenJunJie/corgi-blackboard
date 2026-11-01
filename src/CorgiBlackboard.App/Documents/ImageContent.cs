using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CorgiBlackboard.App.Input;
using CorgiBlackboard.App.Rendering;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Model;

namespace CorgiBlackboard.App.Documents;

public sealed class ImageContent : Grid, IDisposable
{
    private readonly EditingContext _context;
    private readonly ImageDocument _document;
    private readonly ContentInputBinding _binding;
    private readonly Image _image;
    private readonly Canvas _page;
    private readonly CommittedInkLayer _committed = new();
    private readonly TransientInkLayer _transient = new();
    private bool _disposed;

    public ImageContent(EditingContext context, ImageDocument document, BitmapSource pixels, Window root)
    {
        _context = context;
        _document = document;
        Viewport = new ImageViewport(context.Workspace, document.Id);
        Background = Brushes.Transparent;
        ClipToBounds = true;
        Focusable = true;
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        _image = new Image
        {
            Source = pixels, Width = document.PixelWidth, Height = document.PixelHeight,
            Stretch = Stretch.Fill
        };
        _page = new Canvas
        {
            Width = document.PixelWidth, Height = document.PixelHeight,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Clip = new RectangleGeometry(new Rect(0, 0, document.PixelWidth, document.PixelHeight))
        };
        _page.Children.Add(_image);
        _page.Children.Add(_committed);
        _page.Children.Add(_transient);
        Children.Add(_page);
        _binding = new ContentInputBinding(this, root, context, SurfaceId, () =>
        {
            var origin = TranslatePoint(default, root);
            return Viewport.Transform(origin.X, origin.Y);
        });
        context.Session.ContentChanged += ContentChanged;
        context.Session.PendingChanged += PendingChanged;
        context.Session.ReadingChanged += ReadingChanged;
        context.Workspace.Layout.Changed += LayoutChanged;
        Viewport.Changed += SyncViewport;
        MouseWheel += Wheel;
        ContentChanged(SurfaceId);
    }

    public ImageViewport Viewport { get; }
    public SurfaceId SurfaceId => _document.PageSurfaceIds[0];

    private void ContentChanged(SurfaceId surface)
    {
        if (surface == SurfaceId)
            _committed.Sync(_context.Workspace.GetElements(SurfaceId));
    }

    private void PendingChanged()
    {
        var session = _context.Session;
        var here = session.PendingSurfaceId == SurfaceId;
        _committed.Hide(here ? session.PendingBefore : []);
        _transient.Show(here ? session.PendingAfter : []);
    }

    private void ReadingChanged(SurfaceId surface)
    {
        if (surface == SurfaceId) SyncViewport();
    }

    private void LayoutChanged(WindowLayoutChange change)
    {
        if (change.DocumentId == _document.Id && change.Action != WindowLayoutAction.Closed)
            SyncViewport();
    }

    private void SyncViewport()
    {
        var transform = Viewport.Transform();
        _page.RenderTransform = new MatrixTransform(transform.Scale, 0, 0, transform.Scale,
            transform.OffsetX, transform.OffsetY);
    }

    private void Wheel(object sender, MouseWheelEventArgs args)
    {
        _context.Session.InterruptSurface(SurfaceId, InputCancellationReason.Wheel);
        Viewport.Wheel(args.Delta, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        args.Handled = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _binding.Dispose();
        _context.Session.ContentChanged -= ContentChanged;
        _context.Session.PendingChanged -= PendingChanged;
        _context.Session.ReadingChanged -= ReadingChanged;
        _context.Workspace.Layout.Changed -= LayoutChanged;
        Viewport.Changed -= SyncViewport;
        MouseWheel -= Wheel;
        _image.Source = null;
    }
}
