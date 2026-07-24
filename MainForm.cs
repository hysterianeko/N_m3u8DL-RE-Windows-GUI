using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using CancellationTokenSource = System.Threading.CancellationTokenSource;
using SemaphoreSlim = System.Threading.SemaphoreSlim;

namespace M3u8DownloaderGui
{
    internal sealed class MainForm : Form
    {
        private static readonly Color BackgroundColor = Color.FromArgb(246, 248, 249);
        private static readonly Color SurfaceColor = Color.White;
        private static readonly Color TextColor = Color.FromArgb(31, 41, 51);
        private static readonly Color MutedTextColor = Color.FromArgb(91, 103, 112);
        private static readonly Color BorderColor = Color.FromArgb(210, 218, 223);
        private static readonly Color AccentColor = Color.FromArgb(20, 122, 88);
        private static readonly Color AccentHoverColor = Color.FromArgb(15, 103, 74);
        private static readonly Color DangerColor = Color.FromArgb(183, 55, 53);
        private static readonly Color InvalidColor = Color.FromArgb(255, 238, 236);
        private static readonly Color ValidColor = Color.White;
        private static readonly Color DisabledControlColor = Color.FromArgb(231, 235, 237);
        private static readonly Color DisabledTextColor = Color.FromArgb(132, 142, 149);
        private static readonly Color DisabledBorderColor = Color.FromArgb(203, 210, 214);
        private static readonly Regex DownloaderLogPrefix = new Regex(
            @"^(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d{3})?[ \t]+" +
            @"(?:TRACE|DEBUG|INFO|WARN|ERROR|FATAL)[ \t]*:[ \t]*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex DownloaderErrorSeverity = new Regex(
            @"(?:^|\s)(?:ERROR|FATAL)[ \t]*:[ \t]*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex DownloaderWarningSeverity = new Regex(
            @"(?:^|\s)WARN[ \t]*:[ \t]*",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private const int MaximumLogCharacters = 1000000;

        private readonly ToolTip _toolTip;
        private readonly UserSettings _settings;
        private readonly bool _enableStartupDependencyPrompt;
        private readonly ConcurrentQueue<string> _pendingLogLines = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<ExternalToolProgress> _pendingProgressUpdates =
            new ConcurrentQueue<ExternalToolProgress>();
        private readonly object _progressLogSync = new object();
        private readonly SemaphoreSlim _dependencyWorkflowGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, int> _progressLogBuckets =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private TextBox _urlTextBox;
        private TextBox _saveDirectoryTextBox;
        private TextBox _fileNameTextBox;
        private TextBox _downloaderPathTextBox;
        private TextBox _ffmpegPathTextBox;
        private TextBox _logTextBox;

        private Button _pasteButton;
        private Button _browseDirectoryButton;
        private Button _autoNameButton;
        private Button _browseDownloaderButton;
        private Button _browseFfmpegButton;
        private Button _detectToolsButton;
        private Button _startButton;
        private Button _cancelButton;
        private Button _openFolderButton;
        private Button _keyOptionsButton;
        private Button _convertFileButton;
        private Button _copyLogButton;
        private Button _clearLogButton;
        private Button _captureButton;

        private CheckBox _muxToMp4CheckBox;
        private CheckBox _openFolderWhenDoneCheckBox;
        private Label _toolStatusLabel;
        private Label _statusLabel;
        private ProgressBar _progressBar;
        private Timer _logFlushTimer;

        private Process _activeProcess;
        private ProcessJob _processJob;
        private Process _downloadOutputProcess;
        private ProcessOutputPump _downloadOutputPump;
        private ExternalToolOutputParser _downloadStandardOutputParser;
        private ExternalToolOutputParser _downloadStandardErrorParser;
        private CurlMediaProxy _activeMediaProxy;
        private bool _isCancelling;
        private bool _isPausing;
        private bool _updatingAutoName;
        private bool _fileNameWasEdited;
        private string _lastAutoName = string.Empty;
        private DateTime _downloadStartedUtc;
        private Dictionary<string, FileStamp> _filesBeforeDownload;
        private string _lastOutputPath;
        private string _expectedOutputBaseName;
        private string _importedPlaylistPath;
        private string _supersededImportedPlaylistPath;
        private string _manualHlsKey = string.Empty;
        private string _manualHlsIv = string.Empty;
        private MediaRequestHeaders _capturedHeaders;
        private string _temporaryHlsKeyPath;
        private string _temporaryHlsIvPath;
        private string _downloadTemporaryDirectory;
        private DownloadRequest _resumableRequest;
        private DownloadResumeManifest _downloadResumeManifest;
        private DownloadResumeActivityLease _downloadResumeLease;
        private DownloadTaskState _downloadTaskState;
        private bool _restoringResumeTask;
        private bool _resumeTaskTouchedThisSession;
        private readonly List<string> _secretRedactionValues = new List<string>();
        private OperationKind _activeOperation;
        private DownloadPhase _downloadPhase;
        private string _activeOperationDirectory;
        private string _conversionFinalOutputPath;
        private string _conversionTemporaryOutputPath;
        private bool _startupDependencyPromptShown;
        private bool _toolDetectionInProgress;
        private Task<string[]> _toolDetectionTask;
        private bool _dependencyInstallInProgress;
        private bool _closeRequestedDuringDependencyInstall;
        private CancellationTokenSource _dependencyInstallCancellation;
        private Task<DependencyInstallResult> _dependencyInstallTask;
        private string _lastDependencyInstallStage;
        private string _lastDownloaderError;
        private string _lastDownloaderWarning;

        public MainForm()
            : this(true)
        {
        }

        internal MainForm(bool enableStartupDependencyPrompt)
        {
            _enableStartupDependencyPrompt = enableStartupDependencyPrompt;
            _toolTip = new ToolTip();
            _toolTip.AutoPopDelay = 8000;
            _toolTip.InitialDelay = 450;
            _toolTip.ReshowDelay = 100;

            _settings = SettingsStore.Load();

            InitializeForm();
            BuildInterface();
            ApplySettings();
            WireEvents();
            ApplyCueBanners();
            SetToolStatusPending();
            UpdateKeyState();
            CleanupOldImportedPlaylists(GetImportedPlaylistDirectory());
            SecretFileStore.CleanupOldFiles();

            _logFlushTimer = new Timer();
            _logFlushTimer.Interval = 100;
            _logFlushTimer.Tick += delegate { FlushPendingOutput(); };
            _logFlushTimer.Start();
        }

        internal bool RunSmokeTest()
        {
            PerformLayout();

            bool controlsExist =
                _urlTextBox != null &&
                _saveDirectoryTextBox != null &&
                _fileNameTextBox != null &&
                _downloaderPathTextBox != null &&
                _ffmpegPathTextBox != null &&
                _startButton != null &&
                _keyOptionsButton != null &&
                _convertFileButton != null &&
                _logTextBox != null;

            bool namingWorks = string.Equals(
                FileNameHelper.FromInput("https://media.example.com/20260503/ExampleVideo/index.m3u8"),
                "ExampleVideo",
                StringComparison.Ordinal);

            bool layoutIsStable =
                ClientSize.Width >= 760 &&
                ClientSize.Height >= 640 &&
                _startButton.Width >= 120 &&
                _logTextBox.Height > 80;

            SetRunningState(true);
            bool runningStateIsClear =
                !_keyOptionsButton.Enabled &&
                !_convertFileButton.Enabled &&
                _keyOptionsButton.BackColor == DisabledControlColor &&
                _convertFileButton.BackColor == DisabledControlColor &&
                _keyOptionsButton.ForeColor == DisabledTextColor &&
                _convertFileButton.ForeColor == DisabledTextColor &&
                _keyOptionsButton.FlatAppearance.BorderColor == DisabledBorderColor &&
                _convertFileButton.FlatAppearance.BorderColor == DisabledBorderColor &&
                _keyOptionsButton.Cursor == Cursors.Default &&
                _convertFileButton.Cursor == Cursors.Default;
            SetRunningState(false);
            bool idleStateIsRestored =
                _keyOptionsButton.Enabled &&
                _convertFileButton.Enabled &&
                _keyOptionsButton.BackColor == SurfaceColor &&
                _convertFileButton.BackColor == SurfaceColor &&
                _keyOptionsButton.ForeColor == TextColor &&
                _convertFileButton.ForeColor == TextColor &&
                _keyOptionsButton.FlatAppearance.BorderColor == BorderColor &&
                _convertFileButton.FlatAppearance.BorderColor == BorderColor &&
                _keyOptionsButton.Cursor == Cursors.Hand &&
                _convertFileButton.Cursor == Cursors.Hand;

            _manualHlsKey = "00112233445566778899aabbccddeeff";
            UpdateKeyState();
            SetRunningState(true);
            bool configuredKeyIsDisabledClearly =
                !_keyOptionsButton.Enabled &&
                _keyOptionsButton.BackColor == DisabledControlColor;
            SetRunningState(false);
            bool configuredKeyStyleIsRestored =
                _keyOptionsButton.Enabled &&
                _keyOptionsButton.ForeColor == AccentColor &&
                _keyOptionsButton.FlatAppearance.BorderColor == AccentColor;
            _manualHlsKey = string.Empty;
            UpdateKeyState();
            bool clearedKeyStyleIsRestored =
                _keyOptionsButton.Text == "密钥..." &&
                _keyOptionsButton.ForeColor == TextColor &&
                _keyOptionsButton.FlatAppearance.BorderColor == BorderColor &&
                _startButton.Text == "开始下载";

            _downloadTaskState = DownloadTaskState.Paused;
            UpdateDownloadActionButtons();
            bool pausedActionsAreClear =
                _startButton.Text == "继续" &&
                _startButton.Enabled &&
                _cancelButton.Text == "清除缓存" &&
                _cancelButton.Enabled;
            _downloadTaskState = DownloadTaskState.Failed;
            UpdateDownloadActionButtons();
            bool failedActionCanRetry =
                _startButton.Text == "重试下载" && _startButton.Enabled;
            _downloadTaskState = DownloadTaskState.Completed;
            UpdateDownloadActionButtons();
            bool completedActionCanReset =
                _startButton.Text == "完成" &&
                _startButton.Enabled &&
                !_cancelButton.Enabled;
            _downloadTaskState = DownloadTaskState.Idle;
            UpdateDownloadActionButtons();

            Process smokeProcess = new Process();
            _activeProcess = smokeProcess;
            _activeOperation = OperationKind.Download;
            _isCancelling = false;
            ResetDownloadProgress();
            SetRunningState(true);
            _statusLabel.Text = "正在解析视频信息...";
            UpdateStatusFromLog("16:00:00.000 WARN : 检测到特殊模式，将自动开启二进制合并");
            bool mergeModeWarningDoesNotChangePhase =
                _downloadPhase == DownloadPhase.Starting &&
                _statusLabel.Text == "正在解析视频信息...";
            UpdateStatusFromLog("16:00:00.001 INFO : 正在解析媒体信息...");
            ExternalToolProgress smokeProgress = new ExternalToolProgress();
            smokeProgress.StreamKind = "Vid";
            smokeProgress.Current = 5;
            smokeProgress.Total = 20;
            smokeProgress.Percent = 25;
            smokeProgress.DownloadedSize = "5.00MB";
            smokeProgress.TotalSize = "20.00MB";
            smokeProgress.Speed = "1.00MBps";
            smokeProgress.RemainingTime = "00:00:15";
            ApplyDownloadProgress(smokeProgress);
            bool numericProgressIsVisible =
                _downloadPhase == DownloadPhase.Downloading &&
                _progressBar.Style == ProgressBarStyle.Blocks &&
                _progressBar.Value == 25 &&
                _statusLabel.Text.IndexOf("5/20", StringComparison.Ordinal) >= 0;
            UpdateStatusFromLog("16:00:00.002 INFO : 二进制合并中...");
            string mergingStatus = _statusLabel.Text;
            ProgressBarStyle mergingStyle = _progressBar.Style;
            smokeProgress.Current = 10;
            smokeProgress.Percent = 50;
            ApplyDownloadProgress(smokeProgress);
            bool lateProgressDoesNotUndoMerging =
                _downloadPhase == DownloadPhase.Merging &&
                _statusLabel.Text == mergingStatus &&
                _progressBar.Style == mergingStyle &&
                mergingStyle == ProgressBarStyle.Marquee;
            _downloadPhase = DownloadPhase.Parsing;
            _isCancelling = true;
            _statusLabel.Text = "正在取消任务...";
            UpdateStatusFromLog("16:00:00.003 INFO : 二进制合并中...");
            ApplyDownloadProgress(smokeProgress);
            bool cancellationStatusHasPriority =
                _statusLabel.Text == "正在取消任务..." &&
                _downloadPhase == DownloadPhase.Parsing;

            _activeProcess = null;
            _isCancelling = false;
            smokeProcess.Dispose();
            SetRunningState(false);
            ResetDownloadProgress();
            ExternalToolProgress milestoneProgress = new ExternalToolProgress();
            milestoneProgress.StreamKind = "Vid";
            milestoneProgress.Total = 100;
            milestoneProgress.Current = 1;
            milestoneProgress.Percent = 1;
            string firstMilestone = CreateProgressMilestone(milestoneProgress);
            milestoneProgress.Current = 4;
            milestoneProgress.Percent = 4;
            string duplicateBucket = CreateProgressMilestone(milestoneProgress);
            milestoneProgress.Current = 5;
            milestoneProgress.Percent = 5;
            string nextMilestone = CreateProgressMilestone(milestoneProgress);
            milestoneProgress.Current = 100;
            milestoneProgress.Percent = 100;
            string completedMilestone = CreateProgressMilestone(milestoneProgress);
            bool progressMilestonesAreDeduplicated =
                !string.IsNullOrEmpty(firstMilestone) &&
                string.IsNullOrEmpty(duplicateBucket) &&
                !string.IsNullOrEmpty(nextMilestone) &&
                string.IsNullOrEmpty(completedMilestone);

            _logTextBox.Clear();
            AppendLogCore(new string('A', 600000));
            AppendLogCore(new string('B', 600000));
            bool logLimitIsEnforced = _logTextBox.TextLength <= MaximumLogCharacters;
            _logTextBox.Clear();
            _statusLabel.Text = "就绪";
            bool restartResumeUiWorks = RunRestartResumeUiSmokeTest();

            return controlsExist && namingWorks && layoutIsStable &&
                   runningStateIsClear && idleStateIsRestored &&
                   configuredKeyIsDisabledClearly && configuredKeyStyleIsRestored &&
                   clearedKeyStyleIsRestored && pausedActionsAreClear &&
                   failedActionCanRetry && completedActionCanReset &&
                   mergeModeWarningDoesNotChangePhase &&
                   numericProgressIsVisible && lateProgressDoesNotUndoMerging &&
                   cancellationStatusHasPriority && progressMilestonesAreDeduplicated &&
                   logLimitIsEnforced && restartResumeUiWorks;
        }

        private bool RunRestartResumeUiSmokeTest()
        {
            string testRoot = Path.Combine(
                Path.GetTempPath(),
                "M3u8DownloaderGui_UiResumeSmoke_" + Guid.NewGuid().ToString("N"));
            IDisposable rootScope = null;
            string cacheDirectory = null;
            try
            {
                rootScope = DownloadResumeStore.UseIsolatedRootForTests(testRoot);
                cacheDirectory = DownloadResumeStore.CreateOwnedCacheForTests();
                string playlist =
                    "#EXTM3U\n#EXTINF:4,\nhttps://cdn.example.test/video0.ts?token=secret\n";
                MediaRequestHeaders headers = new MediaRequestHeaders();
                headers.Cookie = "session=resume-smoke-secret";
                headers.Referer = "https://example.test/watch";
                headers.UserAgent = "ResumeSmoke/1.0";
                headers.SourceUrl = "https://cdn.example.test/video0.ts?token=secret";

                DownloadResumeManifest manifest = new DownloadResumeManifest();
                manifest.CacheDirectory = cacheDirectory;
                manifest.State = DownloadResumeState.Running;
                manifest.SaveDirectory = Path.Combine(testRoot, "output");
                manifest.FileName = "restart-resume-smoke";
                manifest.DownloaderPath = Path.Combine(testRoot, "N_m3u8DL-RE.exe");
                manifest.FfmpegPath = Path.Combine(testRoot, "ffmpeg.exe");
                manifest.MuxToMp4 = true;
                manifest.Input = Path.Combine(testRoot, "deleted-import.m3u8");
                manifest.InputIsImportedPlaylist = true;
                manifest.ImportedPlaylistContent = playlist;
                manifest.CapturedHeaders = headers;
                string saveError;
                if (!DownloadResumeStore.TrySave(manifest, out saveError))
                {
                    return false;
                }

                RestoreInterruptedDownload();
                bool restored =
                    _downloadTaskState == DownloadTaskState.Paused &&
                    _resumableRequest != null &&
                    string.Equals(
                        _downloadTemporaryDirectory,
                        cacheDirectory,
                        StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(_resumableRequest.Input) &&
                    string.Equals(_resumableRequest.FileName, manifest.FileName, StringComparison.Ordinal) &&
                    _capturedHeaders != null &&
                    string.Equals(
                        _capturedHeaders.Cookie,
                        headers.Cookie,
                        StringComparison.Ordinal) &&
                    _downloadResumeLease != null &&
                    _startButton.Text == "继续" &&
                    _cancelButton.Text == "清除缓存";

                string restoredInput = _resumableRequest.Input;
                _urlTextBox.Text = "https://draft.example.test/other.m3u8";
                bool editingDoesNotDiscard =
                    Directory.Exists(cacheDirectory) &&
                    File.Exists(Path.Combine(
                        cacheDirectory,
                        DownloadResumeStore.ManifestFileName));
                SetInputWithoutTaskTransition(restoredInput);
                string materializeError;
                bool materializedOnDemand = EnsureRestoredInputMaterialized(
                    out materializeError) &&
                    File.Exists(_resumableRequest.Input);
                string materializedInput = _importedPlaylistPath;
                DeleteImportedPlaylist(false);
                bool closeCleanupPreservesResume =
                    !File.Exists(materializedInput) &&
                    Directory.Exists(cacheDirectory) &&
                    File.Exists(Path.Combine(
                        cacheDirectory,
                        DownloadResumeStore.ManifestFileName)) &&
                    _downloadTaskState == DownloadTaskState.Paused &&
                    _resumableRequest != null;
                bool discarded = DiscardResumableDownload() &&
                    !Directory.Exists(cacheDirectory) &&
                    _capturedHeaders == null;
                cacheDirectory = null;
                return restored && editingDoesNotDiscard && materializedOnDemand &&
                    closeCleanupPreservesResume && discarded;
            }
            catch
            {
                return false;
            }
            finally
            {
                ReleaseDownloadResumeLease();
                if (!string.IsNullOrWhiteSpace(cacheDirectory) && Directory.Exists(cacheDirectory))
                {
                    string discardError;
                    DownloadResumeStore.TryDiscard(cacheDirectory, out discardError);
                }
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

        private void InitializeForm()
        {
            SuspendLayout();
            Text = "M3U8 视频下载器 1.4.0";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(900, 760);
            MinimumSize = new Size(800, 680);
            BackColor = BackgroundColor;
            ForeColor = TextColor;
            Font = CreateUiFont(9F, FontStyle.Regular);
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Padding = new Padding(18, 14, 18, 16);
            MaximizeBox = true;

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
            }

            ResumeLayout(false);
        }

        private void BuildInterface()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Margin = Padding.Empty;
            root.Padding = Padding.Empty;
            root.BackColor = BackgroundColor;
            root.ColumnCount = 1;
            root.RowCount = 6;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 204F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildTaskSection(), 0, 1);
            root.Controls.Add(BuildToolsSection(), 0, 2);
            root.Controls.Add(BuildActions(), 0, 3);
            root.Controls.Add(BuildStatusBar(), 0, 4);
            root.Controls.Add(BuildLogSection(), 0, 5);

            Controls.Add(root);
            AcceptButton = _startButton;
        }

        private Control BuildHeader()
        {
            Panel panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.BackColor = BackgroundColor;
            panel.Margin = new Padding(0, 0, 0, 4);

            Label title = new Label();
            title.AutoSize = true;
            title.Location = new Point(0, 4);
            title.Font = CreateUiFont(17F, FontStyle.Bold);
            title.ForeColor = TextColor;
            title.Text = "M3U8 视频下载器";

            Label subtitle = new Label();
            subtitle.AutoSize = true;
            subtitle.Location = new Point(2, 38);
            subtitle.Font = CreateUiFont(8.5F, FontStyle.Regular);
            subtitle.ForeColor = MutedTextColor;
            subtitle.Text = "N_m3u8DL-RE  ·  FFmpeg";

            Panel line = new Panel();
            line.Dock = DockStyle.Bottom;
            line.Height = 1;
            line.BackColor = BorderColor;

            panel.Controls.Add(title);
            panel.Controls.Add(subtitle);
            panel.Controls.Add(line);
            return panel;
        }

        private Control BuildTaskSection()
        {
            TableLayoutPanel layout = CreateSectionLayout(5);
            layout.Margin = new Padding(0, 6, 0, 6);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35F));

            Label title = CreateSectionTitle("下载任务");
            layout.Controls.Add(title, 0, 0);
            layout.SetColumnSpan(title, 3);

            _urlTextBox = CreateInputTextBox();
            _pasteButton = CreateSecondaryButton("粘贴");
            AddFieldRow(layout, 1, "视频链接", _urlTextBox, _pasteButton);

            _saveDirectoryTextBox = CreateInputTextBox();
            _browseDirectoryButton = CreateSecondaryButton("浏览...");
            AddFieldRow(layout, 2, "保存目录", _saveDirectoryTextBox, _browseDirectoryButton);

            _fileNameTextBox = CreateInputTextBox();
            _autoNameButton = CreateSecondaryButton("自动命名");
            AddFieldRow(layout, 3, "文件名称", _fileNameTextBox, _autoNameButton);

            Label optionLabel = CreateFieldLabel("完成处理");
            optionLabel.Margin = new Padding(0, 5, 8, 0);
            layout.Controls.Add(optionLabel, 0, 4);

            FlowLayoutPanel options = new FlowLayoutPanel();
            options.Dock = DockStyle.Fill;
            options.FlowDirection = FlowDirection.LeftToRight;
            options.WrapContents = false;
            options.Margin = Padding.Empty;
            options.Padding = new Padding(0, 3, 0, 0);

            _muxToMp4CheckBox = CreateCheckBox("混流为 MP4");
            _openFolderWhenDoneCheckBox = CreateCheckBox("完成后打开目录");
            options.Controls.Add(_muxToMp4CheckBox);
            options.Controls.Add(_openFolderWhenDoneCheckBox);
            layout.Controls.Add(options, 1, 4);
            layout.SetColumnSpan(options, 2);

            _toolTip.SetToolTip(_urlTextBox, "支持直接的 .m3u8、.mpd 链接或本地播放列表文件");
            _toolTip.SetToolTip(_fileNameTextBox, "可以修改；扩展名由下载结果决定");
            _toolTip.SetToolTip(_muxToMp4CheckBox, "下载完成后尝试使用 FFmpeg 混流为 MP4");

            return layout;
        }

        private Control BuildToolsSection()
        {
            TableLayoutPanel layout = CreateSectionLayout(4);
            layout.Margin = new Padding(0, 0, 0, 6);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));

            Label title = CreateSectionTitle("工具路径");
            layout.Controls.Add(title, 0, 0);
            layout.SetColumnSpan(title, 2);

            _detectToolsButton = CreateSecondaryButton("自动检测");
            _detectToolsButton.Margin = new Padding(0, 0, 0, 4);
            layout.Controls.Add(_detectToolsButton, 2, 0);

            _downloaderPathTextBox = CreateInputTextBox();
            _browseDownloaderButton = CreateSecondaryButton("浏览...");
            AddFieldRow(layout, 1, "下载程序", _downloaderPathTextBox, _browseDownloaderButton);

            _ffmpegPathTextBox = CreateInputTextBox();
            _browseFfmpegButton = CreateSecondaryButton("浏览...");
            AddFieldRow(layout, 2, "FFmpeg", _ffmpegPathTextBox, _browseFfmpegButton);

            _toolStatusLabel = new Label();
            _toolStatusLabel.AutoSize = true;
            _toolStatusLabel.Anchor = AnchorStyles.Left;
            _toolStatusLabel.Font = CreateUiFont(8.5F, FontStyle.Regular);
            _toolStatusLabel.ForeColor = MutedTextColor;
            _toolStatusLabel.Margin = new Padding(0, 2, 0, 0);
            layout.Controls.Add(_toolStatusLabel, 1, 3);
            layout.SetColumnSpan(_toolStatusLabel, 2);

            _toolTip.SetToolTip(_downloaderPathTextBox, "N_m3u8DL-RE.exe 的完整路径");
            _toolTip.SetToolTip(_ffmpegPathTextBox, "ffmpeg.exe 的完整路径");
            return layout;
        }

        private Control BuildActions()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Margin = new Padding(0, 4, 0, 4);
            layout.Padding = Padding.Empty;
            layout.ColumnCount = 2;
            layout.RowCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            FlowLayoutPanel primaryActions = new FlowLayoutPanel();
            primaryActions.Dock = DockStyle.Fill;
            primaryActions.FlowDirection = FlowDirection.LeftToRight;
            primaryActions.WrapContents = false;
            primaryActions.Margin = Padding.Empty;
            primaryActions.Padding = Padding.Empty;

            _startButton = CreatePrimaryButton("开始下载", 132);
            _cancelButton = CreateDangerButton("取消", 82);
            _cancelButton.Enabled = false;
            _openFolderButton = CreateSecondaryButton("打开目录");
            _openFolderButton.Width = 96;
            _keyOptionsButton = CreateSecondaryButton("密钥...");
            _keyOptionsButton.Width = 94;
            _convertFileButton = CreateSecondaryButton("转换文件...");
            _convertFileButton.Width = 104;

            _captureButton = CreateSecondaryButton("从网页捕获");
            _captureButton.Width = 104;

            primaryActions.Controls.Add(_startButton);
            primaryActions.Controls.Add(_cancelButton);
            primaryActions.Controls.Add(_openFolderButton);
            primaryActions.Controls.Add(_keyOptionsButton);
            primaryActions.Controls.Add(_convertFileButton);
            primaryActions.Controls.Add(_captureButton);

            FlowLayoutPanel logActions = new FlowLayoutPanel();
            logActions.Dock = DockStyle.Fill;
            logActions.AutoSize = true;
            logActions.FlowDirection = FlowDirection.LeftToRight;
            logActions.WrapContents = false;
            logActions.Margin = Padding.Empty;
            logActions.Padding = Padding.Empty;

            _copyLogButton = CreateSecondaryButton("复制日志");
            _copyLogButton.Width = 88;
            _clearLogButton = CreateSecondaryButton("清空");
            _clearLogButton.Width = 72;
            logActions.Controls.Add(_copyLogButton);
            logActions.Controls.Add(_clearLogButton);

            layout.Controls.Add(primaryActions, 0, 0);
            layout.Controls.Add(logActions, 1, 0);
            return layout;
        }

        private Control BuildStatusBar()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Margin = Padding.Empty;
            layout.Padding = Padding.Empty;
            layout.ColumnCount = 2;
            layout.RowCount = 1;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            _progressBar = new ProgressBar();
            _progressBar.Dock = DockStyle.Fill;
            _progressBar.Margin = new Padding(0, 7, 12, 7);
            _progressBar.Style = ProgressBarStyle.Blocks;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 100;

            _statusLabel = new Label();
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.AutoEllipsis = true;
            _statusLabel.ForeColor = MutedTextColor;
            _statusLabel.Text = "就绪";

            layout.Controls.Add(_progressBar, 0, 0);
            layout.Controls.Add(_statusLabel, 1, 0);
            return layout;
        }

        private Control BuildLogSection()
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Margin = new Padding(0, 3, 0, 0);
            layout.Padding = Padding.Empty;
            layout.ColumnCount = 1;
            layout.RowCount = 2;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            Label title = CreateSectionTitle("运行日志");
            layout.Controls.Add(title, 0, 0);

            _logTextBox = new TextBox();
            _logTextBox.Dock = DockStyle.Fill;
            _logTextBox.Margin = Padding.Empty;
            _logTextBox.Multiline = true;
            _logTextBox.ReadOnly = true;
            _logTextBox.WordWrap = false;
            _logTextBox.ScrollBars = ScrollBars.Both;
            _logTextBox.BorderStyle = BorderStyle.FixedSingle;
            _logTextBox.BackColor = SurfaceColor;
            _logTextBox.ForeColor = Color.FromArgb(43, 52, 58);
            _logTextBox.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            layout.Controls.Add(_logTextBox, 0, 1);
            return layout;
        }

        private TableLayoutPanel CreateSectionLayout(int rowCount)
        {
            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.BackColor = BackgroundColor;
            layout.Padding = Padding.Empty;
            layout.ColumnCount = 3;
            layout.RowCount = rowCount;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98F));
            return layout;
        }

        private void AddFieldRow(
            TableLayoutPanel layout,
            int row,
            string labelText,
            TextBox textBox,
            Button button)
        {
            Label label = CreateFieldLabel(labelText);
            label.Margin = new Padding(0, 0, 8, 0);
            textBox.Margin = new Padding(0, 5, 10, 5);
            button.Margin = new Padding(0, 4, 0, 4);
            layout.Controls.Add(label, 0, row);
            layout.Controls.Add(textBox, 1, row);
            layout.Controls.Add(button, 2, row);
        }

        private Label CreateSectionTitle(string text)
        {
            Label label = new Label();
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Font = CreateUiFont(10.5F, FontStyle.Bold);
            label.ForeColor = TextColor;
            label.Text = text;
            label.Margin = Padding.Empty;
            return label;
        }

        private Label CreateFieldLabel(string text)
        {
            Label label = new Label();
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.AutoEllipsis = true;
            label.ForeColor = TextColor;
            label.Text = text;
            return label;
        }

        private TextBox CreateInputTextBox()
        {
            TextBox textBox = new TextBox();
            textBox.Dock = DockStyle.Fill;
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.BackColor = SurfaceColor;
            textBox.ForeColor = TextColor;
            return textBox;
        }

        private CheckBox CreateCheckBox(string text)
        {
            CheckBox checkBox = new CheckBox();
            checkBox.AutoSize = true;
            checkBox.Text = text;
            checkBox.ForeColor = TextColor;
            checkBox.Margin = new Padding(0, 2, 24, 0);
            return checkBox;
        }

        private Button CreatePrimaryButton(string text, int width)
        {
            Button button = CreateFlatButton(text);
            button.Width = width;
            button.BackColor = AccentColor;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = AccentHoverColor;
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(12, 86, 61);
            return button;
        }

        private Button CreateDangerButton(string text, int width)
        {
            Button button = CreateFlatButton(text);
            button.Width = width;
            button.BackColor = SurfaceColor;
            button.ForeColor = DangerColor;
            button.FlatAppearance.BorderColor = Color.FromArgb(220, 170, 168);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 241, 240);
            return button;
        }

        private Button CreateSecondaryButton(string text)
        {
            Button button = CreateFlatButton(text);
            button.Width = 94;
            button.BackColor = SurfaceColor;
            button.ForeColor = TextColor;
            button.FlatAppearance.BorderColor = BorderColor;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(237, 241, 243);
            return button;
        }

        private Button CreateFlatButton(string text)
        {
            Button button = new Button();
            button.Height = 34;
            button.Margin = new Padding(0, 0, 8, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            button.Text = text;
            button.AutoEllipsis = true;
            return button;
        }

        private static Font CreateUiFont(float size, FontStyle style)
        {
            try
            {
                return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
            }
            catch
            {
                return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style, GraphicsUnit.Point);
            }
        }

        private void WireEvents()
        {
            _urlTextBox.TextChanged += UrlTextBoxTextChanged;
            _urlTextBox.KeyDown += UrlTextBoxKeyDown;
            _fileNameTextBox.TextChanged += FileNameTextBoxTextChanged;
            _downloaderPathTextBox.TextChanged += delegate { SetToolStatusPending(); };
            _ffmpegPathTextBox.TextChanged += delegate { SetToolStatusPending(); };

            _pasteButton.Click += PasteButtonClick;
            _browseDirectoryButton.Click += BrowseDirectoryButtonClick;
            _autoNameButton.Click += delegate { ApplyAutomaticName(true); };
            _browseDownloaderButton.Click += BrowseDownloaderButtonClick;
            _browseFfmpegButton.Click += BrowseFfmpegButtonClick;
            _detectToolsButton.Click += DetectToolsButtonClick;
            _startButton.Click += StartButtonClick;
            _cancelButton.Click += CancelButtonClick;
            _openFolderButton.Click += delegate { OpenSaveDirectory(); };
            _keyOptionsButton.Click += KeyOptionsButtonClick;
            _convertFileButton.Click += ConvertFileButtonClick;
            _copyLogButton.Click += CopyLogButtonClick;
            _clearLogButton.Click += delegate { ClearLog(); };
            _captureButton.Click += CaptureButtonClick;
            Shown += MainFormShown;
            FormClosing += MainFormFormClosing;
        }

        private void ApplySettings()
        {
            _saveDirectoryTextBox.Text = _settings.SaveDirectory ?? string.Empty;
            _downloaderPathTextBox.Text = _settings.DownloaderPath ?? string.Empty;
            _ffmpegPathTextBox.Text = _settings.FfmpegPath ?? string.Empty;
            _muxToMp4CheckBox.Checked = _settings.MuxToMp4;
            _openFolderWhenDoneCheckBox.Checked = _settings.OpenFolderWhenDone;
        }

        private void ApplyCueBanners()
        {
            NativeMethods.SetCueBanner(_urlTextBox, "https://example.com/video/index.m3u8");
            NativeMethods.SetCueBanner(_fileNameTextBox, "根据链接自动填写");
        }

        private void UrlTextBoxTextChanged(object sender, EventArgs e)
        {
            bool isBlob = PlaylistInput.IsBlobUrl(_urlTextBox.Text);
            _urlTextBox.BackColor = isBlob ? Color.FromArgb(255, 247, 224) : ValidColor;
            if (isBlob)
            {
                _statusLabel.Text = "Blob 是浏览器临时地址，请从猫抓复制“原始m3u8”内容";
            }

            HandleUrlChangeForTaskState();

            if (!_fileNameWasEdited || string.IsNullOrWhiteSpace(_fileNameTextBox.Text) ||
                string.Equals(_fileNameTextBox.Text, _lastAutoName, StringComparison.Ordinal))
            {
                ApplyAutomaticName(false);
            }
        }

        private void FileNameTextBoxTextChanged(object sender, EventArgs e)
        {
            if (!_updatingAutoName)
            {
                _fileNameWasEdited = true;
            }
        }

        private void ApplyAutomaticName(bool force)
        {
            string automaticName = FileNameHelper.FromInput(_urlTextBox.Text);
            if (string.IsNullOrWhiteSpace(automaticName) && !force)
            {
                return;
            }

            _updatingAutoName = true;
            try
            {
                _fileNameTextBox.Text = automaticName;
                _lastAutoName = automaticName;
                _fileNameWasEdited = false;
            }
            finally
            {
                _updatingAutoName = false;
            }
        }

        private void PasteButtonClick(object sender, EventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string clipboardText = Clipboard.GetText().Trim();
                    if (PlaylistInput.LooksLikePlaylistContent(clipboardText))
                    {
                        ImportPlaylistContent(clipboardText);
                        return;
                    }

                    _urlTextBox.Text = clipboardText;
                    _urlTextBox.SelectionStart = _urlTextBox.TextLength;
                    _urlTextBox.Focus();
                }
            }
            catch (Exception exception)
            {
                ShowError("无法读取剪贴板：" + exception.Message);
            }
        }

        private void UrlTextBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (!e.Control || e.KeyCode != Keys.V)
            {
                return;
            }

            try
            {
                if (Clipboard.ContainsText())
                {
                    string clipboardText = Clipboard.GetText().Trim();
                    if (PlaylistInput.LooksLikePlaylistContent(clipboardText))
                    {
                        e.SuppressKeyPress = true;
                        e.Handled = true;
                        ImportPlaylistContent(clipboardText);
                    }
                }
            }
            catch (Exception exception)
            {
                ShowError("无法读取剪贴板：" + exception.Message);
            }
        }

        private void ImportPlaylistContent(string content)
        {
            ImportPlaylistContent(content, "剪贴板");
        }

        private void ImportPlaylistContent(string content, string sourceDescription)
        {
            try
            {
                if (PlaylistInput.ContainsRelativeMediaReferences(content))
                {
                    MessageBox.Show(
                        this,
                        "这份播放列表包含相对媒体、密钥或初始化地址，脱离原网页后无法确定 Base URL，因此没有导入。\r\n\r\n" +
                        "请在猫抓的“原始m3u8”中复制已经补全为完整 https:// 地址的内容。",
                        "播放列表缺少完整地址",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                DeleteImportedPlaylist();
                string importDirectory = GetImportedPlaylistDirectory();
                Directory.CreateDirectory(importDirectory);
                CleanupOldImportedPlaylists(importDirectory);

                string fileName = "pasted_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") +
                                  PlaylistInput.GetExtension(content);
                string path = Path.Combine(importDirectory, fileName);
                File.WriteAllText(path, content, new UTF8Encoding(false));
                _importedPlaylistPath = path;

                _fileNameWasEdited = false;
                _urlTextBox.Text = path;
                ApplyAutomaticName(false);
                _urlTextBox.SelectionStart = _urlTextBox.TextLength;
                _urlTextBox.Focus();
                string source = string.IsNullOrWhiteSpace(sourceDescription)
                    ? "外部内容"
                    : sourceDescription;
                _statusLabel.Text = "已从" + source + "导入播放列表";
                AppendLog("[GUI] 已导入" + source + "中的播放列表：" + path);
            }
            catch (Exception exception)
            {
                ShowError("无法导入播放列表内容：" + exception.Message);
            }
        }

        private static string GetImportedPlaylistDirectory()
        {
            return Path.Combine(Path.GetTempPath(), @"N_m3u8DL-RE-GUI\ImportedPlaylists");
        }

        private void DeleteImportedPlaylist()
        {
            DeleteImportedPlaylist(true);
        }

        private void DeleteImportedPlaylist(bool clearInput)
        {
            string path = _importedPlaylistPath;
            _importedPlaylistPath = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                DeleteSupersededImportedPlaylist();
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                if (clearInput && _urlTextBox != null &&
                    string.Equals(_urlTextBox.Text.Trim(), path, StringComparison.OrdinalIgnoreCase))
                {
                    _urlTextBox.Clear();
                }
            }
            catch
            {
            }
            DeleteSupersededImportedPlaylist();
        }

        private void DeleteSupersededImportedPlaylist()
        {
            string path = _supersededImportedPlaylistPath;
            _supersededImportedPlaylistPath = null;
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

        private static void CleanupOldImportedPlaylists(string directory)
        {
            try
            {
                DateTime cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (string path in Directory.GetFiles(directory, "pasted_*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < cutoff)
                        {
                            File.Delete(path);
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
        }

        private void BrowseDirectoryButtonClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择视频保存目录";
                dialog.ShowNewFolderButton = true;
                if (Directory.Exists(_saveDirectoryTextBox.Text))
                {
                    dialog.SelectedPath = _saveDirectoryTextBox.Text;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _saveDirectoryTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void BrowseDownloaderButtonClick(object sender, EventArgs e)
        {
            string path = BrowseExecutable(
                "选择 N_m3u8DL-RE.exe",
                "N_m3u8DL-RE|N_m3u8DL-RE.exe|可执行程序|*.exe",
                "N_m3u8DL-RE.exe");
            if (path != null)
            {
                _downloaderPathTextBox.Text = path;
                UpdateToolStatus();
                SaveCurrentSettings();
            }
        }

        private void BrowseFfmpegButtonClick(object sender, EventArgs e)
        {
            string path = BrowseExecutable(
                "选择 ffmpeg.exe",
                "FFmpeg|ffmpeg.exe|可执行程序|*.exe",
                "ffmpeg.exe");
            if (path != null)
            {
                _ffmpegPathTextBox.Text = path;
                UpdateToolStatus();
                SaveCurrentSettings();
            }
        }

        private string BrowseExecutable(string title, string filter, string expectedFileName)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = title;
                dialog.Filter = filter;
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return null;
                }

                string selectedPath = dialog.FileName;
                if (!string.Equals(
                    Path.GetFileName(selectedPath),
                    expectedFileName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    ShowError("请选择名为 " + expectedFileName + " 的程序文件。");
                    return null;
                }

                try
                {
                    using (FileStream stream = new FileStream(
                        selectedPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (stream.Length <= 0)
                        {
                            ShowError(expectedFileName + " 是空文件，无法使用。");
                            return null;
                        }
                    }
                }
                catch (Exception exception)
                {
                    ShowError("无法读取所选程序：" + exception.Message);
                    return null;
                }

                return selectedPath;
            }
        }

        private async void DetectToolsButtonClick(object sender, EventArgs e)
        {
            await PromptForMissingToolsAsync(true, true);
        }

        private void RestoreInterruptedDownload()
        {
            DownloadResumeCleanupResult cleanup = DownloadResumeStore.CleanupExpired();
            if (cleanup.ExpiredDeleted > 0 || cleanup.OrphanedDeleted > 0 ||
                cleanup.DiscardPendingDeleted > 0)
            {
                AppendLog(
                    "[GUI] 已自动清理过期或待删除缓存：" +
                    (cleanup.ExpiredDeleted + cleanup.OrphanedDeleted +
                     cleanup.DiscardPendingDeleted).ToString(CultureInfo.InvariantCulture) +
                    " 个。");
            }
            if (cleanup.DeleteFailed > 0)
            {
                AppendLog(
                    "[GUI] 有 " + cleanup.DeleteFailed.ToString(CultureInfo.InvariantCulture) +
                    " 个旧缓存仍被占用，将在下次启动时重试清理。");
            }

            List<DownloadResumeManifest> candidates =
                DownloadResumeStore.DiscoverRecoverableTasks();
            foreach (DownloadResumeManifest manifest in candidates)
            {
                DownloadResumeActivityLease lease;
                string leaseError;
                if (!DownloadResumeStore.TryAcquireActivityLease(
                    manifest.CacheDirectory,
                    out lease,
                    out leaseError))
                {
                    continue;
                }
                DeleteBrowserTransportPlaylist(manifest.CacheDirectory);

                string effectiveInput = manifest.Input;
                string inputError = null;
                if (!manifest.InputIsImportedPlaylist &&
                    !DownloadResumeStore.TryMaterializeInput(
                        manifest,
                        out effectiveInput,
                        out inputError))
                {
                    lease.Dispose();
                    AppendLog(
                        "[GUI] 无法恢复缓存中的任务输入：" + inputError +
                        " 缓存仍会按保留期限自动清理。");
                    continue;
                }
                if (manifest.InputIsImportedPlaylist && File.Exists(effectiveInput))
                {
                    try
                    {
                        File.Delete(effectiveInput);
                    }
                    catch
                    {
                    }
                }

                DownloadRequest request = CreateDownloadRequest(manifest, effectiveInput);
                _restoringResumeTask = true;
                _updatingAutoName = true;
                try
                {
                    _urlTextBox.Text = effectiveInput;
                    _saveDirectoryTextBox.Text = manifest.SaveDirectory;
                    _fileNameTextBox.Text = manifest.FileName;
                    _downloaderPathTextBox.Text = manifest.DownloaderPath;
                    _ffmpegPathTextBox.Text = manifest.FfmpegPath;
                    _muxToMp4CheckBox.Checked = manifest.MuxToMp4;
                    _manualHlsKey = manifest.HlsKey ?? string.Empty;
                    _manualHlsIv = manifest.HlsIv ?? string.Empty;
                    _capturedHeaders = manifest.CapturedHeaders == null
                        ? null
                        : manifest.CapturedHeaders.Clone();
                    _importedPlaylistPath = manifest.InputIsImportedPlaylist
                        ? effectiveInput
                        : null;
                    _downloadTemporaryDirectory = manifest.CacheDirectory;
                    _downloadResumeManifest = manifest;
                    _downloadResumeLease = lease;
                    _resumableRequest = request;
                    _resumeTaskTouchedThisSession = false;
                    _downloadTaskState = manifest.State == DownloadResumeState.Failed
                        ? DownloadTaskState.Failed
                        : DownloadTaskState.Paused;
                    _fileNameWasEdited = true;
                    _lastAutoName = manifest.FileName;
                }
                finally
                {
                    _updatingAutoName = false;
                    _restoringResumeTask = false;
                }

                UpdateKeyState();
                SetToolStatusPending();
                UpdateDownloadActionButtons();
                ShowRestoredDownloadStatus();
                AppendLog(
                    "[GUI] 已恢复上次未完成任务；继续时将复用分片缓存：" +
                    _downloadTemporaryDirectory);
                if (candidates.Count > 1)
                {
                    AppendLog(
                        "[GUI] 另有 " + (candidates.Count - 1).ToString(CultureInfo.InvariantCulture) +
                        " 个未完成任务正由其他实例使用或等待到期清理。");
                }
                return;
            }
        }

        private static DownloadRequest CreateDownloadRequest(
            DownloadResumeManifest manifest,
            string effectiveInput)
        {
            DownloadRequest request = new DownloadRequest();
            request.Input = effectiveInput;
            request.SaveDirectory = manifest.SaveDirectory;
            request.FileName = manifest.FileName;
            request.DownloaderPath = manifest.DownloaderPath;
            request.FfmpegPath = manifest.FfmpegPath;
            request.MuxToMp4 = manifest.MuxToMp4;
            request.HlsKey = manifest.HlsKey ?? string.Empty;
            request.HlsIv = manifest.HlsIv ?? string.Empty;
            request.InputIsImportedPlaylist = manifest.InputIsImportedPlaylist;
            request.ImportedPlaylistContent = manifest.ImportedPlaylistContent;
            request.CapturedHeaders = manifest.CapturedHeaders == null
                ? null
                : manifest.CapturedHeaders.Clone();
            return request;
        }

        private void ShowRestoredDownloadStatus()
        {
            if (_resumableRequest == null || string.IsNullOrWhiteSpace(_downloadTemporaryDirectory))
            {
                return;
            }

            long cacheBytes = GetDirectorySize(_downloadTemporaryDirectory);
            _statusLabel.Text = cacheBytes > 0
                ? "已恢复未完成任务，缓存 " + FormatByteCount(cacheBytes)
                : "已恢复未完成任务，可继续或清除缓存";
            UpdateDownloadActionButtons();
        }

        private static long GetDirectorySize(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                return 0;
            }

            long total = 0;
            Stack<string> pending = new Stack<string>();
            pending.Push(rootDirectory);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                try
                {
                    foreach (string file in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            total = checked(total + new FileInfo(file).Length);
                        }
                        catch
                        {
                        }
                    }

                    foreach (string child in Directory.GetDirectories(
                        directory,
                        "*",
                        SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            {
                                pending.Push(child);
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
            }

            return total;
        }

        private async void MainFormShown(object sender, EventArgs e)
        {
            Shown -= MainFormShown;
            if (!_enableStartupDependencyPrompt)
            {
                return;
            }

            RestoreInterruptedDownload();
            if (_startupDependencyPromptShown)
            {
                return;
            }

            _startupDependencyPromptShown = true;
            SetToolDetectionState(true);
            await Task.Yield();
            if (!IsDisposed && !Disposing && _activeProcess == null)
            {
                await PromptForMissingToolsAsync(true, true);
            }
            if (!IsDisposed && !Disposing &&
                !_toolDetectionInProgress &&
                !_dependencyInstallInProgress &&
                _activeProcess == null)
            {
                SetToolDetectionState(false);
            }

            if (!IsDisposed && !Disposing && _resumableRequest != null)
            {
                _resumableRequest.DownloaderPath = _downloaderPathTextBox.Text.Trim();
                _resumableRequest.FfmpegPath = _ffmpegPathTextBox.Text.Trim();
                ShowRestoredDownloadStatus();
            }
        }

        private async Task<bool> ResolveAndApplyToolPathsAsync()
        {
            if (IsDisposed || Disposing)
            {
                return false;
            }

            Task<string[]> detectionTask = _toolDetectionTask;
            bool ownsDetection = detectionTask == null;
            if (ownsDetection)
            {
                _toolDetectionInProgress = true;
                SetToolDetectionState(true);
                _statusLabel.Text = "正在检测外部工具...";
                string preferredDownloader = _downloaderPathTextBox.Text;
                string preferredFfmpeg = _ffmpegPathTextBox.Text;
                detectionTask = Task.Run(
                    delegate
                    {
                        string downloader = ToolLocator.FindDownloader(preferredDownloader);
                        string ffmpeg = ToolLocator.FindFfmpeg(preferredFfmpeg, downloader);
                        return new[] { downloader, ffmpeg };
                    });
                _toolDetectionTask = detectionTask;
            }

            try
            {
                string[] resolvedPaths = await detectionTask;

                if (IsDisposed || Disposing)
                {
                    return false;
                }

                _downloaderPathTextBox.Text = resolvedPaths[0];
                _ffmpegPathTextBox.Text = resolvedPaths[1];
                UpdateToolStatus();
                return true;
            }
            catch (Exception exception)
            {
                if (!IsDisposed && !Disposing)
                {
                    _statusLabel.Text = "检测外部工具失败";
                    AppendLog("[GUI] 检测外部工具失败：" + exception.Message);
                }
                return false;
            }
            finally
            {
                if (ownsDetection && ReferenceEquals(_toolDetectionTask, detectionTask))
                {
                    _toolDetectionTask = null;
                    _toolDetectionInProgress = false;
                    if (!IsDisposed && !Disposing)
                    {
                        SetToolDetectionState(false);
                    }
                }
            }
        }

        private async Task<bool> PromptForMissingToolsAsync(
            bool requireDownloader,
            bool requireFfmpeg)
        {
            await _dependencyWorkflowGate.WaitAsync();
            try
            {
                return await RunMissingToolsWorkflowAsync(
                    requireDownloader,
                    requireFfmpeg);
            }
            finally
            {
                _dependencyWorkflowGate.Release();
            }
        }

        private async Task<bool> RunMissingToolsWorkflowAsync(
            bool requireDownloader,
            bool requireFfmpeg)
        {
            if (_dependencyInstallInProgress || _activeProcess != null || IsDisposed || Disposing)
            {
                return false;
            }

            if (!await ResolveAndApplyToolPathsAsync())
            {
                return false;
            }

            bool missingDownloader = requireDownloader &&
                !ToolLocator.IsUsableExecutable(_downloaderPathTextBox.Text);
            bool missingFfmpeg = requireFfmpeg &&
                !ToolLocator.IsUsableExecutable(_ffmpegPathTextBox.Text);
            if (!missingDownloader && !missingFfmpeg)
            {
                _statusLabel.Text = requireDownloader
                    ? "已找到下载程序和 FFmpeg"
                    : "已找到 FFmpeg";
                SaveCurrentSettings();
                return true;
            }

            string missingText = GetMissingToolsText(missingDownloader, missingFfmpeg);
            string downloadSize = missingDownloader && missingFfmpeg
                ? "通常需要下载约 38 MB"
                : (missingDownloader ? "需要下载约 5 MB" : "通常需要下载约 32 MB");
            DialogResult choice = MessageBox.Show(
                this,
                "未检测到：" + missingText + "\r\n\r\n" +
                "是否使用当前网络直连 GitHub Release 自动下载？" + downloadSize + "。\r\n" +
                "GUI 不会使用 Windows 系统代理。\r\n" +
                "下载完成后会校验 SHA-256，再保存到当前用户的应用数据目录。\r\n\r\n" +
                "选择“是”：自动下载\r\n" +
                "选择“否”：浏览并指定电脑中已有的程序\r\n" +
                "选择“取消”：暂不处理",
                "缺少外部工具",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);

            if (choice == DialogResult.Yes)
            {
                await InstallMissingDependenciesAsync(missingDownloader, missingFfmpeg);
            }
            else if (choice == DialogResult.No)
            {
                await BrowseForMissingToolsAsync(
                    missingDownloader,
                    missingFfmpeg,
                    requireDownloader,
                    requireFfmpeg);
            }
            else
            {
                _statusLabel.Text = "缺少外部工具；可随时点击“自动检测”重试";
            }

            if (IsDisposed || Disposing)
            {
                return false;
            }
            return AreRequiredToolsReady(requireDownloader, requireFfmpeg);
        }

        private static string GetMissingToolsText(bool missingDownloader, bool missingFfmpeg)
        {
            if (missingDownloader && missingFfmpeg)
            {
                return "N_m3u8DL-RE.exe 和 ffmpeg.exe";
            }
            return missingDownloader ? "N_m3u8DL-RE.exe" : "ffmpeg.exe";
        }

        private async Task BrowseForMissingToolsAsync(
            bool missingDownloader,
            bool missingFfmpeg,
            bool requireDownloader,
            bool requireFfmpeg)
        {
            if (missingDownloader)
            {
                string downloader = BrowseExecutable(
                    "选择 N_m3u8DL-RE.exe",
                    "N_m3u8DL-RE|N_m3u8DL-RE.exe|可执行程序|*.exe",
                    "N_m3u8DL-RE.exe");
                if (downloader != null)
                {
                    _downloaderPathTextBox.Text = downloader;
                }
            }

            if (missingFfmpeg)
            {
                string ffmpeg = BrowseExecutable(
                    "选择 ffmpeg.exe",
                    "FFmpeg|ffmpeg.exe|可执行程序|*.exe",
                    "ffmpeg.exe");
                if (ffmpeg != null)
                {
                    _ffmpegPathTextBox.Text = ffmpeg;
                }
            }

            await ResolveAndApplyToolPathsAsync();
            if (IsDisposed || Disposing)
            {
                return;
            }
            SaveCurrentSettings();
            bool ready = AreRequiredToolsReady(requireDownloader, requireFfmpeg);
            _statusLabel.Text = ready
                ? "所需工具已就绪"
                : "所需工具仍未找到；可点击“自动检测”重试";
        }

        private bool AreRequiredToolsReady(bool requireDownloader, bool requireFfmpeg)
        {
            return (!requireDownloader ||
                    ToolLocator.IsUsableExecutable(_downloaderPathTextBox.Text)) &&
                (!requireFfmpeg ||
                    ToolLocator.IsUsableExecutable(_ffmpegPathTextBox.Text));
        }

        private async Task InstallMissingDependenciesAsync(
            bool installDownloader,
            bool installFfmpeg)
        {
            if (_dependencyInstallInProgress)
            {
                return;
            }

            _dependencyInstallInProgress = true;
            _closeRequestedDuringDependencyInstall = false;
            _lastDependencyInstallStage = string.Empty;
            _dependencyInstallCancellation = new CancellationTokenSource();
            SetRunningState(true);
            _progressBar.Style = ProgressBarStyle.Blocks;
            _progressBar.MarqueeAnimationSpeed = 0;
            _progressBar.Value = 0;
            _statusLabel.Text = "正在准备下载外部工具...";
            AppendLog(string.Empty);
            AppendLog("[GUI] 开始直连 GitHub Release 下载缺失工具；未使用 Windows 系统代理。");

            bool offerManualBrowse = false;
            bool installationCancelled = false;
            bool installationSucceeded = false;
            Exception installationFailure = null;
            DependencyInstallResult result = null;
            try
            {
                try
                {
                    Progress<DependencyInstallProgress> progress =
                        new Progress<DependencyInstallProgress>(UpdateDependencyInstallProgress);
                    _dependencyInstallTask = DependencyInstaller.InstallAsync(
                        installDownloader,
                        installFfmpeg,
                        progress,
                        _dependencyInstallCancellation.Token);
                    result = await _dependencyInstallTask;
                    installationSucceeded = true;
                }
                catch (OperationCanceledException)
                {
                    installationCancelled = true;
                }
                catch (Exception exception)
                {
                    installationFailure = exception;
                }

                if (_closeRequestedDuringDependencyInstall || IsDisposed || Disposing)
                {
                    if (installationFailure != null)
                    {
                        AppendLog("[GUI] 关闭窗口时工具下载已停止：" +
                            installationFailure.Message);
                    }
                }
                else
                {
                    ApplyInstalledDependencyPaths(result, installDownloader, installFfmpeg);
                    if (!IsDisposed && !Disposing)
                    {
                        SaveCurrentSettings();
                        if (installationSucceeded)
                        {
                            _progressBar.Style = ProgressBarStyle.Blocks;
                            _progressBar.Value = 100;
                            _statusLabel.Text = "工具下载完成";
                            AppendLog("[GUI] 外部工具下载并校验完成。");
                        }
                        else if (installationCancelled)
                        {
                            bool requestedToolReady =
                                (installDownloader && ToolLocator.IsUsableExecutable(
                                    _downloaderPathTextBox.Text)) ||
                                (installFfmpeg && ToolLocator.IsUsableExecutable(
                                    _ffmpegPathTextBox.Text));
                            _statusLabel.Text = requestedToolReady
                                ? "工具下载已取消；已完成的工具已保留"
                                : "工具下载已取消";
                            AppendLog("[GUI] 工具下载已取消。");
                        }
                        else if (installationFailure != null)
                        {
                            _statusLabel.Text = "工具自动下载失败";
                            AppendLog("[GUI] 工具自动下载失败：" + installationFailure.Message);
                            ShowError(
                                "自动下载工具失败：\r\n\r\n" + installationFailure.Message +
                                "\r\n\r\n请确认当前网络可以直接访问 GitHub；GUI 不会使用 Windows 系统代理。" +
                                "也可以手动浏览选择已有程序。");
                            offerManualBrowse = true;
                        }
                    }
                }
            }
            finally
            {
                if (_dependencyInstallCancellation != null)
                {
                    _dependencyInstallCancellation.Dispose();
                    _dependencyInstallCancellation = null;
                }
                _dependencyInstallTask = null;
                _dependencyInstallInProgress = false;
                SetRunningState(false);
                if (!installationSucceeded)
                {
                    ResetDownloadProgress();
                }
                UpdateToolStatus();
            }

            if (_closeRequestedDuringDependencyInstall)
            {
                _closeRequestedDuringDependencyInstall = false;
                Close();
                return;
            }

            if (offerManualBrowse)
            {
                bool missingDownloader = installDownloader &&
                    !ToolLocator.IsUsableExecutable(_downloaderPathTextBox.Text);
                bool missingFfmpeg = installFfmpeg &&
                    !ToolLocator.IsUsableExecutable(_ffmpegPathTextBox.Text);
                DialogResult browse = MessageBox.Show(
                    this,
                    "是否现在浏览并选择电脑中已有的工具？",
                    "改用手动选择",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);
                if (browse == DialogResult.Yes)
                {
                    await BrowseForMissingToolsAsync(
                        missingDownloader,
                        missingFfmpeg,
                        installDownloader,
                        installFfmpeg);
                }
            }
        }

        private void ApplyInstalledDependencyPaths(
            DependencyInstallResult result,
            bool installDownloader,
            bool installFfmpeg)
        {
            if (result != null && !string.IsNullOrWhiteSpace(result.DownloaderPath))
            {
                _downloaderPathTextBox.Text = result.DownloaderPath;
            }
            if (result != null && !string.IsNullOrWhiteSpace(result.FfmpegPath))
            {
                _ffmpegPathTextBox.Text = result.FfmpegPath;
            }

            string managedDirectory = ToolLocator.GetManagedToolsDirectory();
            string managedDownloader = Path.Combine(managedDirectory, "N_m3u8DL-RE.exe");
            string managedFfmpeg = Path.Combine(managedDirectory, "ffmpeg.exe");
            if (installDownloader && ToolLocator.IsUsableExecutable(managedDownloader))
            {
                _downloaderPathTextBox.Text = managedDownloader;
            }
            if (installFfmpeg && ToolLocator.IsUsableExecutable(managedFfmpeg))
            {
                _ffmpegPathTextBox.Text = managedFfmpeg;
            }
        }

        private void UpdateDependencyInstallProgress(DependencyInstallProgress progress)
        {
            if (!_dependencyInstallInProgress ||
                progress == null ||
                IsDisposed ||
                Disposing ||
                (_dependencyInstallCancellation != null &&
                 _dependencyInstallCancellation.IsCancellationRequested))
            {
                return;
            }

            string stageKey = (progress.ToolName ?? string.Empty) + "|" +
                (progress.Stage ?? string.Empty);
            if (!string.Equals(
                stageKey,
                _lastDependencyInstallStage,
                StringComparison.Ordinal))
            {
                _lastDependencyInstallStage = stageKey;
                AppendLog("[GUI] " + progress.ToolName + "：" + progress.Stage);
            }

            if (progress.TotalBytes > 0)
            {
                double ratio = Math.Max(
                    0,
                    Math.Min(1, progress.BytesReceived / (double)progress.TotalBytes));
                int percent = Math.Min(99, (int)Math.Floor(ratio * 100));
                _progressBar.MarqueeAnimationSpeed = 0;
                _progressBar.Style = ProgressBarStyle.Blocks;
                _progressBar.Value = percent;
                _statusLabel.Text =
                    progress.Stage + " " + progress.ToolName + " " + percent + "%  " +
                    FormatByteCount(progress.BytesReceived) + "/" +
                    FormatByteCount(progress.TotalBytes);
            }
            else
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
                _progressBar.MarqueeAnimationSpeed = 28;
                _statusLabel.Text = progress.Stage + " " + progress.ToolName + "...";
            }
        }

        private static string FormatByteCount(long bytes)
        {
            if (bytes >= 1024L * 1024L)
            {
                return (bytes / (1024d * 1024d)).ToString("0.0") + " MB";
            }
            if (bytes >= 1024L)
            {
                return (bytes / 1024d).ToString("0.0") + " KB";
            }
            return Math.Max(0, bytes) + " B";
        }

        private void UpdateToolStatus()
        {
            if (_downloaderPathTextBox == null || _ffmpegPathTextBox == null || _toolStatusLabel == null)
            {
                return;
            }

            bool downloaderReady = ToolLocator.IsUsableExecutable(_downloaderPathTextBox.Text);
            bool ffmpegReady = ToolLocator.IsUsableExecutable(_ffmpegPathTextBox.Text);
            _downloaderPathTextBox.BackColor = downloaderReady ? ValidColor : InvalidColor;
            _ffmpegPathTextBox.BackColor = ffmpegReady ? ValidColor : InvalidColor;

            if (downloaderReady && ffmpegReady)
            {
                _toolStatusLabel.Text = "工具已就绪";
                _toolStatusLabel.ForeColor = AccentColor;
            }
            else
            {
                _toolStatusLabel.Text = !downloaderReady && !ffmpegReady
                    ? "未找到 N_m3u8DL-RE.exe 和 ffmpeg.exe"
                    : (!downloaderReady
                        ? "未找到 N_m3u8DL-RE.exe"
                        : "未找到 ffmpeg.exe");
                _toolStatusLabel.ForeColor = DangerColor;
            }
        }

        private void SetToolStatusPending()
        {
            if (_downloaderPathTextBox == null || _ffmpegPathTextBox == null || _toolStatusLabel == null)
            {
                return;
            }

            _downloaderPathTextBox.BackColor = ValidColor;
            _ffmpegPathTextBox.BackColor = ValidColor;
            _toolStatusLabel.Text = _toolDetectionInProgress
                ? "正在检测工具"
                : "等待检测工具";
            _toolStatusLabel.ForeColor = MutedTextColor;
        }

        private async void StartButtonClick(object sender, EventArgs e)
        {
            if (_activeProcess != null)
            {
                if (_activeOperation == OperationKind.Download && !_isCancelling)
                {
                    StopActiveProcess(true);
                }
                return;
            }

            if (_downloadTaskState == DownloadTaskState.Completed)
            {
                ResetCompletedDownloadState();
                return;
            }

            string restoreInputError;
            if (!EnsureRestoredInputMaterialized(out restoreInputError))
            {
                ShowError("无法恢复加密保存的播放列表：\r\n\r\n" + restoreInputError);
                return;
            }

            if (!await PromptForMissingToolsAsync(true, true))
            {
                return;
            }

            DownloadRequest request;
            if (!TryCreateRequest(out request))
            {
                return;
            }

            bool resumeExistingTask = CanResumeDownload(request);
            string[] conflicts = resumeExistingTask
                ? new string[0]
                : FindExistingOutputs(request.SaveDirectory, request.FileName);
            if (conflicts.Length > 0)
            {
                DialogResult overwriteResult = MessageBox.Show(
                    this,
                    "保存目录中已经存在同名媒体文件。继续运行可能覆盖文件或生成新文件名。\r\n\r\n是否继续？",
                    "文件已存在",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (overwriteResult != DialogResult.Yes)
                {
                    return;
                }
            }

            if ((_downloadTaskState == DownloadTaskState.Paused ||
                 _downloadTaskState == DownloadTaskState.Failed) &&
                !resumeExistingTask)
            {
                DialogResult replaceTask = MessageBox.Show(
                    this,
                    "当前输入或下载参数与已保留任务不同。开始新任务会永久删除旧任务的分片缓存。\r\n\r\n是否放弃旧任务并继续？",
                    "开始新任务",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (replaceTask != DialogResult.Yes)
                {
                    return;
                }

                if (!string.Equals(
                        request.Input,
                        _resumableRequest.Input,
                        StringComparison.Ordinal) &&
                    CapturedHeadersEqual(
                        request.CapturedHeaders,
                        _resumableRequest.CapturedHeaders))
                {
                    request.CapturedHeaders = null;
                }
                string preserveInputError;
                if (!TryPreserveImportedInputBeforeDiscard(
                    request,
                    out preserveInputError))
                {
                    ShowError(
                        "无法在清理旧缓存前保留播放列表，因此没有开始新任务：\r\n\r\n" +
                        preserveInputError);
                    return;
                }
                if (!DiscardResumableDownload())
                {
                    ShowError("旧任务缓存仍被占用，无法开始新任务。请关闭可能占用缓存文件的程序后重试。");
                    return;
                }
                AppendLog("[GUI] 下载参数已变化，已清理旧任务缓存并开始新任务。");
            }

            SaveCurrentSettings();
            StartDownload(request, resumeExistingTask);
        }

        private void KeyOptionsButtonClick(object sender, EventArgs e)
        {
            using (HlsKeyDialog dialog = new HlsKeyDialog(_manualHlsKey, _manualHlsIv))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                _manualHlsKey = dialog.KeyValue;
                _manualHlsIv = dialog.IvValue;
                UpdateKeyState();
            }
        }

        private void UpdateKeyState()
        {
            bool hasKey = !string.IsNullOrWhiteSpace(_manualHlsKey);
            _keyOptionsButton.Text = hasKey ? "密钥已设置" : "密钥...";
            SetSecondaryButtonEnabled(_keyOptionsButton, _keyOptionsButton.Enabled);
            UpdateDownloadActionButtons();
            _toolTip.SetToolTip(
                _keyOptionsButton,
                hasKey ? "已设置手动 HLS 密钥；不会保存到配置文件" : "设置可选的 HLS AES-128 密钥和 IV");
        }

        private void HandleUrlChangeForTaskState()
        {
            if (_restoringResumeTask || _activeProcess != null || _resumableRequest == null)
            {
                return;
            }

            string currentInput = _urlTextBox.Text.Trim();
            if (string.Equals(currentInput, _resumableRequest.Input, StringComparison.Ordinal))
            {
                return;
            }

            if (_downloadTaskState == DownloadTaskState.Completed)
            {
                ResetCompletedDownloadState();
            }
            else if (_downloadTaskState == DownloadTaskState.Paused ||
                     _downloadTaskState == DownloadTaskState.Failed)
            {
                _statusLabel.Text = "输入已变化；开始新任务时会先确认是否清除旧缓存";
                return;
            }

            if (!string.IsNullOrWhiteSpace(_importedPlaylistPath) &&
                !string.Equals(currentInput, _importedPlaylistPath, StringComparison.OrdinalIgnoreCase))
            {
                DeleteImportedPlaylist();
            }
        }

        private bool CanResumeDownload(DownloadRequest request)
        {
            return (_downloadTaskState == DownloadTaskState.Paused ||
                    _downloadTaskState == DownloadTaskState.Failed) &&
                   _resumableRequest != null &&
                   !string.IsNullOrWhiteSpace(_downloadTemporaryDirectory) &&
                   Directory.Exists(_downloadTemporaryDirectory) &&
                   DownloadRequestsMatch(_resumableRequest, request);
        }

        private static bool DownloadRequestsMatch(DownloadRequest left, DownloadRequest right)
        {
            return left != null && right != null &&
                   string.Equals(left.Input, right.Input, StringComparison.Ordinal) &&
                   string.Equals(left.SaveDirectory, right.SaveDirectory, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase) &&
                   left.MuxToMp4 == right.MuxToMp4 &&
                   string.Equals(left.HlsKey, right.HlsKey, StringComparison.Ordinal) &&
                   string.Equals(left.HlsIv, right.HlsIv, StringComparison.Ordinal);
        }

        private static bool CapturedHeadersEqual(
            MediaRequestHeaders left,
            MediaRequestHeaders right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            if (left == null || right == null ||
                !string.Equals(left.Referer, right.Referer, StringComparison.Ordinal) ||
                !string.Equals(left.Cookie, right.Cookie, StringComparison.Ordinal) ||
                !string.Equals(left.UserAgent, right.UserAgent, StringComparison.Ordinal) ||
                !string.Equals(left.Origin, right.Origin, StringComparison.Ordinal) ||
                !string.Equals(left.Authorization, right.Authorization, StringComparison.Ordinal) ||
                !string.Equals(left.SourceUrl, right.SourceUrl, StringComparison.Ordinal) ||
                left.AdditionalHeaders.Count != right.AdditionalHeaders.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, string> header in left.AdditionalHeaders)
            {
                string rightValue;
                if (!right.AdditionalHeaders.TryGetValue(header.Key, out rightValue) ||
                    !string.Equals(header.Value, rightValue, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private void ClearCapturedHeadersForRequest(DownloadRequest request)
        {
            if (request != null && CapturedHeadersEqual(_capturedHeaders, request.CapturedHeaders))
            {
                _capturedHeaders = null;
            }
        }

        private bool TryPersistDownloadResume(
            DownloadRequest request,
            DownloadResumeState state,
            out string errorMessage)
        {
            errorMessage = null;
            if (request == null || string.IsNullOrWhiteSpace(_downloadTemporaryDirectory) ||
                !Directory.Exists(_downloadTemporaryDirectory))
            {
                errorMessage = "下载缓存目录已经不存在。";
                return false;
            }

            bool inputIsImported = request.InputIsImportedPlaylist;
            string importedContent = request.ImportedPlaylistContent;
            if (inputIsImported)
            {
                try
                {
                    if (File.Exists(request.Input))
                    {
                        importedContent = File.ReadAllText(request.Input, Encoding.UTF8);
                    }
                }
                catch (Exception exception)
                {
                    errorMessage = "无法保存导入的播放列表：" + exception.Message;
                    return false;
                }

                if (string.IsNullOrWhiteSpace(importedContent) &&
                    _downloadResumeManifest != null &&
                    _downloadResumeManifest.InputIsImportedPlaylist)
                {
                    importedContent = _downloadResumeManifest.ImportedPlaylistContent;
                }
                if (string.IsNullOrWhiteSpace(importedContent))
                {
                    errorMessage = "导入的播放列表正文已经不存在。";
                    return false;
                }
                request.ImportedPlaylistContent = importedContent;
            }

            DownloadResumeManifest manifest = _downloadResumeManifest ??
                new DownloadResumeManifest();
            manifest.CacheDirectory = _downloadTemporaryDirectory;
            manifest.State = state;
            manifest.SaveDirectory = request.SaveDirectory;
            manifest.FileName = request.FileName;
            manifest.DownloaderPath = request.DownloaderPath;
            manifest.FfmpegPath = request.FfmpegPath;
            manifest.MuxToMp4 = request.MuxToMp4;
            manifest.Input = request.Input;
            manifest.InputIsImportedPlaylist = inputIsImported;
            manifest.ImportedPlaylistContent = importedContent;
            manifest.HlsKey = request.HlsKey;
            manifest.HlsIv = request.HlsIv;
            manifest.CapturedHeaders = request.CapturedHeaders == null
                ? null
                : request.CapturedHeaders.Clone();

            if (!DownloadResumeStore.TrySave(manifest, out errorMessage))
            {
                return false;
            }

            _downloadResumeManifest = manifest;
            return true;
        }

        private bool EnsureRestoredInputMaterialized(out string errorMessage)
        {
            errorMessage = null;
            if (_downloadResumeManifest == null ||
                !_downloadResumeManifest.InputIsImportedPlaylist ||
                _resumableRequest == null ||
                !string.Equals(
                    _urlTextBox.Text.Trim(),
                    _resumableRequest.Input,
                    StringComparison.Ordinal) ||
                File.Exists(_resumableRequest.Input))
            {
                return true;
            }

            string input;
            if (!DownloadResumeStore.TryMaterializeInput(
                _downloadResumeManifest,
                out input,
                out errorMessage))
            {
                return false;
            }

            _resumableRequest.Input = input;
            _importedPlaylistPath = input;
            SetInputWithoutTaskTransition(input);
            return true;
        }

        private bool TryPreserveImportedInputBeforeDiscard(
            DownloadRequest request,
            out string errorMessage)
        {
            errorMessage = null;
            if (request == null || _resumableRequest == null ||
                !_resumableRequest.InputIsImportedPlaylist ||
                !string.Equals(
                    request.Input,
                    _resumableRequest.Input,
                    StringComparison.Ordinal))
            {
                return true;
            }

            string content = _resumableRequest.ImportedPlaylistContent;
            if (string.IsNullOrWhiteSpace(content) && _downloadResumeManifest != null)
            {
                content = _downloadResumeManifest.ImportedPlaylistContent;
            }
            if (string.IsNullOrWhiteSpace(content))
            {
                try
                {
                    content = File.ReadAllText(request.Input, Encoding.UTF8);
                }
                catch (Exception exception)
                {
                    errorMessage = exception.Message;
                    return false;
                }
            }

            try
            {
                string importDirectory = GetImportedPlaylistDirectory();
                Directory.CreateDirectory(importDirectory);
                string path = Path.Combine(
                    importDirectory,
                    "pasted_recovered_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") +
                    PlaylistInput.GetExtension(content));
                File.WriteAllText(path, content, new UTF8Encoding(false));
                request.Input = path;
                request.InputIsImportedPlaylist = true;
                request.ImportedPlaylistContent = content;
                _importedPlaylistPath = path;
                SetInputWithoutTaskTransition(path);
                return true;
            }
            catch (Exception exception)
            {
                errorMessage = exception.Message;
                return false;
            }
        }

        private bool TryCanonicalizeImportedInput(
            DownloadRequest request,
            out string errorMessage)
        {
            errorMessage = null;
            if (request == null || !request.InputIsImportedPlaylist)
            {
                return true;
            }

            string content = request.ImportedPlaylistContent;
            if (File.Exists(request.Input))
            {
                try
                {
                    content = File.ReadAllText(request.Input, Encoding.UTF8);
                }
                catch (Exception exception)
                {
                    errorMessage = exception.Message;
                    return false;
                }
            }
            if (string.IsNullOrWhiteSpace(content))
            {
                errorMessage = "导入的播放列表正文已经不存在。";
                return false;
            }
            request.ImportedPlaylistContent = content;

            DownloadResumeManifest snapshot = new DownloadResumeManifest();
            snapshot.CacheDirectory = _downloadTemporaryDirectory;
            snapshot.InputIsImportedPlaylist = true;
            snapshot.ImportedPlaylistContent = content;
            string taskInput;
            if (!DownloadResumeStore.TryMaterializeInput(
                snapshot,
                out taskInput,
                out errorMessage))
            {
                return false;
            }

            string oldInput = request.Input;
            request.Input = taskInput;
            _importedPlaylistPath = taskInput;
            if (_resumableRequest != null &&
                string.Equals(
                    _resumableRequest.Input,
                    oldInput,
                    StringComparison.OrdinalIgnoreCase))
            {
                _resumableRequest.Input = taskInput;
            }
            SetInputWithoutTaskTransition(taskInput);
            if (!string.Equals(oldInput, taskInput, StringComparison.OrdinalIgnoreCase))
            {
                _supersededImportedPlaylistPath = oldInput;
            }
            return true;
        }

        private void SetInputWithoutTaskTransition(string input)
        {
            bool oldRestoring = _restoringResumeTask;
            bool oldFileNameWasEdited = _fileNameWasEdited;
            _restoringResumeTask = true;
            _fileNameWasEdited = true;
            try
            {
                _urlTextBox.Text = input ?? string.Empty;
            }
            finally
            {
                _fileNameWasEdited = oldFileNameWasEdited;
                _restoringResumeTask = oldRestoring;
            }
        }

        private bool EnsureDownloadResumeLease(out string errorMessage)
        {
            errorMessage = null;
            if (_downloadResumeLease != null)
            {
                return true;
            }

            return DownloadResumeStore.TryAcquireActivityLease(
                _downloadTemporaryDirectory,
                out _downloadResumeLease,
                out errorMessage);
        }

        private bool PersistDownloadResumeOrLog(DownloadResumeState state)
        {
            string errorMessage;
            if (TryPersistDownloadResume(_resumableRequest, state, out errorMessage))
            {
                return true;
            }

            AppendLog("[GUI] 无法更新重启恢复信息：" + errorMessage);
            return false;
        }

        private void ReleaseDownloadResumeLease()
        {
            DownloadResumeActivityLease lease = _downloadResumeLease;
            _downloadResumeLease = null;
            if (lease != null)
            {
                lease.Dispose();
            }
        }

        private bool DiscardResumableDownload()
        {
            ClearCapturedHeadersForRequest(_resumableRequest);
            _resumeTaskTouchedThisSession = false;
            if (!DeleteDownloadTemporaryDirectory())
            {
                _resumableRequest = null;
                _downloadResumeManifest = null;
                _downloadTaskState = DownloadTaskState.Idle;
                UpdateDownloadActionButtons();
                return false;
            }

            DeleteTemporarySecrets();
            _resumableRequest = null;
            _downloadResumeManifest = null;
            _downloadTaskState = DownloadTaskState.Idle;
            _lastOutputPath = null;
            ResetDownloadProgress();
            UpdateDownloadActionButtons();
            return true;
        }

        private void ResetCompletedDownloadState()
        {
            ClearCapturedHeadersForRequest(_resumableRequest);
            _resumeTaskTouchedThisSession = false;
            _resumableRequest = null;
            _downloadResumeManifest = null;
            _downloadTaskState = DownloadTaskState.Idle;
            _lastOutputPath = null;
            ResetDownloadProgress();
            _statusLabel.Text = "准备下载";
            UpdateDownloadActionButtons();
        }

        private async void ConvertFileButtonClick(object sender, EventArgs e)
        {
            if (!await PromptForMissingToolsAsync(false, true))
            {
                return;
            }

            string sourcePath;
            using (OpenFileDialog sourceDialog = new OpenFileDialog())
            {
                sourceDialog.Title = "选择要转换的媒体文件";
                sourceDialog.Filter = "媒体文件|*.ts;*.m2ts;*.mkv;*.mov;*.webm;*.flv;*.mp4;*.m4v|所有文件|*.*";
                sourceDialog.CheckFileExists = true;
                sourceDialog.Multiselect = false;
                sourceDialog.RestoreDirectory = true;
                if (Directory.Exists(_saveDirectoryTextBox.Text))
                {
                    sourceDialog.InitialDirectory = _saveDirectoryTextBox.Text;
                }

                if (sourceDialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                sourcePath = sourceDialog.FileName;
            }

            string outputPath;
            using (SaveFileDialog outputDialog = new SaveFileDialog())
            {
                outputDialog.Title = "保存转换后的 MP4";
                outputDialog.Filter = "MP4 视频|*.mp4";
                outputDialog.AddExtension = true;
                outputDialog.DefaultExt = "mp4";
                outputDialog.OverwritePrompt = true;
                outputDialog.RestoreDirectory = true;
                outputDialog.InitialDirectory = Path.GetDirectoryName(sourcePath);
                string sourceName = Path.GetFileNameWithoutExtension(sourcePath);
                outputDialog.FileName = string.Equals(
                    Path.GetExtension(sourcePath),
                    ".mp4",
                    StringComparison.OrdinalIgnoreCase)
                    ? sourceName + "_fixed.mp4"
                    : sourceName + ".mp4";

                if (outputDialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                outputPath = outputDialog.FileName;
            }

            if (string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(outputPath),
                StringComparison.OrdinalIgnoreCase))
            {
                ShowError("输出文件不能与源文件相同。");
                return;
            }

            string ffmpegPath = ToolLocator.FindFfmpeg(
                _ffmpegPathTextBox.Text,
                _downloaderPathTextBox.Text);
            if (!ToolLocator.IsUsableExecutable(ffmpegPath))
            {
                ShowValidationError("找不到 ffmpeg.exe，请重新选择 FFmpeg 程序。", _ffmpegPathTextBox);
                return;
            }

            _ffmpegPathTextBox.Text = ffmpegPath;
            StartFileConversion(sourcePath, outputPath, ffmpegPath);
        }

        private void StartFileConversion(string sourcePath, string outputPath, string ffmpegPath)
        {
            string outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                ShowError("无法确定输出目录。");
                return;
            }

            try
            {
                Directory.CreateDirectory(outputDirectory);
            }
            catch (Exception exception)
            {
                ShowError("无法创建转换输出目录：\r\n\r\n" + exception.Message);
                return;
            }

            DeleteConversionTemporaryOutput();
            if (!string.IsNullOrWhiteSpace(_conversionTemporaryOutputPath))
            {
                ShowError("上一次转换的临时文件仍被占用，请稍后重试或关闭占用该文件的程序。");
                return;
            }

            _conversionFinalOutputPath = outputPath;
            _conversionTemporaryOutputPath = Path.Combine(
                outputDirectory,
                "." + Path.GetFileNameWithoutExtension(outputPath) + "." +
                Guid.NewGuid().ToString("N") + ".partial.mp4");

            List<string> arguments = new List<string>();
            arguments.Add("-hide_banner");
            arguments.Add("-y");
            arguments.Add("-fflags");
            arguments.Add("+genpts");
            arguments.Add("-i");
            arguments.Add(sourcePath);
            arguments.Add("-map");
            arguments.Add("0:v:0?");
            arguments.Add("-map");
            arguments.Add("0:a?");
            arguments.Add("-map_metadata");
            arguments.Add("0");
            arguments.Add("-c");
            arguments.Add("copy");
            arguments.Add("-avoid_negative_ts");
            arguments.Add("make_zero");
            arguments.Add("-movflags");
            arguments.Add("+faststart");
            arguments.Add(_conversionTemporaryOutputPath);

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = ffmpegPath;
            startInfo.Arguments = CommandLine.JoinArguments(arguments);
            startInfo.WorkingDirectory = outputDirectory;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            ExternalToolOutputEncodings.ApplyFfmpeg(startInfo);

            Process process = new Process();
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = false;
            process.OutputDataReceived += ProcessOutputDataReceived;
            process.ErrorDataReceived += ProcessErrorDataReceived;
            process.Exited += ProcessExited;

            _activeOperation = OperationKind.ConvertFile;
            _activeOperationDirectory = outputDirectory;
            _isCancelling = false;
            _lastOutputPath = null;
            _expectedOutputBaseName = Path.GetFileNameWithoutExtension(outputPath);
            _downloadStartedUtc = DateTime.UtcNow;
            _filesBeforeDownload = CaptureFileState(outputDirectory);
            _activeProcess = process;
            SetRunningState(true);

            AppendLog(string.Empty);
            AppendLog("[GUI] 开始无损转换：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AppendLog("[GUI] 源文件：" + sourcePath);
            AppendLog("[GUI] 输出文件：" + outputPath);
            _statusLabel.Text = "正在启动 FFmpeg...";

            bool processStarted = false;
            bool exitMonitoringEnabled = false;
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("FFmpeg 没有成功启动。");
                }
                processStarted = true;

                ProcessJob job = ProcessJob.TryCreateKillOnClose();
                if (job == null)
                {
                    throw new InvalidOperationException(
                        "无法创建转换进程保护 Job，已停止 FFmpeg。");
                }
                if (!job.AddProcess(process))
                {
                    job.Dispose();
                    throw new InvalidOperationException(
                        "无法把 FFmpeg 加入转换进程保护 Job，已停止转换。");
                }
                _processJob = job;

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.EnableRaisingEvents = true;
                exitMonitoringEnabled = true;
                _statusLabel.Text = "正在无损转换为 MP4...";
            }
            catch (Exception exception)
            {
                AppendLog("[GUI] FFmpeg 启动失败：" + exception.Message);
                bool processStopped = !processStarted || StopProcessForCleanup(process, 5000);
                if (!processStopped)
                {
                    if (!exitMonitoringEnabled)
                    {
                        try
                        {
                            process.EnableRaisingEvents = true;
                            exitMonitoringEnabled = true;
                        }
                        catch
                        {
                        }
                    }

                    _isCancelling = true;
                    _statusLabel.Text = "FFmpeg 启动异常，正在等待进程退出";
                    AppendLog("[GUI] 无法确认 FFmpeg 已退出；保留临时文件并维持取消状态。");
                    ShowError(
                        "FFmpeg 启动后发生异常，并且暂时无法确认进程已经退出。\r\n\r\n" +
                        "请再次点击“取消”，或关闭窗口以重试终止进程。");
                    return;
                }

                _activeProcess = null;
                DisposeProcessJob();

                process.Dispose();
                SetRunningState(false);
                _statusLabel.Text = "转换启动失败";
                DeleteConversionTemporaryOutput();
                ShowError("无法启动 FFmpeg：\r\n\r\n" + exception.Message);
            }
        }

        private bool CommitConversionOutput(out string outputPath, out string errorMessage)
        {
            outputPath = null;
            errorMessage = null;
            string temporaryPath = _conversionTemporaryOutputPath;
            string finalPath = _conversionFinalOutputPath;

            if (!ConversionFileStore.TryCommit(
                temporaryPath,
                finalPath,
                out errorMessage))
            {
                return false;
            }

            _conversionTemporaryOutputPath = null;
            _conversionFinalOutputPath = null;
            outputPath = finalPath;
            return true;
        }

        private bool DeleteConversionTemporaryOutput()
        {
            string path = _conversionTemporaryOutputPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                _conversionFinalOutputPath = null;
                return true;
            }

            if (ConversionFileStore.Delete(path))
            {
                _conversionTemporaryOutputPath = null;
                _conversionFinalOutputPath = null;
                return true;
            }

            return false;
        }

        private bool TryCreateRequest(out DownloadRequest request)
        {
            request = null;
            string input = _urlTextBox.Text.Trim();
            if (input.Length == 0)
            {
                ShowValidationError("请输入视频链接。", _urlTextBox);
                return false;
            }

            if (PlaylistInput.IsBlobUrl(input))
            {
                MessageBox.Show(
                    this,
                    "这个地址是猫抓扩展生成的浏览器临时 Blob，外部下载程序无法直接读取。\r\n\r\n" +
                    "请在猫抓的 M3U8解析器 页面中：\r\n" +
                    "1. 展开“查看所有切片和下载进度”\r\n" +
                    "2. 点击“原始m3u8”\r\n" +
                    "3. 在文本框中按 Ctrl+A、Ctrl+C\r\n" +
                    "4. 回到本程序点击“粘贴”\r\n\r\n" +
                    "本程序会自动把 #EXTM3U 文本导入为本地播放列表。",
                    "无法直接使用 Blob 地址",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                _urlTextBox.Focus();
                return false;
            }

            Uri uri;
            bool validUri = Uri.TryCreate(input, UriKind.Absolute, out uri);
            bool existingFile = File.Exists(input);
            if (!validUri && !existingFile)
            {
                ShowValidationError("视频链接格式不正确。", _urlTextBox);
                return false;
            }

            string saveDirectory = _saveDirectoryTextBox.Text.Trim();
            if (saveDirectory.Length == 0)
            {
                ShowValidationError("请选择保存目录。", _saveDirectoryTextBox);
                return false;
            }

            try
            {
                saveDirectory = Path.GetFullPath(saveDirectory);
                Directory.CreateDirectory(saveDirectory);
                VerifyDirectoryIsWritable(saveDirectory);
                _saveDirectoryTextBox.Text = saveDirectory;
            }
            catch (Exception exception)
            {
                ShowValidationError("保存目录不可用：" + exception.Message, _saveDirectoryTextBox);
                return false;
            }

            string fileName = FileNameHelper.CleanFileName(_fileNameTextBox.Text);
            if (fileName.Length == 0)
            {
                ShowValidationError("请输入文件名称。", _fileNameTextBox);
                return false;
            }

            _updatingAutoName = true;
            try
            {
                _fileNameTextBox.Text = fileName;
            }
            finally
            {
                _updatingAutoName = false;
            }

            string downloaderPath = ResolveRequiredTool(
                _downloaderPathTextBox,
                ToolLocator.FindDownloader(_downloaderPathTextBox.Text),
                "找不到 N_m3u8DL-RE.exe，请将它放在本程序同目录或 tools 子目录，或者手动选择。\r\n\r\n下载来源：https://github.com/nilaoda/N_m3u8DL-RE/releases");
            if (downloaderPath == null)
            {
                return false;
            }

            string ffmpegPath = ResolveRequiredTool(
                _ffmpegPathTextBox,
                ToolLocator.FindFfmpeg(_ffmpegPathTextBox.Text, downloaderPath),
                "找不到 ffmpeg.exe，请重新选择 FFmpeg 程序。");
            if (ffmpegPath == null)
            {
                return false;
            }

            request = new DownloadRequest();
            request.Input = input;
            request.SaveDirectory = saveDirectory;
            request.FileName = fileName;
            request.DownloaderPath = downloaderPath;
            request.FfmpegPath = ffmpegPath;
            request.MuxToMp4 = _muxToMp4CheckBox.Checked;
            request.HlsKey = _manualHlsKey;
            request.HlsIv = _manualHlsIv;
            request.InputIsImportedPlaylist =
                !string.IsNullOrWhiteSpace(_importedPlaylistPath) &&
                string.Equals(input, _importedPlaylistPath, StringComparison.OrdinalIgnoreCase);
            if (request.InputIsImportedPlaylist)
            {
                try
                {
                    request.ImportedPlaylistContent = File.ReadAllText(input, Encoding.UTF8);
                }
                catch (Exception exception)
                {
                    ShowValidationError(
                        "无法读取导入的播放列表：" + exception.Message,
                        _urlTextBox);
                    return false;
                }
            }
            request.CapturedHeaders = _capturedHeaders == null
                ? null
                : _capturedHeaders.Clone();
            Uri remoteInput;
            if (request.CapturedHeaders != null &&
                Uri.TryCreate(input, UriKind.Absolute, out remoteInput) &&
                (remoteInput.Scheme == Uri.UriSchemeHttp ||
                 remoteInput.Scheme == Uri.UriSchemeHttps) &&
                (RequiresCapturedHeaderIsolation(request.CapturedHeaders) ||
                 (!string.IsNullOrWhiteSpace(request.CapturedHeaders.SourceUrl) &&
                  !MediaRequestHeaders.AreSameOrigin(
                      input,
                      request.CapturedHeaders.SourceUrl))))
            {
                MediaRequestHeaders userAgentOnly = new MediaRequestHeaders();
                userAgentOnly.UserAgent = request.CapturedHeaders.UserAgent;
                request.CapturedHeaders = userAgentOnly.HasAny ? userAgentOnly : null;
                AppendLog(
                    "[GUI] 远程播放列表不会全局转发 Cookie、Authorization 或自定义凭据，" +
                    "以免泄露给异源分片；需要登录态时请使用“从网页捕获”导入完整播放列表正文。");
            }
            return true;
        }

        private string ResolveRequiredTool(TextBox textBox, string resolvedPath, string errorMessage)
        {
            if (!ToolLocator.IsUsableExecutable(resolvedPath))
            {
                ShowValidationError(errorMessage, textBox);
                return null;
            }

            textBox.Text = resolvedPath;
            return resolvedPath;
        }

        private void StartDownload(DownloadRequest request, bool resumeExistingTask)
        {
            _resumeTaskTouchedThisSession = true;
            _lastDownloaderError = null;
            _lastDownloaderWarning = null;
            StopActiveMediaProxy();

            string keyArgument;
            string ivArgument;
            if (!PrepareTemporarySecrets(request.HlsKey, request.HlsIv, out keyArgument, out ivArgument))
            {
                return;
            }

            if (!resumeExistingTask && !DeleteDownloadTemporaryDirectory())
            {
                DeleteTemporarySecrets();
                ShowError("上一次下载的临时目录仍被占用，请稍后重试或关闭占用该目录的程序。");
                return;
            }

            try
            {
                if (resumeExistingTask)
                {
                    if (string.IsNullOrWhiteSpace(_downloadTemporaryDirectory) ||
                        !Directory.Exists(_downloadTemporaryDirectory))
                    {
                        throw new DirectoryNotFoundException("可恢复的下载缓存已经不存在。");
                    }
                }
                else
                {
                    _downloadTemporaryDirectory = DownloadTemporaryStore.Create();
                    _downloadResumeManifest = null;
                }
            }
            catch (Exception exception)
            {
                DeleteTemporarySecrets();
                ShowError("无法创建下载临时目录：\r\n\r\n" + exception.Message);
                return;
            }

            string leaseError;
            if (!EnsureDownloadResumeLease(out leaseError))
            {
                DeleteTemporarySecrets();
                if (!resumeExistingTask)
                {
                    DeleteDownloadTemporaryDirectory();
                }

                ShowError("无法锁定可恢复任务缓存：\r\n\r\n" + leaseError);
                return;
            }

            string canonicalInputError;
            if (!TryCanonicalizeImportedInput(request, out canonicalInputError))
            {
                DeleteTemporarySecrets();
                if (!resumeExistingTask)
                {
                    DeleteDownloadTemporaryDirectory();
                }
                ShowError(
                    "无法把导入的播放列表保存到任务缓存：\r\n\r\n" +
                    canonicalInputError);
                return;
            }

            string effectiveInput;
            string mediaTransportError;
            if (!TryPrepareCurlMediaTransport(request, out effectiveInput, out mediaTransportError))
            {
                StopActiveMediaProxy();
                DeleteTemporarySecrets();
                _downloadTaskState = DownloadTaskState.Failed;
                _resumableRequest = request;
                string persistError;
                if (TryPersistDownloadResume(
                    request,
                    DownloadResumeState.Failed,
                    out persistError))
                {
                    DeleteSupersededImportedPlaylist();
                }
                else
                {
                    AppendLog("[GUI] 无法更新重启恢复信息：" + persistError);
                }
                _statusLabel.Text = "下载准备失败，播放列表与缓存已保留";
                UpdateDownloadActionButtons();

                ShowError("无法准备受保护分片传输：\r\n\r\n" + mediaTransportError);
                return;
            }

            string resumeSaveError;
            if (!TryPersistDownloadResume(
                request,
                DownloadResumeState.Running,
                out resumeSaveError))
            {
                StopActiveMediaProxy();
                DeleteTemporarySecrets();
                _downloadTaskState = DownloadTaskState.Failed;
                _resumableRequest = request;
                _statusLabel.Text = "无法保存重启续传信息，任务未启动";
                UpdateDownloadActionButtons();

                ShowError(
                    "无法保存重启续传信息，因此没有启动下载：\r\n\r\n" +
                    resumeSaveError);
                return;
            }
            DeleteSupersededImportedPlaylist();

            List<string> arguments = new List<string>();
            arguments.Add(effectiveInput);
            arguments.Add("--save-dir");
            arguments.Add(request.SaveDirectory);
            arguments.Add("--save-name");
            arguments.Add(request.FileName);
            arguments.Add("--tmp-dir");
            arguments.Add(_downloadTemporaryDirectory);
            arguments.Add("--auto-select");
            arguments.Add("--ffmpeg-binary-path");
            arguments.Add(request.FfmpegPath);
            arguments.Add("--ui-language");
            arguments.Add("zh-CN");
            arguments.Add("--no-ansi-color");
            arguments.Add("--no-log");
            arguments.Add("--write-meta-json");
            arguments.Add("false");
            arguments.Add("--del-after-done");
            arguments.Add("false");
            arguments.Add("--disable-update-check");

            if (!string.IsNullOrWhiteSpace(keyArgument))
            {
                arguments.Add("--custom-hls-key");
                arguments.Add(keyArgument);
                if (!string.IsNullOrWhiteSpace(ivArgument))
                {
                    arguments.Add("--custom-hls-iv");
                    arguments.Add(ivArgument);
                }
            }

            if (request.MuxToMp4)
            {
                arguments.Add("-M");
                arguments.Add("format=mp4");
            }

            if (_activeMediaProxy != null)
            {
                AppendCurlTransportDownloaderArguments(arguments);
            }
            else
            {
                AppendCapturedHeaderArguments(arguments, request.CapturedHeaders);
            }

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = request.DownloaderPath;
            startInfo.Arguments = CommandLine.JoinArguments(arguments);
            startInfo.WorkingDirectory = request.SaveDirectory;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            ExternalToolOutputEncodings.ApplyDownloader(startInfo);

            string ffmpegDirectory = Path.GetDirectoryName(request.FfmpegPath);
            if (!string.IsNullOrWhiteSpace(ffmpegDirectory))
            {
                string existingPath = startInfo.EnvironmentVariables["PATH"] ?? string.Empty;
                startInfo.EnvironmentVariables["PATH"] = ffmpegDirectory + ";" + existingPath;
            }

            Process process = new Process();
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = false;

            _isCancelling = false;
            _isPausing = false;
            _activeOperation = OperationKind.Download;
            _downloadTaskState = DownloadTaskState.Running;
            _resumableRequest = request;
            _activeOperationDirectory = request.SaveDirectory;
            _lastOutputPath = null;
            _expectedOutputBaseName = request.FileName;
            _downloadStartedUtc = DateTime.UtcNow;
            _filesBeforeDownload = CaptureFileState(request.SaveDirectory);
            _activeProcess = process;
            ResetDownloadProgress();
            SetRunningState(true);

            AppendLog(string.Empty);
            AppendLog("[GUI] 开始任务：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AppendLog("[GUI] 保存目录：" + request.SaveDirectory);
            AppendLog("[GUI] 文件名称：" + request.FileName);
            AppendLog("[GUI] 分片缓存：" + _downloadTemporaryDirectory);
            if (request.CapturedHeaders != null && request.CapturedHeaders.HasAny)
            {
                AppendLog("[GUI] 已捕获浏览器请求头：" +
                          DescribeCapturedHeaderNames(request.CapturedHeaders));
                if (!string.IsNullOrWhiteSpace(request.CapturedHeaders.Referer))
                {
                    AppendLog("[GUI] 分片 Referer：" +
                              DescribeRefererForLog(request.CapturedHeaders.Referer));
                }
            }
            if (resumeExistingTask)
            {
                AppendLog("[GUI] 继续已有任务；下载器将校验缓存并补下缺失分片。");
            }
            _statusLabel.Text = "正在启动下载程序...";
            UpdateDownloadActionButtons();

            bool processStarted = false;
            bool exitMonitoringEnabled = false;
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("下载程序没有成功启动。");
                }
                processStarted = true;
                StartDownloadOutputCapture(process);

                ProcessJob job = ProcessJob.TryCreateKillOnClose();
                if (job == null)
                {
                    throw new InvalidOperationException(
                        "无法创建下载进程保护 Job，已停止任务以避免重启后两个下载器共用缓存。");
                }
                if (!job.AddProcess(process))
                {
                    job.Dispose();
                    throw new InvalidOperationException(
                        "无法把下载器加入进程保护 Job，已停止任务以保护分片缓存。");
                }
                _processJob = job;

                process.Exited += ProcessExited;
                process.EnableRaisingEvents = true;
                exitMonitoringEnabled = true;
                AdvanceDownloadPhase(DownloadPhase.Parsing, "正在解析视频信息...");
            }
            catch (Exception exception)
            {
                AppendLog("[GUI] 启动失败：" + exception.Message);
                bool processStopped = !processStarted || StopProcessForCleanup(process, 5000);
                if (!processStopped)
                {
                    if (!exitMonitoringEnabled)
                    {
                        try
                        {
                            process.Exited -= ProcessExited;
                            process.Exited += ProcessExited;
                            process.EnableRaisingEvents = true;
                            exitMonitoringEnabled = true;
                        }
                        catch
                        {
                        }
                    }

                    _isCancelling = true;
                    _statusLabel.Text = "下载程序启动异常，正在等待进程退出";
                    AppendLog("[GUI] 无法确认下载进程已退出；保留临时文件并维持取消状态。");
                    ShowError(
                        "下载程序启动后发生异常，并且暂时无法确认进程已经退出。\r\n\r\n" +
                        "请再次点击“取消”，或关闭窗口以重试终止进程。");
                    return;
                }

                WaitForDownloadOutputCapture(process);
                ClearDownloadOutputCapture(process);
                _activeProcess = null;
                DisposeProcessJob();
                StopActiveMediaProxy();
                try
                {
                    process.Dispose();
                }
                catch
                {
                }

                _downloadTaskState = DownloadTaskState.Failed;
                _resumableRequest = request;
                string persistError;
                if (!TryPersistDownloadResume(
                    request,
                    DownloadResumeState.Failed,
                    out persistError))
                {
                    AppendLog("[GUI] 无法更新重启恢复信息：" + persistError);
                }
                SetRunningState(false);
                _statusLabel.Text = "启动失败，缓存已保留";
                AppendLog("[GUI] 任务缓存已保留，可点击“重试下载”。");
                DeleteTemporarySecrets();
                UpdateDownloadActionButtons();
                ShowError("无法启动下载程序：\r\n\r\n" + exception.Message);
            }
        }

        internal static void AppendCapturedHeaderArguments(
            IList<string> arguments,
            MediaRequestHeaders headers)
        {
            if (arguments == null || headers == null)
            {
                return;
            }

            AppendHeaderArgument(arguments, "Cookie", headers.Cookie);
            AppendHeaderArgument(arguments, "Referer", headers.Referer);
            AppendHeaderArgument(arguments, "User-Agent", headers.UserAgent);
            AppendHeaderArgument(arguments, "Origin", headers.Origin);
            AppendHeaderArgument(arguments, "Authorization", headers.Authorization);

            List<string> names = new List<string>(headers.AdditionalHeaders.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                string value = headers.AdditionalHeaders[name];
                if (MediaRequestHeaders.IsAllowedAdditionalHeader(name, value))
                {
                    AppendHeaderArgument(arguments, name, value);
                }
            }
        }

        internal static void AppendCurlTransportDownloaderArguments(IList<string> arguments)
        {
            if (arguments == null)
            {
                return;
            }

            arguments.Add("--use-system-proxy");
            arguments.Add("false");
            arguments.Add("--thread-count");
            arguments.Add("4");
            arguments.Add("--download-retry-count");
            arguments.Add("10");
            arguments.Add("--http-request-timeout");
            arguments.Add("660");
        }

        private static void AppendHeaderArgument(
            IList<string> arguments,
            string name,
            string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            arguments.Add("-H");
            arguments.Add(name + ": " + value);
        }

        internal static string DescribeCapturedHeaderNames(MediaRequestHeaders headers)
        {
            if (headers == null || !headers.HasAny)
            {
                return "无";
            }

            List<string> names = new List<string>();
            if (!string.IsNullOrWhiteSpace(headers.Cookie))
            {
                names.Add("Cookie");
            }

            if (!string.IsNullOrWhiteSpace(headers.Referer))
            {
                names.Add("Referer");
            }

            if (!string.IsNullOrWhiteSpace(headers.UserAgent))
            {
                names.Add("User-Agent");
            }

            if (!string.IsNullOrWhiteSpace(headers.Origin))
            {
                names.Add("Origin");
            }

            if (!string.IsNullOrWhiteSpace(headers.Authorization))
            {
                names.Add("Authorization");
            }

            List<string> additionalNames = new List<string>(headers.AdditionalHeaders.Keys);
            additionalNames.Sort(StringComparer.OrdinalIgnoreCase);
            names.AddRange(additionalNames);
            return string.Join(", ", names.ToArray());
        }

        internal static string DescribeRefererForLog(string referer)
        {
            Uri uri;
            if (!Uri.TryCreate(referer, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "[已捕获，格式不可显示]";
            }

            return uri.GetLeftPart(UriPartial.Path);
        }

        private bool TryPrepareCurlMediaTransport(
            DownloadRequest request,
            out string effectiveInput,
            out string errorMessage)
        {
            effectiveInput = request == null ? null : request.Input;
            errorMessage = null;
            if (request == null || string.IsNullOrWhiteSpace(request.Input) ||
                !File.Exists(request.Input))
            {
                return true;
            }

            string playlistContent;
            try
            {
                playlistContent = File.ReadAllText(request.Input, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                errorMessage = "无法读取本地播放列表：" + exception.Message;
                return false;
            }

            if (!RequiresCurlMediaTransport(playlistContent) &&
                !RequiresCapturedHeaderIsolation(request.CapturedHeaders))
            {
                return true;
            }

            Uri observedSegmentUri;
            if (request.CapturedHeaders == null ||
                !Uri.TryCreate(
                    request.CapturedHeaders.SourceUrl,
                    UriKind.Absolute,
                    out observedSegmentUri) ||
                (observedSegmentUri.Scheme != Uri.UriSchemeHttp &&
                 observedSegmentUri.Scheme != Uri.UriSchemeHttps))
            {
                errorMessage =
                    "该播放列表的 CDN 会校验浏览器请求，但程序尚未观察到真实分片。\r\n" +
                    "请重新点击“从网页捕获”，在内嵌浏览器中播放几秒，看到“已捕获真实分片请求”后再选择。";
                return false;
            }

            HlsPlaylistInspection inspection = HlsPlaylistInspector.Inspect(
                playlistContent,
                request.Input);
            if (inspection.SegmentCount == 0 || ContainsNestedPlaylistResource(inspection))
            {
                errorMessage =
                    "当前捕获项仍是主播放列表或包含尚未展开的媒体子列表。\r\n" +
                    "请返回捕获窗口继续播放，选择实际列出 .ts/.m4s/.jpeg 分片的媒体播放列表。";
                return false;
            }

            string curlError;
            if (!CurlMediaProxy.IsCurlAvailable(out curlError))
            {
                errorMessage = curlError;
                return false;
            }

            CurlMediaProxy proxy = null;
            try
            {
                MediaRequestHeaders transportHeaders = request.CapturedHeaders.Clone();
                if (string.IsNullOrWhiteSpace(transportHeaders.UserAgent))
                {
                    transportHeaders.UserAgent =
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) M3U8-Video-Downloader";
                }

                proxy = CurlMediaProxy.Start(
                    _downloadTemporaryDirectory,
                    transportHeaders,
                    AppendLog);
                int resourceCount = 0;
                string rewritten = HlsPlaylistInspector.RewriteReferences(
                    playlistContent,
                    request.Input,
                    delegate(string url)
                    {
                        Uri uri;
                        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                            (uri.Scheme != Uri.UriSchemeHttp &&
                             uri.Scheme != Uri.UriSchemeHttps))
                        {
                            return url;
                        }

                        resourceCount++;
                        return proxy.Register(url);
                    });

                if (resourceCount == 0)
                {
                    proxy.Dispose();
                    return true;
                }

                string proxyPlaylistPath = Path.Combine(
                    _downloadTemporaryDirectory,
                    "browser_transport.m3u8");
                File.WriteAllText(proxyPlaylistPath, rewritten, new UTF8Encoding(false));
                _activeMediaProxy = proxy;
                effectiveInput = proxyPlaylistPath;
                AppendLog(
                    "[GUI] 已启用隔离浏览器凭据的 Windows cURL 回环传输，共 " +
                    resourceCount.ToString(CultureInfo.InvariantCulture) +
                    " 个播放列表资源。N_m3u8DL-RE 仅连接 127.0.0.1 并继续负责缓存与合并。");
                return true;
            }
            catch (Exception exception)
            {
                if (proxy != null)
                {
                    proxy.Dispose();
                }

                errorMessage = exception.Message;
                return false;
            }
        }

        internal static bool RequiresCurlMediaTransport(string playlistContent)
        {
            if (!PlaylistInput.LooksLikePlaylistContent(playlistContent))
            {
                return false;
            }

            if (ContainsPrivateTokenTag(playlistContent))
            {
                return true;
            }

            HlsPlaylistInspection inspection = HlsPlaylistInspector.Inspect(
                playlistContent,
                null);
            foreach (HlsPlaylistResource resource in inspection.Resources)
            {
                Uri uri;
                if (!Uri.TryCreate(resource.Url, UriKind.Absolute, out uri))
                {
                    continue;
                }

                if (string.Equals(uri.Host, "surrit.com", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith(".surrit.com", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool RequiresCapturedHeaderIsolation(MediaRequestHeaders headers)
        {
            return headers != null &&
                (!string.IsNullOrWhiteSpace(headers.Cookie) ||
                 !string.IsNullOrWhiteSpace(headers.Authorization) ||
                 headers.AdditionalHeaders.Count > 0);
        }

        internal static bool ContainsPrivateTokenTag(string playlistContent)
        {
            if (string.IsNullOrEmpty(playlistContent))
            {
                return false;
            }

            string[] lines = playlistContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.Equals(line, "#EXT-X-TOKEN", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("#EXT-X-TOKEN=", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("#EXT-X-TOKEN:", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsNestedPlaylistResource(HlsPlaylistInspection inspection)
        {
            if (inspection == null)
            {
                return false;
            }

            foreach (HlsPlaylistResource resource in inspection.Resources)
            {
                if (resource == null || resource.SegmentNumber > 0)
                {
                    continue;
                }

                if (string.Equals(resource.Kind, "子播放列表", StringComparison.Ordinal) ||
                    string.Equals(resource.Kind, "媒体轨道", StringComparison.Ordinal) ||
                    string.Equals(resource.Kind, "I 帧播放列表", StringComparison.Ordinal))
                {
                    return true;
                }

                Uri uri;
                if (Uri.TryCreate(resource.Url, UriKind.Absolute, out uri) &&
                    uri.AbsolutePath.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void StopActiveMediaProxy()
        {
            CurlMediaProxy proxy = _activeMediaProxy;
            _activeMediaProxy = null;
            if (proxy != null)
            {
                try
                {
                    proxy.Dispose();
                }
                catch (Exception exception)
                {
                    AppendLog("[GUI] 清理 cURL 回环传输时发生错误：" + exception.Message);
                }
            }
            DeleteBrowserTransportPlaylist(_downloadTemporaryDirectory);
        }

        private static void DeleteBrowserTransportPlaylist(string cacheDirectory)
        {
            string ownedDirectory;
            string taskId;
            if (!DownloadResumeStore.TryGetOwnedCacheDirectory(
                cacheDirectory,
                out ownedDirectory,
                out taskId))
            {
                return;
            }

            try
            {
                string path = Path.Combine(ownedDirectory, "browser_transport.m3u8");
                if (File.Exists(path))
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    }
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private bool PrepareTemporarySecrets(
            string keyValue,
            string ivValue,
            out string keyArgument,
            out string ivArgument)
        {
            keyArgument = null;
            ivArgument = null;
            DeleteTemporarySecrets();

            if (string.IsNullOrWhiteSpace(keyValue))
            {
                return true;
            }

            try
            {
                byte[] keyBytes;
                if (!HlsKeyValue.TryGetBytes(keyValue, out keyBytes))
                {
                    throw new InvalidDataException("HLS 密钥不是有效的 16 字节 AES-128 数据。");
                }

                _temporaryHlsKeyPath = SecretFileStore.Create(keyBytes, ".key");
                keyArgument = _temporaryHlsKeyPath;
                AddSecretRedactions(keyValue, keyBytes);

                if (!string.IsNullOrWhiteSpace(ivValue))
                {
                    byte[] ivBytes;
                    if (!HlsKeyValue.TryGetBytes(ivValue, out ivBytes))
                    {
                        throw new InvalidDataException("HLS IV 不是有效的 16 字节数据。");
                    }

                    _temporaryHlsIvPath = SecretFileStore.Create(ivBytes, ".iv");
                    ivArgument = _temporaryHlsIvPath;
                    AddSecretRedactions(ivValue, ivBytes);
                }

                return true;
            }
            catch (Exception exception)
            {
                DeleteTemporarySecrets();
                ShowError("无法准备手动密钥：\r\n\r\n" + exception.Message);
                return false;
            }
        }

        private void AddSecretRedactions(string originalValue, byte[] bytes)
        {
            if (!string.IsNullOrWhiteSpace(originalValue))
            {
                _secretRedactionValues.Add(originalValue.Trim());
            }

            string hex = BitConverter.ToString(bytes).Replace("-", string.Empty);
            _secretRedactionValues.Add(hex);
            _secretRedactionValues.Add(hex.ToLowerInvariant());
            _secretRedactionValues.Add(Convert.ToBase64String(bytes));
        }

        private void DeleteTemporarySecrets()
        {
            if (SecretFileStore.Delete(_temporaryHlsKeyPath))
            {
                _temporaryHlsKeyPath = null;
            }

            if (SecretFileStore.Delete(_temporaryHlsIvPath))
            {
                _temporaryHlsIvPath = null;
            }

            if (_temporaryHlsKeyPath == null && _temporaryHlsIvPath == null)
            {
                _secretRedactionValues.Clear();
            }
        }

        private void StartDownloadOutputCapture(Process process)
        {
            ExternalToolOutputParser standardOutputParser = new ExternalToolOutputParser(
                HandleDownloaderLogRecord,
                QueueDownloadProgress);
            ExternalToolOutputParser standardErrorParser = new ExternalToolOutputParser(
                HandleDownloaderLogRecord,
                QueueDownloadProgress);
            ProcessOutputPump pump = new ProcessOutputPump(
                process.StandardOutput,
                process.StandardError,
                standardOutputParser.Append,
                standardErrorParser.Append,
                standardOutputParser.Complete,
                standardErrorParser.Complete);

            _downloadOutputProcess = process;
            _downloadStandardOutputParser = standardOutputParser;
            _downloadStandardErrorParser = standardErrorParser;
            _downloadOutputPump = pump;
            pump.Start();
        }

        private void HandleDownloaderLogRecord(string record)
        {
            string redacted = RedactSecrets(record);
            string error = ExtractDownloaderFailureSummary(redacted, false);
            if (!string.IsNullOrWhiteSpace(error))
            {
                _lastDownloaderError = error;
            }
            else
            {
                string warning = ExtractDownloaderFailureSummary(redacted, true);
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    _lastDownloaderWarning = warning;
                }
            }

            AppendLog(redacted);
        }

        internal static string ExtractDownloaderFailureSummary(string record, bool includeWarnings)
        {
            if (string.IsNullOrWhiteSpace(record))
            {
                return null;
            }

            bool isError = DownloaderErrorSeverity.IsMatch(record);
            bool isWarning = includeWarnings && DownloaderWarningSeverity.IsMatch(record);
            if (!isError && !isWarning)
            {
                return null;
            }

            string summary = DownloaderLogPrefix.Replace(record.Trim(), string.Empty, 1);
            summary = Regex.Replace(summary, @"\s+", " ").Trim();
            const int maximumSummaryLength = 500;
            if (summary.Length > maximumSummaryLength)
            {
                summary = summary.Substring(0, maximumSummaryLength) + "…";
            }

            return summary;
        }

        private string GetLastDownloaderFailureSummary()
        {
            return !string.IsNullOrWhiteSpace(_lastDownloaderError)
                ? _lastDownloaderError
                : _lastDownloaderWarning;
        }

        private void QueueDownloadProgress(ExternalToolProgress progress)
        {
            if (progress != null && !_isCancelling && _downloadPhase < DownloadPhase.Merging)
            {
                string milestone = CreateProgressMilestone(progress);
                if (!string.IsNullOrEmpty(milestone))
                {
                    AppendLog(milestone);
                }

                _pendingProgressUpdates.Enqueue(progress);
            }
        }

        private bool WaitForDownloadOutputCapture(Process process)
        {
            ProcessOutputPump pump = ReferenceEquals(process, _downloadOutputProcess)
                ? _downloadOutputPump
                : null;
            if (pump == null || pump.WaitForCompletion(5000))
            {
                return true;
            }

            pump.Stop();
            pump.WaitForCompletion(1000);
            AppendLog("[GUI] 警告：下载器输出管道未及时关闭，尾部日志可能不完整。");
            return false;
        }

        private void ClearDownloadOutputCapture(Process process)
        {
            if (!ReferenceEquals(process, _downloadOutputProcess))
            {
                return;
            }

            _downloadOutputProcess = null;
            _downloadOutputPump = null;
            _downloadStandardOutputParser = null;
            _downloadStandardErrorParser = null;
        }

        private void ProcessOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                AppendLog(RedactSecrets(e.Data));
            }
        }

        private void ProcessErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                AppendLog(RedactSecrets(e.Data));
            }
        }

        private string RedactSecrets(string line)
        {
            string result = line ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(_manualHlsKey))
            {
                result = Regex.Replace(
                    result,
                    Regex.Escape(_manualHlsKey),
                    "[HLS KEY HIDDEN]",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            if (!string.IsNullOrWhiteSpace(_manualHlsIv))
            {
                result = Regex.Replace(
                    result,
                    Regex.Escape(_manualHlsIv),
                    "[HLS IV HIDDEN]",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            foreach (string secret in _secretRedactionValues)
            {
                if (!string.IsNullOrWhiteSpace(secret))
                {
                    result = Regex.Replace(
                        result,
                        Regex.Escape(secret),
                        "[HLS SECRET HIDDEN]",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
            }

            if (_capturedHeaders != null)
            {
                if (!string.IsNullOrWhiteSpace(_capturedHeaders.Cookie))
                {
                    result = result.Replace(_capturedHeaders.Cookie, "[COOKIE HIDDEN]");
                }

                if (!string.IsNullOrWhiteSpace(_capturedHeaders.Authorization))
                {
                    result = result.Replace(_capturedHeaders.Authorization, "[AUTHORIZATION HIDDEN]");
                }

                foreach (KeyValuePair<string, string> header in
                         _capturedHeaders.AdditionalHeaders)
                {
                    if (!string.IsNullOrWhiteSpace(header.Value))
                    {
                        result = result.Replace(
                            header.Value,
                            "[CAPTURED HEADER HIDDEN]");
                    }
                }
            }

            return result;
        }

        private void ProcessExited(object sender, EventArgs e)
        {
            Process completedProcess = sender as Process;
            if (completedProcess == null)
            {
                return;
            }

            int exitCode = -1;
            if (ReferenceEquals(completedProcess, _activeProcess))
            {
                DisposeProcessJob();
            }

            try
            {
                completedProcess.WaitForExit();
            }
            catch
            {
            }

            WaitForDownloadOutputCapture(completedProcess);
            try
            {
                exitCode = completedProcess.ExitCode;
            }
            catch
            {
            }

            SafeBeginInvoke(delegate
            {
                FinishDownload(completedProcess, exitCode);
            });
        }

        private void FinishDownload(Process completedProcess, int exitCode)
        {
            if (!ReferenceEquals(completedProcess, _activeProcess))
            {
                return;
            }

            bool wasPausing = _isPausing;
            bool wasCancelled = _isCancelling && !wasPausing;
            OperationKind completedOperation = _activeOperation;
            bool isConversion = completedOperation == OperationKind.ConvertFile;
            string saveDirectory = string.IsNullOrWhiteSpace(_activeOperationDirectory)
                ? _saveDirectoryTextBox.Text.Trim()
                : _activeOperationDirectory;
            WaitForDownloadOutputCapture(completedProcess);
            FlushPendingOutput();
            ClearDownloadOutputCapture(completedProcess);
            _activeProcess = null;
            StopActiveMediaProxy();
            _isCancelling = false;
            _isPausing = false;
            _activeOperationDirectory = null;
            while (!_pendingLogLines.IsEmpty)
            {
                FlushPendingLogLines();
            }

            DisposeProcessJob();

            try
            {
                completedProcess.Dispose();
            }
            catch
            {
            }

            SetRunningState(false);

            string output = null;
            string conversionCommitError = null;
            bool preserveDownloadCache = false;
            if (isConversion)
            {
                if (exitCode == 0 && !wasCancelled)
                {
                    if (!CommitConversionOutput(out output, out conversionCommitError))
                    {
                        DeleteConversionTemporaryOutput();
                    }
                }
                else if (!DeleteConversionTemporaryOutput())
                {
                    AppendLog("[GUI] 警告：转换临时文件仍被占用，将在下次操作时重试清理。");
                }
            }
            else
            {
                output = FindChangedOutput(saveDirectory);
                bool downloadSucceeded = exitCode == 0 && output != null;
                preserveDownloadCache = !downloadSucceeded &&
                    (wasPausing || !wasCancelled);
                if (!preserveDownloadCache && !DeleteDownloadTemporaryDirectory())
                {
                    AppendLog("[GUI] 警告：下载临时目录仍被占用，将在下次任务时重试清理。");
                }
            }

            _lastOutputPath = output;
            if (!isConversion)
            {
                DeleteTemporarySecrets();
                if (!preserveDownloadCache)
                {
                    DeleteImportedPlaylist();
                }
            }

            if (!string.IsNullOrWhiteSpace(conversionCommitError))
            {
                _progressBar.Value = 0;
                _statusLabel.Text = "转换结果保存失败";
                AppendLog("[GUI] 转换结果保存失败：" + conversionCommitError);
                MessageBox.Show(
                    this,
                    "FFmpeg 已结束，但无法安全保存最终 MP4。已有目标文件没有被修改。\r\n\r\n" +
                    conversionCommitError,
                    "转换结果保存失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            if (!isConversion && wasPausing && !(exitCode == 0 && output != null))
            {
                _downloadTaskState = DownloadTaskState.Paused;
                PersistDownloadResumeOrLog(DownloadResumeState.Paused);
                _statusLabel.Text = "下载已暂停，分片缓存已保留";
                AppendLog("[GUI] 下载已暂停。缓存保留在：" + _downloadTemporaryDirectory);
                UpdateDownloadActionButtons();
                return;
            }

            if (wasCancelled && !(exitCode == 0 && output != null))
            {
                if (!isConversion)
                {
                    _downloadTaskState = DownloadTaskState.Idle;
                    ClearCapturedHeadersForRequest(_resumableRequest);
                    _resumableRequest = null;
                }
                _progressBar.Value = 0;
                _statusLabel.Text = "任务已取消";
                AppendLog("[GUI] 任务已取消。");
                UpdateDownloadActionButtons();
                return;
            }

            if (wasCancelled && exitCode == 0 && output != null)
            {
                AppendLog("[GUI] 取消请求到达时任务已经完成，保留下载结果。");
            }

            if (exitCode == 0 && output != null)
            {
                string completedText = isConversion ? "转换完成" : "下载完成";
                if (!isConversion)
                {
                    _downloadTaskState = DownloadTaskState.Completed;
                }
                _progressBar.Value = 100;
                _statusLabel.Text = completedText + "：" + Path.GetFileName(output);
                AppendLog("[GUI] " + completedText + "：" + output);
                UpdateDownloadActionButtons();

                if (_openFolderWhenDoneCheckBox.Checked)
                {
                    OpenDirectory(saveDirectory);
                }

                MessageBox.Show(
                    this,
                    completedText + "。\r\n\r\n" + output,
                    "任务完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (exitCode == 0)
            {
                if (!isConversion)
                {
                    _downloadTaskState = DownloadTaskState.Failed;
                    PersistDownloadResumeOrLog(DownloadResumeState.Failed);
                }
                else
                {
                    _progressBar.Value = 100;
                }
                _statusLabel.Text = isConversion
                    ? "任务结束，请打开保存目录查看结果"
                    : "未识别到成品，分片缓存已保留";
                AppendLog("[GUI] 进程已正常结束，但没有识别到新生成的媒体文件。");
                if (!isConversion)
                {
                    AppendLog("[GUI] 缓存保留在：" + _downloadTemporaryDirectory);
                    AppendLog("[GUI] 可点击“重试下载”继续校验并补下分片。");
                }
                UpdateDownloadActionButtons();
                MessageBox.Show(
                    this,
                    "任务已经结束，但程序没有识别到新生成的媒体文件。\r\n\r\n" +
                    (isConversion
                        ? "请打开保存目录并查看运行日志。"
                        : "分片缓存已保留，可点击“重试下载”继续。"),
                    "请检查结果",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            string failureText = isConversion ? "转换失败" : "下载失败";
            string failureSummary = isConversion ? null : GetLastDownloaderFailureSummary();
            if (!isConversion)
            {
                _downloadTaskState = DownloadTaskState.Failed;
                PersistDownloadResumeOrLog(DownloadResumeState.Failed);
            }
            else
            {
                _progressBar.Value = 0;
            }
            _statusLabel.Text = isConversion
                ? failureText + "，退出代码 " + exitCode
                : failureText + "，分片缓存已保留";
            AppendLog("[GUI] " + failureText + "，退出代码：" + exitCode);
            if (!string.IsNullOrWhiteSpace(failureSummary))
            {
                AppendLog("[GUI] 下载器最后错误：" + failureSummary);
            }
            if (!isConversion)
            {
                AppendLog("[GUI] 缓存保留在：" + _downloadTemporaryDirectory);
                AppendLog("[GUI] 修复上述错误后点击“重试下载”，只补缺失分片。");
            }
            UpdateDownloadActionButtons();
            string failureDetails = string.IsNullOrWhiteSpace(failureSummary)
                ? "请查看运行日志中失败前的最后几行。"
                : "下载器最后错误：\r\n" + failureSummary;
            MessageBox.Show(
                this,
                failureText + "，退出代码：" + exitCode + "。\r\n\r\n" +
                (isConversion
                    ? "请查看运行日志中的最后几行。"
                    : failureDetails + "\r\n\r\n分片缓存已保留；修复该错误后点击“重试下载”即可继续。"),
                failureText,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        private void AppendLog(string line)
        {
            if (IsDisposed || Disposing)
            {
                return;
            }

            if (InvokeRequired)
            {
                _pendingLogLines.Enqueue(line ?? string.Empty);
                return;
            }

            AppendLogCore(line);
        }

        private void FlushPendingOutput()
        {
            FlushPendingLogLines();
            FlushPendingProgressUpdates();
        }

        private void FlushPendingLogLines()
        {
            if (IsDisposed || Disposing || _logTextBox == null)
            {
                return;
            }

            StringBuilder batch = new StringBuilder();
            List<string> statusLines = new List<string>();
            string line;
            int count = 0;
            while (count < 500 && _pendingLogLines.TryDequeue(out line))
            {
                batch.AppendLine(line ?? string.Empty);
                statusLines.Add(line);
                count++;
            }

            if (batch.Length == 0)
            {
                return;
            }

            AppendTextToLog(batch.ToString());
            _logTextBox.SelectionStart = _logTextBox.TextLength;
            _logTextBox.ScrollToCaret();
            foreach (string statusLine in statusLines)
            {
                UpdateStatusFromLog(statusLine);
            }
        }

        private void AppendLogCore(string line)
        {
            AppendTextToLog((line ?? string.Empty) + Environment.NewLine);
            _logTextBox.SelectionStart = _logTextBox.TextLength;
            _logTextBox.ScrollToCaret();
            UpdateStatusFromLog(line);
        }

        private void AppendTextToLog(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (text.Length > MaximumLogCharacters)
            {
                text = text.Substring(text.Length - MaximumLogCharacters);
                int firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0 && firstNewline + 1 < text.Length)
                {
                    text = text.Substring(firstNewline + 1);
                }
            }

            int excess = _logTextBox.TextLength + text.Length - MaximumLogCharacters;
            if (excess > 0 && _logTextBox.TextLength > 0)
            {
                string existing = _logTextBox.Text;
                int removeLength = Math.Min(existing.Length, excess);
                int newline = existing.IndexOf('\n', removeLength);
                if (newline >= 0)
                {
                    removeLength = newline + 1;
                }
                else
                {
                    removeLength = existing.Length;
                }

                _logTextBox.Select(0, removeLength);
                _logTextBox.SelectedText = string.Empty;
            }

            _logTextBox.AppendText(text);
        }

        private void ResetDownloadProgress()
        {
            ExternalToolProgress ignored;
            while (_pendingProgressUpdates.TryDequeue(out ignored))
            {
            }

            lock (_progressLogSync)
            {
                _progressLogBuckets.Clear();
            }
            _downloadPhase = DownloadPhase.Starting;
            _progressBar.Value = 0;
        }

        private void FlushPendingProgressUpdates()
        {
            ExternalToolProgress progress;
            int count = 0;
            while (count < 500 && _pendingProgressUpdates.TryDequeue(out progress))
            {
                ApplyDownloadProgress(progress);
                count++;
            }
        }

        private void ApplyDownloadProgress(ExternalToolProgress progress)
        {
            if (progress == null || _activeProcess == null ||
                _activeOperation != OperationKind.Download || _isCancelling ||
                _downloadPhase >= DownloadPhase.Merging)
            {
                return;
            }

            AdvanceDownloadPhase(DownloadPhase.Downloading, null);
            string streamKind = string.IsNullOrWhiteSpace(progress.StreamKind)
                ? "Vid"
                : progress.StreamKind;
            int displayedPercent = Math.Max(0, Math.Min(99, (int)Math.Round(progress.Percent)));
            _progressBar.MarqueeAnimationSpeed = 0;
            _progressBar.Style = ProgressBarStyle.Blocks;
            _progressBar.Value = displayedPercent;

            string streamLabel = GetProgressStreamLabel(streamKind);
            StringBuilder status = new StringBuilder();
            status.Append("正在下载");
            status.Append(streamLabel);
            status.Append("：");
            status.Append(progress.Current);
            status.Append('/');
            status.Append(progress.Total);
            status.Append(" (");
            status.Append(progress.Percent.ToString("0.00"));
            status.Append("%)");
            if (!string.IsNullOrWhiteSpace(progress.DownloadedSize))
            {
                status.Append("  ");
                status.Append(progress.DownloadedSize);
                if (!string.IsNullOrWhiteSpace(progress.TotalSize))
                {
                    status.Append('/');
                    status.Append(progress.TotalSize);
                }
            }

            if (!string.IsNullOrWhiteSpace(progress.Speed) && progress.Speed != "-")
            {
                status.Append("  ");
                status.Append(progress.Speed);
            }

            if (!string.IsNullOrWhiteSpace(progress.RemainingTime))
            {
                status.Append("  剩余 ");
                status.Append(progress.RemainingTime);
            }

            _statusLabel.Text = status.ToString();
        }

        private string CreateProgressMilestone(ExternalToolProgress progress)
        {
            if (progress.Percent >= 100)
            {
                return null;
            }

            string streamLabel = GetProgressStreamLabel(progress.StreamKind);
            int bucket = Math.Max(0, (int)Math.Floor(progress.Percent / 5.0));
            string key = (progress.StreamKind ?? string.Empty) + "|" + progress.Total;
            lock (_progressLogSync)
            {
                int previousBucket;
                if (_progressLogBuckets.TryGetValue(key, out previousBucket) && bucket <= previousBucket)
                {
                    return null;
                }

                _progressLogBuckets[key] = bucket;
            }

            return "[进度] " + streamLabel + " " + progress.Current + "/" + progress.Total +
                   " (" + progress.Percent.ToString("0.00") + "%)";
        }

        private static string GetProgressStreamLabel(string streamKind)
        {
            if (string.Equals(streamKind, "Aud", StringComparison.OrdinalIgnoreCase))
            {
                return "音频";
            }

            if (string.Equals(streamKind, "Sub", StringComparison.OrdinalIgnoreCase))
            {
                return "字幕";
            }

            return "视频";
        }

        private void UpdateStatusFromLog(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || _activeProcess == null ||
                _activeOperation != OperationKind.Download || _isCancelling)
            {
                return;
            }

            string message = DownloaderLogPrefix.Replace(line.Trim(), string.Empty);
            if (message.StartsWith("正在解析媒体信息", StringComparison.OrdinalIgnoreCase))
            {
                AdvanceDownloadPhase(DownloadPhase.Parsing, "正在解析媒体信息...");
            }
            else if (message.StartsWith("开始下载", StringComparison.OrdinalIgnoreCase))
            {
                AdvanceDownloadPhase(DownloadPhase.Downloading, "正在下载视频分片...");
            }
            else if (
                message.StartsWith("二进制合并中", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("正在合并", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("开始合并", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("正在混流", StringComparison.OrdinalIgnoreCase) ||
                message.StartsWith("Muxing to ", StringComparison.OrdinalIgnoreCase))
            {
                AdvanceDownloadPhase(DownloadPhase.Merging, "正在合并媒体文件...");
            }
        }

        private void AdvanceDownloadPhase(DownloadPhase phase, string statusText)
        {
            if (_activeOperation != OperationKind.Download || _isCancelling || phase < _downloadPhase)
            {
                return;
            }

            _downloadPhase = phase;
            if (phase == DownloadPhase.Merging)
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
                _progressBar.MarqueeAnimationSpeed = 28;
            }

            if (!string.IsNullOrWhiteSpace(statusText))
            {
                _statusLabel.Text = statusText;
            }
        }

        private void CancelButtonClick(object sender, EventArgs e)
        {
            if (_dependencyInstallInProgress)
            {
                DialogResult dependencyResult = MessageBox.Show(
                    this,
                    "确定要取消当前工具下载吗？",
                    "取消工具下载",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (dependencyResult == DialogResult.Yes)
                {
                    _cancelButton.Enabled = false;
                    _statusLabel.Text = "正在取消工具下载...";
                    if (_dependencyInstallCancellation != null)
                    {
                        _dependencyInstallCancellation.Cancel();
                    }
                }
                return;
            }

            if (_activeProcess == null)
            {
                if (_downloadTaskState != DownloadTaskState.Paused &&
                    _downloadTaskState != DownloadTaskState.Failed)
                {
                    return;
                }

                DialogResult clearResult = MessageBox.Show(
                    this,
                    "确定要放弃当前任务并删除已下载的分片缓存吗？",
                    "清除任务缓存",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (clearResult == DialogResult.Yes)
                {
                    if (!DiscardResumableDownload())
                    {
                        ShowError("任务缓存仍被占用，暂时无法删除。");
                        return;
                    }

                    DeleteImportedPlaylist();
                    _statusLabel.Text = "任务已取消，缓存已清理";
                    AppendLog("[GUI] 已放弃任务并清理分片缓存。");
                    UpdateDownloadActionButtons();
                }
                return;
            }

            DialogResult result = MessageBox.Show(
                this,
                "确定要取消当前下载任务吗？",
                "取消下载",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (result == DialogResult.Yes)
            {
                StopActiveProcess(false);
            }
        }

        private void StopActiveProcess(bool preserveForResume)
        {
            Process process = _activeProcess;
            if (process == null)
            {
                return;
            }

            try
            {
                process.Refresh();
                if (process.HasExited)
                {
                    _statusLabel.Text = "任务正在完成，请稍候...";
                    return;
                }

                _isPausing = preserveForResume;
                _isCancelling = true;
                _cancelButton.Enabled = false;
                _statusLabel.Text = preserveForResume ? "正在暂停下载..." : "正在取消任务...";
                AppendLog(preserveForResume
                    ? "[GUI] 正在暂停下载并保留分片缓存..."
                    : "[GUI] 正在取消任务...");
                UpdateDownloadActionButtons();

                ProcessJob job = TakeProcessJob();
                if (job != null)
                {
                    job.Dispose();
                    return;
                }

                ProcessStartInfo killInfo = new ProcessStartInfo();
                killInfo.FileName = "taskkill.exe";
                killInfo.Arguments = "/PID " + process.Id + " /T /F";
                killInfo.UseShellExecute = false;
                killInfo.CreateNoWindow = true;
                killInfo.WindowStyle = ProcessWindowStyle.Hidden;

                Process killer = Process.Start(killInfo);
                if (killer != null)
                {
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        bool taskkillSucceeded = false;
                        try
                        {
                            if (!killer.WaitForExit(5000))
                            {
                                killer.Kill();
                            }
                            else
                            {
                                taskkillSucceeded = killer.ExitCode == 0;
                            }
                        }
                        catch
                        {
                        }
                        finally
                        {
                            killer.Dispose();
                        }

                        if (!taskkillSucceeded)
                        {
                            try
                            {
                                process.Refresh();
                                if (!process.HasExited)
                                {
                                    process.Kill();
                                }
                            }
                            catch
                            {
                            }
                        }

                        bool stillRunning = false;
                        try
                        {
                            process.Refresh();
                            stillRunning = !process.HasExited;
                        }
                        catch
                        {
                        }

                        if (stillRunning)
                        {
                            SafeBeginInvoke(delegate
                            {
                                _isCancelling = false;
                                _isPausing = false;
                                _cancelButton.Enabled = true;
                                _statusLabel.Text = preserveForResume
                                    ? "暂停失败，任务仍在运行"
                                    : "取消失败，任务仍在运行";
                                AppendLog(preserveForResume
                                    ? "[GUI] 无法暂停下载进程，请重试。"
                                    : "[GUI] 无法终止下载进程，请重试或关闭程序。");
                                UpdateDownloadActionButtons();
                            });
                        }
                    });
                    return;
                }

                process.Kill();
            }
            catch (Exception exception)
            {
                AppendLog("[GUI] " + (preserveForResume ? "暂停" : "取消") +
                    "任务时发生错误：" + exception.Message);
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
            }
        }

        private ProcessJob TakeProcessJob()
        {
            return System.Threading.Interlocked.Exchange(ref _processJob, null);
        }

        private void DisposeProcessJob()
        {
            ProcessJob job = TakeProcessJob();
            if (job != null)
            {
                job.Dispose();
            }
        }

        private bool StopProcessForCleanup(Process process, int timeoutMilliseconds)
        {
            ProcessJob job = TakeProcessJob();
            if (job != null)
            {
                job.Dispose();
            }

            if (process == null || WaitForProcessExit(process, Math.Min(1500, timeoutMilliseconds)))
            {
                return true;
            }

            int processId = -1;
            try
            {
                processId = process.Id;
            }
            catch
            {
            }

            if (processId > 0)
            {
                try
                {
                    ProcessStartInfo killInfo = new ProcessStartInfo();
                    killInfo.FileName = "taskkill.exe";
                    killInfo.Arguments = "/PID " + processId + " /T /F";
                    killInfo.UseShellExecute = false;
                    killInfo.CreateNoWindow = true;
                    killInfo.WindowStyle = ProcessWindowStyle.Hidden;

                    using (Process killer = Process.Start(killInfo))
                    {
                        if (killer != null)
                        {
                            if (!killer.WaitForExit(Math.Min(2500, timeoutMilliseconds)))
                            {
                                killer.Kill();
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            if (WaitForProcessExit(process, Math.Min(1000, timeoutMilliseconds)))
            {
                return true;
            }

            try
            {
                process.Kill();
            }
            catch
            {
            }

            return WaitForProcessExit(process, Math.Min(1000, timeoutMilliseconds));
        }

        private static bool WaitForProcessExit(Process process, int timeoutMilliseconds)
        {
            try
            {
                process.Refresh();
                return process.HasExited || process.WaitForExit(Math.Max(0, timeoutMilliseconds));
            }
            catch
            {
                return false;
            }
        }

        private void SetRunningState(bool isRunning)
        {
            _urlTextBox.Enabled = !isRunning;
            _saveDirectoryTextBox.Enabled = !isRunning;
            _fileNameTextBox.Enabled = !isRunning;
            _downloaderPathTextBox.Enabled = !isRunning;
            _ffmpegPathTextBox.Enabled = !isRunning;
            _pasteButton.Enabled = !isRunning;
            _browseDirectoryButton.Enabled = !isRunning;
            _autoNameButton.Enabled = !isRunning;
            _browseDownloaderButton.Enabled = !isRunning;
            _browseFfmpegButton.Enabled = !isRunning;
            _detectToolsButton.Enabled = !isRunning;
            _muxToMp4CheckBox.Enabled = !isRunning;
            _openFolderWhenDoneCheckBox.Enabled = !isRunning;
            SetSecondaryButtonEnabled(_keyOptionsButton, !isRunning);
            SetSecondaryButtonEnabled(_convertFileButton, !isRunning);
            SetSecondaryButtonEnabled(_captureButton, !isRunning);
            _startButton.Enabled = !isRunning;
            _cancelButton.Enabled = isRunning;

            if (isRunning)
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
                _progressBar.MarqueeAnimationSpeed = 28;
            }
            else
            {
                _progressBar.MarqueeAnimationSpeed = 0;
                _progressBar.Style = ProgressBarStyle.Blocks;
            }

            UpdateDownloadActionButtons();
        }

        private void UpdateDownloadActionButtons()
        {
            if (_startButton == null || _cancelButton == null)
            {
                return;
            }

            if (_dependencyInstallInProgress || _toolDetectionInProgress)
            {
                return;
            }

            if (_activeProcess != null)
            {
                if (_activeOperation == OperationKind.Download)
                {
                    _startButton.Enabled = !_isCancelling;
                    _startButton.Text = _isPausing ? "正在暂停..." : "暂停";
                    _cancelButton.Enabled = !_isCancelling;
                    _cancelButton.Text = "取消";
                }
                else
                {
                    _startButton.Enabled = false;
                    _cancelButton.Enabled = true;
                    _cancelButton.Text = "取消";
                }
                return;
            }

            _startButton.Enabled = true;
            _cancelButton.Text = "取消";
            switch (_downloadTaskState)
            {
                case DownloadTaskState.Paused:
                    _startButton.Text = "继续";
                    _cancelButton.Text = "清除缓存";
                    _cancelButton.Enabled = true;
                    break;
                case DownloadTaskState.Failed:
                    _startButton.Text = "重试下载";
                    _cancelButton.Text = "清除缓存";
                    _cancelButton.Enabled = true;
                    break;
                case DownloadTaskState.Completed:
                    _startButton.Text = "完成";
                    _cancelButton.Enabled = false;
                    break;
                default:
                    _startButton.Text = string.IsNullOrWhiteSpace(_manualHlsKey)
                        ? "开始下载"
                        : "使用密钥下载";
                    _cancelButton.Enabled = false;
                    break;
            }
        }

        private void SetToolDetectionState(bool isDetecting)
        {
            bool enabled = !isDetecting &&
                !_dependencyInstallInProgress &&
                _activeProcess == null;
            _downloaderPathTextBox.Enabled = enabled;
            _ffmpegPathTextBox.Enabled = enabled;
            _browseDownloaderButton.Enabled = enabled;
            _browseFfmpegButton.Enabled = enabled;
            _detectToolsButton.Enabled = enabled;
            _startButton.Enabled = enabled;
            SetSecondaryButtonEnabled(_convertFileButton, enabled);
            UpdateDownloadActionButtons();
        }

        private void SetSecondaryButtonEnabled(Button button, bool enabled)
        {
            bool emphasizeKey =
                ReferenceEquals(button, _keyOptionsButton) &&
                !string.IsNullOrWhiteSpace(_manualHlsKey);
            button.Enabled = enabled;
            button.BackColor = enabled ? SurfaceColor : DisabledControlColor;
            button.ForeColor = enabled
                ? (emphasizeKey ? AccentColor : TextColor)
                : DisabledTextColor;
            button.FlatAppearance.BorderColor = enabled
                ? (emphasizeKey ? AccentColor : BorderColor)
                : DisabledBorderColor;
            button.Cursor = enabled ? Cursors.Hand : Cursors.Default;
        }

        private void OpenSaveDirectory()
        {
            string directory = _saveDirectoryTextBox.Text.Trim();
            if (directory.Length == 0)
            {
                directory = ToolLocator.GetDefaultSaveDirectory();
            }

            OpenDirectory(directory);
        }

        private void OpenDirectory(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = "explorer.exe";
                startInfo.Arguments = CommandLine.QuoteArgument(directory);
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                ShowError("无法打开保存目录：" + exception.Message);
            }
        }

        private void CaptureButtonClick(object sender, EventArgs e)
        {
            if (!WebView2Runtime.EnsureAvailable(this))
            {
                return;
            }

            string initialUrl = _urlTextBox.Text.Trim();
            if (!initialUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !initialUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                initialUrl = string.Empty;
            }

            using (CaptureBrowserForm form = new CaptureBrowserForm(initialUrl))
            {
                if (form.ShowDialog(this) != DialogResult.OK || form.Result == null)
                {
                    return;
                }

                CaptureResult result = form.Result;
                _capturedHeaders = result.Headers;
                if (PlaylistInput.LooksLikePlaylistContent(result.PlaylistContent))
                {
                    ImportPlaylistContent(
                        result.PlaylistContent,
                        result.IsBlob ? "网页捕获的 Blob" : "网页捕获的 m3u8 正文");
                    return;
                }

                _urlTextBox.Text = result.Url;
                _fileNameWasEdited = false;
                ApplyAutomaticName(false);
            }
        }

        private void CopyLogButtonClick(object sender, EventArgs e)
        {
            try
            {
                if (_logTextBox.TextLength > 0)
                {
                    Clipboard.SetText(_logTextBox.Text);
                    _statusLabel.Text = "日志已复制";
                }
            }
            catch (Exception exception)
            {
                ShowError("无法复制日志：" + exception.Message);
            }
        }

        private void ClearLog()
        {
            string ignored;
            while (_pendingLogLines.TryDequeue(out ignored))
            {
            }

            _logTextBox.Clear();
        }

        private void MainFormFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_dependencyInstallInProgress)
            {
                if (_closeRequestedDuringDependencyInstall)
                {
                    e.Cancel = true;
                    return;
                }

                DialogResult dependencyResult = MessageBox.Show(
                    this,
                    "工具仍在下载。关闭窗口会取消下载，确定继续吗？",
                    "工具下载中",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (dependencyResult != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }

                _closeRequestedDuringDependencyInstall = true;
                _cancelButton.Enabled = false;
                _statusLabel.Text = "正在取消工具下载并清理...";
                if (_dependencyInstallCancellation != null)
                {
                    _dependencyInstallCancellation.Cancel();
                }
                e.Cancel = true;
                return;
            }

            Process processToStop = _activeProcess;
            if (processToStop != null)
            {
                try
                {
                    processToStop.Refresh();
                    if (processToStop.HasExited)
                    {
                        int completedExitCode = processToStop.ExitCode;
                        FinishDownload(processToStop, completedExitCode);
                        processToStop = _activeProcess;
                    }
                }
                catch
                {
                }
            }
            bool stoppingRunningDownload = processToStop != null &&
                _activeOperation == OperationKind.Download;
            bool cancelWasAlreadyRequested = stoppingRunningDownload &&
                _isCancelling && !_isPausing;
            if (processToStop != null)
            {
                string closeMessage = cancelWasAlreadyRequested
                    ? "下载正在取消。关闭窗口会完成取消并清理分片缓存。\r\n\r\n确定关闭吗？"
                    : (stoppingRunningDownload
                        ? "下载仍在运行。关闭窗口会暂停任务并保留分片缓存，下次打开新版可继续。\r\n\r\n确定关闭吗？"
                        : "转换仍在运行。关闭窗口会取消转换，确定继续吗？");
                DialogResult result = MessageBox.Show(
                    this,
                    closeMessage,
                    "任务正在运行",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }

                if (stoppingRunningDownload && !cancelWasAlreadyRequested)
                {
                    string persistError;
                    if (!TryPersistDownloadResume(
                        _resumableRequest,
                        DownloadResumeState.Paused,
                        out persistError))
                    {
                        MessageBox.Show(
                            this,
                            "无法保存重启续传信息，因此窗口暂不关闭。\r\n\r\n" + persistError,
                            "无法保留下载任务",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        e.Cancel = true;
                        return;
                    }

                    _downloadTaskState = DownloadTaskState.Paused;
                    _isPausing = true;
                }

                _isCancelling = true;
                _activeProcess = null;
                if (!StopProcessForCleanup(processToStop, 5000))
                {
                    _activeProcess = processToStop;
                    _isCancelling = false;
                    if (stoppingRunningDownload && !cancelWasAlreadyRequested)
                    {
                        _isPausing = false;
                        _downloadTaskState = DownloadTaskState.Running;
                        PersistDownloadResumeOrLog(DownloadResumeState.Running);
                    }
                    _cancelButton.Enabled = true;
                    _statusLabel.Text = "无法结束任务，窗口保持打开";
                    MessageBox.Show(
                        this,
                        "无法结束下载器或 FFmpeg 进程。为避免后台任务继续运行，窗口暂不关闭。\r\n\r\n请稍后重试，或在任务管理器中结束相关进程。",
                        "无法关闭任务",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    e.Cancel = true;
                    return;
                }

                WaitForDownloadOutputCapture(processToStop);
                ClearDownloadOutputCapture(processToStop);
                try
                {
                    processToStop.Dispose();
                }
                catch
                {
                }
            }

            bool preserveDownloadForRestart =
                _resumableRequest != null &&
                !string.IsNullOrWhiteSpace(_downloadTemporaryDirectory) &&
                Directory.Exists(_downloadTemporaryDirectory) &&
                (_downloadTaskState == DownloadTaskState.Paused ||
                 _downloadTaskState == DownloadTaskState.Failed);
            if (preserveDownloadForRestart && !stoppingRunningDownload &&
                _resumeTaskTouchedThisSession)
            {
                DownloadResumeState resumeState =
                    _downloadTaskState == DownloadTaskState.Failed
                        ? DownloadResumeState.Failed
                        : DownloadResumeState.Paused;
                if (!PersistDownloadResumeOrLog(resumeState) &&
                    _downloadResumeManifest == null)
                {
                    MessageBox.Show(
                        this,
                        "无法保存重启续传信息，因此窗口暂不关闭。请先重试或点击“清除缓存”。",
                        "无法保留下载任务",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    e.Cancel = true;
                    return;
                }
            }

            if (_logFlushTimer != null)
            {
                _logFlushTimer.Stop();
                _logFlushTimer.Dispose();
                _logFlushTimer = null;
            }

            StopActiveMediaProxy();
            DeleteTemporarySecrets();
            DeleteImportedPlaylist(false);
            if (preserveDownloadForRestart)
            {
                ReleaseDownloadResumeLease();
            }
            else
            {
                DeleteDownloadTemporaryDirectory();
            }
            DeleteConversionTemporaryOutput();
            SaveCurrentSettings();
        }

        private bool DeleteDownloadTemporaryDirectory()
        {
            string path = _downloadTemporaryDirectory;
            if (string.IsNullOrWhiteSpace(path))
            {
                ReleaseDownloadResumeLease();
                _downloadResumeManifest = null;
                return true;
            }

            ReleaseDownloadResumeLease();
            if (!Directory.Exists(path))
            {
                _downloadTemporaryDirectory = null;
                _downloadResumeManifest = null;
                return true;
            }

            string errorMessage;
            if (DownloadResumeStore.TryDiscard(path, out errorMessage))
            {
                _downloadTemporaryDirectory = null;
                _downloadResumeManifest = null;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                AppendLog("[GUI] 缓存清理尚未完成：" + errorMessage);
            }

            return false;
        }

        private void SaveCurrentSettings()
        {
            _settings.SaveDirectory = _saveDirectoryTextBox.Text.Trim();
            _settings.DownloaderPath = _downloaderPathTextBox.Text.Trim();
            _settings.FfmpegPath = _ffmpegPathTextBox.Text.Trim();
            _settings.MuxToMp4 = _muxToMp4CheckBox.Checked;
            _settings.OpenFolderWhenDone = _openFolderWhenDoneCheckBox.Checked;
            SettingsStore.Save(_settings);
        }

        private static void VerifyDirectoryIsWritable(string directory)
        {
            string testFile = Path.Combine(directory, ".m3u8-gui-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream stream = new FileStream(
                    testFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose))
                {
                    stream.WriteByte(0);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(testFile))
                    {
                        File.Delete(testFile);
                    }
                }
                catch
                {
                }
            }
        }

        private static string[] FindExistingOutputs(string directory, string fileName)
        {
            try
            {
                List<string> matches = new List<string>();
                string[] mediaExtensions =
                {
                    ".mp4", ".m4v", ".ts", ".m2ts", ".mkv", ".webm", ".mov", ".m4a",
                    ".aac", ".mp3", ".ac3", ".eac3", ".opus", ".ogg", ".wav", ".flv"
                };
                foreach (string extension in mediaExtensions)
                {
                    string path = Path.Combine(directory, fileName + extension);
                    if (File.Exists(path))
                    {
                        matches.Add(path);
                    }
                }

                return matches.ToArray();
            }
            catch
            {
                return new string[0];
            }
        }

        private static Dictionary<string, FileStamp> CaptureFileState(string directory)
        {
            Dictionary<string, FileStamp> result = new Dictionary<string, FileStamp>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string path in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    FileInfo info = new FileInfo(path);
                    result[path] = new FileStamp(info.Length, info.LastWriteTimeUtc);
                }
            }
            catch
            {
            }

            return result;
        }

        private string FindChangedOutput(string directory)
        {
            try
            {
                HashSet<string> mediaExtensions = new HashSet<string>(
                    new[]
                    {
                        ".mp4", ".m4v", ".ts", ".m2ts", ".mkv", ".webm", ".mov", ".m4a",
                        ".aac", ".mp3", ".ac3", ".eac3", ".opus", ".ogg", ".wav", ".flv"
                    },
                    StringComparer.OrdinalIgnoreCase);

                FileInfo newest = null;
                foreach (string path in Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    FileInfo info = new FileInfo(path);
                    if (!mediaExtensions.Contains(info.Extension))
                    {
                        continue;
                    }

                    FileStamp before;
                    bool changed = _filesBeforeDownload == null ||
                                   !_filesBeforeDownload.TryGetValue(path, out before) ||
                                   before.Length != info.Length ||
                                   before.LastWriteTimeUtc != info.LastWriteTimeUtc;
                    if (!changed || info.LastWriteTimeUtc < _downloadStartedUtc.AddSeconds(-2))
                    {
                        continue;
                    }

                    string outputBaseName = Path.GetFileNameWithoutExtension(info.Name);
                    if (!OutputNameMatchesExpected(outputBaseName, _expectedOutputBaseName))
                    {
                        continue;
                    }

                    if (newest == null || info.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                    {
                        newest = info;
                    }
                }

                return newest == null ? null : newest.FullName;
            }
            catch
            {
                return null;
            }
        }

        private static bool OutputNameMatchesExpected(string outputName, string expectedName)
        {
            if (string.IsNullOrWhiteSpace(outputName) || string.IsNullOrWhiteSpace(expectedName))
            {
                return false;
            }

            if (string.Equals(outputName, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!outputName.StartsWith(expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string suffix = outputName.Substring(expectedName.Length);
            return Regex.IsMatch(
                suffix,
                @"^_(?:Vid|Aud|Sub|Video|Audio|Subtitle|\d+)(?:_|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                   Regex.IsMatch(suffix, @"^ \(\d+\)$", RegexOptions.CultureInvariant);
        }

        private void ShowValidationError(string message, Control control)
        {
            MessageBox.Show(this, message, "请检查输入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (control != null && control.CanFocus)
            {
                control.Focus();
            }
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, "M3U8 视频下载器", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void SafeBeginInvoke(MethodInvoker action)
        {
            try
            {
                if (!IsDisposed && !Disposing && IsHandleCreated)
                {
                    BeginInvoke(action);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private sealed class DownloadRequest
        {
            public string Input;
            public string SaveDirectory;
            public string FileName;
            public string DownloaderPath;
            public string FfmpegPath;
            public bool MuxToMp4;
            public string HlsKey;
            public string HlsIv;
            public bool InputIsImportedPlaylist;
            public string ImportedPlaylistContent;
            public MediaRequestHeaders CapturedHeaders;
        }

        private enum OperationKind
        {
            Download,
            ConvertFile
        }

        private enum DownloadTaskState
        {
            Idle,
            Running,
            Paused,
            Failed,
            Completed
        }

        private enum DownloadPhase
        {
            Starting,
            Parsing,
            Downloading,
            Merging
        }

        private sealed class FileStamp
        {
            public readonly long Length;
            public readonly DateTime LastWriteTimeUtc;

            public FileStamp(long length, DateTime lastWriteTimeUtc)
            {
                Length = length;
                LastWriteTimeUtc = lastWriteTimeUtc;
            }
        }

        private static class NativeMethods
        {
            private const int EmSetCueBanner = 0x1501;

            [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            private static extern IntPtr SendMessage(
                IntPtr windowHandle,
                int message,
                IntPtr wideParameter,
                string longParameter);

            public static void SetCueBanner(TextBox textBox, string text)
            {
                try
                {
                    SendMessage(textBox.Handle, EmSetCueBanner, IntPtr.Zero, text);
                }
                catch
                {
                }
            }
        }
    }
}
