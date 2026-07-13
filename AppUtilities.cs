using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using System.Xml;

namespace M3u8DownloaderGui
{
    internal sealed class UserSettings
    {
        public string SaveDirectory;
        public string DownloaderPath;
        public string FfmpegPath;
        public bool MuxToMp4 = true;
        public bool OpenFolderWhenDone;
    }

    internal static class SettingsStore
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "N_m3u8DL-RE-GUI");

        private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.xml");

        public static UserSettings Load()
        {
            UserSettings settings = CreateDefaults();

            try
            {
                if (!File.Exists(SettingsPath))
                {
                    return settings;
                }

                XmlDocument document = new XmlDocument();
                document.Load(SettingsPath);
                XmlElement root = document.DocumentElement;
                if (root == null || !string.Equals(root.Name, "settings", StringComparison.Ordinal))
                {
                    return settings;
                }

                settings.SaveDirectory = ReadElement(root, "saveDirectory", settings.SaveDirectory);
                settings.DownloaderPath = ReadElement(root, "downloaderPath", settings.DownloaderPath);
                settings.FfmpegPath = ReadElement(root, "ffmpegPath", settings.FfmpegPath);
                settings.MuxToMp4 = ReadBoolean(root, "muxToMp4", settings.MuxToMp4);
                settings.OpenFolderWhenDone = ReadBoolean(
                    root,
                    "openFolderWhenDone",
                    settings.OpenFolderWhenDone);
            }
            catch
            {
                // A damaged settings file must never prevent the application from opening.
            }

            return settings;
        }

        public static void Save(UserSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            string temporaryPath = null;
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

                XmlWriterSettings writerSettings = new XmlWriterSettings();
                writerSettings.Encoding = new UTF8Encoding(false);
                writerSettings.Indent = true;
                writerSettings.NewLineChars = Environment.NewLine;

                using (XmlWriter writer = XmlWriter.Create(temporaryPath, writerSettings))
                {
                    writer.WriteStartDocument();
                    writer.WriteStartElement("settings");
                    writer.WriteAttributeString("version", "1");
                    writer.WriteElementString("saveDirectory", settings.SaveDirectory ?? string.Empty);
                    writer.WriteElementString("downloaderPath", settings.DownloaderPath ?? string.Empty);
                    writer.WriteElementString("ffmpegPath", settings.FfmpegPath ?? string.Empty);
                    writer.WriteElementString("muxToMp4", settings.MuxToMp4 ? "true" : "false");
                    writer.WriteElementString(
                        "openFolderWhenDone",
                        settings.OpenFolderWhenDone ? "true" : "false");
                    writer.WriteEndElement();
                    writer.WriteEndDocument();
                }

                if (File.Exists(SettingsPath))
                {
                    File.Replace(temporaryPath, SettingsPath, null, true);
                }
                else
                {
                    File.Move(temporaryPath, SettingsPath);
                }
            }
            catch
            {
                // Settings persistence is helpful but not required for downloads to work.
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(temporaryPath) && File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                }
            }
        }

        private static UserSettings CreateDefaults()
        {
            UserSettings settings = new UserSettings();
            settings.SaveDirectory = ToolLocator.GetDefaultSaveDirectory();
            settings.DownloaderPath = ToolLocator.FindDownloader(null);
            settings.FfmpegPath = ToolLocator.FindFfmpeg(null, settings.DownloaderPath);
            return settings;
        }

        private static string ReadElement(XmlElement root, string name, string fallback)
        {
            XmlNode node = root.SelectSingleNode(name);
            if (node == null || string.IsNullOrWhiteSpace(node.InnerText))
            {
                return fallback;
            }

            return node.InnerText.Trim();
        }

        private static bool ReadBoolean(XmlElement root, string name, bool fallback)
        {
            string text = ReadElement(root, name, null);
            bool value;
            return bool.TryParse(text, out value) ? value : fallback;
        }
    }

    internal static class ToolLocator
    {
        private static readonly string WinGetFfmpegLink = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\WinGet\Links\ffmpeg.exe");

        public static string GetDefaultSaveDirectory()
        {
            return Path.Combine(GetDownloadsDirectory(), "Videos");
        }

        public static string FindDownloader(string preferredPath)
        {
            List<string> candidates = new List<string>();
            AddCandidate(candidates, preferredPath);
            AddCandidate(candidates, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "N_m3u8DL-RE.exe"));
            AddCandidate(candidates, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"tools\N_m3u8DL-RE.exe"));
            AddCandidate(candidates, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\tools\N_m3u8DL-RE.exe"));
            AddCandidate(
                candidates,
                Path.Combine(
                    GetDownloadsDirectory(),
                    "N_m3u8DL-RE.exe"));

            string found = FirstExistingFile(candidates);
            if (found == null)
            {
                found = FindWithWhereExe("N_m3u8DL-RE.exe");
            }

            return found ?? (string.IsNullOrWhiteSpace(preferredPath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "N_m3u8DL-RE.exe")
                : preferredPath.Trim());
        }

        public static string FindFfmpeg(string preferredPath, string downloaderPath)
        {
            List<string> candidates = new List<string>();
            AddCandidate(candidates, preferredPath);
            AddCandidate(candidates, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"));
            AddCandidate(candidates, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"bin\ffmpeg.exe"));
            AddCandidate(candidates, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"tools\ffmpeg.exe"));
            AddCandidate(candidates, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"tools\bin\ffmpeg.exe"));
            AddCandidate(candidates, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\tools\ffmpeg.exe"));
            AddCandidate(candidates, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\tools\bin\ffmpeg.exe"));

            if (!string.IsNullOrWhiteSpace(downloaderPath))
            {
                try
                {
                    string downloaderDirectory = Path.GetDirectoryName(downloaderPath.Trim());
                    if (!string.IsNullOrWhiteSpace(downloaderDirectory))
                    {
                        AddCandidate(candidates, Path.Combine(downloaderDirectory, "ffmpeg.exe"));
                        AddCandidate(candidates, Path.Combine(downloaderDirectory, @"bin\ffmpeg.exe"));
                    }
                }
                catch
                {
                }
            }

            AddPathCandidates(candidates, Environment.GetEnvironmentVariable("PATH"));
            AddPathCandidates(candidates, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddPathCandidates(candidates, Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
            AddCandidate(candidates, WinGetFfmpegLink);
            AddCandidate(candidates, @"C:\ffmpeg\bin\ffmpeg.exe");

            string found = FirstExistingFile(candidates);
            if (found != null)
            {
                return found;
            }

            found = FindInWinGetPackages();
            if (found != null)
            {
                return found;
            }

            found = FindWithWhereExe("ffmpeg.exe");
            if (found != null)
            {
                return found;
            }

            return string.IsNullOrWhiteSpace(preferredPath) ? WinGetFfmpegLink : preferredPath.Trim();
        }

        public static bool IsUsableExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                return File.Exists(path.Trim());
            }
            catch
            {
                return false;
            }
        }

        private static void AddPathCandidates(List<string> candidates, string pathValue)
        {
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                return;
            }

            string[] directories = pathValue.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string directoryValue in directories)
            {
                string directory = directoryValue.Trim().Trim('"');
                if (directory.Length > 0)
                {
                    AddCandidate(candidates, Path.Combine(directory, "ffmpeg.exe"));
                }
            }
        }

        private static string GetDownloadsDirectory()
        {
            try
            {
                const string keyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";
                const string downloadsValueName = "{374DE290-123F-4565-9164-39C4925E467B}";
                object value = Registry.GetValue(keyPath, downloadsValueName, null);
                string configuredPath = value as string;
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    return Environment.ExpandEnvironmentVariables(configuredPath.Trim());
                }
            }
            catch
            {
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
        }

        private static string FindInWinGetPackages()
        {
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Microsoft\WinGet\Packages");
                if (!Directory.Exists(root))
                {
                    return null;
                }

                string[] packages = Directory.GetDirectories(root, "Gyan.FFmpeg*");
                Array.Sort(packages, StringComparer.OrdinalIgnoreCase);
                for (int index = packages.Length - 1; index >= 0; index--)
                {
                    string[] files = Directory.GetFiles(packages[index], "ffmpeg.exe", SearchOption.AllDirectories);
                    if (files.Length > 0)
                    {
                        return files[0];
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string FindWithWhereExe(string executableName)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = "where.exe";
                startInfo.Arguments = CommandLine.QuoteArgument(executableName);
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return null;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(3000))
                    {
                        process.Kill();
                        return null;
                    }

                    string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        if (File.Exists(line.Trim()))
                        {
                            return line.Trim();
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static void AddCandidate(List<string> candidates, string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                candidates.Add(candidate.Trim());
            }
        }

        private static string FirstExistingFile(List<string> candidates)
        {
            HashSet<string> checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in candidates)
            {
                try
                {
                    string fullPath = Path.GetFullPath(candidate);
                    if (checkedPaths.Add(fullPath) && File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
                catch
                {
                }
            }

            return null;
        }
    }

    internal static class FileNameHelper
    {
        private static readonly HashSet<string> GenericNames = new HashSet<string>(
            new[]
            {
                "index", "master", "playlist", "manifest", "chunklist", "stream",
                "hls", "dash", "vod", "live", "media", "video"
            },
            StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ReservedNames = new HashSet<string>(
            new[]
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            },
            StringComparer.OrdinalIgnoreCase);

        private static readonly string[] KnownExtensions =
        {
            ".m3u8", ".mpd", ".mp4", ".mkv", ".ts", ".mov", ".webm", ".m4v"
        };

        public static string FromInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            string value = input.Trim();
            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri))
            {
                string queryName = NameFromQuery(uri);
                if (!string.IsNullOrWhiteSpace(queryName))
                {
                    return CleanFileName(queryName);
                }

                string fromPath = NameFromPath(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(fromPath))
                {
                    return fromPath;
                }
            }

            int queryIndex = value.IndexOfAny(new[] { '?', '#' });
            if (queryIndex >= 0)
            {
                value = value.Substring(0, queryIndex);
            }

            return NameFromPath(value);
        }

        public static string CleanFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string decoded = SafeUrlDecode(value.Trim());
            decoded = StripKnownExtension(decoded);

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(decoded.Length);
            foreach (char character in decoded)
            {
                bool isInvalid = Array.IndexOf(invalid, character) >= 0 || char.IsControl(character);
                builder.Append(isInvalid ? '_' : character);
            }

            string result = Regex.Replace(builder.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
            if (result.Length > 120)
            {
                result = result.Substring(0, 120).TrimEnd('.', ' ');
            }

            string deviceBaseName = result.Split('.')[0].TrimEnd(' ');
            if (ReservedNames.Contains(deviceBaseName))
            {
                result = "_" + result;
            }

            return result;
        }

        private static string NameFromQuery(Uri uri)
        {
            try
            {
                NameValueCollection query = HttpUtility.ParseQueryString(uri.Query);
                string[] keys = { "filename", "file", "name", "title" };
                foreach (string key in keys)
                {
                    string value = query[key];
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string NameFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string[] rawSegments = path.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            string fallback = null;

            for (int index = rawSegments.Length - 1; index >= 0; index--)
            {
                string segment = CleanFileName(rawSegments[index]);
                if (string.IsNullOrWhiteSpace(segment))
                {
                    continue;
                }

                if (fallback == null)
                {
                    fallback = segment;
                }

                if (GenericNames.Contains(segment) || LooksLikeQualityDirectory(segment))
                {
                    continue;
                }

                return segment;
            }

            return fallback ?? "video";
        }

        private static bool LooksLikeQualityDirectory(string value)
        {
            return Regex.IsMatch(
                value,
                @"^(\d{3,5}(p|k|kb|kbit|mbps)?|\d{2,5}x\d{2,5})$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string StripKnownExtension(string value)
        {
            string result = value;
            foreach (string extension in KnownExtensions)
            {
                if (result.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return result.Substring(0, result.Length - extension.Length);
                }
            }

            return result;
        }

        private static string SafeUrlDecode(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value.Replace("+", "%20"));
            }
            catch
            {
                return value;
            }
        }
    }

    internal static class PlaylistInput
    {
        private static readonly Regex HlsUriAttribute = new Regex(
            @"\bURI\s*=\s*(?:""(?<double>[^""]+)""|'(?<single>[^']+)'|(?<plain>[^,\s]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool IsBlobUrl(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.TrimStart().StartsWith("blob:", StringComparison.OrdinalIgnoreCase);
        }

        public static bool LooksLikePlaylistContent(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
                   trimmed.IndexOf("<MPD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   trimmed.StartsWith("<MPD", StringComparison.OrdinalIgnoreCase);
        }

        public static string GetExtension(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return ".m3u8";
            }

            string trimmed = value.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            return trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ? ".m3u8" : ".mpd";
        }

        public static bool ContainsRelativeMediaReferences(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmedValue = value.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (trimmedValue.StartsWith("<", StringComparison.Ordinal))
            {
                return MpdContainsRelativeReferences(trimmedValue);
            }

            string[] lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    MatchCollection uriMatches = HlsUriAttribute.Matches(line);
                    foreach (Match match in uriMatches)
                    {
                        string uriValue = match.Groups["double"].Success
                            ? match.Groups["double"].Value
                            : (match.Groups["single"].Success
                                ? match.Groups["single"].Value
                                : match.Groups["plain"].Value);
                        if (IsRelativeReference(uriValue))
                        {
                            return true;
                        }
                    }

                    continue;
                }

                if (IsRelativeReference(line))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool MpdContainsRelativeReferences(string xml)
        {
            try
            {
                XmlDocument document = new XmlDocument();
                document.XmlResolver = null;
                document.LoadXml(xml);

                XmlNodeList baseUrls = document.SelectNodes("//*[local-name()='BaseURL']");
                if (baseUrls != null)
                {
                    foreach (XmlNode node in baseUrls)
                    {
                        if (node != null &&
                            IsRelativeReference(node.InnerText) &&
                            !HasAbsoluteBaseUrlInScope(node.ParentNode, false))
                        {
                            return true;
                        }
                    }
                }

                XmlNodeList attributes = document.SelectNodes("//@*");
                if (attributes != null)
                {
                    foreach (XmlNode node in attributes)
                    {
                        XmlAttribute attribute = node as XmlAttribute;
                        if (attribute == null)
                        {
                            continue;
                        }

                        string name = attribute.LocalName;
                        bool isMediaReference =
                            string.Equals(name, "media", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "initialization", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "index", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "bitstreamSwitching", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "sourceURL", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(name, "href", StringComparison.OrdinalIgnoreCase);
                        if (isMediaReference &&
                            IsRelativeReference(attribute.Value) &&
                            !HasAbsoluteBaseUrlInScope(attribute.OwnerElement, true))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (XmlException)
            {
                // The downloader will report malformed XML. This method only detects relative references.
            }

            return false;
        }

        private static bool HasAbsoluteBaseUrlInScope(XmlNode node, bool includeDirectBaseUrls)
        {
            XmlNode current = node;
            while (current != null)
            {
                if (includeDirectBaseUrls)
                {
                    foreach (XmlNode child in current.ChildNodes)
                    {
                        if (child.NodeType == XmlNodeType.Element &&
                            string.Equals(child.LocalName, "BaseURL", StringComparison.OrdinalIgnoreCase) &&
                            !IsRelativeReference(child.InnerText))
                        {
                            return true;
                        }
                    }
                }

                includeDirectBaseUrls = true;
                current = current.ParentNode;
            }

            return false;
        }

        private static bool IsRelativeReference(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string reference = value.Trim();
            if (reference.StartsWith("//", StringComparison.Ordinal))
            {
                return true;
            }

            Uri uri;
            return !Uri.TryCreate(reference, UriKind.Absolute, out uri);
        }
    }

    internal static class HlsKeyValue
    {
        public static string Normalize(string value)
        {
            string result = (value ?? string.Empty).Trim();
            if (Regex.IsMatch(
                result,
                @"^0x[0-9a-fA-F]{32}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return result.Substring(2);
            }

            return result;
        }

        public static bool IsRecognized(string value)
        {
            byte[] bytes;
            return string.IsNullOrWhiteSpace(value) || TryGetBytes(value, out bytes);
        }

        public static bool TryGetBytes(string value, out byte[] bytes)
        {
            bytes = null;
            string normalized = Normalize(value);
            if (normalized.Length == 0)
            {
                return false;
            }

            try
            {
                if (File.Exists(normalized))
                {
                    byte[] fileBytes = File.ReadAllBytes(normalized);
                    if (fileBytes.Length == 16)
                    {
                        bytes = fileBytes;
                        return true;
                    }

                    string fileText = Encoding.UTF8.GetString(fileBytes).Trim().TrimStart('\uFEFF');
                    return TryDecodeText(fileText, out bytes);
                }
            }
            catch
            {
                return false;
            }

            return TryDecodeText(normalized, out bytes);
        }

        private static bool TryDecodeText(string value, out byte[] bytes)
        {
            bytes = null;
            string normalized = Normalize(value);
            if (Regex.IsMatch(normalized, @"^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant))
            {
                byte[] decodedHex = new byte[16];
                for (int index = 0; index < decodedHex.Length; index++)
                {
                    decodedHex[index] = Convert.ToByte(normalized.Substring(index * 2, 2), 16);
                }

                bytes = decodedHex;
                return true;
            }

            try
            {
                byte[] decodedBase64 = Convert.FromBase64String(normalized);
                if (decodedBase64.Length == 16)
                {
                    bytes = decodedBase64;
                    return true;
                }
            }
            catch (FormatException)
            {
            }

            return false;
        }
    }

    internal static class SecretFileStore
    {
        private static readonly string SecretDirectory = Path.Combine(
            Path.GetTempPath(),
            @"N_m3u8DL-RE-GUI\Secrets");

        public static string Create(byte[] bytes, string extension)
        {
            if (bytes == null || bytes.Length != 16)
            {
                throw new ArgumentException("HLS secret material must contain exactly 16 bytes.", "bytes");
            }

            EnsureProtectedDirectory();
            string safeExtension = string.Equals(extension, ".iv", StringComparison.OrdinalIgnoreCase)
                ? ".iv"
                : ".key";
            string path = Path.Combine(
                SecretDirectory,
                "secret_" + Guid.NewGuid().ToString("N") + safeExtension);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public static bool Delete(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    string fullPath = Path.GetFullPath(path);
                    string fullDirectory = Path.GetFullPath(SecretDirectory).TrimEnd(Path.DirectorySeparatorChar) +
                                           Path.DirectorySeparatorChar;
                    if (!fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (File.Exists(fullPath))
                    {
                        File.Delete(fullPath);
                    }

                    TryDeleteDirectoryIfEmpty();
                    return !File.Exists(fullPath);
                }
                catch
                {
                    if (attempt < 2)
                    {
                        Thread.Sleep(50);
                    }
                }
            }

            return false;
        }

        public static void CleanupOldFiles()
        {
            try
            {
                if (!Directory.Exists(SecretDirectory))
                {
                    return;
                }

                DateTime cutoff = DateTime.UtcNow.AddDays(-1);
                foreach (string path in Directory.GetFiles(SecretDirectory, "secret_*", SearchOption.TopDirectoryOnly))
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        Delete(path);
                    }
                }

                TryDeleteDirectoryIfEmpty();
            }
            catch
            {
            }
        }

        private static void EnsureProtectedDirectory()
        {
            DirectoryInfo directory = Directory.CreateDirectory(SecretDirectory);
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier user = identity.User;
            if (user == null)
            {
                throw new InvalidOperationException("Unable to identify the current Windows user.");
            }

            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);
        }

        private static void TryDeleteDirectoryIfEmpty()
        {
            try
            {
                if (Directory.Exists(SecretDirectory) &&
                    Directory.GetFileSystemEntries(SecretDirectory).Length == 0)
                {
                    Directory.Delete(SecretDirectory, false);
                }
            }
            catch
            {
            }
        }
    }

    internal static class ConversionFileStore
    {
        public static bool TryCommit(
            string temporaryPath,
            string finalPath,
            out string errorMessage)
        {
            errorMessage = null;
            try
            {
                if (string.IsNullOrWhiteSpace(temporaryPath) ||
                    string.IsNullOrWhiteSpace(finalPath) ||
                    !File.Exists(temporaryPath) ||
                    new FileInfo(temporaryPath).Length == 0)
                {
                    throw new InvalidDataException("FFmpeg 没有生成有效的临时 MP4 文件。");
                }

                string fullTemporaryPath = Path.GetFullPath(temporaryPath);
                string fullFinalPath = Path.GetFullPath(finalPath);
                if (string.Equals(fullTemporaryPath, fullFinalPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("转换临时文件不能与最终文件相同。");
                }

                if (File.Exists(fullFinalPath))
                {
                    File.Replace(fullTemporaryPath, fullFinalPath, null, true);
                }
                else
                {
                    File.Move(fullTemporaryPath, fullFinalPath);
                }

                return true;
            }
            catch (Exception exception)
            {
                errorMessage = exception.Message;
                return false;
            }
        }

        public static bool Delete(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    if (!File.Exists(path))
                    {
                        return true;
                    }
                }
                catch
                {
                }

                Thread.Sleep(50);
            }

            return false;
        }
    }

    internal static class CommandLine
    {
        public static string JoinArguments(IEnumerable<string> arguments)
        {
            StringBuilder builder = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(QuoteArgument(argument));
            }

            return builder.ToString();
        }

        public static string QuoteArgument(string argument)
        {
            if (argument == null)
            {
                return "\"\"";
            }

            StringBuilder builder = new StringBuilder();
            builder.Append('"');
            int backslashes = 0;

            foreach (char character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }

                builder.Append(character);
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes * 2);
            }

            builder.Append('"');
            return builder.ToString();
        }
    }

    internal sealed class ProcessJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;

        private IntPtr _handle;

        private ProcessJob(IntPtr handle)
        {
            _handle = handle;
        }

        public static ProcessJob TryCreateKillOnClose()
        {
            IntPtr handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            JobObjectExtendedLimitInformation information = new JobObjectExtendedLimitInformation();
            information.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            int length = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
            IntPtr pointer = Marshal.AllocHGlobal(length);

            try
            {
                Marshal.StructureToPtr(information, pointer, false);
                if (!NativeMethods.SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformationClass,
                    pointer,
                    (uint)length))
                {
                    NativeMethods.CloseHandle(handle);
                    return null;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            return new ProcessJob(handle);
        }

        public bool AddProcess(Process process)
        {
            if (process == null || _handle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return NativeMethods.AssignProcessToJobObject(_handle, process.Handle);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern IntPtr CreateJobObject(IntPtr jobAttributes, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetInformationJobObject(
                IntPtr job,
                int informationClass,
                IntPtr information,
                uint informationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);
        }
    }
}
