using CorgiBlackboard.Core.Coordinates;
using CorgiBlackboard.Core.Input;
using CorgiBlackboard.Core.Documents;
using CorgiBlackboard.Core.Model;
using Xunit;

namespace CorgiBlackboard.Core.Tests;

public sealed class EditingSessionTests
{
    private const uint Red = 0xFFE55353;
    private const uint Blue = 0xFF5595DC;
    private const double OriginX = 70;
    private const double OriginY = 40;

    [Theory]
    [InlineData(PointerDeviceKind.Mouse)]
    [InlineData(PointerDeviceKind.Stylus)]
    [InlineData(PointerDeviceKind.Touch)]
    public void Ink_stays_pending_until_release_and_commits_world_points_once_for_every_device(PointerDeviceKind device)
    {
        var h = new Harness(Ink(Red, 4)) { Device = device };
        h.Workspace.Camera.Pan(-100, 30);

        h.Board(1, PointerAction.Down, 200, 100);
        h.Promoted(PointerAction.Down, 200, 100);
        h.Board(1, PointerAction.Move, 260, 150);
        h.Promoted(PointerAction.Move, 400, 400);
        h.Board(1, PointerAction.Move, 260, 150);

        var pending = Assert.Single(h.Session.PendingAfter);
        Assert.Equal(h.Workspace.BoardSurfaceId, h.Session.PendingSurfaceId);
        Assert.Empty(h.BoardElements);
        Assert.Equal(0, h.Workspace.ContentRevision);

        h.Board(1, PointerAction.Up, 300, 190);
        h.Promoted(PointerAction.Up, 300, 190);

        var stroke = Assert.Single(h.BoardElements);
        Assert.Equal(pending.Id, stroke.Id);
        Assert.Equal(new[] { new PagePoint(230, 30), new PagePoint(290, 80), new PagePoint(330, 120) }, stroke.Points);
        Assert.Equal(new StrokeStyle(Red, 4), stroke.Style);
        Assert.Equal(1, h.Workspace.ContentRevision);
        Assert.Equal(new[] { h.Workspace.BoardSurfaceId }, h.ContentEvents);
        Assert.Null(h.Session.PendingSurfaceId);
        Assert.Empty(h.Session.PendingAfter);
        Assert.Equal(InputArbiterState.Idle, h.Arbiter.State);
    }

    [Fact]
    public void Settings_changed_mid_gesture_apply_to_new_content_only()
    {
        var h = new Harness(Ink(Red, 2));
        h.Board(1, PointerAction.Down, 100, 100);
        h.Board(1, PointerAction.Move, 150, 100);
        h.Session.Select(Ink(Blue, 8));
        Assert.Equal(InputArbiterState.Editing, h.Arbiter.State);
        h.Board(1, PointerAction.Up, 200, 100);
        var first = Assert.Single(h.BoardElements);

        h.Board(2, PointerAction.Down, 100, 200);
        h.Board(2, PointerAction.Up, 200, 200);

        Assert.Equal(2, h.BoardElements.Count);
        Assert.Same(first, h.BoardElements[0]);
        Assert.Equal(new StrokeStyle(Red, 2), first.Style);
        Assert.Equal(new StrokeStyle(Blue, 8), h.BoardElements[1].Style);
    }

    [Fact]
    public void Page_gestures_convert_dip_sizes_through_locked_scale_and_erase_keeps_lineage()
    {
        var h = new Harness(Ink(Red, 4));
        var page = h.AddPage(200, 100);
        var transform = new ViewportTransform(2, 300, 100);
        h.Send(1, PointerAction.Down, 320, 200, page, transform);
        h.Send(1, PointerAction.Up, 660, 200, page, transform);
        var line = Assert.IsType<StrokeElement>(Assert.Single(h.Workspace.GetElements(page)));
        Assert.Equal(new StrokeStyle(Red, 2), line.Style);
        Assert.Equal(new[] { new PagePoint(10, 50), new PagePoint(180, 50) }, line.Points);

        h.Session.Select(new ToolSettings(InputTool.Eraser, Red, 4, 24));
        h.Send(2, PointerAction.Down, 550, 160, page, transform);
        Assert.Same(line, Assert.Single(h.Workspace.GetElements(page)));
        h.Send(2, PointerAction.Up, 550, 240, page, transform);

        var fragments = h.Workspace.GetElements(page).Cast<StrokeElement>().ToArray();
        Assert.Equal(2, fragments.Length);
        Assert.All(fragments, fragment => Assert.Equal(line.Id, fragment.SourceElementId));
        Assert.All(fragments, fragment => Assert.Equal(line.Style, fragment.Style));
        // Eraser diameter 24 DIP / 2 / scale 2 = radius 6 page px, plus half the 2 px stroke.
        Assert.Equal(118, fragments[0].Points[^1].X, 9);
        Assert.Equal(132, fragments[1].Points[0].X, 9);
        Assert.Empty(h.BoardElements);
    }

    [Theory]
    [InlineData(InputTool.Ink)]
    [InlineData(InputTool.Eraser)]
    public void Second_contact_drops_pending_edit_restores_content_and_pans_without_phantom_ink(InputTool tool)
    {
        var h = new Harness(Ink(Red, 4));
        h.Board(1, PointerAction.Down, 100, 140);
        h.Board(1, PointerAction.Up, 300, 140);
        var line = Assert.Single(h.BoardElements);
        h.Session.Select(new ToolSettings(tool, Blue, 4, 24));

        h.Board(2, PointerAction.Down, 200, 100);
        h.Board(2, PointerAction.Move, 200, 180);
        Assert.NotEmpty(h.Session.PendingAfter);
        Assert.Equal(tool == InputTool.Eraser ? new[] { line } : [], h.Session.PendingBefore);

        h.Board(3, PointerAction.Down, 400, 100);
        Assert.Null(h.Session.PendingSurfaceId);
        Assert.Empty(h.Session.PendingAfter);
        Assert.Empty(h.Session.PendingBefore);
        Assert.Equal(InputArbiterState.Navigating, h.Arbiter.State);

        h.Board(2, PointerAction.Move, 240, 200);
        h.Board(3, PointerAction.Move, 440, 120);
        Assert.Equal(new ViewportTransform(1, 40, 20), h.Workspace.Camera.Transform);
        h.Board(3, PointerAction.Up, 440, 120);
        h.Board(2, PointerAction.Move, 260, 260);
        h.Board(2, PointerAction.Up, 260, 260);

        Assert.Equal(new ViewportTransform(1, 40, 20), h.Workspace.Camera.Transform);
        Assert.Equal(2, h.CameraEvents);
        Assert.Same(line, Assert.Single(h.BoardElements));
        Assert.Equal(1, h.Workspace.ContentRevision);
        Assert.Equal(InputArbiterState.Idle, h.Arbiter.State);

        h.Session.Select(Ink(Blue, 4));
        h.Board(4, PointerAction.Down, 100, 300);
        h.Board(4, PointerAction.Up, 140, 300);
        Assert.Equal(2, h.Workspace.ContentRevision);
        Assert.Equal(new[] { new PagePoint(-10, 240), new PagePoint(30, 240) }, h.BoardElements[1].Points);
    }

    [Fact]
    public void Wheel_pans_48_dip_per_notch_shift_horizontal_and_keeps_fractional_deltas()
    {
        var h = new Harness(Ink(Red, 4));
        h.Session.Wheel(-120, horizontal: false);
        Assert.Equal(new ViewportTransform(1, 0, -48), h.Workspace.Camera.Transform);
        h.Session.Wheel(240, horizontal: true);
        Assert.Equal(new ViewportTransform(1, 96, -48), h.Workspace.Camera.Transform);
        for (var notch = 0; notch < 4; notch++)
        {
            h.Session.Wheel(30, horizontal: false);
        }

        Assert.Equal(new ViewportTransform(1, 96, 0), h.Workspace.Camera.Transform);
        h.Session.Wheel(1, horizontal: false);
        Assert.Equal(0.4, h.Workspace.Camera.Transform.OffsetY, 12);
        h.Session.Wheel(0, horizontal: true);
        Assert.Equal(7, h.CameraEvents);
    }

    [Fact]
    public void Wheel_cancels_a_pending_board_edit_but_not_an_edit_on_a_screen_fixed_page()
    {
        var h = new Harness(Ink(Red, 4));
        h.Board(1, PointerAction.Down, 100, 100);
        h.Board(1, PointerAction.Move, 160, 100);
        h.Session.Wheel(120, horizontal: false);
        Assert.Null(h.Session.PendingSurfaceId);
        Assert.Equal(InputArbiterState.AwaitAllReleased, h.Arbiter.State);
        h.Board(1, PointerAction.Move, 200, 100);
        h.Board(1, PointerAction.Up, 220, 100);
        Assert.Equal(0, h.Workspace.ContentRevision);
        Assert.Equal(new ViewportTransform(1, 0, 48), h.Workspace.Camera.Transform);

        var page = h.AddPage(200, 100);
        var transform = new ViewportTransform(1, 500, 300);
        h.Send(2, PointerAction.Down, 510, 310, page, transform);
        h.Session.Wheel(-120, horizontal: false);
        h.Send(2, PointerAction.Up, 560, 310, page, transform);

        Assert.Equal(1, h.Workspace.ContentRevision);
        Assert.Single(h.Workspace.GetElements(page));
        Assert.Equal(new ViewportTransform(1, 0, 0), h.Workspace.Camera.Transform);
    }

    [Fact]
    public void Camera_roundtrip_preserves_points_ids_and_fixed_document_layers()
    {
        var h = new Harness(Ink(Red, 4));
        h.Board(1, PointerAction.Down, 100, 100);
        h.Board(1, PointerAction.Move, 140, 130);
        h.Board(1, PointerAction.Up, 180, 100);
        var document = h.AddDocument(200, 100);
        var page = document.PageSurfaceIds[0];
        h.Workspace.Layout.SetTeachingArea(1024, 768);
        h.Workspace.Layout.Open(document.Id, new WindowRectangle(600, 80, 240, 180));
        var pageTransform = new ViewportTransform(1, 600, 80);
        h.Send(2, PointerAction.Down, 610, 90, page, pageTransform);
        h.Send(2, PointerAction.Up, 650, 90, page, pageTransform);

        var board = h.BoardElements.ToArray();
        var pageContent = h.Workspace.GetElements(page).ToArray();
        var layout = h.Workspace.Layout.Windows[document.Id];
        var layoutRevision = h.Workspace.Layout.Revision;
        var readingRevision = h.Workspace.Reading.Revision;
        var drawnAt = ScreenPoints(h, board[0]);

        h.Session.Wheel(-360, horizontal: false);
        h.Session.Wheel(240, horizontal: true);
        h.Board(3, PointerAction.Down, 300, 300);
        h.Board(4, PointerAction.Down, 400, 300);
        h.Board(4, PointerAction.Move, 430, 260);
        h.Board(3, PointerAction.Up, 300, 300);
        h.Board(4, PointerAction.Up, 430, 260);
        Assert.Equal(new ViewportTransform(1, 111, -164), h.Workspace.Camera.Transform);
        Assert.NotEqual(drawnAt, ScreenPoints(h, board[0]));

        h.Session.Wheel(360, horizontal: false);
        h.Session.Wheel(-240, horizontal: true);
        h.Board(5, PointerAction.Down, 300, 300);
        h.Board(6, PointerAction.Down, 430, 260);
        h.Board(6, PointerAction.Move, 400, 300);
        h.Board(5, PointerAction.Up, 300, 300);
        h.Board(6, PointerAction.Up, 400, 300);

        Assert.Equal(new ViewportTransform(1, 0, 0), h.Workspace.Camera.Transform);
        Assert.Equal(board.Length, h.BoardElements.Count);
        Assert.All(board.Zip(h.BoardElements), pair => Assert.Same(pair.First, pair.Second));
        Assert.All(pageContent.Zip(h.Workspace.GetElements(page)), pair => Assert.Same(pair.First, pair.Second));
        Assert.Equal(drawnAt, ScreenPoints(h, board[0]));
        Assert.Same(layout, h.Workspace.Layout.Windows[document.Id]);
        Assert.Equal(layoutRevision, h.Workspace.Layout.Revision);
        Assert.Equal(readingRevision, h.Workspace.Reading.Revision);
        Assert.Equal(2, h.Workspace.ContentRevision);
    }

    [Theory]
    [InlineData(InputCancellationReason.LeftSurface)]
    [InlineData(InputCancellationReason.LayoutChanged)]
    public void Leaving_or_moving_the_locked_surface_cancels_until_every_contact_releases(InputCancellationReason expected)
    {
        var h = new Harness(Ink(Red, 4));
        var reasons = new List<InputCancellationReason>();
        h.Arbiter.EditingCancelled += (_, reason) => reasons.Add(reason);
        var board = h.Workspace.BoardSurfaceId;

        h.Board(1, PointerAction.Down, 100, 100);
        h.Session.GuardContact(board, insideSurface: true, h.Transform);
        h.Board(1, PointerAction.Move, 150, 100);
        Assert.Equal(InputArbiterState.Editing, h.Arbiter.State);

        var left = expected == InputCancellationReason.LeftSurface;
        h.Session.GuardContact(board, insideSurface: !left, left ? h.Transform : new ViewportTransform(1, OriginX + 5, OriginY));

        Assert.Equal(new[] { expected }, reasons);
        Assert.Null(h.Session.PendingSurfaceId);
        Assert.Equal(InputArbiterState.AwaitAllReleased, h.Arbiter.State);
        h.Board(1, PointerAction.Move, 120, 100);
        h.Board(1, PointerAction.Up, 120, 100);
        Assert.Equal(0, h.Workspace.ContentRevision);
        Assert.Equal(InputArbiterState.Idle, h.Arbiter.State);

        h.Board(2, PointerAction.Down, 100, 200);
        h.Board(2, PointerAction.Up, 140, 200);
        Assert.Equal(1, h.Workspace.ContentRevision);
    }

    [Fact]
    public void Surface_interrupts_cancel_only_the_locked_gesture_and_a_tool_switch_cancels()
    {
        var h = new Harness(Ink(Red, 4));
        var page = h.AddPage(200, 100);
        var transform = new ViewportTransform(1, 500, 300);
        h.Send(1, PointerAction.Down, 510, 310, page, transform);
        h.Session.InterruptSurface(h.Workspace.BoardSurfaceId, InputCancellationReason.Resize);
        Assert.Equal(page, h.Session.PendingSurfaceId);
        h.Session.InterruptSurface(page, InputCancellationReason.FitChanged);
        Assert.Null(h.Session.PendingSurfaceId);
        h.Send(1, PointerAction.Up, 560, 310, page, transform);
        Assert.Equal(0, h.Workspace.ContentRevision);

        h.Board(2, PointerAction.Down, 100, 100);
        h.Board(2, PointerAction.Move, 140, 100);
        h.Session.Select(new ToolSettings(InputTool.Eraser, Red, 4, 24));
        Assert.Null(h.Session.PendingSurfaceId);
        h.Board(2, PointerAction.Up, 180, 100);

        Assert.Equal(0, h.Workspace.ContentRevision);
        Assert.Empty(h.ContentEvents);
        Assert.Equal(InputArbiterState.Idle, h.Arbiter.State);
    }

    private static ToolSettings Ink(uint argb, double width) => new(InputTool.Ink, argb, width, 24);

    private static ScreenPoint[] ScreenPoints(Harness h, StrokeElement stroke) =>
        stroke.Points.Select(point => h.Transform.ToScreen(new BoardPoint(point.X, point.Y))).ToArray();

    private sealed class Harness
    {
        private long _sequence;

        public Harness(ToolSettings settings)
        {
            Session = new EditingSession(Workspace, Arbiter, settings);
            Session.ContentChanged += surface => ContentEvents.Add(surface);
            Session.CameraChanged += () => CameraEvents++;
        }

        public Workspace Workspace { get; } = new();
        public InputArbiter Arbiter { get; } = new();
        public EditingSession Session { get; }
        public PointerDeviceKind Device { get; init; } = PointerDeviceKind.Touch;
        public List<SurfaceId> ContentEvents { get; } = [];
        public int CameraEvents { get; private set; }
        public ViewportTransform Transform => Session.BoardTransform(OriginX, OriginY);
        public IReadOnlyList<StrokeElement> BoardElements => Workspace.GetElements(Workspace.BoardSurfaceId).Cast<StrokeElement>().ToArray();

        public void Board(long pointer, PointerAction action, double x, double y) =>
            Send(pointer, action, x, y, Workspace.BoardSurfaceId, Transform);

        public void Promoted(PointerAction action, double x, double y) =>
            Arbiter.Process(new PointerSample(999, _sequence++, _sequence, new ScreenPoint(x, y),
                PointerDeviceKind.Mouse, action, InputSurfaceKind.Content, Workspace.BoardSurfaceId, Transform, IsPromotedMouse: true));

        public void Send(long pointer, PointerAction action, double x, double y, SurfaceId surface, ViewportTransform transform) =>
            Arbiter.Process(new PointerSample(pointer, _sequence++, _sequence, new ScreenPoint(x, y),
                Device, action, InputSurfaceKind.Content, surface, transform));

        public SurfaceId AddPage(double width, double height) => AddDocument(width, height).PageSurfaceIds[0];

        public ImageDocument AddDocument(double width, double height)
        {
            var asset = new Asset(AssetId.New(), "image/png", 1);
            Workspace.AddAsset(asset);
            return Workspace.AddImageDocument(asset.Id, width, height);
        }
    }
}
