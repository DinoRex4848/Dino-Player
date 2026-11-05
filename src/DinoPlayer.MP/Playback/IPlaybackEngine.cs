namespace DinoPlayer.MP.Playback;

public interface IPlaybackEngine : IDisposable
{
    event Action<long>? MediaLoaded;
    event Action<long>? MediaEnded;
    /// <summary>A seek, playback restart, pause or speed change refreshed the cached snapshot; raised on the native worker.</summary>
    event Action<long>? PositionChanged;
    event Action<PlaybackError>? Error;
    event Action<PlaybackPointerEvent>? PointerInput;

    long Generation { get; }
    Task StopAsync();

    IReadOnlyList<AudioTrack> AudioTracks { get; }
    string NativeLibraryDescription { get; }
    string ClientApiVersion { get; }

    /// <summary>Load from the beginning, optionally holding playback paused while the caller prepares audio.</summary>
    Task OpenAsync(string path, bool startPaused = false);
    Task TogglePauseAsync();
    Task SetPausedAsync(bool paused);
    Task AdjustPlaybackSpeedAsync(int direction);
    /// <summary>Seek to an exact absolute position in seconds.</summary>
    Task SeekAsync(double seconds);
    /// <summary>Seek exactly and wait for playback to restart at the target, for media preparation.</summary>
    Task SeekAndWaitAsync(double seconds);
    /// <summary>Navigate by a signed offset using fast keyframe seeking.</summary>
    Task SeekRelativeAsync(double deltaSeconds);
    /// <summary>Navigate by a signed offset to an exact position.</summary>
    Task SeekRelativeExactAsync(double deltaSeconds);
    /// <summary>Fast keyframe seek to an absolute position, for previews while dragging.</summary>
    Task SeekPreviewAsync(double seconds);
    /// <summary>Show the next (positive) or previous (negative) frame and pause.</summary>
    Task FrameStepAsync(int direction);
    /// <summary>Read the current position from mpv instead of the cached snapshot.</summary>
    Task<double> GetPositionAsync();
    Task SetMasterVolumeAsync(double volumePercent);
    Task SetCursorAutohideAsync(bool enabled);
    Task SetLoopingAsync(bool enabled);
    /// <summary>Complete after the latest coalesced audio mix has been applied, including deferred setup.</summary>
    Task ApplyAudioMixAsync();
    PlaybackSnapshot GetSnapshot();
}
