namespace DinoPlayer.MP.Playback;

public sealed record PlaybackSnapshot(
    double PositionSeconds,
    double DurationSeconds,
    double PlaybackSpeed,
    bool IsPaused,
    string VideoOutput,
    string GpuContext,
    string HardwareDecoder,
    long DecoderDroppedFrames,
    long OutputDroppedFrames)
{
    public long Generation { get; init; }
    public bool IsLoaded { get; init; }
    public static PlaybackSnapshot Empty { get; } = new(0, 0, 1, true, "unavailable", "unavailable", "none", 0, 0);
}

public sealed record PlaybackError(long Generation, string Message);
