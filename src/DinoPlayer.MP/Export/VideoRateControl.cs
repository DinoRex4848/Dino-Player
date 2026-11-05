using System.Globalization;

namespace DinoPlayer.MP.Export;

internal static class VideoRateControl
{
    public static (int Minimum, int Maximum) QualityRange(string codec, bool gpu) =>
        (gpu ? 1 : 0, codec == ExportNames.Av1 ? 63 : 51);

    public static bool IsValidQuality(string? value, string codec, bool gpu)
    {
        var (minimum, maximum) = QualityRange(codec, gpu);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality) &&
               quality >= minimum && quality <= maximum;
    }

    public static void Validate(string mode, string bitrate, string quality, string targetSize, string codec, bool gpu)
    {
        if (mode == ExportNames.Bitrate)
        {
            if (!double.TryParse(bitrate, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
                !double.IsFinite(parsed) || parsed <= 0)
                throw new ArgumentException("Enter a finite video bitrate greater than 0 Mbps.");
        }
        else if (mode == ExportNames.Quality)
        {
            if (!IsValidQuality(quality, codec, gpu))
            {
                var (minimum, maximum) = QualityRange(codec, gpu);
                throw new ArgumentException($"Enter a whole-number {(gpu ? "CQ" : "CRF")} quality between {minimum} and {maximum}." +
                                            (gpu ? " NVENC quality 0 means automatic." : ""));
            }
        }
        else if (mode == ExportNames.Size)
        {
            if (!TargetSize.TryParseMegabytes(targetSize, out _))
                throw new ArgumentException("Enter a target size in MB greater than 0.");
        }
        else throw new ArgumentException("Choose Bitrate, Target quality or Target size for video rate control.");
    }
}
