using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

// A separate libmpv client observes completed playback restarts without taking
// events from the application's event loop or changing the production API.
internal sealed class NativePlaybackProbe : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateClient(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyClient(IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr WaitEvent(IntPtr context, double timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDouble(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetFlag(IntPtr context, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out int value);

    private readonly IntPtr _client;
    private readonly DestroyClient _destroy;
    private readonly WaitEvent _wait;
    private readonly GetDouble _getDouble;
    private readonly GetFlag _getFlag;
    private readonly Thread _thread;
    private int _stopped;
    private long _restarts;
    private long _lastRestart;

    public NativePlaybackProbe(object engine)
    {
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        var engineType = engine.GetType();
        var context = (IntPtr)engineType.GetField("_context", fields)!.GetValue(engine)!;
        var nativeType = engineType.Assembly.GetType("DinoPlayer.MP.Playback.MpvNative")!;
        var library = (IntPtr)nativeType.GetField("_libraryHandle", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        _destroy = Export<DestroyClient>("mpv_destroy");
        _wait = Export<WaitEvent>("mpv_wait_event");
        _getDouble = Export<GetDouble>("mpv_get_property");
        _getFlag = Export<GetFlag>("mpv_get_property");
        _client = Export<CreateClient>("mpv_create_client")(context, "seek-test-probe");
        Program.Check(_client != IntPtr.Zero, "Could not create playback event probe.");
        _thread = new Thread(Poll) { IsBackground = true, Name = "seek test events" };
        _thread.Start();
    }

    public long Restarts => Interlocked.Read(ref _restarts);
    public long LastRestartTimestamp => Interlocked.Read(ref _lastRestart);
    public double Position => _getDouble(_client, "time-pos", 5, out var value) >= 0 ? value : double.NaN;
    public bool IsSeeking => Flag("seeking");
    public bool IsPaused => Flag("pause");
    public bool EofReached => Flag("eof-reached");

    private bool Flag(string name) => _getFlag(_client, name, 3, out var value) >= 0 && value != 0;

    private void Poll()
    {
        while (Volatile.Read(ref _stopped) == 0)
        {
            var ev = _wait(_client, .05);
            if (ev != IntPtr.Zero && Marshal.ReadInt32(ev) == 21) // MPV_EVENT_PLAYBACK_RESTART
            {
                Interlocked.Exchange(ref _lastRestart, Stopwatch.GetTimestamp());
                Interlocked.Increment(ref _restarts);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _thread.Join();
        _destroy(_client);
    }
}
