namespace DinoPlayer.MP.Playback;

public sealed class AudioTrack
{
    public required long Id { get; init; }
    // The mpv track id is not guaranteed to match FFmpeg's zero-based audio
    // stream index. Keep both so the exporter can map the same streams the
    // player discovered.
    public int SourceIndex { get; init; } = -1;
    public int AudioOrdinal { get; init; }
    public required string Label { get; init; }
    public bool Enabled { get; set; }
    public double VolumePercent { get; set; } = 50;
}
