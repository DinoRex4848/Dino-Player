using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

internal static class PlaybackBenchmark
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
        var context = new AssemblyLoadContext("benchmark");
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        assembly.GetType("DinoPlayer.MP.Settings.SettingsStore")!.GetMethod("Initialize")!.Invoke(null, null);
        AppBuilder.Configure(() => (Application)Activator.CreateInstance(assembly.GetType("DinoPlayer.MP.App")!)!).UsePlatformDetect().SetupWithoutStarting();
        using var done = new CancellationTokenSource();
        var result = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            Window? window = null;
            try
            {
                window = (Window)Activator.CreateInstance(assembly.GetType("DinoPlayer.MP.MainWindow")!)!;
                window.Show();
                await Until(() => window.FindControl<MenuItem>("OpenMenuItem")!.IsEnabled);
                await Task.Delay(500);
                Call(window, "LoadVideo", source);
                await Until(() => window.FindControl<Button>("PlayPauseButton")!.IsEnabled);
                var player = Field(window, "_player")!;
                await PlayerCall(player, "SetMasterVolume", 0d);
                await PlayerCall(player, "SetLooping", true);
                await PlayerCall(player, "SetPaused", false);
                await Task.Delay(2000);
                var process = Process.GetCurrentProcess();
                var cpuStart = process.TotalProcessorTime;
                var allocations = GC.GetTotalAllocatedBytes(true);
                var memoryStart = GC.GetTotalMemory(true);
                var heartbeat = new List<double>();
                var lastTick = Stopwatch.GetTimestamp();
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Background, (_, _) =>
                {
                    var now = Stopwatch.GetTimestamp();
                    heartbeat.Add(Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds);
                    lastTick = now;
                });
                timer.Start();
                var wall = Stopwatch.StartNew();
                await Task.Delay(10000);
                var playbackSeconds = wall.Elapsed.TotalSeconds;
                var cpuMs = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                var allocated = GC.GetTotalAllocatedBytes(true) - allocations;
                var retained = GC.GetTotalMemory(true) - memoryStart;
                var snapshot = Call(player, "GetSnapshot")!;
                var dropped = snapshot.GetType().GetProperty("OutputDroppedFrames")!.GetValue(snapshot);
                timer.Stop();
                var playbackP95 = Percentile(heartbeat, .95);
                var playbackMax = heartbeat.DefaultIfEmpty().Max();
                await PlayerCall(player, "SetPaused", true);
                var updateTimer = (DispatcherTimer)Field(window, "_uiTimer")!;
                updateTimer.Stop();

                heartbeat.Clear(); lastTick = Stopwatch.GetTimestamp(); timer.Start();
                var logMethod = window.GetType().GetMethod("OnLogMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
                // Measure the visible log; a hidden log is not refreshed.
                var logExpander = window.FindControl<Expander>("DiagnosticLogExpander")!;
                logExpander.IsVisible = logExpander.IsExpanded = true;
                var logStart = Stopwatch.StartNew();
                var logAllocated = GC.GetTotalAllocatedBytes(true);
                await Task.Run(() =>
                {
                    for (var i = 0; i < 3000; i++) logMethod.Invoke(window, [$"BENCH {i:D4} " + new string('x', 70)]);
                });
                await Until(() => window.FindControl<TextBox>("LogTextBox")!.Text?.Contains("BENCH 2999") == true);
                await Task.Delay(300);
                timer.Stop();
                var logMs = logStart.Elapsed.TotalMilliseconds;
                var logBytes = GC.GetTotalAllocatedBytes(true) - logAllocated;
                var logChars = window.FindControl<TextBox>("LogTextBox")!.Text!.Length;
                var logMax = heartbeat.DefaultIfEmpty().Max();

                var timelineStart = Stopwatch.StartNew();
                var timelineAllocated = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 10; i++) Call(window, "SetTimelineDuration", 86400d);
                var timelineMs = timelineStart.Elapsed.TotalMilliseconds;
                var timelineBytes = GC.GetAllocatedBytesForCurrentThread() - timelineAllocated;
                var markers = window.FindControl<Canvas>("TimelineCanvas")!.Children.Count;
                var report = new
                {
                    assembly = assemblyPath, source, playbackSeconds,
                    playbackCpuMs = cpuMs, playbackAllocatedBytes = allocated, playbackRetainedBytes = retained,
                    playbackUiP95Ms = playbackP95, playbackUiMaxMs = playbackMax, outputDroppedFrames = dropped,
                    logBurstMs = logMs, logAllocatedBytes = logBytes, logRetainedCharacters = logChars, logUiMaxMs = logMax,
                    timelineTenRebuildsMs = timelineMs, timelineAllocatedBytes = timelineBytes, timelineControls = markers
                };
                Console.WriteLine("BENCHMARK " + JsonSerializer.Serialize(report));
                result = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine($"BENCHMARK FAILED: {exception}"); }
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

    private static double Percentile(List<double> values, double fraction) => values.Count == 0 ? 0 : values.Order().ElementAt((int)((values.Count - 1) * fraction));
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, args);
    private static async Task PlayerCall(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethod(name + "Async") ?? target.GetType().GetMethod(name)!;
        if (method.Invoke(target, args) is Task task) await task;
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Benchmark state did not settle.");
            await Task.Delay(25);
        }
    }
}
