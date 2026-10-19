#!/usr/bin/env python3
"""Package portable publishes using the version declared by the application."""

from pathlib import Path
import hashlib
import json
import shutil
import zipfile
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
VERSION = ET.parse(ROOT / "src/DinoPlayer.MP/DinoPlayer.MP.csproj").findtext("PropertyGroup/Version")
if not VERSION:
    raise RuntimeError("The application project must declare a Version before packaging.")
PUBLISH = ROOT / "publish"
DEPENDENCIES = json.loads((ROOT / "packaging/dependencies-v1.5.1.json").read_text())


def package(platform: str) -> tuple[Path, str]:
    windows = platform == "Windows"
    source = PUBLISH / ("portable-win-x64" if windows else "portable-linux-x64")
    destination = PUBLISH / f"Dino Player V{VERSION} - {platform} x64"
    destination.mkdir(exist_ok=True)
    files = {
        "DinoPlayer.MP.exe" if windows else "DinoPlayer.MP": "Dino Player.exe" if windows else "Dino Player",
        "settings.json": "settings.json",
    }
    if windows:
        files.update({"libmpv-2.dll": "libmpv-2.dll", "ffmpeg.exe": "ffmpeg.exe"})
    else:
        files.update({"ffmpeg": "ffmpeg", "DinoPlayer.MP.png": "DinoPlayer.MP.png", "install-desktop.sh": "install-desktop.sh"})
    expected = set(files.values()) | {"README.txt", "LICENSE.txt"}
    unexpected = {p.name for p in destination.iterdir()} - expected
    if unexpected:
        raise RuntimeError(f"Refusing to package unexpected files in {destination}: {sorted(unexpected)}")
    for name, output_name in files.items():
        target = destination / output_name
        # Always package pristine portable preferences, even after a staging build was run.
        shutil.copy2(ROOT / "packaging/settings.json" if name == "settings.json" else source / name, target)
        target.chmod(0o755 if output_name in {"Dino Player", "ffmpeg", "install-desktop.sh"} else 0o644)
    shutil.copy2(ROOT / "LICENSE", destination / "LICENSE.txt")
    (destination / "LICENSE.txt").chmod(0o644)
    assert json.loads((destination / "settings.json").read_text())["general"]["defaultOutputFolder"] == ""
    instructions = f"""DINO PLAYER V{VERSION} - {platform.upper()} X64 PORTABLE

Extract the WHOLE ZIP to a writable folder, then run {'Dino Player.exe' if windows else './Dino Player'}.
No .NET, mpv or FFmpeg installation is required.
Preferences and presets are saved in settings.json beside the app.
Diagnostic logs and the playback script cache are created in data/ on first use.
Bundled .NET native libraries are unpacked automatically into the .NET user cache.
Keep the included settings.json file to retain portable mode.

Dino Player source and releases: https://github.com/DinoRex4848/Dino-Player
Dino Player is licensed under GPL-3.0; see LICENSE.txt.
Third-party components retain the licenses and notices listed below.

"""
    if windows:
        instructions += "Requires 64-bit Windows 10 or 11. Keep libmpv-2.dll and ffmpeg.exe beside the app.\n"
        instructions += f"mpv runtime: {DEPENDENCIES['windowsMpv']['version']}\n"
        instructions += f"FFmpeg: {DEPENDENCIES['windowsFfmpeg']['version']}\n"
    else:
        instructions += """Requires a 64-bit Linux desktop with glibc 2.35 or newer (for example Ubuntu 22.04+),
X11 or XWayland, and working system graphics/audio/font libraries and GPU drivers.
mpv and its additional media libraries are embedded in Dino Player.
If ZIP extraction removed executable permissions, run:
  chmod +x "Dino Player" ffmpeg install-desktop.sh
For an applications-menu entry and taskbar/dock pinning, run:
  bash install-desktop.sh
Run the installer again if you move this folder. Launcher registration is optional.
"""
    instructions += """
USING THE PLAYER
Click the import prompt or drop a video onto the window.
Space plays/pauses; Left/Right seek; =/+ and -/_ change speed; F11 toggles fullscreen.
Crop times display as text. Preferences > Enable editable crop times allows typing
HH:MM:SS or HH:MM:SS.mmm; Enter applies an edit and Escape cancels it.
Exports use the included FFmpeg. GPU encoding requires a supported NVIDIA GPU/driver;
CPU encoding is available without NVIDIA hardware.

UPSTREAM COMPONENTS AND SOURCE LOCATIONS
Avalonia: https://github.com/AvaloniaUI/Avalonia/tree/12.1.2
.NET: https://github.com/dotnet/runtime/tree/v10.0.12
SkiaSharp/HarfBuzzSharp: https://github.com/mono/SkiaSharp
mpv: https://github.com/mpv-player/mpv
Windows mpv source revision: https://github.com/mpv-player/mpv/commit/9e06c3248
Windows FFmpeg builds: https://www.gyan.dev/ffmpeg/builds/
Linux mpv runtime/build recipes: https://github.com/mpc-qt/mpc-qt/tree/v26.07
Linux mpv source: https://github.com/mpv-player/mpv/tree/v0.41.0
Linux FFmpeg build recipes: https://github.com/BtbN/FFmpeg-Builds
Linux FFmpeg source revision: https://github.com/FFmpeg/FFmpeg/commit/46d899d7a0

"""
    instructions += (ROOT / "packaging/licenses/common.txt").read_text()
    if not windows:
        instructions += "\n\n" + (ROOT / "packaging/licenses/linux.txt").read_text()
    (destination / "README.txt").write_text(instructions)
    if windows:
        for file_name, key in (("libmpv-2.dll", "windowsMpv"), ("ffmpeg.exe", "windowsFfmpeg")):
            assert hashlib.sha256((destination / file_name).read_bytes()).hexdigest() == DEPENDENCIES[key]["sha256"]
    archive = Path(str(destination) + ".zip")
    temporary = Path(str(archive) + ".tmp")
    with zipfile.ZipFile(temporary, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as output:
        for path in sorted(destination.iterdir()):
            output.write(path, f"Dino Player V{VERSION}/{path.name}")
    with zipfile.ZipFile(temporary) as check:
        assert check.testzip() is None
        assert len(check.infolist()) == len(expected)
        for entry in check.infolist():
            actual = check.read(entry)
            original = (destination / Path(entry.filename).name).read_bytes()
            assert hashlib.sha256(actual).digest() == hashlib.sha256(original).digest()
    temporary.replace(archive)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    print(f"{archive.name}: {archive.stat().st_size / 1024 / 1024:.1f} MiB; {len(expected)} files")
    return archive, digest


if __name__ == "__main__":
    # Verify the native inputs before packaging either platform.
    for file_name, key in (("libmpv-2.dll", "windowsMpv"), ("ffmpeg.exe", "windowsFfmpeg")):
        path = PUBLISH / "portable-win-x64" / file_name
        if hashlib.file_digest(path.open("rb"), "sha256").hexdigest() != DEPENDENCIES[key]["sha256"]:
            raise RuntimeError(f"Native dependency hash mismatch: {path}")
    for name, expected in DEPENDENCIES["linuxNativeLibraries"].items():
        path = PUBLISH / "portable-linux-x64/lib" / name
        if hashlib.file_digest(path.open("rb"), "sha256").hexdigest() != expected:
            raise RuntimeError(f"Native dependency hash mismatch: {path}")
    path = PUBLISH / "portable-linux-x64/ffmpeg"
    if hashlib.file_digest(path.open("rb"), "sha256").hexdigest() != DEPENDENCIES["linuxFfmpeg"]["binarySha256"]:
        raise RuntimeError(f"Native dependency hash mismatch: {path}")
    results = [package("Windows"), package("Linux")]
    (PUBLISH / f"Dino Player V{VERSION}.sha256").write_text(
        "".join(f"{digest}  {archive.name}\n" for archive, digest in results)
    )
