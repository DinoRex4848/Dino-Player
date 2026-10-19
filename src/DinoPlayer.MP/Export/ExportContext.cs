using DinoPlayer.MP.Playback;

namespace DinoPlayer.MP.Export;

public sealed class ExportContext
{
    public string SourcePath { get; init; } = string.Empty;
    public bool CropEnabled { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public double Duration { get; init; }
    public double PlaybackSpeed { get; init; } = 1;
    public IReadOnlyList<AudioTrack> Tracks { get; init; } = Array.Empty<AudioTrack>();
}
