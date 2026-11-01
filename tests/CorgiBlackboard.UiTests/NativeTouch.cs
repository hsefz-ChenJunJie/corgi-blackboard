using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace CorgiBlackboard.UiTests;

// One complete frame includes every currently active contact, including stationary ones.
internal sealed class NativeTouch(Action<object> trace)
{
    private readonly Dictionary<uint, Point> _active = [];
    private bool _initialized;
    internal bool Released => _active.Count == 0;
    internal const uint Down = 0x10000, Move = 0x20000, Up = 0x40000, Cancel = 0x8000;

    internal void Frame(params (uint Id, Point Position, uint Action)[] changes)
    {
        if (!_initialized)
        {
            if (!InitializeTouchInjection(10, 3)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "InitializeTouchInjection");
            _initialized = true;
        }
        var changed = changes.ToDictionary(value => value.Id);
        foreach (var change in changes)
        {
            QaContract.Require(change.Action == Down ? !_active.ContainsKey(change.Id) : _active.ContainsKey(change.Id),
                "Invalid native contact lifetime.");
            if ((change.Action & Up) != 0)
                QaContract.Require(_active[change.Id] == change.Position, "Native Up must use previous Update position.");
        }
        var frame = _active.Keys.Union(changed.Keys).Select(id =>
        {
            var change = changed.GetValueOrDefault(id, (id, _active.GetValueOrDefault(id), Move));
            return Contact(change.Item1, change.Item2, change.Item3);
        }).ToArray();
        var ok = InjectTouchInput((uint)frame.Length, frame);
        var error = ok ? 0 : Marshal.GetLastPInvokeError();
        trace(new { api = "InjectTouchInput", success = ok, win32Error = error,
            contacts = frame.Select(contact => new { id = contact.Info.Id, x = contact.Info.Pixel.X, y = contact.Info.Pixel.Y, flags = contact.Info.Flags }).ToArray() });
        if (!ok) throw new Win32Exception(error, "InjectTouchInput; no replay fallback");
        foreach (var change in changes)
            if ((change.Action & Up) != 0) _active.Remove(change.Id);
            else _active[change.Id] = change.Position;
    }

    internal void Release()
    {
        if (_active.Count != 0)
            Frame(_active.Select(pair => (pair.Key, pair.Value, Up | Cancel)).ToArray());
    }

    internal static TouchInfo Contact(uint id, Point point, uint action) => new()
    {
        Info = new PointerInfo { Type = 2, Id = id, Flags = action | ((action & Up) == 0 ? 6u : 0u), Pixel = point },
        Mask = 7, Area = new Rect(point.X - 2, point.Y - 2, point.X + 2, point.Y + 2), Orientation = 90, Pressure = 512
    };

    [DllImport("user32.dll", SetLastError = true)] private static extern bool InitializeTouchInjection(uint maxCount, uint feedback);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool InjectTouchInput(uint count, [In] TouchInfo[] contacts);
    [StructLayout(LayoutKind.Sequential)]
    internal struct PointerInfo
    {
        public uint Type, Id, FrameId, Flags;
        public IntPtr Source, Target;
        public Point Pixel, Himetric, PixelRaw, HimetricRaw;
        public uint Time, History;
        public int InputData;
        public uint KeyStates;
        public ulong PerformanceCount;
        public int ButtonChange;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct TouchInfo
    {
        public PointerInfo Info;
        public uint Flags, Mask;
        public Rect Area, RawArea;
        public uint Orientation, Pressure;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Rect(int Left, int Top, int Right, int Bottom);
}
