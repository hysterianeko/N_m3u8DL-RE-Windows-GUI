using System;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("M3U8 Video Downloader")]
[assembly: AssemblyDescription("Windows front end for N_m3u8DL-RE and FFmpeg")]
[assembly: AssemblyCompany("hysterianeko")]
[assembly: AssemblyProduct("M3U8 Video Downloader")]
[assembly: AssemblyCopyright("Copyright (c) 2026 hysterianeko")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace M3u8DownloaderGui
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                ShowFatalError(e.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Exception exception = e.ExceptionObject as Exception;
                if (exception != null)
                {
                    ShowFatalError(exception);
                }
            };

            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--smoke-test", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (MainForm form = new MainForm())
                    {
                        form.CreateControl();
                        bool mainFormPassed = form.RunSmokeTest();
                        using (HlsKeyDialog keyDialog = new HlsKeyDialog(string.Empty, string.Empty))
                        {
                            keyDialog.CreateControl();
                            Environment.ExitCode = mainFormPassed && keyDialog.RunSmokeTest() ? 0 : 2;
                        }
                    }
                }
                catch
                {
                    Environment.ExitCode = 3;
                }

                return;
            }

            Application.Run(new MainForm());
        }

        private static void ShowFatalError(Exception exception)
        {
            try
            {
                MessageBox.Show(
                    "程序遇到未处理的错误：\r\n\r\n" + exception.Message,
                    "M3U8 视频下载器",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // There is no useful recovery path if the UI itself cannot show the error.
            }
        }
    }
}
