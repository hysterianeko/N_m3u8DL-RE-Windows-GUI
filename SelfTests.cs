using System;
using System.Collections.Generic;
using System.IO;

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

            TestToolLocator();
            TestDownloadTemporaryStore();
            TestConversionFileStore();

            Console.WriteLine(_failed == 0 ? "ALL TESTS PASSED" : (_failed + " TEST(S) FAILED"));
            return _failed == 0 ? 0 : 1;
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
    }
}
