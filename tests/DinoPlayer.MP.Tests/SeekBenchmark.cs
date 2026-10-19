using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

// Reflection allows the same driver to measure an unchanged baseline assembly.
internal static class SeekBenchmark
{
    public static int Run(string assemblyPath, string source)
    {
        var result = 1;
        var thread = new Thread(() => result = RunUi(assemblyPath, source));
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join(); return result;
    }

    private static int RunUi(string assemblyPath, string source)
    {
        Directory.CreateDirectory(Program.Temp);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Program.Temp);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Program.Temp);
        var context = new AssemblyLoadContext("seek-benchmark");
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        assembly.GetType("DinoPlayer.MP.Settings.SettingsStore")!.GetMethod("Initialize")!.Invoke(null, null);
        AppBuilder.Configure(() => (Application)Activator.CreateInstance(assembly.GetType("DinoPlayer.MP.App")!)!)
            .UsePlatformDetect().SetupWithoutStarting();
        using var done = new CancellationTokenSource();
        var result = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            Window? window = null;
            try
            {
                window = (Window)Activator.CreateInstance(assembly.GetType("DinoPlayer.MP.MainWindow")!)!;
                window.Show();
                await SeekTests.Until(() => window.FindControl<MenuItem>("OpenMenuItem")!.IsEnabled);
                Call(window, "LoadVideo", source);
                await SeekTests.Until(() => window.FindControl<Button>("PlayPauseButton")!.IsEnabled);
                var player = Field(window, "_player")!;
                await PlayerCall(player, "SetPausedAsync", true);
                await PlayerCall(player, "SetMasterVolumeAsync", 0d);
                await PlayerCall(player, "SetLoopingAsync", false);
                using var probe = new NativePlaybackProbe(player);
                var root = window.FindControl<Grid>("RootGrid")!;
                var logEvent = assembly.GetType("DinoPlayer.MP.AppLog")!.GetEvent("MessageWritten", BindingFlags.Public | BindingFlags.Static)!;
                var dispatches = new ConcurrentQueue<double>();
                long started = 0;
                Action<string> log = line =>
                {
                    var timestamp = Interlocked.Read(ref started);
                    if (timestamp != 0 && line.Contains("Seek requested:", StringComparison.Ordinal))
                        dispatches.Enqueue(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds);
                };
                logEvent.AddEventHandler(null, log);
                try
                {
                    var samples = new List<object>();
                    foreach (var (name, count, spacing) in new[] { ("single", 1, 0), ("burst", 5, 0), ("repeat", 12, 35) })
                    for (var trial = 0; trial < 5; trial++)
                    {
                        var before = probe.Restarts;
                        await PlayerCall(player, "SeekAsync", 22d);
                        await SeekTests.Until(() => probe.Restarts > before && !probe.IsSeeking && Math.Abs(probe.Position - 22) < .1);
                        await SeekTests.Until(() => Math.Abs(SnapshotPosition(player) - 22) < .1);
                        Call(window, "UpdatePlaybackUi", null, null);
                        dispatches.Clear();
                        before = probe.Restarts;
                        Interlocked.Exchange(ref started, Stopwatch.GetTimestamp());
                        for (var i = 0; i < count; i++)
                        {
                            var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right };
                            root.RaiseEvent(key);
                            Program.Check(key.Handled, "Benchmark arrow was not handled.");
                            if (spacing > 0 && i + 1 < count) await Task.Delay(spacing);
                        }
                        var released = Stopwatch.GetTimestamp();
                        var framesDuringInput = probe.Restarts - before;
                        await SeekTests.Until(() => probe.Restarts > before && !probe.IsSeeking);
                        // Let any already submitted seeks finish before reporting
                        // the final native restart and the remaining release tail.
                        var displaySamples = new List<(double Position, long Timestamp)>();
                        var settling = Stopwatch.StartNew();
                        do
                        {
                            var position = (double)Field(window, "_currentPos")!;
                            if (Math.Abs(window.FindControl<Slider>("FullscreenTimelineSlider")!.Value - position) < .1)
                                displaySamples.Add((position, Stopwatch.GetTimestamp()));
                            await Task.Delay(5);
                        }
                        while ((Stopwatch.GetElapsedTime(probe.LastRestartTimestamp).TotalMilliseconds < 150 || probe.IsSeeking ||
                            Math.Abs((double)Field(window, "_currentPos")! - probe.Position) > .1) && settling.Elapsed < TimeSpan.FromSeconds(10));
                        Program.Check(!probe.IsSeeking && Math.Abs((double)Field(window, "_currentPos")! - probe.Position) < .1,
                            "Benchmark seek backlog or timeline did not settle.");
                        var inputTimestamp = Interlocked.Exchange(ref started, 0);
                        var snapshot = Call(player, "GetSnapshot")!;
                        displaySamples.Add(((double)Field(window, "_currentPos")!, Stopwatch.GetTimestamp()));
                        var displayed = displaySamples.First(s => Math.Abs(s.Position - probe.Position) < .1).Timestamp;
                        samples.Add(new
                        {
                            scenario = name, trial, inputs = count, nativeCommands = dispatches.Count,
                            firstCommandMs = dispatches.DefaultIfEmpty().First(),
                            lastCommandMs = dispatches.DefaultIfEmpty().Last(),
                            restartMs = Stopwatch.GetElapsedTime(inputTimestamp, probe.LastRestartTimestamp).TotalMilliseconds,
                            releaseTailMs = Math.Max(0, Stopwatch.GetElapsedTime(released, probe.LastRestartTimestamp).TotalMilliseconds),
                            timelineMs = Stopwatch.GetElapsedTime(inputTimestamp, displayed).TotalMilliseconds,
                            timelineReleaseTailMs = Math.Max(0, Stopwatch.GetElapsedTime(released, displayed).TotalMilliseconds),
                            framesDuringInput, completedRestarts = probe.Restarts - before, finalPosition = probe.Position,
                            videoOutput = snapshot.GetType().GetProperty("VideoOutput")!.GetValue(snapshot),
                            hardwareDecoder = snapshot.GetType().GetProperty("HardwareDecoder")!.GetValue(snapshot)
                        });
                    }
                    Console.WriteLine("SEEK_BENCHMARK " + JsonSerializer.Serialize(new { assemblyPath, source, samples }));
                    result = 0;
                }
                finally { logEvent.RemoveEventHandler(null, log); }
            }
            catch (Exception exception) { Console.Error.WriteLine($"SEEK BENCHMARK FAILED: {exception}"); }
            finally
            {
                window?.Close();
                if (window is not null && Field(window, "_playerShutdown") is Task stop) await stop;
                done.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        return result;
    }

    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
    private static double SnapshotPosition(object player)
    {
        var snapshot = Call(player, "GetSnapshot")!;
        return (double)snapshot.GetType().GetProperty("PositionSeconds")!.GetValue(snapshot)!;
    }
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)!.Invoke(target, args);
    private static Task PlayerCall(object player, string name, params object?[] args) => (Task)Call(player, name, args)!;
}
