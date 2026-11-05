using System.Globalization;

namespace DinoPlayer.MP.Export;

/// <summary>
/// A size limit for one encode: the video bitrate to use, the bytes expected for
/// audio and the container, and which attempt this is.
/// </summary>
internal sealed record SizeLimit(long MaxBytes, int VideoKbps, double Seconds, long OtherBytes, int Attempt = 1);

/// <summary>What to do after an encode with a size limit.</summary>
internal sealed record SizeCheck(bool Fits, SizeLimit? Retry, string Message);

// "Target size" export: picks the video bitrate that keeps the whole file under
// a size, and checks the finished file in case the encoder went over. Encoders
// with a rate cap stay under (measured at 70–92% of the size); SVT-AV1 has no
// cap and can go 15% over on short clips, so it relies on the check.
internal static class TargetSize
{
    public const string DefaultMegabytes = "10";
    public const int MinimumVideoKbps = 100;
    // SVT-AV1 rejects higher targets, and a size-limited clip never needs more.
    public const int MaximumVideoKbps = 100_000;
    public const int MaxAttempts = 3;
    // 1 MB = 1,000,000 bytes, so a file under 10 MB also fits a 10 MiB limit.
    private const double BytesPerMegabyte = 1_000_000;
    private const double MaxMegabytes = 1_000_000;
    // MP4 and MKV headers and indexes, measured at 0.1–0.6% of short clips.
    private const double ContainerBytes = 32_000;
    private const double ContainerBytesPerSecond = 2_000;
    // AAC and Opus land within about 1% of their bitrate.
    private const double AudioMargin = 1.02;
    // A retry aims this far under the limit.
    private const double RetryAim = 0.97;
    // Each retry lowers the bitrate by at least this much.
    private const double RetryStep = 0.9;
    // Below this, a retry that shrank the file less than 5% won't get there.
    private const double NoProgress = 0.95;
    private const int LowBitrateKbps = 2_000;

    public static bool TryParseMegabytes(string? text, out double megabytes) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out megabytes) &&
        double.IsFinite(megabytes) && megabytes > 0 && megabytes <= MaxMegabytes;

    /// <summary>
    /// Encoders that honour -maxrate and -bufsize, so the video has a hard upper
    /// bound. SVT-AV1 only allows a cap in CRF mode, so CPU AV1 relies on the size check.
    /// </summary>
    public static bool HasRateCap(ExportOptions options) => options.UseGpu || options.VideoCodec != ExportNames.Av1;

    /// <summary>Rate-control buffer length. The video can exceed the cap by one buffer.</summary>
    public static double BufferSeconds(double seconds) => Math.Clamp(seconds * 0.1, 0.5, 2);

    /// <summary>The first attempt's bitrate. Throws ArgumentException with a message for the user.</summary>
    public static SizeLimit Plan(ExportOptions options)
    {
        if (!TryParseMegabytes(options.VideoTargetSize, out var megabytes))
            throw new ArgumentException("Enter a target size in MB greater than 0.");
        var tracks = options.Tracks.Count(track => track.Enabled);
        if (tracks > 0 && (options.AudioAdvanced || options.AudioCodec is not (ExportNames.Aac or ExportNames.Opus)))
            throw new ArgumentException("Target size needs AAC or Opus audio, or no audio tracks.");
        var seconds = ExportCommandBuilder.OutputSeconds(options);
        if (!double.IsFinite(seconds) || seconds <= 0)
            throw new ArgumentException("The clip length is unavailable.");

        var audioStreams = tracks == 0 ? 0 : options.KeepTracksSeparate ? tracks : 1;
        var audioBits = audioStreams * AudioKbps(options.AudioBitrate) * 1000 * seconds * AudioMargin;
        var maxBytes = (long)(megabytes * BytesPerMegabyte);
        var otherBytes = (long)(ContainerBytes + ContainerBytesPerSecond * seconds + audioBits / 8);
        var videoBits = (maxBytes - otherBytes) * 8d;
        var videoSeconds = seconds + (HasRateCap(options) ? BufferSeconds(seconds) : 0);
        var kbps = videoBits / videoSeconds / 1000;
        if (kbps < MinimumVideoKbps)
            throw new ArgumentException($"{FormatLimit(maxBytes)} is too small for this {TimeFormat.Clock(seconds)} clip. " +
                                        "Choose a larger size, a shorter clip or a lower audio bitrate.");
        return new(maxBytes, (int)Math.Min(kbps, MaximumVideoKbps), seconds, otherBytes);
    }

    /// <summary>Checks a finished file. <paramref name="previousBytes"/> is the last attempt's size.</summary>
    public static SizeCheck Check(SizeLimit limit, long bytes, long? previousBytes)
    {
        if (bytes <= limit.MaxBytes)
            return new(true, null, $"Size: {FormatSize(bytes, roundUp: false)}, under the {FormatLimit(limit.MaxBytes)} limit.");
        var over = $"The clip is {FormatSize(bytes, roundUp: true)}, over the {FormatLimit(limit.MaxBytes)} limit";
        const string advice = "Choose a lower resolution, a shorter clip or a larger size.";
        if (previousBytes is { } previous && bytes > previous * NoProgress)
            return new(false, null, $"{over}, and a lower bitrate did not make it smaller. {advice}");
        if (limit.Attempt >= MaxAttempts)
            return new(false, null, $"{over} after {MaxAttempts} attempts. {advice}");
        // Scale the video bitrate by how far over the video part came out, aiming a
        // little under the limit. Audio and the container stay the same size.
        var videoBytes = Math.Max(1, bytes - limit.OtherBytes);
        var wantedVideoBytes = limit.MaxBytes * RetryAim - limit.OtherBytes;
        var kbps = (int)Math.Min(limit.VideoKbps * wantedVideoBytes / videoBytes, limit.VideoKbps * RetryStep);
        if (kbps < MinimumVideoKbps)
            return new(false, null, $"{over}. {advice}");
        var next = limit with { VideoKbps = kbps, Attempt = limit.Attempt + 1 };
        return new(false, next, $"{over}. Encoding again at {FormatKbps(kbps)} (attempt {next.Attempt} of {MaxAttempts}).");
    }

    /// <summary>A one-line summary for the Export tab, or why the size can't be used.</summary>
    public static string Describe(ExportOptions options)
    {
        if (!double.IsFinite(options.Duration) || options.Duration <= 0)
            return "The video bitrate is worked out from the clip length.";
        try
        {
            var plan = Plan(options);
            var text = $"About {FormatKbps(plan.VideoKbps)} of video for this {TimeFormat.Clock(plan.Seconds)} clip.";
            if (plan.VideoKbps < LowBitrateKbps && options.Resolution != ExportNames.FixedResolutions[0].Value)
                text += " At this bitrate, 720p looks sharper.";
            return text;
        }
        catch (ArgumentException exception) { return exception.Message; }
    }

    private static double AudioKbps(string bitrate) =>
        double.TryParse(bitrate.TrimEnd('k', 'K'), NumberStyles.Float, CultureInfo.InvariantCulture, out var kbps) &&
        double.IsFinite(kbps) && kbps > 0 ? kbps : 320;

    internal static string FormatKbps(int kbps) => kbps >= 1000
        ? $"{(kbps / 1000d).ToString("0.#", CultureInfo.InvariantCulture)} Mbps"
        : $"{kbps.ToString(CultureInfo.InvariantCulture)} kbps";

    // Rounded away from the limit, so "over" never shows the limit itself.
    private static string FormatSize(long bytes, bool roundUp)
    {
        var hundredths = bytes / BytesPerMegabyte * 100;
        var shown = (roundUp ? Math.Ceiling(hundredths) : Math.Floor(hundredths)) / 100;
        return $"{shown.ToString("0.00", CultureInfo.InvariantCulture)} MB";
    }
    internal static string FormatLimit(long bytes) => $"{(bytes / BytesPerMegabyte).ToString("0.##", CultureInfo.InvariantCulture)} MB";
}
