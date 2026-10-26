using System.Globalization;
using System.Text;

namespace DinoPlayer.MP.Export;

internal sealed record ExportCommand(IReadOnlyList<string> Arguments, double DurationSeconds, string? Warning);

internal static class ExportCommandBuilder
{
    public static ExportCommand Build(ExportOptions options)
    {
        Validate(options);
        var outputPath = options.OutputPath.Trim();
        var videoFlags = options.VideoAdvanced ? ParseArguments(options.VideoAdvancedText) : [];
        var audioFlags = options.AudioAdvanced && options.Tracks.Any(t => t.Enabled) ? ParseArguments(options.AudioAdvancedText) : [];
        var args = new List<string> { "-hide_banner", "-progress", "pipe:1", "-nostats" };
        if (options.CropEnabled)
        {
            args.Add("-ss");
            args.Add(options.Start.ToString(CultureInfo.InvariantCulture));
        }
        var copySpeed = options.CopyPlaybackSpeed;
        var speed = copySpeed ? NormalizePlaybackSpeed(options.PlaybackSpeed) : 1;
        if (copySpeed && options.CropEnabled)
        {
            args.Add("-t");
            args.Add((options.End - options.Start).ToString(CultureInfo.InvariantCulture));
        }
        args.Add("-i");
        args.Add(options.SourcePath);

        var videoAdvanced = options.VideoAdvanced;
        var audioAdvanced = options.AudioAdvanced;
        var selectedVideo = options.VideoCodec;
        var selectedAudio = options.AudioCodec;
        var enabledTracks = options.Tracks.Where(track => track.Enabled).ToArray();
        var videoCopy = !videoAdvanced && selectedVideo == ExportNames.Copy;
        var audioCopy = !audioAdvanced && selectedAudio == ExportNames.Copy;
        var filterAudio = !audioAdvanced && !audioCopy && enabledTracks.Length > 0;
        var mergeAudio = filterAudio && !options.KeepTracksSeparate;
        var scaleVideo = !videoAdvanced && !videoCopy && ExportNames.IsFixedResolution(options.Resolution);
        var filter = new StringBuilder();

        if (scaleVideo || copySpeed)
        {
            filter.Append("[0:v:0]");
            if (copySpeed)
                filter.Append("setpts=(PTS-STARTPTS)/").Append(speed.ToString("0.###", CultureInfo.InvariantCulture));
            if (scaleVideo)
            {
                if (copySpeed)
                    filter.Append(',');
                // Fixed height; -2 keeps the source's shape with an even width,
                // so ultrawide and 4:3 clips keep square pixels.
                filter.Append("scale=-2:").Append(ExportNames.ResolutionHeight(options.Resolution));
            }
            filter.Append("[vout];");
        }

        if (filterAudio)
        {
            for (var index = 0; index < enabledTracks.Length; index++)
            {
                var track = enabledTracks[index];
                var gain = options.IgnoreVolume ? 1 : track.VolumePercent / 100d;
                filter.Append('[').Append(MapAudioTrack(track)).Append(']');
                if (copySpeed)
                    filter.Append("asetpts=PTS-STARTPTS,");
                filter.Append($"volume={gain.ToString("0.###", CultureInfo.InvariantCulture)}");
                if (copySpeed && !mergeAudio)
                    filter.Append(',').Append(BuildTempoFilter(speed));
                filter.Append($"[a{index}];");
            }

            if (mergeAudio)
            {
                if (enabledTracks.Length == 1)
                {
                    filter.Append("[a0]");
                    if (copySpeed)
                        filter.Append(BuildTempoFilter(speed)).Append(',');
                    filter.Append("aformat=channel_layouts=stereo[aout]");
                }
                else
                {
                    for (var index = 0; index < enabledTracks.Length; index++)
                        filter.Append($"[a{index}]");
                    filter.Append($"amix=inputs={enabledTracks.Length}:normalize=0");
                    if (copySpeed)
                        filter.Append(',').Append(BuildTempoFilter(speed));
                    filter.Append(",aformat=channel_layouts=stereo[aout]");
                }
            }
        }

        if (filter.Length > 0)
        {
            if (filter[^1] == ';')
                filter.Length--;
            args.Add("-filter_complex");
            args.Add(filter.ToString());
        }

        args.Add("-map");
        args.Add(scaleVideo || copySpeed ? "[vout]" : "0:v:0");
        if (enabledTracks.Length > 0)
        {
            if (mergeAudio)
                args.AddRange(new[] { "-map", "[aout]" });
            else if (filterAudio)
            {
                for (var index = 0; index < enabledTracks.Length; index++)
                    args.AddRange(new[] { "-map", $"[a{index}]" });
            }
            else
            {
                foreach (var track in enabledTracks)
                    args.AddRange(new[] { "-map", MapAudioTrack(track) });
            }
        }

        if (videoAdvanced)
            args.AddRange(videoFlags);
        else if (videoCopy)
            args.AddRange(new[] { "-c:v", "copy" });
        else
        {
            var encoder = selectedVideo switch
            {
                ExportNames.H265 => options.UseGpu ? "hevc_nvenc" : "libx265",
                ExportNames.Av1 => options.UseGpu ? "av1_nvenc" : "libsvtav1",
                _ => options.UseGpu ? "h264_nvenc" : "libx264"
            };
            args.AddRange(new[] { "-c:v", encoder, "-preset", options.VideoPreset });
            if (options.VideoRateControl == ExportNames.Quality)
            {
                var quality = int.Parse(options.VideoQuality, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                args.AddRange(options.UseGpu
                    ? new[] { "-rc", "vbr", "-cq", quality, "-b:v", "0" }
                    : new[] { "-crf", quality });
            }
            else
                args.AddRange(new[] { "-b:v", $"{double.Parse(options.VideoBitrate, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}M" });
            args.AddRange(new[] { "-pix_fmt", encoder.Contains("264", StringComparison.Ordinal) ? "yuv420p" : "yuv420p10le" });
        }

        if (enabledTracks.Length > 0)
        {
            if (audioAdvanced)
            {
                args.AddRange(audioFlags);
                if (copySpeed)
                    args.AddRange(new[] { "-filter:a", BuildTempoFilter(speed) });
            }
            else if (audioCopy)
                args.AddRange(new[] { "-c:a", "copy" });
            else
            {
                var encoder = selectedAudio switch { ExportNames.Opus => "libopus", ExportNames.Flac => "flac", _ => "aac" };
                args.AddRange(new[] { "-c:a", encoder });
                if (selectedAudio != ExportNames.Flac)
                    args.AddRange(new[] { "-b:a", options.AudioBitrate });
            }
        }

        if (options.CropEnabled && !copySpeed)
            args.AddRange(new[] { "-t", (options.End - options.Start).ToString(CultureInfo.InvariantCulture) });
        if (copySpeed && !options.CropEnabled)
            args.AddRange(new[] { "-t", (options.Duration / speed).ToString(CultureInfo.InvariantCulture) });
        if (outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(options.Overwrite ? "-y" : "-n");
        args.Add(outputPath);
        var seconds = (options.CropEnabled ? options.End - options.Start : options.Duration) / speed;
        return new ExportCommand(args.AsReadOnly(), seconds,
            options.CropEnabled && (videoCopy || audioCopy)
                ? "Stream-copy trimming starts on keyframes and may be slightly offset." : null);
    }

    private static double NormalizePlaybackSpeed(double speed) =>
        double.IsFinite(speed) ? Math.Clamp(speed, 0.25, 3) : 1;

    private static string BuildTempoFilter(double speed)
    {
        if (Math.Abs(speed - 1) < 0.0001)
            return "anull";

        var filters = new List<string>();
        var remaining = speed;
        while (remaining > 2)
        {
            filters.Add("atempo=2");
            remaining /= 2;
        }
        while (remaining < 0.5)
        {
            filters.Add("atempo=0.5");
            remaining /= 0.5;
        }
        if (Math.Abs(remaining - 1) > 0.0001)
            filters.Add($"atempo={remaining.ToString("0.###", CultureInfo.InvariantCulture)}");
        return string.Join(',', filters);
    }

    public static void Validate(ExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SourcePath) || string.IsNullOrWhiteSpace(options.OutputPath))
            throw new ArgumentException("Choose a source and output location first.");
        if (!double.IsFinite(options.Duration) || options.Duration <= 0)
            throw new ArgumentException("The source duration is unavailable.");
        if (options.CropEnabled && (!double.IsFinite(options.Start) || !double.IsFinite(options.End) ||
            options.Start < 0 || options.End <= options.Start || options.End > options.Duration))
            throw new ArgumentException("The crop end must be after its start and within the source duration.");
        if (!options.VideoAdvanced && options.VideoCodec != ExportNames.Copy)
            VideoRateControl.Validate(options.VideoRateControl, options.VideoBitrate, options.VideoQuality,
                options.VideoCodec, options.UseGpu);
        if (options.Tracks.Any(t => t.Enabled && (!double.IsFinite(t.VolumePercent) || t.VolumePercent < 0 || t.VolumePercent > 150)))
            throw new ArgumentException("Audio volume must be between 0 and 150 percent.");
        if (options.Tracks.Any(t => t.Enabled && t.SourceIndex < 0 && t.AudioOrdinal < 0))
            throw new ArgumentException("An audio track has no valid stream selector.");
        // Parse advanced arguments here so an unclosed quote is reported on submission.
        if (options.VideoAdvanced) ParseArguments(options.VideoAdvancedText);
        if (options.AudioAdvanced && options.Tracks.Any(t => t.Enabled)) ParseArguments(options.AudioAdvancedText);
        if (options.CopyPlaybackSpeed && !CanCopySpeed(options))
            throw new ArgumentException("Copy playback speed cannot be combined with stream copy or custom maps and filters.");
    }

    public static bool CanCopySpeed(ExportOptions options)
    {
        try
        {
            var video = options.VideoAdvanced ? ParseArguments(options.VideoAdvancedText) : [];
            var audio = options.AudioAdvanced && options.Tracks.Any(t => t.Enabled)
                ? ParseArguments(options.AudioAdvancedText) : [];
            return (options.VideoAdvanced || options.VideoCodec != ExportNames.Copy) &&
                   (!options.Tracks.Any(t => t.Enabled) || options.AudioAdvanced || options.AudioCodec != ExportNames.Copy) &&
                   !HasConflictingFlags(video) && !HasConflictingFlags(audio);
        }
        catch (ArgumentException) { return false; }
    }

    private static bool HasConflictingFlags(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var flag = tokens[i].Split('=')[0];
            if (flag is "-map" or "-filter_complex" or "-filter_complex_script" or "-vf" or "-af" ||
                flag == "-filter" || flag.StartsWith("-filter:", StringComparison.Ordinal))
                return true;
            if (flag is "-c" or "-codec" || flag.StartsWith("-c:", StringComparison.Ordinal) ||
                flag.StartsWith("-codec:", StringComparison.Ordinal))
            {
                var equals = tokens[i].IndexOf('=');
                var value = equals >= 0 ? tokens[i][(equals + 1)..] : i + 1 < tokens.Count ? tokens[i + 1] : "";
                if (value.Equals("copy", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static string MapAudioTrack(ExportAudioTrack track) =>
        track.SourceIndex >= 0 ? $"0:{track.SourceIndex}" : $"0:a:{track.AudioOrdinal}";

    // Shell-free tokenizer. Quotes group arguments; literal Windows path separators
    // are preserved. Backslashes before quotes use the conventional odd/even
    // escaping rule; elsewhere (including UNC paths) they remain literal.
    public static IReadOnlyList<string> ParseArguments(string? raw)
    {
        var result = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';
        var started = false;
        raw ??= "";
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '\\')
            {
                var start = i;
                while (i < raw.Length && raw[i] == '\\') i++;
                var count = i - start;
                if (i < raw.Length && (raw[i] == '"' || raw[i] == '\'') && (quote == '\0' || quote == raw[i]))
                {
                    token.Append('\\', count / 2);
                    if (count % 2 != 0) token.Append(raw[i]);
                    else quote = quote == '\0' ? raw[i] : '\0';
                }
                else { token.Append('\\', count); i--; }
                started = true;
            }
            else if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else token.Append(c);
            }
            else if (c is '"' or '\'') { quote = c; started = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (started) { result.Add(token.ToString()); token.Clear(); started = false; }
            }
            else { token.Append(c); started = true; }
        }
        if (quote != '\0') throw new ArgumentException("An advanced argument has an unclosed quote.");
        if (started) result.Add(token.ToString());
        return result.AsReadOnly();
    }
}
