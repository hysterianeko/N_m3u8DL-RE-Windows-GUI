using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace M3u8DownloaderGui
{
    // Opens a captured stream URL in a local media player for preview. Prefers a
    // detected desktop player (PotPlayer / VLC / mpv) because those handle HLS
    // directly; falls back to the system default handler. External players cannot
    // receive the captured Referer/Cookie, so preview may fail on sites that
    // require them even when the download (which does pass headers) succeeds.
    internal static class LocalPlayer
    {
        private static readonly string[] CandidatePaths =
        {
            @"%ProgramFiles%\DAUM\PotPlayer\PotPlayerMini64.exe",
            @"%ProgramFiles(x86)%\DAUM\PotPlayer\PotPlayerMini.exe",
            @"%ProgramFiles%\PotPlayer\PotPlayerMini64.exe",
            @"%ProgramFiles%\VideoLAN\VLC\vlc.exe",
            @"%ProgramFiles(x86)%\VideoLAN\VLC\vlc.exe",
            @"%ProgramFiles%\mpv\mpv.exe",
            @"%APPDATA%\mpv\mpv.exe",
        };

        public static void Preview(IWin32Window owner, string url, MediaRequestHeaders headers)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            MediaRequestHeaders safeHeaders = headers == null
                ? null
                : headers.CreateSafeProjection(url);
            bool needsHeaders = safeHeaders != null &&
                (!string.IsNullOrWhiteSpace(safeHeaders.Referer) ||
                 !string.IsNullOrWhiteSpace(safeHeaders.Cookie));

            string player = FindPlayer();
            try
            {
                if (player != null)
                {
                    ProcessStartInfo startInfo = new ProcessStartInfo();
                    startInfo.FileName = player;
                    startInfo.Arguments = BuildPlayerArguments(player, url, safeHeaders);
                    startInfo.UseShellExecute = false;
                    Process.Start(startInfo);
                }
                else
                {
                    // No known player found: let the OS pick a handler for the URL.
                    ProcessStartInfo startInfo = new ProcessStartInfo(url);
                    startInfo.UseShellExecute = true;
                    Process.Start(startInfo);
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    owner,
                    "无法启动预览播放器：\r\n\r\n" + exception.Message + "\r\n\r\n" +
                    "你仍然可以直接“使用此链接下载”。",
                    "预览失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            bool canCarryRequiredHeaders = PlayerSupportsHeaders(player) &&
                (safeHeaders == null || string.IsNullOrWhiteSpace(safeHeaders.Cookie));
            if (needsHeaders && !canCarryRequiredHeaders)
            {
                MessageBox.Show(
                    owner,
                    "该视频可能需要 Referer/Cookie 才能访问。\r\n" +
                    "外部播放器通常无法带上这些请求头，预览也许会失败或黑屏。\r\n\r\n" +
                    "这不影响下载——下载时本程序会自动带上捕获到的请求头。",
                    "预览提示",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private static string FindPlayer()
        {
            foreach (string candidate in CandidatePaths)
            {
                string expanded = Environment.ExpandEnvironmentVariables(candidate);
                if (!string.IsNullOrEmpty(expanded) && File.Exists(expanded))
                {
                    return expanded;
                }
            }

            return null;
        }

        private static bool IsVlc(string player)
        {
            return player != null &&
                   player.IndexOf("vlc", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsMpv(string player)
        {
            return player != null &&
                   player.IndexOf("mpv", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool PlayerSupportsHeaders(string player)
        {
            // Only mpv/VLC get header args below; PotPlayer and the OS handler do not.
            return IsVlc(player) || IsMpv(player);
        }

        internal static string BuildPlayerArguments(string player, string url, MediaRequestHeaders headers)
        {
            List<string> arguments = new List<string>();
            arguments.Add(url);

            if (IsMpv(player) && headers != null)
            {
                if (!string.IsNullOrWhiteSpace(headers.Referer))
                {
                    arguments.Add("--referrer=" + headers.Referer);
                }

                if (!string.IsNullOrWhiteSpace(headers.UserAgent))
                {
                    arguments.Add("--user-agent=" + headers.UserAgent);
                }

                return CommandLine.JoinArguments(arguments);
            }

            if (IsVlc(player) && headers != null)
            {
                if (!string.IsNullOrWhiteSpace(headers.Referer))
                {
                    arguments.Add(":http-referrer=" + headers.Referer);
                }

                if (!string.IsNullOrWhiteSpace(headers.UserAgent))
                {
                    arguments.Add(":http-user-agent=" + headers.UserAgent);
                }

                return CommandLine.JoinArguments(arguments);
            }

            return CommandLine.JoinArguments(arguments);
        }
    }
}
