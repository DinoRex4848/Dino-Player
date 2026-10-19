using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DinoPlayer.MP.Playback;

public sealed class MpvPlaybackEngine : IPlaybackEngine
{
    private const int MpvFormatFlag = 3;
    private const int MpvFormatInt64 = 4;
    private const int MpvFormatDouble = 5;
    private const int MpvEventShutdown = 1;
    private const int MpvEventLogMessage = 2;
    private const int MpvEventEndFile = 7;
    private const int MpvEventFileLoaded = 8;
    private const int MpvEventVideoReconfig = 17;
    private const int MpvEventSeek = 20;
    private const int MpvEventPlaybackRestart = 21;
    private const int MpvEventClientMessage = 16;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDoubleClickTime();

    private readonly IntPtr _context;
    private readonly PlaybackWorker _worker;
    private readonly object _pendingOperationSync = new();
    private int _disposed;
    private IReadOnlyList<AudioTrack> _audioTracks = Array.Empty<AudioTrack>();
    private readonly string _windowId;
    private readonly string? _requiredGpuContext;
    private string? _lastOutputSignature;
    private long _mediaGeneration;
    private long _activeGeneration;
    private readonly Dictionary<long, long> _entryGenerations = new();
    private PlaybackSnapshot _snapshot = PlaybackSnapshot.Empty;
    private long _lastPositionSample = Stopwatch.GetTimestamp();
    private bool _mediaLoaded;
    private bool _videoReady;
    private bool _hardwareDecodeAttempted;
    private bool _hardwareDecodeFallback;
    private AudioMixRequest? _pendingAudioMix;
    private bool _audioMixOperationQueued;
    private SeekCompletion? _pendingSeekCompletion;

    public MpvPlaybackEngine(IntPtr windowHandle, string handleDescriptor) : this(windowHandle, handleDescriptor, false) { }

    // Null output is used by native integration tests; the public constructor
    // always enforces embedded HWND/X11 output.
    internal MpvPlaybackEngine(IntPtr windowHandle, string handleDescriptor, bool headless)
    {
        MpvNative.EnsureLoaded();
        NativeLibraryDescription = MpvNative.LoadedLibraryDescription;

        if (!headless && windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Avalonia supplied a null native video handle ({handleDescriptor}). Refusing to start mpv outside the application window.");
        }

        var isWindowsHandle = OperatingSystem.IsWindows() &&
                              handleDescriptor.Contains("HWND", StringComparison.OrdinalIgnoreCase);
        var isX11Handle = OperatingSystem.IsLinux() &&
                          (handleDescriptor.Contains("XID", StringComparison.OrdinalIgnoreCase) ||
                           handleDescriptor.Contains("X11", StringComparison.OrdinalIgnoreCase));
        if (!headless && !isWindowsHandle && !isX11Handle)
        {
            throw new PlatformNotSupportedException(
                $"Unsupported native video handle '{handleDescriptor}'. Expected HWND on Windows or X11/XID on Linux; refusing external-window fallback.");
        }

        _windowId = FormatWindowId(windowHandle, handleDescriptor);
        _requiredGpuContext = isX11Handle ? "x11egl" : null;

        var apiVersion = MpvNative.mpv_client_api_version();
        ClientApiVersion = $"{apiVersion >> 16}.{apiVersion & 0xffff}";

        _context = MpvNative.mpv_create();
        if (_context == IntPtr.Zero)
        {
            throw new InvalidOperationException("mpv_create returned a null context.");
        }

        try
        {
            if (!headless) SetOptionNative("wid", _windowId);
            else SetOptionNative("ao", "null");
            SetOptionNative("config", "no");
            SetOptionNative("terminal", "no");
            SetOptionNative("input-default-bindings", "no");
            SetOptionNative("input-vo-keyboard", "no");
            if (isWindowsHandle)
            {
                var systemDoubleClickTime = GetDoubleClickTime();
                var doubleClickTime = systemDoubleClickTime == 0
                    ? 500u
                    : Math.Clamp(systemDoubleClickTime, 1u, 1000u);
                SetOptionNative("input-doubleclick-time", doubleClickTime.ToString(CultureInfo.InvariantCulture));
                AppLog.Info($"Windows double-click interval: system={systemDoubleClickTime} ms; libmpv={doubleClickTime} ms");
            }

            SetOptionNative("cursor-autohide", "no");
            SetOptionNative("idle", "yes");
            SetOptionNative("keep-open", "yes");
            SetOptionNative("start", "0");
            SetOptionNative("resume-playback", "no");
            SetOptionNative("audio-display", "no");
            SetOptionNative("aid", "no");
            SetOptionNative("audio-pitch-correction", "yes");
            SetOptionNative("hwdec", headless ? "no" : "auto-safe");
            SetOptionNative("vo", headless ? "null" : "gpu-next");
            if (_requiredGpuContext is not null)
            {
                SetOptionNative("gpu-api", "opengl");
                SetOptionNative("gpu-context", _requiredGpuContext);
            }

            AppLog.Info($"Initializing embedded libmpv: host-handle={handleDescriptor} {windowHandle}; supplied-wid={_windowId}; " +
                        $"requested-vo=gpu-next; requested-gpu-context={_requiredGpuContext ?? "auto (platform native)"}");
            Check(MpvNative.mpv_initialize(_context), "mpv_initialize");
            Check(MpvNative.mpv_request_event(_context, MpvEventClientMessage, 1), "mpv_request_event(client-message)");
            Check(MpvNative.mpv_request_log_messages(_context, "info"), "mpv_request_log_messages");
            LoadInputBridge();
            ObserveProperties();
        }
        catch
        {
            MpvNative.mpv_terminate_destroy(_context);
            throw;
        }

        AppLog.Info($"libmpv version: {GetStringNative("mpv-version") ?? "unknown"}; client API: {ClientApiVersion}; " +
                    $"native handle: {handleDescriptor} {windowHandle}; wid={_windowId}");
        AppLog.Info($"Actual application display backend: {DescribeBackend(handleDescriptor)}");

        _worker = new PlaybackWorker(PollEvent, () => MpvNative.mpv_wakeup(_context),
            () =>
            {
                CompletePendingAudioMix(new ObjectDisposedException(nameof(MpvPlaybackEngine)));
                CompletePendingSeek(new ObjectDisposedException(nameof(MpvPlaybackEngine)));
                MpvNative.mpv_terminate_destroy(_context);
                Volatile.Write(ref _snapshot, PlaybackSnapshot.Empty with { Generation = Generation });
                AppLog.Info("libmpv stopped and native resources were released.");
            }, exception =>
            {
                AppLog.Error($"libmpv event loop failed: {exception}");
                ReportError(exception.Message);
            });
        _worker.Start();
    }

    public event Action<long>? MediaLoaded;
    public event Action<long>? MediaEnded;
    public event Action<long>? PositionChanged;
    public event Action<PlaybackError>? Error;
    public event Action<PlaybackPointerEvent>? PointerInput;

    public long Generation => Volatile.Read(ref _mediaGeneration);
    public IReadOnlyList<AudioTrack> AudioTracks => Volatile.Read(ref _audioTracks);
    public string NativeLibraryDescription { get; }
    public string ClientApiVersion { get; }

    public Task OpenAsync(string path, bool startPaused = false)
    {
        ThrowIfDisposed();
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected media file does not exist.", fullPath);
        }

        var generation = Interlocked.Increment(ref _mediaGeneration);
        Volatile.Write(ref _snapshot, PlaybackSnapshot.Empty with { Generation = generation });
        AppLog.Info($"Opening media generation {generation}: {fullPath}");
        return InvokeOnMpvThreadAsync(() =>
        {
            if (generation != Generation) return 0;
            Volatile.Write(ref _audioTracks, Array.Empty<AudioTrack>());
            _mediaLoaded = false;
            _videoReady = false;
            _hardwareDecodeAttempted = false;
            _hardwareDecodeFallback = false;
            _lastOutputSignature = null;
            CompletePendingAudioMix();
            CompletePendingSeek(new OperationCanceledException("Media changed while preparing playback."));
            if (startPaused) SetPausedNative(true);
            SetPropertyNative("speed", "1");
            SetPropertyNative("lavfi-complex", string.Empty);
            SetPropertyNative("aid", "no");
            CommandNative("loadfile", fullPath, "replace");
            var entry = GetInt64Native("playlist/0/id", -1);
            if (entry >= 0) _entryGenerations[entry] = generation;
            return 0;
        });
    }

    public Task TogglePauseAsync()
    {
        return InvokeOnMpvThreadAsync(() =>
        {
            SetPausedNative(!GetFlagNative("pause", fallback: false));
            return 0;
        });
    }

    public Task SetPausedAsync(bool paused)
    {
        ThrowIfDisposed();
        return InvokeOnMpvThreadAsync(() =>
        {
            SetPausedNative(paused);
            return 0;
        });
    }

    public Task AdjustPlaybackSpeedAsync(int direction)
    {
        ThrowIfDisposed();
        if (direction == 0)
            return Task.CompletedTask;

        return InvokeOnMpvThreadAsync(() =>
        {
            var current = Math.Clamp(GetDoubleNative("speed", 1), 0.25, 3);
            var next = direction > 0
                ? current < 0.3 ? 0.3 : Math.Min(3, Math.Round(current + 0.1, 2))
                : current <= 0.3 ? 0.25 : Math.Max(0.25, Math.Round(current - 0.1, 2));
            SetPropertyNative("speed", next.ToString("0.##", CultureInfo.InvariantCulture));
            AppLog.Info($"Playback speed changed: {current:0.##}x -> {next:0.##}x.");
            return 0;
        });
    }

    public Task SeekAsync(double seconds)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        return QueueSeek(Math.Max(0, seconds), "absolute+exact");
    }

    public Task SeekRelativeAsync(double deltaSeconds)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(deltaSeconds)) throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        return deltaSeconds == 0 ? Task.CompletedTask : QueueSeek(deltaSeconds, "relative+keyframes");
    }

    public Task SeekAndWaitAsync(double seconds)
    {
        ThrowIfDisposed();
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        return SeekAndWaitCoreAsync(new SeekCompletion(Generation, Math.Max(0, seconds),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)));
    }

    private async Task SeekAndWaitCoreAsync(SeekCompletion request)
    {
        try
        {
            await InvokeOnMpvThreadAsync(() =>
            {
                if (request.Generation != Generation) throw new OperationCanceledException("Media changed while preparing playback.");
                if (!_mediaLoaded || _activeGeneration != request.Generation)
                    throw new InvalidOperationException("Media must be loaded before preparing playback.");
                lock (_pendingOperationSync)
                {
                    _pendingSeekCompletion?.Completion.TrySetCanceled();
                    _pendingSeekCompletion = request;
                }
                CommandNative("seek", request.Seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
                return 0;
            }).ConfigureAwait(false);
            await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        finally
        {
            lock (_pendingOperationSync)
                if (ReferenceEquals(_pendingSeekCompletion, request)) _pendingSeekCompletion = null;
        }
    }

    private void CompletePendingSeek(Exception? error = null)
    {
        lock (_pendingOperationSync)
        {
            var pending = _pendingSeekCompletion;
            _pendingSeekCompletion = null;
            if (error is null) pending?.Completion.TrySetResult();
            else pending?.Completion.TrySetException(error);
        }
    }

    private void CompleteReadySeek()
    {
        SeekCompletion? pending;
        lock (_pendingOperationSync) pending = _pendingSeekCompletion;
        if (pending is null || pending.Generation != Generation || GetFlagNative("seeking", true)) return;
        var fps = GetDoubleNative("estimated-vf-fps", 0);
        var tolerance = fps > 0 ? Math.Max(.1, 1 / fps + .01) : .25;
        if (Math.Abs(GetSnapshot().PositionSeconds - pending.Seconds) <= tolerance) CompletePendingSeek();
    }

    private Task QueueSeek(double seconds, string flags)
    {
        var generation = Generation;
        // Keep every seek in worker order. mpv combines pending relative seeks
        // itself, using its current seek target rather than the sampled UI time.
        return QueueNativeOperation("seek", () =>
        {
            if (generation != Generation || _activeGeneration != generation || !_mediaLoaded) return;
            CommandNative("seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), flags);
            AppLog.Info($"Seek requested: generation={generation}; seconds={seconds:0.###}; flags={flags}");
        });
    }

    public Task SetMasterVolumeAsync(double volumePercent)
    {
        ThrowIfDisposed();
        var volume = Math.Clamp(volumePercent, 0, 150);
        return InvokeOnMpvThreadAsync(() =>
        {
            SetPropertyNative("volume", volume.ToString("0.###", CultureInfo.InvariantCulture));
            return 0;
        });
    }

    public Task SetCursorAutohideAsync(bool enabled)
    {
        ThrowIfDisposed();
        return InvokeOnMpvThreadAsync(() =>
        {
            SetPropertyNative("cursor-autohide", enabled ? "4000" : "no");
            return 0;
        });
    }

    public Task SetLoopingAsync(bool enabled)
    {
        ThrowIfDisposed();
        return InvokeOnMpvThreadAsync(() =>
        {
            SetPropertyNative("loop-file", enabled ? "inf" : "no");
            AppLog.Info("Looping " + (enabled ? "enabled." : "disabled."));
            return 0;
        });
    }

    public Task ApplyAudioMixAsync()
    {
        ThrowIfDisposed();

        // The UI owns the mutable AudioTrack instances. Copy their values before
        // queueing work so the mpv thread never observes a partially edited model.
        var tracks = _audioTracks.Select(track => new AudioTrack
        {
            Id = track.Id,
            SourceIndex = track.SourceIndex,
            AudioOrdinal = track.AudioOrdinal,
            Label = track.Label,
            Enabled = track.Enabled,
            VolumePercent = track.VolumePercent
        }).ToArray();
        var graph = AudioMixGraph.Build(tracks);
        var enabled = tracks.Where(track => track.Enabled)
            .Select(track => $"aid={track.Id}@{track.VolumePercent:0}%")
            .ToArray();
        lock (_pendingOperationSync)
        {
            // Shutdown may have started while the track values were copied.
            ThrowIfDisposed();
            var generation = Generation;
            var completion = _pendingAudioMix?.Generation == generation
                ? _pendingAudioMix.Completion
                : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_pendingAudioMix?.Generation != generation) _pendingAudioMix?.Completion.TrySetResult();
            _pendingAudioMix = new AudioMixRequest(generation, graph,
                enabled.Length == 0 ? "all muted" : string.Join(", ", enabled), completion);
            if (!_audioMixOperationQueued)
            {
                _audioMixOperationQueued = true;
                _ = QueueNativeOperation("audio mix", () => ExecutePendingAudioMix("queued"));
            }
            // Deferred requests complete only after the graph is applied. This
            // lets imports remain paused until their audio setup is ready.
            return completion.Task;
        }
    }

    public PlaybackSnapshot GetSnapshot() => Volatile.Read(ref _snapshot);

    private PlaybackSnapshot GetSnapshotNative() => new(
        GetDoubleNative("time-pos", 0),
        GetDoubleNative("duration", 0),
        Math.Clamp(GetDoubleNative("speed", 1), 0.25, 3),
        GetFlagNative("pause", true),
        GetStringNative("current-vo") ?? GetStringNative("vo-configured") ?? "unknown",
        GetStringNative("current-gpu-context") ?? "unknown",
        GetStringNative("hwdec-current") ?? "none",
        GetInt64Native("decoder-frame-drop-count", 0),
        GetInt64Native("frame-drop-count", 0)) { Generation = _activeGeneration, IsLoaded = _mediaLoaded };

    private void PollEvent()
    {
        var pointer = MpvNative.mpv_wait_event(_context, _worker.HasPendingOperations ? 0 : 0.25);
        if (pointer != IntPtr.Zero) HandleEvent(Marshal.PtrToStructure<MpvNative.MpvEvent>(pointer));
        // time-pos can change every video frame. Sample it at the UI cadence on
        // the native worker instead of sending a managed event per frame.
        if (_mediaLoaded && _activeGeneration == Generation &&
            Stopwatch.GetElapsedTime(_lastPositionSample) >= TimeSpan.FromMilliseconds(200))
        {
            RefreshPosition();
        }
    }

    private void RefreshPosition(bool notify = false)
    {
        if (!_mediaLoaded || _activeGeneration != Generation) return;
        _lastPositionSample = Stopwatch.GetTimestamp();
        var current = GetSnapshot();
        var position = GetDoubleNative("time-pos", current.PositionSeconds);
        Volatile.Write(ref _snapshot, current with
        {
            PositionSeconds = double.IsFinite(position) ? Math.Max(0, position) : current.PositionSeconds
        });
        if (!notify) return;
        try { PositionChanged?.Invoke(_activeGeneration); }
        catch (Exception exception) { AppLog.Error($"Playback position handler failed: {exception}"); }
    }

    private void ObserveProperties()
    {
        var properties = new (string Name, int Format)[]
        {
            ("time-pos", MpvFormatDouble), ("duration", MpvFormatDouble), ("speed", MpvFormatDouble),
            ("pause", MpvFormatFlag), ("current-vo", 1), ("current-gpu-context", 1), ("hwdec-current", 1),
            ("decoder-frame-drop-count", MpvFormatInt64), ("frame-drop-count", MpvFormatInt64)
        };
        for (var i = 1; i < properties.Length; i++)
            Check(MpvNative.mpv_observe_property(_context, (ulong)i + 1, properties[i].Name, properties[i].Format),
                $"observe {properties[i].Name}");
    }

    private void UpdateObservedProperty(MpvNative.MpvEvent ev)
    {
        if (!_mediaLoaded || _activeGeneration != Generation || ev.Data == IntPtr.Zero) return;
        var property = Marshal.PtrToStructure<MpvNative.MpvEventProperty>(ev.Data);
        var available = property.Format != 0 && property.Data != IntPtr.Zero;
        var number = available && property.Format == MpvFormatDouble ? Marshal.PtrToStructure<double>(property.Data) : 0;
        if (!double.IsFinite(number)) number = 0;
        var integer = available && property.Format == MpvFormatInt64 ? Marshal.ReadInt64(property.Data) : 0;
        var flag = !available || property.Format != MpvFormatFlag || Marshal.ReadInt32(property.Data) != 0;
        var text = available && property.Format == 1 ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(property.Data)) : null;
        var snapshot = GetSnapshot();
        snapshot = ev.ReplyUserData switch
        {
            1 => snapshot with { PositionSeconds = Math.Max(0, number) },
            2 => snapshot with { DurationSeconds = Math.Max(0, number) },
            3 => snapshot with { PlaybackSpeed = available ? Math.Clamp(number, 0.25, 3) : 1 },
            4 => snapshot with { IsPaused = flag },
            5 => snapshot with { VideoOutput = text ?? "unknown" },
            6 => snapshot with { GpuContext = text ?? "unknown" },
            7 => snapshot with { HardwareDecoder = text ?? "none" },
            8 => snapshot with { DecoderDroppedFrames = integer },
            9 => snapshot with { OutputDroppedFrames = integer },
            _ => snapshot
        };
        Volatile.Write(ref _snapshot, snapshot);
    }

    private void HandleEvent(MpvNative.MpvEvent mpvEvent)
    {
        switch (mpvEvent.EventId)
        {
            case 0:
                break;
            case MpvEventShutdown:
                AppLog.Info("libmpv sent shutdown event.");
                _ = _worker.StopAsync();
                break;
            case MpvEventLogMessage:
                WriteMpvLog(mpvEvent.Data);
                break;
            case MpvEventClientMessage:
                HandleClientMessage(mpvEvent.Data);
                break;
            case 6: // MPV_EVENT_START_FILE: associate queued native events with their load request.
                var entry = mpvEvent.Data == IntPtr.Zero ? -1 : Marshal.ReadInt64(mpvEvent.Data);
                _activeGeneration = _entryGenerations.GetValueOrDefault(entry, -1);
                break;
            case 22: // MPV_EVENT_PROPERTY_CHANGE
                UpdateObservedProperty(mpvEvent);
                break;
            case MpvEventFileLoaded:
                if (_activeGeneration == Generation) OnFileLoaded();
                break;
            case MpvEventEndFile:
                HandleEndFile(mpvEvent);
                break;
            case MpvEventVideoReconfig:
                if (_activeGeneration != Generation) break;
                _videoReady = true;
                LogAndValidateOutput("video-reconfig");
                TryApplyPendingAudioMix("video-reconfig");
                break;
            case MpvEventSeek:
                RefreshPosition(notify: true);
                break;
            case MpvEventPlaybackRestart:
                if (_activeGeneration != Generation) break;
                _videoReady = true;
                LogAndValidateOutput("playback-restart");
                TryApplyPendingAudioMix("playback-restart");
                RefreshPosition(notify: true);
                CompleteReadySeek();
                break;
        }
    }

    private void HandleClientMessage(IntPtr data)
    {
        if (data == IntPtr.Zero)
            return;

        var message = Marshal.PtrToStructure<MpvNative.MpvEventClientMessage>(data);
        var argumentCount = Math.Clamp(message.NumArgs, 0, 16);
        if (message.Args == IntPtr.Zero || argumentCount < 2)
            return;

        var arguments = new string[argumentCount];
        for (var index = 0; index < argumentCount; index++)
        {
            var argument = Marshal.ReadIntPtr(message.Args, index * IntPtr.Size);
            arguments[index] = argument == IntPtr.Zero
                ? string.Empty
                : Marshal.PtrToStringUTF8(argument) ?? string.Empty;
        }

        if (!string.Equals(arguments[0], "dinoplayer-input", StringComparison.Ordinal))
            return;

        var kind = arguments[1] switch
        {
            "move" => PlaybackPointerEventKind.Move,
            "enter" => PlaybackPointerEventKind.Enter,
            "leave" => PlaybackPointerEventKind.Leave,
            "double-click" => PlaybackPointerEventKind.DoubleClick,
            "wheel" => PlaybackPointerEventKind.Wheel,
            "right-click" => PlaybackPointerEventKind.RightClick,
            _ => (PlaybackPointerEventKind?)null
        };
        if (kind is null)
            return;

        var x = ParseInputCoordinate(arguments, 2);
        var y = ParseInputCoordinate(arguments, 3);
        var height = ParseInputCoordinate(arguments, 4);
        var wheelDelta = ParseInputCoordinate(arguments, 5);
        try
        {
            PointerInput?.Invoke(new PlaybackPointerEvent(kind.Value, x, y, height, wheelDelta));
        }
        catch (Exception exception)
        {
            AppLog.Error($"Playback pointer event handler failed: {exception}");
        }
    }

    private static double ParseInputCoordinate(IReadOnlyList<string> arguments, int index) =>
        index < arguments.Count && double.TryParse(arguments[index], NumberStyles.Float,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private void OnFileLoaded()
    {
        try
        {
            _audioTracks = DiscoverAudioTracks();
            _mediaLoaded = true;
            _videoReady = false;

            var width = GetInt64Native("video-params/w", 0);
            var height = GetInt64Native("video-params/h", 0);
            var fps = GetDoubleNative("estimated-vf-fps", 0);
            var duration = GetDoubleNative("duration", 0);
            AppLog.Info($"Media loaded: generation={Volatile.Read(ref _mediaGeneration)}; codec={GetStringNative("video-codec") ?? "none"}; " +
                        $"format={GetStringNative("video-format") ?? "unknown"}; size={width}x{height}; fps={fps:0.###}; " +
                        $"duration={duration:0.###}s; audio tracks={_audioTracks.Count}; audio graph deferred until video-ready");
            LogAndValidateOutput("file-loaded");
            TryApplyPendingAudioMix("file-loaded");
            ScheduleVideoReadyProbe(Volatile.Read(ref _mediaGeneration));
            Volatile.Write(ref _snapshot, GetSnapshotNative());
            MediaLoaded?.Invoke(_activeGeneration);
        }
        catch (Exception exception)
        {
            AppLog.Error($"Failed while configuring loaded media: {exception}");
            ReportError(exception.Message);
        }
    }

    private void HandleEndFile(MpvNative.MpvEvent mpvEvent)
    {
        var endFile = mpvEvent.Data == IntPtr.Zero
            ? default
            : Marshal.PtrToStructure<MpvNative.MpvEventEndFile>(mpvEvent.Data);
        var generation = _entryGenerations.GetValueOrDefault(endFile.PlaylistEntryId, -1);
        _entryGenerations.Remove(endFile.PlaylistEntryId);
        if (generation != Generation) return;
        MediaEnded?.Invoke(generation);
        if (endFile.Error < 0)
        {
            var nativeError = ErrorText(endFile.Error);
            var message = $"mpv could not play the file: {nativeError} ({endFile.Error}). " +
                          $"Required output is vo=gpu-next, gpu-context={_requiredGpuContext ?? "platform native"}; no external-window fallback was allowed.";
            AppLog.Error(message);
            CompletePendingAudioMix(new InvalidOperationException(message));
            CompletePendingSeek(new InvalidOperationException(message));
            ReportError(message);
            return;
        }

        AppLog.Info($"Media ended; reason={endFile.Reason}; mpv event error={mpvEvent.Error}.");
    }

    private void LogAndValidateOutput(string source)
    {
        var videoOutput = GetStringNative("current-vo") ?? "unknown";
        var gpuContext = GetStringNative("current-gpu-context") ?? "unknown";
        var hardwareDecoder = GetStringNative("hwdec-current") ?? "none";
        var signature = $"{videoOutput}|{gpuContext}|{hardwareDecoder}";
        if (!string.Equals(signature, _lastOutputSignature, StringComparison.Ordinal))
        {
            _lastOutputSignature = signature;
            var hardwareState = DescribeHardwareState(hardwareDecoder);
            AppLog.Info($"Playback output ({source}): current-vo={videoOutput}; current-gpu-context={gpuContext}; " +
                        $"requested-gpu-context={_requiredGpuContext ?? "auto (platform native)"}; host-handle/wid={_windowId}; " +
                        $"requested-hwdec=auto-safe; active-hwdec={hardwareDecoder}; media-loaded={_mediaLoaded}; " +
                        $"video-ready={_videoReady}; hwdec-attempted={_hardwareDecodeAttempted}; hwdec-fallback={_hardwareDecodeFallback}; " +
                        $"hardware-state={hardwareState}");
        }

        if (_requiredGpuContext is not null && gpuContext != "unknown" &&
            !string.Equals(gpuContext, _requiredGpuContext, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Embedding safety check failed: X11/XID host requires GPU context '{_requiredGpuContext}', but mpv activated '{gpuContext}'.";
            AppLog.Error(message);
            ReportError(message);
        }
    }

    private string DescribeHardwareState(string hardwareDecoder)
    {
        if (_hardwareDecodeFallback)
        {
            return "hardware-initialization-failed; software-fallback-active";
        }

        if (_hardwareDecodeAttempted && !string.Equals(hardwareDecoder, "none", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(hardwareDecoder, "no", StringComparison.OrdinalIgnoreCase))
        {
            return $"hardware-decoding-active ({hardwareDecoder})";
        }

        return _hardwareDecodeAttempted
            ? "hardware-decoding-attempted; active-decoder-unavailable"
            : "hardware-decoding-not-attempted";
    }

    private IReadOnlyList<AudioTrack> DiscoverAudioTracks()
    {
        var count = GetInt64Native("track-list/count", 0);
        var tracks = new List<AudioTrack>();
        var audioOrdinal = 0;
        for (var index = 0L; index < count; index++)
        {
            if (!string.Equals(GetStringNative($"track-list/{index}/type"), "audio", StringComparison.Ordinal))
            {
                continue;
            }

            var id = GetInt64Native($"track-list/{index}/id", -1);
            if (id < 0)
            {
                continue;
            }

            var title = GetStringNative($"track-list/{index}/title");
            var language = GetStringNative($"track-list/{index}/lang");
            var codec = GetStringNative($"track-list/{index}/codec");
            var ffIndex = GetInt64Native($"track-list/{index}/ff-index", -1);
            var details = new[] { title, language, codec }.Where(value => !string.IsNullOrWhiteSpace(value));
            tracks.Add(new AudioTrack
            {
                Id = id,
                SourceIndex = ffIndex is >= 0 and <= int.MaxValue ? (int)ffIndex : -1,
                AudioOrdinal = audioOrdinal,
                Label = $"Track {id}: {string.Join(" · ", details.DefaultIfEmpty("untitled"))}",
                Enabled = true,
                VolumePercent = 50
            });
            audioOrdinal++;
        }

        AppLog.Info(tracks.Count == 0
            ? "No audio tracks were discovered; playback will remain muted."
            : $"Discovered audio tracks: {string.Join(" | ", tracks.Select(track => track.Label))}. All start enabled at 50% for safer summing.");
        return tracks;
    }

    private void ExecutePendingAudioMix(string source)
    {
        AudioMixRequest? request;
        lock (_pendingOperationSync)
        {
            _audioMixOperationQueued = false;
            request = _pendingAudioMix;
            if (request is not null && request.Generation == Generation && (!_mediaLoaded || !_videoReady)) return;
            _pendingAudioMix = null;
        }

        if (request is null)
        {
            return;
        }

        if (request.Generation != Volatile.Read(ref _mediaGeneration))
        {
            AppLog.Info($"Discarding stale audio mix for media generation {request.Generation}; current generation is {Volatile.Read(ref _mediaGeneration)}.");
            request.Completion.TrySetResult();
            return;
        }
        try
        {
            ApplyAudioMixNative(request, source);
            request.Completion.TrySetResult();
        }
        catch (Exception exception)
        {
            request.Completion.TrySetException(exception);
            ReportQueuedOperationError("audio mix", exception);
        }
    }

    private void TryApplyPendingAudioMix(string source)
    {
        if (!_mediaLoaded || !_videoReady)
        {
            return;
        }

        ExecutePendingAudioMix(source);
    }

    private void CompletePendingAudioMix(Exception? error = null)
    {
        lock (_pendingOperationSync)
        {
            var pending = _pendingAudioMix;
            _pendingAudioMix = null;
            _audioMixOperationQueued = false;
            if (error is null) pending?.Completion.TrySetResult();
            else pending?.Completion.TrySetException(error);
        }
    }

    private void ScheduleVideoReadyProbe(long generation, int attempt = 0)
    {
        _ = ProbeVideoReadyAsync(generation, attempt);
    }

    private async Task ProbeVideoReadyAsync(long generation, int attempt)
    {
        try
        {
            await Task.Delay(attempt == 0 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMilliseconds(250));
            if (Volatile.Read(ref _disposed) != 0 || _worker.IsStopping)
            {
                return;
            }

            await QueueNativeOperation("video readiness probe", () => CheckVideoReadyProbe(generation, attempt));
        }
        catch (Exception exception)
        {
            if (exception is not ObjectDisposedException && Volatile.Read(ref _disposed) == 0)
            {
                AppLog.Warn($"Video readiness probe failed: {exception.Message}");
            }
        }
    }

    private void CheckVideoReadyProbe(long generation, int attempt)
    {
        if (generation != Volatile.Read(ref _mediaGeneration) || !_mediaLoaded || _videoReady)
        {
            return;
        }

        var videoOutput = GetStringNative("current-vo");
        var width = GetInt64Native("video-params/w", 0);
        var height = GetInt64Native("video-params/h", 0);
        if (!string.IsNullOrWhiteSpace(videoOutput) && videoOutput != "unknown" && width > 0 && height > 0)
        {
            _videoReady = true;
            AppLog.Info($"Video readiness probe succeeded: generation={generation}; vo={videoOutput}; size={width}x{height}; attempt={attempt + 1}.");
            LogAndValidateOutput("video-ready-probe");
            TryApplyPendingAudioMix("video-ready-probe");
            return;
        }

        if (attempt < 8)
        {
            AppLog.Info($"Video readiness probe waiting: generation={generation}; vo={videoOutput ?? "unknown"}; size={width}x{height}; attempt={attempt + 1}.");
            ScheduleVideoReadyProbe(generation, attempt + 1);
        }
        else
        {
            AppLog.Warn($"Video readiness probe timed out: generation={generation}; vo={videoOutput ?? "unknown"}; size={width}x{height}.");
            CompletePendingAudioMix(new InvalidOperationException("Video output did not become ready for audio setup."));
        }
    }

    private void ApplyAudioMixNative(AudioMixRequest request, string source)
    {
        var positionBefore = GetDoubleNative("time-pos", 0);
        var pausedBefore = GetFlagNative("pause", false);
        var started = Stopwatch.GetTimestamp();

        SetPropertyNative("lavfi-complex", request.Graph);
        if (request.Graph.Length == 0)
        {
            SetPropertyNative("aid", "no");
        }

        var elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var positionAfter = GetDoubleNative("time-pos", positionBefore);
        var pausedAfter = GetFlagNative("pause", pausedBefore);
        var speed = Math.Clamp(GetDoubleNative("speed", 1), 0.25, 3);
        var expectedPosition = pausedBefore ? positionBefore : positionBefore + elapsedSeconds * speed;
        var positionChangedUnexpectedly = double.IsFinite(positionAfter) &&
                                          Math.Abs(positionAfter - expectedPosition) > 0.5;

        if (pausedAfter != pausedBefore)
        {
            SetPropertyNative("pause", pausedBefore ? "yes" : "no");
            AppLog.Info($"Restored pause state after audio graph update: paused={pausedBefore}.");
        }

        if (positionChangedUnexpectedly && positionBefore > 0)
        {
            CommandNative("seek", positionBefore.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
            AppLog.Warn($"Restored playback position after audio graph update: before={positionBefore:0.###}; after={positionAfter:0.###}; expected={expectedPosition:0.###}.");
        }

        AppLog.Info($"Audio mix applied: source={source}; generation={request.Generation}; enabled={request.Description}; " +
                    $"position={positionBefore:0.000}; paused={pausedBefore}; graph={request.Graph}");
    }

    private Task<T> InvokeOnMpvThreadAsync<T>(Func<T> operation)
    {
        ThrowIfDisposed();
        return _worker.InvokeAsync(operation);
    }

    private async Task QueueNativeOperation(string name, Action operation)
    {
        try { await InvokeOnMpvThreadAsync(() => { operation(); return 0; }).ConfigureAwait(false); }
        catch (Exception exception) { ReportQueuedOperationError(name, exception); }
    }

    private void ReportQueuedOperationError(string operation, Exception exception)
    {
        if (exception is ObjectDisposedException)
        {
            return;
        }

        AppLog.Error($"Queued playback operation failed ({operation}): {exception}");
        ReportError(exception.Message);
    }

    private void ReportError(string message)
    {
        try
        {
            Error?.Invoke(new PlaybackError(Generation, message));
        }
        catch (Exception exception)
        {
            AppLog.Error($"Playback error handler failed: {exception}");
        }
    }

    private void SetPausedNative(bool paused) =>
        SetPropertyNative("pause", paused ? "yes" : "no");

    private void SetOptionNative(string name, string value) =>
        Check(MpvNative.mpv_set_option_string(_context, name, value), $"set option {name}={value}");

    private void SetPropertyNative(string name, string value) =>
        Check(MpvNative.mpv_set_property_string(_context, name, value), $"set property {name}={value}");

    private void LoadInputBridge()
    {
        if (OperatingSystem.IsWindows())
        {
            AppLog.Info("Using the Windows native video input bridge; libmpv Lua input bridge is disabled.");
            return;
        }

        var scriptPath = EmbeddedInputBridge.GetPath();
        CommandNative("load-script", scriptPath);
        AppLog.Info($"Loaded libmpv input bridge: {scriptPath}");
    }

    private void CommandNative(params string[] arguments)
    {
        var strings = new IntPtr[arguments.Length];
        var array = IntPtr.Zero;
        try
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                strings[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
            }

            array = Marshal.AllocHGlobal(IntPtr.Size * (arguments.Length + 1));
            for (var index = 0; index < strings.Length; index++)
            {
                Marshal.WriteIntPtr(array, index * IntPtr.Size, strings[index]);
            }

            Marshal.WriteIntPtr(array, arguments.Length * IntPtr.Size, IntPtr.Zero);
            Check(MpvNative.mpv_command(_context, array), $"command {arguments[0]}");
        }
        finally
        {
            foreach (var pointer in strings)
            {
                Marshal.FreeCoTaskMem(pointer);
            }

            if (array != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(array);
            }
        }
    }

    private string? GetStringNative(string property)
    {
        var pointer = MpvNative.mpv_get_property_string(_context, property);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            MpvNative.mpv_free(pointer);
        }
    }

    private double GetDoubleNative(string property, double fallback) =>
        MpvNative.mpv_get_property(_context, property, MpvFormatDouble, out var value) >= 0 && double.IsFinite(value)
            ? value : fallback;

    private long GetInt64Native(string property, long fallback) =>
        MpvNative.mpv_get_property_int64(_context, property, MpvFormatInt64, out var value) >= 0 ? value : fallback;

    private bool GetFlagNative(string property, bool fallback) =>
        MpvNative.mpv_get_property_flag(_context, property, MpvFormatFlag, out var value) >= 0 ? value != 0 : fallback;

    private void WriteMpvLog(IntPtr data)
    {
        if (data == IntPtr.Zero)
        {
            return;
        }

        var message = Marshal.PtrToStructure<MpvNative.MpvEventLogMessage>(data);
        var level = Marshal.PtrToStringUTF8(message.Level) ?? "info";
        var prefix = Marshal.PtrToStringUTF8(message.Prefix) ?? "mpv";
        var text = (Marshal.PtrToStringUTF8(message.Text) ?? string.Empty).TrimEnd();
        if (text.Length == 0)
        {
            return;
        }

        if (text.Contains("Using hardware decoding", StringComparison.OrdinalIgnoreCase))
        {
            _hardwareDecodeAttempted = true;
        }
        else if (text.Contains("Attempting next decoding method after failure", StringComparison.OrdinalIgnoreCase))
        {
            _hardwareDecodeAttempted = true;
            _hardwareDecodeFallback = true;
        }

        if (level is "error" or "fatal")
        {
            AppLog.Error($"mpv/{level}/{prefix}: {text}");
        }
        else
        {
            AppLog.Info($"mpv/{level}/{prefix}: {text}");
        }
    }

    private static string FormatWindowId(IntPtr handle, string descriptor)
    {
        if (descriptor.Contains("HWND", StringComparison.OrdinalIgnoreCase) || OperatingSystem.IsWindows())
        {
            return unchecked((uint)handle.ToInt64()).ToString(CultureInfo.InvariantCulture);
        }

        return unchecked((ulong)handle.ToInt64()).ToString(CultureInfo.InvariantCulture);
    }

    private static string DescribeBackend(string handleDescriptor)
    {
        if (OperatingSystem.IsWindows())
        {
            return $"Win32 ({handleDescriptor})";
        }

        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        if (string.Equals(session, "wayland", StringComparison.OrdinalIgnoreCase))
        {
            return $"X11 via XWayland ({handleDescriptor}) in a Wayland desktop session";
        }

        return $"X11 ({handleDescriptor}); desktop session={session ?? "unknown"}";
    }

    private static void Check(int result, string operation)
    {
        if (result >= 0)
        {
            return;
        }

        var error = ErrorText(result);
        throw new InvalidOperationException($"libmpv failed to {operation}: {error} ({result}).");
    }

    private static string ErrorText(int error) =>
        Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(error)) ?? $"error {error}";

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || _worker.IsStopping, this);

    public Task StopAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        CompletePendingAudioMix(new ObjectDisposedException(nameof(MpvPlaybackEngine)));
        CompletePendingSeek(new ObjectDisposedException(nameof(MpvPlaybackEngine)));
        return _worker.StopAsync();
    }

    public void Dispose() => _ = StopAsync();

    private sealed record AudioMixRequest(long Generation, string Graph, string Description, TaskCompletionSource Completion);
    private sealed record SeekCompletion(long Generation, double Seconds, TaskCompletionSource Completion);
}
