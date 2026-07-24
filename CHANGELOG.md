# Changelog

All notable changes to this project are documented here.

## 1.4.0 - 2026-07-24

### Added

- Capture complete HLS playlist text from browser-created Blob URLs in the embedded WebView2 window and pass it through the existing local-playlist import path.
- Expand every captured playlist candidate to inspect ordered segment, key, initialization, media-track, and child-playlist URLs.
- Associate actual browser segment requests with their owning playlist, including CDN cookies and safely replayable custom headers.
- Route protected Surrit/private-token playlist resources through a random loopback-only Windows cURL bridge when Cloudflare rejects the downloader's .NET/TLS client fingerprint.
- Restore unfinished downloads after the GUI or Windows restarts by reopening the same owned segment cache and downloading only missing or incomplete segments.
- Protect the complete recovery request, imported playlist, browser headers, URL tokens, and manual KEY/IV with per-user Windows DPAPI encryption.
- Prevent two GUI instances from restoring the same cache through a per-task exclusive lease, and retry cache deletion after transient file locks.
- Require the downloader to join a kill-on-close Windows Job before it can use a recoverable cache, preventing an orphaned old downloader from racing a restarted GUI.

### Changed

- Read ordinary m3u8 response bodies for capture details and resolve relative Blob playlist references against a trustworthy HTTP(S) document base URL.
- Fall back to an HLS bandwidth-by-duration size estimate when a CDN rejects independent segment-size probes, while keeping the result explicitly approximate.
- Feed browser-captured HTTP media-playlist text to the local import path and resolve captured master playlists to an available media child, avoiding a second protected playlist request by the downloader.
- Keep the exact page URL separate from the Blob resource base URL so captured Referer values remain correct.
- Keep protected cURL attempts below the downloader's matching local timeout, retry transient transport failures once, and use a four-thread tolerant mode that permits slow transfers while detecting connections that are effectively stalled.
- Coalesce recoverable cURL timeout, retry, and local-disconnect messages into periodic counters instead of logging every attempt as a separate alarming error.
- Keep unfinished recoverable and old-version orphan tasks for three days, and delete them immediately after success, explicit cancellation, or `清除缓存`.
- Send captured Cookie, Authorization, and custom credentials only through a per-resource same-origin cURL policy; remote URL mode no longer passes them to the downloader as global headers.
- Apply the same fail-closed origin policy to Referer, Origin, playlist size probes, redirects, and local previews so an untrusted playlist cannot relay captured browser credentials to another origin.
- Quote external player arguments with the shared Windows argument builder and keep captured cookies out of process command lines.

## 1.3.0 - 2026-07-18

### Added

- Turn the primary download button into a `下载 / 暂停 / 继续 / 重试下载 / 完成` task-state control.
- Preserve owned segment caches after pauses, network failures, and output-detection failures, then reuse the same temporary directory so `N_m3u8DL-RE` only fetches missing or incomplete segments.
- Let users explicitly discard a paused or failed task through the separate `清除缓存` action.

### Changed

- Delete segment caches only after confirmed success, explicit cancellation, a changed input URL, or application shutdown.
- Keep the final progress value visible after a recoverable failure and report the exact cache directory in the log.

## 1.2.3 - 2026-07-15

### Added

- Detect missing downloader and FFmpeg tools when the GUI first opens or an operation needs them, then offer verified automatic download, manual browsing, or deferral.
- Download pinned dependencies by direct HTTPS connections to GitHub Release assets without using the Windows system proxy, with visible progress, cancellation, controlled extraction, and atomic installation into the current user's LocalAppData.

### Fixed

- Run broad external-tool discovery outside the UI thread, require only FFmpeg for file conversion, and reject accidentally selected executables with the wrong filename.
- Keep verified tools and a completed progress state after installation while suppressing failure prompts during a confirmed window close.
- Read redirected downloader output as raw character chunks so segment progress reaches the GUI before a newline or process exit.
- Split concatenated timestamped records into readable lines and compact high-frequency terminal redraws into periodic progress milestones.
- Update the numeric progress bar from the current media track without allowing delayed progress to overwrite merging or cancellation states.
- Bound output-pipe shutdown, close the process Job before draining inherited handles, and preserve FFmpeg conversion logging.

## 1.2.2 - 2026-07-14

### Fixed

- Decode redirected `N_m3u8DL-RE` output with the Windows default code page while keeping FFmpeg output on UTF-8.
- Give each download an owned `--tmp-dir` and remove its fragments after completion, failure, or cancellation without guessing paths in the user's save directory.
- Preserve temporary files and the running state when an external process cannot be confirmed stopped.
- Gray out the unavailable key and file-conversion actions while a task is running, then restore their normal or configured-key appearance afterward.

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
