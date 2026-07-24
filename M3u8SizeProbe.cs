using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace M3u8DownloaderGui
{
    // HTTP headers captured from the embedded browser (or entered manually) that
    // must accompany playlist / segment requests. Many sites reject requests that
    // lack the original Referer or Cookie with 403, so probing and downloading
    // both need to replay them.
    internal sealed class MediaRequestHeaders
    {
        private static readonly HashSet<string> BlockedAdditionalHeaderNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Accept-Encoding",
                "Authorization",
                "Connection",
                "Cookie",
                "Cookie2",
                "Date",
                "Expect",
                "Host",
                "Keep-Alive",
                "Origin",
                "Proxy-Authenticate",
                "Proxy-Authorization",
                "Proxy-Connection",
                "Range",
                "Referer",
                "Set-Cookie",
                "Set-Cookie2",
                "TE",
                "Trailer",
                "Transfer-Encoding",
                "Upgrade",
                "User-Agent",
                "Via",
                "WWW-Authenticate"
            };

        private readonly Dictionary<string, string> _additionalHeaders =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _connectionSpecificHeaderNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Referer;
        public string Cookie;
        public string UserAgent;
        public string Origin;
        public string Authorization;
        public string SourceUrl;

        public IReadOnlyDictionary<string, string> AdditionalHeaders
        {
            get { return _additionalHeaders; }
        }

        public bool HasAny
        {
            get
            {
                return !string.IsNullOrWhiteSpace(Referer)
                    || !string.IsNullOrWhiteSpace(Cookie)
                    || !string.IsNullOrWhiteSpace(UserAgent)
                    || !string.IsNullOrWhiteSpace(Origin)
                    || !string.IsNullOrWhiteSpace(Authorization)
                    || _additionalHeaders.Count > 0;
            }
        }

        public bool TrySetAdditionalHeader(string name, string value)
        {
            if (string.Equals(name, "Connection", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Proxy-Connection", StringComparison.OrdinalIgnoreCase))
            {
                RememberConnectionSpecificHeaders(value);
                return false;
            }

            if (!IsAllowedAdditionalHeader(name, value) ||
                _connectionSpecificHeaderNames.Contains(name))
            {
                return false;
            }

            _additionalHeaders[name] = value.Trim();
            return true;
        }

        public void ClearAdditionalHeaders()
        {
            _additionalHeaders.Clear();
            _connectionSpecificHeaderNames.Clear();
        }

        internal static bool AreSameOrigin(string firstUrl, string secondUrl)
        {
            Uri first;
            Uri second;
            return Uri.TryCreate(firstUrl, UriKind.Absolute, out first) &&
                   Uri.TryCreate(secondUrl, UriKind.Absolute, out second) &&
                   (first.Scheme == Uri.UriSchemeHttp || first.Scheme == Uri.UriSchemeHttps) &&
                   string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase) &&
                   first.Port == second.Port;
        }

        internal MediaRequestHeaders CreateSafeProjection(string targetUrl)
        {
            MediaRequestHeaders projection = new MediaRequestHeaders();
            projection.UserAgent = UserAgent;
            projection.SourceUrl = SourceUrl;

            if (!AreSameOrigin(SourceUrl, targetUrl))
            {
                return projection;
            }

            projection.Referer = Referer;
            projection.Cookie = Cookie;
            projection.Origin = Origin;
            projection.Authorization = Authorization;
            foreach (KeyValuePair<string, string> header in _additionalHeaders)
            {
                projection.TrySetAdditionalHeader(header.Key, header.Value);
            }

            return projection;
        }

        internal static bool IsAllowedAdditionalHeader(string name, string value)
        {
            return IsValidAdditionalHeaderName(name)
                && IsValidAdditionalHeaderValue(value)
                && !IsBlockedAdditionalHeaderName(name);
        }

        internal static bool IsValidAdditionalHeaderName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            foreach (char character in name)
            {
                if (!IsHeaderNameCharacter(character))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsValidAdditionalHeaderValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (char character in value)
            {
                if (character == '\r' || character == '\n' || character == '\0' ||
                    character == (char)127 ||
                    (character < (char)32 && character != '\t'))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsBlockedAdditionalHeaderName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return true;
            }

            return BlockedAdditionalHeaderNames.Contains(name)
                || name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("If-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHeaderNameCharacter(char character)
        {
            if ((character >= 'a' && character <= 'z') ||
                (character >= 'A' && character <= 'Z') ||
                (character >= '0' && character <= '9'))
            {
                return true;
            }

            switch (character)
            {
                case '!':
                case '#':
                case '$':
                case '%':
                case '&':
                case '\'':
                case '*':
                case '+':
                case '-':
                case '.':
                case '^':
                case '_':
                case '`':
                case '|':
                case '~':
                    return true;
                default:
                    return false;
            }
        }

        private void RememberConnectionSpecificHeaders(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            string[] names = value.Split(',');
            foreach (string item in names)
            {
                string name = item.Trim();
                if (!IsValidAdditionalHeaderName(name))
                {
                    continue;
                }

                _connectionSpecificHeaderNames.Add(name);
                _additionalHeaders.Remove(name);
            }
        }

        public MediaRequestHeaders Clone()
        {
            MediaRequestHeaders copy = new MediaRequestHeaders();
            copy.Referer = Referer;
            copy.Cookie = Cookie;
            copy.UserAgent = UserAgent;
            copy.Origin = Origin;
            copy.Authorization = Authorization;
            copy.SourceUrl = SourceUrl;
            foreach (KeyValuePair<string, string> header in _additionalHeaders)
            {
                copy._additionalHeaders[header.Key] = header.Value;
            }

            foreach (string name in _connectionSpecificHeaderNames)
            {
                copy._connectionSpecificHeaderNames.Add(name);
            }

            return copy;
        }
    }

    internal enum SizeProbeStatus
    {
        Exact,
        Estimated,
        Unknown,
        Failed
    }

    internal sealed class SizeProbeResult
    {
        public SizeProbeStatus Status;
        public long TotalBytes;
        public int SegmentCount;
        public string Message;
        public bool FromPlaylistMetadata;

        public string Display
        {
            get
            {
                switch (Status)
                {
                    case SizeProbeStatus.Exact:
                        return M3u8SizeProbe.FormatBytes(TotalBytes);
                    case SizeProbeStatus.Estimated:
                        return "约 " + M3u8SizeProbe.FormatBytes(TotalBytes);
                    case SizeProbeStatus.Unknown:
                        return "未知";
                    default:
                        return string.IsNullOrEmpty(Message) ? "探测失败" : "失败: " + Message;
                }
            }
        }
    }

    internal sealed class PlaylistDownloadResult
    {
        public PlaylistDownloadResult(string content, string finalUrl)
        {
            Content = content;
            FinalUrl = finalUrl;
        }

        public string Content { get; private set; }
        public string FinalUrl { get; private set; }
    }

    // A single quality option parsed from a master playlist.
    internal sealed class MasterVariant
    {
        public long Bandwidth;
        public long AverageBandwidth;
        public string Resolution;
        public string Codecs;
        public string Url;

        public string DisplayName
        {
            get
            {
                StringBuilder builder = new StringBuilder();
                if (!string.IsNullOrEmpty(Resolution))
                {
                    builder.Append(Resolution);
                }

                if (Bandwidth > 0)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append("  ");
                    }

                    builder.Append((Bandwidth / 1000L).ToString(CultureInfo.InvariantCulture));
                    builder.Append(" kbps");
                }

                return builder.Length > 0 ? builder.ToString() : "默认";
            }
        }
    }

    internal static class M3u8SizeProbe
    {
        static M3u8SizeProbe()
        {
            // .NET Framework defaults to TLS 1.0; modern CDNs require TLS 1.2+.
            System.Net.ServicePointManager.SecurityProtocol =
                System.Net.SecurityProtocolType.Tls12 |
                System.Net.SecurityProtocolType.Tls11 |
                System.Net.SecurityProtocolType.Tls;
        }

        // Below this segment count we probe every segment for an exact sum.
        // Above it we sample to keep the probe responsive.
        private const int ExactProbeLimit = 40;
        private const int MaxRedirects = 5;
        private const int SampleSize = 10;
        private const int RequestTimeoutMilliseconds = 12000;
        private const int MaxRedirectFollow = 5;

        private static readonly Regex StreamInfRegex = new Regex(
            @"#EXT-X-STREAM-INF:(?<attrs>.*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex AttributeRegex = new Regex(
            @"(?<key>[A-Z0-9\-]+)\s*=\s*(?:""(?<qval>[^""]*)""|(?<val>[^,]*))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex ByteRangeRegex = new Regex(
            @"#EXT-X-BYTERANGE:\s*(?<len>\d+)(?:@(?<off>\d+))?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0)
            {
                return "未知";
            }

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024.0 && unit < units.Length - 1)
            {
                value = value / 1024.0;
                unit++;
            }

            string format = unit == 0 ? "{0:0} {1}" : "{0:0.0} {1}";
            return string.Format(CultureInfo.InvariantCulture, format, value, units[unit]);
        }

        // Parses a master playlist body into its variant list, or returns null when
        // the text is not a master playlist (i.e. it is a media playlist).
        public static List<MasterVariant> TryParseMaster(string playlistUrl, string body)
        {
            if (string.IsNullOrEmpty(body) ||
                body.IndexOf("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            List<MasterVariant> variants = new List<MasterVariant>();
            string[] lines = SplitLines(body);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                Match match = StreamInfRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                string uri = null;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    string candidate = lines[j].Trim();
                    if (candidate.Length == 0)
                    {
                        continue;
                    }

                    if (candidate.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    uri = candidate;
                    break;
                }

                if (uri == null)
                {
                    continue;
                }

                MasterVariant variant = new MasterVariant();
                variant.Url = ResolveUrl(playlistUrl, uri);
                ParseStreamAttributes(match.Groups["attrs"].Value, variant);
                variants.Add(variant);
            }

            return variants.Count > 0 ? variants : null;
        }

        // Probes the total media size for a playlist URL. Follows a master playlist
        // to its highest-bandwidth variant so a captured master still yields a
        // meaningful size for the largest quality.
        public static SizeProbeResult Probe(
            string playlistUrl,
            MediaRequestHeaders headers,
            CancellationToken cancellationToken)
        {
            try
            {
                return ProbeInternal(playlistUrl, headers, cancellationToken, 0);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                SizeProbeResult failure = new SizeProbeResult();
                failure.Status = SizeProbeStatus.Failed;
                failure.Message = exception.Message;
                return failure;
            }
        }

        public static SizeProbeResult ProbeContent(
            string playlistUrl,
            string body,
            MediaRequestHeaders headers,
            CancellationToken cancellationToken)
        {
            try
            {
                return ProbeContentInternal(playlistUrl, body, headers, cancellationToken, 0);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                SizeProbeResult failure = new SizeProbeResult();
                failure.Status = SizeProbeStatus.Failed;
                failure.Message = exception.Message;
                return failure;
            }
        }

        private static SizeProbeResult ProbeInternal(
            string playlistUrl,
            MediaRequestHeaders headers,
            CancellationToken cancellationToken,
            int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PlaylistDownloadResult download = DownloadPlaylist(playlistUrl, headers, cancellationToken);
            return ProbeContentInternal(
                download.FinalUrl,
                download.Content,
                headers,
                cancellationToken,
                depth);
        }

        private static SizeProbeResult ProbeContentInternal(
            string playlistUrl,
            string body,
            MediaRequestHeaders headers,
            CancellationToken cancellationToken,
            int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<MasterVariant> variants = TryParseMaster(playlistUrl, body);
            if (variants != null && variants.Count > 0)
            {
                if (depth >= MaxRedirectFollow)
                {
                    return Unknown("嵌套播放列表层级过深");
                }

                MasterVariant best = null;
                foreach (MasterVariant variant in variants)
                {
                    if (best == null || variant.Bandwidth > best.Bandwidth)
                    {
                        best = variant;
                    }
                }

                if (best == null || string.IsNullOrEmpty(best.Url))
                {
                    return Unknown("主播放列表未包含可用清晰度");
                }

                return ProbeInternal(best.Url, headers, cancellationToken, depth + 1);
            }

            List<SegmentReference> segments = ParseSegments(playlistUrl, body);
            return ProbeSegments(segments, headers, cancellationToken);
        }

        private static SizeProbeResult ProbeSegments(
            List<SegmentReference> segments,
            MediaRequestHeaders headers,
            CancellationToken cancellationToken)
        {
            if (segments.Count == 0)
            {
                return Unknown("未解析到分片");
            }

            // If the playlist uses EXT-X-BYTERANGE, each segment's byte length is
            // declared inline, so the exact total is available with no network I/O.
            bool allByteRange = true;
            long byteRangeTotal = 0;
            foreach (SegmentReference segment in segments)
            {
                if (segment.ByteRangeLength > 0)
                {
                    byteRangeTotal += segment.ByteRangeLength;
                }
                else
                {
                    allByteRange = false;
                    break;
                }
            }

            if (allByteRange)
            {
                SizeProbeResult exactByteRange = new SizeProbeResult();
                exactByteRange.Status = SizeProbeStatus.Exact;
                exactByteRange.TotalBytes = byteRangeTotal;
                exactByteRange.SegmentCount = segments.Count;
                return exactByteRange;
            }

            if (segments.Count <= ExactProbeLimit)
            {
                long sum = 0;
                int known = 0;
                foreach (SegmentReference segment in segments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long length = ProbeContentLength(segment.Url, headers, cancellationToken);
                    if (length >= 0)
                    {
                        sum += length;
                        known++;
                    }
                }

                if (known == segments.Count)
                {
                    SizeProbeResult exact = new SizeProbeResult();
                    exact.Status = SizeProbeStatus.Exact;
                    exact.TotalBytes = sum;
                    exact.SegmentCount = segments.Count;
                    return exact;
                }

                if (known > 0)
                {
                    double average = (double)sum / known;
                    return Estimated((long)(average * segments.Count), segments.Count);
                }

                return Unknown("服务器未返回分片大小");
            }

            // Many segments: sample a spread across the playlist and extrapolate.
            long sampleSum = 0;
            int sampleKnown = 0;
            int step = segments.Count / SampleSize;
            if (step < 1)
            {
                step = 1;
            }

            for (int i = 0; i < segments.Count && sampleKnown < SampleSize; i += step)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long length = ProbeContentLength(segments[i].Url, headers, cancellationToken);
                if (length >= 0)
                {
                    sampleSum += length;
                    sampleKnown++;
                }
            }

            if (sampleKnown > 0)
            {
                double average = (double)sampleSum / sampleKnown;
                return Estimated((long)(average * segments.Count), segments.Count);
            }

            return Unknown("服务器未返回分片大小");
        }

        private static SizeProbeResult Estimated(long totalBytes, int segmentCount)
        {
            SizeProbeResult result = new SizeProbeResult();
            result.Status = SizeProbeStatus.Estimated;
            result.TotalBytes = totalBytes;
            result.SegmentCount = segmentCount;
            return result;
        }

        internal static SizeProbeResult EstimateFromPlaylistMetadata(
            double durationSeconds,
            long bandwidthBitsPerSecond,
            int segmentCount)
        {
            if (durationSeconds <= 0 || bandwidthBitsPerSecond <= 0 || segmentCount <= 0)
            {
                return null;
            }

            double estimatedBytes = durationSeconds * bandwidthBitsPerSecond / 8.0;
            if (double.IsNaN(estimatedBytes) || double.IsInfinity(estimatedBytes) ||
                estimatedBytes <= 0 || estimatedBytes > long.MaxValue)
            {
                return null;
            }

            SizeProbeResult result = Estimated((long)Math.Ceiling(estimatedBytes), segmentCount);
            result.FromPlaylistMetadata = true;
            return result;
        }

        private static SizeProbeResult Unknown(string message)
        {
            SizeProbeResult result = new SizeProbeResult();
            result.Status = SizeProbeStatus.Unknown;
            result.Message = message;
            return result;
        }

        private static List<SegmentReference> ParseSegments(string playlistUrl, string body)
        {
            List<SegmentReference> segments = new List<SegmentReference>();
            if (string.IsNullOrEmpty(body))
            {
                return segments;
            }

            string[] lines = SplitLines(body);
            long pendingByteRange = 0;
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    Match byteRange = ByteRangeRegex.Match(line);
                    if (byteRange.Success)
                    {
                        long parsed;
                        if (long.TryParse(
                                byteRange.Groups["len"].Value,
                                NumberStyles.Integer,
                                CultureInfo.InvariantCulture,
                                out parsed))
                        {
                            pendingByteRange = parsed;
                        }
                    }

                    continue;
                }

                SegmentReference segment = new SegmentReference();
                segment.Url = ResolveUrl(playlistUrl, line);
                segment.ByteRangeLength = pendingByteRange;
                segments.Add(segment);
                pendingByteRange = 0;
            }

            return segments;
        }

        private static void ParseStreamAttributes(string attributeText, MasterVariant variant)
        {
            MatchCollection matches = AttributeRegex.Matches(attributeText);
            foreach (Match match in matches)
            {
                string key = match.Groups["key"].Value.ToUpperInvariant();
                string value = match.Groups["qval"].Success
                    ? match.Groups["qval"].Value
                    : match.Groups["val"].Value;
                value = value.Trim();

                if (key == "BANDWIDTH")
                {
                    long bandwidth;
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bandwidth) &&
                        bandwidth > 0)
                    {
                        variant.Bandwidth = bandwidth;
                    }
                }
                else if (key == "AVERAGE-BANDWIDTH")
                {
                    long averageBandwidth;
                    if (long.TryParse(
                            value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out averageBandwidth) && averageBandwidth > 0)
                    {
                        variant.AverageBandwidth = averageBandwidth;
                    }
                }
                else if (key == "RESOLUTION")
                {
                    variant.Resolution = value;
                }
                else if (key == "CODECS")
                {
                    variant.Codecs = value;
                }
            }
        }

        private static long ProbeContentLength(
            string url,
            MediaRequestHeaders headers,
            CancellationToken cancellationToken)
        {
            long headLength = TryContentLength(url, headers, "HEAD", false, cancellationToken);
            if (headLength >= 0)
            {
                return headLength;
            }

            // Some servers reject HEAD or omit Content-Length; a one-byte ranged GET
            // returns Content-Range whose total is the real segment size.
            return TryContentLength(url, headers, "GET", true, cancellationToken);
        }

        private static long TryContentLength(
            string url,
            MediaRequestHeaders headers,
            string method,
            bool useRange,
            CancellationToken cancellationToken)
        {
            try
            {
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                {
                    return -1;
                }

                for (int redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
                {
                    HttpWebRequest request = CreateRequest(uri, method, headers, useRange);
                    cancellationToken.ThrowIfCancellationRequested();

                    using (HttpWebResponse response = GetResponseAllowingRedirect(request))
                    {
                        if (IsRedirectStatus(response.StatusCode))
                        {
                            Uri redirected;
                            if (redirectCount == MaxRedirects ||
                                !TryResolveRedirect(uri, response.Headers[HttpResponseHeader.Location], out redirected))
                            {
                                return -1;
                            }

                            uri = redirected;
                            continue;
                        }

                        if (useRange)
                        {
                            string contentRange = response.Headers["Content-Range"];
                            long fromRange = ParseContentRangeTotal(contentRange);
                            if (fromRange >= 0)
                            {
                                return fromRange;
                            }
                        }

                        return response.ContentLength;
                    }
                }

                return -1;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WebException)
            {
                return -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static long ParseContentRangeTotal(string contentRange)
        {
            if (string.IsNullOrEmpty(contentRange))
            {
                return -1;
            }

            int slash = contentRange.LastIndexOf('/');
            if (slash < 0 || slash + 1 >= contentRange.Length)
            {
                return -1;
            }

            string totalText = contentRange.Substring(slash + 1).Trim();
            long total;
            if (long.TryParse(totalText, NumberStyles.Integer, CultureInfo.InvariantCulture, out total))
            {
                return total;
            }

            return -1;
        }

        internal static string DownloadPlaylistText(
            string url,
            MediaRequestHeaders headers,
            CancellationToken cancellationToken)
        {
            return DownloadPlaylist(url, headers, cancellationToken).Content;
        }

        internal static PlaylistDownloadResult DownloadPlaylist(
            string url,
            MediaRequestHeaders headers,
            CancellationToken cancellationToken)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                throw new InvalidOperationException("无效的播放列表地址：" + url);
            }

            for (int redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
            {
                HttpWebRequest request = CreateRequest(uri, "GET", headers, false);
                cancellationToken.ThrowIfCancellationRequested();

                using (HttpWebResponse response = GetResponseAllowingRedirect(request))
                {
                    if (IsRedirectStatus(response.StatusCode))
                    {
                        Uri redirected;
                        if (redirectCount == MaxRedirects ||
                            !TryResolveRedirect(uri, response.Headers[HttpResponseHeader.Location], out redirected))
                        {
                            throw new WebException("The playlist redirect was invalid or exceeded the limit.");
                        }

                        uri = redirected;
                        continue;
                    }

                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        return new PlaylistDownloadResult(reader.ReadToEnd(), uri.AbsoluteUri);
                    }
                }
            }

            throw new WebException("The playlist redirect exceeded the limit.");
        }

        internal static HttpWebRequest CreateRequest(
            Uri uri,
            string method,
            MediaRequestHeaders headers,
            bool useRange)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = method;
            request.Timeout = RequestTimeoutMilliseconds;
            request.ReadWriteTimeout = RequestTimeoutMilliseconds;
            request.AllowAutoRedirect = false;
            ApplyHeaders(request, headers);
            if (useRange)
            {
                request.AddRange(0, 0);
            }

            return request;
        }

        private static HttpWebResponse GetResponseAllowingRedirect(HttpWebRequest request)
        {
            try
            {
                return (HttpWebResponse)request.GetResponse();
            }
            catch (WebException exception)
            {
                HttpWebResponse response = exception.Response as HttpWebResponse;
                if (response != null && IsRedirectStatus(response.StatusCode))
                {
                    return response;
                }

                throw;
            }
        }

        private static bool IsRedirectStatus(HttpStatusCode statusCode)
        {
            int code = (int)statusCode;
            return code == 300 || code == 301 || code == 302 || code == 303 ||
                   code == 307 || code == 308;
        }

        private static bool TryResolveRedirect(Uri current, string location, out Uri redirected)
        {
            redirected = null;
            if (current == null || string.IsNullOrWhiteSpace(location) ||
                !Uri.TryCreate(current, location.Trim(), out redirected))
            {
                return false;
            }

            return redirected.Scheme == Uri.UriSchemeHttp || redirected.Scheme == Uri.UriSchemeHttps;
        }

        private static void ApplyHeaders(HttpWebRequest request, MediaRequestHeaders headers)
        {
            // A browser-like default keeps naive origin checks happy even without capture.
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) M3U8-Video-Downloader";

            MediaRequestHeaders safeHeaders = headers == null
                ? null
                : headers.CreateSafeProjection(request.RequestUri.AbsoluteUri);
            if (safeHeaders == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(safeHeaders.UserAgent))
            {
                request.UserAgent = safeHeaders.UserAgent;
            }

            if (!string.IsNullOrWhiteSpace(safeHeaders.Referer))
            {
                request.Referer = safeHeaders.Referer;
            }

            if (!string.IsNullOrWhiteSpace(safeHeaders.Cookie))
            {
                request.Headers[HttpRequestHeader.Cookie] = safeHeaders.Cookie;
            }

            if (!string.IsNullOrWhiteSpace(safeHeaders.Origin))
            {
                request.Headers["Origin"] = safeHeaders.Origin;
            }

            if (!string.IsNullOrWhiteSpace(safeHeaders.Authorization))
            {
                request.Headers[HttpRequestHeader.Authorization] = safeHeaders.Authorization;
            }

            foreach (KeyValuePair<string, string> header in safeHeaders.AdditionalHeaders)
            {
                if (!MediaRequestHeaders.IsAllowedAdditionalHeader(header.Key, header.Value))
                {
                    continue;
                }

                if (string.Equals(header.Key, "Accept", StringComparison.OrdinalIgnoreCase))
                {
                    request.Accept = header.Value;
                }
                else
                {
                    request.Headers[header.Key] = header.Value;
                }
            }
        }

        internal static string ResolveUrl(string baseUrl, string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return reference;
            }

            string trimmed = reference.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                Uri schemeBase;
                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out schemeBase) &&
                    (string.Equals(schemeBase.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(schemeBase.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    return schemeBase.Scheme + ":" + trimmed;
                }
            }

            Uri absolute;
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out absolute))
            {
                return absolute.ToString();
            }

            Uri baseUri;
            if (Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri))
            {
                Uri resolved;
                if (Uri.TryCreate(baseUri, trimmed, out resolved))
                {
                    return resolved.ToString();
                }
            }

            return trimmed;
        }

        private static string[] SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private sealed class SegmentReference
        {
            public string Url;
            public long ByteRangeLength;
        }
    }
}
