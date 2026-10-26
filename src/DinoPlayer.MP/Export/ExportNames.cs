namespace DinoPlayer.MP.Export;

// Option values shared by the Export tab, saved presets and the FFmpeg command
// builder. They are stored in settings.json, so existing values must not change.
internal static class ExportNames
{
    public const string Mp4 = "MP4";
    public const string Mkv = "MKV";

    public const string SourceResolution = "Source";

    public const string H264 = "H.264";
    public const string H265 = "H.265";
    public const string Av1 = "AV1";
    public const string Copy = "Copy";

    public const string Aac = "AAC";
    public const string Opus = "Opus";
    public const string Flac = "FLAC";

    public const string Bitrate = "Bitrate";
    public const string Quality = "Quality";
}
