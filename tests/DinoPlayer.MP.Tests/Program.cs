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
                Check(Value(scaled.Arguments, "-filter_complex")!.Contains("scale=3840:2160"), "4K dimensions were not passed to FFmpeg.");
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
            await Test("worker shutdown rejects queued work and destroys once", WorkerShutdown);
            await Test("worker failure completes callers and rejects later native calls", WorkerFailure);
            await Test("FFmpeg cancellation escalates and can be repeated", Cancellation);
            await Test("FFmpeg missing executable is reported", async () =>
            {
                var result = await new FfmpegRunner().RunAsync(Path.Combine(Temp, "missing-ffmpeg"), new([], 1, null), _ => { }, _ => { }, default);
                Check(result.Error is not null, "Missing FFmpeg was reported as success.");
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
