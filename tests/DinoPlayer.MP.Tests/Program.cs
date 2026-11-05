using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using DinoPlayer.MP;
using DinoPlayer.MP.Export;
using DinoPlayer.MP.Playback;
using DinoPlayer.MP.Settings;

internal static class Program
{
    internal static readonly string Temp = Path.Combine(Path.GetTempPath(), "dinoplayer-tests-" + Guid.NewGuid().ToString("N"));
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--benchmark") return PlaybackBenchmark.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--seek-benchmark") return SeekBenchmark.Run(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--ui") return UiTests.Run(args[1]);
        if (args.Contains("--leaf")) { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
        if (args.Contains("--stubborn"))
        {
            using var child = Process.Start(new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                ArgumentList = { typeof(Program).Assembly.Location, "--leaf" }
            });
            Console.Error.WriteLine("ready");
            await Task.Delay(TimeSpan.FromSeconds(30));
            return 0;
        }
        Directory.CreateDirectory(Temp);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Temp);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Temp);
        SettingsStore.Initialize();
        try
        {
            if (args.Contains("--seeks"))
            {
                await SeekTests.Run();
                Console.WriteLine($"RESULT: {_passed} passed; {_failed} failed. Artifacts: {Temp}");
                return _failed == 0 ? 0 : 1;
            }
            await Test("portable preferences stay beside the executable and embedded input bridge repairs its cache", () =>
            {
                var app = Path.Combine(Temp, "portable app Ω");
                Directory.CreateDirectory(app);
                var normal = Path.Combine(Temp, "configuration", "DinoPlayer", "settings.json");
                Check(RuntimePaths.ResolveSettingsPath(app, Path.Combine(Temp, "configuration"), Temp) == normal,
                    "Normal builds lost their configured preference location.");
                var portable = Path.Combine(app, "settings.json");
                File.WriteAllText(portable, "{\"version\":6}");
                Check(RuntimePaths.ResolveSettingsPath(app, Path.Combine(Temp, "configuration"), Temp) == portable,
                    "Portable preferences did not override the profile location.");
                var repository = new SettingsRepository(portable);
                repository.SavePreferences(null, Temp, new(), editableCropTimes: true);
                Check(new SettingsRepository(portable).Snapshot.General.EditableCropTimes, "Portable preferences did not persist.");
                var bridge = EmbeddedInputBridge.GetPath();
                var original = File.ReadAllBytes(bridge);
                Check(original.Length > 0 && Encoding.UTF8.GetString(original).Contains("dinoplayer_mouse_double_click"),
                    "The embedded input bridge did not extract.");
                File.WriteAllText(bridge, "damaged cache");
                Check(EmbeddedInputBridge.GetPath() == bridge && File.ReadAllBytes(bridge).SequenceEqual(original),
                    "A damaged input bridge cache was reused.");
                var staleBridge = Path.Combine(Path.GetDirectoryName(bridge)!, "mpv-input-00000000000000000000000000000000.lua");
                File.WriteAllText(staleBridge, "old build");
                Check(EmbeddedInputBridge.GetPath() == bridge && !File.Exists(staleBridge) && File.Exists(bridge),
                    "An input bridge from an earlier build was left in the cache.");
                return Task.CompletedTask;
            });
            await Test("old diagnostic logs are pruned, newest first", () =>
            {
                var logs = Directory.CreateDirectory(Path.Combine(Temp, "log-retention")).FullName;
                var start = DateTime.UtcNow.AddDays(-30);
                for (var i = 0; i < 25; i++)
                {
                    var file = Path.Combine(logs, $"dinoplayer-mp-{i:D2}.log");
                    File.WriteAllText(file, "log");
                    File.SetLastWriteTimeUtc(file, start.AddHours(i));
                }
                File.WriteAllText(Path.Combine(logs, "unrelated.txt"), "keep");
                AppLog.PruneOldLogs(logs, AppLog.RetainedLogFiles - 1);
                var kept = Directory.GetFiles(logs, "dinoplayer-mp-*.log").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
                Check(kept.Length == AppLog.RetainedLogFiles - 1 && kept[0] == "dinoplayer-mp-06.log" && kept[^1] == "dinoplayer-mp-24.log" &&
                      File.Exists(Path.Combine(logs, "unrelated.txt")), $"Log pruning kept the wrong files: {string.Join(", ", kept)}");
                return Task.CompletedTask;
            });
            await Test("export filters and speed range", () =>
            {
                foreach (var count in new[] { 0, 1, 4 })
                foreach (var speed in new[] { 0.25, 0.5, 1, 1.5, 2, 3 })
                foreach (var separate in new[] { false, true })
                {
                    var options = Options(count) with { CopyPlaybackSpeed = speed != 1, PlaybackSpeed = speed, KeepTracksSeparate = separate };
                    var command = ExportCommandBuilder.Build(options);
                    var filter = Value(command.Arguments, "-filter_complex");
                    Check(filter is null || !filter.Contains("],"), "An empty filter was generated.");
                    Check(Math.Abs(command.DurationSeconds - 3 / speed) < .001, "Wrong export duration.");
                    Check(command.Arguments.Count(a => a == "-map") == 1 + (count == 0 ? 0 : separate ? count : 1), "Wrong stream mapping.");
                }
                return Task.CompletedTask;
            });
            await Test("empty and invalid crops cannot export the full source", () =>
            {
                foreach (var (start, end) in new[] { (1d, 1d), (2d, 1d), (-1d, 2d), (0d, 4d), (double.NaN, 2d), (1d, double.PositiveInfinity) })
                    Throws<ArgumentException>(() => ExportCommandBuilder.Build(Options() with { CropEnabled = true, Start = start, End = end }));
                var crop = ExportCommandBuilder.Build(Options() with { CropEnabled = true, Start = 1, End = 2 });
                Check(crop.DurationSeconds == 1 && Value(crop.Arguments, "-ss") == "1", "Crop bounds were lost.");
                return Task.CompletedTask;
            });
            await Test("inactive advanced fields and inactive simple copy settings", () =>
            {
                var options = Options() with { CopyPlaybackSpeed = true, VideoAdvancedText = "-c:v copy -map 0", AudioAdvancedText = "\"unclosed" };
                ExportCommandBuilder.Build(options);
                Check(ExportCommandBuilder.CanCopySpeed(options), "Hidden flags blocked speed export.");
                var active = options with { VideoAdvanced = true, VideoAdvancedText = "-c:v libx264", VideoCodec = "Copy" };
                ExportCommandBuilder.Build(active);
                Check(!ExportCommandBuilder.CanCopySpeed(active with { VideoAdvancedText = "-c:v:0 copy" }), "Active copy was allowed.");
                Check(!ExportCommandBuilder.CanCopySpeed(active with { VideoAdvancedText = "-vf scale=64:64" }), "Conflicting filters were allowed.");
                return Task.CompletedTask;
            });
            await Test("quoted advanced arguments and finite bitrate validation", () =>
            {
                var tokens = ExportCommandBuilder.ParseArguments("-metadata title=\"café clip\" -vf 'scale=64:64,drawtext=text=hello world' -metadata path=\"C:\\Videos\\clip.mp4\"");
                Check(tokens.Count == 6 && tokens[1] == "title=café clip" && tokens[3].EndsWith("hello world"), "Quotes were split.");
                Check(tokens[5] == "path=C:\\Videos\\clip.mp4", "Windows path separators changed.");
                Check(ExportCommandBuilder.ParseArguments("-metadata path=\"\\\\server\\share\\clip.mp4\"")[1] == "path=\\\\server\\share\\clip.mp4", "UNC path separators changed.");
                Check(ExportCommandBuilder.ParseArguments("-metadata title=\"\"")[1] == "title=", "Empty quoted value lost.");
                Throws<ArgumentException>(() => ExportCommandBuilder.ParseArguments("-metadata 'unfinished"));
                foreach (var bitrate in new[] { "NaN", "Infinity", "1e999", "0", "-1", "oops" })
                    Throws<ArgumentException>(() => ExportCommandBuilder.Build(Options() with { VideoBitrate = bitrate }));
                return Task.CompletedTask;
            });
            await Test("bitrate and quality commands for every CPU and NVENC codec", () =>
            {
                foreach (var codec in new[] { "H.264", "H.265", "AV1" })
                foreach (var gpu in new[] { false, true })
                {
                    var quality = ExportCommandBuilder.Build(Options() with
                    {
                        VideoCodec = codec, UseGpu = gpu, VideoPreset = gpu ? "p4" : codec == "AV1" ? "8" : "ultrafast",
                        VideoRateControl = "Quality", VideoQuality = "19", VideoBitrate = "invalid inactive value"
                    }).Arguments;
                    Check(Value(quality, gpu ? "-cq" : "-crf") == "19", "Target quality was lost.");
                    Check(Value(quality, gpu ? "-crf" : "-cq") is null, "Quality uses the wrong encoder option.");
                    Check(Value(quality, "-b:v") == (gpu ? "0" : null), "Quality inherited a target bitrate.");
                    Check(Value(quality, "-rc") == (gpu ? "vbr" : null), "NVENC quality did not select VBR.");
                    var bitrate = ExportCommandBuilder.Build(Options() with { VideoCodec = codec, UseGpu = gpu, VideoBitrate = "6.5", VideoQuality = "invalid inactive value" }).Arguments;
                    Check(Value(bitrate, "-b:v") == "6.5M" && Value(bitrate, "-crf") is null && Value(bitrate, "-cq") is null, "Bitrate behavior changed.");
                }
                return Task.CompletedTask;
            });
            await Test("quality ranges, active-mode validation and Copy/Advanced isolation", () =>
            {
                foreach (var codec in new[] { "H.264", "H.265", "AV1" })
                foreach (var gpu in new[] { false, true })
                {
                    var (minimum, maximum) = VideoRateControl.QualityRange(codec, gpu);
                    var options = Options() with { VideoCodec = codec, UseGpu = gpu, VideoRateControl = "Quality" };
                    foreach (var value in new[] { minimum.ToString(), maximum.ToString() })
                        ExportCommandBuilder.Build(options with { VideoQuality = value });
                    foreach (var value in new[] { (minimum - 1).ToString(), (maximum + 1).ToString(), "23.5", "NaN", "Infinity", "", "oops" })
                        Throws<ArgumentException>(() => ExportCommandBuilder.Build(options with { VideoQuality = value }));
                }
                var ignored = Options() with { VideoRateControl = "bad", VideoQuality = "bad", VideoBitrate = "bad" };
                ExportCommandBuilder.Build(ignored with { VideoCodec = "Copy" });
                var advanced = ExportCommandBuilder.Build(ignored with { VideoAdvanced = true, VideoAdvancedText = "-c:v libx264 -crf 17" }).Arguments;
                Check(Value(advanced, "-crf") == "17" && Value(advanced, "-b:v") is null, "Simple settings leaked into Advanced.");
                Throws<ArgumentException>(() => ExportCommandBuilder.Build(ignored));
                Throws<ArgumentException>(() => SettingsStore.AddOrUpdateExportPreset(new() { Name = "bad quality", VideoRateControl = "Quality", VideoQuality = "52" }));
                SettingsStore.AddOrUpdateExportPreset(new() { Name = "quality", VideoRateControl = "Quality", VideoQuality = "19", VideoBitrate = "NaN" });
                Check(SettingsStore.FindExportPreset("quality")!.VideoQuality == "19", "Saving quality validated inactive bitrate.");
                return Task.CompletedTask;
            });
            await Test("audio mix keeps track routing and gains apart so volumes change live", () =>
            {
                static AudioTrack Track(long id, bool enabled, double volume) => new() { Id = id, Label = $"Track {id}", Enabled = enabled, VolumePercent = volume };
                var culture = System.Globalization.CultureInfo.CurrentCulture;
                System.Globalization.CultureInfo.CurrentCulture = new("de-DE");
                try
                {
                    var none = AudioMixGraph.Build([]);
                    Check(none is { Complex: "", Filters: "", TrackIds.IsEmpty: true }, "A file without audio produced a graph.");
                    var one = AudioMixGraph.Build([Track(2, true, 33.33)]);
                    Check(one.Complex == "[aid2]aformat=channel_layouts=stereo[ao]" && one.Filters == "@dinotracks:lavfi=[volume@t0=0.333]",
                        $"Wrong single-track mix: {one.Complex} / {one.Filters}");
                    // Unchecked tracks stay in the graph at gain 0, so checking them is a live volume change.
                    var three = AudioMixGraph.Build([Track(1, true, 50), Track(2, false, 80), Track(3, true, 200)]);
                    Check(three.Complex == "[aid1]aformat=channel_layouts=stereo[s0];[aid2]aformat=channel_layouts=stereo[s1];" +
                          "[aid3]aformat=channel_layouts=stereo[s2];[s0][s1][s2]amerge=inputs=3[ao]", $"Wrong track routing: {three.Complex}");
                    Check(three.Filters == "@dinotracks:lavfi=[asplit=3[i0][i1][i2];[i0]pan=stereo|c0=c0|c1=c1,volume@t0=0.5[m0];" +
                          "[i1]pan=stereo|c0=c2|c1=c3,volume@t1=0[m1];[i2]pan=stereo|c0=c4|c1=c5,volume@t2=1.5[m2];[m0][m1][m2]amix=inputs=3:normalize=0]",
                        $"Wrong gain graph: {three.Filters}");
                    Check(three.TrackIds.SequenceEqual([1L, 2, 3]) && three.Gains.SequenceEqual([.5, 0, 1.5]), "Wrong tracks or clamped gains.");
                    Check(AudioMixGraph.Build([Track(1, true, double.NaN)]).Gains.Single() == 0, "A non-finite volume was not silenced.");
                    var toggled = AudioMixGraph.Build([Track(1, false, 50), Track(2, true, 80), Track(3, true, 100)]);
                    Check(toggled.HasSameTracks(three) && toggled.Gains.SequenceEqual([0, .8, 1]) && toggled.Complex == three.Complex,
                        "Checking or unchecking a track changed the graph.");
                    Check(!toggled.HasSameTracks(one) && !three.HasSameTracks(none), "Different track sets were treated as the same.");
                    Check(AudioMixGraph.VolumeFilterName(2) == "volume@t2" && AudioMixGraph.FormatGain(1.25) == "1.25", "Live command names or values changed.");
                }
                finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
                return Task.CompletedTask;
            });
            await Test("target size bitrate follows clip length, audio and encoder caps", () =>
            {
                var options = Options(2) with { VideoRateControl = "Size", VideoTargetSize = "10", Duration = 20, AudioBitrate = "192k" };
                var plan = TargetSize.Plan(options);
                // 10 MB, minus container overhead and one 192 kbps mixed track, over 20 s plus a 2 s rate buffer.
                var videoBits = (10_000_000 - 32_000 - 2_000 * 20) * 8 - 192_000 * 20 * 1.02;
                var expected = (int)(videoBits / 22 / 1000);
                Check(plan is { MaxBytes: 10_000_000, Seconds: 20, Attempt: 1 } && plan.VideoKbps == expected, $"Wrong plan: {plan}, expected {expected} kbps.");
                var command = ExportCommandBuilder.Build(options);
                Check(command.SizeLimit == plan && Value(command.Arguments, "-b:v") == $"{expected}k" &&
                      Value(command.Arguments, "-maxrate") == $"{expected}k" && Value(command.Arguments, "-bufsize") == $"{expected * 2}k",
                    "x264 size export lost its bitrate cap.");
                Check(TargetSize.Plan(options with { KeepTracksSeparate = true }).VideoKbps < expected - 150, "Separate tracks did not reserve more audio.");
                Check(TargetSize.Plan(options with { AudioBitrate = "128k" }).VideoKbps > expected, "A lower audio bitrate did not leave more for video.");
                Check(TargetSize.Plan(options with { CopyPlaybackSpeed = true, PlaybackSpeed = 2 }).Seconds == 10 &&
                      TargetSize.Plan(options with { CropEnabled = true, Start = 4, End = 9 }).Seconds == 5, "Size plan used the wrong clip length.");
                foreach (var codec in new[] { "H.264", "H.265", "AV1" })
                foreach (var gpu in new[] { false, true })
                {
                    var args = ExportCommandBuilder.Build(options with { VideoCodec = codec, UseGpu = gpu, VideoPreset = gpu ? "p4" : codec == "AV1" ? "8" : "medium" }).Arguments;
                    var capped = gpu || codec != "AV1";
                    Check((Value(args, "-maxrate") is not null) == capped && (Value(args, "-bufsize") is not null) == capped,
                        $"{codec} {(gpu ? "NVENC" : "CPU")} has the wrong rate cap.");
                    Check(Value(args, "-rc") == (gpu ? "vbr" : null) && Value(args, "-crf") is null && Value(args, "-cq") is null,
                        $"{codec} {(gpu ? "NVENC" : "CPU")} size export used another rate mode.");
                }
                // SVT-AV1 has no cap, so its plan leaves out the buffer.
                Check(TargetSize.Plan(options with { VideoCodec = "AV1" }).VideoKbps == (int)(videoBits / 20 / 1000), "CPU AV1 reserved a rate buffer.");
                Check(TargetSize.Plan(options with { VideoTargetSize = "100000", Duration = 3 }).VideoKbps == TargetSize.MaximumVideoKbps, "Huge sizes were not capped.");
                Check(ExportCommandBuilder.Build(Options() with { VideoTargetSize = "bad" }).SizeLimit is null &&
                      ExportCommandBuilder.Build(Options() with { VideoRateControl = "Quality" }).SizeLimit is null, "Other modes received a size limit.");
                foreach (var size in new[] { "0", "-1", "NaN", "Infinity", "1e999", "", "oops", "0.05" })
                    Throws<ArgumentException>(() => ExportCommandBuilder.Build(options with { VideoTargetSize = size }));
                foreach (var audio in new[] { options with { AudioCodec = "FLAC" }, options with { AudioCodec = "Copy" }, options with { AudioAdvanced = true, AudioAdvancedText = "-c:a aac" } })
                    Throws<ArgumentException>(() => ExportCommandBuilder.Build(audio));
                ExportCommandBuilder.Build(Options(0) with { VideoRateControl = "Size", AudioCodec = "FLAC" });
                ExportCommandBuilder.Build(options with { VideoCodec = "Copy", VideoTargetSize = "bad" });
                Check(TargetSize.Describe(options with { VideoTargetSize = "2" }).Contains("720p") &&
                      !TargetSize.Describe(options with { VideoTargetSize = "2", Resolution = "1280x720" }).Contains("720p") &&
                      !TargetSize.Describe(options).Contains("720p") && TargetSize.Describe(options).Contains("Mbps of video for this 00:20 clip"),
                    $"Unexpected size hint: {TargetSize.Describe(options)}");
                Throws<ArgumentException>(() => SettingsStore.AddOrUpdateExportPreset(new() { Name = "bad size", VideoRateControl = "Size", VideoTargetSize = "0" }));
                SettingsStore.AddOrUpdateExportPreset(new() { Name = "Discord", VideoRateControl = "Size", VideoTargetSize = "9.5", VideoBitrate = "NaN" });
                Check(SettingsStore.FindExportPreset("Discord") is { VideoRateControl: "Size", VideoTargetSize: "9.5" }, "Size preset did not save.");
                return Task.CompletedTask;
            });
            await Test("target size retries lower the bitrate and stop when it stops helping", () =>
            {
                var limit = new SizeLimit(10_000_000, 4000, 20, OtherBytes: 1_000_000);
                Check(TargetSize.Check(limit, 10_000_000, null) is { Fits: true, Retry: null } fits && fits.Message.Contains("10.00 MB"), "A file at the limit did not fit.");
                Check(TargetSize.Check(limit, 9_999_999, null).Message.Contains("9.99 MB") &&
                      TargetSize.Check(limit, 10_000_001, null).Message.Contains("10.01 MB, over the 10 MB limit"), "Sizes near the limit were rounded towards it.");
                // 10 MB of video should have been 8.7 MB: 3% under the limit, less 1 MB of audio.
                var over = TargetSize.Check(limit, 11_000_000, null);
                Check(over is { Fits: false, Retry: { Attempt: 2, VideoKbps: 3480, OtherBytes: 1_000_000 } } && over.Message.Contains("3.5 Mbps"), $"Wrong retry: {over}");
                Check(TargetSize.Check(limit, 10_100_000, null).Retry?.VideoKbps == 3600, "A small overshoot did not lower the bitrate by 10%.");
                Check(TargetSize.Check(limit with { Attempt = 2 }, 10_900_000, 11_000_000) is { Retry: null } stuck && stuck.Message.Contains("did not make it smaller"), "A stuck encoder kept retrying.");
                Check(TargetSize.Check(limit with { Attempt = 3 }, 10_500_000, 12_000_000) is { Retry: null } last && last.Message.Contains("after 3 attempts"), "Retries did not stop.");
                Check(TargetSize.Check(new SizeLimit(1_000_000, 110, 20, 100_000), 2_000_000, null) is { Fits: false, Retry: null }, "A retry went below the minimum bitrate.");
                return Task.CompletedTask;
            });
            await Test("settings normalization and detached snapshots", () =>
            {
                var path = Path.Combine(Temp, "malformed.json");
                File.WriteAllText(path, """{"version":2,"general":null,"playbackShortcuts":null,"exportPresets":[null,{"name":""},{"name":"Good","videoBitrate":"NaN","videoAdvancedText":null}],"audio":{"trackPresets":[null,{"trackCount":1,"tracks":[null]},{"trackCount":1,"tracks":[{"volumePercent":200}]}]}}""");
                var repository = new SettingsRepository(path);
                var settings = repository.Snapshot;
                Check(settings.Version == 6 && settings.ExportPresets.Count == 1 && settings.ExportPresets[0].VideoBitrate == "8", "Malformed presets were not normalized.");
                Check(settings.Audio.TrackPresets.Single().Tracks.Single().VolumePercent == 150, "Audio preset normalization failed.");
                settings.General.DefaultOutputFolder = "changed";
                Check(repository.Snapshot.General.DefaultOutputFolder == "", "A snapshot mutated stored settings.");
                File.WriteAllText(path, "{");
                Check(new SettingsRepository(path).Snapshot.Version == 6, "Invalid JSON did not fall back.");
                Check(new SettingsRepository(Temp).Snapshot.Version == 6, "Unreadable settings did not fall back.");
                Throws<ArgumentException>(() => SettingsStore.AddOrUpdateExportPreset(new ExportPreset
                { Name = "bad bitrate", VideoBitrate = "NaN" }));
                return Task.CompletedTask;
            });
            await Test("preferences commit once and failed writes change nothing", () =>
            {
                var path = Path.Combine(Temp, "transaction.json");
                File.WriteAllText(path, """{"version":4,"exportPresets":[{"name":"Test"}]}""");
                var before = File.ReadAllText(path);
                var writes = 0;
                var repository = new SettingsRepository(path, (_, _) => { writes++; throw new IOException("simulated disk failure"); });
                Throws<IOException>(() => repository.SavePreferences("Test", Temp, new(), true, true, true, true));
                Check(writes == 1 && File.ReadAllText(path) == before && repository.Snapshot.General.DefaultOutputFolder == "" && !repository.Snapshot.General.LoopByDefault && !repository.Snapshot.General.ShowDiagnosticLog && !repository.Snapshot.General.ShowPlayerQueueButton && !repository.Snapshot.General.EditableCropTimes, "Failed save partially committed.");
                Throws<ArgumentException>(() => repository.SavePreferences("missing", Temp, new()));
                Check(writes == 1, "Invalid settings reached the writer.");
                var good = new SettingsRepository(path, (target, text) => { writes++; File.WriteAllText(target, text); });
                good.SavePreferences("test", Temp, new(), true, true, true, true);
                Check(writes == 2 && good.Snapshot.General.DefaultExportPresetName == "Test" && good.Snapshot.General.DefaultOutputFolder == Temp && good.Snapshot.General.LoopByDefault && good.Snapshot.General.ShowDiagnosticLog && good.Snapshot.General.ShowPlayerQueueButton && good.Snapshot.General.EditableCropTimes, "Preferences were not committed together.");
                var real = new SettingsRepository(path);
                real.SavePreferences("Test", Temp, new());
                Check(!Directory.EnumerateFiles(Temp, "*.tmp").Any(), "Temporary settings file leaked.");
                return Task.CompletedTask;
            });
            await Test("UI preferences and container migrate and round-trip", () =>
            {
                var path = Path.Combine(Temp, "ui-settings.json");
                File.WriteAllText(path, """{"version":4,"general":{"loopByDefault":true},"exportPresets":[{"name":"Old"},{"name":"Matroska","container":"MKV"},{"name":"Unknown","container":"other"}]}""");
                var repository = new SettingsRepository(path);
                var settings = repository.Snapshot;
                Check(settings.Version == 6 && settings.General.LoopByDefault && !settings.General.ShowDiagnosticLog,
                    "Migration lost playback preference or enabled diagnostics.");
                Check(settings.ExportPresets.Select(p => p.Container).SequenceEqual(new[] { "MP4", "MKV", "MP4" }), "Preset containers did not normalize.");
                repository.SavePreferences("Matroska", Temp, new(), false, true);
                var reloaded = new SettingsRepository(path).Snapshot;
                Check(!reloaded.General.LoopByDefault && reloaded.General.ShowDiagnosticLog && reloaded.ExportPresets[1].Container == "MKV",
                    "UI preferences or container did not persist.");
                return Task.CompletedTask;
            });
            await Test("editable crop times default off, persist and survive unrelated preference saves", () =>
            {
                var path = Path.Combine(Temp, "crop-time-settings.json");
                File.WriteAllText(path, """{"version":6,"general":{"showPlayerQueueButton":true}}""");
                var repository = new SettingsRepository(path);
                Check(!repository.Snapshot.General.EditableCropTimes && !new GeneralSettings().EditableCropTimes,
                    "Existing settings or a new profile enabled crop time editing.");
                repository.SavePreferences(null, Temp, new(), editableCropTimes: true);
                repository.SavePreferences(null, Temp, new(), showDiagnosticLog: true);
                var reloaded = new SettingsRepository(path);
                Check(reloaded.Snapshot.General.EditableCropTimes && reloaded.Snapshot.General.ShowPlayerQueueButton &&
                      reloaded.Snapshot.General.ShowDiagnosticLog, "Crop time editing did not persist or changed another preference.");
                reloaded.SavePreferences(null, Temp, new(), editableCropTimes: false);
                Check(!new SettingsRepository(path).Snapshot.General.EditableCropTimes, "Disabling crop time editing did not persist.");
                return Task.CompletedTask;
            });
            await Test("version 5 presets migrate and 4K quality preferences round-trip", () =>
            {
                var path = Path.Combine(Temp, "quality-settings.json");
                File.WriteAllText(path, """{"version":5,"exportPresets":[{"name":"Legacy","videoBitrate":"12"},{"name":"4K","resolution":"3840x2160","videoRateControl":"Quality","videoQuality":"20"},{"name":"Malformed","videoRateControl":"unknown","videoQuality":"NaN"},{"name":"GPU","useGpu":true,"videoRateControl":"Quality","videoQuality":"0"}]}""");
                var repository = new SettingsRepository(path);
                var before = repository.Snapshot;
                Check(before.Version == 6 && !before.General.ShowPlayerQueueButton, "Migration enabled the optional shortcut.");
                Check(before.ExportPresets[0].VideoRateControl == "Bitrate" && before.ExportPresets[0].VideoBitrate == "12", "Legacy encoding changed.");
                Check(before.ExportPresets[2].VideoRateControl == "Bitrate" && before.ExportPresets[2].VideoQuality == "23" && before.ExportPresets[3].VideoQuality == "23", "Malformed new fields did not normalize.");
                repository.SavePreferences("4K", Temp, new(), showPlayerQueueButton: true);
                var after = new SettingsRepository(path).Snapshot;
                Check(after.General.ShowPlayerQueueButton && after.ExportPresets[1].Resolution == "3840x2160" &&
                      after.ExportPresets[1].VideoRateControl == "Quality" && after.ExportPresets[1].VideoQuality == "20", "4K quality preferences did not persist.");
                var scaled = ExportCommandBuilder.Build(Options() with { Resolution = "3840x2160" });
                Check(Value(scaled.Arguments, "-filter_complex")!.Contains("scale=-2:2160"), "The 4K height was not passed to FFmpeg.");
                Check(Value(ExportCommandBuilder.Build(Options(0)).Arguments, "-filter_complex") is null &&
                      Value(ExportCommandBuilder.Build(Options(0) with { Resolution = "640x480" }).Arguments, "-filter_complex") is null,
                    "Source or unknown resolutions were scaled.");
                return Task.CompletedTask;
            });
            await Test("export name and folder assembly preserve Unicode and selected format", () =>
            {
                Check(ExportDestination.Build(" café Ω.MP4 ", Temp, "MKV") == Path.Combine(Temp, "café Ω.mkv"), "Pasted suffix was duplicated or selected format was ignored.");
                Check(ExportDestination.Build("Replay.2026", Temp, "MP4") == Path.Combine(Temp, "Replay.2026.mp4"), "A dot in the base filename was removed.");
                foreach (var invalid in new[] { "", " ", ".mp4", "../clip", "folder/clip", @"folder\clip", ".", ".." })
                    Throws<ArgumentException>(() => ExportDestination.Build(invalid, Temp, "MP4"));
                Throws<ArgumentException>(() => ExportDestination.Build("clip", "", "MP4"));
                return Task.CompletedTask;
            });
            await Test("newer settings remain unchanged in memory and on disk", () =>
            {
                var path = Path.Combine(Temp, "future.json");
                const string json = """{"version":99,"general":{"defaultOutputFolder":"original"},"futureField":true}""";
                File.WriteAllText(path, json);
                var repository = new SettingsRepository(path);
                Throws<InvalidOperationException>(() => repository.SavePreferences(null, Temp, new()));
                Check(File.ReadAllText(path) == json && repository.Snapshot.General.DefaultOutputFolder == "original", "Future settings changed.");
                return Task.CompletedTask;
            });
            await Test("log subscribers cannot break playback", () =>
            {
                Action<string> bad = _ => throw new IOException("simulated subscriber failure");
                var received = false;
                Action<string> good = _ => received = true;
                AppLog.MessageWritten += bad; AppLog.MessageWritten += good;
                try { AppLog.Info("subscriber isolation test"); Check(received, "One subscriber prevented others from receiving logs."); }
                finally { AppLog.MessageWritten -= bad; AppLog.MessageWritten -= good; }
                return Task.CompletedTask;
            });
            await Test("log lines are written in the background and flushed on demand", () =>
            {
                AppLog.Initialize();
                Check(File.Exists(AppLog.LogPath), "The diagnostic log file was not created.");
                for (var i = 0; i < 500; i++) AppLog.Info($"background log line {i}");
                AppLog.Flush();
                using var stream = new FileStream(AppLog.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var text = new StreamReader(stream).ReadToEnd();
                Check(text.Contains("background log line 0") && text.Contains("background log line 499"), "Queued log lines were not written to the file.");
                return Task.CompletedTask;
            });
            await Test("displayed and pending logs are bounded", () =>
            {
                var log = new BoundedLogBuffer();
                for (var i = 0; i < 100_000; i++) log.Append($"{i}: diagnostic line Ω");
                var text = log.TakeSnapshot()!;
                Check(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length <= BoundedLogBuffer.MaxLines && text.Contains("99999:"), "Line cap or newest lines are wrong.");
                for (var i = 0; i < 100; i++) log.Append(new string('Ω', 10000));
                Check(Encoding.UTF8.GetByteCount(log.TakeSnapshot()!) <= BoundedLogBuffer.MaxBytes, "Byte cap exceeded.");
                log.Append(new string('x', 500000));
                Check(Encoding.UTF8.GetByteCount(log.TakeSnapshot()!) <= BoundedLogBuffer.MaxBytes, "An oversized line exceeded the cap.");
                Check(log.TakeSnapshot() is null, "Unchanged logs were rebuilt.");
                log.Stop(); log.Append("late callback"); Check(log.TakeSnapshot() is null, "Stopped logs accepted callbacks.");
                return Task.CompletedTask;
            });
            await Test("timeline marker work scales with the viewport", () =>
            {
                foreach (var duration in new[] { 3600d, 86400, 864000 })
                {
                    var (first, last) = TimelineViewport.VisibleMinutes(duration, duration * 2, 1000, duration);
                    Check(last - first + 1 <= 27, "Marker count scaled with source duration.");
                    Check(first * 120 <= duration - 50 && last * 120 >= duration + 950 - 120, "Visible timeline markers missing.");
                }
                Check(TimelineViewport.VisibleMinutes(0, 0, 1000, 0).Last == -1, "Empty timeline created markers.");
                return Task.CompletedTask;
            });
            await Test("playhead estimates advance only while playing and stay within bounds", () =>
            {
                var second = Stopwatch.Frequency;
                var sample = PlaybackSnapshot.Empty with
                {
                    PositionSeconds = 10, DurationSeconds = 30, PlaybackSpeed = 2, IsPaused = false, SampleTimestamp = 1000
                };
                Check(Math.Abs(sample.EstimatePosition(1000 + second / 4) - 10.5) < 1e-6, "Playing estimate ignored elapsed time or speed.");
                Check(sample.EstimatePosition(1000 + second * 5) == 12, "A stalled sample was extrapolated beyond one second.");
                Check(sample.EstimatePosition(0) == 10, "An estimate moved backwards before its sample.");
                Check((sample with { IsPaused = true }).EstimatePosition(1000 + second) == 10, "A paused estimate moved.");
                Check((sample with { IsSeeking = true }).EstimatePosition(1000 + second) == 10, "An estimate moved during a seek.");
                Check((sample with { SampleTimestamp = 0 }).EstimatePosition(1000 + second) == 10, "An unsampled estimate moved.");
                Check((sample with { PositionSeconds = 29.8 }).EstimatePosition(1000 + second) == 30, "An estimate passed the end.");
                return Task.CompletedTask;
            });
            await Test("command-line videos accept paths and file URIs, skipping options", () =>
            {
                Check(StartupArguments.FindMediaPath(null) is null && StartupArguments.FindMediaPath(["--verbose", " "]) is null,
                    "Options or blanks were treated as a video.");
                var relative = Path.Combine("clips", "my clip.mkv");
                Check(StartupArguments.FindMediaPath(["-x", relative, "other.mkv"]) == Path.GetFullPath(relative),
                    "A relative path was not resolved from the working directory.");
                var absolute = Path.Combine(Temp, "Gear 100% #1.mkv");
                Check(StartupArguments.FindMediaPath([absolute]) == absolute, "A literal path with URI characters changed.");
                Check(StartupArguments.FindMediaPath([new Uri(absolute).AbsoluteUri]) == absolute, "A file URI was not decoded.");
                return Task.CompletedTask;
            });
            await Test("new player keys are unavailable to unmodified speed shortcuts only", () =>
            {
                foreach (var key in new[] { "Comma", "Period", "I", "O", "M", "ArrowUp", "ArrowDown" })
                {
                    Check(PlaybackShortcut.IsPlayerKey(key, "None"), $"{key} could become a speed shortcut.");
                    Check(!PlaybackShortcut.IsPlayerKey(key, "Ctrl"), $"Ctrl+{key} was blocked.");
                    Check(!PlaybackShortcut.IsReserved(key), $"{key} would reset saved shortcuts on load.");
                }
                Check(PlaybackShortcut.IsPlayerKey("ArrowLeft", "Ctrl") && PlaybackShortcut.IsPlayerKey("Space", "Shift"),
                    "Fixed keys became available with modifiers.");
                Check(!PlaybackShortcut.IsPlayerKey("Equal", "None") && !PlaybackShortcut.IsPlayerKey("BracketLeft", "None"),
                    "Speed defaults or free keys were blocked.");
                return Task.CompletedTask;
            });
            await Test("worker shutdown rejects queued work and destroys once", WorkerShutdown);
            await Test("worker failure completes callers and rejects later native calls", WorkerFailure);
            await Test("FFmpeg cancellation escalates and can be repeated", Cancellation);
            await Test("FFmpeg missing executable is reported", async () =>
            {
                var result = await new FfmpegRunner().RunAsync(Path.Combine(Temp, "missing-ffmpeg"), new([], 1, null), _ => { }, _ => { }, default);
                Check(result.Error?.StartsWith("FFmpeg was not found", StringComparison.Ordinal) == true &&
                      result.Error.Contains("DINOPLAYER_FFMPEG", StringComparison.Ordinal),
                    $"Missing FFmpeg was not explained: {result.Error ?? "success"}");
                if (!OperatingSystem.IsWindows())
                {
                    var notExecutable = Path.Combine(Temp, "not-executable-ffmpeg");
                    File.WriteAllText(notExecutable, "#!/bin/sh\n");
                    result = await new FfmpegRunner().RunAsync(notExecutable, new([], 1, null), _ => { }, _ => { }, default);
                    Check(result.Error?.Contains("chmod +x", StringComparison.Ordinal) == true,
                        $"A non-executable FFmpeg was not explained: {result.Error ?? "success"}");
                }
            });
            await ExportManagerTests.Run();
            if (args.Contains("--integration")) await IntegrationTests.Run(args.Contains("--nvenc"));
            Console.WriteLine($"RESULT: {_passed} passed; {_failed} failed. Artifacts: {Temp}");
            return _failed == 0 ? 0 : 1;
        }
        finally { AppLog.Shutdown(); }
    }

    internal static ExportOptions Options(int count = 1) => new()
    {
        SourcePath = "source Ω.mkv", OutputPath = "output Ω.mp4", Duration = 3, VideoPreset = "ultrafast",
        Tracks = Enumerable.Range(1, count).Select(i => new ExportAudioTrack(i, i - 1, true, 50)).ToImmutableArray()
    };
    internal static string? Value(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++) if (args[i] == flag) return args[i + 1];
        return null;
    }
    internal static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    internal static async Task Test(string name, Func<Task> test)
    {
        try { await test(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception exception) { _failed++; Console.Error.WriteLine($"FAIL {name}: {exception}"); }
    }

    private static async Task WorkerShutdown()
    {
        using var wake = new AutoResetEvent(false);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var destroyed = 0;
        var invalidCalls = 0;
        using var worker = new PlaybackWorker(() => wake.WaitOne(10), () =>
        {
            if (Volatile.Read(ref destroyed) != 0) Interlocked.Increment(ref invalidCalls);
            wake.Set();
        }, () => Interlocked.Increment(ref destroyed), _ => { });
        worker.Start();
        var active = worker.InvokeAsync(() => { entered.Set(); release.Wait(); return 1; });
        Check(entered.Wait(2000), "Worker did not start.");
        var pending = Enumerable.Range(0, 50).Select(_ => worker.InvokeAsync(() => 2)).ToArray();
        var stop = worker.StopAsync();
        try { Check(pending.All(t => t.IsCompleted), "Queued calls did not complete on stop."); }
        finally { release.Set(); }
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Check(await active == 1 && pending.All(t => t.IsFaulted), "Shutdown executed queued native calls.");
        foreach (var task in pending) _ = task.Exception;
        var late = worker.InvokeAsync(() => 3);
        try { await late; throw new Exception("Late call ran."); } catch (ObjectDisposedException) { }
        await worker.StopAsync(); worker.Dispose();
        Check(destroyed == 1 && invalidCalls == 0, "A released native handle was used or destroyed twice.");
    }

    private static async Task WorkerFailure()
    {
        using var entered = new ManualResetEventSlim();
        using var fail = new ManualResetEventSlim();
        var destroyed = 0;
        using var worker = new PlaybackWorker(() => { entered.Set(); fail.Wait(); throw new IOException("native poll failed"); },
            () => { }, () => destroyed++, _ => throw new Exception("error subscriber failed"));
        worker.Start();
        Check(entered.Wait(2000), "Worker did not poll.");
        var pending = worker.InvokeAsync(() => 1);
        fail.Set();
        await worker.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(pending.IsFaulted, "A caller remained waiting after worker failure.");
        _ = pending.Exception;
        var late = worker.InvokeAsync(() => 1);
        Check(late.IsFaulted, "A failed worker accepted work."); _ = late.Exception;
        await worker.StopAsync();
        Check(destroyed == 1, "Failure did not release native resources once.");
    }

    private static async Task Cancellation()
    {
        for (var i = 0; i < 2; i++)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var started = Stopwatch.StartNew();
            var result = await new FfmpegRunner().RunAsync("dotnet",
                new([typeof(Program).Assembly.Location, "--stubborn"], 1, null),
                line => { if (line == "ready") cancel.Cancel(); }, _ => { }, cancel.Token);
            Check(result.Cancelled && result.Error is null, "Cancellation did not finish cleanly.");
            Check(started.Elapsed.TotalSeconds >= 3 && started.Elapsed.TotalSeconds < 8, "Cancellation did not honor the grace period.");
        }
    }
}
