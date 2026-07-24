using System;
using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

namespace M3u8DownloaderGui
{
    // Detects the Evergreen WebView2 Runtime and guides the user to install it
    // when missing. The embedded browser capture feature depends on this runtime;
    // it is not bundled with the app (only the loader/managed DLLs are), matching
    // how DependencyInstaller treats N_m3u8DL-RE and FFmpeg as external tools.
    internal static class WebView2Runtime
    {
        // Client id of the Evergreen WebView2 Runtime under EdgeUpdate.
        private const string ClientKey =
            "Microsoft\\EdgeUpdate\\Clients\\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        // Official redirect that always resolves to the latest bootstrapper.
        private const string BootstrapperUrl =
            "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        // Returns true when a usable runtime version is recorded in the registry.
        public static bool IsInstalled()
        {
            return !string.IsNullOrEmpty(GetInstalledVersion());
        }

        public static string GetInstalledVersion()
        {
            // 64-bit Windows records the runtime under WOW6432Node (per-machine)
            // and under HKCU (per-user). Any non-empty pv value means installed.
            string version = ReadVersion(RegistryHive.LocalMachine, RegistryView.Registry64)
                ?? ReadVersion(RegistryHive.LocalMachine, RegistryView.Registry32)
                ?? ReadVersion(RegistryHive.CurrentUser, RegistryView.Registry64)
                ?? ReadVersion(RegistryHive.CurrentUser, RegistryView.Registry32);
            return version;
        }

        private static string ReadVersion(RegistryHive hive, RegistryView view)
        {
            try
            {
                using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey clientKey = baseKey.OpenSubKey("SOFTWARE\\" + ClientKey))
                {
                    if (clientKey == null)
                    {
                        return null;
                    }

                    string pv = clientKey.GetValue("pv") as string;
                    if (string.IsNullOrEmpty(pv) || pv == "0.0.0.0")
                    {
                        return null;
                    }

                    return pv;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Shows a prompt explaining the missing runtime and offers to open the
        // official download page. Returns true only when the runtime is present.
        public static bool EnsureAvailable(IWin32Window owner)
        {
            if (IsInstalled())
            {
                return true;
            }

            DialogResult choice = MessageBox.Show(
                owner,
                "“从网页捕获”需要 Microsoft Edge WebView2 运行时，但当前系统没有检测到它。\r\n\r\n" +
                "WebView2 运行时是微软官方的免费组件，Windows 11 与较新的 Windows 10 通常已自带。\r\n" +
                "安装后无需重启本程序，重新点击“从网页捕获”即可使用。\r\n\r\n" +
                "是否现在打开官方下载页面？\r\n" +
                "（下载 “Evergreen Bootstrapper” 并运行安装即可。）",
                "缺少 WebView2 运行时",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1);

            if (choice == DialogResult.Yes)
            {
                OpenDownloadPage(owner);
            }

            return false;
        }

        private static void OpenDownloadPage(IWin32Window owner)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(BootstrapperUrl);
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    owner,
                    "无法自动打开下载页面：\r\n\r\n" + exception.Message + "\r\n\r\n" +
                    "请手动在浏览器打开：\r\n" + BootstrapperUrl,
                    "打开下载页面失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }
}
