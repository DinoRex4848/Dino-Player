# Developing Dino Player

Run commands from the repository root.

## Requirements

- .NET 10 SDK
- Avalonia Desktop and Fluent theme 12.1.2 (restored from NuGet)
- libmpv: `mpv-libs` on Fedora/Linux, or `libmpv-2.dll` beside the Windows executable
- FFmpeg on `PATH` for exporting clips, or set `DINOPLAYER_FFMPEG` to its full path

No native media binaries are committed to this source workspace.

## Build and run on Fedora/Linux

```bash
cd Dino-Player
dotnet restore DinoPlayer.MP.slnx
dotnet build DinoPlayer.MP.slnx -c Debug --no-restore
dotnet run --project src/DinoPlayer.MP -c Debug --no-build
```

Install the native prerequisites first when needed:

```bash
sudo dnf install dotnet-sdk-10.0 mpv-libs ffmpeg libX11 libICE libSM fontconfig
```

On a Wayland desktop, this build uses an X11 host through XWayland. If libmpv is installed outside the standard locations, set `DINOPLAYER_MPV_LIBRARY` to its absolute path.

## Build and run on Windows

Install the .NET 10 SDK and obtain an x64 `libmpv-2.dll` from a trusted mpv build. Then:

```powershell
cd "C:\path\to\Dino-Player"
dotnet restore .\DinoPlayer.MP.slnx
dotnet build .\DinoPlayer.MP.slnx -c Debug --no-restore
Copy-Item "C:\path\to\libmpv-2.dll" ".\src\DinoPlayer.MP\bin\Debug\net10.0\"
dotnet run --project .\src\DinoPlayer.MP -c Debug --no-build
```

Alternatively, set `DINOPLAYER_MPV_LIBRARY` to the DLL's full path. The app loads libmpv; it does not require `mpv.exe`.

## Publish

Portable downloads are attached to [GitHub Releases](https://github.com/DinoRex4848/Dino-Player/releases). Build outputs and native binaries are intentionally excluded from Git.

For ordinary self-contained folder builds:

```bash
dotnet publish src/DinoPlayer.MP -c Release -r linux-x64 --self-contained true -o publish/linux-x64
dotnet publish src/DinoPlayer.MP -c Release -r win-x64 --self-contained true -o publish/win-x64
```

Supply a compatible libmpv library and FFmpeg separately for these folder layouts.

The app icon comes from the PNG and ICO in `Icons/`. Windows builds embed the ICO in the executable, and every app window and dialog shares the platform-appropriate icon. Linux publishes include the PNG and a desktop launcher installer. After moving the Linux publish folder to its permanent location, run:

```bash
bash publish/linux-x64/install-desktop.sh
```

This installs Dino Player in your applications menu with its icon and matches running windows to the launcher for taskbar/dock pinning. Run it again if you move the app folder. From a source checkout, you can also run `bash scripts/install-linux-desktop.sh /path/to/linux-build`.

Portable build profiles are in `src/DinoPlayer.MP/Properties/PublishProfiles`. To recreate the portable executables, use `-p:PublishProfile=Portable-Windows` or `-p:PublishProfile=Portable-Linux`; Linux additionally requires `-p:DinoPlayerMpvBundleDirectory="/path/to/matching/native/libraries"`. The verified Linux native inputs must be placed in `publish/portable-linux-x64/lib`; they are not included in a source checkout. Copy the external mpv and FFmpeg binaries into their portable staging directories, then run `python3 scripts/package-portable.py` to create and verify both ZIPs. The native dependency set inherited from v1.5.1 is recorded in `packaging/dependencies-v1.5.1.json`; the filename identifies that dependency baseline, not the current app version. Some native inputs have only provenance/hashes recorded and must be supplied separately. Do not substitute a moving `latest` download if its hash differs. The packaging script reads the app version from the project file, verifies the supplied native inputs, and includes the required files, GPL license, third-party notices, and fresh default preferences. Debug symbols and runtime logs are excluded. Python 3.11 or later is required.

## Test media

With FFmpeg installed, create a small four-audio-track fixture for playback and export checks:

```bash
./scripts/create-test-media.sh
```

Generated media, logs, `bin/`, and `obj/` output are intentionally ignored by Git.

## Regression checks

Run the non-graphical regression and FFmpeg integration suite with:

```bash
dotnet run --project tests/DinoPlayer.MP.Tests -- --integration
```

On a machine with NVIDIA hardware supporting H.264, HEVC, and AV1 encoding, append `--nvenc` to include real NVENC quality exports. The regular suite checks NVENC command generation without requiring a GPU.

With a working Linux X11/XWayland display, run the UI smoke checks using a media file:

```bash
dotnet run --project tests/DinoPlayer.MP.Tests -- --ui "/path/to/video.mkv"
```

The integration suite creates media under the system temporary directory and prints its location.

Run just the native seek regressions with `dotnet run --project tests/DinoPlayer.MP.Tests -c Release -- --seeks`. These checks generate 180-second fixtures and cover rapid input, mixed relative/absolute ordering, exact positioning, pause state, boundaries, looping, media changes, seek notifications, deferred audio setup, and shutdown. The integration suite also delays multi-track and silent imports to verify that they stay at zero during setup. The UI suite tests slow multi-track imports, position restoration after native-host recreation, and five-key bursts, skip buttons, and repeated keys in windowed and fullscreen playback. Timeline checks stop the polling timer to verify that native seek events update both timelines independently.

To compare seek responsiveness between saved Release assemblies, use the same media file (at least 180 seconds long), display, and decoding setup:

```bash
dotnet run --project tests/DinoPlayer.MP.Tests -c Release -- --seek-benchmark \
  "/path/to/DinoPlayer.MP.dll" \
  "/path/to/long-keyframe-interval-video.mkv"
```

The `SEEK_BENCHMARK` JSON contains five trials each of a single Right press, five immediate presses, and twelve presses spaced 35 ms apart. Each trial starts paused at 22 seconds after synchronizing the UI position. It records command dispatch latency, completed native playback-restart latency, the remaining time after key release, final position, and decoder/output details. `timelineMs` and `timelineReleaseTailMs` measure when both timeline controls reflect the final native position, sampled every 5 ms; they do not measure display scanout. Playback-restart timing uses a separate libmpv client rather than sampled UI positions. Keyframe seeks intentionally allow different landing times; compare repeatability and input preservation as well as latency. Keep the desktop focused on the test window when running native keyboard checks.

For a repeatable performance run on Linux X11/XWayland, build Release and use the same 4K media file and display for each assembly being compared:

```bash
dotnet run --project tests/DinoPlayer.MP.Tests -c Release -- --benchmark \
  "$(pwd)/src/DinoPlayer.MP/bin/Release/net10.0/DinoPlayer.MP.dll" \
  "/path/to/4k-video.mkv"
```

The `BENCHMARK` JSON reports ten seconds of playback CPU, allocations, UI heartbeat, and dropped frames, followed by a 3,000-line log burst and ten long-timeline rebuilds. Compare repeated runs under the same conditions; hardware decoding, display load, and media content affect results. The UI smoke suite exercises tab switching, background playback, a 250-job queue, master mute restoration, scrollbar hover and dragging, close warnings, fullscreen, resizing, and native-host recreation on Linux. It saves Export-tab screenshots at normal, minimum, and 1080p window sizes in its temporary artifact directory. Windows behavior still needs a run on a Windows machine with libmpv and FFmpeg installed.

## Releasing

1. Update `Version` in the application project and add a changelog entry.
2. Build and run the regression checks; run the UI suite on Linux X11/XWayland.
3. Supply the native inputs matching the dependency manifest. Publish with both portable profiles:

```bash
dotnet publish src/DinoPlayer.MP -c Release -r linux-x64 --self-contained true -p:PublishProfile=Portable-Linux -p:DinoPlayerMpvBundleDirectory="$(pwd)/publish/portable-linux-x64/lib"
dotnet publish src/DinoPlayer.MP -c Release -r win-x64 --self-contained true -p:PublishProfile=Portable-Windows
python3 scripts/package-portable.py
```

4. Extract each ZIP into a fresh directory, verify the checksums and defaults, and smoke-test the target platforms that are available. Record any untested platform in the release notes.
5. Commit the source and documentation. Wait for GitHub Actions to pass, then tag that exact commit `v<Version>`.
6. Create a draft GitHub release, attach both ZIPs and the generated `.sha256` file, verify the uploaded assets, and publish it. Keep generated outputs out of Git.

The Windows staging folder additionally needs `libmpv-2.dll` and `ffmpeg.exe`; the Linux staging folder needs `ffmpeg` plus the matching native library directory. The default `settings.json` comes from `packaging/settings.json`. Release packages include `LICENSE.txt` and component notices in `README.txt`.

## Continuous integration

GitHub Actions restores and builds with .NET 10, runs the custom non-graphical regression executable on Linux and Windows, and runs the FFmpeg/libmpv integration suite on Linux. Use `dotnet run --project tests/DinoPlayer.MP.Tests -- ...` rather than `dotnet test`: this repository uses its own executable test harness. Interactive UI and real NVENC checks require the relevant display or hardware and are not part of hosted CI.
