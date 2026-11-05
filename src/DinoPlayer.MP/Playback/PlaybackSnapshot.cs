using System.Diagnostics;

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
    // Longest gap between samples that is filled by extrapolation. Beyond it the
    // engine has stalled (or is shutting down), so the last sample is shown.
    private const double MaxExtrapolationSeconds = 1;

    public long Generation { get; init; }
    public bool IsLoaded { get; init; }
    /// <summary>Stopwatch timestamp of the PositionSeconds sample; 0 when unknown.</summary>
    public long SampleTimestamp { get; init; }
    /// <summary>A seek has started and playback has not restarted yet.</summary>
    public bool IsSeeking { get; init; }
    public static PlaybackSnapshot Empty { get; } = new(0, 0, 1, true, "unavailable", "unavailable", "none", 0, 0);

    /// <summary>
    /// The position at <paramref name="timestamp"/>: the last sample, advanced by the
    /// elapsed time while playing so the UI can move smoothly between samples.
    /// </summary>
    public double EstimatePosition(long timestamp)
    {
        if (IsPaused || IsSeeking || SampleTimestamp == 0) return PositionSeconds;
        var elapsed = Math.Clamp(Stopwatch.GetElapsedTime(SampleTimestamp, timestamp).TotalSeconds, 0, MaxExtrapolationSeconds);
        var estimate = PositionSeconds + elapsed * PlaybackSpeed;
        return DurationSeconds > 0 ? Math.Min(estimate, DurationSeconds) : estimate;
    }
}

public sealed record PlaybackError(long Generation, string Message);
