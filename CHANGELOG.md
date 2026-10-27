# Changelog

## 1.5.3 — 2026-10-26

### Fixed

- Fixed export resolutions keep the source's shape. A 21:9 or 4:3 clip exported at 1080p is now 2580x1080 or 1440x1080 with square pixels, instead of 1920x1080 with stretched pixels. The choices are labeled 720p, 1080p, 1440p and 2160p (4K).
- The export folder follows each newly opened video (the Preferences default, or the video's folder) until you choose one with Browse. Changing the default in Preferences applies immediately.
- Old diagnostic logs are removed at startup (the newest 20 are kept), along with input scripts left by earlier builds.
- The per-track-count audio preset is only rewritten when a track setting changes.

### Changed

- Turning Crop off and on keeps the selection for the current video; turning it on near the end selects the last ten seconds.
- Escape cancels Preferences and the preset name dialog, which now focuses its name field. The speed shortcut fields no longer capture Tab, Enter or Escape.
- Clearer messages when FFmpeg is missing or not executable, or a video takes too long to prepare. Proper plurals in queue text and the close warning, which now sizes to its message.
- Switching video codec or encoder keeps the chosen preset when the new list offers it. Timeline labels use 00:00 consistently, and arrow keys show readable names in Preferences.

### Performance

- The diagnostic log text is only rebuilt while the log is shown and expanded.
- Log lines are written to disk and the console on a background task instead of the UI and libmpv threads.
- While the player tab is shown, the export queue refresh only updates the tab header. Queue refreshes rebuild only the selected job's log, and path checks reuse each job's resolved paths.
- Timeline duration changes resize the existing timeline instead of rebuilding it.

### Code quality

- MainWindow is split into partial files by area and uses Avalonia's generated control fields.
- Shared helpers replace duplicated tooltip, volume slider, time formatting and reserved-shortcut code. Export option names and libmpv ids are named constants.

### Documentation

- Replace the player screenshot with approved gameplay paused at 00:25, and update the user guide and screenshot capture notes.

## 1.5.2 — 2026-10-18

### Fixed

- Keyboard skipping and skip buttons respond promptly to rapid or held input.
- Windowed and fullscreen timelines update promptly from native seek events.
- New videos start at zero after their starting frame and saved audio settings are ready.
- The import prompt remains visible while browsing for a video and after cancelling the picker.

### Added

- First GitHub source distribution and refreshed Windows/Linux x64 portable downloads.
- Project documentation, screenshots using generated test media, and issue templates.
- Automated Release builds and non-graphical checks on Windows/Linux, with FFmpeg/libmpv integration checks on Linux.
- Portable packaging takes the version from the app project and includes GPL text, component notices, pristine settings, and SHA-256 checksums.

### Validation limits

Interactive Windows playback has not yet been validated. Hosted CI does not exercise NVIDIA hardware encoding or interactive desktop input.
