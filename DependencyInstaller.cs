using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace M3u8DownloaderGui
{
    internal sealed class DependencyInstallProgress
    {
        public string ToolName;
        public string Stage;
        public long BytesReceived;
        public long TotalBytes;
    }

    internal sealed class DependencyInstallResult
    {
        public string DownloaderPath;
        public string FfmpegPath;
    }

    internal static class DependencyInstaller
    {
        internal const string DownloaderArchiveSha256 =
            "3825FD42EE502F98A9378F6FDDDB2F7822709F521806214F466DB6935C950F1A";
        internal const string DownloaderExecutableSha256 =
            "35E7C16983F0315BBA2A3F37DC392FDFA074BE614C8EFFB642B78376B18BA272";
        internal const string FfmpegSevenZipSha256 =
            "E25B682664025D49034C981AFB4BAE36238A40F29A3CC1C713AD9A8B5B3528F6";
        internal const string FfmpegZipSha256 =
            "DB580001CAA24AC104C8CB856CD113A87B0A443F7BDF47D8C12B1D740584A2EC";
        internal const string FfmpegExecutableSha256 =
            "1326DDE4C84FF1F96FE6B8916C5BED29E163E9B5DCCF995F6F3DB069D143EC5E";

        internal const long DownloaderArchiveLength = 5477475L;
        internal const long FfmpegSevenZipLength = 33876939L;
        internal const long FfmpegZipLength = 109728040L;

        private const string DownloaderArchiveUrl =
            "https://github.com/nilaoda/N_m3u8DL-RE/releases/download/v0.6.0-beta/" +
            "N_m3u8DL-RE_v0.6.0-beta_win-x64_20260629.zip";
        private const string FfmpegSevenZipUrl =
            "https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/" +
            "ffmpeg-8.1.2-essentials_build.7z";
        private const string FfmpegZipUrl =
            "https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/" +
            "ffmpeg-8.1.2-essentials_build.zip";
        internal const long DownloaderExecutableLength = 13287936L;
        internal const long FfmpegExecutableLength = 101897728L;
        internal const long FfmpegLicenseLength = 35147L;
        internal const long FfmpegReadmeLength = 41440L;

        private const int DownloadBufferSize = 262144;
        private const long RangeSize = 4L * 1024L * 1024L;
        private const int MaximumAttempts = 3;
        private const string FfmpegArchiveRoot = "ffmpeg-8.1.2-essentials_build";

        private const string DownloaderLicenseText =
            "MIT License\r\n\r\n" +
            "Copyright (c) 2022 nilaoda\r\n\r\n" +
            "Permission is hereby granted, free of charge, to any person obtaining a copy\r\n" +
            "of this software and associated documentation files (the \"Software\"), to deal\r\n" +
            "in the Software without restriction, including without limitation the rights\r\n" +
            "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell\r\n" +
            "copies of the Software, and to permit persons to whom the Software is\r\n" +
            "furnished to do so, subject to the following conditions:\r\n\r\n" +
            "The above copyright notice and this permission notice shall be included in all\r\n" +
            "copies or substantial portions of the Software.\r\n\r\n" +
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR\r\n" +
            "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,\r\n" +
            "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE\r\n" +
            "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER\r\n" +
            "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,\r\n" +
            "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE\r\n" +
            "SOFTWARE.\r\n";

        public static Task<DependencyInstallResult> InstallAsync(
            bool installDownloader,
            bool installFfmpeg,
            IProgress<DependencyInstallProgress> progress,
            CancellationToken cancellationToken)
        {
            string installDirectory = ToolLocator.GetManagedToolsDirectory();
            return Task.Factory.StartNew(
                delegate
                {
                    return Install(
                        installDownloader,
                        installFfmpeg,
                        installDirectory,
                        progress,
                        cancellationToken);
                },
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        internal static string ComputeSha256(string path, CancellationToken cancellationToken)
        {
            using (SHA256 sha256 = SHA256.Create())
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DownloadBufferSize,
                FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[DownloadBufferSize];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sha256.TransformBlock(buffer, 0, read, null, 0);
                }
                sha256.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha256.Hash).Replace("-", string.Empty);
            }
        }

        internal static bool HashMatches(
            string path,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            return string.Equals(
                ComputeSha256(path, cancellationToken),
                expectedSha256,
                StringComparison.OrdinalIgnoreCase);
        }

        internal static void ExtractNamedZipEntry(
            string archivePath,
            string entryFileName,
            string destinationPath,
            long expectedLength,
            CancellationToken cancellationToken)
        {
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                ZipArchiveEntry selected = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (!string.Equals(entry.Name, entryFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (selected != null)
                    {
                        throw new InvalidDataException(
                            "下载的压缩包中包含重复的 " + entryFileName + "。为安全起见，文件没有安装。");
                    }
                    selected = entry;
                }

                if (selected == null)
                {
                    throw new InvalidDataException(
                        "下载的压缩包中没有找到 " + entryFileName + "。请改用手动浏览。");
                }
                if (selected.Length != expectedLength)
                {
                    throw new InvalidDataException(
                        entryFileName + " 的解压长度与固定版本不一致。为安全起见，文件没有安装。");
                }

                using (Stream input = selected.Open())
                using (FileStream output = new FileStream(
                    destinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    byte[] buffer = new byte[DownloadBufferSize];
                    long total = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        total += read;
                        if (total > expectedLength)
                        {
                            throw new InvalidDataException(
                                entryFileName + " 的解压内容超过固定长度。");
                        }
                        output.Write(buffer, 0, read);
                    }
                    if (total != expectedLength)
                    {
                        throw new EndOfStreamException(
                            entryFileName + " 解压不完整，应为 " + expectedLength +
                            " 字节，实际为 " + total + " 字节。");
                    }
                }
            }
        }

        private static DependencyInstallResult Install(
            bool installDownloader,
            bool installFfmpeg,
            string installDirectory,
            IProgress<DependencyInstallProgress> progress,
            CancellationToken cancellationToken)
        {
            if (!installDownloader && !installFfmpeg)
            {
                return new DependencyInstallResult();
            }

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Directory.CreateDirectory(installDirectory);
            string lockPath = Path.Combine(installDirectory, ".dependency-install.lock");
            FileStream installLock;
            try
            {
                installLock = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException(
                    "另一个下载器窗口正在安装外部工具，请等待它完成后重试。",
                    exception);
            }

            string temporaryDirectory = null;
            try
            {
                using (installLock)
                {
                    CleanupInterruptedInstallFiles(installDirectory);
                    EnsureFreeSpace(
                        installDirectory,
                        installFfmpeg ? 384L * 1024L * 1024L : 64L * 1024L * 1024L);

                    temporaryDirectory = Path.Combine(
                        Path.GetTempPath(),
                        "N_m3u8DL-RE-GUI",
                        "DependencyInstall",
                        Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temporaryDirectory);
                    EnsureFreeSpace(
                        temporaryDirectory,
                        installFfmpeg ? 384L * 1024L * 1024L : 64L * 1024L * 1024L);

                    DependencyInstallResult result = new DependencyInstallResult();
                    if (installDownloader)
                    {
                        result.DownloaderPath = InstallDownloader(
                            installDirectory,
                            temporaryDirectory,
                            progress,
                            cancellationToken);
                    }

                    if (installFfmpeg)
                    {
                        result.FfmpegPath = InstallFfmpeg(
                            installDirectory,
                            temporaryDirectory,
                            progress,
                            cancellationToken);
                    }

                    return result;
                }
            }
            finally
            {
                TryDeleteDirectory(temporaryDirectory);
                TryDeleteFile(lockPath);
            }
        }

        private static string InstallDownloader(
            string installDirectory,
            string temporaryDirectory,
            IProgress<DependencyInstallProgress> progress,
            CancellationToken cancellationToken)
        {
            const string toolName = "N_m3u8DL-RE";
            string archivePath = Path.Combine(temporaryDirectory, "N_m3u8DL-RE.zip");
            Report(progress, toolName, "正在下载", 0, DownloaderArchiveLength);
            DownloadFile(
                DownloaderArchiveUrl,
                archivePath,
                DownloaderArchiveLength,
                toolName,
                progress,
                cancellationToken);

            Report(progress, toolName, "正在校验下载文件", 0, 0);
            VerifyHash(archivePath, DownloaderArchiveSha256, "N_m3u8DL-RE 下载包", cancellationToken);

            string extractedPath = Path.Combine(temporaryDirectory, "N_m3u8DL-RE.exe");
            Report(progress, toolName, "正在解压", 0, 0);
            ExtractNamedZipEntry(
                archivePath,
                "N_m3u8DL-RE.exe",
                extractedPath,
                DownloaderExecutableLength,
                cancellationToken);
            VerifyHash(extractedPath, DownloaderExecutableSha256, "N_m3u8DL-RE.exe", cancellationToken);

            string destinationPath = Path.Combine(installDirectory, "N_m3u8DL-RE.exe");
            Report(progress, toolName, "正在安装", 0, 0);
            CommitVerifiedFile(
                extractedPath,
                destinationPath,
                DownloaderExecutableSha256,
                cancellationToken);
            WriteTextFileAtomically(
                Path.Combine(installDirectory, "LICENSE.N_m3u8DL-RE"),
                DownloaderLicenseText);
            return destinationPath;
        }

        private static string InstallFfmpeg(
            string installDirectory,
            string temporaryDirectory,
            IProgress<DependencyInstallProgress> progress,
            CancellationToken cancellationToken)
        {
            const string toolName = "FFmpeg";
            string extractedDirectory = Path.Combine(temporaryDirectory, "ffmpeg-extracted");
            string extractedExecutable;
            string extractedLicense;
            string extractedReadme;

            string tarPath = Path.Combine(Environment.SystemDirectory, "tar.exe");
            bool extractedWithTar = false;
            if (File.Exists(tarPath))
            {
                string archivePath = Path.Combine(temporaryDirectory, "ffmpeg.7z");
                Report(progress, toolName, "正在下载", 0, FfmpegSevenZipLength);
                DownloadFile(
                    FfmpegSevenZipUrl,
                    archivePath,
                    FfmpegSevenZipLength,
                    toolName,
                    progress,
                    cancellationToken);
                Report(progress, toolName, "正在校验下载文件", 0, 0);
                VerifyHash(archivePath, FfmpegSevenZipSha256, "FFmpeg 下载包", cancellationToken);

                Report(progress, toolName, "正在解压", 0, 0);
                Directory.CreateDirectory(extractedDirectory);
                extractedWithTar = TryExtractWithTar(
                    tarPath,
                    archivePath,
                    extractedDirectory,
                    cancellationToken);
            }

            if (extractedWithTar)
            {
                extractedExecutable = FindFile(extractedDirectory, "ffmpeg.exe");
                extractedLicense = FindFile(extractedDirectory, "LICENSE");
                extractedReadme = FindFile(extractedDirectory, "README.txt");
            }
            else
            {
                TryDeleteDirectory(extractedDirectory);
                string zipPath = Path.Combine(temporaryDirectory, "ffmpeg.zip");
                Report(progress, toolName, "正在下载 ZIP", 0, FfmpegZipLength);
                DownloadFile(
                    FfmpegZipUrl,
                    zipPath,
                    FfmpegZipLength,
                    toolName,
                    progress,
                    cancellationToken);
                Report(progress, toolName, "正在校验下载文件", 0, 0);
                VerifyHash(zipPath, FfmpegZipSha256, "FFmpeg ZIP 下载包", cancellationToken);

                extractedExecutable = Path.Combine(temporaryDirectory, "ffmpeg.exe");
                extractedLicense = Path.Combine(temporaryDirectory, "LICENSE.FFmpeg");
                extractedReadme = Path.Combine(temporaryDirectory, "README.FFmpeg.txt");
                Report(progress, toolName, "正在解压", 0, 0);
                ExtractNamedZipEntry(
                    zipPath,
                    "ffmpeg.exe",
                    extractedExecutable,
                    FfmpegExecutableLength,
                    cancellationToken);
                ExtractNamedZipEntry(
                    zipPath,
                    "LICENSE",
                    extractedLicense,
                    FfmpegLicenseLength,
                    cancellationToken);
                ExtractNamedZipEntry(
                    zipPath,
                    "README.txt",
                    extractedReadme,
                    FfmpegReadmeLength,
                    cancellationToken);
            }

            if (extractedExecutable == null || extractedLicense == null || extractedReadme == null)
            {
                throw new InvalidDataException(
                    "FFmpeg 压缩包内容不完整。请改用手动浏览选择 ffmpeg.exe。");
            }

            Report(progress, toolName, "正在校验程序", 0, 0);
            VerifyHash(extractedExecutable, FfmpegExecutableSha256, "ffmpeg.exe", cancellationToken);
            string destinationPath = Path.Combine(installDirectory, "ffmpeg.exe");
            Report(progress, toolName, "正在安装", 0, 0);
            CommitVerifiedFile(
                extractedExecutable,
                destinationPath,
                FfmpegExecutableSha256,
                cancellationToken);
            CopyFileAtomically(
                extractedLicense,
                Path.Combine(installDirectory, "LICENSE.FFmpeg-GPLv3.txt"));
            CopyFileAtomically(
                extractedReadme,
                Path.Combine(installDirectory, "README.FFmpeg-Gyan.txt"));
            return destinationPath;
        }

        private static void DownloadFile(
            string uri,
            string destinationPath,
            long expectedLength,
            string toolName,
            IProgress<DependencyInstallProgress> progress,
            CancellationToken cancellationToken)
        {
            if (expectedLength >= 16L * 1024L * 1024L)
            {
                try
                {
                    DownloadFileInRanges(
                        uri,
                        destinationPath,
                        expectedLength,
                        toolName,
                        progress,
                        cancellationToken);
                }
                catch (RangeNotSupportedException)
                {
                    TryDeleteFile(destinationPath);
                    DownloadRequestWithRetries(
                        uri,
                        destinationPath,
                        null,
                        null,
                        0,
                        expectedLength,
                        toolName,
                        progress,
                        cancellationToken);
                }
            }
            else
            {
                DownloadRequestWithRetries(
                    uri,
                    destinationPath,
                    null,
                    null,
                    0,
                    expectedLength,
                    toolName,
                    progress,
                    cancellationToken);
            }

            long actualLength = new FileInfo(destinationPath).Length;
            if (actualLength != expectedLength)
            {
                throw new InvalidDataException(
                    toolName + " 下载长度不正确，应为 " + expectedLength +
                    " 字节，实际为 " + actualLength + " 字节。");
            }
        }

        private static void DownloadFileInRanges(
            string uri,
            string destinationPath,
            long expectedLength,
            string toolName,
            IProgress<DependencyInstallProgress> progress,
            CancellationToken cancellationToken)
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            using (FileStream output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                for (long start = 0; start < expectedLength; start += RangeSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long end = Math.Min(start + RangeSize - 1, expectedLength - 1);
                    string partPath = destinationPath + ".part";
                    DownloadRequestWithRetries(
                        uri,
                        partPath,
                        start,
                        end,
                        start,
                        expectedLength,
                        toolName,
                        progress,
                        cancellationToken);

                    using (FileStream input = new FileStream(
                        partPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        DownloadBufferSize,
                        FileOptions.SequentialScan))
                    {
                        input.CopyTo(output);
                    }
                    File.Delete(partPath);
                }
            }
        }

        private static void DownloadRequestWithRetries(
            string uri,
            string destinationPath,
            long? rangeStart,
            long? rangeEnd,
            long completedBeforeRequest,
            long totalLength,
            string toolName,
            IProgress<DependencyInstallProgress> progress,
            CancellationToken cancellationToken)
        {
            Exception lastException = null;
            for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    DownloadRequest(
                        uri,
                        destinationPath,
                        rangeStart,
                        rangeEnd,
                        completedBeforeRequest,
                        totalLength,
                        toolName,
                        progress,
                        cancellationToken);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (RangeNotSupportedException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastException = exception;
                    TryDeleteFile(destinationPath);
                    if (attempt < MaximumAttempts &&
                        cancellationToken.WaitHandle.WaitOne(1000))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }

            throw new InvalidOperationException(
                toolName + " 下载失败，已重试 " + MaximumAttempts + " 次。",
                lastException);
        }

        private static void DownloadRequest(
            string uri,
            string destinationPath,
            long? rangeStart,
            long? rangeEnd,
            long completedBeforeRequest,
            long totalLength,
            string toolName,
            IProgress<DependencyInstallProgress> progress,
            CancellationToken cancellationToken)
        {
            HttpWebRequest request = CreateDirectDownloadRequest(uri);
            if (rangeStart.HasValue && rangeEnd.HasValue)
            {
                request.AddRange(rangeStart.Value, rangeEnd.Value);
            }

            using (cancellationToken.Register(delegate { request.Abort(); }))
            {
                try
                {
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    {
                        if (response.ResponseUri == null ||
                            !string.Equals(
                                response.ResponseUri.Scheme,
                                Uri.UriSchemeHttps,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                "下载重定向没有停留在 HTTPS 地址。为安全起见，已停止下载。");
                        }
                        if (rangeStart.HasValue &&
                            response.StatusCode != HttpStatusCode.PartialContent)
                        {
                            throw new RangeNotSupportedException(
                                "下载服务器没有返回预期的分段内容。");
                        }
                        if (rangeStart.HasValue)
                        {
                            string expectedContentRange =
                                "bytes " + rangeStart.Value + "-" + rangeEnd.Value +
                                "/" + totalLength;
                            string contentRange = response.Headers[HttpResponseHeader.ContentRange];
                            if (!string.Equals(
                                (contentRange ?? string.Empty).Trim(),
                                expectedContentRange,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException(
                                    "下载服务器返回了错误的 Content-Range。");
                            }
                        }

                        long expectedResponseLength = rangeStart.HasValue
                            ? rangeEnd.Value - rangeStart.Value + 1
                            : totalLength;
                        if (response.ContentLength >= 0 &&
                            response.ContentLength != expectedResponseLength)
                        {
                            throw new InvalidDataException(
                                "下载服务器返回的文件长度与预期不符。");
                        }

                        using (Stream input = response.GetResponseStream())
                        using (FileStream output = new FileStream(
                            destinationPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            DownloadBufferSize,
                            FileOptions.SequentialScan))
                        {
                            byte[] buffer = new byte[DownloadBufferSize];
                            long received = 0;
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (received + read > expectedResponseLength)
                                {
                                    throw new InvalidDataException(
                                        "下载内容超过固定版本的预期长度。");
                                }
                                output.Write(buffer, 0, read);
                                received += read;
                                Report(
                                    progress,
                                    toolName,
                                    "正在下载",
                                    completedBeforeRequest + received,
                                    totalLength);
                            }

                            if (received != expectedResponseLength)
                            {
                                throw new EndOfStreamException(
                                    "下载提前结束，收到 " + received +
                                    " 字节，预期 " + expectedResponseLength + " 字节。");
                            }
                        }
                    }
                }
                catch (WebException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                    throw;
                }
            }
        }

        internal static HttpWebRequest CreateDirectDownloadRequest(string uri)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.AllowAutoRedirect = true;
            request.UserAgent = "M3U8-Video-Downloader/1.2.3";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 60000;
            request.KeepAlive = true;
            request.Proxy = null;
            return request;
        }

        private static bool TryExtractWithTar(
            string tarPath,
            string archivePath,
            string destinationDirectory,
            CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = tarPath;
            startInfo.Arguments =
                "-xf " + CommandLine.QuoteArgument(archivePath) +
                " -C " + CommandLine.QuoteArgument(destinationDirectory) + " " +
                CommandLine.QuoteArgument(FfmpegArchiveRoot + "/bin/ffmpeg.exe") + " " +
                CommandLine.QuoteArgument(FfmpegArchiveRoot + "/LICENSE") + " " +
                CommandLine.QuoteArgument(FfmpegArchiveRoot + "/README.txt");
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;

            Process process = null;
            ProcessJob processJob = null;
            try
            {
                process = Process.Start(startInfo);
                if (process == null)
                {
                    return false;
                }

                processJob = ProcessJob.TryCreateKillOnClose();
                if (processJob != null && !processJob.AddProcess(process))
                {
                    processJob.Dispose();
                    processJob = null;
                }

                while (!process.WaitForExit(100))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        TerminateTarProcess(process, processJob);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException(
                        "取消 FFmpeg 解压时无法确认 tar.exe 已退出。",
                        exception);
                }
                return false;
            }
            finally
            {
                Exception shutdownFailure = null;
                try
                {
                    if (process != null)
                    {
                        bool exited = false;
                        try
                        {
                            exited = process.HasExited;
                        }
                        catch
                        {
                        }

                        if (!exited)
                        {
                            try
                            {
                                TerminateTarProcess(process, processJob);
                            }
                            catch (Exception exception)
                            {
                                shutdownFailure = exception;
                            }
                        }
                    }
                }
                finally
                {
                    if (process != null)
                    {
                        process.Dispose();
                    }
                    if (processJob != null)
                    {
                        processJob.Dispose();
                    }
                }

                if (shutdownFailure != null)
                {
                    throw new InvalidOperationException(
                        "无法确认 tar.exe 已退出，已停止安装以避免并发写入。",
                        shutdownFailure);
                }
            }
        }

        private static void TerminateTarProcess(Process process, ProcessJob processJob)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
            }

            if (processJob != null)
            {
                processJob.Dispose();
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (process.HasExited || process.WaitForExit(250))
                    {
                        return;
                    }
                    process.Kill();
                }
                catch
                {
                    try
                    {
                        if (process.HasExited)
                        {
                            return;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            throw new IOException("无法终止 tar.exe，临时文件可能仍被占用。");
        }

        private static void EnsureFreeSpace(string path, long requiredBytes)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                DriveInfo drive = new DriveInfo(root);
                if (drive.IsReady && drive.AvailableFreeSpace < requiredBytes)
                {
                    throw new IOException(
                        "磁盘可用空间不足。自动安装至少需要 " +
                        (requiredBytes / (1024L * 1024L)) + " MB 可用空间。");
                }
            }
            catch (IOException)
            {
                throw;
            }
            catch
            {
                // Some network or virtual drives do not expose free-space information.
            }
        }

        private static string FindFile(string directory, string fileName)
        {
            try
            {
                string[] files = Directory.GetFiles(
                    directory,
                    fileName,
                    SearchOption.AllDirectories);
                if (files.Length == 0)
                {
                    return null;
                }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                return files[0];
            }
            catch
            {
                return null;
            }
        }

        private static void VerifyHash(
            string path,
            string expectedSha256,
            string description,
            CancellationToken cancellationToken)
        {
            string actual = ComputeSha256(path, cancellationToken);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    description + " 的 SHA-256 校验失败。为安全起见，文件没有安装。");
            }
        }

        private static void CommitVerifiedFile(
            string sourcePath,
            string destinationPath,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            string stagedPath = destinationPath + ".install-" +
                Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                CopyFileWithCancellation(sourcePath, stagedPath, cancellationToken);
                VerifyHash(stagedPath, expectedSha256, Path.GetFileName(destinationPath), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(destinationPath))
                {
                    File.Replace(stagedPath, destinationPath, null, true);
                }
                else
                {
                    File.Move(stagedPath, destinationPath);
                }
            }
            finally
            {
                TryDeleteFile(stagedPath);
            }
        }

        private static void CopyFileAtomically(string sourcePath, string destinationPath)
        {
            string stagedPath = destinationPath + ".install-" +
                Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(sourcePath, stagedPath, true);
                if (File.Exists(destinationPath))
                {
                    File.Replace(stagedPath, destinationPath, null, true);
                }
                else
                {
                    File.Move(stagedPath, destinationPath);
                }
            }
            finally
            {
                TryDeleteFile(stagedPath);
            }
        }

        private static void CopyFileWithCancellation(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            using (FileStream input = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DownloadBufferSize,
                FileOptions.SequentialScan))
            using (FileStream output = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                DownloadBufferSize,
                FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[DownloadBufferSize];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
            }
        }

        private static void WriteTextFileAtomically(string destinationPath, string content)
        {
            string stagedPath = destinationPath + ".install-" +
                Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(stagedPath, content, new UTF8Encoding(false));
                if (File.Exists(destinationPath))
                {
                    File.Replace(stagedPath, destinationPath, null, true);
                }
                else
                {
                    File.Move(stagedPath, destinationPath);
                }
            }
            finally
            {
                TryDeleteFile(stagedPath);
            }
        }

        private static void CleanupInterruptedInstallFiles(string installDirectory)
        {
            try
            {
                foreach (string file in Directory.GetFiles(
                    installDirectory,
                    "*.install-*.tmp",
                    SearchOption.TopDirectoryOnly))
                {
                    TryDeleteFile(file);
                }
            }
            catch
            {
            }
        }

        private static void Report(
            IProgress<DependencyInstallProgress> progress,
            string toolName,
            string stage,
            long bytesReceived,
            long totalBytes)
        {
            if (progress == null)
            {
                return;
            }

            DependencyInstallProgress value = new DependencyInstallProgress();
            value.ToolName = toolName;
            value.Stage = stage;
            value.BytesReceived = bytesReceived;
            value.TotalBytes = totalBytes;
            progress.Report(value);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (!Directory.Exists(path))
                    {
                        return;
                    }
                    Directory.Delete(path, true);
                    return;
                }
                catch
                {
                    if (attempt < 5)
                    {
                        Thread.Sleep(200);
                    }
                }
            }
        }

        private sealed class RangeNotSupportedException : IOException
        {
            public RangeNotSupportedException(string message)
                : base(message)
            {
            }
        }
    }
}
