# Changelog

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
