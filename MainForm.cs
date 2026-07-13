using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

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

        private readonly ToolTip _toolTip;
        private readonly UserSettings _settings;
        private readonly ConcurrentQueue<string> _pendingLogLines = new ConcurrentQueue<string>();

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

        private CheckBox _muxToMp4CheckBox;
        private CheckBox _openFolderWhenDoneCheckBox;
        private Label _toolStatusLabel;
        private Label _statusLabel;
        private ProgressBar _progressBar;
        private Timer _logFlushTimer;

        private Process _activeProcess;
        private ProcessJob _processJob;
        private bool _isCancelling;
        private bool _updatingAutoName;
        private bool _fileNameWasEdited;
        private string _lastAutoName = string.Empty;
        private DateTime _downloadStartedUtc;
        private Dictionary<string, FileStamp> _filesBeforeDownload;
        private string _lastOutputPath;
        private string _expectedOutputBaseName;
        private string _importedPlaylistPath;
        private string _manualHlsKey = string.Empty;
        private string _manualHlsIv = string.Empty;
        private string _temporaryHlsKeyPath;
        private string _temporaryHlsIvPath;
        private readonly List<string> _secretRedactionValues = new List<string>();
        private OperationKind _activeOperation;
        private string _activeOperationDirectory;
        private string _conversionFinalOutputPath;
        private string _conversionTemporaryOutputPath;

        public MainForm()
        {
            _toolTip = new ToolTip();
            _toolTip.AutoPopDelay = 8000;
            _toolTip.InitialDelay = 450;
            _toolTip.ReshowDelay = 100;

            _settings = SettingsStore.Load();

            InitializeForm();
            BuildInterface();
            WireEvents();
            ApplySettings();
            ApplyCueBanners();
            UpdateToolStatus();
            UpdateKeyState();
            CleanupOldImportedPlaylists(GetImportedPlaylistDirectory());
            SecretFileStore.CleanupOldFiles();

            _logFlushTimer = new Timer();
            _logFlushTimer.Interval = 100;
            _logFlushTimer.Tick += delegate { FlushPendingLogLines(); };
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

            return controlsExist && namingWorks && layoutIsStable;
        }

        private void InitializeForm()
        {
            SuspendLayout();
            Text = "M3U8 视频下载器";
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

            primaryActions.Controls.Add(_startButton);
            primaryActions.Controls.Add(_cancelButton);
            primaryActions.Controls.Add(_openFolderButton);
            primaryActions.Controls.Add(_keyOptionsButton);
            primaryActions.Controls.Add(_convertFileButton);

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
            _downloaderPathTextBox.TextChanged += delegate { UpdateToolStatus(); };
            _ffmpegPathTextBox.TextChanged += delegate { UpdateToolStatus(); };

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
            FormClosing += MainFormFormClosing;
        }

        private void ApplySettings()
        {
            _saveDirectoryTextBox.Text = _settings.SaveDirectory ?? string.Empty;
            _downloaderPathTextBox.Text = ToolLocator.FindDownloader(_settings.DownloaderPath);
            _ffmpegPathTextBox.Text = ToolLocator.FindFfmpeg(
                _settings.FfmpegPath,
                _downloaderPathTextBox.Text);
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
                _statusLabel.Text = "已从剪贴板导入播放列表";
                AppendLog("[GUI] 已导入剪贴板中的播放列表：" + path);
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
            string path = _importedPlaylistPath;
            _importedPlaylistPath = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                if (_urlTextBox != null &&
                    string.Equals(_urlTextBox.Text.Trim(), path, StringComparison.OrdinalIgnoreCase))
                {
                    _urlTextBox.Clear();
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
                "N_m3u8DL-RE|N_m3u8DL-RE.exe|可执行程序|*.exe");
            if (path != null)
            {
                _downloaderPathTextBox.Text = path;
            }
        }

        private void BrowseFfmpegButtonClick(object sender, EventArgs e)
        {
            string path = BrowseExecutable(
                "选择 ffmpeg.exe",
                "FFmpeg|ffmpeg.exe|可执行程序|*.exe");
            if (path != null)
            {
                _ffmpegPathTextBox.Text = path;
            }
        }

        private string BrowseExecutable(string title, string filter)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = title;
                dialog.Filter = filter;
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private void DetectToolsButtonClick(object sender, EventArgs e)
        {
            string downloader = ToolLocator.FindDownloader(_downloaderPathTextBox.Text);
            string ffmpeg = ToolLocator.FindFfmpeg(_ffmpegPathTextBox.Text, downloader);
            _downloaderPathTextBox.Text = downloader;
            _ffmpegPathTextBox.Text = ffmpeg;
            UpdateToolStatus();

            if (ToolLocator.IsUsableExecutable(downloader) && ToolLocator.IsUsableExecutable(ffmpeg))
            {
                _statusLabel.Text = "已找到下载程序和 FFmpeg";
            }
            else
            {
                _statusLabel.Text = "有工具未找到，请使用“浏览...”指定路径";
            }
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
                _toolStatusLabel.Text = "请检查标红的工具路径";
                _toolStatusLabel.ForeColor = DangerColor;
            }
        }

        private void StartButtonClick(object sender, EventArgs e)
        {
            DownloadRequest request;
            if (!TryCreateRequest(out request))
            {
                return;
            }

            string[] conflicts = FindExistingOutputs(request.SaveDirectory, request.FileName);
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

            SaveCurrentSettings();
            StartDownload(request);
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
            _keyOptionsButton.ForeColor = hasKey ? AccentColor : TextColor;
            _keyOptionsButton.FlatAppearance.BorderColor = hasKey ? AccentColor : BorderColor;
            _startButton.Text = hasKey ? "使用密钥下载" : "开始下载";
            _toolTip.SetToolTip(
                _keyOptionsButton,
                hasKey ? "已设置手动 HLS 密钥；不会保存到配置文件" : "设置可选的 HLS AES-128 密钥和 IV");
        }

        private void ConvertFileButtonClick(object sender, EventArgs e)
        {
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
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            Process process = new Process();
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;
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
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("FFmpeg 没有成功启动。");
                }
                processStarted = true;

                ProcessJob job = ProcessJob.TryCreateKillOnClose();
                if (job != null)
                {
                    if (job.AddProcess(process))
                    {
                        _processJob = job;
                    }
                    else
                    {
                        job.Dispose();
                    }
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _statusLabel.Text = "正在无损转换为 MP4...";
            }
            catch (Exception exception)
            {
                AppendLog("[GUI] FFmpeg 启动失败：" + exception.Message);
                if (processStarted)
                {
                    StopProcessForCleanup(process, 5000);
                }

                _activeProcess = null;
                if (_processJob != null)
                {
                    _processJob.Dispose();
                    _processJob = null;
                }

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

        private void StartDownload(DownloadRequest request)
        {
            string keyArgument;
            string ivArgument;
            if (!PrepareTemporarySecrets(request.HlsKey, request.HlsIv, out keyArgument, out ivArgument))
            {
                return;
            }

            List<string> arguments = new List<string>();
            arguments.Add(request.Input);
            arguments.Add("--save-dir");
            arguments.Add(request.SaveDirectory);
            arguments.Add("--save-name");
            arguments.Add(request.FileName);
            arguments.Add("--auto-select");
            arguments.Add("--ffmpeg-binary-path");
            arguments.Add(request.FfmpegPath);
            arguments.Add("--ui-language");
            arguments.Add("zh-CN");
            arguments.Add("--no-ansi-color");
            arguments.Add("--no-log");
            arguments.Add("--write-meta-json");
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

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = request.DownloaderPath;
            startInfo.Arguments = CommandLine.JoinArguments(arguments);
            startInfo.WorkingDirectory = request.SaveDirectory;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;

            string ffmpegDirectory = Path.GetDirectoryName(request.FfmpegPath);
            if (!string.IsNullOrWhiteSpace(ffmpegDirectory))
            {
                string existingPath = startInfo.EnvironmentVariables["PATH"] ?? string.Empty;
                startInfo.EnvironmentVariables["PATH"] = ffmpegDirectory + ";" + existingPath;
            }

            Process process = new Process();
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += ProcessOutputDataReceived;
            process.ErrorDataReceived += ProcessErrorDataReceived;
            process.Exited += ProcessExited;

            _isCancelling = false;
            _activeOperation = OperationKind.Download;
            _activeOperationDirectory = request.SaveDirectory;
            _lastOutputPath = null;
            _expectedOutputBaseName = request.FileName;
            _downloadStartedUtc = DateTime.UtcNow;
            _filesBeforeDownload = CaptureFileState(request.SaveDirectory);
            _activeProcess = process;
            SetRunningState(true);

            AppendLog(string.Empty);
            AppendLog("[GUI] 开始任务：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            AppendLog("[GUI] 保存目录：" + request.SaveDirectory);
            AppendLog("[GUI] 文件名称：" + request.FileName);
            _statusLabel.Text = "正在启动下载程序...";

            bool processStarted = false;
            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("下载程序没有成功启动。");
                }
                processStarted = true;

                ProcessJob job = ProcessJob.TryCreateKillOnClose();
                if (job != null)
                {
                    if (job.AddProcess(process))
                    {
                        _processJob = job;
                    }
                    else
                    {
                        job.Dispose();
                    }
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _statusLabel.Text = "正在解析视频信息...";
            }
            catch (Exception exception)
            {
                AppendLog("[GUI] 启动失败：" + exception.Message);
                if (processStarted)
                {
                    StopProcessForCleanup(process, 5000);
                }

                _activeProcess = null;
                if (_processJob != null)
                {
                    _processJob.Dispose();
                    _processJob = null;
                }
                try
                {
                    process.Dispose();
                }
                catch
                {
                }

                SetRunningState(false);
                _statusLabel.Text = "启动失败";
                DeleteImportedPlaylist();
                DeleteTemporarySecrets();
                ShowError("无法启动下载程序：\r\n\r\n" + exception.Message);
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
            try
            {
                completedProcess.WaitForExit();
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

            bool wasCancelled = _isCancelling;
            OperationKind completedOperation = _activeOperation;
            bool isConversion = completedOperation == OperationKind.ConvertFile;
            string saveDirectory = string.IsNullOrWhiteSpace(_activeOperationDirectory)
                ? _saveDirectoryTextBox.Text.Trim()
                : _activeOperationDirectory;
            _activeProcess = null;
            _isCancelling = false;
            _activeOperationDirectory = null;
            while (!_pendingLogLines.IsEmpty)
            {
                FlushPendingLogLines();
            }

            if (_processJob != null)
            {
                _processJob.Dispose();
                _processJob = null;
            }

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
            }

            _lastOutputPath = output;
            if (!isConversion)
            {
                DeleteImportedPlaylist();
                DeleteTemporarySecrets();
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

            if (wasCancelled && !(exitCode == 0 && output != null))
            {
                _progressBar.Value = 0;
                _statusLabel.Text = "任务已取消";
                AppendLog("[GUI] 任务已取消。");
                return;
            }

            if (wasCancelled && exitCode == 0 && output != null)
            {
                AppendLog("[GUI] 取消请求到达时任务已经完成，保留下载结果。");
            }

            if (exitCode == 0 && output != null)
            {
                string completedText = isConversion ? "转换完成" : "下载完成";
                _progressBar.Value = 100;
                _statusLabel.Text = completedText + "：" + Path.GetFileName(output);
                AppendLog("[GUI] " + completedText + "：" + output);

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
                _progressBar.Value = 100;
                _statusLabel.Text = "任务结束，请打开保存目录查看结果";
                AppendLog("[GUI] 进程已正常结束，但没有识别到新生成的媒体文件。");
                MessageBox.Show(
                    this,
                    "任务已经结束，但程序没有识别到新生成的媒体文件。请打开保存目录并查看运行日志。",
                    "请检查结果",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            _progressBar.Value = 0;
            string failureText = isConversion ? "转换失败" : "下载失败";
            _statusLabel.Text = failureText + "，退出代码 " + exitCode;
            AppendLog("[GUI] " + failureText + "，退出代码：" + exitCode);
            MessageBox.Show(
                this,
                failureText + "，退出代码：" + exitCode + "。\r\n\r\n请查看运行日志中的最后几行。",
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

            if (_logTextBox.TextLength > 1000000)
            {
                _logTextBox.Select(0, 200000);
                _logTextBox.SelectedText = string.Empty;
            }

            _logTextBox.AppendText(batch.ToString());
            _logTextBox.SelectionStart = _logTextBox.TextLength;
            _logTextBox.ScrollToCaret();
            foreach (string statusLine in statusLines)
            {
                UpdateStatusFromLog(statusLine);
            }
        }

        private void AppendLogCore(string line)
        {
            if (_logTextBox.TextLength > 1000000)
            {
                _logTextBox.Select(0, 200000);
                _logTextBox.SelectedText = string.Empty;
            }

            _logTextBox.AppendText((line ?? string.Empty) + Environment.NewLine);
            _logTextBox.SelectionStart = _logTextBox.TextLength;
            _logTextBox.ScrollToCaret();
            UpdateStatusFromLog(line);
        }

        private void UpdateStatusFromLog(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || _activeProcess == null)
            {
                return;
            }

            if (line.IndexOf("开始下载", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _statusLabel.Text = "正在下载视频分片...";
            }
            else if (line.IndexOf("合并", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     line.IndexOf("混流", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     line.IndexOf("mux", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _statusLabel.Text = "正在合并媒体文件...";
            }
            else if (line.IndexOf("正在解析", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _statusLabel.Text = "正在解析媒体信息...";
            }
        }

        private void CancelButtonClick(object sender, EventArgs e)
        {
            if (_activeProcess == null)
            {
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
                CancelActiveProcess();
            }
        }

        private void CancelActiveProcess()
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

                _isCancelling = true;
                _cancelButton.Enabled = false;
                _statusLabel.Text = "正在取消任务...";
                AppendLog("[GUI] 正在取消任务...");

                ProcessJob job = _processJob;
                _processJob = null;
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
                                _cancelButton.Enabled = true;
                                _statusLabel.Text = "取消失败，任务仍在运行";
                                AppendLog("[GUI] 无法终止下载进程，请重试或关闭程序。");
                            });
                        }
                    });
                    return;
                }

                process.Kill();
            }
            catch (Exception exception)
            {
                AppendLog("[GUI] 取消任务时发生错误：" + exception.Message);
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

        private bool StopProcessForCleanup(Process process, int timeoutMilliseconds)
        {
            ProcessJob job = _processJob;
            _processJob = null;
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
                return true;
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
            _keyOptionsButton.Enabled = !isRunning;
            _convertFileButton.Enabled = !isRunning;
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
            Process processToStop = _activeProcess;
            if (processToStop != null)
            {
                DialogResult result = MessageBox.Show(
                    this,
                    "任务仍在运行。关闭窗口会取消任务，确定继续吗？",
                    "任务正在运行",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }

                _isCancelling = true;
                _activeProcess = null;
                if (!StopProcessForCleanup(processToStop, 5000))
                {
                    _activeProcess = processToStop;
                    _isCancelling = false;
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

                try
                {
                    processToStop.Dispose();
                }
                catch
                {
                }
            }

            if (_logFlushTimer != null)
            {
                _logFlushTimer.Stop();
                _logFlushTimer.Dispose();
                _logFlushTimer = null;
            }

            DeleteImportedPlaylist();
            DeleteTemporarySecrets();
            DeleteConversionTemporaryOutput();
            SaveCurrentSettings();
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
        }

        private enum OperationKind
        {
            Download,
            ConvertFile
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
