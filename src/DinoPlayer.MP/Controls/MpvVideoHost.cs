using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using DinoPlayer.MP.Playback;

namespace DinoPlayer.MP.Controls;

public sealed class MpvVideoHost : NativeControlHost
{
    private const uint WmSetCursor = 0x0020;
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmMouseLeave = 0x02A3;
    private const uint TmeLeave = 0x00000002;
    private const int GwlpWndProc = -4;
    private const int SmCxDoubleClk = 36;
    private const int SmCyDoubleClk = 37;
    private const int WHEEL_DELTA = 120;
    private const int IdcArrow = 32512;

    private delegate IntPtr WindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public IntPtr TrackedWindow;
        public uint HoverTime;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr CallWindowProcW(IntPtr previousWindowProc, IntPtr windowHandle,
        uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr DefWindowProcW(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetClientRect(IntPtr windowHandle, out Rect clientRect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData trackMouseEvent);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool ScreenToClient(IntPtr windowHandle, ref Point point);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private IntPtr _nativeHandle;
    private IntPtr _previousWindowProc;
    private WindowProc? _windowProc;
    private bool _trackingMouse;
    private bool _nativeCursorVisible = true;
    private long _lastLeftButtonDownMilliseconds;
    private Point _lastLeftButtonDownPoint;
    private bool _hasPendingLeftButtonDown;

    public event Action<IntPtr, string>? HandleCreated;
    public event Action? HandleDestroying;
    public event Action<PlaybackPointerEvent>? PointerInput;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var child = base.CreateNativeControlCore(parent);
        var descriptor = child.HandleDescriptor ?? "unknown";
        AppLog.Info($"Avalonia native video child created: {descriptor} {child.Handle}");

        if (OperatingSystem.IsWindows() && string.Equals(descriptor, "HWND", StringComparison.OrdinalIgnoreCase))
            InstallWindowsInputBridge(child.Handle);

        HandleCreated?.Invoke(child.Handle, descriptor);
        return child;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        RemoveWindowsInputBridge();
        HandleDestroying?.Invoke();
        AppLog.Info($"Avalonia native video child destroying: {control.HandleDescriptor} {control.Handle}");
        base.DestroyNativeControlCore(control);
    }

    public void SetNativeCursorVisible(bool visible)
    {
        _nativeCursorVisible = visible;
        if (!OperatingSystem.IsWindows() || _nativeHandle == IntPtr.Zero)
            return;

        SetCursor(visible ? LoadCursorW(IntPtr.Zero, new IntPtr(IdcArrow)) : IntPtr.Zero);
    }

    private void InstallWindowsInputBridge(IntPtr nativeHandle)
    {
        RemoveWindowsInputBridge();
        _nativeHandle = nativeHandle;
        _windowProc = WindowsWindowProc;
        _previousWindowProc = SetWindowLongPtrW(nativeHandle, GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProc));
        if (_previousWindowProc == IntPtr.Zero)
        {
            AppLog.Error($"Unable to subclass native video window {nativeHandle}: Win32 error {Marshal.GetLastWin32Error()}.");
            _windowProc = null;
            _nativeHandle = IntPtr.Zero;
            return;
        }

        AppLog.Info($"Installed Windows native video input bridge on HWND {nativeHandle}.");
    }

    private void RemoveWindowsInputBridge()
    {
        if (_nativeHandle != IntPtr.Zero && _previousWindowProc != IntPtr.Zero)
        {
            SetWindowLongPtrW(_nativeHandle, GwlpWndProc, _previousWindowProc);
            AppLog.Info($"Removed Windows native video input bridge from HWND {_nativeHandle}.");
        }

        _nativeHandle = IntPtr.Zero;
        _previousWindowProc = IntPtr.Zero;
        _windowProc = null;
        _trackingMouse = false;
        _hasPendingLeftButtonDown = false;
    }

    private IntPtr WindowsWindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case WmSetCursor when !_nativeCursorVisible:
                    SetCursor(IntPtr.Zero);
                    return new IntPtr(1);

                case WmMouseMove:
                    if (!_trackingMouse)
                    {
                        _trackingMouse = true;
                        var tracking = new TrackMouseEventData
                        {
                            Size = (uint)Marshal.SizeOf<TrackMouseEventData>(),
                            Flags = TmeLeave,
                            TrackedWindow = windowHandle
                        };
                        TrackMouseEvent(ref tracking);
                        RaisePointerInput(CreatePointerEvent(PlaybackPointerEventKind.Enter, lParam));
                    }

                    RaisePointerInput(CreatePointerEvent(PlaybackPointerEventKind.Move, lParam));
                    break;

                case WmMouseLeave:
                    _trackingMouse = false;
                    RaisePointerInput(CreatePointerEvent(PlaybackPointerEventKind.Leave, IntPtr.Zero));
                    break;

                case WmLButtonDown:
                    if (IsDoubleClick(lParam))
                    {
                        RaisePointerInput(CreatePointerEvent(PlaybackPointerEventKind.DoubleClick, lParam));
                        _hasPendingLeftButtonDown = false;
                    }
                    else
                    {
                        _hasPendingLeftButtonDown = true;
                        _lastLeftButtonDownMilliseconds = Environment.TickCount64;
                        _lastLeftButtonDownPoint = PointFromLParam(lParam);
                    }
                    break;

                case WmLButtonDblClk:
                    RaisePointerInput(CreatePointerEvent(PlaybackPointerEventKind.DoubleClick, lParam));
                    _hasPendingLeftButtonDown = false;
                    break;

                case WmRButtonDown:
                    RaisePointerInput(CreatePointerEvent(PlaybackPointerEventKind.RightClick, lParam));
                    break;

                case WmMouseWheel:
                    var wheelPoint = PointFromLParam(lParam);
                    ScreenToClient(windowHandle, ref wheelPoint);
                    var wheelEvent = CreatePointerEvent(PlaybackPointerEventKind.Wheel, wheelPoint,
                        (short)((long)wParam >> 16) / (double)WHEEL_DELTA);
                    RaisePointerInput(wheelEvent);
                    break;
            }
        }
        catch (Exception exception)
        {
            AppLog.Error($"Windows native video input bridge failed: {exception}");
        }

        return _previousWindowProc != IntPtr.Zero
            ? CallWindowProcW(_previousWindowProc, windowHandle, message, wParam, lParam)
            : DefWindowProcW(windowHandle, message, wParam, lParam);
    }

    private bool IsDoubleClick(IntPtr lParam)
    {
        var point = PointFromLParam(lParam);
        var now = Environment.TickCount64;
        var elapsed = now - _lastLeftButtonDownMilliseconds;
        var width = Math.Max(1, GetSystemMetrics(SmCxDoubleClk));
        var height = Math.Max(1, GetSystemMetrics(SmCyDoubleClk));
        var isDoubleClick = _hasPendingLeftButtonDown &&
                            elapsed >= 0 && elapsed <= Math.Max(1, GetDoubleClickTime()) &&
                            Math.Abs(point.X - _lastLeftButtonDownPoint.X) <= width &&
                            Math.Abs(point.Y - _lastLeftButtonDownPoint.Y) <= height;
        return isDoubleClick;
    }

    private PlaybackPointerEvent CreatePointerEvent(PlaybackPointerEventKind kind, IntPtr lParam, double wheelDelta = 0)
    {
        return CreatePointerEvent(kind, PointFromLParam(lParam), wheelDelta);
    }

    private PlaybackPointerEvent CreatePointerEvent(PlaybackPointerEventKind kind, Point point, double wheelDelta = 0)
    {
        var height = 0;
        if (_nativeHandle != IntPtr.Zero && GetClientRect(_nativeHandle, out var clientRect))
            height = Math.Max(0, clientRect.Bottom - clientRect.Top);

        return new PlaybackPointerEvent(kind, point.X, point.Y, height, wheelDelta);
    }

    private static Point PointFromLParam(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return new Point
        {
            X = (short)(value & 0xffff),
            Y = (short)((value >> 16) & 0xffff)
        };
    }

    private void RaisePointerInput(PlaybackPointerEvent pointerEvent)
    {
        PointerInput?.Invoke(pointerEvent);
    }
}
