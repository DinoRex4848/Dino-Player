using System.Runtime.InteropServices;
using System.Text;

namespace DinoPlayer.MP;

internal static class AppLog
{
    private static readonly object Sync = new();
    private static StreamWriter? _writer;

    public static event Action<string>? MessageWritten;

    public static string LogPath { get; private set; } = string.Empty;

    public static void Initialize()
    {
        try
        {
            var logDirectory = Path.Combine(RuntimePaths.DataDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            LogPath = Path.Combine(logDirectory, $"dinoplayer-mp-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
            _writer = new StreamWriter(LogPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogPath = "File logging unavailable";
            WriteConsole($"Could not open diagnostic log: {exception.Message}");
        }

        Info("Dino Player MP starting");
        Info($"OS: {RuntimeInformation.OSDescription}; architecture: {RuntimeInformation.OSArchitecture}");
        Info($"Framework: {RuntimeInformation.FrameworkDescription}; process architecture: {RuntimeInformation.ProcessArchitecture}");
        Info($"Desktop environment: XDG_CURRENT_DESKTOP={Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "(unset)"}");
        Info($"Session: XDG_SESSION_TYPE={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "(unset)"}; WAYLAND_DISPLAY={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "(unset)"}; DISPLAY={Environment.GetEnvironmentVariable("DISPLAY") ?? "(unset)"}");
        Info($"Command line: {Environment.CommandLine}");
        Info($"Log file: {LogPath}");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message.TrimEnd()}";
        lock (Sync)
        {
            try { _writer?.WriteLine(line); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                DisposeWriter();
                WriteConsole($"File logging stopped: {exception.Message}");
            }
        }

        WriteConsole(line);
        if (MessageWritten is { } subscribers)
            foreach (Action<string> subscriber in subscribers.GetInvocationList())
            {
                try { subscriber(line); }
                catch (Exception exception) { WriteConsole($"Log subscriber failed: {exception.Message}"); }
            }
    }

    private static void WriteConsole(string message)
    {
        try { Console.Error.WriteLine(message); }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
    }

    private static void DisposeWriter()
    {
        try { _writer?.Dispose(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        finally { _writer = null; }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            DisposeWriter();
        }
    }
}
