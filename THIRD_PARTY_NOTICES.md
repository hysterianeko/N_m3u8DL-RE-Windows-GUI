# Third-party notices

This project is an independent graphical wrapper. It starts the following
third-party command-line programs as external processes. Their source code,
copyright, trademarks, releases, and licenses remain with their respective
upstream projects.

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
