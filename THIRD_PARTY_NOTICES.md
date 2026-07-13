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
source repository and lightweight release package do not bundle that binary.
The optional dependency setup script downloads the selected binary directly
from the official upstream GitHub Release, verifies its SHA-256 digest, and
keeps the included upstream license beside the installed executable.

This project is not affiliated with, maintained by, or endorsed by nilaoda or
the N_m3u8DL-RE project.

## FFmpeg

- Project: FFmpeg
- Website and source: https://ffmpeg.org/
- Download information: https://ffmpeg.org/download.html
- Legal and license information: https://ffmpeg.org/legal.html

The GUI invokes `ffmpeg.exe` as an external executable. The source repository
and lightweight release package do not bundle an FFmpeg binary. The optional
dependency setup script asks WinGet to install the `Gyan.FFmpeg` package; that
package and its licensing terms are managed by its publisher and WinGet.

FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project.

## Release packaging policy

Before adding any third-party executable to a future release asset, the
maintainer must review that exact build's license, include all required notices
and license texts, and satisfy any corresponding source-code obligations.
