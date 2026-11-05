using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using DinoPlayer.MP.Playback;

internal static class SeekTests
{
    public static async Task<string> CreateFixture(string directory, bool longGop = false)
    {
        var path = Path.Combine(directory, longGop ? "seek-long-gop.mkv" : "seek-all-frames.mkv");
        var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "testsrc2=size=160x90:rate=30:duration=180", "-c:v", "libx264", "-preset", "ultrafast",
            "-g", longGop ? "300" : "1", "-keyint_min", longGop ? "300" : "1", "-sc_threshold", "0", path })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Program.Check(process.ExitCode == 0, $"Seek fixture generation failed: {await error}");
        return path;
    }

    public static async Task Run()
    {
        var source = await CreateFixture(Program.Temp);
        await Program.Test("relative seek bursts, order, boundaries, pause and generations", () => NativeSeeks(source));
        await Program.Test("shutdown completes pending relative and absolute seeks", () => PendingShutdown(source));
        await Program.Test("deferred audio setup waits for readiness and completes on shutdown", DeferredMixShutdown);
        await Program.Test("playback preparation waits for its frame and cancels on replacement and shutdown", () => PreparationLifecycle(source));
        var longGop = await CreateFixture(Program.Temp, longGop: true);
        await Program.Test("navigation uses keyframes while absolute jumps remain precise", () => KeyframeSeeks(longGop));
        await Program.Test("pause and speed changes notify at once with a fresh position", () => InstantStateNotifications(source));
        await Program.Test("frame steps, exact one-second steps, drag previews and fresh positions", () => ClipNavigation(longGop));
    }

    private static async Task InstantStateNotifications(string source)
    {
        using var engine = new MpvPlaybackEngine(IntPtr.Zero, "seek-state", headless: true);
        using var probe = new NativePlaybackProbe(engine);
        var notifications = 0;
        engine.PositionChanged += _ => Interlocked.Increment(ref notifications);
        await engine.OpenAsync(source);
        await Until(() => engine.GetSnapshot().IsLoaded && probe.Restarts > 0);
        await engine.SetPausedAsync(true);
        await SeekTo(engine, probe, 20, .02);

        foreach (var paused in new[] { false, true })
        {
            var before = Volatile.Read(ref notifications);
            var sampled = engine.GetSnapshot().SampleTimestamp;
            await engine.SetPausedAsync(paused);
            await Until(() => Volatile.Read(ref notifications) > before && engine.GetSnapshot().IsPaused == paused &&
                              engine.GetSnapshot().SampleTimestamp > sampled,
                () => $"Changing pause to {paused} did not notify with a new sample.");
        }
        // The pause notification carries the paused frame, so the UI does not jump back.
        await Until(() => Math.Abs(engine.GetSnapshot().PositionSeconds - probe.Position) < .02,
            () => $"Paused snapshot {engine.GetSnapshot().PositionSeconds} differs from mpv {probe.Position}.");

        var beforeSpeed = Volatile.Read(ref notifications);
        await engine.AdjustPlaybackSpeedAsync(1);
        await Until(() => Volatile.Read(ref notifications) > beforeSpeed && Math.Abs(engine.GetSnapshot().PlaybackSpeed - 1.1) < .001,
            () => "A speed change did not notify.");
        await engine.AdjustPlaybackSpeedAsync(-1);
        await Until(() => Math.Abs(engine.GetSnapshot().PlaybackSpeed - 1) < .001);
    }

    // A keyframe every ten seconds separates fast previews from exact positioning.
    private static async Task ClipNavigation(string longGop)
    {
        const double frame = 1 / 30d;
        using var engine = new MpvPlaybackEngine(IntPtr.Zero, "seek-clip", headless: true);
        using var probe = new NativePlaybackProbe(engine);
        var errors = new ConcurrentQueue<PlaybackError>();
        engine.Error += errors.Enqueue;
        await engine.FrameStepAsync(1); // Empty players must ignore frame steps.
        await engine.OpenAsync(longGop);
        await Until(() => engine.GetSnapshot().IsLoaded && probe.Restarts > 0);
        await engine.SetPausedAsync(true);
        await SeekTo(engine, probe, 23, .02);

        await engine.FrameStepAsync(1);
        await Until(() => probe.IsPaused && !probe.IsSeeking && Math.Abs(probe.Position - (23 + frame)) < .005,
            () => $"Frame step landed at {probe.Position}.");
        await engine.FrameStepAsync(-1);
        await Until(() => probe.IsPaused && !probe.IsSeeking && Math.Abs(probe.Position - 23) < .005,
            () => $"Frame back-step landed at {probe.Position}.");

        await Settled(probe, () => engine.SeekRelativeExactAsync(1), 24, .02);
        await Settled(probe, () => Task.WhenAll(engine.SeekRelativeExactAsync(-1), engine.SeekRelativeExactAsync(-1)), 22, .02);
        Program.Check(probe.IsPaused, "Exact relative steps resumed paused playback.");
        Program.Check(Math.Abs(await engine.GetPositionAsync() - probe.Position) < .001, "A fresh position read differs from mpv.");

        var before = probe.Restarts;
        await engine.SeekPreviewAsync(27);
        await Until(() => probe.Restarts > before && !probe.IsSeeking && Math.Abs(probe.Position - 10 * Math.Round(probe.Position / 10)) < .08,
            () => $"A drag preview did not land on a keyframe: {probe.Position}.");
        Program.Throws<ArgumentOutOfRangeException>(() => engine.SeekPreviewAsync(double.NaN));
        Program.Throws<ArgumentOutOfRangeException>(() => engine.SeekRelativeExactAsync(double.PositiveInfinity));

        await engine.SetPausedAsync(false);
        await Task.Delay(300);
        Program.Check(Math.Abs(await engine.GetPositionAsync() - probe.Position) < .1, "A fresh position read lagged playback.");
        await engine.SetPausedAsync(true);
        Program.Check(errors.IsEmpty, string.Join("; ", errors.Select(e => e.Message)));
    }

    private static async Task KeyframeSeeks(string source)
    {
        using var engine = new MpvPlaybackEngine(IntPtr.Zero, "seek-keyframes", headless: true);
        using var probe = new NativePlaybackProbe(engine);
        var positions = new ConcurrentQueue<PlaybackSnapshot>();
        var staleNotifications = 0;
        engine.PositionChanged += generation =>
        {
            var snapshot = engine.GetSnapshot();
            if (snapshot.Generation != generation) Interlocked.Increment(ref staleNotifications);
            positions.Enqueue(snapshot);
        };
        await engine.OpenAsync(source);
        await Until(() => engine.GetSnapshot().IsLoaded && probe.Restarts > 0);
        await engine.SetPausedAsync(true);
        await SeekTo(engine, probe, 22, .08);
        await Settled(probe, () => engine.SeekRelativeAsync(5), 30, .08);
        await Settled(probe, () => engine.SeekRelativeAsync(-5), 20, .08);
        await SeekTo(engine, probe, 27, .08);
        await Until(() => positions.Any(p => Math.Abs(p.PositionSeconds - 30) < .08) &&
            positions.Any(p => Math.Abs(p.PositionSeconds - 27) < .08));
        Program.Check(staleNotifications == 0, "Seek notification carried a stale snapshot.");
        Program.Check(probe.IsPaused, "Switching seek modes resumed paused playback.");
    }

    private static async Task NativeSeeks(string source)
    {
        using var engine = new MpvPlaybackEngine(IntPtr.Zero, "seek-test", headless: true);
        using var probe = new NativePlaybackProbe(engine);
        var errors = new ConcurrentQueue<PlaybackError>();
        engine.Error += errors.Enqueue;
        await engine.SeekRelativeAsync(5); // Empty players must ignore navigation.
        await engine.SeekRelativeAsync(0);
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Program.Throws<ArgumentOutOfRangeException>(() => engine.SeekRelativeAsync(value));
            Program.Throws<ArgumentOutOfRangeException>(() => engine.SeekAsync(value));
        }
        await engine.OpenAsync(source);
        await Until(() => engine.GetSnapshot().IsLoaded && probe.Restarts > 0);
        await engine.SetPausedAsync(true);
        await Until(() => probe.IsPaused);
        await SeekTo(engine, probe, 20);
        await Settled(probe, () => Task.WhenAll(Enumerable.Range(0, 5).Select(_ => engine.SeekRelativeAsync(5))), 45);
        await Settled(probe, () => Task.WhenAll(Enumerable.Range(0, 5).Select(_ => engine.SeekRelativeAsync(-5))), 20);
        Program.Check(probe.IsPaused, "Relative navigation resumed paused playback.");

        foreach (var laterAbsolute in new[] { false, true })
        {
            await SeekTo(engine, probe, 20);
            await Settled(probe, async () =>
            {
                using var held = new WorkerHold(engine);
                var requests = new List<Task> { engine.SeekAsync(30), engine.SeekRelativeAsync(5) };
                if (laterAbsolute) requests.Add(engine.SeekAsync(70));
                held.Release();
                await Task.WhenAll(requests);
            }, laterAbsolute ? 70 : 35);
        }
        await Settled(probe, () => Task.WhenAll(engine.SeekRelativeAsync(5), engine.SeekRelativeAsync(-10)), 65);
        await SeekTo(engine, probe, 35.237, .08);
        Program.Check(probe.IsPaused, "An exact seek resumed paused playback.");
        await Settled(probe, () => engine.SeekRelativeAsync(-1000), 0);
        await engine.SeekRelativeAsync(1000);
        await Until(() => probe.EofReached && !probe.IsSeeking);
        Program.Check(probe.Position >= 179, "Forward seeking past the end wrapped unexpectedly.");

        await SeekTo(engine, probe, 170);
        await engine.SetLoopingAsync(true);
        await engine.SetPausedAsync(false);
        await engine.SeekRelativeAsync(15);
        await Until(() => !probe.EofReached && probe.Position < 5 && !probe.IsSeeking);
        await engine.SetLoopingAsync(false);
        await SeekTo(engine, probe, 20);
        await Settled(probe, () => Task.WhenAll(Enumerable.Range(0, 5).Select(_ => engine.SeekRelativeAsync(5))), 45, 1.5);
        Program.Check(!probe.IsPaused, "Relative navigation paused playing media.");
        await engine.SetPausedAsync(true);

        using (var held = new WorkerHold(engine))
        {
            var obsolete = new[] { engine.SeekAsync(90), engine.SeekRelativeAsync(5) };
            var open = engine.OpenAsync(source);
            held.Release();
            await Task.WhenAll(obsolete.Append(open));
        }
        await Until(() => engine.GetSnapshot().IsLoaded && engine.GetSnapshot().Generation == engine.Generation);
        await Until(() => !probe.IsSeeking && probe.Position < 2);
        Program.Check(errors.IsEmpty, string.Join("; ", errors.Select(e => e.Message)));
    }

    private static async Task PendingShutdown(string source)
    {
        using var engine = new MpvPlaybackEngine(IntPtr.Zero, "seek-shutdown", headless: true);
        await engine.OpenAsync(source);
        await Until(() => engine.GetSnapshot().IsLoaded);
        using var held = new WorkerHold(engine);
        var requests = Enumerable.Range(0, 50).Select(i => i % 2 == 0 ? engine.SeekRelativeAsync(5) : engine.SeekAsync(20)).ToArray();
        var stop = engine.StopAsync();
        held.Release();
        await Task.WhenAll(requests.Append(stop)).WaitAsync(TimeSpan.FromSeconds(5));
        Program.Throws<ObjectDisposedException>(() => engine.SeekRelativeAsync(5));
        Program.Throws<ObjectDisposedException>(() => engine.SeekAsync(5));
    }

    private static async Task PreparationLifecycle(string source)
    {
        using var engine = new MpvPlaybackEngine(IntPtr.Zero, "seek-preparation", headless: true);
        await engine.OpenAsync(source, startPaused: true);
        await Until(() => engine.GetSnapshot().IsLoaded);
        await engine.SeekAndWaitAsync(1.25);
        Program.Check(engine.GetSnapshot().IsPaused && Math.Abs(engine.GetSnapshot().PositionSeconds - 1.25) < .08,
            "Playback preparation completed before its paused target frame.");

        using (var held = new WorkerHold(engine))
        {
            var preparing = engine.SeekAndWaitAsync(30);
            var opening = engine.OpenAsync(source, startPaused: true);
            held.Release();
            try
            {
                await preparing.WaitAsync(TimeSpan.FromSeconds(2));
                throw new InvalidOperationException("Superseded playback preparation succeeded.");
            }
            catch (OperationCanceledException) { }
            await opening;
        }
        await Until(() => engine.GetSnapshot().IsLoaded && engine.GetSnapshot().Generation == engine.Generation);

        using (var held = new WorkerHold(engine))
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var preparing = engine.SeekAndWaitAsync(30);
            var worker = (PlaybackWorker)typeof(MpvPlaybackEngine).GetField("_worker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
            var barrier = worker.InvokeAsync(() => { entered.Set(); release.Wait(); return 0; });
            held.Release();
            try
            {
                Program.Check(entered.Wait(2000), "Could not hold playback before its seek-restart event.");
                var stop = engine.StopAsync();
                release.Set();
                await Task.WhenAll(stop, barrier).WaitAsync(TimeSpan.FromSeconds(2));
                try
                {
                    await preparing.WaitAsync(TimeSpan.FromSeconds(2));
                    throw new InvalidOperationException("Shutdown left playback preparation successful.");
                }
                catch (ObjectDisposedException) { }
            }
            finally { release.Set(); }
        }
    }

    private static async Task DeferredMixShutdown()
    {
        using var engine = new MpvPlaybackEngine(IntPtr.Zero, "deferred-audio", headless: true);
        var pending = engine.ApplyAudioMixAsync();
        var worker = (PlaybackWorker)typeof(MpvPlaybackEngine).GetField("_worker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        await worker.InvokeAsync(() => 0);
        Program.Check(!pending.IsCompleted, "Audio setup completed before media/video readiness.");
        await engine.StopAsync();
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("Shutdown reported that the unapplied audio graph completed.");
        }
        catch (ObjectDisposedException) { }
    }

    internal static Task SeekTo(IPlaybackEngine engine, NativePlaybackProbe probe, double target, double tolerance = .35) =>
        Settled(probe, () => engine.SeekAsync(target), target, tolerance);

    internal static async Task Settled(NativePlaybackProbe probe, Func<Task> action, double target, double tolerance = .35)
    {
        var before = probe.Restarts;
        await action();
        await Until(() => probe.Restarts > before && !probe.IsSeeking && Math.Abs(probe.Position - target) <= tolerance,
            () => $"Seek did not settle near {target}: position={probe.Position}; restarts={probe.Restarts - before}; seeking={probe.IsSeeking}.");
    }

    internal static async Task Until(Func<bool> condition, Func<string>? failure = null)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(failure?.Invoke() ?? "Seek test timed out.");
            await Task.Delay(10);
        }
    }

    internal sealed class WorkerHold : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Task<int> _operation;

        public WorkerHold(MpvPlaybackEngine engine)
        {
            var worker = (PlaybackWorker)typeof(MpvPlaybackEngine).GetField("_worker", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
            _operation = worker.InvokeAsync(() => { _entered.Set(); _release.Wait(); return 0; });
            if (!_entered.Wait(2000))
            {
                Release();
                throw new TimeoutException("Could not hold the playback worker.");
            }
        }

        public void Release() => _release.Set();
        public void Dispose()
        {
            Release();
            _operation.GetAwaiter().GetResult();
            _entered.Dispose();
            _release.Dispose();
        }
    }
}
