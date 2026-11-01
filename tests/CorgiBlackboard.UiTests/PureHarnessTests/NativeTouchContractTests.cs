using System.Drawing;
using System.Runtime.InteropServices;
using Xunit;

namespace CorgiBlackboard.UiTests;

public sealed class NativeTouchContractTests
{
    [Theory]
    [InlineData(0x10000u, 0x10006u)]
    [InlineData(0x20000u, 0x20006u)]
    [InlineData(0x40000u, 0x40000u)]
    [InlineData(0x48000u, 0x48000u)]
    public void Native_frames_supply_valid_optional_fields_including_cancel(uint action, uint expectedFlags)
    {
        // Given a physical point on a monitor with a negative desktop origin.
        var point = new Point(-150, 240);

        // When the same factory used by InjectTouchInput creates this frame.
        var contact = NativeTouch.Contact(3, point, action);

        // Then its masked fields satisfy the Win32 ABI; no native API is called.
        Assert.Equal(7u, contact.Mask);
        Assert.InRange(contact.Pressure, 0u, 1024u);
        Assert.InRange(contact.Orientation, 0u, 359u);
        Assert.Equal(point, contact.Info.Pixel);
        Assert.Equal(3u, contact.Info.Id);
        Assert.Equal(2u, contact.Info.Type);
        Assert.Equal(expectedFlags, contact.Info.Flags);
        Assert.Equal(new NativeTouch.Rect(-152, 238, -148, 242), contact.Area);
    }

    [Fact]
    public void X64_touch_frame_matches_native_struct_layout()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(96, Marshal.SizeOf<NativeTouch.PointerInfo>());
        Assert.Equal(144, Marshal.SizeOf<NativeTouch.TouchInfo>());
        Assert.Equal(140, Marshal.OffsetOf<NativeTouch.TouchInfo>(nameof(NativeTouch.TouchInfo.Pressure)).ToInt32());
    }
}
