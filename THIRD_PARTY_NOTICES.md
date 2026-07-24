# Third-party notices

This project is an independent graphical wrapper. It redistributes the
Microsoft Edge WebView2 SDK files described below and can start the listed
third-party command-line programs as external processes. Their source code,
copyright, trademarks, releases, and licenses remain with their respective
upstream projects.

## Microsoft Edge WebView2 SDK

- Package: `Microsoft.Web.WebView2`
- Version: `1.0.2957.106`
- Project: https://aka.ms/webview
- NuGet package: https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.2957.106
- Included license: [LICENSE.WebView2](LICENSE.WebView2)
- Included third-party notice: [NOTICE.WebView2](NOTICE.WebView2)

The Windows x64 release ZIP redistributes the unmodified .NET Framework 4.6.2
Core and WinForms assemblies and the x64 native WebView2 loader from the
official package. The build script downloads that pinned package only when the
local SDK files are absent or invalid, verifies its SHA-256 digest, extracts the
three required files, and verifies each extracted file before use. The package
does not bundle the Microsoft Edge WebView2 Runtime itself.

## N_m3u8DL-RE

- Project: `nilaoda/N_m3u8DL-RE`
- Source: https://github.com/nilaoda/N_m3u8DL-RE
- Official releases: https://github.com/nilaoda/N_m3u8DL-RE/releases
- Upstream license: MIT
- License page: https://github.com/nilaoda/N_m3u8DL-RE/blob/main/LICENSE
- Included license text: [LICENSE.N_m3u8DL-RE](LICENSE.N_m3u8DL-RE)

The GUI invokes `N_m3u8DL-RE.exe` as an unmodified external executable. The
source repository and release package do not bundle that binary. When the user
chooses automatic setup, the GUI downloads the pinned `v0.6.0-beta` Windows x64
archive directly from the official upstream GitHub Release, verifies the
archive and executable SHA-256 digests, and keeps the included upstream
license beside the installed executable.

The GUI uses direct HTTPS connections for automatic dependency downloads and
does not use the Windows system proxy.

This project is not affiliated with, maintained by, or endorsed by nilaoda or
the N_m3u8DL-RE project.

## FFmpeg

- Project: FFmpeg
- Website and source: https://ffmpeg.org/
- Download information: https://ffmpeg.org/download.html
- Legal and license information: https://ffmpeg.org/legal.html
- Selected Windows build publisher: https://www.gyan.dev/ffmpeg/builds/
- Selected build: Gyan FFmpeg `8.1.2-essentials_build`, GPL v3

The GUI invokes `ffmpeg.exe` as an external executable. The source repository
and release package do not bundle an FFmpeg binary. When the user chooses
automatic setup, the GUI downloads the pinned Gyan Windows build from the
publisher's GitHub Release, verifies the archive and executable SHA-256
digests, and keeps the archive's GPL license and build README beside the
installed executable. Gyan is a Windows build provider linked from FFmpeg's
download page; the downloaded binary is not published by the FFmpeg project
itself.

FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project.

## Release packaging policy

Before adding any third-party executable to a future release asset, the
maintainer must review that exact build's license, include all required notices
and license texts, and satisfy any corresponding source-code obligations.
