# Changelog

All notable changes to this project are documented here.

## 1.2.1 - 2026-07-14

### Fixed

- Detect `N_m3u8DL-RE.exe` from process, user, and machine PATH values.
- Check common Downloads folders on every ready fixed drive without performing a recursive drive scan.
- Expand environment variables and outer quotes in configured tool paths.
- Identify the specific missing dependency in the automatic detection status message.

## 1.2.0 - 2026-07-13

### Added

- Native Windows Forms interface for `N_m3u8DL-RE` and FFmpeg.
- Automatic filename extraction with editable names and Windows filename sanitization.
- Persistent downloader, FFmpeg, and save-directory settings.
- Cat Catch `#EXTM3U` clipboard import with Blob URL detection.
- Optional manual HLS AES-128 key and IV input using HEX, Base64, or key files.
- Restricted temporary secret files so raw keys are not placed on the child process command line.
- Lossless conversion of existing TS, M2TS, MKV, MOV, WebM, FLV, MP4, and M4V media to MP4.
- Atomic MP4 conversion commits that preserve existing targets after cancellation or failure.
- MPD clipboard imports with relative segment templates under an absolute BaseURL.
- Lightweight Windows release packaging, verified dependency setup, Visual Studio solution, license, and third-party notices.
- Live logs, cancellation, output detection, open-directory action, and configuration validation.
- Self-tests, GUI smoke tests, high-DPI manifest, and GitHub Actions build workflow.

### Security

- Media URLs and logs are not saved in application settings.
- Imported playlists and temporary key material are cleaned up after use.
- Logs redact known HEX, Base64, key, and IV representations.
- Developer-machine paths and upstream icon extraction are excluded from portable builds.
