using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace M3u8DownloaderGui
{
    internal static class SelfTests
    {
        private static int _failed;

        public static int Main()
        {
            AssertEqual(
                "ExampleVideo",
                FileNameHelper.FromInput("https://media.example.com/20260503/ExampleVideo/index.m3u8"),
                "generic index name uses parent directory");

            AssertEqual(
                "课程 第一集",
                FileNameHelper.FromInput("https://example.com/hls/master.m3u8?filename=%E8%AF%BE%E7%A8%8B%20%E7%AC%AC%E4%B8%80%E9%9B%86.mp4&token=a%26b"),
                "query filename is decoded and extension removed");

            AssertEqual(
                "movie",
                FileNameHelper.FromInput("https://example.com/video/movie.mpd?token=a&x=b"),
                "query parameters do not affect path name");

            AssertEqual("_CON", FileNameHelper.CleanFileName("CON.mp4"), "reserved Windows name");
            AssertEqual("_NUL.download", FileNameHelper.CleanFileName("NUL.download"), "reserved Windows name with unknown extension");
            AssertEqual("a_b", FileNameHelper.CleanFileName("a:b"), "invalid filename character");
            AssertEqual("\"https://example.com/a?x=1&y=2\"", CommandLine.QuoteArgument("https://example.com/a?x=1&y=2"), "URL quoting");
            AssertEqual("\"C:\\path with space\\\\\"", CommandLine.QuoteArgument("C:\\path with space\\"), "trailing slash quoting");

            List<string> arguments = new List<string>();
            arguments.Add("https://example.com/a.m3u8?x=1&y=2");
            arguments.Add("--save-dir");
            arguments.Add("C:\\Media\\My Videos");
            string joined = CommandLine.JoinArguments(arguments);
            AssertTrue(joined.IndexOf("&y=2", StringComparison.Ordinal) >= 0, "ampersand remains inside URL argument");
            AssertTrue(joined.IndexOf("My Videos", StringComparison.Ordinal) >= 0, "space-containing path remains present");
            AssertTrue(
                PlaylistInput.IsBlobUrl("blob:chrome-extension://example/123"),
                "browser blob URL is detected");
            AssertTrue(
                PlaylistInput.LooksLikePlaylistContent("#EXTM3U\n#EXTINF:10,\nhttps://example.com/1.ts"),
                "HLS playlist text is detected");
            AssertTrue(
                !PlaylistInput.ContainsRelativeMediaReferences("#EXTM3U\nhttps://example.com/1.ts"),
                "absolute HLS segment is accepted");
            AssertTrue(
                PlaylistInput.ContainsRelativeMediaReferences("#EXTM3U\nsegments/1.ts"),
                "relative HLS segment is detected");
            AssertTrue(
                PlaylistInput.ContainsRelativeMediaReferences("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"keys/video.key\"\nhttps://example.com/1.ts"),
                "relative HLS key URI is detected");
            AssertTrue(
                PlaylistInput.ContainsRelativeMediaReferences("#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\nhttps://example.com/1.m4s"),
                "relative HLS initialization URI is detected");
            AssertTrue(
                !PlaylistInput.ContainsRelativeMediaReferences("#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"https://example.com/video.key\"\nhttps://example.com/1.ts"),
                "absolute HLS key URI is accepted");
            AssertTrue(
                PlaylistInput.ContainsRelativeMediaReferences("<?xml version=\"1.0\"?><MPD><Period><BaseURL>segments/</BaseURL></Period></MPD>"),
                "relative MPD BaseURL is detected");
            AssertTrue(
                !PlaylistInput.ContainsRelativeMediaReferences(
                    "<MPD><BaseURL>https://cdn.example.com/video/</BaseURL><Period>" +
                    "<SegmentTemplate media=\"$Number$.m4s\" initialization=\"init.mp4\" />" +
                    "</Period></MPD>"),
                "absolute MPD BaseURL resolves relative segment templates");
            AssertTrue(
                !PlaylistInput.ContainsRelativeMediaReferences(
                    "<MPD><BaseURL>https://cdn.example.com/</BaseURL><Period><BaseURL>video/</BaseURL>" +
                    "<SegmentTemplate media=\"$Number$.m4s\" /></Period></MPD>"),
                "absolute ancestor MPD BaseURL resolves nested relative references");
            AssertTrue(
                PlaylistInput.ContainsRelativeMediaReferences(
                    "<MPD><Period><SegmentTemplate media=\"$Number$.m4s\" /></Period></MPD>"),
                "relative MPD template without an absolute BaseURL is detected");
            AssertTrue(
                PlaylistInput.ContainsRelativeMediaReferences(
                    "<MPD><BaseURL>https://cdn.example.com/video/</BaseURL><BaseURL>relative/</BaseURL>" +
                    "<Period><SegmentTemplate media=\"$Number$.m4s\" /></Period></MPD>"),
                "relative MPD BaseURL is not resolved by an absolute sibling BaseURL");
            AssertTrue(
                PlaylistInput.ContainsRelativeMediaReferences(
                    "<MPD><Period><SegmentTemplate index=\"index.sidx\" /></Period></MPD>"),
                "relative MPD index template without an absolute BaseURL is detected");
            AssertTrue(
                !PlaylistInput.ContainsRelativeMediaReferences(
                    "<MPD><BaseURL>https://cdn.example.com/video/</BaseURL><Period>" +
                    "<SegmentTemplate bitstreamSwitching=\"switch.m4s\" /></Period></MPD>"),
                "absolute MPD BaseURL resolves bitstream switching templates");
            AssertTrue(
                PlaylistInput.ContainsRelativeMediaReferences("#EXTM3U\n#EXT-X-MAP:URI=\"//cdn.example.com/init.mp4\"\nhttps://example.com/1.m4s"),
                "scheme-relative HLS URI is detected");
            AssertTrue(HlsKeyValue.IsRecognized("00112233445566778899aabbccddeeff"), "32-character HLS hex key");
            AssertTrue(HlsKeyValue.IsRecognized("ABEiM0RVZneImaq7zN3u/w=="), "16-byte HLS Base64 key");
            AssertTrue(!HlsKeyValue.IsRecognized("not-a-valid-key"), "invalid HLS key is rejected by validation");
            AssertEqual(
                "00112233445566778899aabbccddeeff",
                HlsKeyValue.Normalize("0x00112233445566778899aabbccddeeff"),
                "0x prefix is removed from HLS hex key");
            byte[] keyBytes;
            AssertTrue(
                HlsKeyValue.TryGetBytes("00112233445566778899aabbccddeeff", out keyBytes) && keyBytes.Length == 16,
                "HLS key decodes to exactly 16 bytes");
            string temporarySecret = null;
            try
            {
                temporarySecret = SecretFileStore.Create(keyBytes, ".key");
                AssertTrue(File.Exists(temporarySecret), "protected temporary key file is created");
                AssertTrue(File.ReadAllBytes(temporarySecret).Length == 16, "temporary key file contains 16 bytes");
            }
            finally
            {
                SecretFileStore.Delete(temporarySecret);
            }
            AssertTrue(!File.Exists(temporarySecret), "temporary key file is deleted");

            AssertTrue(
                ExternalToolOutputEncodings.Downloader.CodePage == System.Text.Encoding.Default.CodePage,
                "downloader output uses the Windows default code page");
            AssertTrue(
                ExternalToolOutputEncodings.Ffmpeg.CodePage == System.Text.Encoding.UTF8.CodePage,
                "FFmpeg output remains UTF-8");
            System.Diagnostics.ProcessStartInfo downloaderStartInfo =
                new System.Diagnostics.ProcessStartInfo();
            ExternalToolOutputEncodings.ApplyDownloader(downloaderStartInfo);
            AssertTrue(
                downloaderStartInfo.StandardOutputEncoding.CodePage ==
                    System.Text.Encoding.Default.CodePage &&
                downloaderStartInfo.StandardErrorEncoding.CodePage ==
                    System.Text.Encoding.Default.CodePage,
                "downloader stdout and stderr both use the Windows default code page");
            System.Diagnostics.ProcessStartInfo ffmpegStartInfo =
                new System.Diagnostics.ProcessStartInfo();
            ExternalToolOutputEncodings.ApplyFfmpeg(ffmpegStartInfo);
            AssertTrue(
                ffmpegStartInfo.StandardOutputEncoding.CodePage ==
                    System.Text.Encoding.UTF8.CodePage &&
                ffmpegStartInfo.StandardErrorEncoding.CodePage ==
                    System.Text.Encoding.UTF8.CodePage,
                "FFmpeg stdout and stderr both use UTF-8");

            TestExternalToolOutputParser();
            TestProcessOutputPump();
            TestToolLocator();
            TestDependencyInstaller();
            TestDownloadTemporaryStore();
            TestConversionFileStore();

            Console.WriteLine(_failed == 0 ? "ALL TESTS PASSED" : (_failed + " TEST(S) FAILED"));
            return _failed == 0 ? 0 : 1;
        }

        private static void TestExternalToolOutputParser()
        {
            List<string> logs = new List<string>();
            List<ExternalToolProgress> progressUpdates = new List<ExternalToolProgress>();
            ExternalToolOutputParser parser = new ExternalToolOutputParser(
                logs.Add,
                progressUpdates.Add);

            parser.Append(
                "15:43:34.273 INFO : 内容匹配: HTTP Live Streaming" +
                "15:43:34.273 INFO : 正在解");
            AssertTrue(logs.Count == 1, "a complete timestamp starts a new logical log record");
            parser.Append("析媒体信息...\n");
            AssertTrue(
                logs.Count == 2 &&
                logs[0].EndsWith("HTTP Live Streaming", StringComparison.Ordinal) &&
                logs[1].EndsWith("正在解析媒体信息...", StringComparison.Ordinal),
                "concatenated and cross-chunk timestamp records are split cleanly");

            List<string> splitHeaderLogs = new List<string>();
            ExternalToolOutputParser splitHeaderParser = new ExternalToolOutputParser(
                splitHeaderLogs.Add,
                null);
            splitHeaderParser.Append("15:43:34.270 INFO : first15:43");
            splitHeaderParser.Append(":34.271 INFO : second\n");
            AssertTrue(
                splitHeaderLogs.Count == 2 &&
                splitHeaderLogs[0].EndsWith("first", StringComparison.Ordinal) &&
                splitHeaderLogs[1].EndsWith("second", StringComparison.Ordinal),
                "a timestamp header split across chunks is reconstructed");

            parser.Append("ETA remains 00:00:03 and is not a timestamp\n");
            AssertTrue(
                logs.Count == 3 && logs[2].IndexOf("00:00:03", StringComparison.Ordinal) >= 0,
                "an ETA is not treated as a structured log boundary");

            parser.Append(
                "15:43:34.276 INFO : Vid Kbps | 204 Segments | ~28m55s" +
                "15:43:34.277 INFO : 已选择的流:\n");
            AssertTrue(
                logs.Count == 5 &&
                logs[3].IndexOf("204 Segments", StringComparison.Ordinal) >= 0 &&
                logs[4].EndsWith("已选择的流:", StringComparison.Ordinal),
                "stream summary and selected-stream header remain readable records");

            parser.Append(
                "15:43:35.000 INFO : Vid 1280x720 | 2000 Kbps ------------------------------ " +
                "10/204 4.90% 9.50MB/190.00MB1.25MBps00:02:20");
            AssertTrue(
                progressUpdates.Count == 1 && progressUpdates[0].Current == 10,
                "progress is emitted before a newline or process exit");
            parser.Append(
                "Vid 1280x720 | 2000 Kbps ------------------------------ " +
                "20/204 9.80% 19.00MB/190.00MB1.30MBps00:02:10");
            AssertTrue(
                progressUpdates.Count == 2 &&
                progressUpdates[0].Current == 10 &&
                progressUpdates[1].Current == 20,
                "progress callbacks remain ordered and are not duplicated across chunks");
            parser.Complete();

            ExternalToolProgress lastProgress = progressUpdates[progressUpdates.Count - 1];
            AssertTrue(
                lastProgress.Current == 20 &&
                lastProgress.Total == 204 &&
                Math.Abs(lastProgress.Percent - 9.80) < 0.001,
                "the newest progress frame wins across raw reader chunks");
            AssertTrue(
                string.Equals(lastProgress.DownloadedSize, "19.00MB", StringComparison.Ordinal) &&
                string.Equals(lastProgress.TotalSize, "190.00MB", StringComparison.Ordinal) &&
                string.Equals(lastProgress.Speed, "1.30MBps", StringComparison.Ordinal) &&
                string.Equals(lastProgress.RemainingTime, "00:02:10", StringComparison.Ordinal),
                "progress size, speed, and remaining time are parsed");
            AssertTrue(
                logs.Count == 5,
                "high-frequency progress frames are not appended to historical logs");

            List<string> unicodeLogs = new List<string>();
            List<ExternalToolProgress> unicodeProgress = new List<ExternalToolProgress>();
            ExternalToolOutputParser unicodeParser = new ExternalToolOutputParser(
                unicodeLogs.Add,
                unicodeProgress.Add);
            unicodeParser.Append(
                "Vid 1280x720 | 2000 Kbps ━━━━━━━━━━━━━━━━━━━━ " +
                "1/2 50.00% 1.00MB/2.00MB1.00MBps00:00:01");
            unicodeParser.Complete();
            AssertTrue(
                unicodeProgress.Count == 1 && unicodeProgress[0].Current == 1 && unicodeLogs.Count == 0,
                "Unicode progress bars are parsed and suppressed from historical logs");

            List<ExternalToolProgress> barlessProgress = new List<ExternalToolProgress>();
            ExternalToolOutputParser barlessParser = new ExternalToolOutputParser(
                null,
                barlessProgress.Add);
            barlessParser.Append("Aud AAC 2/4 50.00% 2.00MB/4.00MB500.00KBps00:00:04");
            barlessParser.Complete();
            AssertTrue(
                barlessProgress.Count == 1 && barlessProgress[0].Current == 2,
                "barless progress output remains detectable");

            List<string> mixedLogs = new List<string>();
            List<ExternalToolProgress> mixedProgress = new List<ExternalToolProgress>();
            ExternalToolOutputParser mixedParser = new ExternalToolOutputParser(
                mixedLogs.Add,
                mixedProgress.Add);
            mixedParser.Append(
                "Vid 320x180 ---------------- 1/10 10.00% 1.00MB/10.00MB1.00MBps00:00:09" +
                "16:00:01.000 WARN : network retry\n");
            mixedParser.Complete();
            AssertTrue(
                mixedProgress.Count == 1 && mixedLogs.Count == 1 &&
                mixedLogs[0].EndsWith("network retry", StringComparison.Ordinal),
                "a warning after a progress frame remains a readable log record");

            List<ExternalToolProgress> invalidProgress = new List<ExternalToolProgress>();
            ExternalToolOutputParser invalidParser = new ExternalToolOutputParser(
                null,
                invalidProgress.Add);
            invalidParser.Append("Vid invalid 12/10 120.00%");
            invalidParser.Complete();
            AssertTrue(invalidProgress.Count == 0, "out-of-range progress is ignored");

            List<string> oversizedLogs = new List<string>();
            ExternalToolOutputParser oversizedParser = new ExternalToolOutputParser(
                oversizedLogs.Add,
                null);
            oversizedParser.Append("16:00:00.000 ERROR: " + new string('X', 66000));
            oversizedParser.Complete();
            AssertTrue(
                oversizedLogs.Count >= 2 &&
                oversizedLogs[0].IndexOf("单条日志过长", StringComparison.Ordinal) >= 0 &&
                oversizedLogs[0].IndexOf("ERROR", StringComparison.Ordinal) >= 0 &&
                string.Join("\n", oversizedLogs.ToArray()).IndexOf("高频进度", StringComparison.Ordinal) < 0,
                "an oversized ordinary error is truncated honestly instead of being labeled as progress");
        }

        private static void TestProcessOutputPump()
        {
            System.Text.StringBuilder standardOutput = new System.Text.StringBuilder();
            System.Text.StringBuilder standardError = new System.Text.StringBuilder();
            int completedReaders = 0;
            ProcessOutputPump pump = new ProcessOutputPump(
                new StringReader("stdout without a newline"),
                new StringReader("stderr without a newline"),
                delegate(string chunk) { standardOutput.Append(chunk); },
                delegate(string chunk) { standardError.Append(chunk); },
                delegate { System.Threading.Interlocked.Increment(ref completedReaders); },
                delegate { System.Threading.Interlocked.Increment(ref completedReaders); });
            pump.Start();
            pump.WaitForCompletion();

            AssertEqual(
                "stdout without a newline",
                standardOutput.ToString(),
                "raw stdout pump does not wait for a line delimiter");
            AssertEqual(
                "stderr without a newline",
                standardError.ToString(),
                "raw stderr pump does not wait for a line delimiter");
            AssertTrue(completedReaders == 2, "both raw output readers report completion");

            int handlerCalls = 0;
            string longOutput = new string('x', 5000);
            ProcessOutputPump throwingHandlerPump = new ProcessOutputPump(
                new StringReader(longOutput),
                new StringReader(string.Empty),
                delegate(string chunk)
                {
                    handlerCalls++;
                    if (handlerCalls == 1)
                    {
                        throw new InvalidOperationException("expected test exception");
                    }
                },
                null,
                null,
                null);
            throwingHandlerPump.Start();
            AssertTrue(
                throwingHandlerPump.WaitForCompletion(2000) && handlerCalls >= 2,
                "a handler exception does not stop the output pipe from being drained");

            BlockingTextReader blockingReader = new BlockingTextReader();
            ProcessOutputPump blockingPump = new ProcessOutputPump(
                blockingReader,
                new StringReader(string.Empty),
                null,
                null,
                null,
                null);
            blockingPump.Start();
            AssertTrue(
                !blockingPump.WaitForCompletion(50),
                "a pipe that remains open does not block the caller indefinitely");
            blockingPump.Stop();
            AssertTrue(
                blockingPump.WaitForCompletion(1000),
                "stopping the output pump unblocks a pending reader");
        }

        private sealed class BlockingTextReader : TextReader
        {
            private readonly System.Threading.ManualResetEvent _released =
                new System.Threading.ManualResetEvent(false);

            public override int Read(char[] buffer, int index, int count)
            {
                _released.WaitOne();
                return 0;
            }

            protected override void Dispose(bool disposing)
            {
                _released.Set();
                base.Dispose(disposing);
            }
        }

        private static void TestToolLocator()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "M3u8DownloaderGuiToolTests_" + Guid.NewGuid().ToString("N"));
            string originalTestRoot = Environment.GetEnvironmentVariable("M3U8_GUI_TEST_ROOT");

            try
            {
                string fakeDriveRoot = Path.Combine(directory, "FakeE");
                string downloads = Path.Combine(fakeDriveRoot, "Downloads");
                Directory.CreateDirectory(downloads);
                string downloader = Path.Combine(downloads, "N_m3u8DL-RE.exe");
                File.WriteAllBytes(downloader, new byte[] { 1 });

                List<string> commonDirectories = ToolLocator.BuildCommonToolDirectories(
                    null,
                    null,
                    null,
                    new[] { fakeDriveRoot });
                AssertContainsPath(
                    commonDirectories,
                    downloads,
                    "tool locator includes Downloads on another fixed drive");

                Environment.SetEnvironmentVariable("M3U8_GUI_TEST_ROOT", downloads);
                string found = ToolLocator.FindInPathValues(
                    "N_m3u8DL-RE.exe",
                    "\"%M3U8_GUI_TEST_ROOT%\"");
                AssertPathEqual(
                    downloader,
                    found,
                    "tool PATH values expand environment variables and outer quotes");
            }
            finally
            {
                Environment.SetEnvironmentVariable("M3U8_GUI_TEST_ROOT", originalTestRoot);
                try
                {
                    Directory.Delete(directory, true);
                }
                catch
                {
                }
            }
        }

        private static void TestDependencyInstaller()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "M3u8DownloaderGuiDependencyTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                string hashFile = Path.Combine(directory, "hash.bin");
                File.WriteAllBytes(hashFile, Encoding.ASCII.GetBytes("abc"));
                AssertEqual(
                    "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
                    DependencyInstaller.ComputeSha256(hashFile, CancellationToken.None),
                    "dependency installer computes SHA-256");
                AssertTrue(
                    DependencyInstaller.HashMatches(
                        hashFile,
                        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                        CancellationToken.None),
                    "dependency installer compares SHA-256 case-insensitively");

                System.Net.HttpWebRequest directRequest =
                    DependencyInstaller.CreateDirectDownloadRequest("https://github.com/");
                try
                {
                    AssertTrue(
                        directRequest.Proxy == null,
                        "dependency downloads bypass the Windows system proxy");
                    AssertEqual(
                        "GET",
                        directRequest.Method,
                        "dependency downloads use HTTP GET");
                }
                finally
                {
                    directRequest.Abort();
                }

                CancellationTokenSource cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                AssertThrows<OperationCanceledException>(
                    delegate
                    {
                        DependencyInstaller.ComputeSha256(hashFile, cancelled.Token);
                    },
                    "dependency hash calculation honors cancellation");
                cancelled.Dispose();

                string validZip = Path.Combine(directory, "valid.zip");
                CreateTestZip(
                    validZip,
                    new[] { "nested/N_m3u8DL-RE.exe" },
                    new[] { new byte[] { 1, 2, 3 } });
                string extracted = Path.Combine(directory, "controlled-output.exe");
                DependencyInstaller.ExtractNamedZipEntry(
                    validZip,
                    "N_m3u8DL-RE.exe",
                    extracted,
                    3,
                    CancellationToken.None);
                AssertTrue(
                    File.Exists(extracted) &&
                    File.ReadAllBytes(extracted).Length == 3 &&
                    File.ReadAllBytes(extracted)[0] == 1,
                    "dependency ZIP extraction writes only the controlled output");

                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        DependencyInstaller.ExtractNamedZipEntry(
                            validZip,
                            "missing.exe",
                            Path.Combine(directory, "missing.exe"),
                            3,
                            CancellationToken.None);
                    },
                    "dependency ZIP rejects a missing payload");
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        DependencyInstaller.ExtractNamedZipEntry(
                            validZip,
                            "N_m3u8DL-RE.exe",
                            Path.Combine(directory, "wrong-length.exe"),
                            4,
                            CancellationToken.None);
                    },
                    "dependency ZIP rejects an unexpected expanded length");
                CancellationTokenSource extractionCancellation =
                    new CancellationTokenSource();
                extractionCancellation.Cancel();
                AssertThrows<OperationCanceledException>(
                    delegate
                    {
                        DependencyInstaller.ExtractNamedZipEntry(
                            validZip,
                            "N_m3u8DL-RE.exe",
                            Path.Combine(directory, "cancelled-extract.exe"),
                            3,
                            extractionCancellation.Token);
                    },
                    "dependency ZIP extraction honors cancellation");
                extractionCancellation.Dispose();

                string duplicateZip = Path.Combine(directory, "duplicate.zip");
                CreateTestZip(
                    duplicateZip,
                    new[]
                    {
                        "first/ffmpeg.exe",
                        "second/ffmpeg.exe"
                    },
                    new[]
                    {
                        new byte[] { 4 },
                        new byte[] { 5 }
                    });
                AssertThrows<InvalidDataException>(
                    delegate
                    {
                        DependencyInstaller.ExtractNamedZipEntry(
                            duplicateZip,
                            "ffmpeg.exe",
                            Path.Combine(directory, "duplicate.exe"),
                            1,
                            CancellationToken.None);
                    },
                    "dependency ZIP rejects duplicate payload names");

                AssertTrue(
                    DependencyInstaller.DownloaderArchiveSha256.Length == 64 &&
                    DependencyInstaller.FfmpegSevenZipSha256.Length == 64 &&
                    DependencyInstaller.DownloaderArchiveLength > 0 &&
                    DependencyInstaller.FfmpegSevenZipLength > 0,
                    "dependency catalog pins lengths and SHA-256 values");
                AssertEqual(
                    "3825FD42EE502F98A9378F6FDDDB2F7822709F521806214F466DB6935C950F1A",
                    DependencyInstaller.DownloaderArchiveSha256,
                    "downloader archive hash is pinned");
                AssertEqual(
                    "E25B682664025D49034C981AFB4BAE36238A40F29A3CC1C713AD9A8B5B3528F6",
                    DependencyInstaller.FfmpegSevenZipSha256,
                    "FFmpeg archive hash is pinned");
                AssertEqual(
                    "1326DDE4C84FF1F96FE6B8916C5BED29E163E9B5DCCF995F6F3DB069D143EC5E",
                    DependencyInstaller.FfmpegExecutableSha256,
                    "FFmpeg executable hash is pinned");
                AssertPathEqual(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "N_m3u8DL-RE-GUI",
                        "tools"),
                    ToolLocator.GetManagedToolsDirectory(),
                    "managed dependency directory is stable under LocalAppData");
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch
                {
                }
            }
        }

        private static void CreateTestZip(
            string path,
            string[] entryNames,
            byte[][] contents)
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                for (int index = 0; index < entryNames.Length; index++)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(entryNames[index]);
                    using (Stream output = entry.Open())
                    {
                        output.Write(contents[index], 0, contents[index].Length);
                    }
                }
            }
        }

        private static void TestDownloadTemporaryStore()
        {
            string ownedDirectory = null;
            string unownedDirectory = null;
            string managedRoot = null;

            try
            {
                ownedDirectory = DownloadTemporaryStore.Create();
                managedRoot = Directory.GetParent(ownedDirectory).FullName;
                string nested = Path.Combine(ownedDirectory, "video", "segments");
                Directory.CreateDirectory(nested);
                File.WriteAllBytes(Path.Combine(nested, "000.ts.tmp"), new byte[] { 1, 2, 3 });
                AssertTrue(
                    DownloadTemporaryStore.Delete(ownedDirectory) && !Directory.Exists(ownedDirectory),
                    "owned download temporary directory is deleted recursively");
                ownedDirectory = null;

                unownedDirectory = Path.Combine(
                    managedRoot,
                    "download_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(unownedDirectory);
                File.WriteAllText(Path.Combine(unownedDirectory, "user-file.txt"), "keep");
                AssertTrue(
                    !DownloadTemporaryStore.Delete(unownedDirectory) && Directory.Exists(unownedDirectory),
                    "download temporary cleanup rejects a directory without an ownership marker");
            }
            finally
            {
                DownloadTemporaryStore.Delete(ownedDirectory);
                try
                {
                    if (!string.IsNullOrWhiteSpace(unownedDirectory) && Directory.Exists(unownedDirectory))
                    {
                        Directory.Delete(unownedDirectory, true);
                    }

                    if (!string.IsNullOrWhiteSpace(managedRoot) &&
                        Directory.Exists(managedRoot) &&
                        Directory.GetFileSystemEntries(managedRoot).Length == 0)
                    {
                        Directory.Delete(managedRoot, false);
                    }
                }
                catch
                {
                }
            }
        }

        private static void TestConversionFileStore()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "M3u8DownloaderGuiTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            try
            {
                string newTemporary = Path.Combine(directory, "new.partial.mp4");
                string newFinal = Path.Combine(directory, "new.mp4");
                File.WriteAllBytes(newTemporary, new byte[] { 1, 2, 3 });
                string error;
                AssertTrue(
                    ConversionFileStore.TryCommit(newTemporary, newFinal, out error),
                    "conversion output is moved to a new final path");
                AssertTrue(
                    File.Exists(newFinal) && File.ReadAllBytes(newFinal)[0] == 1,
                    "new conversion output contains the temporary file data");

                string replacementTemporary = Path.Combine(directory, "replace.partial.mp4");
                string replacementFinal = Path.Combine(directory, "replace.mp4");
                File.WriteAllBytes(replacementFinal, new byte[] { 9, 9, 9 });
                File.WriteAllBytes(replacementTemporary, new byte[] { 4, 5, 6 });
                AssertTrue(
                    ConversionFileStore.TryCommit(replacementTemporary, replacementFinal, out error),
                    "conversion output atomically replaces an existing target");
                AssertTrue(
                    File.ReadAllBytes(replacementFinal)[0] == 4,
                    "replacement target contains the completed conversion");

                AssertTrue(
                    !ConversionFileStore.TryCommit(
                        Path.Combine(directory, "missing.partial.mp4"),
                        replacementFinal,
                        out error),
                    "missing conversion temporary file is rejected");
                AssertTrue(
                    File.ReadAllBytes(replacementFinal)[0] == 4,
                    "failed conversion commit preserves the existing target");

                string abandonedTemporary = Path.Combine(directory, "abandoned.partial.mp4");
                File.WriteAllBytes(abandonedTemporary, new byte[] { 7 });
                AssertTrue(
                    ConversionFileStore.Delete(abandonedTemporary) && !File.Exists(abandonedTemporary),
                    "abandoned conversion temporary file is deleted");
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, true);
                }
                catch
                {
                }
            }
        }

        private static void AssertEqual(string expected, string actual, string testName)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                _failed++;
                Console.Error.WriteLine(
                    "FAIL: " + testName + Environment.NewLine +
                    "  expected: " + expected + Environment.NewLine +
                    "  actual:   " + actual);
            }
        }

        private static void AssertPathEqual(string expected, string actual, string testName)
        {
            string expectedPath = expected == null ? null : Path.GetFullPath(expected);
            string actualPath = actual == null ? null : Path.GetFullPath(actual);
            if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
            {
                _failed++;
                Console.Error.WriteLine(
                    "FAIL: " + testName + Environment.NewLine +
                    "  expected: " + expectedPath + Environment.NewLine +
                    "  actual:   " + actualPath);
            }
        }

        private static void AssertContainsPath(
            IEnumerable<string> paths,
            string expected,
            string testName)
        {
            string expectedPath = Path.GetFullPath(expected);
            foreach (string path in paths)
            {
                if (string.Equals(
                    expectedPath,
                    Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            _failed++;
            Console.Error.WriteLine("FAIL: " + testName + Environment.NewLine + "  missing: " + expectedPath);
        }

        private static void AssertTrue(bool condition, string testName)
        {
            if (!condition)
            {
                _failed++;
                Console.Error.WriteLine("FAIL: " + testName);
            }
        }

        private static void AssertThrows<TException>(Action action, string testName)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception exception)
            {
                _failed++;
                Console.Error.WriteLine(
                    "FAIL: " + testName + Environment.NewLine +
                    "  expected exception: " + typeof(TException).FullName + Environment.NewLine +
                    "  actual exception:   " + exception.GetType().FullName);
                return;
            }

            _failed++;
            Console.Error.WriteLine(
                "FAIL: " + testName + Environment.NewLine +
                "  expected exception: " + typeof(TException).FullName);
        }
    }
}
