using System.Collections.Immutable;

namespace DinoPlayer.MP.Export;

internal sealed record ExportAudioTrack(int SourceIndex, int AudioOrdinal, bool Enabled, double VolumePercent);

internal sealed record ExportOptions
{
    public string SourcePath { get; init; } = "";
    public string OutputPath { get; init; } = "";
    public bool CropEnabled { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public double Duration { get; init; }
    public double PlaybackSpeed { get; init; } = 1;
    public ImmutableArray<ExportAudioTrack> Tracks { get; init; } = [];
    public string Resolution { get; init; } = "Source";
    public string VideoCodec { get; init; } = "H.264";
    public string VideoPreset { get; init; } = "medium";
    public string VideoBitrate { get; init; } = "8";
    public string VideoRateControl { get; init; } = "Bitrate";
    public string VideoQuality { get; init; } = "23";
    public bool UseGpu { get; init; }
    public string AudioCodec { get; init; } = "AAC";
    public string AudioBitrate { get; init; } = "192k";
    public bool KeepTracksSeparate { get; init; }
    public bool IgnoreVolume { get; init; }
    public bool VideoAdvanced { get; init; }
    public bool AudioAdvanced { get; init; }
    public string VideoAdvancedText { get; init; } = "";
    public string AudioAdvancedText { get; init; } = "";
    public bool CopyPlaybackSpeed { get; init; }
    public bool Overwrite { get; init; }
}
