namespace DinoPlayer.MP.Settings;

// Keep the root object stable and sectioned. New preferences should be added
// to a typed section instead of changing the meaning of existing properties.
public sealed class AppSettings
{
    public int Version { get; set; } = 6;
    public List<ExportPreset> ExportPresets { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public PlaybackShortcutSettings PlaybackShortcuts { get; set; } = new();
}

public sealed class PlaybackShortcutSettings
{
    public string SpeedUpKey { get; set; } = "Equal";
    public string SpeedUpModifiers { get; set; } = "None";
    public string SpeedDownKey { get; set; } = "Minus";
    public string SpeedDownModifiers { get; set; } = "None";
}

public sealed class GeneralSettings
{
    public string DefaultOutputFolder { get; set; } = string.Empty;
    public bool LoopByDefault { get; set; }
    public bool ShowDiagnosticLog { get; set; }
    public bool ShowPlayerQueueButton { get; set; }
    public bool EditableCropTimes { get; set; }
    public string DefaultExportPresetName { get; set; } = string.Empty;
}

public sealed class AudioSettings
{
    public List<AudioTrackPreset> TrackPresets { get; set; } = new();
}

public sealed class AudioTrackPreset
{
    public int TrackCount { get; set; }
    public List<AudioTrackSetting> Tracks { get; set; } = new();
}

public sealed class AudioTrackSetting
{
    public bool Enabled { get; set; }
    public double VolumePercent { get; set; } = 50;
}

public sealed class ExportPreset
{
    public string Name { get; set; } = string.Empty;
    public string Container { get; set; } = "MP4";

    public string Resolution { get; set; } = "Source";
    public string VideoCodecName { get; set; } = "H.264";
    public bool UseGpu { get; set; }
    public string VideoPreset { get; set; } = "medium";
    public string VideoBitrate { get; set; } = "8";
    public string VideoRateControl { get; set; } = "Bitrate";
    public string VideoQuality { get; set; } = "23";

    public string AudioCodecName { get; set; } = "AAC";
    public string AudioBitrate { get; set; } = "192k";
    public bool KeepTracksSeparate { get; set; }
    public bool IgnoreVolume { get; set; }

    public bool VideoAdvanced { get; set; }
    public string VideoAdvancedText { get; set; } = string.Empty;
    public bool AudioAdvanced { get; set; }
    public string AudioAdvancedText { get; set; } = string.Empty;
}
