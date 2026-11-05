using System.Reflection;
using System.Runtime.InteropServices;

namespace DinoPlayer.MP.Playback;

internal static class MpvNative
{
    private const string LogicalLibraryName = "dinoplayer_mpv";
    private static readonly object LoadSync = new();
    private static IntPtr _libraryHandle;

    static MpvNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, ResolveLibrary);
    }

    public static string LoadedLibraryDescription { get; private set; } = "not loaded";

    public static void EnsureLoaded()
    {
        lock (LoadSync)
        {
            if (_libraryHandle != IntPtr.Zero)
            {
                return;
            }

            var overridePath = Environment.GetEnvironmentVariable("DINOPLAYER_MPV_LIBRARY");
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                candidates.Add(Path.GetFullPath(overridePath));
            }

            if (OperatingSystem.IsWindows())
            {
                candidates.Add(Path.Combine(AppContext.BaseDirectory, "mpv-2.dll"));
                candidates.Add(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"));
                candidates.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "mpv-2.dll"));
                candidates.Add("mpv-2.dll");
                candidates.Add("libmpv-2.dll");
                candidates.Add("mpv-1.dll");
            }
            else if (OperatingSystem.IsLinux())
            {
                candidates.Add(Path.Combine(AppContext.BaseDirectory, "libmpv.so.2"));
                candidates.Add(Path.Combine(AppContext.BaseDirectory, "lib", "libmpv.so.2"));
                candidates.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", "libmpv.so.2"));
                // Assembly-aware loading also searches native libraries extracted
                // from a self-contained single-file executable.
                candidates.Add("libmpv.so.2");
                candidates.Add("/usr/lib64/libmpv.so.2");
                candidates.Add("/usr/lib/x86_64-linux-gnu/libmpv.so.2");
                candidates.Add("/usr/lib/aarch64-linux-gnu/libmpv.so.2");
                candidates.Add("libmpv.so.2");
                candidates.Add("libmpv.so.1");
                candidates.Add("libmpv.so");
            }
            else
            {
                throw new PlatformNotSupportedException("This MP supports Windows and Linux/X11 only.");
            }

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (NativeLibrary.TryLoad(candidate, typeof(MpvNative).Assembly, null, out _libraryHandle))
                {
                    LoadedLibraryDescription = candidate;
                    AppLog.Info($"Loaded libmpv from: {candidate}");
                    return;
                }
            }

            throw new DllNotFoundException(
                "Could not load libmpv. Install mpv-libs on Fedora or place mpv-2.dll next to the Windows executable. " +
                "Set DINOPLAYER_MPV_LIBRARY to an exact library path to override discovery. Tried: " +
                string.Join(", ", candidates));
        }
    }

    private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LogicalLibraryName)
        {
            return IntPtr.Zero;
        }

        EnsureLoaded();
        return _libraryHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MpvEvent
    {
        public readonly int EventId;
        public readonly int Error;
        public readonly ulong ReplyUserData;
        public readonly IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MpvEventLogMessage
    {
        public readonly IntPtr Prefix;
        public readonly IntPtr Level;
        public readonly IntPtr Text;
        public readonly int LogLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MpvEventEndFile
    {
        public readonly int Reason;
        public readonly int Error;
        public readonly long PlaylistEntryId;
        public readonly long PlaylistInsertId;
        public readonly int PlaylistInsertNumEntries;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MpvEventClientMessage
    {
        public readonly int NumArgs;
        public readonly IntPtr Args;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MpvEventProperty
    {
        public readonly IntPtr Name;
        public readonly int Format;
        public readonly IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MpvEventHook
    {
        public readonly IntPtr Name;
        public readonly ulong Id;
    }

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_hook_add(IntPtr context, ulong replyUserData,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int priority);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_hook_continue(IntPtr context, ulong id);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_observe_property(IntPtr context, ulong userData,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_create();

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_initialize(IntPtr context);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_terminate_destroy(IntPtr context);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_wakeup(IntPtr context);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong mpv_client_api_version();

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_option_string(
        IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property_string(
        IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property(
        IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        out double data);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    internal static extern int mpv_get_property_int64(
        IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        out long data);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    internal static extern int mpv_get_property_flag(
        IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        out int data);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_get_property_string(
        IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free(IntPtr data);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_command(IntPtr context, IntPtr arguments);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_wait_event(IntPtr context, double timeoutSeconds);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_request_event(IntPtr context, int eventId, int enable);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_request_log_messages(
        IntPtr context,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string minimumLevel);

    [DllImport(LogicalLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_error_string(int error);
}
