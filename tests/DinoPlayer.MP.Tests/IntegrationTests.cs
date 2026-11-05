using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DinoPlayer.MP.Export;
using DinoPlayer.MP.Playback;

internal static class IntegrationTests
{
    public static async Task Run(bool testNvenc = false)
    {
        var source = Path.Combine(Program.Temp, "Four tracks – café Ω.mkv");
        var silent = Path.Combine(Program.Temp, "No audio – Ω.mp4");
        await Program.Test("generate Unicode multi-track and silent fixtures", async () =>
        {
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30:duration=3" };
            for (var i = 0; i < 4; i++) args.AddRange(["-f", "lavfi", "-i", $"sine=frequency={220 + i * 110}:sample_rate=48000:duration=3"]);
            for (var i = 0; i < 5; i++) args.AddRange(["-map", $"{i}:0"]);
            args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-g", "15", "-c:a", "aac", source]);
            await ProcessAsync("ffmpeg", args);
            await ProcessAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-i", source, "-map", "0:v:0", "-c:v", "copy", silent]);
        });
        if (!File.Exists(source)) return;
        foreach (var codec in new[] { "H.264", "H.265", "AV1" })
            await Program.Test($"FFmpeg CPU {codec} target quality", async () =>
            {
                await ExportAndVerify(Program.Options(0) with
                {
                    SourcePath = silent, OutputPath = Path.Combine(Program.Temp, $"quality-{codec}.mkv"),
                    VideoCodec = codec, VideoPreset = codec == "AV1" ? "10" : "ultrafast",
                    VideoRateControl = "Quality", VideoQuality = "23", CropEnabled = true, Start = 0, End = .3
                }, 0);
            });
        if (testNvenc)
            foreach (var codec in new[] { "H.264", "H.265", "AV1" })
                await Program.Test($"FFmpeg NVENC {codec} target quality", async () =>
                {
                    await ExportAndVerify(Program.Options(0) with
                    {
                        SourcePath = silent, OutputPath = Path.Combine(Program.Temp, $"nvenc-quality-{codec}.mkv"),
                        VideoCodec = codec, UseGpu = true, VideoPreset = "p4",
                        VideoRateControl = "Quality", VideoQuality = "23", CropEnabled = true, Start = 0, End = .3
                    }, 0);
                });
        await Program.Test("FFmpeg 4K output dimensions", async () =>
        {
            var output = Path.Combine(Program.Temp, "quality-4k.mp4");
            await ExportAndVerify(Program.Options(0) with
            {
                SourcePath = silent, OutputPath = output, Resolution = "3840x2160",
                VideoRateControl = "Quality", VideoQuality = "23", CropEnabled = true, Start = 0, End = .1
            }, 0);
            var json = await ProcessAsync("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "json", output]);
            using var document = JsonDocument.Parse(json);
            var video = document.RootElement.GetProperty("streams")[0];
            Program.Check(video.GetProperty("width").GetInt32() == 3840 && video.GetProperty("height").GetInt32() == 2160,
                "4K export dimensions differ from the selected resolution.");
        });
        await Program.Test("FFmpeg fixed resolutions keep the source shape with square pixels", async () =>
        {
            foreach (var (size, expectedWidth) in new[] { ("640x480", 1440), ("860x360", 2580), ("1280x720", 1920) })
            {
                var shaped = Path.Combine(Program.Temp, $"shape-{size}.mp4");
                await ProcessAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=30:duration=1",
                    "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", shaped]);
                var output = Path.Combine(Program.Temp, $"shape-{size}-1080p.mp4");
                await ExportAndVerify(Program.Options(0) with
                {
                    SourcePath = shaped, OutputPath = output, Duration = 1, Resolution = "1920x1080",
                    CropEnabled = true, Start = 0, End = .2
                }, 0);
                var json = await ProcessAsync("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,sample_aspect_ratio", "-of", "json", output]);
                using var document = JsonDocument.Parse(json);
                var video = document.RootElement.GetProperty("streams")[0];
                var sar = video.TryGetProperty("sample_aspect_ratio", out var value) ? value.GetString() : "1:1";
                Program.Check(video.GetProperty("width").GetInt32() == expectedWidth && video.GetProperty("height").GetInt32() == 1080 && sar == "1:1",
                    $"A {size} source exported at 1080p as {video.GetProperty("width")}x{video.GetProperty("height")} with SAR {sar}.");
            }
        });
        var caseNumber = 0;
        foreach (var count in new[] { 0, 1, 4 })
        foreach (var separate in new[] { false, true })
        foreach (var crop in new[] { false, true })
        foreach (var speed in new[] { 0.25, 1, 3 })
        {
            var name = $"FFmpeg {count} audio, {(separate ? "separate" : "mix")}, crop={crop}, speed={speed}";
            var output = Path.Combine(Program.Temp, $"Export café {caseNumber++}.mp4");
            await Program.Test(name, async () =>
            {
                var options = Program.Options(count) with
                {
                    SourcePath = count == 0 ? silent : source, OutputPath = output, CropEnabled = crop, Start = 0.5, End = 2,
                    CopyPlaybackSpeed = speed != 1, PlaybackSpeed = speed, KeepTracksSeparate = separate
                };
                await ExportAndVerify(options, count == 0 ? 0 : separate ? count : 1);
            });
        }
        foreach (var crop in new[] { false, true })
        await Program.Test($"FFmpeg stream copy crop={crop}", async () =>
        {
            await ExportAndVerify(Program.Options(4) with
            {
                SourcePath = source, OutputPath = Path.Combine(Program.Temp, $"copy-{crop}.mkv"),
                VideoCodec = "Copy", AudioCodec = "Copy", CropEnabled = crop, Start = .5, End = 2
            }, 4, tolerance: .65);
        });
        await Program.Test("FFmpeg audio ordinal fallback when mpv stream indices are unavailable", async () =>
        {
            var tracks = Program.Options(4).Tracks.Select(t => t with { SourceIndex = -1 }).ToImmutableArray();
            await ExportAndVerify(Program.Options(4) with
            {
                SourcePath = source, OutputPath = Path.Combine(Program.Temp, "ordinal-fallback.mp4"),
                Tracks = tracks, KeepTracksSeparate = true
            }, 4);
        });
        await Program.Test("FFmpeg advanced quoted metadata and speed", async () =>
        {
            await ExportAndVerify(Program.Options(1) with
            {
                SourcePath = source, OutputPath = Path.Combine(Program.Temp, "advanced.mp4"),
                VideoAdvanced = true, VideoAdvancedText = "-c:v libx264 -preset ultrafast -metadata title=\"A café clip\"",
                AudioAdvanced = true, AudioAdvancedText = "-c:a aac -b:a 128k", CopyPlaybackSpeed = true, PlaybackSpeed = .5
            }, 1);
        });
        await Program.Test("real queued exports preserve independent crop, audio and speed settings", () => QueuedExports(source, silent));
        await Program.Test("real target size exports fit the limit, including a forced second attempt", () => TargetSizeExports(source, testNvenc));
        await Program.Test("native playback snapshots, seeks, rapid switches and teardown", () => NativePlayback(source, silent));
        await Program.Test("imports stay at zero during delayed audio setup and then begin at zero", () => ImportAtZero(source, silent));
        await SeekTests.Run();
    }

    private static async Task ExportAndVerify(ExportOptions options, int expectedAudio, double tolerance = .2)
    {
        var log = new StringBuilder();
        var command = ExportCommandBuilder.Build(options);
        var progress = -1d;
        var result = await new FfmpegRunner().RunAsync("ffmpeg", command, line => log.AppendLine(line),
            value => progress = value, CancellationToken.None);
        Program.Check(result.ExitCode == 0 && result.Error is null, $"Export failed: {result.Error}\n{log}");
        await VerifyOutput(options.OutputPath, command.DurationSeconds, expectedAudio, tolerance);
        Program.Check(progress >= 0, "Machine-readable FFmpeg progress was not received.");
    }

    private static async Task VerifyOutput(string outputPath, double expectedDuration, int expectedAudio, double tolerance = .2)
    {
        var json = await ProcessAsync("ffprobe", ["-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", outputPath]);
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        Program.Check(streams.Count(s => s.GetProperty("codec_type").GetString() == "audio") == expectedAudio, "Wrong audio stream count.");
        Program.Check(streams.Count(s => s.GetProperty("codec_type").GetString() == "video") == 1, "Wrong video stream count.");
        var duration = double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Program.Check(Math.Abs(duration - expectedDuration) <= tolerance, $"Duration {duration} differs from {expectedDuration}.");
        await ProcessAsync("ffmpeg", ["-v", "error", "-i", outputPath, "-map", "0", "-f", "null", "-"]);
    }

    private static async Task QueuedExports(string source, string silent)
    {
        var concurrent = 0;
        var maximum = 0;
        var manager = new ExportManager(async (executable, command, log, progress, cancellation) =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref concurrent));
            try { return await new FfmpegRunner().RunAsync(executable, command, log, progress, cancellation); }
            finally { Interlocked.Decrement(ref concurrent); }
        });
        try
        {
            var output = Path.Combine(Program.Temp, "Queued café Ω.mp4");
            manager.AddToQueue(Program.Options(0) with
            {
                SourcePath = silent, OutputPath = output, CropEnabled = true, Start = .5, End = 1.5,
                CopyPlaybackSpeed = true, PlaybackSpeed = .5
            });
            manager.AddToQueue(Program.Options(4) with
            {
                SourcePath = source, OutputPath = output, CropEnabled = true, Start = .3, End = 2.4,
                CopyPlaybackSpeed = true, PlaybackSpeed = 2, KeepTracksSeparate = true
            });
            manager.AddToQueue(Program.Options(1) with
            {
                SourcePath = source, OutputPath = output, CopyPlaybackSpeed = true, PlaybackSpeed = 1.25,
                Tracks = [new(1, 0, true, 25)]
            });
            manager.StartQueue();
            await manager.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var jobs = manager.GetSnapshot().Jobs;
            Program.Check(maximum == 1 && jobs.All(job => job.State == ExportJobState.Completed && job.Progress == 100),
                $"Queue failed or ran concurrently: {string.Join("\n", jobs.Select(job => job.Log))}");
            var expectedTracks = new[] { 0, 4, 1 };
            for (var index = 0; index < jobs.Length; index++)
            {
                var job = jobs[index];
                await VerifyOutput(job.Definition.Options.OutputPath, job.Definition.Command.DurationSeconds, expectedTracks[index]);
                Program.Check(!File.Exists(job.Definition.TemporaryPath), "Completed queue retained temporary output.");
            }
            Program.Check(jobs.Select(job => job.Definition.Options.OutputPath).Distinct().Count() == 3, "Queue outputs shared a filename.");
        }
        finally { await manager.StopAsync(); }
    }

    private static async Task TargetSizeExports(string source, bool nvenc)
    {
        // The first attempt is padded past the limit, so the real retry path runs FFmpeg again.
        var attempts = 0;
        var manager = new ExportManager(async (executable, command, log, progress, cancellation) =>
        {
            var result = await new FfmpegRunner().RunAsync(executable, command, log, progress, cancellation);
            if (Interlocked.Increment(ref attempts) == 1 && result.ExitCode == 0)
                await using (var file = new FileStream(command.Arguments[^1], FileMode.Open)) file.SetLength(command.SizeLimit!.MaxBytes + 1);
            return result;
        });
        try
        {
            var encoders = new List<(string Codec, bool Gpu, string Preset)> { ("H.264", false, "ultrafast"), ("AV1", false, "10") };
            if (nvenc) encoders.AddRange([("H.264", true, "p4"), ("H.265", true, "p4"), ("AV1", true, "p4")]);
            foreach (var (codec, gpu, preset) in encoders)
                manager.AddToQueue(Program.Options(4) with
                {
                    SourcePath = source, OutputPath = Path.Combine(Program.Temp, $"size-{codec}-{(gpu ? "nvenc" : "cpu")}.mp4"),
                    VideoCodec = codec, UseGpu = gpu, VideoPreset = preset, AudioBitrate = "128k",
                    VideoRateControl = "Size", VideoTargetSize = "0.25"
                });
            manager.StartQueue();
            await manager.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(120));
            var jobs = manager.GetSnapshot().Jobs;
            // CPU AV1 has no rate cap, so it may need another attempt of its own.
            Program.Check(jobs.All(job => job.State == ExportJobState.Completed) && attempts > jobs.Length,
                $"Size exports failed or retried unexpectedly ({attempts} runs):\n{string.Join("\n", jobs.Select(job => job.Log))}");
            Program.Check(jobs[0].Log.Contains("Encoding again"), $"The padded clip was not encoded again:\n{jobs[0].Log}");
            foreach (var job in jobs)
            {
                Program.Check(new FileInfo(job.Definition.Options.OutputPath).Length <= 250_000, $"{Path.GetFileName(job.Definition.Options.OutputPath)} is over 0.25 MB.");
                await VerifyOutput(job.Definition.Options.OutputPath, job.Definition.Command.DurationSeconds, 1);
            }
        }
        finally { await manager.StopAsync(); }
    }

    private static async Task ImportAtZero(string source, string silent)
    {
        using var engine = new MpvPlaybackEngine(IntPtr.Zero, "import-test", headless: true);
        using var probe = new NativePlaybackProbe(engine);
        foreach (var path in new[] { source, silent, source })
        {
            await engine.OpenAsync(path, startPaused: true);
            await Until(() => engine.GetSnapshot().IsLoaded && engine.GetSnapshot().Generation == engine.Generation);
            // Simulate slow preset/UI setup. The old loading path would already
            // be playing here, consuming the beginning before audio was ready.
            await Task.Delay(600);
            Program.Check(probe.IsPaused && probe.Position < .08,
                $"Import advanced during setup: position={probe.Position}; paused={probe.IsPaused}.");
            foreach (var track in engine.AudioTracks) track.VolumePercent = 37;
            await engine.ApplyAudioMixAsync();
            await engine.SeekAndWaitAsync(0);
            Program.Check(!probe.IsSeeking && probe.Position < .08, "Prepared import was released before its starting frame was ready.");
            Program.Check(probe.IsPaused, "Audio setup unexpectedly started playback.");
            await engine.SetPausedAsync(false);
            await Until(() => probe.Position > .15);
            Program.Check(probe.Position < 1, $"Prepared import skipped its beginning: {probe.Position}.");
        }
    }

    private static async Task NativePlayback(string source, string silent)
    {
        using var engine = new MpvPlaybackEngine(IntPtr.Zero, "test", headless: true);
        var errors = new List<PlaybackError>();
        engine.Error += error => { lock (errors) errors.Add(error); };
        await engine.OpenAsync(source);
        await Until(() => engine.GetSnapshot().IsLoaded && engine.AudioTracks.Count == 4);
        Program.Check(engine.GetSnapshot().Generation == engine.Generation, "Snapshot generation mismatched.");
        await engine.ApplyAudioMixAsync();
        await engine.SetPausedAsync(true);
        await Until(() => engine.GetSnapshot().IsPaused);
        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => engine.SeekAsync(i / 10d)));
        await Until(() => Math.Abs(engine.GetSnapshot().PositionSeconds - 2) < .15);
        Program.Check(engine.GetSnapshot().IsPaused, "Seeking resumed paused playback.");
        for (var i = 0; i < 10; i++)
        {
            foreach (var track in engine.AudioTracks) track.VolumePercent = 20 + i;
            await engine.ApplyAudioMixAsync();
        }
        await Until(() => (bool)typeof(MpvPlaybackEngine).GetField("_videoReady",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(engine)!);
        var concurrentMixes = new List<Task>();
        for (var i = 0; i < 100; i++)
        {
            foreach (var track in engine.AudioTracks) track.VolumePercent = 30 + i;
            concurrentMixes.Add(engine.ApplyAudioMixAsync());
        }
        await Task.WhenAll(concurrentMixes);
        var nativeWorker = (PlaybackWorker)typeof(MpvPlaybackEngine).GetField("_worker",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(engine)!;
        var graph = await nativeWorker.InvokeAsync(() => (string?)typeof(MpvPlaybackEngine).GetMethod("GetStringNative",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(engine, ["lavfi-complex"]));
        Program.Check(graph?.Contains("volume=1.29", StringComparison.Ordinal) == true,
            "An older audio mix replaced the latest slider values.");
        await engine.AdjustPlaybackSpeedAsync(1);
        await Until(() => Math.Abs(engine.GetSnapshot().PlaybackSpeed - 1.1) < .01);
        // Hold native work deliberately. GetSnapshot must remain a pure cached read.
        var worker = (PlaybackWorker)typeof(MpvPlaybackEngine).GetField("_worker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(engine)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var busy = worker.InvokeAsync(() => { entered.Set(); release.Wait(); return 0; });
        Program.Check(entered.Wait(2000), "Could not hold the native worker.");
        var clock = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        try { for (var i = 0; i < 100_000; i++) _ = engine.GetSnapshot(); }
        finally { release.Set(); }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        await busy;
        Console.WriteLine($"MEASURE: 100,000 cached snapshot reads: {clock.Elapsed.TotalMilliseconds:0.00} ms; {bytes} bytes allocated.");
        Program.Check(clock.Elapsed < TimeSpan.FromSeconds(1), "Snapshots waited on the busy native worker.");
        var requests = new List<Task>();
        for (var i = 0; i < 12; i++) requests.Add(engine.OpenAsync(i % 2 == 0 ? source : silent));
        await Task.WhenAll(requests);
        await Until(() => engine.GetSnapshot().IsLoaded && engine.GetSnapshot().Generation == engine.Generation && engine.AudioTracks.Count == 0);
        await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync();
        Program.Throws<ObjectDisposedException>(() => engine.SeekAsync(1));
        lock (errors) Program.Check(errors.Count == 0, string.Join("; ", errors.Select(e => e.Message)));
        using var recreated = new MpvPlaybackEngine(IntPtr.Zero, "test", headless: true);
        await recreated.OpenAsync(source);
        await Until(() => recreated.GetSnapshot().IsLoaded);
        await recreated.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task Until(Func<bool> condition)
    {
        var start = Stopwatch.StartNew();
        while (!condition())
        {
            if (start.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Playback state did not settle.");
            await Task.Delay(20);
        }
    }

    internal static async Task<string> ProcessAsync(string executable, IEnumerable<string> args)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true } };
        foreach (var argument in args) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        var error = await stderr;
        Program.Check(process.ExitCode == 0, $"{executable} failed: {error}");
        return await stdout;
    }
}
