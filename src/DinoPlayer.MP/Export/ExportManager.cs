using System.Collections.Immutable;

namespace DinoPlayer.MP.Export;

internal enum ExportJobState { Waiting, Scheduled, Exporting, Cancelling, Completed, Failed, Cancelled }

internal sealed record ExportJobDefinition(Guid Id, ExportOptions Options, ExportCommand Command, string TemporaryPath);

internal sealed record ExportJobSnapshot(ExportJobDefinition Definition, ExportJobState State,
    double Progress, string? Error, string Log, bool IsQueueJob)
{
    public Guid Id => Definition.Id;
    public bool IsPending => State is ExportJobState.Waiting or ExportJobState.Scheduled;
    public bool IsActive => State is ExportJobState.Exporting or ExportJobState.Cancelling;
    public bool IsFinished => State is ExportJobState.Completed or ExportJobState.Failed or ExportJobState.Cancelled;
}

internal sealed record ExportManagerSnapshot(ImmutableArray<ExportJobSnapshot> Jobs, bool QueueStarted,
    bool QueuePaused, bool IsStopping)
{
    public ExportJobSnapshot? Active => Jobs.FirstOrDefault(job => job.IsActive);
    public int WaitingCount => Jobs.Count(job => job.IsPending);
    public int FailedCount => Jobs.Count(job => job.State == ExportJobState.Failed);
    public bool HasUnfinished => Jobs.Any(job => job.IsPending || job.IsActive);
    public bool IsRunning => Active is not null || Jobs.Any(job => job.State == ExportJobState.Scheduled && (!job.IsQueueJob || !QueuePaused));
}

// The manager owns processes and job state, independently of the visible tab.
// Waiting jobs are deliberately separate from jobs scheduled for the active run.
internal sealed class ExportManager
{
    internal delegate Task<FfmpegResult> RunExport(string executable, ExportCommand command,
        Action<string> log, Action<double> progress, CancellationToken cancellationToken);

    private sealed class Job(ExportJobDefinition definition)
    {
        public ExportJobDefinition Definition { get; } = definition;
        public ExportJobState State = ExportJobState.Waiting;
        public bool QueueJob;
        public double Progress;
        public string? Error;
        public readonly BoundedLogBuffer Log = new();
        public string LogText = "";
        public CancellationTokenSource? Cancellation;
        public TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExportJobSnapshot Snapshot()
        {
            LogText = Log.TakeSnapshot() ?? LogText;
            return new(Definition, State, Progress, Error, LogText, QueueJob);
        }
    }

    private readonly object _gate = new();
    private readonly List<Job> _jobs = [];
    private readonly RunExport _run;
    private readonly Func<string> _resolveExecutable;
    private readonly Func<string?> _playerSource;
    private Job? _active;
    private Task? _pumpTask;
    private bool _queueStarted;
    private bool _queuePaused;
    private bool _cancelAll;
    private bool _shutdown;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public ExportManager(RunExport? run = null, Func<string>? resolveExecutable = null, Func<string?>? playerSource = null)
    {
        _run = run ?? new FfmpegRunner().RunAsync;
        _resolveExecutable = resolveExecutable ?? FfmpegRunner.ResolveExecutable;
        _playerSource = playerSource ?? (() => null);
    }

    public ExportManagerSnapshot GetSnapshot()
    {
        lock (_gate)
            return new(_jobs.Select(job => job.Snapshot()).ToImmutableArray(), _queueStarted, _queuePaused, _shutdown || _cancelAll);
    }

    public Guid AddToQueue(ExportOptions options) => Submit(options, start: false);
    public Guid StartExport(ExportOptions options) => Submit(options, start: true);

    private Guid Submit(ExportOptions options, bool start)
    {
        ExportCommandBuilder.Validate(options);
        var source = Path.GetFullPath(options.SourcePath);
        var requested = Path.GetFullPath(options.OutputPath.Trim());
        lock (_gate)
        {
            EnsureAccepting();
            ValidateSource(source);
            ValidateDestination(requested, source);
            // A new source must not be a destination still awaiting publication.
            if (_jobs.Any(job => IsUnfinished(job) && PathsEqual(job.Definition.Options.OutputPath, source)))
                throw new ArgumentException("This source is the destination of an unfinished export. Choose another output location for that export first.");
            var output = AllocateDestination(requested, options.Overwrite);
            var id = Guid.NewGuid();
            var temporary = Path.Combine(Path.GetDirectoryName(output)!,
                $".{Path.GetFileNameWithoutExtension(output)}.{id:N}.partial{Path.GetExtension(output)}");
            var frozen = options with { SourcePath = source, OutputPath = output, Tracks = options.Tracks.ToImmutableArray() };
            var command = ExportCommandBuilder.Build(frozen with { OutputPath = temporary, Overwrite = false });
            command = command with { Arguments = command.Arguments.ToImmutableArray() };
            var job = new Job(new(id, frozen, command, temporary));
            if (start)
            {
                job.State = ExportJobState.Scheduled;
                // An immediate export can bypass an idle, paused queue, but never
                // unpauses a queue whose current clip is still finishing.
                job.QueueJob = _active?.QueueJob == true || (_queueStarted && !_queuePaused);
            }
            else if (_queueStarted)
            {
                job.State = ExportJobState.Scheduled;
                job.QueueJob = true;
            }
            _jobs.Add(job);
            EnsurePump();
            return id;
        }
    }

    public void StartQueue()
    {
        lock (_gate)
        {
            EnsureAccepting();
            _queueStarted = true;
            _queuePaused = false;
            foreach (var job in _jobs.Where(IsUnfinished))
            {
                job.QueueJob = true;
                if (job.State == ExportJobState.Waiting) job.State = ExportJobState.Scheduled;
            }
            EnsurePump();
            ResetQueueIfFinished();
        }
    }

    public void PauseAfterCurrent()
    {
        lock (_gate)
        {
            EnsureAccepting();
            if (_queueStarted) _queuePaused = true;
        }
    }

    public void ResumeQueue() => StartQueue();

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            EnsureAccepting();
            var job = _jobs.FirstOrDefault(item => item.Definition.Id == id);
            if (job is null || ReferenceEquals(job, _active)) return false;
            _jobs.Remove(job);
            job.Log.Stop();
            job.Completion.TrySetResult();
            ResetQueueIfFinished();
            return true;
        }
    }

    public bool Move(Guid id, int direction)
    {
        lock (_gate)
        {
            EnsureAccepting();
            var pending = _jobs.Where(job => job.State is ExportJobState.Waiting or ExportJobState.Scheduled).ToList();
            var index = pending.FindIndex(job => job.Definition.Id == id);
            var target = index + Math.Sign(direction);
            if (index < 0 || target < 0 || target >= pending.Count || index == target) return false;
            var from = _jobs.IndexOf(pending[index]);
            var to = _jobs.IndexOf(pending[target]);
            (_jobs[from], _jobs[to]) = (_jobs[to], _jobs[from]);
            return true;
        }
    }

    public bool Retry(Guid id)
    {
        lock (_gate)
        {
            EnsureAccepting();
            var job = _jobs.FirstOrDefault(item => item.Definition.Id == id);
            if (job is null || ReferenceEquals(job, _active) || job.State is not (ExportJobState.Failed or ExportJobState.Cancelled)) return false;
            job.State = _queueStarted ? ExportJobState.Scheduled : ExportJobState.Waiting;
            job.QueueJob = _queueStarted;
            job.Progress = 0;
            job.Error = null;
            job.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _jobs.Remove(job);
            _jobs.Add(job);
            EnsurePump();
            return true;
        }
    }

    public void ClearFinished()
    {
        lock (_gate)
        {
            EnsureAccepting();
            foreach (var job in _jobs.Where(job => !ReferenceEquals(job, _active) && !IsUnfinished(job)).ToArray())
            {
                _jobs.Remove(job);
                job.Log.Stop();
            }
        }
    }

    public Task CancelCurrentAsync()
    {
        CancellationTokenSource? cancellation;
        Task completion;
        lock (_gate)
        {
            if (_active is null) return Task.CompletedTask;
            if (_active.State is ExportJobState.Exporting or ExportJobState.Cancelling) _active.State = ExportJobState.Cancelling;
            cancellation = _active.Cancellation;
            completion = _active.Completion.Task;
        }
        Cancel(cancellation);
        return completion;
    }

    public Task CancelAllAsync()
    {
        CancellationTokenSource? cancellation;
        Task completion;
        lock (_gate)
        {
            _cancelAll = _pumpTask is not null;
            _queueStarted = false;
            _queuePaused = false;
            foreach (var job in _jobs.Where(job => job.State is ExportJobState.Waiting or ExportJobState.Scheduled))
            {
                job.State = ExportJobState.Cancelled;
                job.Completion.TrySetResult();
            }
            if (_active?.State is ExportJobState.Exporting or ExportJobState.Cancelling) _active.State = ExportJobState.Cancelling;
            cancellation = _active?.Cancellation;
            completion = _pumpTask ?? Task.CompletedTask;
        }
        Cancel(cancellation);
        return completion;
    }

    public Task StopAsync()
    {
        lock (_gate) _shutdown = true;
        return CancelAllAsync();
    }

    public Task WhenIdleAsync()
    {
        lock (_gate) return _pumpTask ?? Task.CompletedTask;
    }

    private void EnsurePump()
    {
        if (_pumpTask is null && !_cancelAll && !_shutdown && _jobs.Any(CanRun))
            _pumpTask = Task.Run(RunLoopAsync);
    }

    private bool CanRun(Job job) => job.State == ExportJobState.Scheduled && (!job.QueueJob || !_queuePaused);
    private static bool IsUnfinished(Job job) => job.State is ExportJobState.Waiting or ExportJobState.Scheduled or ExportJobState.Exporting or ExportJobState.Cancelling;

    private async Task RunLoopAsync()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                var next = _shutdown || _cancelAll ? null : _jobs.FirstOrDefault(CanRun);
                if (next is null)
                {
                    _active = null;
                    _pumpTask = null;
                    _cancelAll = false;
                    ResetQueueIfFinished();
                    return;
                }
                job = next;
                _active = job;
                job.State = ExportJobState.Exporting;
                job.Cancellation = new();
                job.Log.Clear();
                job.Log.Append($"Destination: {job.Definition.Options.OutputPath}");
                job.Log.Append($"ffmpeg {string.Join(" ", job.Definition.Command.Arguments.Select(QuoteForLog))}");
                if (job.Definition.Command.Warning is { } warning) job.Log.Append(warning);
            }
            await ExecuteAsync(job).ConfigureAwait(false);
            lock (_gate)
            {
                job.Cancellation.Dispose();
                job.Cancellation = null;
                _active = null;
                job.Completion.TrySetResult();
                ResetQueueIfFinished();
            }
        }
    }

    private async Task ExecuteAsync(Job job)
    {
        var definition = job.Definition;
        try
        {
            lock (_gate)
            {
                ValidateSource(definition.Options.SourcePath);
                ValidateDestination(definition.Options.OutputPath, definition.Options.SourcePath);
                if (!definition.Options.Overwrite && File.Exists(definition.Options.OutputPath))
                    throw new IOException("The destination was created after this clip was queued. Remove the clip and add it again to choose a new filename.");
            }
            var cancellation = job.Cancellation!;
            var result = await _run(_resolveExecutable(), definition.Command,
                line => { lock (_gate) if (ReferenceEquals(job.Cancellation, cancellation)) job.Log.Append(line); },
                value =>
                {
                    lock (_gate)
                        if (ReferenceEquals(job.Cancellation, cancellation) && job.State == ExportJobState.Exporting && double.IsFinite(value))
                            job.Progress = Math.Clamp(value, 0, 100);
                }, cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (job.State == ExportJobState.Cancelling || result.Cancelled)
                    job.State = ExportJobState.Cancelled;
                else if (result.Error is not null || result.ExitCode != 0)
                    Fail(job, result.Error ?? $"FFmpeg failed with exit code {result.ExitCode}.");
                else
                {
                    // Recheck the live player source before replacing an existing
                    // file: the user may have opened the destination during export.
                    ValidateDestination(definition.Options.OutputPath, definition.Options.SourcePath);
                    File.Move(definition.TemporaryPath, definition.Options.OutputPath, definition.Options.Overwrite);
                    job.Progress = 100;
                    job.State = ExportJobState.Completed;
                    job.Log.Append("Export complete.");
                }
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (job.State == ExportJobState.Cancelling) job.State = ExportJobState.Cancelled;
                else Fail(job, exception.Message);
            }
        }
        finally
        {
            try { File.Delete(definition.TemporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                job.Log.Append($"Could not remove incomplete output {definition.TemporaryPath}: {exception.Message}");
                AppLog.Warn($"Export temporary-file cleanup failed: {exception.Message}");
            }
        }
    }

    private static void Fail(Job job, string error)
    {
        job.State = ExportJobState.Failed;
        job.Error = error;
        job.Log.Append(error);
    }

    private void ResetQueueIfFinished()
    {
        if (!_jobs.Any(job => job.QueueJob && IsUnfinished(job)))
        {
            _queueStarted = false;
            _queuePaused = false;
        }
    }

    private void EnsureAccepting()
    {
        if (_shutdown) throw new ObjectDisposedException(nameof(ExportManager));
        if (_cancelAll) throw new InvalidOperationException("Wait for export cancellation to finish before adding more clips.");
    }

    private static void ValidateSource(string source)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("The source video is no longer available.", source);
    }

    private void ValidateDestination(string output, string source)
    {
        if (!Directory.Exists(Path.GetDirectoryName(output)))
            throw new DirectoryNotFoundException("The output folder does not exist.");
        if (Directory.Exists(output)) throw new ArgumentException("Choose a video filename, not a folder.");
        if (PathsEqual(output, source) ||
            _jobs.Any(job => PathsEqual(output, job.Definition.Options.SourcePath)) ||
            _playerSource() is { Length: > 0 } playerSource && PathsEqual(output, playerSource))
            throw new ArgumentException("The output must not replace a source video. Choose a different filename.");
    }

    private string AllocateDestination(string requested, bool overwrite)
    {
        var output = requested;
        var number = 2;
        while (_jobs.Any(job => PathsEqual(job.Definition.Options.OutputPath, output)) ||
               _jobs.Any(job => PathsEqual(job.Definition.Options.SourcePath, output)) ||
               _playerSource() is { Length: > 0 } playerSource && PathsEqual(output, playerSource) ||
               Directory.Exists(output) || (!overwrite && File.Exists(output)))
        {
            output = Path.Combine(Path.GetDirectoryName(requested)!,
                $"{Path.GetFileNameWithoutExtension(requested)}_{number++}{Path.GetExtension(requested)}");
        }
        return output;
    }

    private static bool PathsEqual(string first, string second) => PathComparer.Equals(PathIdentity(first), PathIdentity(second));

    private static string PathIdentity(string path)
    {
        // FullPath alone misses directory aliases, allowing an overwrite of the
        // source through a symlinked folder or a Windows directory junction.
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null) current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return current;
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static string QuoteForLog(string value) =>
        value.IndexOfAny([' ', '\t', '"']) >= 0 ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}
