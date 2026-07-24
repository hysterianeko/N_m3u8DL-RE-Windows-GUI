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
        public string LastCaptureUrl;
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
                settings.LastCaptureUrl = ReadElement(root, "lastCaptureUrl", settings.LastCaptureUrl);
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
                    writer.WriteElementString("openFolderWhenDone",
                        settings.OpenFolderWhenDone ? "true" : "false");
                    writer.WriteElementString("lastCaptureUrl", settings.LastCaptureUrl ?? string.Empty);
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
            settings.DownloaderPath = string.Empty;
            settings.FfmpegPath = string.Empty;
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

    internal static class ExternalToolOutputEncodings
    {
        public static Encoding Downloader
        {
            get { return Encoding.Default; }
        }

        public static Encoding Ffmpeg
        {
            get { return Encoding.UTF8; }
        }

        public static void ApplyDownloader(ProcessStartInfo startInfo)
        {
            Apply(startInfo, Downloader);
        }

        public static void ApplyFfmpeg(ProcessStartInfo startInfo)
        {
            Apply(startInfo, Ffmpeg);
        }

        private static void Apply(ProcessStartInfo startInfo, Encoding encoding)
        {
            if (startInfo == null)
            {
                throw new ArgumentNullException("startInfo");
            }

            startInfo.StandardOutputEncoding = encoding;
            startInfo.StandardErrorEncoding = encoding;
        }
    }

    internal sealed class ExternalToolProgress
    {
        public string StreamKind;
        public int Current;
        public int Total;
        public double Percent;
        public string DownloadedSize;
        public string TotalSize;
        public string Speed;
        public string RemainingTime;

        public string Identity
        {
            get
            {
                return (StreamKind ?? string.Empty) + "|" +
                       Current + "|" + Total + "|" +
                       Percent.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "|" +
                       (DownloadedSize ?? string.Empty) + "|" +
                       (TotalSize ?? string.Empty) + "|" +
                       (Speed ?? string.Empty) + "|" +
                       (RemainingTime ?? string.Empty);
            }
        }
    }

    internal sealed class ExternalToolOutputParser
    {
        private const int MaximumPendingCharacters = 65536;
        private const string TimestampHeaderPattern =
            @"(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d{3})?[ \t]+" +
            @"(?:TRACE|DEBUG|INFO|WARN|ERROR|FATAL)[ \t]*:[ \t]*";

        private static readonly Regex TimestampHeader = new Regex(
            TimestampHeaderPattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex ProgressFrame = new Regex(
            @"(?<kind>Vid|Aud|Sub)\b" +
            @"(?:(?!(?:" + TimestampHeaderPattern + @")|(?:Vid|Aud|Sub)\b).){0,768}?" +
            @"(?<current>\d{1,9})/(?<total>\d{1,9})\s+" +
            @"(?<percent>\d{1,3}(?:\.\d+)?)%",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant |
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex ProgressDetails = new Regex(
            @"^\s*(?<downloaded>\d+(?:\.\d+)?\s*[KMGT]?B)" +
            @"(?:/(?<size>\d+(?:\.\d+)?\s*[KMGT]?B))?\s*" +
            @"(?<speed>(?:\d+(?:\.\d+)?\s*[KMGT]?Bps|-))?\s*" +
            @"(?<eta>\d{2}:\d{2}:\d{2})?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex AnsiCsi = new Regex(
            "\\x1B\\[[0-?]*[ -/]*[@-~]",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex AnsiOsc = new Regex(
            "\\x1B\\][^\\x07]*(?:\\x07|\\x1B\\\\)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private readonly StringBuilder _pending = new StringBuilder();
        private readonly Action<string> _logHandler;
        private readonly Action<ExternalToolProgress> _progressHandler;
        private string _lastProgressIdentity = string.Empty;
        private DateTime _lastProgressEmittedUtc = DateTime.MinValue;
        private bool _reportedCompaction;
        private bool _reportedOversizedLog;

        public ExternalToolOutputParser(
            Action<string> logHandler,
            Action<ExternalToolProgress> progressHandler)
        {
            _logHandler = logHandler;
            _progressHandler = progressHandler;
        }

        public void Append(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            _pending.Append(chunk.Replace("\r\n", "\n").Replace('\r', '\n'));
            DrainCompleteRecords(false);
            TryEmitLatestProgress(_pending.ToString());

            if (_pending.Length > MaximumPendingCharacters)
            {
                CompactOversizedPendingText();
            }
        }

        public void Complete()
        {
            DrainCompleteRecords(true);
        }

        private void DrainCompleteRecords(bool flushAll)
        {
            while (_pending.Length > 0)
            {
                if (_pending[0] == '\n')
                {
                    _pending.Remove(0, 1);
                    continue;
                }

                string text = _pending.ToString();
                int boundary = FindNextBoundary(text);
                if (boundary < 0)
                {
                    if (flushAll)
                    {
                        EmitRecord(text);
                        _pending.Clear();
                    }

                    return;
                }

                if (boundary == 0)
                {
                    _pending.Remove(0, 1);
                    continue;
                }

                EmitRecord(text.Substring(0, boundary));
                _pending.Remove(0, boundary);
            }
        }

        private static int FindNextBoundary(string text)
        {
            int searchStart = 1;
            Match timestampAtStart = TimestampHeader.Match(text);
            if (timestampAtStart.Success && timestampAtStart.Index == 0)
            {
                searchStart = Math.Max(searchStart, timestampAtStart.Length);
                Match attachedProgress = ProgressFrame.Match(text, searchStart);
                if (attachedProgress.Success && attachedProgress.Index == searchStart)
                {
                    searchStart = attachedProgress.Groups["kind"].Index +
                                  attachedProgress.Groups["kind"].Length;
                }
            }
            else
            {
                Match progressAtStart = ProgressFrame.Match(text);
                if (progressAtStart.Success && progressAtStart.Index == 0)
                {
                    searchStart = progressAtStart.Groups["kind"].Length;
                }
            }

            int boundary = text.IndexOf('\n', searchStart);
            Match timestamp = TimestampHeader.Match(text, searchStart);
            if (timestamp.Success && (boundary < 0 || timestamp.Index < boundary))
            {
                boundary = timestamp.Index;
            }

            Match progress = ProgressFrame.Match(text, searchStart);
            if (progress.Success && (boundary < 0 || progress.Index < boundary))
            {
                boundary = progress.Index;
            }

            return boundary;
        }

        private void EmitRecord(string rawRecord)
        {
            string record = Sanitize(rawRecord).Trim();
            if (record.Length == 0)
            {
                return;
            }

            ExternalToolProgress progress;
            if (TryParseProgress(record, out progress))
            {
                EmitProgress(progress);
                return;
            }

            if (_logHandler != null)
            {
                _logHandler(record);
            }
        }

        private void TryEmitLatestProgress(string text)
        {
            MatchCollection matches = ProgressFrame.Matches(text);
            if (matches.Count == 0)
            {
                return;
            }

            ExternalToolProgress progress;
            if (TryCreateProgress(text, matches[matches.Count - 1], out progress))
            {
                EmitProgress(progress);
            }
        }

        private void EmitProgress(ExternalToolProgress progress)
        {
            if (progress == null || _progressHandler == null)
            {
                return;
            }

            string identity = progress.Identity;
            DateTime now = DateTime.UtcNow;
            bool positionChanged =
                string.IsNullOrEmpty(_lastProgressIdentity) ||
                !SameProgressPosition(_lastProgressIdentity, progress);
            if (!positionChanged &&
                string.Equals(identity, _lastProgressIdentity, StringComparison.Ordinal) ||
                (!positionChanged && (now - _lastProgressEmittedUtc).TotalMilliseconds < 250))
            {
                return;
            }

            _lastProgressIdentity = identity;
            _lastProgressEmittedUtc = now;
            _progressHandler(progress);
        }

        private static bool SameProgressPosition(string identity, ExternalToolProgress progress)
        {
            string prefix = (progress.StreamKind ?? string.Empty) + "|" +
                            progress.Current + "|" + progress.Total + "|" +
                            progress.Percent.ToString(
                                "0.00",
                                System.Globalization.CultureInfo.InvariantCulture) + "|";
            return identity.StartsWith(prefix, StringComparison.Ordinal);
        }

        private static bool TryParseProgress(string text, out ExternalToolProgress progress)
        {
            MatchCollection matches = ProgressFrame.Matches(text);
            if (matches.Count == 0)
            {
                progress = null;
                return false;
            }

            return TryCreateProgress(text, matches[matches.Count - 1], out progress);
        }

        private static bool TryCreateProgress(
            string text,
            Match match,
            out ExternalToolProgress progress)
        {
            int current;
            int total;
            double percent;
            if (!int.TryParse(match.Groups["current"].Value, out current) ||
                !int.TryParse(match.Groups["total"].Value, out total) ||
                !double.TryParse(
                    match.Groups["percent"].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out percent) ||
                total <= 0 || current < 0 || current > total ||
                percent < 0 || percent > 100)
            {
                progress = null;
                return false;
            }

            string tail = match.Index + match.Length < text.Length
                ? text.Substring(match.Index + match.Length)
                : string.Empty;
            Match details = ProgressDetails.Match(tail);
            progress = new ExternalToolProgress();
            progress.StreamKind = NormalizeStreamKind(match.Groups["kind"].Value);
            progress.Current = current;
            progress.Total = total;
            progress.Percent = percent;
            if (details.Success)
            {
                progress.DownloadedSize = details.Groups["downloaded"].Value.Replace(" ", string.Empty);
                progress.TotalSize = details.Groups["size"].Value.Replace(" ", string.Empty);
                progress.Speed = details.Groups["speed"].Value.Replace(" ", string.Empty);
                progress.RemainingTime = details.Groups["eta"].Value;
            }

            return true;
        }

        private static string NormalizeStreamKind(string value)
        {
            if (string.Equals(value, "Aud", StringComparison.OrdinalIgnoreCase))
            {
                return "Aud";
            }

            if (string.Equals(value, "Sub", StringComparison.OrdinalIgnoreCase))
            {
                return "Sub";
            }

            return "Vid";
        }

        private void CompactOversizedPendingText()
        {
            string text = _pending.ToString();
            ExternalToolProgress progress;
            bool wasProgress = TryParseProgress(text, out progress);
            if (wasProgress)
            {
                EmitProgress(progress);
                _pending.Clear();
                int progressTail = Math.Min(2048, text.Length);
                _pending.Append(text.Substring(text.Length - progressTail));
                if (!_reportedCompaction && _logHandler != null)
                {
                    _reportedCompaction = true;
                    _logHandler("[GUI] 已压缩下载器的高频进度输出。");
                }

                return;
            }

            int headLength = Math.Min(32768, text.Length);
            if (_logHandler != null)
            {
                string head = Sanitize(text.Substring(0, headLength)).Trim();
                if (head.Length > 0)
                {
                    _logHandler(head + " [单条日志过长，后续内容已截断]");
                }

                if (!_reportedOversizedLog)
                {
                    _reportedOversizedLog = true;
                    _logHandler("[GUI] 下载器输出了一条超长日志，界面仅保留其开头和最新尾部。");
                }
            }

            _pending.Clear();
            int logTail = Math.Min(2048, text.Length - headLength);
            if (logTail > 0)
            {
                _pending.Append(text.Substring(text.Length - logTail));
            }
        }

        private static string Sanitize(string value)
        {
            string withoutAnsi = AnsiOsc.Replace(AnsiCsi.Replace(value ?? string.Empty, string.Empty), string.Empty);
            StringBuilder result = new StringBuilder(withoutAnsi.Length);
            foreach (char character in withoutAnsi)
            {
                if (character == '\t' ||
                    (character >= ' ' && !(character >= '\u0080' && character <= '\u009F')))
                {
                    result.Append(character);
                }
            }

            return result.ToString();
        }
    }

    internal sealed class ProcessOutputPump
    {
        private readonly TextReader _standardOutput;
        private readonly TextReader _standardError;
        private readonly Action<string> _outputHandler;
        private readonly Action<string> _errorHandler;
        private readonly Action _outputCompleted;
        private readonly Action _errorCompleted;
        private Thread _outputThread;
        private Thread _errorThread;
        private bool _started;
        private volatile bool _stopRequested;

        public ProcessOutputPump(
            TextReader standardOutput,
            TextReader standardError,
            Action<string> outputHandler,
            Action<string> errorHandler,
            Action outputCompleted,
            Action errorCompleted)
        {
            _standardOutput = standardOutput;
            _standardError = standardError;
            _outputHandler = outputHandler;
            _errorHandler = errorHandler;
            _outputCompleted = outputCompleted;
            _errorCompleted = errorCompleted;
        }

        public void Start()
        {
            if (_started)
            {
                throw new InvalidOperationException("The process output pump has already started.");
            }

            _started = true;
            _outputThread = CreateReaderThread(
                "M3U8 GUI stdout reader",
                _standardOutput,
                _outputHandler,
                _outputCompleted);
            _errorThread = CreateReaderThread(
                "M3U8 GUI stderr reader",
                _standardError,
                _errorHandler,
                _errorCompleted);
            _outputThread.Start();
            _errorThread.Start();
        }

        public bool WaitForCompletion()
        {
            return WaitForCompletion(5000);
        }

        public bool WaitForCompletion(int timeoutMilliseconds)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool outputCompleted = JoinReader(_outputThread, timeoutMilliseconds);
            int remaining = Math.Max(0, timeoutMilliseconds - (int)stopwatch.ElapsedMilliseconds);
            bool errorCompleted = JoinReader(_errorThread, remaining);
            return outputCompleted && errorCompleted;
        }

        public void Stop()
        {
            _stopRequested = true;
            try
            {
                if (_standardOutput != null)
                {
                    _standardOutput.Dispose();
                }
            }
            catch
            {
            }

            try
            {
                if (_standardError != null)
                {
                    _standardError.Dispose();
                }
            }
            catch
            {
            }
        }

        private Thread CreateReaderThread(
            string name,
            TextReader reader,
            Action<string> handler,
            Action completed)
        {
            Thread thread = new Thread(delegate()
            {
                try
                {
                    char[] buffer = new char[2048];
                    int read;
                    while (reader != null && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (!_stopRequested && handler != null)
                        {
                            try
                            {
                                handler(new string(buffer, 0, read));
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    if (!_stopRequested && completed != null)
                    {
                        try
                        {
                            completed();
                        }
                        catch
                        {
                        }
                    }
                }
            });
            thread.IsBackground = true;
            thread.Name = name;
            return thread;
        }

        private static bool JoinReader(Thread thread, int timeoutMilliseconds)
        {
            if (thread == null)
            {
                return true;
            }

            if (ReferenceEquals(Thread.CurrentThread, thread))
            {
                return false;
            }

            try
            {
                return !thread.IsAlive || thread.Join(Math.Max(0, timeoutMilliseconds));
            }
            catch
            {
                return false;
            }
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

        internal static string GetManagedToolsDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "N_m3u8DL-RE-GUI",
                "tools");
        }

        public static string FindDownloader(string preferredPath)
        {
            const string executableName = "N_m3u8DL-RE.exe";
            List<string> candidates = new List<string>();
            AddCandidate(candidates, preferredPath);
            AddCandidate(candidates, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, executableName));
            AddCandidate(candidates, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools", executableName));
            AddCandidate(candidates, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "tools", executableName));
            AddCandidate(candidates, Path.Combine(GetManagedToolsDirectory(), executableName));
            AddCandidate(
                candidates,
                Path.Combine(
                    GetDownloadsDirectory(),
                    executableName));

            AddCandidate(
                candidates,
                FindInPathValues(
                    executableName,
                    Environment.GetEnvironmentVariable("PATH"),
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)));

            List<string> commonDirectories = GetCommonToolDirectories();
            foreach (string directory in commonDirectories)
            {
                AddCandidate(candidates, Path.Combine(directory, executableName));
            }

            string found = FirstExistingFile(candidates);
            if (found == null)
            {
                found = FindWithWhereExe(executableName);
            }

            return found ?? (string.IsNullOrWhiteSpace(preferredPath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, executableName)
                : NormalizePath(preferredPath));
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
            AddCandidate(candidates, Path.Combine(GetManagedToolsDirectory(), "ffmpeg.exe"));

            if (!string.IsNullOrWhiteSpace(downloaderPath))
            {
                try
                {
                    string downloaderDirectory = Path.GetDirectoryName(NormalizePath(downloaderPath));
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

            AddCandidate(
                candidates,
                FindInPathValues(
                    "ffmpeg.exe",
                    Environment.GetEnvironmentVariable("PATH"),
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)));
            AddCandidate(candidates, WinGetFfmpegLink);
            AddCandidate(candidates, @"C:\ffmpeg\bin\ffmpeg.exe");

            List<string> commonDirectories = GetCommonToolDirectories();
            foreach (string directory in commonDirectories)
            {
                AddCandidate(candidates, Path.Combine(directory, "ffmpeg.exe"));
                AddCandidate(candidates, Path.Combine(directory, "bin", "ffmpeg.exe"));
            }

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

            return string.IsNullOrWhiteSpace(preferredPath)
                ? WinGetFfmpegLink
                : NormalizePath(preferredPath);
        }

        public static bool IsUsableExecutable(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                string fullPath = Path.GetFullPath(NormalizePath(path));
                return File.Exists(fullPath) && IsSaneManagedTool(fullPath);
            }
            catch
            {
                return false;
            }
        }

        private static void AddPathCandidates(
            List<string> candidates,
            string pathValue,
            string executableName)
        {
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                return;
            }

            string[] directories = pathValue.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string directoryValue in directories)
            {
                string directory = NormalizePath(directoryValue);
                if (directory.Length > 0)
                {
                    AddCandidate(candidates, Path.Combine(directory, executableName));
                }
            }
        }

        internal static string FindInPathValues(string executableName, params string[] pathValues)
        {
            List<string> candidates = new List<string>();
            if (pathValues != null)
            {
                foreach (string pathValue in pathValues)
                {
                    AddPathCandidates(candidates, pathValue, executableName);
                }
            }

            return FirstExistingFile(candidates);
        }

        private static List<string> GetCommonToolDirectories()
        {
            List<string> fixedDriveRoots = new List<string>();

            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                        {
                            fixedDriveRoots.Add(drive.RootDirectory.FullName);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            return BuildCommonToolDirectories(
                GetDownloadsDirectory(),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                fixedDriveRoots);
        }

        internal static List<string> BuildCommonToolDirectories(
            string configuredDownloads,
            string fallbackDownloads,
            string desktopDirectory,
            IEnumerable<string> fixedDriveRoots)
        {
            List<string> directories = new List<string>();
            AddDirectory(directories, configuredDownloads);
            AddDirectory(directories, fallbackDownloads);
            AddDirectory(directories, desktopDirectory);

            if (fixedDriveRoots != null)
            {
                foreach (string root in fixedDriveRoots)
                {
                    if (string.IsNullOrWhiteSpace(root))
                    {
                        continue;
                    }

                    AddDirectory(directories, Path.Combine(root, "Downloads"));
                    AddDirectory(directories, Path.Combine(root, "Download"));
                    AddDirectory(directories, Path.Combine(root, "下载"));
                }
            }

            return directories;
        }

        private static void AddDirectory(List<string> directories, string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                string fullPath = Path.GetFullPath(NormalizePath(directory));
                if (!Directory.Exists(fullPath))
                {
                    return;
                }

                foreach (string existing in directories)
                {
                    if (string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }

                directories.Add(fullPath);
            }
            catch
            {
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

                string[] packages = Directory.GetDirectories(root, "*FFmpeg*", SearchOption.TopDirectoryOnly);
                Array.Sort(packages, StringComparer.OrdinalIgnoreCase);
                for (int index = packages.Length - 1; index >= 0; index--)
                {
                    List<string> candidates = new List<string>();
                    AddCandidate(candidates, Path.Combine(packages[index], "ffmpeg.exe"));
                    AddCandidate(candidates, Path.Combine(packages[index], "bin", "ffmpeg.exe"));

                    string[] children;
                    try
                    {
                        children = Directory.GetDirectories(
                            packages[index],
                            "*",
                            SearchOption.TopDirectoryOnly);
                        Array.Sort(children, StringComparer.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        children = new string[0];
                    }

                    for (int childIndex = children.Length - 1; childIndex >= 0; childIndex--)
                    {
                        AddCandidate(candidates, Path.Combine(children[childIndex], "ffmpeg.exe"));
                        AddCandidate(candidates, Path.Combine(children[childIndex], "bin", "ffmpeg.exe"));
                    }

                    string found = FirstExistingFile(candidates);
                    if (found != null)
                    {
                        return found;
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

                    if (!process.WaitForExit(3000))
                    {
                        process.Kill();
                        process.WaitForExit(1000);
                        return null;
                    }
                    string output = process.StandardOutput.ReadToEnd();

                    string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        if (IsUsableExecutable(line.Trim()))
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
                candidates.Add(NormalizePath(candidate));
            }
        }

        internal static string NormalizePath(string path)
        {
            string normalized = Environment.ExpandEnvironmentVariables((path ?? string.Empty).Trim());
            if (normalized.Length >= 2 && normalized[0] == '"' && normalized[normalized.Length - 1] == '"')
            {
                normalized = normalized.Substring(1, normalized.Length - 2).Trim();
            }

            return normalized;
        }

        private static string FirstExistingFile(List<string> candidates)
        {
            HashSet<string> checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in candidates)
            {
                try
                {
                    string fullPath = Path.GetFullPath(NormalizePath(candidate));
                    if (checkedPaths.Add(fullPath) &&
                        File.Exists(fullPath) &&
                        IsSaneManagedTool(fullPath))
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

        private static bool IsSaneManagedTool(string path)
        {
            try
            {
                string managedDirectory = Path.GetFullPath(GetManagedToolsDirectory())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.Equals(
                    managedDirectory,
                    directory,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string fileName = Path.GetFileName(path);
                long expectedLength;
                if (string.Equals(
                    fileName,
                    "N_m3u8DL-RE.exe",
                    StringComparison.OrdinalIgnoreCase))
                {
                    expectedLength = DependencyInstaller.DownloaderExecutableLength;
                }
                else if (string.Equals(
                    fileName,
                    "ffmpeg.exe",
                    StringComparison.OrdinalIgnoreCase))
                {
                    expectedLength = DependencyInstaller.FfmpegExecutableLength;
                }
                else
                {
                    return true;
                }

                return new FileInfo(path).Length == expectedLength;
            }
            catch
            {
                return false;
            }
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

    internal static class DownloadTemporaryStore
    {
        private const string DirectoryPrefix = "download_";
        private const string OwnerMarkerName = ".m3u8-gui-owner";
        private const string OwnerMarkerPrefix = "M3u8DownloaderGui:v1:";

        private static readonly string RootDirectory = Path.Combine(
            Path.GetTempPath(),
            @"N_m3u8DL-RE-GUI\Downloads");

        public static string Create()
        {
            Directory.CreateDirectory(RootDirectory);
            string taskId = Guid.NewGuid().ToString("N");
            string path = Path.Combine(
                RootDirectory,
                DirectoryPrefix + taskId);
            Directory.CreateDirectory(path);
            try
            {
                using (FileStream stream = new FileStream(
                    Path.Combine(path, OwnerMarkerName),
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(OwnerMarkerPrefix + taskId);
                }

                return path;
            }
            catch
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                    }

                    TryDeleteRootIfEmpty();
                }
                catch
                {
                }

                throw;
            }
        }

        public static bool Delete(string path)
        {
            string fullPath;
            if (!TryGetOwnedPath(path, out fullPath))
            {
                return false;
            }

            for (int attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    if (Directory.Exists(fullPath))
                    {
                        FileAttributes rootAttributes = File.GetAttributes(fullPath);
                        if ((rootAttributes & FileAttributes.ReparsePoint) != 0 ||
                            !HasValidOwnerMarker(fullPath))
                        {
                            return false;
                        }

                        DeleteDirectoryTree(fullPath);
                    }

                    TryDeleteRootIfEmpty();
                    return !Directory.Exists(fullPath);
                }
                catch
                {
                    if (attempt < 11)
                    {
                        Thread.Sleep(150);
                    }
                }
            }

            return false;
        }

        private static bool TryGetOwnedPath(string path, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                string root = Path.GetFullPath(RootDirectory).TrimEnd(Path.DirectorySeparatorChar);
                string candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                DirectoryInfo parent = Directory.GetParent(candidate);
                string name = Path.GetFileName(candidate);
                Guid taskId;
                if (parent == null ||
                    !string.Equals(parent.FullName, root, StringComparison.OrdinalIgnoreCase) ||
                    name.Length != DirectoryPrefix.Length + 32 ||
                    !name.StartsWith(DirectoryPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !Guid.TryParseExact(name.Substring(DirectoryPrefix.Length), "N", out taskId))
                {
                    return false;
                }

                fullPath = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasValidOwnerMarker(string directory)
        {
            try
            {
                string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
                string taskId = name.Substring(DirectoryPrefix.Length);
                string marker = File.ReadAllText(
                    Path.Combine(directory, OwnerMarkerName),
                    Encoding.UTF8).Trim();
                return string.Equals(
                    marker,
                    OwnerMarkerPrefix + taskId,
                    StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static void DeleteDirectoryTree(string directory)
        {
            FileAttributes attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(directory, false);
                return;
            }

            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                File.Delete(file);
            }

            foreach (string child in Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                DeleteDirectoryTree(child);
            }

            Directory.Delete(directory, false);
        }

        private static void TryDeleteRootIfEmpty()
        {
            try
            {
                if (Directory.Exists(RootDirectory) &&
                    Directory.GetFileSystemEntries(RootDirectory).Length == 0)
                {
                    Directory.Delete(RootDirectory, false);
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
