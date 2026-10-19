using System.Diagnostics;
using System.Globalization;

namespace DinoPlayer.MP.Export;

internal sealed record FfmpegResult(int? ExitCode, bool Cancelled, string? Error = null);

internal sealed class FfmpegRunner
{
    internal static readonly TimeSpan CancellationGrace = TimeSpan.FromSeconds(3);

    public async Task<FfmpegResult> RunAsync(string executable, ExportCommand command,
        Action<string> log, Action<double> progress, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new(null, true);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true
            }
        };
        foreach (var argument in command.Arguments) process.StartInfo.ArgumentList.Add(argument);
        var started = false;
        try
        {
            process.Start();
            started = true;
            var stderr = ReadLinesAsync(process.StandardError, log);
            var stdout = ReadLinesAsync(process.StandardOutput, line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && command.DurationSeconds > 0 &&
                    double.TryParse(line.AsSpan(12), NumberStyles.Float, CultureInfo.InvariantCulture, out var microseconds) &&
                    double.IsFinite(microseconds))
                    progress(Math.Clamp(microseconds / 1_000_000 / command.DurationSeconds * 100, 0, 100));
            });
            var exit = process.WaitForExitAsync();
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
            if (await Task.WhenAny(exit, cancelled.Task).ConfigureAwait(false) != exit)
            {
                // Start the deadline before writing: even a blocked stdin must not
                // prevent escalation. Observe the write separately after process exit.
                var deadline = Task.Delay(CancellationGrace);
                var stop = RequestStopAsync(process);
                if (await Task.WhenAny(exit, deadline).ConfigureAwait(false) != exit)
                    Kill(process);
                await exit.ConfigureAwait(false);
                // The process has exited, so a stalled stdin flush must not hold
                // the export task (and a closing window) open indefinitely.
                try { await stop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    _ = stop.ContinueWith(task => _ = task.Exception,
                        TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            await exit.ConfigureAwait(false);
            await Task.WhenAll(stderr, stdout).ConfigureAwait(false);
            return new(process.ExitCode, cancellationToken.IsCancellationRequested);
        }
        catch (Exception exception)
        {
            if (started)
            {
                Kill(process);
                try { await process.WaitForExitAsync().ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
            return new(null, cancellationToken.IsCancellationRequested, exception.Message);
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> receive)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            try { receive(line); }
            catch (Exception exception) { AppLog.Warn($"Export progress/log callback failed: {exception.Message}"); }
        }
    }

    private static async Task RequestStopAsync(Process process)
    {
        try
        {
            await process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException) { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    public static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("DINOPLAYER_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var fileName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var besideApp = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(besideApp) ? besideApp : fileName;
    }
}
