using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;

namespace M3u8DownloaderGui
{
    internal enum DownloadResumeState
    {
        Running,
        Paused,
        Failed,
        Interrupted
    }

    // Serializable state needed to reconstruct a DownloadRequest after the GUI restarts.
    // Fields that can contain credentials are stored in the DPAPI-protected payload.
    internal sealed class DownloadResumeManifest
    {
        public string TaskId;
        public long Revision;
        public DateTime CreatedUtc;
        public DateTime UpdatedUtc;
        public DateTime DeleteAfterUtc;
        public int RetentionDays = DownloadResumeStore.DefaultRetentionDays;
        public DownloadResumeState State;
        public string CacheDirectory;
        public string SaveDirectory;
        public string FileName;
        public string DownloaderPath;
        public string FfmpegPath;
        public bool MuxToMp4;

        public string Input;
        public bool InputIsImportedPlaylist;
        public string ImportedPlaylistContent;
        public string HlsKey;
        public string HlsIv;
        public MediaRequestHeaders CapturedHeaders;

        public DownloadResumeManifest Clone()
        {
            DownloadResumeManifest copy = (DownloadResumeManifest)MemberwiseClone();
            copy.CapturedHeaders = CapturedHeaders == null ? null : CapturedHeaders.Clone();
            return copy;
        }
    }

    internal sealed class DownloadResumeCleanupResult
    {
        public int ExpiredDeleted;
        public int OrphanedDeleted;
        public int DiscardPendingDeleted;
        public int ActiveSkipped;
        public int DeleteFailed;
    }

    // Holding this object prevents this task from being mistaken for abandoned cache.
    internal sealed class DownloadResumeActivityLease : IDisposable
    {
        private FileStream _stream;

        internal DownloadResumeActivityLease(FileStream stream)
        {
            _stream = stream;
        }

        public void Dispose()
        {
            FileStream stream = Interlocked.Exchange(ref _stream, null);
            if (stream != null)
            {
                stream.Dispose();
            }
        }
    }

    internal static class DownloadResumeStore
    {
        internal const int DefaultRetentionDays = 3;
        internal const int MaximumRetentionDays = 3;
        internal const int OrphanRetentionDays = 3;

        private const int FormatVersion = 1;
        private const int MaximumManifestBytes = 8 * 1024 * 1024;
        private const int MaximumPlaylistCharacters = 6 * 1024 * 1024;
        private const int MaximumOrdinaryValueCharacters = 32768;
        private const int MaximumHeaderValueCharacters = 65536;
        private const int MaximumAdditionalHeaders = 128;
        private const string DirectoryPrefix = "download_";
        private const string OwnerMarkerName = ".m3u8-gui-owner";
        private const string OwnerMarkerPrefix = "M3u8DownloaderGui:v1:";
        private const string ManifestName = ".m3u8-gui-resume.xml";
        private const string ManifestBackupName = ".m3u8-gui-resume.xml.bak";
        private const string ActivityLeasePrefix = ".m3u8-gui-active-";
        private const string ActivityLeaseSuffix = ".lock";
        private const string DiscardPendingName = ".m3u8-gui-discard-pending";

        private static readonly string DefaultRootDirectory = Path.Combine(
            Path.GetTempPath(),
            @"N_m3u8DL-RE-GUI\Downloads");

        private static readonly object TestRootSync = new object();
        private static string _testRootDirectory;

        private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes(
            "M3u8DownloaderGui:DownloadResumeManifest:v1");

        public static string ManifestFileName
        {
            get { return ManifestName; }
        }

        internal static string ManagedRootDirectory
        {
            get { return GetRootDirectory(); }
        }

        // Tests must use an isolated root so discovery and cleanup cannot see real downloads.
        internal static IDisposable UseIsolatedRootForTests(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentNullException("rootDirectory");
            }

            string fullPath = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string productionRoot = Path.GetFullPath(DefaultRootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar);
            if (!fullPath.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullPath, productionRoot, StringComparison.OrdinalIgnoreCase) ||
                productionRoot.StartsWith(
                    fullPath + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The isolated resume test root must be a non-production temporary directory.",
                    "rootDirectory");
            }

            lock (TestRootSync)
            {
                if (_testRootDirectory != null)
                {
                    throw new InvalidOperationException("A resume-store test root is already active.");
                }

                _testRootDirectory = fullPath;
                return new TestRootScope(fullPath);
            }
        }

        internal static string CreateOwnedCacheForTests()
        {
            string root;
            lock (TestRootSync)
            {
                root = _testRootDirectory;
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException("No isolated resume-store test root is active.");
            }

            Directory.CreateDirectory(root);
            string taskId = Guid.NewGuid().ToString("N");
            string path = Path.Combine(root, DirectoryPrefix + taskId);
            Directory.CreateDirectory(path);
            File.WriteAllText(
                Path.Combine(path, OwnerMarkerName),
                OwnerMarkerPrefix + taskId,
                new UTF8Encoding(false));
            return path;
        }

        public static void Save(DownloadResumeManifest manifest)
        {
            string errorMessage;
            if (!TrySave(manifest, out errorMessage))
            {
                throw new InvalidOperationException(errorMessage);
            }
        }

        public static bool TrySave(DownloadResumeManifest manifest, out string errorMessage)
        {
            errorMessage = null;
            if (manifest == null)
            {
                errorMessage = "The resume manifest is missing.";
                return false;
            }

            string cacheDirectory;
            string taskId;
            if (!TryGetOwnedCacheDirectory(manifest.CacheDirectory, out cacheDirectory, out taskId))
            {
                errorMessage = "The resume cache directory is not owned by this application.";
                return false;
            }

            DownloadResumeManifest snapshot = manifest.Clone();
            snapshot.CacheDirectory = cacheDirectory;
            if (!string.IsNullOrWhiteSpace(snapshot.TaskId) &&
                !string.Equals(snapshot.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "The resume task identifier does not match its cache directory.";
                return false;
            }

            snapshot.TaskId = taskId;
            DateTime now = DateTime.UtcNow;
            snapshot.CreatedUtc = NormalizeUtc(
                snapshot.CreatedUtc == DateTime.MinValue ? now : snapshot.CreatedUtc);
            snapshot.UpdatedUtc = now;
            snapshot.RetentionDays = NormalizeRetentionDays(snapshot.RetentionDays);
            snapshot.DeleteAfterUtc = now.AddDays(snapshot.RetentionDays);
            snapshot.Revision = snapshot.Revision < 0 ? 1 : snapshot.Revision + 1;

            if (!ValidateForSave(snapshot, out errorMessage))
            {
                return false;
            }

            byte[] serialized = null;
            try
            {
                serialized = Serialize(snapshot);
                if (serialized.Length > MaximumManifestBytes)
                {
                    errorMessage = "The resume manifest is too large.";
                    return false;
                }

                WriteAtomic(
                    Path.Combine(cacheDirectory, ManifestName),
                    Path.Combine(cacheDirectory, ManifestBackupName),
                    serialized);

                manifest.TaskId = snapshot.TaskId;
                manifest.Revision = snapshot.Revision;
                manifest.CreatedUtc = snapshot.CreatedUtc;
                manifest.UpdatedUtc = snapshot.UpdatedUtc;
                manifest.DeleteAfterUtc = snapshot.DeleteAfterUtc;
                manifest.RetentionDays = snapshot.RetentionDays;
                manifest.CacheDirectory = snapshot.CacheDirectory;
                return true;
            }
            catch (Exception exception)
            {
                errorMessage = "Unable to save the resume manifest: " + exception.Message;
                return false;
            }
            finally
            {
                Clear(serialized);
            }
        }

        public static bool TryLoad(
            string cacheDirectory,
            out DownloadResumeManifest manifest,
            out string errorMessage)
        {
            manifest = null;
            errorMessage = null;

            string ownedDirectory;
            string taskId;
            if (!TryGetOwnedCacheDirectory(cacheDirectory, out ownedDirectory, out taskId))
            {
                errorMessage = "The resume cache directory is not owned by this application.";
                return false;
            }

            string primaryPath = Path.Combine(ownedDirectory, ManifestName);
            string backupPath = Path.Combine(ownedDirectory, ManifestBackupName);
            string primaryError;
            if (TryLoadFile(primaryPath, ownedDirectory, taskId, out manifest, out primaryError))
            {
                return true;
            }

            string backupError;
            if (TryLoadFile(backupPath, ownedDirectory, taskId, out manifest, out backupError))
            {
                errorMessage = null;
                return true;
            }

            errorMessage = File.Exists(primaryPath)
                ? primaryError
                : "No resume manifest exists in this cache directory.";
            if (File.Exists(backupPath) && !string.IsNullOrWhiteSpace(backupError))
            {
                errorMessage += " Backup manifest: " + backupError;
            }

            return false;
        }

        public static List<DownloadResumeManifest> DiscoverRecoverableTasks(DateTime utcNow)
        {
            DateTime now = NormalizeUtc(utcNow);
            List<DownloadResumeManifest> tasks = new List<DownloadResumeManifest>();
            string rootDirectory = GetRootDirectory();
            if (!Directory.Exists(rootDirectory))
            {
                return tasks;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(
                    rootDirectory,
                    DirectoryPrefix + "*",
                    SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return tasks;
            }

            foreach (string directory in directories)
            {
                try
                {
                    string ownedDirectory;
                    string taskId;
                    if (!TryGetOwnedCacheDirectory(directory, out ownedDirectory, out taskId) ||
                        File.Exists(Path.Combine(ownedDirectory, DiscardPendingName)) ||
                        HasActiveLease(ownedDirectory))
                    {
                        continue;
                    }

                    DownloadResumeManifest manifest;
                    string errorMessage;
                    if (TryLoad(ownedDirectory, out manifest, out errorMessage) &&
                        manifest.DeleteAfterUtc > now)
                    {
                        if (manifest.State == DownloadResumeState.Running)
                        {
                            manifest.State = DownloadResumeState.Interrupted;
                        }

                        tasks.Add(manifest);
                    }
                }
                catch
                {
                }
            }

            tasks.Sort(delegate(DownloadResumeManifest left, DownloadResumeManifest right)
            {
                return right.UpdatedUtc.CompareTo(left.UpdatedUtc);
            });
            return tasks;
        }

        public static List<DownloadResumeManifest> DiscoverRecoverableTasks()
        {
            return DiscoverRecoverableTasks(DateTime.UtcNow);
        }

        public static DownloadResumeCleanupResult CleanupExpired(DateTime utcNow)
        {
            DownloadResumeCleanupResult result = new DownloadResumeCleanupResult();
            DateTime now = NormalizeUtc(utcNow);
            string rootDirectory = GetRootDirectory();
            if (!Directory.Exists(rootDirectory))
            {
                return result;
            }
            try
            {
                if ((File.GetAttributes(rootDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    result.DeleteFailed++;
                    return result;
                }
                CleanupStaleLeaseFiles(rootDirectory);
            }
            catch
            {
                result.DeleteFailed++;
                return result;
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(
                    rootDirectory,
                    DirectoryPrefix + "*",
                    SearchOption.TopDirectoryOnly);
            }
            catch
            {
                result.DeleteFailed++;
                return result;
            }

            foreach (string directory in directories)
            {
                try
                {
                string ownedDirectory;
                string taskId;
                if (!TryGetOwnedCacheDirectory(directory, out ownedDirectory, out taskId))
                {
                    continue;
                }

                if (HasActiveLease(ownedDirectory))
                {
                    result.ActiveSkipped++;
                    continue;
                }

                if (File.Exists(Path.Combine(ownedDirectory, DiscardPendingName)))
                {
                    string discardError;
                    if (TryDiscard(ownedDirectory, out discardError))
                    {
                        result.DiscardPendingDeleted++;
                    }
                    else
                    {
                        result.DeleteFailed++;
                    }

                    continue;
                }

                DownloadResumeManifest manifest;
                string loadError;
                if (TryLoad(ownedDirectory, out manifest, out loadError))
                {
                    if (manifest.DeleteAfterUtc <= now)
                    {
                        if (manifest.State == DownloadResumeState.Running &&
                            GetLatestCacheFileActivityUtc(ownedDirectory) >
                                now.AddDays(-DefaultRetentionDays))
                        {
                            DownloadResumeActivityLease refreshLease;
                            string refreshError;
                            if (!TryAcquireActivityLease(
                                ownedDirectory,
                                out refreshLease,
                                out refreshError))
                            {
                                result.ActiveSkipped++;
                                continue;
                            }
                            using (refreshLease)
                            {
                                manifest.State = DownloadResumeState.Interrupted;
                                if (!TrySave(manifest, out refreshError))
                                {
                                    result.DeleteFailed++;
                                }
                            }
                            continue;
                        }

                        string discardError;
                        if (TryDiscard(ownedDirectory, out discardError))
                        {
                            result.ExpiredDeleted++;
                        }
                        else
                        {
                            result.DeleteFailed++;
                        }
                    }

                    continue;
                }

                DateTime lastActivityUtc = GetLastActivityUtc(ownedDirectory);
                if (lastActivityUtc > now.AddDays(1))
                {
                    NormalizeFutureOrphanTimestamps(ownedDirectory, now);
                    lastActivityUtc = now;
                }
                if (lastActivityUtc <= now.AddDays(-OrphanRetentionDays))
                {
                    string discardError;
                    if (TryDiscard(ownedDirectory, out discardError))
                    {
                        result.OrphanedDeleted++;
                    }
                    else
                    {
                        result.DeleteFailed++;
                    }
                }
                }
                catch
                {
                    result.DeleteFailed++;
                }
            }

            return result;
        }

        public static DownloadResumeCleanupResult CleanupExpired()
        {
            return CleanupExpired(DateTime.UtcNow);
        }

        public static bool TryDiscard(string cacheDirectory, out string errorMessage)
        {
            errorMessage = null;
            string ownedDirectory;
            string taskId;
            if (!TryGetOwnedCacheDirectory(cacheDirectory, out ownedDirectory, out taskId))
            {
                errorMessage = "The resume cache directory is not owned by this application.";
                return false;
            }

            FileStream deletionLease;
            try
            {
                deletionLease = OpenActivityLease(taskId);
            }
            catch (IOException)
            {
                errorMessage = "The resume task is still active. Release its activity lease first.";
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                errorMessage = "Unable to lock the resume task for deletion: " + exception.Message;
                return false;
            }

            using (deletionLease)
            {
                try
                {
                    byte[] marker = Encoding.UTF8.GetBytes(
                        "M3u8DownloaderGui:discard:v1:" + taskId + ":" +
                        FormatUtc(DateTime.UtcNow));
                    try
                    {
                        string tombstonePath = Path.Combine(ownedDirectory, DiscardPendingName);
                        WriteAtomic(tombstonePath, tombstonePath + ".bak", marker);
                    }
                    finally
                    {
                        Clear(marker);
                    }

                    if (DeleteOwnedCacheDirectory(ownedDirectory))
                    {
                        return true;
                    }

                    errorMessage = "The cache is marked for deletion and will be retried at next startup.";
                    return false;
                }
                catch (Exception exception)
                {
                    errorMessage = "Unable to mark the resume cache for deletion: " + exception.Message;
                    return false;
                }
            }
        }

        public static bool TryAcquireActivityLease(
            string cacheDirectory,
            out DownloadResumeActivityLease lease,
            out string errorMessage)
        {
            lease = null;
            errorMessage = null;
            string ownedDirectory;
            string taskId;
            if (!TryGetOwnedCacheDirectory(cacheDirectory, out ownedDirectory, out taskId))
            {
                errorMessage = "The resume cache directory is not owned by this application.";
                return false;
            }

            try
            {
                string tombstonePath = Path.Combine(ownedDirectory, DiscardPendingName);
                if (File.Exists(tombstonePath))
                {
                    errorMessage = "The resume task is pending deletion.";
                    return false;
                }

                FileStream stream = OpenActivityLease(taskId);
                string revalidatedDirectory;
                string revalidatedTaskId;
                if (!TryGetOwnedCacheDirectory(
                        ownedDirectory,
                        out revalidatedDirectory,
                        out revalidatedTaskId) ||
                    !string.Equals(
                        revalidatedTaskId,
                        taskId,
                        StringComparison.OrdinalIgnoreCase) ||
                    File.Exists(tombstonePath))
                {
                    stream.Dispose();
                    errorMessage = "The resume task was deleted or is pending deletion.";
                    return false;
                }
                lease = new DownloadResumeActivityLease(stream);
                return true;
            }
            catch (Exception exception)
            {
                errorMessage = "Unable to acquire the resume task lease: " + exception.Message;
                return false;
            }
        }

        public static bool TryMaterializeInput(
            DownloadResumeManifest manifest,
            out string input,
            out string errorMessage)
        {
            input = null;
            errorMessage = null;
            if (manifest == null)
            {
                errorMessage = "The resume manifest is missing.";
                return false;
            }

            if (!manifest.InputIsImportedPlaylist)
            {
                Uri uri;
                if (Uri.TryCreate(manifest.Input, UriKind.Absolute, out uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    input = manifest.Input;
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(manifest.Input) && File.Exists(manifest.Input))
                {
                    input = Path.GetFullPath(manifest.Input);
                    return true;
                }

                errorMessage = "The original local playlist no longer exists.";
                return false;
            }

            string ownedDirectory;
            string taskId;
            if (!TryGetOwnedCacheDirectory(manifest.CacheDirectory, out ownedDirectory, out taskId))
            {
                errorMessage = "The resume cache directory is not owned by this application.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(manifest.ImportedPlaylistContent) ||
                manifest.ImportedPlaylistContent.Length > MaximumPlaylistCharacters)
            {
                errorMessage = "The saved playlist content is missing or too large.";
                return false;
            }

            try
            {
                string extension = PlaylistInput.GetExtension(manifest.ImportedPlaylistContent);
                if (!string.Equals(extension, ".mpd", StringComparison.OrdinalIgnoreCase))
                {
                    extension = ".m3u8";
                }

                string path = Path.Combine(
                    ownedDirectory,
                    "resume_input" + extension);
                byte[] bytes = new UTF8Encoding(false).GetBytes(manifest.ImportedPlaylistContent);
                try
                {
                    WriteReplaceableInput(path, bytes);
                }
                finally
                {
                    Clear(bytes);
                }

                input = path;
                return true;
            }
            catch (Exception exception)
            {
                errorMessage = "Unable to restore the saved playlist: " + exception.Message;
                return false;
            }
        }

        internal static bool TryGetOwnedCacheDirectory(
            string path,
            out string fullPath,
            out string taskId)
        {
            fullPath = null;
            taskId = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                string root = Path.GetFullPath(GetRootDirectory()).TrimEnd(Path.DirectorySeparatorChar);
                string candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                if (!Directory.Exists(root) ||
                    (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
                DirectoryInfo parent = Directory.GetParent(candidate);
                string name = Path.GetFileName(candidate);
                Guid parsedTaskId;
                if (parent == null ||
                    !string.Equals(parent.FullName, root, StringComparison.OrdinalIgnoreCase) ||
                    name.Length != DirectoryPrefix.Length + 32 ||
                    !name.StartsWith(DirectoryPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(name.Substring(DirectoryPrefix.Length), "N", out parsedTaskId) ||
                    !Directory.Exists(candidate))
                {
                    return false;
                }

                FileAttributes attributes = File.GetAttributes(candidate);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                string parsedId = parsedTaskId.ToString("N");
                string marker = File.ReadAllText(
                    Path.Combine(candidate, OwnerMarkerName),
                    Encoding.UTF8).Trim();
                if (!string.Equals(
                    marker,
                    OwnerMarkerPrefix + parsedId,
                    StringComparison.Ordinal))
                {
                    return false;
                }

                fullPath = candidate;
                taskId = parsedId;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryLoadFile(
            string path,
            string cacheDirectory,
            string expectedTaskId,
            out DownloadResumeManifest manifest,
            out string errorMessage)
        {
            manifest = null;
            errorMessage = null;
            byte[] bytes = null;
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists)
                {
                    errorMessage = "Manifest file does not exist.";
                    return false;
                }

                if (info.Length <= 0 || info.Length > MaximumManifestBytes)
                {
                    errorMessage = "Manifest file has an invalid size.";
                    return false;
                }

                bytes = File.ReadAllBytes(path);
                manifest = Deserialize(bytes, cacheDirectory, expectedTaskId);
                if (!ValidateLoaded(manifest, out errorMessage))
                {
                    manifest = null;
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                manifest = null;
                errorMessage = exception.Message;
                return false;
            }
            finally
            {
                Clear(bytes);
            }
        }

        private static byte[] Serialize(DownloadResumeManifest manifest)
        {
            string fingerprint = ComputePublicFingerprint(manifest);
            byte[] sensitiveBytes = null;
            byte[] protectedBytes = null;
            try
            {
                sensitiveBytes = SerializeSensitive(manifest, fingerprint);
                protectedBytes = ProtectedData.Protect(
                    sensitiveBytes,
                    DpapiEntropy,
                    DataProtectionScope.CurrentUser);

                using (MemoryStream memory = new MemoryStream())
                {
                    XmlWriterSettings settings = CreateWriterSettings();
                    using (XmlWriter writer = XmlWriter.Create(memory, settings))
                    {
                        writer.WriteStartDocument();
                        writer.WriteStartElement("downloadResume");
                        writer.WriteAttributeString("version", FormatVersion.ToString(CultureInfo.InvariantCulture));
                        WriteElement(writer, "taskId", manifest.TaskId);
                        WriteElement(writer, "revision", manifest.Revision.ToString(CultureInfo.InvariantCulture));
                        WriteElement(writer, "createdUtc", FormatUtc(manifest.CreatedUtc));
                        WriteElement(writer, "updatedUtc", FormatUtc(manifest.UpdatedUtc));
                        WriteElement(writer, "deleteAfterUtc", FormatUtc(manifest.DeleteAfterUtc));
                        WriteElement(writer, "retentionDays", manifest.RetentionDays.ToString(CultureInfo.InvariantCulture));
                        WriteElement(writer, "state", manifest.State.ToString());
                        writer.WriteStartElement("protectedPayload");
                        writer.WriteAttributeString("protection", "DPAPI-CurrentUser");
                        writer.WriteAttributeString("encoding", "base64");
                        writer.WriteBase64(protectedBytes, 0, protectedBytes.Length);
                        writer.WriteEndElement();
                        writer.WriteEndElement();
                        writer.WriteEndDocument();
                    }

                    return memory.ToArray();
                }
            }
            finally
            {
                Clear(sensitiveBytes);
                Clear(protectedBytes);
            }
        }

        private static byte[] SerializeSensitive(
            DownloadResumeManifest manifest,
            string fingerprint)
        {
            using (MemoryStream memory = new MemoryStream())
            {
                XmlWriterSettings settings = CreateWriterSettings();
                settings.Indent = false;
                using (XmlWriter writer = XmlWriter.Create(memory, settings))
                {
                    writer.WriteStartDocument();
                    writer.WriteStartElement("resumeSecrets");
                    writer.WriteAttributeString("version", FormatVersion.ToString(CultureInfo.InvariantCulture));
                    WriteElement(writer, "publicFingerprint", fingerprint);
                    WriteElement(writer, "saveDirectory", manifest.SaveDirectory);
                    WriteElement(writer, "fileName", manifest.FileName);
                    WriteElement(writer, "downloaderPath", manifest.DownloaderPath);
                    WriteElement(writer, "ffmpegPath", manifest.FfmpegPath);
                    WriteElement(writer, "muxToMp4", manifest.MuxToMp4 ? "true" : "false");
                    WriteElement(writer, "input", manifest.Input);
                    WriteElement(
                        writer,
                        "inputIsImportedPlaylist",
                        manifest.InputIsImportedPlaylist ? "true" : "false");
                    WriteElement(writer, "importedPlaylistContent", manifest.ImportedPlaylistContent);
                    WriteElement(writer, "hlsKey", manifest.HlsKey);
                    WriteElement(writer, "hlsIv", manifest.HlsIv);
                    WriteHeaders(writer, manifest.CapturedHeaders);
                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                }

                return memory.ToArray();
            }
        }

        private static DownloadResumeManifest Deserialize(
            byte[] bytes,
            string cacheDirectory,
            string expectedTaskId)
        {
            XmlDocument document = LoadXml(bytes);
            XmlElement root = document.DocumentElement;
            RequireElement(root, "downloadResume");
            RequireVersion(root);

            DownloadResumeManifest manifest = new DownloadResumeManifest();
            manifest.CacheDirectory = cacheDirectory;
            manifest.TaskId = ReadRequiredText(root, "taskId", 64);
            if (!string.Equals(manifest.TaskId, expectedTaskId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Manifest task identifier does not match its directory.");
            }

            manifest.Revision = ReadLong(root, "revision", 1, long.MaxValue);
            manifest.CreatedUtc = ReadUtc(root, "createdUtc");
            manifest.UpdatedUtc = ReadUtc(root, "updatedUtc");
            manifest.DeleteAfterUtc = ReadUtc(root, "deleteAfterUtc");
            manifest.RetentionDays = (int)ReadLong(
                root,
                "retentionDays",
                1,
                MaximumRetentionDays);
            manifest.State = ReadState(root);

            XmlElement protectedPayload = RequireChild(root, "protectedPayload");
            if (!string.Equals(
                    protectedPayload.GetAttribute("protection"),
                    "DPAPI-CurrentUser",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    protectedPayload.GetAttribute("encoding"),
                    "base64",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Manifest uses an unsupported protection format.");
            }

            byte[] protectedBytes = null;
            byte[] sensitiveBytes = null;
            try
            {
                protectedBytes = Convert.FromBase64String(protectedPayload.InnerText.Trim());
                if (protectedBytes.Length == 0 || protectedBytes.Length > MaximumManifestBytes)
                {
                    throw new InvalidDataException("Protected manifest payload has an invalid size.");
                }

                sensitiveBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    DpapiEntropy,
                    DataProtectionScope.CurrentUser);
                DeserializeSensitive(sensitiveBytes, manifest);
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException(
                    "Protected resume data cannot be opened by the current Windows user.",
                    exception);
            }
            finally
            {
                Clear(protectedBytes);
                Clear(sensitiveBytes);
            }

            return manifest;
        }

        private static void DeserializeSensitive(
            byte[] bytes,
            DownloadResumeManifest manifest)
        {
            XmlDocument document = LoadXml(bytes);
            XmlElement root = document.DocumentElement;
            RequireElement(root, "resumeSecrets");
            RequireVersion(root);

            string storedFingerprint = ReadRequiredText(root, "publicFingerprint", 128);
            string expectedFingerprint = ComputePublicFingerprint(manifest);
            if (!FixedTimeEquals(storedFingerprint, expectedFingerprint))
            {
                throw new InvalidDataException("Resume manifest metadata failed its integrity check.");
            }

            manifest.SaveDirectory = ReadRequiredText(
                root,
                "saveDirectory",
                MaximumOrdinaryValueCharacters);
            manifest.FileName = ReadRequiredText(root, "fileName", 260);
            manifest.DownloaderPath = ReadRequiredText(
                root,
                "downloaderPath",
                MaximumOrdinaryValueCharacters);
            manifest.FfmpegPath = ReadRequiredText(
                root,
                "ffmpegPath",
                MaximumOrdinaryValueCharacters);
            manifest.MuxToMp4 = ReadBoolean(root, "muxToMp4");
            manifest.Input = ReadOptionalText(root, "input", MaximumOrdinaryValueCharacters);
            manifest.InputIsImportedPlaylist = ReadBoolean(root, "inputIsImportedPlaylist");
            manifest.ImportedPlaylistContent = ReadOptionalText(
                root,
                "importedPlaylistContent",
                MaximumPlaylistCharacters);
            manifest.HlsKey = ReadOptionalText(root, "hlsKey", MaximumHeaderValueCharacters);
            manifest.HlsIv = ReadOptionalText(root, "hlsIv", MaximumHeaderValueCharacters);
            manifest.CapturedHeaders = ReadHeaders(root);
        }

        private static void WriteHeaders(XmlWriter writer, MediaRequestHeaders headers)
        {
            writer.WriteStartElement("headers");
            WriteElement(writer, "referer", headers == null ? null : headers.Referer);
            WriteElement(writer, "cookie", headers == null ? null : headers.Cookie);
            WriteElement(writer, "userAgent", headers == null ? null : headers.UserAgent);
            WriteElement(writer, "origin", headers == null ? null : headers.Origin);
            WriteElement(writer, "authorization", headers == null ? null : headers.Authorization);
            WriteElement(writer, "sourceUrl", headers == null ? null : headers.SourceUrl);
            if (headers != null)
            {
                List<string> names = new List<string>(headers.AdditionalHeaders.Keys);
                names.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string name in names)
                {
                    string value = headers.AdditionalHeaders[name];
                    if (!MediaRequestHeaders.IsAllowedAdditionalHeader(name, value))
                    {
                        continue;
                    }

                    writer.WriteStartElement("header");
                    writer.WriteAttributeString("name", name);
                    writer.WriteString(value);
                    writer.WriteEndElement();
                }
            }

            writer.WriteEndElement();
        }

        private static MediaRequestHeaders ReadHeaders(XmlElement root)
        {
            XmlElement headersElement = RequireChild(root, "headers");
            MediaRequestHeaders headers = new MediaRequestHeaders();
            headers.Referer = ReadOptionalText(
                headersElement,
                "referer",
                MaximumHeaderValueCharacters);
            headers.Cookie = ReadOptionalText(
                headersElement,
                "cookie",
                MaximumHeaderValueCharacters);
            headers.UserAgent = ReadOptionalText(
                headersElement,
                "userAgent",
                MaximumHeaderValueCharacters);
            headers.Origin = ReadOptionalText(
                headersElement,
                "origin",
                MaximumHeaderValueCharacters);
            headers.Authorization = ReadOptionalText(
                headersElement,
                "authorization",
                MaximumHeaderValueCharacters);
            headers.SourceUrl = ReadOptionalText(
                headersElement,
                "sourceUrl",
                MaximumHeaderValueCharacters);

            XmlNodeList nodes = headersElement.SelectNodes("header");
            if (nodes != null && nodes.Count > MaximumAdditionalHeaders)
            {
                throw new InvalidDataException("Resume manifest contains too many request headers.");
            }

            if (nodes != null)
            {
                foreach (XmlNode node in nodes)
                {
                    XmlElement element = node as XmlElement;
                    if (element == null ||
                        !headers.TrySetAdditionalHeader(element.GetAttribute("name"), element.InnerText))
                    {
                        throw new InvalidDataException("Resume manifest contains an unsafe request header.");
                    }
                }
            }

            return headers.HasAny || !string.IsNullOrWhiteSpace(headers.SourceUrl)
                ? headers
                : null;
        }

        private static bool ValidateForSave(
            DownloadResumeManifest manifest,
            out string errorMessage)
        {
            if (!ValidateCommon(manifest, out errorMessage))
            {
                return false;
            }

            if (manifest.CreatedUtc > manifest.UpdatedUtc.AddMinutes(5))
            {
                errorMessage = "The resume creation time is invalid.";
                return false;
            }

            if (manifest.DeleteAfterUtc <= manifest.UpdatedUtc ||
                manifest.DeleteAfterUtc > manifest.UpdatedUtc.AddDays(MaximumRetentionDays + 1))
            {
                errorMessage = "The resume cleanup deadline is invalid.";
                return false;
            }

            return true;
        }

        private static bool ValidateLoaded(
            DownloadResumeManifest manifest,
            out string errorMessage)
        {
            if (!ValidateCommon(manifest, out errorMessage))
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if (manifest.CreatedUtc > now.AddDays(1) ||
                manifest.UpdatedUtc < manifest.CreatedUtc.AddMinutes(-5) ||
                manifest.UpdatedUtc > now.AddDays(1) ||
                manifest.DeleteAfterUtc <= manifest.UpdatedUtc ||
                manifest.DeleteAfterUtc > manifest.UpdatedUtc.AddDays(MaximumRetentionDays + 1))
            {
                errorMessage = "The resume manifest timestamps are invalid.";
                return false;
            }

            return true;
        }

        private static bool ValidateCommon(
            DownloadResumeManifest manifest,
            out string errorMessage)
        {
            errorMessage = null;
            if (manifest.Revision < 1 ||
                string.IsNullOrWhiteSpace(manifest.TaskId) ||
                manifest.RetentionDays < 1 ||
                manifest.RetentionDays > MaximumRetentionDays)
            {
                errorMessage = "The resume manifest metadata is invalid.";
                return false;
            }

            if (!IsRootedPath(manifest.SaveDirectory) ||
                !IsRootedExecutable(manifest.DownloaderPath) ||
                !IsRootedExecutable(manifest.FfmpegPath))
            {
                errorMessage = "The resume manifest contains an invalid output or tool path.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(manifest.FileName) ||
                manifest.FileName.Length > 240 ||
                !string.Equals(
                    manifest.FileName,
                    Path.GetFileName(manifest.FileName),
                    StringComparison.Ordinal) ||
                manifest.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                errorMessage = "The resume output file name is invalid.";
                return false;
            }

            Uri inputUri;
            bool remoteInput = Uri.TryCreate(manifest.Input, UriKind.Absolute, out inputUri) &&
                (inputUri.Scheme == Uri.UriSchemeHttp || inputUri.Scheme == Uri.UriSchemeHttps);
            bool localInput = IsRootedPath(manifest.Input);
            if (!remoteInput && !localInput)
            {
                errorMessage = "The resume input is invalid.";
                return false;
            }

            if (manifest.InputIsImportedPlaylist)
            {
                if (string.IsNullOrWhiteSpace(manifest.ImportedPlaylistContent) ||
                    manifest.ImportedPlaylistContent.Length > MaximumPlaylistCharacters ||
                    (!PlaylistInput.LooksLikePlaylistContent(manifest.ImportedPlaylistContent) &&
                     !manifest.ImportedPlaylistContent.TrimStart().StartsWith("<", StringComparison.Ordinal)))
                {
                    errorMessage = "The imported playlist snapshot is missing or invalid.";
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(manifest.ImportedPlaylistContent))
            {
                errorMessage = "Unexpected imported playlist content is present.";
                return false;
            }

            if (!ValidateHeaderValues(manifest.CapturedHeaders, out errorMessage))
            {
                return false;
            }

            return true;
        }

        private static bool ValidateHeaderValues(
            MediaRequestHeaders headers,
            out string errorMessage)
        {
            errorMessage = null;
            if (headers == null)
            {
                return true;
            }

            string[] values =
            {
                headers.Referer,
                headers.Cookie,
                headers.UserAgent,
                headers.Origin,
                headers.Authorization,
                headers.SourceUrl
            };
            foreach (string value in values)
            {
                if (value != null &&
                    (value.Length > MaximumHeaderValueCharacters ||
                     value.IndexOf('\0') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0))
                {
                    errorMessage = "The resume manifest contains an invalid request header value.";
                    return false;
                }
            }

            if (headers.AdditionalHeaders.Count > MaximumAdditionalHeaders)
            {
                errorMessage = "The resume manifest contains too many request headers.";
                return false;
            }

            foreach (KeyValuePair<string, string> header in headers.AdditionalHeaders)
            {
                if (header.Value.Length > MaximumHeaderValueCharacters ||
                    !MediaRequestHeaders.IsAllowedAdditionalHeader(header.Key, header.Value))
                {
                    errorMessage = "The resume manifest contains an unsafe request header.";
                    return false;
                }
            }

            return true;
        }

        private static string ComputePublicFingerprint(DownloadResumeManifest manifest)
        {
            StringBuilder value = new StringBuilder();
            AppendFingerprintValue(value, manifest.TaskId);
            AppendFingerprintValue(value, manifest.Revision.ToString(CultureInfo.InvariantCulture));
            AppendFingerprintValue(value, FormatUtc(manifest.CreatedUtc));
            AppendFingerprintValue(value, FormatUtc(manifest.UpdatedUtc));
            AppendFingerprintValue(value, FormatUtc(manifest.DeleteAfterUtc));
            AppendFingerprintValue(value, manifest.RetentionDays.ToString(CultureInfo.InvariantCulture));
            AppendFingerprintValue(value, manifest.State.ToString());

            byte[] source = Encoding.UTF8.GetBytes(value.ToString());
            byte[] hash = null;
            try
            {
                using (SHA256 algorithm = SHA256.Create())
                {
                    hash = algorithm.ComputeHash(source);
                }

                return Convert.ToBase64String(hash);
            }
            finally
            {
                Clear(source);
                Clear(hash);
            }
        }

        private static void AppendFingerprintValue(StringBuilder builder, string value)
        {
            string text = value ?? string.Empty;
            builder.Append(text.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(text);
            builder.Append('|');
        }

        private static bool FixedTimeEquals(string first, string second)
        {
            byte[] left = Encoding.UTF8.GetBytes(first ?? string.Empty);
            byte[] right = Encoding.UTF8.GetBytes(second ?? string.Empty);
            try
            {
                int difference = left.Length ^ right.Length;
                int length = Math.Max(left.Length, right.Length);
                for (int index = 0; index < length; index++)
                {
                    byte leftValue = index < left.Length ? left[index] : (byte)0;
                    byte rightValue = index < right.Length ? right[index] : (byte)0;
                    difference |= leftValue ^ rightValue;
                }

                return difference == 0;
            }
            finally
            {
                Clear(left);
                Clear(right);
            }
        }

        private static XmlDocument LoadXml(byte[] bytes)
        {
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            settings.MaxCharactersInDocument = MaximumManifestBytes;
            settings.MaxCharactersFromEntities = 0;

            XmlDocument document = new XmlDocument();
            document.XmlResolver = null;
            using (MemoryStream memory = new MemoryStream(bytes, false))
            using (XmlReader reader = XmlReader.Create(memory, settings))
            {
                document.Load(reader);
            }

            return document;
        }

        private static XmlWriterSettings CreateWriterSettings()
        {
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.Encoding = new UTF8Encoding(false);
            settings.Indent = true;
            settings.NewLineChars = Environment.NewLine;
            settings.CloseOutput = false;
            return settings;
        }

        private static void WriteAtomic(string path, string backupPath, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("Resume cache directory does not exist.");
            }

            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }

                    File.Replace(temporaryPath, path, backupPath, true);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                }
            }
        }

        private static void WriteReplaceableInput(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("Resume cache directory does not exist.");
            }

            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temporaryPath, path);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                }
            }
        }

        private static bool HasActiveLease(string cacheDirectory)
        {
            string ownedDirectory;
            string taskId;
            if (!TryGetOwnedCacheDirectory(
                cacheDirectory,
                out ownedDirectory,
                out taskId))
            {
                return true;
            }

            string path = GetActivityLeasePath(taskId);
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                using (FileStream stream = OpenActivityLease(taskId))
                {
                }

                try
                {
                    File.Delete(path);
                }
                catch
                {
                }

                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static string GetActivityLeasePath(string taskId)
        {
            return Path.Combine(
                GetRootDirectory(),
                ActivityLeasePrefix + taskId + ActivityLeaseSuffix);
        }

        private static void CleanupStaleLeaseFiles(string rootDirectory)
        {
            foreach (string path in Directory.GetFiles(
                rootDirectory,
                ActivityLeasePrefix + "*" + ActivityLeaseSuffix,
                SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                int idLength = name.Length - ActivityLeasePrefix.Length -
                    ActivityLeaseSuffix.Length;
                Guid taskId;
                if (idLength != 32 ||
                    !Guid.TryParseExact(
                        name.Substring(ActivityLeasePrefix.Length, idLength),
                        "N",
                        out taskId))
                {
                    continue;
                }

                try
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    }
                    using (FileStream stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        1,
                        FileOptions.DeleteOnClose))
                    {
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private static FileStream OpenActivityLease(string taskId)
        {
            string path = GetActivityLeasePath(taskId);
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (UnauthorizedAccessException)
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
        }

        private static DateTime GetLastActivityUtc(string directory)
        {
            DateTime latest = Directory.GetLastWriteTimeUtc(directory);
            string[] names = { OwnerMarkerName, ManifestName, ManifestBackupName };
            foreach (string name in names)
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    DateTime modified = File.GetLastWriteTimeUtc(path);
                    if (modified > latest)
                    {
                        latest = modified;
                    }
                }
            }

            return latest;
        }

        private static DateTime GetLatestCacheFileActivityUtc(string rootDirectory)
        {
            DateTime latest = Directory.GetLastWriteTimeUtc(rootDirectory);
            Stack<string> pending = new Stack<string>();
            pending.Push(rootDirectory);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string file in Directory.GetFiles(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    DateTime modified = File.GetLastWriteTimeUtc(file);
                    if (modified > latest)
                    {
                        latest = modified;
                    }
                }

                foreach (string child in Directory.GetDirectories(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(child);
                    }
                }
            }
            return latest;
        }

        private static void NormalizeFutureOrphanTimestamps(
            string directory,
            DateTime utcNow)
        {
            Directory.SetLastWriteTimeUtc(directory, utcNow);
            string[] names = { OwnerMarkerName, ManifestName, ManifestBackupName };
            foreach (string name in names)
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > utcNow.AddDays(1))
                {
                    File.SetLastWriteTimeUtc(path, utcNow);
                }
            }
        }

        private static int NormalizeRetentionDays(int retentionDays)
        {
            if (retentionDays < 1)
            {
                return DefaultRetentionDays;
            }

            return Math.Min(retentionDays, MaximumRetentionDays);
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            if (value.Kind == DateTimeKind.Local)
            {
                return value.ToUniversalTime();
            }

            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static string FormatUtc(DateTime value)
        {
            return NormalizeUtc(value).ToString("o", CultureInfo.InvariantCulture);
        }

        private static bool IsRootedPath(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) &&
                    path.Length <= MaximumOrdinaryValueCharacters &&
                    Path.IsPathRooted(path) &&
                    string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRootedExecutable(string path)
        {
            return IsRootedPath(path) &&
                string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        }

        private static void RequireElement(XmlElement element, string expectedName)
        {
            if (element == null || !string.Equals(element.Name, expectedName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Resume manifest has an invalid root element.");
            }
        }

        private static void RequireVersion(XmlElement root)
        {
            int version;
            if (!int.TryParse(
                    root.GetAttribute("version"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out version) ||
                version != FormatVersion)
            {
                throw new InvalidDataException("Resume manifest version is not supported.");
            }
        }

        private static XmlElement RequireChild(XmlElement parent, string name)
        {
            XmlElement child = parent.SelectSingleNode(name) as XmlElement;
            if (child == null)
            {
                throw new InvalidDataException("Resume manifest is missing " + name + ".");
            }

            return child;
        }

        private static string ReadRequiredText(XmlElement parent, string name, int maximumLength)
        {
            string value = ReadOptionalText(parent, name, maximumLength);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("Resume manifest is missing " + name + ".");
            }

            return value;
        }

        private static string ReadOptionalText(XmlElement parent, string name, int maximumLength)
        {
            XmlElement child = RequireChild(parent, name);
            string value = child.InnerText;
            if (value.Length > maximumLength)
            {
                throw new InvalidDataException("Resume manifest value is too large: " + name + ".");
            }

            return value;
        }

        private static long ReadLong(XmlElement parent, string name, long minimum, long maximum)
        {
            string text = ReadRequiredText(parent, name, 32);
            long value;
            if (!long.TryParse(
                    text,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value) ||
                value < minimum || value > maximum)
            {
                throw new InvalidDataException("Resume manifest contains an invalid " + name + ".");
            }

            return value;
        }

        private static bool ReadBoolean(XmlElement parent, string name)
        {
            string text = ReadRequiredText(parent, name, 8);
            bool value;
            if (!bool.TryParse(text, out value))
            {
                throw new InvalidDataException("Resume manifest contains an invalid " + name + ".");
            }

            return value;
        }

        private static DateTime ReadUtc(XmlElement parent, string name)
        {
            string text = ReadRequiredText(parent, name, 64);
            DateTime value;
            if (!DateTime.TryParseExact(
                    text,
                    "o",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out value))
            {
                throw new InvalidDataException("Resume manifest contains an invalid " + name + ".");
            }

            return NormalizeUtc(value);
        }

        private static DownloadResumeState ReadState(XmlElement root)
        {
            string text = ReadRequiredText(root, "state", 32);
            DownloadResumeState state;
            if (!Enum.TryParse(text, false, out state) || !Enum.IsDefined(typeof(DownloadResumeState), state))
            {
                throw new InvalidDataException("Resume manifest contains an invalid state.");
            }

            return state;
        }

        private static void WriteElement(XmlWriter writer, string name, string value)
        {
            writer.WriteStartElement(name);
            writer.WriteString(value ?? string.Empty);
            writer.WriteEndElement();
        }

        private static string GetRootDirectory()
        {
            lock (TestRootSync)
            {
                return _testRootDirectory ?? DefaultRootDirectory;
            }
        }

        private static bool DeleteOwnedCacheDirectory(string path)
        {
            string fullPath;
            string taskId;
            if (!TryGetOwnedCacheDirectory(path, out fullPath, out taskId))
            {
                return false;
            }

            for (int attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    if (Directory.Exists(fullPath))
                    {
                        FileAttributes attributes = File.GetAttributes(fullPath);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            return false;
                        }

                        DeleteDirectoryTree(fullPath, true);
                    }

                    TryDeleteManagedRootIfEmpty();
                    return !Directory.Exists(fullPath);
                }
                catch
                {
                    RestoreDeletionGuards(fullPath, taskId);
                    if (attempt < 11)
                    {
                        Thread.Sleep(150);
                    }
                }
            }

            return false;
        }

        private static void RestoreDeletionGuards(string directory, string taskId)
        {
            try
            {
                if (!Directory.Exists(directory) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    return;
                }

                string ownerMarker = Path.Combine(directory, OwnerMarkerName);
                if (!File.Exists(ownerMarker))
                {
                    File.WriteAllText(
                        ownerMarker,
                        OwnerMarkerPrefix + taskId,
                        new UTF8Encoding(false));
                }

                string discardPending = Path.Combine(directory, DiscardPendingName);
                if (!File.Exists(discardPending))
                {
                    File.WriteAllText(
                        discardPending,
                        "M3u8DownloaderGui:discard:v1:" + taskId + ":" +
                        FormatUtc(DateTime.UtcNow),
                        new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }

        private static void DeleteDirectoryTree(string directory, bool isOwnedRoot)
        {
            FileAttributes attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(directory, false);
                return;
            }

            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (isOwnedRoot &&
                    (string.Equals(name, OwnerMarkerName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, DiscardPendingName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                DeleteFileForCleanup(file);
            }

            foreach (string child in Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                DeleteDirectoryTree(child, false);
            }

            if (isOwnedRoot)
            {
                string discardPending = Path.Combine(directory, DiscardPendingName);
                if (File.Exists(discardPending))
                {
                    DeleteFileForCleanup(discardPending);
                }

                string ownerMarker = Path.Combine(directory, OwnerMarkerName);
                if (File.Exists(ownerMarker))
                {
                    DeleteFileForCleanup(ownerMarker);
                }
            }

            FileAttributes directoryAttributes = File.GetAttributes(directory);
            if ((directoryAttributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(directory, directoryAttributes & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(directory, false);
        }

        private static void DeleteFileForCleanup(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
            File.Delete(path);
        }

        private static void TryDeleteManagedRootIfEmpty()
        {
            try
            {
                string root = GetRootDirectory();
                if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length == 0)
                {
                    Directory.Delete(root, false);
                }
            }
            catch
            {
            }
        }

        private static void Clear(byte[] bytes)
        {
            if (bytes != null)
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        private sealed class TestRootScope : IDisposable
        {
            private string _rootDirectory;

            public TestRootScope(string rootDirectory)
            {
                _rootDirectory = rootDirectory;
            }

            public void Dispose()
            {
                string root = Interlocked.Exchange(ref _rootDirectory, null);
                if (root == null)
                {
                    return;
                }

                lock (TestRootSync)
                {
                    if (string.Equals(_testRootDirectory, root, StringComparison.OrdinalIgnoreCase))
                    {
                        _testRootDirectory = null;
                    }
                }
            }
        }
    }
}
