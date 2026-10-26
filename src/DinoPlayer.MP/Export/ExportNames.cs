namespace DinoPlayer.MP.Export;

// Option values shared by the Export tab, saved presets and the FFmpeg command
// builder. They are stored in settings.json, so existing values must not change.
internal static class ExportNames
{
    public const string Mp4 = "MP4";
    public const string Mkv = "MKV";

    public const string SourceResolution = "Source";

    // Saved resolution values with their Export tab labels. Only the height is
    // used: the width follows the source's shape (see ExportCommandBuilder).
    public static readonly (string Value, string Label)[] FixedResolutions =
    {
        ("1280x720", "720p"), ("1920x1080", "1080p"), ("2560x1440", "1440p"), ("3840x2160", "2160p (4K)")
    };

    public static bool IsFixedResolution(string? value) => FixedResolutions.Any(resolution => resolution.Value == value);

    public static int ResolutionHeight(string value) => int.Parse(value[(value.IndexOf('x') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

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
