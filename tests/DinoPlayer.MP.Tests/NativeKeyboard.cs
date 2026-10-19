using System.Runtime.InteropServices;
using Avalonia.Controls;

// Exercise X11 keyboard delivery as well as Avalonia's managed event routing.
internal static class NativeKeyboard
{
    public static bool HasFocus(Window window)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) return false;
        try
        {
            XGetInputFocus(display, out var focusedWindow, out _);
            return focusedWindow == window.TryGetPlatformHandle()!.Handle;
        }
        finally { XCloseDisplay(display); }
    }

    public static void Press(Window window, string keyName)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        Program.Check(display != IntPtr.Zero, "Could not open the test X11 display.");
        try
        {
            XGetInputFocus(display, out var focusedWindow, out _);
            Program.Check(focusedWindow == window.TryGetPlatformHandle()!.Handle,
                $"Native keyboard focus left the player window: focus={focusedWindow}, player={window.TryGetPlatformHandle()!.Handle}.");
            var keyCode = XKeysymToKeycode(display, XStringToKeysym(keyName));
            Program.Check(keyCode != 0, $"No X11 keycode for {keyName}.");
            var keyEvent = new XKeyEvent
            {
                Display = display, Window = focusedWindow, Root = XDefaultRootWindow(display),
                SameScreen = 1, KeyCode = keyCode, SendEvent = 1
            };
            keyEvent.Type = 2; // KeyPress
            Program.Check(XSendEvent(display, focusedWindow, 0, 1, ref keyEvent) != 0, "X11 key press failed.");
            keyEvent.Type = 3; // KeyRelease
            Program.Check(XSendEvent(display, focusedWindow, 0, 2, ref keyEvent) != 0, "X11 key release failed.");
            XFlush(display);
        }
        finally { XCloseDisplay(display); }
    }

    [StructLayout(LayoutKind.Sequential, Size = 192)]
    private struct XKeyEvent
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public IntPtr Display;
        public IntPtr Window;
        public IntPtr Root;
        public IntPtr Subwindow;
        public nuint Time;
        public int X, Y, XRoot, YRoot;
        public uint State, KeyCode;
        public int SameScreen;
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XGetInputFocus(IntPtr display, out IntPtr focus, out int revert);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern nuint XStringToKeysym(string name);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(IntPtr display, nuint keysym);
    [DllImport("libX11.so.6")] private static extern int XSendEvent(IntPtr display, IntPtr window, int propagate, nint mask, ref XKeyEvent keyEvent);
    [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
}
