using System;
using System.IO;
using System.Text;

namespace M3u8DownloaderGui
{
    internal static class DownloadResumeStoreSelfTests
    {
        public static void Run(Action<bool, string> assert)
        {
            if (assert == null)
            {
                throw new ArgumentNullException("assert");
            }

            string testRoot = Path.Combine(
                Path.GetTempPath(),
                "M3u8DownloaderGui_ResumeTests_" + Guid.NewGuid().ToString("N"));
            IDisposable rootScope = null;
            try
            {
                rootScope = DownloadResumeStore.UseIsolatedRootForTests(testRoot);
                TestProtectedRoundTripAndRecovery(assert, testRoot);
                TestDiscardRetry(assert, testRoot);
                TestOrphanCleanup(assert);
                assert(
                    !Directory.Exists(testRoot) ||
                    Directory.GetFileSystemEntries(testRoot).Length == 0,
                    "resume store tests leave no managed cache or lease files behind");
            }
            catch (Exception exception)
            {
                assert(false, "resume store self-test exception: " + exception.Message);
            }
            finally
            {
                if (rootScope != null)
                {
                    rootScope.Dispose();
                }

                try
                {
                    if (Directory.Exists(testRoot))
                    {
                        Directory.Delete(testRoot, true);
                    }
                }
                catch
                {
                }
            }
        }

        private static void TestProtectedRoundTripAndRecovery(
            Action<bool, string> assert,
            string testRoot)
        {
            string cacheDirectory = DownloadResumeStore.CreateOwnedCacheForTests();
            string originalImportedPath = Path.Combine(testRoot, "deleted_source.m3u8");
            string playlist =
                "#EXTM3U\n" +
                "#EXT-X-TOKEN=private-playlist-token\n" +
                "#EXTINF:4,\n" +
                "https://cdn.example.test/video0.ts?token=private-query\n";

            MediaRequestHeaders headers = new MediaRequestHeaders();
            headers.Referer = "https://example.test/watch/private-title";
            headers.Cookie = "session=private-cookie";
            headers.UserAgent = "ResumeStoreTest/1.0";
            headers.Origin = "https://example.test";
            headers.Authorization = "Bearer private-authorization";
            headers.SourceUrl = "https://cdn.example.test/video0.ts?token=source-secret";
            headers.TrySetAdditionalHeader("X-Playback-Token", "private-custom-header");

            DownloadResumeManifest manifest = new DownloadResumeManifest();
            manifest.CacheDirectory = cacheDirectory;
            manifest.State = DownloadResumeState.Paused;
            manifest.SaveDirectory = Path.Combine(testRoot, "private-output-directory");
            manifest.FileName = "private-output-name";
            manifest.DownloaderPath = Path.Combine(testRoot, "private-downloader.exe");
            manifest.FfmpegPath = Path.Combine(testRoot, "private-ffmpeg.exe");
            manifest.MuxToMp4 = true;
            manifest.Input = originalImportedPath;
            manifest.InputIsImportedPlaylist = true;
            manifest.ImportedPlaylistContent = playlist;
            manifest.HlsKey = "00112233445566778899aabbccddeeff";
            manifest.HlsIv = "ffeeddccbbaa99887766554433221100";
            manifest.CapturedHeaders = headers;

            string saveError;
            assert(
                DownloadResumeStore.TrySave(manifest, out saveError),
                "restart resume manifest is saved atomically");
            assert(
                manifest.RetentionDays == 3 && manifest.Revision == 1,
                "restart resume manifest defaults to a bounded three-day retention");

            string manifestPath = Path.Combine(
                cacheDirectory,
                DownloadResumeStore.ManifestFileName);
            string rawManifest = File.ReadAllText(manifestPath, Encoding.UTF8);
            bool containsPrivateData =
                rawManifest.IndexOf("private-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawManifest.IndexOf("001122334455", StringComparison.OrdinalIgnoreCase) >= 0 ||
                rawManifest.IndexOf("example.test", StringComparison.OrdinalIgnoreCase) >= 0;
            assert(
                !containsPrivateData,
                "resume request, playlist, paths, keys, and headers are absent from plaintext XML");

            DownloadResumeManifest loaded;
            string loadError;
            bool loadedSuccessfully = DownloadResumeStore.TryLoad(
                cacheDirectory,
                out loaded,
                out loadError);
            assert(loadedSuccessfully, "DPAPI-protected restart resume manifest is loaded");
            assert(
                loadedSuccessfully &&
                loaded.ImportedPlaylistContent == playlist &&
                loaded.HlsKey == manifest.HlsKey &&
                loaded.HlsIv == manifest.HlsIv &&
                loaded.CapturedHeaders != null &&
                loaded.CapturedHeaders.Cookie == headers.Cookie &&
                loaded.CapturedHeaders.Authorization == headers.Authorization &&
                loaded.CapturedHeaders.AdditionalHeaders["X-Playback-Token"] ==
                    "private-custom-header",
                "DPAPI round trip preserves all sensitive resume fields");

            string materializedInput = null;
            string materializeError = null;
            bool materialized = loadedSuccessfully && DownloadResumeStore.TryMaterializeInput(
                loaded,
                out materializedInput,
                out materializeError);
            assert(
                materialized && File.Exists(materializedInput) &&
                File.ReadAllText(materializedInput, Encoding.UTF8) == playlist,
                "deleted imported playlist is reconstructed inside the owned cache");
            if (materialized && File.Exists(materializedInput))
            {
                File.Delete(materializedInput);
            }

            string tamperedMetadata = rawManifest.Replace(
                "<state>Paused</state>",
                "<state>Failed</state>");
            File.WriteAllText(manifestPath, tamperedMetadata, new UTF8Encoding(false));
            assert(
                !DownloadResumeStore.TryLoad(cacheDirectory, out loaded, out loadError),
                "public resume metadata tampering fails the protected integrity check");

            assert(
                DownloadResumeStore.TrySave(manifest, out saveError) &&
                DownloadResumeStore.TrySave(manifest, out saveError),
                "a valid atomic update replaces a damaged primary manifest");
            TamperProtectedPayload(manifestPath);
            bool recoveredBackup = DownloadResumeStore.TryLoad(
                cacheDirectory,
                out loaded,
                out loadError);
            assert(
                recoveredBackup && loaded.Revision == manifest.Revision - 1,
                "a damaged primary manifest falls back to the previous atomic backup");

            DownloadResumeActivityLease lease;
            string leaseError;
            bool acquiredLease = DownloadResumeStore.TryAcquireActivityLease(
                cacheDirectory,
                out lease,
                out leaseError);
            DownloadResumeActivityLease secondLease;
            assert(acquiredLease, "resume cache activity lease is acquired");
            assert(
                !DownloadResumeStore.TryAcquireActivityLease(
                    cacheDirectory,
                    out secondLease,
                    out leaseError),
                "a second process cannot acquire the same resume cache lease");
            if (secondLease != null)
            {
                secondLease.Dispose();
            }
            string protectedWhileActive = Path.Combine(cacheDirectory, "active-segment.tmp");
            File.WriteAllText(protectedWhileActive, "keep", new UTF8Encoding(false));
            string activeDiscardError;
            assert(
                !DownloadResumeStore.TryDiscard(cacheDirectory, out activeDiscardError) &&
                File.Exists(protectedWhileActive) && File.Exists(manifestPath),
                "discard cannot partially delete a cache while its activity lease is held");
            assert(
                DownloadResumeStore.DiscoverRecoverableTasks().Count == 0,
                "discovery does not offer a task already leased by another process");

            DownloadResumeCleanupResult activeCleanup = DownloadResumeStore.CleanupExpired(
                DateTime.UtcNow.AddDays(4));
            assert(
                Directory.Exists(cacheDirectory) && activeCleanup.ActiveSkipped >= 1,
                "expiration cleanup skips an actively leased resume cache");
            if (acquiredLease)
            {
                lease.Dispose();
            }

            DownloadResumeCleanupResult expiredCleanup = DownloadResumeStore.CleanupExpired(
                DateTime.UtcNow.AddDays(4));
            assert(
                expiredCleanup.ExpiredDeleted == 1 && !Directory.Exists(cacheDirectory),
                "expired recoverable cache is deleted after its lease is released");
        }

        private static void TestDiscardRetry(Action<bool, string> assert, string testRoot)
        {
            string cacheDirectory = DownloadResumeStore.CreateOwnedCacheForTests();
            DownloadResumeManifest manifest = new DownloadResumeManifest();
            manifest.CacheDirectory = cacheDirectory;
            manifest.State = DownloadResumeState.Failed;
            manifest.SaveDirectory = Path.Combine(testRoot, "discard-output");
            manifest.FileName = "discard-test";
            manifest.DownloaderPath = Path.Combine(testRoot, "downloader.exe");
            manifest.FfmpegPath = Path.Combine(testRoot, "ffmpeg.exe");
            manifest.Input = "https://media.example.test/video.m3u8";
            string saveError;
            assert(
                DownloadResumeStore.TrySave(manifest, out saveError),
                "discard retry test creates a valid recoverable manifest");
            DownloadResumeManifest nullHeaderRoundTrip;
            string loadError;
            assert(
                DownloadResumeStore.TryLoad(
                    cacheDirectory,
                    out nullHeaderRoundTrip,
                    out loadError) &&
                nullHeaderRoundTrip.CapturedHeaders == null,
                "resume manifest without captured headers round trips correctly");

            string lockedPath = Path.Combine(cacheDirectory, "locked-segment.tmp");
            string discardError;
            using (FileStream locked = new FileStream(
                lockedPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                assert(
                    !DownloadResumeStore.TryDiscard(cacheDirectory, out discardError) &&
                    Directory.Exists(cacheDirectory),
                    "failed cache deletion leaves an owned directory for a later retry");
            }

            DownloadResumeActivityLease pendingLease;
            string pendingLeaseError;
            assert(
                !DownloadResumeStore.TryAcquireActivityLease(
                    cacheDirectory,
                    out pendingLease,
                    out pendingLeaseError),
                "discard-pending cache cannot be leased for recovery");
            if (pendingLease != null)
            {
                pendingLease.Dispose();
            }
            assert(
                DownloadResumeStore.DiscoverRecoverableTasks().Count == 0,
                "discovery never offers a cache marked discard-pending");

            DownloadResumeCleanupResult cleanup = DownloadResumeStore.CleanupExpired();
            assert(
                cleanup.DiscardPendingDeleted == 1 && !Directory.Exists(cacheDirectory),
                "discard-pending cache is removed after the file lock is released");

            string readOnlyCache = DownloadResumeStore.CreateOwnedCacheForTests();
            string readOnlyFile = Path.Combine(readOnlyCache, "read-only-segment.ts");
            File.WriteAllText(readOnlyFile, "segment", new UTF8Encoding(false));
            File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
            assert(
                DownloadResumeStore.TryDiscard(readOnlyCache, out discardError) &&
                !Directory.Exists(readOnlyCache),
                "read-only cached segments do not become permanent leftovers");
        }

        private static void TestOrphanCleanup(Action<bool, string> assert)
        {
            string cacheDirectory = DownloadResumeStore.CreateOwnedCacheForTests();
            string marker = Path.Combine(cacheDirectory, ".m3u8-gui-owner");
            DateTime now = DateTime.UtcNow;
            DateTime recent = now.AddDays(-2);
            File.SetLastWriteTimeUtc(marker, recent);
            Directory.SetLastWriteTimeUtc(cacheDirectory, recent);
            DownloadResumeCleanupResult recentCleanup = DownloadResumeStore.CleanupExpired(now);
            assert(
                recentCleanup.OrphanedDeleted == 0 && Directory.Exists(cacheDirectory),
                "owner-valid orphan is retained before the three-day deadline");

            DateTime old = now.AddDays(-4);
            File.SetLastWriteTimeUtc(marker, old);
            Directory.SetLastWriteTimeUtc(cacheDirectory, old);

            DownloadResumeCleanupResult cleanup = DownloadResumeStore.CleanupExpired(now);
            assert(
                cleanup.OrphanedDeleted == 1 && !Directory.Exists(cacheDirectory),
                "owner-valid orphan cache is removed after the three-day grace period");

            string futureCache = DownloadResumeStore.CreateOwnedCacheForTests();
            string futureMarker = Path.Combine(futureCache, ".m3u8-gui-owner");
            DateTime future = now.AddYears(1);
            File.SetLastWriteTimeUtc(futureMarker, future);
            Directory.SetLastWriteTimeUtc(futureCache, future);
            DownloadResumeCleanupResult normalized = DownloadResumeStore.CleanupExpired(now);
            assert(
                normalized.OrphanedDeleted == 0 && Directory.Exists(futureCache),
                "future orphan timestamps are normalized without immediate deletion");
            DownloadResumeCleanupResult futureCleanup =
                DownloadResumeStore.CleanupExpired(now.AddDays(4));
            assert(
                futureCleanup.OrphanedDeleted == 1 && !Directory.Exists(futureCache),
                "future-dated orphan still expires after the bounded grace period");
        }

        private static void TamperProtectedPayload(string path)
        {
            string xml = File.ReadAllText(path, Encoding.UTF8);
            int elementStart = xml.IndexOf("<protectedPayload", StringComparison.Ordinal);
            int contentStart = xml.IndexOf('>', elementStart) + 1;
            while (contentStart < xml.Length && char.IsWhiteSpace(xml[contentStart]))
            {
                contentStart++;
            }

            if (elementStart < 0 || contentStart <= 0 || contentStart >= xml.Length)
            {
                throw new InvalidDataException("Protected payload was not found in test manifest.");
            }

            char replacement = xml[contentStart] == 'A' ? 'B' : 'A';
            xml = xml.Substring(0, contentStart) + replacement + xml.Substring(contentStart + 1);
            File.WriteAllText(path, xml, new UTF8Encoding(false));
        }
    }
}
