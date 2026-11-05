using System.Collections.Immutable;
using System.Threading.Channels;
using DinoPlayer.MP.Export;

internal static class ExportManagerTests
{
    public static async Task Run()
    {
        await Program.Test("queue starts manually and exports sequentially, including new clips", Sequential);
        await Program.Test("standalone export and busy append leave manually saved clips waiting", Standalone);
        await Program.Test("pause finishes current clip, holds additions and allows an independent export", Pause);
        await Program.Test("queue reorder and removal preserve the running clip", Reorder);
        await Program.Test("cancel current waits for runner cleanup before continuing", CancelCurrent);
        await Program.Test("cancel all clears scheduled and waiting work and permits a later run", CancelAll);
        await Program.Test("failed clip continues, retains logs and retries its captured settings", Retry);
        await Program.Test("shutdown rejects new jobs and cancels unfinished work exactly once", Shutdown);
        await Program.Test("destinations are numbered, reserved and normalized", Naming);
        await Program.Test("explicit overwrite publishes only on success and cancellation preserves original", Overwrite);
        await Program.Test("missing source and new destination conflicts fail without invoking FFmpeg", Preflight);
        await Program.Test("export cannot overwrite a source or a video opened during execution", SourceProtection);
        await Program.Test("failed final publication removes temporary output and preserves conflicting files", PublishFailure);
        await Program.Test("runner exceptions continue the queue and progress callbacks cannot change a finished job", RunnerFailure);
        await Program.Test("source protection and destination reservations recognize symlinked folders", Aliases);
        await Program.Test("target size exports encode again until they fit, and stop when that fails", TargetSizeRetries);
    }

    private static async Task TargetSizeRetries()
    {
        // A fake encoder that writes the next size from the list; null waits for cancellation.
        var sizes = new Queue<long?>();
        var commands = new List<ExportCommand>();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(async (_, command, _, progress, token) =>
        {
            commands.Add(command);
            if (sizes.Dequeue() is not { } size)
            {
                waiting.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
                return new(null, true);
            }
            await using (var file = new FileStream(command.Arguments[^1], FileMode.CreateNew)) file.SetLength(size);
            progress(100);
            return new(0, false);
        });
        var manager = fixture.Manager;
        var options = fixture.Options with { VideoRateControl = "Size", VideoTargetSize = "10", Duration = 20 };

        foreach (var size in new long?[] { 12_000_000, 9_000_000 }) sizes.Enqueue(size);
        var fitted = Job(manager, manager.StartExport(options));
        await manager.WhenIdleAsync();
        fitted = Job(manager, fitted.Id);
        var rates = commands.Select(command => command.SizeLimit!.VideoKbps).ToArray();
        Program.Check(fitted.State == ExportJobState.Completed && rates.Length == 2 && rates[1] < rates[0] * .9 &&
                      Program.Value(commands[1].Arguments, "-b:v") == $"{rates[1]}k" && commands[1].SizeLimit!.Attempt == 2,
            $"An oversized clip was not encoded again at a lower bitrate:\n{fitted.Log}");
        Program.Check(new FileInfo(fitted.Definition.Options.OutputPath).Length == 9_000_000 && !File.Exists(fitted.Definition.TemporaryPath) &&
                      fitted.Log.Contains("Encoding again") && fitted.Log.Contains("Size: 9.00 MB"), $"Retry result or log is wrong:\n{fitted.Log}");

        commands.Clear();
        foreach (var size in new long?[] { 12_000_000, 11_900_000 }) sizes.Enqueue(size);
        var stuck = Job(manager, manager.StartExport(options));
        await manager.WhenIdleAsync();
        stuck = Job(manager, stuck.Id);
        Program.Check(stuck.State == ExportJobState.Failed && commands.Count == 2 && stuck.Error!.Contains("did not make it smaller") &&
                      !File.Exists(stuck.Definition.Options.OutputPath) && !File.Exists(stuck.Definition.TemporaryPath),
            $"A clip that would not shrink was published or retried forever:\n{stuck.Log}");

        commands.Clear();
        foreach (var size in new long?[] { 12_000_000, null }) sizes.Enqueue(size);
        var cancelled = Job(manager, manager.StartExport(options));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.CancelCurrentAsync();
        cancelled = Job(manager, cancelled.Id);
        Program.Check(cancelled.State == ExportJobState.Cancelled && commands.Count == 2 &&
                      !File.Exists(cancelled.Definition.Options.OutputPath) && !File.Exists(cancelled.Definition.TemporaryPath),
            "Cancelling a second attempt left files or the wrong state.");
    }

    private static async Task Sequential()
    {
        await using var fixture = new Fixture();
        var manager = fixture.Manager;
        var first = manager.AddToQueue(fixture.Options);
        var second = manager.AddToQueue(fixture.Options);
        Program.Check(fixture.Runner.StartedCount == 0 && manager.GetSnapshot().Jobs.All(job => job.State == ExportJobState.Waiting), "Adding a clip started the queue.");
        manager.StartQueue();
        var execution = await fixture.Runner.Next();
        CheckActive(manager, first);
        var third = manager.AddToQueue(fixture.Options);
        var fourth = manager.StartExport(fixture.Options);
        foreach (var id in new[] { second, third, fourth })
        {
            execution.Succeed();
            execution = await fixture.Runner.Next();
            CheckActive(manager, id);
        }
        execution.Succeed();
        await manager.WhenIdleAsync();
        var snapshot = manager.GetSnapshot();
        Program.Check(snapshot.Jobs.All(job => job.State == ExportJobState.Completed) && !snapshot.QueueStarted, "The queue did not finish.");
        Program.Check(fixture.Runner.MaxConcurrent == 1 && snapshot.Jobs.All(job => File.Exists(job.Definition.Options.OutputPath)), "Jobs overlapped or outputs were not published.");
    }

    private static async Task Standalone()
    {
        await using var fixture = new Fixture();
        var manager = fixture.Manager;
        var held = manager.AddToQueue(fixture.Options);
        var immediate = manager.StartExport(fixture.Options);
        var execution = await fixture.Runner.Next();
        CheckActive(manager, immediate);
        var alsoHeld = manager.AddToQueue(fixture.Options);
        var appended = manager.StartExport(fixture.Options);
        execution.Succeed();
        execution = await fixture.Runner.Next();
        CheckActive(manager, appended);
        execution.Succeed();
        await manager.WhenIdleAsync();
        Program.Check(manager.GetSnapshot().Jobs.Where(job => job.IsPending).Select(job => job.Id).SequenceEqual(new[] { held, alsoHeld }), "A standalone export started saved queue jobs.");
    }

    private static async Task Pause()
    {
        await using var fixture = new Fixture();
        var manager = fixture.Manager;
        manager.AddToQueue(fixture.Options);
        var second = manager.AddToQueue(fixture.Options);
        manager.StartQueue();
        var execution = await fixture.Runner.Next();
        manager.PauseAfterCurrent();
        var third = manager.StartExport(fixture.Options);
        execution.Succeed();
        await manager.WhenIdleAsync();
        Program.Check(manager.GetSnapshot().QueuePaused && fixture.Runner.StartedCount == 1, "Pause started another clip.");
        var fourth = manager.AddToQueue(fixture.Options);
        var independent = manager.StartExport(fixture.Options);
        execution = await fixture.Runner.Next();
        CheckActive(manager, independent);
        execution.Succeed();
        await manager.WhenIdleAsync();
        Program.Check(manager.GetSnapshot().QueuePaused && manager.GetSnapshot().WaitingCount == 3, "Immediate export resumed the paused queue.");
        manager.ResumeQueue();
        foreach (var id in new[] { second, third, fourth })
        {
            execution = await fixture.Runner.Next();
            CheckActive(manager, id);
            execution.Succeed();
        }
        await manager.WhenIdleAsync();
        Program.Check(!manager.GetSnapshot().HasUnfinished && fixture.Runner.MaxConcurrent == 1, "Resume did not finish sequentially.");
    }

    private static async Task Reorder()
    {
        await using var fixture = new Fixture();
        var manager = fixture.Manager;
        var first = manager.AddToQueue(fixture.Options);
        var removed = manager.AddToQueue(fixture.Options);
        var moved = manager.AddToQueue(fixture.Options);
        Program.Check(manager.Move(moved, -1) && manager.Remove(removed), "Waiting jobs could not be changed.");
        manager.StartQueue();
        var execution = await fixture.Runner.Next();
        CheckActive(manager, first);
        Program.Check(!manager.Remove(first) && !manager.Move(first, 1), "A running job was removed or moved.");
        execution.Succeed();
        execution = await fixture.Runner.Next();
        CheckActive(manager, moved);
        execution.Succeed();
        await manager.WhenIdleAsync();
        manager.ClearFinished();
        Program.Check(manager.GetSnapshot().Jobs.IsEmpty, "Finished jobs were not cleared.");
    }

    private static async Task CancelCurrent()
    {
        await using var fixture = new Fixture();
        fixture.Runner.IgnoreCancellation = true;
        var manager = fixture.Manager;
        var first = manager.AddToQueue(fixture.Options);
        var second = manager.AddToQueue(fixture.Options);
        manager.StartQueue();
        var execution = await fixture.Runner.Next();
        var cancel = manager.CancelCurrentAsync();
        Program.Check(!cancel.IsCompleted && fixture.Runner.StartedCount == 1 && execution.Token.IsCancellationRequested, "Cancellation did not wait for FFmpeg.");
        execution.Succeed(); // Even a success result after cancellation must not publish.
        await cancel;
        Program.Check(Job(manager, first).State == ExportJobState.Cancelled && !File.Exists(Job(manager, first).Definition.TemporaryPath) && !File.Exists(Job(manager, first).Definition.Options.OutputPath), "Cancelled clip published or retained partial output.");
        execution = await fixture.Runner.Next();
        CheckActive(manager, second);
        execution.Succeed();
        await manager.WhenIdleAsync();
        Program.Check(fixture.Runner.MaxConcurrent == 1, "Cancellation allowed overlapping runners.");
    }

    private static async Task CancelAll()
    {
        await using var fixture = new Fixture();
        var manager = fixture.Manager;
        manager.AddToQueue(fixture.Options);
        manager.StartQueue();
        await fixture.Runner.Next();
        manager.AddToQueue(fixture.Options);
        await manager.CancelAllAsync();
        Program.Check(manager.GetSnapshot().Jobs.All(job => job.State == ExportJobState.Cancelled) && !manager.GetSnapshot().HasUnfinished, "Cancel all left unfinished jobs.");
        var immediate = manager.StartExport(fixture.Options);
        var execution = await fixture.Runner.Next();
        CheckActive(manager, immediate);
        execution.Succeed();
        await manager.WhenIdleAsync();
        Program.Check(fixture.Runner.StartedCount == 2, "Cancelled jobs restarted.");
        manager.AddToQueue(fixture.Options);
        await manager.CancelAllAsync();
        Program.Check(!manager.GetSnapshot().HasUnfinished, "Idle waiting jobs were not cancelled.");
    }

    private static async Task Retry()
    {
        await using var fixture = new Fixture();
        var manager = fixture.Manager;
        var options = fixture.Options with { CropEnabled = true, Start = .5, End = 2, PlaybackSpeed = 1.5, CopyPlaybackSpeed = true };
        var first = manager.AddToQueue(options);
        var second = manager.AddToQueue(fixture.Options);
        manager.StartQueue();
        var execution = await fixture.Runner.Next();
        execution.Fail("encoder unavailable");
        execution = await fixture.Runner.Next();
        CheckActive(manager, second);
        Program.Check(Job(manager, first).Log.Contains("encoder unavailable") && !File.Exists(Job(manager, first).Definition.TemporaryPath), "Failure log or cleanup was lost.");
        execution.Succeed();
        await manager.WhenIdleAsync();
        var last = manager.AddToQueue(fixture.Options);
        var definition = Job(manager, first).Definition;
        Program.Check(manager.Retry(first) && Job(manager, first).Definition == definition && manager.GetSnapshot().Jobs[^1].Id == first, "Retry did not retain the snapshot at the queue end.");
        manager.StartQueue();
        execution = await fixture.Runner.Next();
        CheckActive(manager, last);
        execution.Succeed();
        execution = await fixture.Runner.Next();
        CheckActive(manager, first);
        Program.Check(execution.Command.Arguments.SequenceEqual(definition.Command.Arguments), "Retry rebuilt settings from another clip.");
        execution.Succeed();
        await manager.WhenIdleAsync();
    }

    private static async Task Shutdown()
    {
        await using var fixture = new Fixture();
        var manager = fixture.Manager;
        manager.AddToQueue(fixture.Options);
        manager.AddToQueue(fixture.Options);
        manager.StartQueue();
        await fixture.Runner.Next();
        await Task.WhenAll(manager.StopAsync(), manager.StopAsync());
        Program.Throws<ObjectDisposedException>(() => manager.StartExport(fixture.Options));
        Program.Throws<ObjectDisposedException>(() => manager.AddToQueue(fixture.Options));
        Program.Check(!manager.GetSnapshot().HasUnfinished && fixture.Runner.StartedCount == 1 && fixture.Runner.MaxConcurrent == 1, "Shutdown started pending work or left a job active.");
        Program.Check(!Directory.EnumerateFiles(fixture.Directory, "*.partial*").Any(), "Shutdown left partial files.");
    }

    private static async Task Naming()
    {
        await using var fixture = new Fixture();
        File.WriteAllText(fixture.Options.OutputPath, "existing");
        var manager = fixture.Manager;
        var first = manager.AddToQueue(fixture.Options);
        var second = manager.AddToQueue(fixture.Options with { OutputPath = Path.Combine(fixture.Directory, ".", "clip.mp4"), Overwrite = true });
        var third = manager.AddToQueue(fixture.Options with { Overwrite = true });
        Program.Check(Path.GetFileName(Job(manager, first).Definition.Options.OutputPath) == "clip_2.mp4" &&
            Path.GetFileName(Job(manager, second).Definition.Options.OutputPath) == "clip.mp4" &&
            Path.GetFileName(Job(manager, third).Definition.Options.OutputPath) == "clip_3.mp4", "Unique names or explicit overwrite were incorrect.");
        var options = fixture.Options with { Tracks = [new(1, 0, true, 70)], CropEnabled = true, Start = .25, End = 2, CopyPlaybackSpeed = true, PlaybackSpeed = 2 };
        var captured = manager.AddToQueue(options);
        options = options with { Tracks = [new(2, 1, false, 10)], Start = 1, PlaybackSpeed = 1 };
        var job = Job(manager, captured);
        Program.Check(job.Definition.Options.Tracks[0].VolumePercent == 70 && job.Definition.Options.Start == .25 && job.Definition.Command.DurationSeconds == .875, "Captured options changed.");
        Program.Check(job.Definition.Command.Arguments[^1] == job.Definition.TemporaryPath && Path.GetExtension(job.Definition.TemporaryPath) == ".mp4", "Temporary output lost its container extension.");
        Program.Check(File.ReadAllText(fixture.Options.OutputPath) == "existing", "Queue submission overwrote a file.");
    }

    private static async Task Overwrite()
    {
        await using var fixture = new Fixture();
        var manager = fixture.Manager;
        var options = fixture.Options with { Overwrite = true };
        File.WriteAllText(options.OutputPath, "original");
        var first = manager.StartExport(options);
        var execution = await fixture.Runner.Next();
        Program.Check(File.ReadAllText(options.OutputPath) == "original", "Existing output changed before completion.");
        await manager.CancelCurrentAsync();
        Program.Check(File.ReadAllText(options.OutputPath) == "original" && !File.Exists(Job(manager, first).Definition.TemporaryPath), "Cancellation deleted the original output.");
        manager.Remove(first);
        var failed = manager.StartExport(options);
        execution = await fixture.Runner.Next();
        execution.Fail("simulated failure");
        await manager.WhenIdleAsync();
        Program.Check(File.ReadAllText(options.OutputPath) == "original" && !File.Exists(Job(manager, failed).Definition.TemporaryPath), "Failure deleted the original output.");
        manager.Remove(failed);
        manager.StartExport(options);
        execution = await fixture.Runner.Next();
        execution.Succeed();
        await manager.WhenIdleAsync();
        Program.Check(File.ReadAllText(options.OutputPath) == "exported", "Successful overwrite was not published.");
    }

    private static async Task Preflight()
    {
        await using var fixture = new Fixture();
        var manager = fixture.Manager;
        var first = manager.AddToQueue(fixture.Options);
        File.WriteAllText(Job(manager, first).Definition.Options.OutputPath, "external");
        var missingSource = Path.Combine(fixture.Directory, "missing.mkv");
        File.WriteAllText(missingSource, "media");
        var second = manager.AddToQueue(fixture.Options with { SourcePath = missingSource });
        File.Delete(missingSource);
        manager.StartQueue();
        await manager.WhenIdleAsync();
        Program.Check(manager.GetSnapshot().Jobs.All(job => job.State == ExportJobState.Failed) && fixture.Runner.StartedCount == 0, "Preflight failures invoked FFmpeg.");
        Program.Check(File.ReadAllText(Job(manager, first).Definition.Options.OutputPath) == "external", "A newly conflicting file was replaced.");
        Program.Check(Job(manager, second).Error!.Contains("source"), "Missing source did not have a useful error.");
        Program.Throws<FileNotFoundException>(() => manager.AddToQueue(fixture.Options with { SourcePath = missingSource }));
        Program.Throws<DirectoryNotFoundException>(() => manager.AddToQueue(fixture.Options with { OutputPath = Path.Combine(fixture.Directory, "absent", "out.mp4") }));
        Program.Throws<ArgumentException>(() => manager.AddToQueue(fixture.Options with { CropEnabled = true, Start = 1, End = 1 }));
    }

    private static async Task SourceProtection()
    {
        await using var fixture = new Fixture();
        var manager = fixture.Manager;
        Program.Throws<ArgumentException>(() => manager.AddToQueue(fixture.Options with { OutputPath = fixture.Options.SourcePath, Overwrite = true }));
        File.WriteAllText(fixture.Options.OutputPath, "original");
        var id = manager.StartExport(fixture.Options with { Overwrite = true });
        var execution = await fixture.Runner.Next();
        fixture.PlayerSource = fixture.Options.OutputPath;
        execution.Succeed();
        await manager.WhenIdleAsync();
        Program.Check(Job(manager, id).State == ExportJobState.Failed && File.ReadAllText(fixture.Options.OutputPath) == "original", "Export replaced a source opened during execution.");
        fixture.PlayerSource = null;
        var target = Path.Combine(fixture.Directory, "future.mp4");
        File.WriteAllText(target, "future-source");
        manager.AddToQueue(fixture.Options with { OutputPath = target, Overwrite = true });
        Program.Throws<ArgumentException>(() => manager.AddToQueue(fixture.Options with { SourcePath = target }));
    }

    private static async Task PublishFailure()
    {
        await using var fixture = new Fixture();
        var manager = fixture.Manager;
        var id = manager.StartExport(fixture.Options);
        var execution = await fixture.Runner.Next();
        File.WriteAllText(Job(manager, id).Definition.Options.OutputPath, "created while running");
        execution.Succeed();
        await manager.WhenIdleAsync();
        Program.Check(Job(manager, id).State == ExportJobState.Failed && !File.Exists(Job(manager, id).Definition.TemporaryPath), "Failed publication did not clean up.");
        Program.Check(File.ReadAllText(Job(manager, id).Definition.Options.OutputPath) == "created while running", "Publication ignored the overwrite policy.");
    }

    private static async Task RunnerFailure()
    {
        var calls = 0;
        var runner = new ControlledRunner();
        await using var fixture = new Fixture(async (exe, command, log, progress, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("simulated start exception");
            return await runner.Run(exe, command, log, progress, token);
        });
        var manager = fixture.Manager;
        var first = manager.AddToQueue(fixture.Options);
        var second = manager.AddToQueue(fixture.Options);
        manager.StartQueue();
        var execution = await runner.Next();
        CheckActive(manager, second);
        Program.Check(Job(manager, first).Error == "simulated start exception", "Runner exception did not become a job failure.");
        execution.Progress(double.NaN);
        Program.Check(double.IsFinite(Job(manager, second).Progress), "Invalid progress reached the UI.");
        execution.Succeed();
        await manager.WhenIdleAsync();
        execution.Progress(5);
        Program.Check(Job(manager, second).Progress == 100, "A stale callback changed a completed job.");
    }

    internal static ExportJobSnapshot Job(ExportManager manager, Guid id) => manager.GetSnapshot().Jobs.First(job => job.Id == id);
    private static async Task Aliases()
    {
        if (OperatingSystem.IsWindows()) return; // Creating symlinks can require administrator rights on Windows.
        await using var fixture = new Fixture();
        var alias = Path.Combine(Program.Temp, "alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(alias, fixture.Directory);
        try
        {
            Program.Throws<ArgumentException>(() => fixture.Manager.AddToQueue(fixture.Options with
            {
                OutputPath = Path.Combine(alias, Path.GetFileName(fixture.Options.SourcePath)), Overwrite = true
            }));
            var first = fixture.Manager.AddToQueue(fixture.Options);
            var second = fixture.Manager.AddToQueue(fixture.Options with { OutputPath = Path.Combine(alias, "clip.mp4"), Overwrite = true });
            Program.Check(Path.GetFileName(Job(fixture.Manager, first).Definition.Options.OutputPath) == "clip.mp4" &&
                Path.GetFileName(Job(fixture.Manager, second).Definition.Options.OutputPath) == "clip_2.mp4", "An aliased folder bypassed a reserved filename.");
        }
        finally { Directory.Delete(alias); }
    }

    private static void CheckActive(ExportManager manager, Guid id) => Program.Check(manager.GetSnapshot().Active?.Id == id, "The wrong queued clip started.");

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Program.Temp, Guid.NewGuid().ToString("N"));
        public ControlledRunner Runner { get; } = new();
        public ExportManager Manager { get; }
        public ExportOptions Options { get; }
        public string? PlayerSource;

        public Fixture(ExportManager.RunExport? run = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var source = Path.Combine(Directory, "source Ω.mkv");
            File.WriteAllText(source, "fake source for scheduler tests");
            Options = Program.Options() with { SourcePath = source, OutputPath = Path.Combine(Directory, "clip.mp4") };
            Manager = new(run ?? Runner.Run, () => "fake-ffmpeg", () => PlayerSource);
        }

        public async ValueTask DisposeAsync() => await Manager.StopAsync();
    }

    internal sealed class ControlledRunner
    {
        public sealed record Execution(ExportCommand Command, CancellationToken Token, Action<double> Progress, Action<string> Log)
        {
            public TaskCompletionSource<FfmpegResult> Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Succeed() => Finish.TrySetResult(new(0, false));
            public void Fail(string error) => Finish.TrySetResult(new(1, false, error));
        }

        private readonly Channel<Execution> _started = Channel.CreateUnbounded<Execution>();
        private int _count;
        private int _concurrent;
        private int _maxConcurrent;
        public int StartedCount => Volatile.Read(ref _count);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);
        public bool IgnoreCancellation;

        public async Task<FfmpegResult> Run(string executable, ExportCommand command,
            Action<string> log, Action<double> progress, CancellationToken token)
        {
            var concurrent = Interlocked.Increment(ref _concurrent);
            Interlocked.Increment(ref _count);
            Interlocked.Exchange(ref _maxConcurrent, Math.Max(concurrent, MaxConcurrent));
            var execution = new Execution(command, token, progress, log);
            try
            {
                await File.WriteAllTextAsync(command.Arguments[^1], "exported", CancellationToken.None);
                log("simulated FFmpeg started");
                progress(25);
                await _started.Writer.WriteAsync(execution, CancellationToken.None);
                return IgnoreCancellation ? await execution.Finish.Task : await execution.Finish.Task.WaitAsync(token);
            }
            catch (OperationCanceledException) { return new(null, true); }
            finally { Interlocked.Decrement(ref _concurrent); }
        }

        public async Task<Execution> Next() => await _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
