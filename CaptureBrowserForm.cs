using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace M3u8DownloaderGui
{
    // The result handed back to MainForm when the user picks a stream to download.
    internal sealed class CaptureResult
    {
        public string Url;
        public MediaRequestHeaders Headers;
        public string PlaylistContent;
        public bool IsBlob;
    }

    // A single captured stream candidate, tracked in the list.
    internal sealed class CaptureCandidate
    {
        public string Url;
        public MediaRequestHeaders Headers;
        public SizeProbeResult Size;
        public bool SizeProbeStarted;
        public bool SizeProbeDone;
        public DateTime CaptureTime;
        public string PlaylistContent;
        public string PlaylistBaseUrl;
        public HlsPlaylistInspection Inspection;
        public long PlaylistBandwidth;
        public string SizeProbeError;
        public bool IsBlob;
        public bool Expanded;
        public bool HasPrivateTokenTag;
        public bool SegmentCookieLookupStarted;
        public string LastObservedResourceUrl;
        public readonly HashSet<string> ObservedSegmentUrls =
            new HashSet<string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> ObservedResourceUrls =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal sealed class CaptureCandidateRowTag
    {
        public string CandidateUrl;
        public bool IsDetail;
    }

    internal sealed class CaptureResourceBinding
    {
        public string CandidateUrl;
        public string ExpectedUrl;
        public HlsPlaylistResource Resource;
    }

    internal sealed class RecentResourceRequest
    {
        public string Url;
        public MediaRequestHeaders Headers;
        public DateTime CapturedUtc;
    }

    // Embedded browser window that sniffs .m3u8 requests as the user browses,
    // lists them as candidates with a probed total size, lets the user preview a
    // stream, and returns the chosen stream (with its captured headers) to the
    // caller. Requires the WebView2 Runtime (checked before this form is shown).
    internal sealed class CaptureBrowserForm : Form
    {
        private const int RecentResourceRequestLimit = 512;
        private static readonly TimeSpan RecentResourceRequestLifetime = TimeSpan.FromSeconds(30);

        private const string BlobCaptureScript =
            @"(function () {
                if (window.__nM3u8DlReBlobCaptureInstalled) return;
                Object.defineProperty(window, '__nM3u8DlReBlobCaptureInstalled', { value: true });
                var originalCreateObjectURL = window.URL && window.URL.createObjectURL;
                if (typeof originalCreateObjectURL !== 'function') return;

                function encodeUtf8Base64(text) {
                    var bytes = new TextEncoder().encode(text);
                    var chunks = [];
                    for (var offset = 0; offset < bytes.length; offset += 32768) {
                        chunks.push(String.fromCharCode.apply(null, bytes.subarray(offset, offset + 32768)));
                    }
                    return btoa(chunks.join(''));
                }

                function inspectBlob(value, blobUrl) {
                    if (!(value instanceof Blob)) return;
                    value.text().then(function (text) {
                        var probe = text.replace(/^[\uFEFF\s]+/, '');
                        if (probe.substring(0, 7).toUpperCase() !== '#EXTM3U') return;
                        var pageUrl = String(location.href || '').replace(/[\r\n]/g, '');
                        var baseUrl = String(document.baseURI || pageUrl || '').replace(/[\r\n]/g, '');
                        var userAgent = String(navigator.userAgent || '').replace(/[\r\n]/g, '');
                        window.chrome.webview.postMessage(
                            'N_M3U8DL_RE_BLOB_PLAYLIST_V2\n' + blobUrl + '\n' + pageUrl + '\n' +
                            baseUrl + '\n' + userAgent + '\n' + encodeUtf8Base64(text));
                    }).catch(function () { });
                }

                window.URL.createObjectURL = function (value) {
                    var blobUrl = originalCreateObjectURL.apply(this, arguments);
                    inspectBlob(value, blobUrl);
                    return blobUrl;
                };
            })();";

        private static readonly Color BackgroundColor = Color.FromArgb(246, 248, 249);
        private static readonly Color AccentColor = Color.FromArgb(20, 122, 88);
        private static readonly Color MutedTextColor = Color.FromArgb(91, 103, 112);

        private readonly WebView2 _webView;
        private readonly TextBox _addressBox;
        private readonly Button _goButton;
        private readonly ListView _candidateList;
        private readonly Button _useButton;
        private readonly Button _previewButton;
        private readonly Button _clearButton;
        private readonly Button _cancelButton;
        private readonly Label _hintLabel;

        private readonly Dictionary<string, CaptureCandidate> _candidates =
            new Dictionary<string, CaptureCandidate>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _variantBandwidths =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<CaptureResourceBinding>> _resourceExactIndex =
            new Dictionary<string, List<CaptureResourceBinding>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CaptureResourceBinding>> _resourcePathIndex =
            new Dictionary<string, List<CaptureResourceBinding>>(StringComparer.Ordinal);
        private readonly Queue<RecentResourceRequest> _recentResourceRequests =
            new Queue<RecentResourceRequest>();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        private CaptureResult _result;
        private bool _coreReady;
        private int _captureGeneration;

        public CaptureBrowserForm(string initialUrl)
        {
            Text = "从网页捕获视频";
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(900, 600);
            Size = new Size(1100, 720);
            BackColor = BackgroundColor;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(10);
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));   // address bar
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // browser
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 240F));  // candidate list and expanded details
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));   // actions

            // --- Address bar ---
            TableLayoutPanel addressRow = new TableLayoutPanel();
            addressRow.Dock = DockStyle.Fill;
            addressRow.ColumnCount = 2;
            addressRow.RowCount = 1;
            addressRow.Margin = Padding.Empty;
            addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            addressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));

            _addressBox = new TextBox();
            _addressBox.Dock = DockStyle.Fill;
            _addressBox.Margin = new Padding(0, 4, 8, 4);
            _addressBox.Text = initialUrl ?? string.Empty;
            _addressBox.KeyDown += AddressBoxKeyDown;

            _goButton = new Button();
            _goButton.Text = "打开";
            _goButton.Dock = DockStyle.Fill;
            _goButton.Margin = new Padding(0, 3, 0, 3);
            _goButton.FlatStyle = FlatStyle.Flat;
            _goButton.Click += GoButtonClick;

            addressRow.Controls.Add(_addressBox, 0, 0);
            addressRow.Controls.Add(_goButton, 1, 0);

            // --- Browser ---
            _webView = new WebView2();
            _webView.Dock = DockStyle.Fill;
            _webView.Margin = new Padding(0, 4, 0, 4);

            // --- Candidate list ---
            _candidateList = new ListView();
            _candidateList.Dock = DockStyle.Fill;
            _candidateList.Margin = new Padding(0, 4, 0, 4);
            _candidateList.View = View.Details;
            _candidateList.FullRowSelect = true;
            _candidateList.MultiSelect = false;
            _candidateList.HideSelection = false;
            _candidateList.ShowItemToolTips = true;
            _candidateList.Columns.Add("捕获链接 / 播放列表资源（点击 +/- 展开）", 560);
            _candidateList.Columns.Add("总大小", 105);
            _candidateList.Columns.Add("内容", 105);
            _candidateList.Columns.Add("请求头", 105);
            _candidateList.Columns.Add("时间 / 详情", 125);
            _candidateList.DoubleClick += CandidateListDoubleClick;
            _candidateList.SelectedIndexChanged += CandidateSelectionChanged;
            _candidateList.MouseClick += CandidateListMouseClick;
            _candidateList.KeyDown += CandidateListKeyDown;

            // --- Actions ---
            TableLayoutPanel actionRow = new TableLayoutPanel();
            actionRow.Dock = DockStyle.Fill;
            actionRow.ColumnCount = 5;
            actionRow.RowCount = 1;
            actionRow.Margin = Padding.Empty;
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90F));
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
            actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80F));

            _hintLabel = new Label();
            _hintLabel.Dock = DockStyle.Fill;
            _hintLabel.TextAlign = ContentAlignment.MiddleLeft;
            _hintLabel.ForeColor = MutedTextColor;
            _hintLabel.Text = "打开视频页并播放；捕获项可展开查看完整切片地址。";

            _clearButton = new Button();
            _clearButton.Text = "清空列表";
            _clearButton.Dock = DockStyle.Fill;
            _clearButton.Margin = new Padding(0, 6, 8, 6);
            _clearButton.FlatStyle = FlatStyle.Flat;
            _clearButton.Enabled = false;
            _clearButton.Click += ClearButtonClick;

            _previewButton = new Button();
            _previewButton.Text = "预览选中项";
            _previewButton.Dock = DockStyle.Fill;
            _previewButton.Margin = new Padding(0, 6, 8, 6);
            _previewButton.FlatStyle = FlatStyle.Flat;
            _previewButton.Enabled = false;
            _previewButton.Click += PreviewButtonClick;

            _useButton = new Button();
            _useButton.Text = "使用选中项下载";
            _useButton.Dock = DockStyle.Fill;
            _useButton.Margin = new Padding(0, 6, 8, 6);
            _useButton.FlatStyle = FlatStyle.Flat;
            _useButton.BackColor = AccentColor;
            _useButton.ForeColor = Color.White;
            _useButton.Enabled = false;
            _useButton.Click += UseButtonClick;

            _cancelButton = new Button();
            _cancelButton.Text = "取消";
            _cancelButton.Dock = DockStyle.Fill;
            _cancelButton.Margin = new Padding(0, 6, 0, 6);
            _cancelButton.FlatStyle = FlatStyle.Flat;
            _cancelButton.Click += CancelButtonClick;

            actionRow.Controls.Add(_hintLabel, 0, 0);
            actionRow.Controls.Add(_clearButton, 1, 0);
            actionRow.Controls.Add(_previewButton, 2, 0);
            actionRow.Controls.Add(_useButton, 3, 0);
            actionRow.Controls.Add(_cancelButton, 4, 0);

            root.Controls.Add(addressRow, 0, 0);
            root.Controls.Add(_webView, 0, 1);
            root.Controls.Add(_candidateList, 0, 2);
            root.Controls.Add(actionRow, 0, 3);
            Controls.Add(root);

            Load += CaptureBrowserFormLoad;
            FormClosed += CaptureBrowserFormClosed;
        }

        public CaptureResult Result
        {
            get { return _result; }
        }

        private async void CaptureBrowserFormLoad(object sender, EventArgs e)
        {
            try
            {
                // Keep the browser profile in the app data folder rather than next
                // to the exe so a read-only install location still works.
                string userDataFolder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "N_m3u8DL-RE-GUI",
                    "WebView2");
                CoreWebView2Environment environment =
                    await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
                await _webView.EnsureCoreWebView2Async(environment);

                CoreWebView2 core = _webView.CoreWebView2;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(BlobCaptureScript);
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += CoreWebResourceRequested;
                core.WebResourceResponseReceived += CoreWebResourceResponseReceived;
                core.WebMessageReceived += CoreWebMessageReceived;
                core.SourceChanged += CoreSourceChanged;
                _coreReady = true;

                if (!string.IsNullOrWhiteSpace(_addressBox.Text))
                {
                    Navigate(_addressBox.Text.Trim());
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "无法初始化内嵌浏览器：\r\n\r\n" + exception.Message,
                    "浏览器初始化失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                DialogResult = DialogResult.Cancel;
                Close();
            }
        }

        private void CoreSourceChanged(object sender, CoreWebView2SourceChangedEventArgs e)
        {
            if (_webView.CoreWebView2 != null)
            {
                _addressBox.Text = _webView.CoreWebView2.Source;
            }
        }

        // Fires for every resource request. Playlist requests become candidates;
        // listed media resources feed their actual browser headers back into the
        // owning candidate so the downloader can replay CDN-specific checks.
        private void CoreWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                CoreWebView2WebResourceRequest request = e.Request;
                if (request == null || !IsHttpUrl(request.Uri))
                {
                    return;
                }

                string url = request.Uri;
                MediaRequestHeaders headers = ReadHeaders(request.Headers);
                headers.SourceUrl = url;
                CompleteBrowserHeaders(headers);
                if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    ObserveResourceRequest(url, headers, true);
                }

                if (!LooksLikeM3u8(url))
                {
                    return;
                }

                AddCandidate(url, headers, null, url, false);

                // Cookie is often added by the network stack after this event, so
                // fall back to the cookie manager for the stream's origin.
                if (string.IsNullOrWhiteSpace(headers.Cookie))
                {
                    BeginResolveCookie(url, url, headers, false, _captureGeneration);
                }
            }
            catch (Exception)
            {
                // A single malformed request must never break browsing.
            }
        }

        private async void CoreWebResourceResponseReceived(
            object sender,
            CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                string url = e.Request == null ? null : e.Request.Uri;
                if (e.Request == null || !IsHttpUrl(url))
                {
                    return;
                }

                MediaRequestHeaders headers = ReadHeaders(e.Request.Headers);
                headers.SourceUrl = url;
                CompleteBrowserHeaders(headers);
                if (string.Equals(e.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    ObserveResourceRequest(url, headers, true);
                }

                if (!LooksLikeM3u8(url))
                {
                    return;
                }

                AddCandidate(url, headers, null, url, false);

                using (Stream stream = await e.Response.GetContentAsync())
                {
                    if (stream == null)
                    {
                        return;
                    }

                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        string content = await reader.ReadToEndAsync();
                        if (PlaylistInput.LooksLikePlaylistContent(content))
                        {
                            PublishCandidatePlaylist(url, content, url, false, false);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Some responses do not expose a body. The background HTTP probe
                // remains a fallback for those candidates.
            }
        }

        private void CoreWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                BlobPlaylistMessage message;
                if (!BlobPlaylistMessageParser.TryParse(e.TryGetWebMessageAsString(), out message))
                {
                    return;
                }

                string baseUrl = IsHttpUrl(message.BaseUrl)
                    ? message.BaseUrl
                    : (IsHttpUrl(e.Source) ? e.Source : message.BaseUrl);
                string pageUrl = IsHttpUrl(message.PageUrl)
                    ? message.PageUrl
                    : (IsHttpUrl(e.Source) ? e.Source : baseUrl);
                string content = HlsPlaylistInspector.MakeReferencesAbsolute(
                    message.PlaylistContent,
                    baseUrl);

                MediaRequestHeaders headers = new MediaRequestHeaders();
                headers.Referer = IsHttpUrl(pageUrl) ? pageUrl : null;
                headers.UserAgent = message.UserAgent;
                AddCandidate(message.BlobUrl, headers, content, baseUrl, true);

                if (IsHttpUrl(baseUrl))
                {
                    BeginResolveCookie(
                        baseUrl,
                        message.BlobUrl,
                        headers,
                        false,
                        _captureGeneration);
                }
            }
            catch (Exception)
            {
                // Ignore malformed or unexpectedly large messages from web pages.
            }
        }

        private async void BeginResolveCookie(
            string cookieUrl,
            string candidateUrl,
            MediaRequestHeaders headers,
            bool overwriteExisting,
            int generation)
        {
            try
            {
                MediaRequestHeaders resolvedHeaders = headers == null
                    ? new MediaRequestHeaders()
                    : headers.Clone();
                CoreWebView2 core = _webView.CoreWebView2;
                if (core == null)
                {
                    return;
                }

                List<CoreWebView2Cookie> cookies = await core.CookieManager.GetCookiesAsync(cookieUrl);
                if (cookies == null || cookies.Count == 0)
                {
                    return;
                }

                List<string> pairs = new List<string>();
                foreach (CoreWebView2Cookie cookie in cookies)
                {
                    pairs.Add(cookie.Name + "=" + cookie.Value);
                }

                resolvedHeaders.Cookie = string.Join("; ", pairs.ToArray());
                resolvedHeaders.SourceUrl = cookieUrl;
                MergeCandidateHeaders(
                    candidateUrl,
                    resolvedHeaders,
                    overwriteExisting,
                    generation);
            }
            catch (Exception)
            {
                // Cookies are best-effort; downloading may still work without them.
            }
        }

        private static MediaRequestHeaders ReadHeaders(CoreWebView2HttpRequestHeaders requestHeaders)
        {
            MediaRequestHeaders headers = new MediaRequestHeaders();
            headers.Referer = SafeGetHeader(requestHeaders, "Referer");
            headers.UserAgent = SafeGetHeader(requestHeaders, "User-Agent");
            headers.Cookie = SafeGetHeader(requestHeaders, "Cookie");
            headers.Origin = SafeGetHeader(requestHeaders, "Origin");
            headers.Authorization = SafeGetHeader(requestHeaders, "Authorization");

            try
            {
                using (CoreWebView2HttpHeadersCollectionIterator iterator = requestHeaders.GetIterator())
                {
                    while (iterator.HasCurrentHeader)
                    {
                        KeyValuePair<string, string> header = iterator.Current;
                        headers.TrySetAdditionalHeader(header.Key, header.Value);
                        iterator.MoveNext();
                    }
                }
            }
            catch (Exception)
            {
                // Some WebView2 versions expose only individually queried headers.
            }

            return headers;
        }

        private void CompleteBrowserHeaders(MediaRequestHeaders headers)
        {
            if (headers == null || _webView.CoreWebView2 == null)
            {
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(headers.Referer) &&
                    IsHttpUrl(_webView.CoreWebView2.Source))
                {
                    headers.Referer = _webView.CoreWebView2.Source;
                }
            }
            catch (Exception)
            {
            }

            try
            {
                if (string.IsNullOrWhiteSpace(headers.UserAgent) &&
                    _webView.CoreWebView2.Settings != null)
                {
                    headers.UserAgent = _webView.CoreWebView2.Settings.UserAgent;
                }
            }
            catch (Exception)
            {
            }
        }

        private static string SafeGetHeader(CoreWebView2HttpRequestHeaders headers, string name)
        {
            try
            {
                if (headers != null && headers.Contains(name))
                {
                    return headers.GetHeader(name);
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private void ObserveResourceRequest(
            string url,
            MediaRequestHeaders headers,
            bool remember)
        {
            if (!IsHttpUrl(url))
            {
                return;
            }

            MediaRequestHeaders snapshot = headers == null
                ? new MediaRequestHeaders()
                : headers.Clone();
            if (remember)
            {
                RecentResourceRequest observation = new RecentResourceRequest();
                observation.Url = url;
                observation.Headers = snapshot;
                observation.CapturedUtc = DateTime.UtcNow;
                _recentResourceRequests.Enqueue(observation);
                while (_recentResourceRequests.Count > RecentResourceRequestLimit)
                {
                    _recentResourceRequests.Dequeue();
                }
            }

            TryAssociateResourceRequest(url, snapshot);
        }

        private bool TryAssociateResourceRequest(string observedUrl, MediaRequestHeaders headers)
        {
            string exactKey = NormalizeResourceUrl(observedUrl, true);
            string pathKey = NormalizeResourceUrl(observedUrl, false);
            if (exactKey == null || pathKey == null)
            {
                return false;
            }

            List<CaptureResourceBinding> bindings;
            bool pathFallback = false;
            if (!_resourceExactIndex.TryGetValue(exactKey, out bindings))
            {
                if (!_resourcePathIndex.TryGetValue(pathKey, out bindings) ||
                    !HasSingleExpectedResource(bindings))
                {
                    return false;
                }

                pathFallback = true;
            }

            bool matched = false;
            HashSet<string> updatedCandidates =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CaptureResourceBinding binding in bindings)
            {
                CaptureCandidate candidate;
                if (binding == null ||
                    !_candidates.TryGetValue(binding.CandidateUrl, out candidate) ||
                    !updatedCandidates.Add(candidate.Url))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(candidate.Headers.SourceUrl) &&
                    !MediaRequestHeaders.AreSameOrigin(
                        candidate.Headers.SourceUrl,
                        observedUrl))
                {
                    candidate.Headers.Cookie = null;
                    candidate.Headers.Authorization = null;
                    candidate.Headers.ClearAdditionalHeaders();
                }

                MergeHeaders(candidate.Headers, headers, true);
                candidate.LastObservedResourceUrl = observedUrl;
                if (binding.Resource != null && binding.Resource.SegmentNumber > 0)
                {
                    candidate.ObservedSegmentUrls.Add(binding.ExpectedUrl);
                    if (pathFallback &&
                        !string.Equals(
                            NormalizeResourceUrl(binding.ExpectedUrl, true),
                            exactKey,
                            StringComparison.Ordinal))
                    {
                        candidate.ObservedResourceUrls[binding.ExpectedUrl] = observedUrl;
                    }

                    if (!candidate.SegmentCookieLookupStarted)
                    {
                        candidate.SegmentCookieLookupStarted = true;
                        BeginResolveCookie(
                            observedUrl,
                            candidate.Url,
                            headers.Clone(),
                            true,
                            _captureGeneration);
                    }
                }

                RefreshCandidateRow(candidate.Url);
                matched = true;
            }

            if (matched)
            {
                _hintLabel.Text = "已捕获真实分片请求；下载时会转发浏览器的 Referer、UA 和站点请求头。";
            }

            return matched;
        }

        private static bool HasSingleExpectedResource(List<CaptureResourceBinding> bindings)
        {
            string expectedKey = null;
            foreach (CaptureResourceBinding binding in bindings)
            {
                string key = binding == null
                    ? null
                    : NormalizeResourceUrl(binding.ExpectedUrl, true);
                if (key == null)
                {
                    continue;
                }

                if (expectedKey == null)
                {
                    expectedKey = key;
                }
                else if (!string.Equals(expectedKey, key, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return expectedKey != null;
        }

        private void RebuildResourceIndexes()
        {
            _resourceExactIndex.Clear();
            _resourcePathIndex.Clear();

            foreach (CaptureCandidate candidate in _candidates.Values)
            {
                if (candidate.Inspection == null)
                {
                    continue;
                }

                foreach (HlsPlaylistResource resource in candidate.Inspection.Resources)
                {
                    string exactKey = NormalizeResourceUrl(resource.Url, true);
                    string pathKey = NormalizeResourceUrl(resource.Url, false);
                    if (exactKey == null || pathKey == null)
                    {
                        continue;
                    }

                    CaptureResourceBinding binding = new CaptureResourceBinding();
                    binding.CandidateUrl = candidate.Url;
                    binding.ExpectedUrl = resource.Url;
                    binding.Resource = resource;
                    AddResourceBinding(_resourceExactIndex, exactKey, binding);
                    AddResourceBinding(_resourcePathIndex, pathKey, binding);
                }
            }
        }

        private static void AddResourceBinding(
            IDictionary<string, List<CaptureResourceBinding>> index,
            string key,
            CaptureResourceBinding binding)
        {
            List<CaptureResourceBinding> bindings;
            if (!index.TryGetValue(key, out bindings))
            {
                bindings = new List<CaptureResourceBinding>();
                index[key] = bindings;
            }

            bindings.Add(binding);
        }

        private void ReplayRecentResourceRequests()
        {
            DateTime cutoff = DateTime.UtcNow.Subtract(RecentResourceRequestLifetime);
            while (_recentResourceRequests.Count > 0 &&
                   _recentResourceRequests.Peek().CapturedUtc < cutoff)
            {
                _recentResourceRequests.Dequeue();
            }

            RecentResourceRequest[] observations = _recentResourceRequests.ToArray();
            foreach (RecentResourceRequest observation in observations)
            {
                TryAssociateResourceRequest(observation.Url, observation.Headers);
            }
        }

        private void MergeCandidateHeaders(
            string candidateUrl,
            MediaRequestHeaders headers,
            bool overwriteExisting,
            int generation)
        {
            if (InvokeRequired)
            {
                if (IsDisposed || Disposing)
                {
                    return;
                }

                try
                {
                    BeginInvoke(
                        new Action<string, MediaRequestHeaders, bool, int>(MergeCandidateHeaders),
                        candidateUrl,
                        headers,
                        overwriteExisting,
                        generation);
                }
                catch (Exception)
                {
                }

                return;
            }

            if (generation != _captureGeneration)
            {
                return;
            }

            CaptureCandidate candidate;
            if (_candidates.TryGetValue(candidateUrl, out candidate))
            {
                MergeHeaders(candidate.Headers, headers, overwriteExisting);
                RefreshCandidateRow(candidateUrl);
            }
        }

        internal static string NormalizeResourceUrl(string value, bool includeQuery)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            UriComponents components = UriComponents.SchemeAndServer | UriComponents.Path;
            if (includeQuery)
            {
                components |= UriComponents.Query;
            }

            return uri.GetComponents(components, UriFormat.UriEscaped);
        }

        private static bool LooksLikeM3u8(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string path = url;
            int query = path.IndexOf('?');
            if (query >= 0)
            {
                path = path.Substring(0, query);
            }

            return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
                   path.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsHttpUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                   (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }

        private void AddCandidate(
            string url,
            MediaRequestHeaders headers,
            string playlistContent,
            string playlistBaseUrl,
            bool isBlob)
        {
            if (InvokeRequired)
            {
                BeginInvoke(
                    new Action<string, MediaRequestHeaders, string, string, bool>(AddCandidate),
                    url,
                    headers,
                    playlistContent,
                    playlistBaseUrl,
                    isBlob);
                return;
            }

            CaptureCandidate candidate;
            if (_candidates.TryGetValue(url, out candidate))
            {
                MergeHeaders(candidate.Headers, headers, false);
                if (!string.IsNullOrWhiteSpace(playlistContent))
                {
                    ApplyCandidatePlaylist(candidate, playlistContent, playlistBaseUrl);
                }

                RefreshCandidateRow(url);
                return;
            }

            candidate = new CaptureCandidate();
            candidate.Url = url;
            candidate.Headers = headers ?? new MediaRequestHeaders();
            candidate.CaptureTime = DateTime.Now;
            candidate.IsBlob = isBlob;
            _candidates[url] = candidate;
            if (!string.IsNullOrWhiteSpace(playlistContent))
            {
                ApplyCandidatePlaylist(candidate, playlistContent, playlistBaseUrl);
            }

            ListViewItem item = new ListViewItem();
            item.SubItems.Add("计算中…");
            item.SubItems.Add(string.Empty);
            item.SubItems.Add(DescribeHeaders(candidate.Headers));
            item.SubItems.Add(candidate.CaptureTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            CaptureCandidateRowTag tag = new CaptureCandidateRowTag();
            tag.CandidateUrl = url;
            item.Tag = tag;
            _candidateList.Items.Add(item);
            UpdateCandidateRootItem(item, candidate);

            _clearButton.Enabled = true;
            _hintLabel.Text = "已捕获 " + _candidates.Count +
                              " 个播放列表。点击 + 可查看每一个切片地址。";

            BeginProbeSize(candidate);
        }

        internal static void MergeHeaders(
            MediaRequestHeaders target,
            MediaRequestHeaders source,
            bool overwriteExisting)
        {
            if (target == null || source == null)
            {
                return;
            }

            bool sameHeaderOrigin =
                string.IsNullOrWhiteSpace(target.SourceUrl) ||
                string.IsNullOrWhiteSpace(source.SourceUrl) ||
                MediaRequestHeaders.AreSameOrigin(target.SourceUrl, source.SourceUrl);

            if (overwriteExisting || string.IsNullOrWhiteSpace(target.Referer))
            {
                if (!string.IsNullOrWhiteSpace(source.Referer))
                {
                    target.Referer = source.Referer;
                }
            }

            if (overwriteExisting || string.IsNullOrWhiteSpace(target.UserAgent))
            {
                if (!string.IsNullOrWhiteSpace(source.UserAgent))
                {
                    target.UserAgent = source.UserAgent;
                }
            }

            if ((overwriteExisting || sameHeaderOrigin) &&
                (overwriteExisting || string.IsNullOrWhiteSpace(target.Cookie)))
            {
                if (!string.IsNullOrWhiteSpace(source.Cookie))
                {
                    target.Cookie = source.Cookie;
                }
            }

            if (overwriteExisting || string.IsNullOrWhiteSpace(target.Origin))
            {
                if (!string.IsNullOrWhiteSpace(source.Origin))
                {
                    target.Origin = source.Origin;
                }
            }

            if ((overwriteExisting || sameHeaderOrigin) &&
                (overwriteExisting || string.IsNullOrWhiteSpace(target.Authorization)))
            {
                if (!string.IsNullOrWhiteSpace(source.Authorization))
                {
                    target.Authorization = source.Authorization;
                }
            }

            if (overwriteExisting || string.IsNullOrWhiteSpace(target.SourceUrl))
            {
                if (!string.IsNullOrWhiteSpace(source.SourceUrl))
                {
                    target.SourceUrl = source.SourceUrl;
                }
            }

            foreach (KeyValuePair<string, string> header in source.AdditionalHeaders)
            {
                if ((overwriteExisting || sameHeaderOrigin) &&
                    (overwriteExisting || !target.AdditionalHeaders.ContainsKey(header.Key)))
                {
                    target.TrySetAdditionalHeader(header.Key, header.Value);
                }
            }
        }

        private void ApplyCandidatePlaylist(
            CaptureCandidate candidate,
            string content,
            string baseUrl)
        {
            candidate.PlaylistContent = content;
            candidate.PlaylistBaseUrl = baseUrl;
            candidate.Inspection = HlsPlaylistInspector.Inspect(content, baseUrl);
            candidate.HasPrivateTokenTag = MainForm.ContainsPrivateTokenTag(content);

            long knownBandwidth;
            if (_variantBandwidths.TryGetValue(candidate.Url, out knownBandwidth))
            {
                candidate.PlaylistBandwidth = knownBandwidth;
            }

            RememberVariantBandwidths(content, baseUrl);
            ApplyPlaylistSizeEstimate(candidate);
            RebuildResourceIndexes();
            ReplayRecentResourceRequests();
        }

        private void RememberVariantBandwidths(string content, string baseUrl)
        {
            List<MasterVariant> variants = M3u8SizeProbe.TryParseMaster(baseUrl, content);
            if (variants == null)
            {
                return;
            }

            foreach (MasterVariant variant in variants)
            {
                long bandwidth = variant.AverageBandwidth > 0
                    ? variant.AverageBandwidth
                    : variant.Bandwidth;
                if (bandwidth <= 0 || string.IsNullOrWhiteSpace(variant.Url))
                {
                    continue;
                }

                _variantBandwidths[variant.Url] = bandwidth;
                CaptureCandidate existingCandidate;
                if (_candidates.TryGetValue(variant.Url, out existingCandidate))
                {
                    existingCandidate.PlaylistBandwidth = bandwidth;
                    ApplyPlaylistSizeEstimate(existingCandidate);
                    RefreshCandidateRow(existingCandidate.Url);
                }
            }
        }

        private static SizeProbeResult CreatePlaylistSizeEstimate(CaptureCandidate candidate)
        {
            if (candidate == null || candidate.Inspection == null)
            {
                return null;
            }

            return M3u8SizeProbe.EstimateFromPlaylistMetadata(
                candidate.Inspection.TotalDurationSeconds,
                candidate.PlaylistBandwidth,
                candidate.Inspection.SegmentCount);
        }

        private static void ApplyPlaylistSizeEstimate(CaptureCandidate candidate)
        {
            SizeProbeResult estimate = CreatePlaylistSizeEstimate(candidate);
            if (estimate == null)
            {
                return;
            }

            if (candidate.Size == null || candidate.Size.FromPlaylistMetadata ||
                candidate.Size.Status == SizeProbeStatus.Failed ||
                candidate.Size.Status == SizeProbeStatus.Unknown)
            {
                candidate.Size = estimate;
            }
        }

        private void PublishCandidatePlaylist(
            string url,
            string content,
            string baseUrl,
            bool makeAbsolute,
            bool onlyIfEmpty)
        {
            if (InvokeRequired)
            {
                if (IsDisposed || Disposing)
                {
                    return;
                }

                try
                {
                    BeginInvoke(
                        new Action<string, string, string, bool, bool>(PublishCandidatePlaylist),
                        url,
                        content,
                        baseUrl,
                        makeAbsolute,
                        onlyIfEmpty);
                }
                catch (Exception)
                {
                }

                return;
            }

            CaptureCandidate candidate;
            if (!_candidates.TryGetValue(url, out candidate) ||
                (onlyIfEmpty && !string.IsNullOrWhiteSpace(candidate.PlaylistContent)))
            {
                return;
            }

            string usableContent = makeAbsolute
                ? HlsPlaylistInspector.MakeReferencesAbsolute(content, baseUrl)
                : content;
            ApplyCandidatePlaylist(candidate, usableContent, baseUrl);
            RefreshCandidateRow(url);
        }

        private void BeginProbeSize(CaptureCandidate candidate)
        {
            if (candidate.SizeProbeStarted)
            {
                return;
            }

            candidate.SizeProbeStarted = true;
            CancellationToken token = _cancellation.Token;
            string url = candidate.Url;
            MediaRequestHeaders headers = candidate.Headers == null ? null : candidate.Headers.Clone();

            Task.Factory.StartNew(delegate
            {
                SizeProbeResult probe;
                string content = candidate.PlaylistContent;
                string baseUrl = candidate.PlaylistBaseUrl;
                if (string.IsNullOrWhiteSpace(content) && !candidate.IsBlob)
                {
                    try
                    {
                        content = M3u8SizeProbe.DownloadPlaylistText(url, headers, token);
                        baseUrl = url;
                        PublishCandidatePlaylist(url, content, baseUrl, false, true);
                        probe = M3u8SizeProbe.ProbeContent(url, content, headers, token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        probe = new SizeProbeResult();
                        probe.Status = SizeProbeStatus.Failed;
                        probe.Message = exception.Message;
                    }
                }
                else
                {
                    string probeBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? url : baseUrl;
                    probe = M3u8SizeProbe.ProbeContent(
                        probeBaseUrl,
                        content,
                        headers,
                        token);
                }

                if (probe.Status == SizeProbeStatus.Failed ||
                    probe.Status == SizeProbeStatus.Unknown)
                {
                    candidate.SizeProbeError = probe.Message;
                    SizeProbeResult metadataEstimate = CreatePlaylistSizeEstimate(candidate);
                    if (metadataEstimate != null)
                    {
                        probe = metadataEstimate;
                    }
                }
                else
                {
                    candidate.SizeProbeError = null;
                }

                candidate.Size = probe;
                candidate.SizeProbeDone = true;
                if (!token.IsCancellationRequested)
                {
                    RefreshCandidateRow(url);
                }
            }, token);
        }

        private void RefreshCandidateRow(string url)
        {
            if (InvokeRequired)
            {
                if (IsDisposed || Disposing)
                {
                    return;
                }

                try
                {
                    BeginInvoke(new Action<string>(RefreshCandidateRow), url);
                }
                catch (Exception)
                {
                }

                return;
            }

            CaptureCandidate candidate;
            if (!_candidates.TryGetValue(url, out candidate))
            {
                return;
            }

            ListViewItem rootItem = FindCandidateRootItem(url);
            if (rootItem == null)
            {
                return;
            }

            _candidateList.BeginUpdate();
            try
            {
                UpdateCandidateRootItem(rootItem, candidate);
                if (candidate.Expanded)
                {
                    RemoveCandidateDetailRows(url);
                    InsertCandidateDetailRows(rootItem.Index, candidate);
                }
            }
            finally
            {
                _candidateList.EndUpdate();
            }
        }

        private ListViewItem FindCandidateRootItem(string url)
        {
            foreach (ListViewItem item in _candidateList.Items)
            {
                CaptureCandidateRowTag tag = item.Tag as CaptureCandidateRowTag;
                if (tag != null && !tag.IsDetail &&
                    string.Equals(tag.CandidateUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return null;
        }

        private static void UpdateCandidateRootItem(ListViewItem item, CaptureCandidate candidate)
        {
            bool canExpand = candidate.Inspection != null && candidate.Inspection.Resources.Count > 0;
            item.Text = (canExpand ? (candidate.Expanded ? "- " : "+ ") : "  ") + candidate.Url;
            bool playlistCaptured = candidate.Inspection != null &&
                                    candidate.Inspection.Resources.Count > 0;
            item.ToolTipText = candidate.Url;
            if (candidate.ObservedSegmentUrls.Count > 0)
            {
                item.ToolTipText += "\r\n已观察到 " +
                                    candidate.ObservedSegmentUrls.Count.ToString(
                                        CultureInfo.InvariantCulture) +
                                    " 个真实分片请求。";
                if (!string.IsNullOrWhiteSpace(candidate.LastObservedResourceUrl))
                {
                    item.ToolTipText += "\r\n最近分片：" + candidate.LastObservedResourceUrl;
                }
            }
            if (candidate.Size != null && candidate.Size.Status == SizeProbeStatus.Failed && playlistCaptured)
            {
                item.ToolTipText += "\r\n播放列表正文已捕获；仅总大小探测失败：" +
                                    (candidate.Size.Message ?? "服务器拒绝独立请求");
            }
            else if (candidate.Size != null && candidate.Size.FromPlaylistMetadata)
            {
                item.ToolTipText += "\r\n总大小根据主播放列表码率和全部切片时长估算。";
                if (!string.IsNullOrWhiteSpace(candidate.SizeProbeError))
                {
                    item.ToolTipText += "\r\n独立字节探测失败：" + candidate.SizeProbeError;
                }
            }

            item.SubItems[1].Text = DescribeSize(candidate, playlistCaptured);
            item.SubItems[2].Text = DescribePlaylist(candidate);
            item.SubItems[3].Text = DescribeHeaders(candidate.Headers);
            item.SubItems[4].Text = candidate.CaptureTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static string DescribeSize(CaptureCandidate candidate, bool playlistCaptured)
        {
            if (candidate.Size == null)
            {
                return "计算中…";
            }

            if (candidate.Size.Status == SizeProbeStatus.Failed && playlistCaptured)
            {
                return "未知（已捕获）";
            }

            return candidate.Size.Display;
        }

        private static string DescribePlaylist(CaptureCandidate candidate)
        {
            if (candidate.Inspection == null)
            {
                return candidate.IsBlob ? "解析中…" : "读取中…";
            }

            if (candidate.Inspection.SegmentCount > 0)
            {
                return candidate.Inspection.SegmentCount.ToString(CultureInfo.InvariantCulture) + " 个切片";
            }

            if (candidate.Inspection.Resources.Count > 0)
            {
                return candidate.Inspection.Resources.Count.ToString(CultureInfo.InvariantCulture) + " 个资源";
            }

            return "未找到切片";
        }

        private void RemoveCandidateDetailRows(string url)
        {
            for (int i = _candidateList.Items.Count - 1; i >= 0; i--)
            {
                CaptureCandidateRowTag tag = _candidateList.Items[i].Tag as CaptureCandidateRowTag;
                if (tag != null && tag.IsDetail &&
                    string.Equals(tag.CandidateUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    _candidateList.Items.RemoveAt(i);
                }
            }
        }

        private void InsertCandidateDetailRows(int rootIndex, CaptureCandidate candidate)
        {
            if (candidate.Inspection == null)
            {
                return;
            }

            int insertAt = rootIndex + 1;
            foreach (HlsPlaylistResource resource in candidate.Inspection.Resources)
            {
                string number = resource.SegmentNumber > 0
                    ? resource.SegmentNumber.ToString("0000", CultureInfo.InvariantCulture)
                    : resource.Order.ToString("0000", CultureInfo.InvariantCulture);
                string observedUrl;
                bool hasObservedRewrite = candidate.ObservedResourceUrls.TryGetValue(
                    resource.Url,
                    out observedUrl);
                string displayUrl = hasObservedRewrite
                    ? resource.Url + "  =>  " + observedUrl
                    : resource.Url;
                ListViewItem item = new ListViewItem("      " + number + "  " + displayUrl);
                item.SubItems.Add(string.Empty);
                item.SubItems.Add(resource.Kind ?? "资源");
                item.SubItems.Add(string.Empty);
                item.SubItems.Add(resource.Detail ?? string.Empty);
                item.ForeColor = MutedTextColor;
                item.ToolTipText = hasObservedRewrite
                    ? "播放列表地址：" + resource.Url + "\r\n浏览器实际请求：" + observedUrl
                    : resource.Url;

                CaptureCandidateRowTag tag = new CaptureCandidateRowTag();
                tag.CandidateUrl = candidate.Url;
                tag.IsDetail = true;
                item.Tag = tag;
                _candidateList.Items.Insert(insertAt++, item);
            }
        }

        private static string DescribeHeaders(MediaRequestHeaders headers)
        {
            if (headers == null || !headers.HasAny)
            {
                return "无";
            }

            List<string> parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(headers.Referer))
            {
                parts.Add("Referer");
            }

            if (!string.IsNullOrWhiteSpace(headers.Cookie))
            {
                parts.Add("Cookie");
            }

            if (!string.IsNullOrWhiteSpace(headers.Origin))
            {
                parts.Add("Origin");
            }

            if (!string.IsNullOrWhiteSpace(headers.Authorization))
            {
                parts.Add("Auth");
            }

            if (headers.AdditionalHeaders.Count > 0)
            {
                parts.Add("自定义(" + headers.AdditionalHeaders.Count.ToString(
                    CultureInfo.InvariantCulture) + ")");
            }

            return parts.Count > 0 ? string.Join("+", parts.ToArray()) : "UA";
        }

        private CaptureCandidate GetSelectedCandidate()
        {
            if (_candidateList.SelectedItems.Count == 0)
            {
                return null;
            }

            CaptureCandidateRowTag tag = _candidateList.SelectedItems[0].Tag as CaptureCandidateRowTag;
            if (tag == null)
            {
                return null;
            }

            CaptureCandidate candidate;
            return _candidates.TryGetValue(tag.CandidateUrl, out candidate) ? candidate : null;
        }

        private CaptureCandidate ResolveCapturedMediaCandidate(CaptureCandidate candidate, int depth)
        {
            return ResolveCapturedMediaCandidate(_candidates, candidate, depth);
        }

        internal static CaptureCandidate ResolveCapturedMediaCandidate(
            IDictionary<string, CaptureCandidate> candidates,
            CaptureCandidate candidate,
            int depth)
        {
            if (candidates == null || candidate == null)
            {
                return null;
            }

            if (candidate.Inspection != null && candidate.Inspection.SegmentCount > 0)
            {
                return candidate;
            }

            List<MasterVariant> variants = M3u8SizeProbe.TryParseMaster(
                candidate.PlaylistBaseUrl,
                candidate.PlaylistContent);
            if (variants == null || variants.Count == 0)
            {
                return candidate;
            }

            if (depth >= 5)
            {
                return null;
            }

            CaptureCandidate bestCandidate = null;
            long bestBandwidth = -1;
            foreach (MasterVariant variant in variants)
            {
                CaptureCandidate capturedVariant;
                if (string.IsNullOrWhiteSpace(variant.Url) ||
                    !candidates.TryGetValue(variant.Url, out capturedVariant) ||
                    string.IsNullOrWhiteSpace(capturedVariant.PlaylistContent))
                {
                    continue;
                }

                CaptureCandidate resolved = ResolveCapturedMediaCandidate(
                    candidates,
                    capturedVariant,
                    depth + 1);
                if (resolved == null || resolved.Inspection == null ||
                    resolved.Inspection.SegmentCount == 0)
                {
                    continue;
                }

                long bandwidth = variant.Bandwidth > 0
                    ? variant.Bandwidth
                    : variant.AverageBandwidth;
                if (bestCandidate == null || bandwidth > bestBandwidth)
                {
                    bestCandidate = resolved;
                    bestBandwidth = bandwidth;
                }
            }

            return bestCandidate;
        }

        private void CandidateSelectionChanged(object sender, EventArgs e)
        {
            CaptureCandidate candidate = GetSelectedCandidate();
            _useButton.Enabled = candidate != null;
            _previewButton.Enabled = candidate != null && !candidate.IsBlob;
            CaptureCandidate mediaCandidate = ResolveCapturedMediaCandidate(candidate, 0);
            if (candidate != null && candidate.IsBlob)
            {
                _useButton.Text = "导入此 Blob 并下载";
            }
            else if (mediaCandidate != null &&
                     !string.IsNullOrWhiteSpace(mediaCandidate.PlaylistContent))
            {
                _useButton.Text = ReferenceEquals(mediaCandidate, candidate)
                    ? "使用捕获正文下载"
                    : "使用已捕获子列表下载";
            }
            else
            {
                _useButton.Text = "使用选中项下载";
            }
            if (mediaCandidate != null && mediaCandidate.ObservedSegmentUrls.Count > 0)
            {
                _hintLabel.Text = "已捕获真实分片请求头；下载会复用浏览器的 Referer 和 User-Agent。";
                return;
            }

            if (mediaCandidate != null && mediaCandidate.HasPrivateTokenTag)
            {
                _hintLabel.Text = "该列表带站点私有 Token；请让视频继续播放几秒，等程序捕获真实分片请求。";
                return;
            }

            if (candidate != null && candidate.Size != null && candidate.Size.FromPlaylistMetadata)
            {
                _hintLabel.Text = string.IsNullOrWhiteSpace(candidate.SizeProbeError)
                    ? "当前大小按主播放列表码率和全部切片时长估算。"
                    : "独立大小探测被 CDN 拒绝，已改用播放列表码率 × 总时长估算。";
            }
            else if (candidate != null && candidate.Size != null &&
                candidate.Size.Status == SizeProbeStatus.Failed &&
                candidate.Inspection != null && candidate.Inspection.Resources.Count > 0)
            {
                _hintLabel.Text = "播放列表已捕获；403 只表示总大小探测被拒，实际下载仍取决于 CDN 校验。";
            }
        }

        private void CandidateListMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            ListViewHitTestInfo hit = _candidateList.HitTest(e.Location);
            if (hit.Item == null || hit.SubItem != hit.Item.SubItems[0] ||
                e.X - hit.Item.Bounds.Left > 28)
            {
                return;
            }

            CaptureCandidateRowTag tag = hit.Item.Tag as CaptureCandidateRowTag;
            if (tag == null || tag.IsDetail)
            {
                return;
            }

            CaptureCandidate candidate;
            if (_candidates.TryGetValue(tag.CandidateUrl, out candidate))
            {
                ToggleCandidate(candidate, !candidate.Expanded);
            }
        }

        private void CandidateListKeyDown(object sender, KeyEventArgs e)
        {
            CaptureCandidate candidate = GetSelectedCandidate();
            if (candidate == null)
            {
                return;
            }

            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Add || e.KeyCode == Keys.Oemplus)
            {
                ToggleCandidate(candidate, true);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Subtract || e.KeyCode == Keys.OemMinus)
            {
                ToggleCandidate(candidate, false);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Space)
            {
                ToggleCandidate(candidate, !candidate.Expanded);
                e.Handled = true;
            }
        }

        private void ToggleCandidate(CaptureCandidate candidate, bool expand)
        {
            if (candidate.Inspection == null || candidate.Inspection.Resources.Count == 0)
            {
                _hintLabel.Text = "该播放列表尚未读取到可展开的切片或关联资源。";
                return;
            }

            candidate.Expanded = expand;
            RefreshCandidateRow(candidate.Url);
            _hintLabel.Text = expand
                ? "已展开 " + candidate.Inspection.Resources.Count + " 条资源，顺序与播放列表一致。"
                : "已收起播放列表明细。";
        }

        private void CandidateListDoubleClick(object sender, EventArgs e)
        {
            CaptureCandidate candidate = GetSelectedCandidate();
            if (candidate != null && candidate.IsBlob)
            {
                ToggleCandidate(candidate, !candidate.Expanded);
            }
            else
            {
                PreviewSelected();
            }
        }

        private void PreviewButtonClick(object sender, EventArgs e)
        {
            PreviewSelected();
        }

        private void PreviewSelected()
        {
            CaptureCandidate candidate = GetSelectedCandidate();
            if (candidate == null)
            {
                return;
            }

            if (candidate.IsBlob)
            {
                MessageBox.Show(
                    this,
                    "Blob 只能在创建它的网页中播放。请选择“导入此 Blob 并下载”，程序会使用捕获到的完整播放列表。",
                    "Blob 无法单独预览",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            LocalPlayer.Preview(this, candidate.Url, candidate.Headers);
        }

        private void UseButtonClick(object sender, EventArgs e)
        {
            CaptureCandidate candidate = GetSelectedCandidate();
            if (candidate == null)
            {
                return;
            }

            CaptureCandidate downloadCandidate = ResolveCapturedMediaCandidate(candidate, 0);
            if (downloadCandidate == null)
            {
                MessageBox.Show(
                    this,
                    "选中的是主播放列表，但浏览器尚未捕获到实际包含切片的子播放列表。\r\n\r\n" +
                    "请继续播放几秒，等待类似 video.m3u8、index.m3u8 或带清晰度目录的候选出现后再试。",
                    "等待媒体播放列表",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (downloadCandidate.HasPrivateTokenTag &&
                downloadCandidate.ObservedSegmentUrls.Count == 0)
            {
                MessageBox.Show(
                    this,
                    "这个播放列表带有站点私有 Token，但程序还没有观察到真实分片请求。\r\n\r\n" +
                    "请返回网页让视频继续播放几秒，看到提示“已捕获真实分片请求”后再下载。",
                    "等待分片请求",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            _result = new CaptureResult();
            _result.Url = downloadCandidate.Url;
            _result.Headers = downloadCandidate.Headers == null
                ? new MediaRequestHeaders()
                : downloadCandidate.Headers.Clone();
            if (!ReferenceEquals(downloadCandidate, candidate))
            {
                MergeHeaders(_result.Headers, candidate.Headers, false);
            }

            _result.PlaylistContent = PlaylistInput.LooksLikePlaylistContent(
                    downloadCandidate.PlaylistContent)
                ? HlsPlaylistInspector.MakeReferencesAbsolute(
                    downloadCandidate.PlaylistContent,
                    downloadCandidate.PlaylistBaseUrl)
                : null;
            _result.IsBlob = downloadCandidate.IsBlob;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void CancelButtonClick(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void ClearButtonClick(object sender, EventArgs e)
        {
            _captureGeneration++;
            _candidates.Clear();
            _variantBandwidths.Clear();
            _resourceExactIndex.Clear();
            _resourcePathIndex.Clear();
            _recentResourceRequests.Clear();
            _candidateList.Items.Clear();
            _clearButton.Enabled = false;
            _useButton.Enabled = false;
            _previewButton.Enabled = false;
            _useButton.Text = "使用选中项下载";
            _hintLabel.Text = "列表已清空。继续浏览播放视频即可重新捕获 m3u8。";
        }

        private void AddressBoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                Navigate(_addressBox.Text.Trim());
            }
        }

        private void GoButtonClick(object sender, EventArgs e)
        {
            Navigate(_addressBox.Text.Trim());
        }

        private void Navigate(string input)
        {
            if (!_coreReady || string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            string target = input;
            if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                target = "https://" + target;
            }

            try
            {
                _webView.CoreWebView2.Navigate(target);
            }
            catch (Exception)
            {
                try
                {
                    _webView.Source = new Uri(target);
                }
                catch (Exception)
                {
                }
            }
        }

        private void CaptureBrowserFormClosed(object sender, FormClosedEventArgs e)
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (Exception)
            {
            }

            try
            {
                if (_webView != null)
                {
                    _webView.Dispose();
                }
            }
            catch (Exception)
            {
            }
        }

        public bool RunSmokeTest()
        {
            // Constructed without touching the runtime; verifies the layout builds.
            return _candidateList.Columns.Count == 5 && _useButton != null;
        }
    }
}
