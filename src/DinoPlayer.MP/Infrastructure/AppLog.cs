using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace DinoPlayer.MP;

internal static class AppLog
{
    // One log file is written per launch; older ones beyond this count are removed.
    internal const int RetainedLogFiles = 20;
    private const string LogFilePattern = "dinoplayer-mp-*.log";
    // File and console output happen on a background task so logging never
    // waits on the disk from the UI or libmpv threads. A burst beyond this many
    // unwritten lines drops the oldest ones rather than blocking the caller.
    private const int MaxPendingLines = 16_384;
    private static readonly object Sync = new();
    private static readonly Channel<string> Pending = Channel.CreateBounded<string>(
        new BoundedChannelOptions(MaxPendingLines) { FullMode = BoundedChannelFullMode.DropOldest });
    private static readonly Task Writer = Task.Run(WriteLoopAsync);
    private static StreamWriter? _writer;

    public static event Action<string>? MessageWritten;

    public static string LogPath { get; private set; } = string.Empty;

    public static void Initialize()
    {
        try
        {
            var logDirectory = Path.Combine(RuntimePaths.DataDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            PruneOldLogs(logDirectory, RetainedLogFiles - 1);
            LogPath = Path.Combine(logDirectory, $"dinoplayer-mp-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
            var writer = new StreamWriter(LogPath, append: false, new UTF8Encoding(false));
            lock (Sync) _writer = writer;
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

    internal static void PruneOldLogs(string directory, int keep)
    {
        try
        {
            var stale = new DirectoryInfo(directory).GetFiles(LogFilePattern)
                .OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .Skip(Math.Max(0, keep));
            foreach (var file in stale)
            {
                try { file.Delete(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message.TrimEnd()}";
        if (!Pending.Writer.TryWrite(line))
            WriteConsole(line); // After shutdown.

        if (MessageWritten is { } subscribers)
            foreach (Action<string> subscriber in subscribers.GetInvocationList())
            {
                try { subscriber(line); }
                catch (Exception exception) { WriteConsole($"Log subscriber failed: {exception.Message}"); }
            }
    }

    private static async Task WriteLoopAsync()
    {
        try
        {
            while (await Pending.Reader.WaitToReadAsync().ConfigureAwait(false))
                WritePending();
        }
        catch (Exception exception) { WriteConsole($"Log writer stopped: {exception.Message}"); }
    }

    // Writes every queued line, one file flush and one console write per batch.
    private static void WritePending()
    {
        lock (Sync)
        {
            var batch = new StringBuilder();
            while (Pending.Reader.TryRead(out var line))
                batch.AppendLine(line);
            if (batch.Length == 0) return;

            var text = batch.ToString();
            try
            {
                _writer?.Write(text);
                _writer?.Flush();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                DisposeWriter();
                WriteConsole($"File logging stopped: {exception.Message}");
            }
            WriteConsole(text.TrimEnd());
        }
    }

    /// <summary>Writes queued lines now, for example before the process terminates.</summary>
    public static void Flush() => WritePending();

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
        Pending.Writer.TryComplete();
        try { Writer.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        lock (Sync)
        {
            WritePending();
            DisposeWriter();
        }
    }
}
