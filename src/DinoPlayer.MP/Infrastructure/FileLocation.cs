using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DinoPlayer.MP;

internal static class FileLocation
{
    private static readonly TimeSpan FileManagerTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Opens the folder that contains <paramref name="path"/>, with the file
    /// selected where the platform's file manager supports it.
    /// </summary>
    public static async Task ShowAsync(TopLevel? topLevel, string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The exported file was moved or deleted.", path);
        if (OperatingSystem.IsWindows())
        {
            // Explorer parses /select itself, so the quoted path goes in one argument string.
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            return;
        }

        if (await TrySelectInFileManagerAsync(path)) return;
        var folder = Path.GetDirectoryName(path)!;
        if (topLevel?.Launcher is not { } launcher || !await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)))
            throw new InvalidOperationException($"No file manager could open {folder}.");
    }

    // The freedesktop FileManager1 interface (Files, Dolphin, Nemo, Thunar and
    // others) opens the folder with the file selected.
    private static async Task<bool> TrySelectInFileManagerAsync(string path)
    {
        // dbus-send separates array items with commas.
        var uri = new Uri(path).AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal);
        var start = new ProcessStartInfo("dbus-send")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList =
            {
                "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems",
                $"array:string:{uri}", "string:"
            }
        };
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null) return false;
            using var timeout = new CancellationTokenSource(FileManagerTimeout);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error);
            if (process.ExitCode == 0) return true;
            AppLog.Info($"File manager selection unavailable: {(await error).Trim()}");
            return false;
        }
        catch (Exception exception) when (exception is Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            AppLog.Info($"File manager selection unavailable: {exception.Message}");
            try { if (process is { HasExited: false }) process.Kill(); }
            catch (InvalidOperationException) { }
            return false;
        }
        finally { process?.Dispose(); }
    }
}
