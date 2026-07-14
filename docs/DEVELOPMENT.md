# Development and build

## Toolchain

The application intentionally targets .NET Framework 4.8 and C# 5 so it can be built on a standard Windows installation without a .NET SDK, Visual Studio, NuGet restore, or network access.

Compiler:

```text
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

Framework references:

- `System.dll`
- `System.Core.dll`
- `System.Drawing.dll`
- `System.Windows.Forms.dll`
- `System.Web.dll`
- `System.Xml.dll`

## Source layout

| File | Responsibility |
| --- | --- |
| `Program.cs` | Assembly metadata, entry point, GUI smoke mode |
| `MainForm.cs` | Main UI, process lifecycle, logs, download and conversion operations |
| `HlsKeyDialog.cs` | Manual HLS key/IV input and validation UI |
| `AppUtilities.cs` | Settings, tool discovery, filename parsing, playlist checks, key handling, command quoting, Job Object |
| `SelfTests.cs` | Dependency-free unit and platform tests |
| `app.manifest` | DPI awareness, supported Windows versions, execution level |
| `build.ps1` | Deterministic compiler invocation and test runner |
| `package.ps1` | Lightweight Windows release packaging and SHA-256 manifest |
| `setup-dependencies.ps1` | Verified upstream downloader setup and optional WinGet FFmpeg install |

## Build

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Project-local output only:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -SkipDesktopCopy
```

The script first compiles `build\SelfTests.exe`, runs it, and only creates the GUI executable when all tests pass.

## Smoke test

```powershell
& ".\build\M3U8视频下载器.exe" --smoke-test
if ($LASTEXITCODE -ne 0) { throw "Smoke test failed: $LASTEXITCODE" }
```

Smoke mode creates the main form and HLS key dialog without showing them or starting external tools. It validates essential controls, automatic naming, and responsive dimensions.

## Process model

1. Validate the URL/local input, writable output directory, filename, and tool paths.
2. Build each native argument independently and quote it using Windows `CommandLineToArgvW` rules.
3. Start `N_m3u8DL-RE` or FFmpeg directly with `UseShellExecute=false`.
4. Assign the process to a kill-on-close Windows Job Object.
5. Read stdout and stderr asynchronously and batch UI updates every 100 ms.
6. Determine success from the exit code and a changed output matching the requested name.
7. Clean imported playlists and restricted temporary key material.

No user-provided URL is passed through `cmd.exe` or PowerShell.

## Secret handling

Text or file-based HLS KEY/IV values are decoded to exactly 16 bytes. The application creates a random file below the user temp directory and replaces inherited ACLs with full control for only the current user and `SYSTEM`. Only that random path is sent to `N_m3u8DL-RE`.

Known raw, HEX, and Base64 forms are registered with the log redactor. Secret files are deleted on all normal exit paths, and stale files older than one day are removed at startup.

## Test coverage

Current self-tests cover:

- URL-based automatic naming and query filename parsing.
- Windows invalid/reserved filename handling.
- Native argument quoting with spaces, quotes, trailing slashes, and ampersands.
- Blob detection and HLS/MPD playlist text recognition.
- Relative segment, key, map, media, scheme-relative, and MPD BaseURL detection.
- AES-128 HEX/Base64 parsing and protected temporary file lifecycle.
- Settings/tool defaults through GUI smoke initialization.

The GitHub Actions workflow builds and smoke-tests the application on `windows-latest` and uploads the EXE as a workflow artifact.

## Package

```powershell
.\package.ps1 -Version 1.2.1
```

This creates a standalone GUI EXE, a lightweight Windows ZIP, and `SHA256SUMS.txt` under `dist`. The ZIP intentionally does not redistribute N_m3u8DL-RE or FFmpeg binaries. It contains the verified dependency setup script and third-party notices instead.

## Release checklist

1. Update assembly and manifest versions.
2. Add user-visible changes to `CHANGELOG.md`.
3. Run `build.ps1` and the GUI smoke test.
4. Verify a normal online playlist with redacted/test media.
5. Verify Blob rejection and absolute `#EXTM3U` import.
6. Verify a synthetic AES-128 key input without committing the key.
7. Verify lossless TS-to-MP4 conversion.
8. Inspect a 125% DPI screenshot for clipping or overlap.
9. Run `package.ps1` and inspect the ZIP and `SHA256SUMS.txt`.
10. Scan the repository for URLs, tokens, cookies, keys, binaries, local paths, and local settings before pushing.
11. Confirm `THIRD_PARTY_NOTICES.md` still describes the exact release contents.
