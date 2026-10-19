namespace DinoPlayer.MP.Playback;

public interface IPlaybackEngine : IDisposable
{
    event Action<long>? MediaLoaded;
    event Action<long>? MediaEnded;
    /// <summary>A seek or playback restart refreshed the cached position; raised on the native worker.</summary>
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
    Task SetMasterVolumeAsync(double volumePercent);
    Task SetCursorAutohideAsync(bool enabled);
    Task SetLoopingAsync(bool enabled);
    /// <summary>Complete after the latest coalesced audio mix has been applied, including deferred setup.</summary>
    Task ApplyAudioMixAsync();
    PlaybackSnapshot GetSnapshot();
}
