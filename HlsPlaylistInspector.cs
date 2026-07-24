using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace M3u8DownloaderGui
{
    internal sealed class HlsPlaylistResource
    {
        public int Order;
        public int SegmentNumber;
        public string Kind;
        public string Url;
        public string Detail;
    }

    internal sealed class HlsPlaylistInspection
    {
        public readonly List<HlsPlaylistResource> Resources = new List<HlsPlaylistResource>();
        public int SegmentCount;
        public double TotalDurationSeconds;
    }

    // Parses the resource references in an HLS playlist without changing their
    // order. The same parser drives both the expandable capture list and the
    // absolute-URL copy written to disk for browser-created blob playlists.
    internal static class HlsPlaylistInspector
    {
        private static readonly Regex UriAttribute = new Regex(
            @"(?<prefix>\bURI\s*=\s*)(?:""(?<double>[^""]*)""|'(?<single>[^']*)'|(?<plain>[^,\s]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static HlsPlaylistInspection Inspect(string content, string baseUrl)
        {
            HlsPlaylistInspection inspection = new HlsPlaylistInspection();
            if (!PlaylistInput.LooksLikePlaylistContent(content) ||
                !content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n')
                    .StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                return inspection;
            }

            string[] lines = SplitLines(content);
            bool nextReferenceIsVariant = false;
            string pendingDuration = null;
            double pendingDurationSeconds = 0;
            string pendingByteRange = null;
            int order = 0;
            int segmentNumber = 0;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingDuration = ParseExtInfDetail(line);
                        pendingDurationSeconds = ParseExtInfDurationSeconds(line);
                    }
                    else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingByteRange = line.Substring(line.IndexOf(':') + 1).Trim();
                    }

                    if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                    {
                        nextReferenceIsVariant = true;
                    }

                    MatchCollection matches = UriAttribute.Matches(line);
                    foreach (Match match in matches)
                    {
                        string reference = GetUriValue(match);
                        if (string.IsNullOrWhiteSpace(reference))
                        {
                            continue;
                        }

                        string kind = DescribeAttributeResource(line);
                        HlsPlaylistResource resource = new HlsPlaylistResource();
                        resource.Order = ++order;
                        resource.Kind = kind;
                        resource.Url = ResolveReference(baseUrl, reference);
                        resource.Detail = DescribeAttributeDetail(line);
                        inspection.Resources.Add(resource);
                    }

                    continue;
                }

                HlsPlaylistResource lineResource = new HlsPlaylistResource();
                lineResource.Order = ++order;
                lineResource.Url = ResolveReference(baseUrl, line);
                if (nextReferenceIsVariant)
                {
                    lineResource.Kind = "子播放列表";
                    nextReferenceIsVariant = false;
                }
                else
                {
                    lineResource.Kind = "切片";
                    lineResource.SegmentNumber = ++segmentNumber;
                    inspection.SegmentCount++;
                    inspection.TotalDurationSeconds += pendingDurationSeconds;
                    lineResource.Detail = JoinDetails(pendingDuration, pendingByteRange);
                }

                pendingDuration = null;
                pendingDurationSeconds = 0;
                pendingByteRange = null;
                inspection.Resources.Add(lineResource);
            }

            return inspection;
        }

        public static string MakeReferencesAbsolute(string content, string baseUrl)
        {
            if (string.IsNullOrEmpty(content) || !IsHttpUrl(baseUrl))
            {
                return content;
            }

            return RewriteReferences(
                content,
                baseUrl,
                delegate(string resolvedUrl) { return resolvedUrl; });
        }

        public static string RewriteReferences(
            string content,
            string baseUrl,
            Func<string, string> rewrite)
        {
            if (string.IsNullOrEmpty(content) || rewrite == null)
            {
                return content;
            }

            StringBuilder result = new StringBuilder(content.Length + 256);
            int position = 0;
            while (position < content.Length)
            {
                int lineEnd = position;
                while (lineEnd < content.Length &&
                       content[lineEnd] != '\r' && content[lineEnd] != '\n')
                {
                    lineEnd++;
                }

                string rawLine = content.Substring(position, lineEnd - position);
                result.Append(RewriteLineReferences(rawLine, baseUrl, rewrite));

                if (lineEnd >= content.Length)
                {
                    break;
                }

                if (content[lineEnd] == '\r' && lineEnd + 1 < content.Length &&
                    content[lineEnd + 1] == '\n')
                {
                    result.Append("\r\n");
                    position = lineEnd + 2;
                }
                else
                {
                    result.Append(content[lineEnd]);
                    position = lineEnd + 1;
                }
            }

            return result.ToString();
        }

        private static string RewriteLineReferences(
            string rawLine,
            string baseUrl,
            Func<string, string> rewrite)
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                return rawLine;
            }

            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                return UriAttribute.Replace(
                    rawLine,
                    delegate(Match match)
                    {
                        return RewriteAttributeReference(match, baseUrl, rewrite);
                    });
            }

            string rewritten = RewriteReference(baseUrl, trimmed, rewrite);
            if (string.Equals(rewritten, trimmed, StringComparison.Ordinal))
            {
                return rawLine;
            }

            int start = rawLine.IndexOf(trimmed, StringComparison.Ordinal);
            return rawLine.Substring(0, start) + rewritten +
                   rawLine.Substring(start + trimmed.Length);
        }

        private static string RewriteAttributeReference(
            Match match,
            string baseUrl,
            Func<string, string> rewrite)
        {
            string original = GetUriValue(match);
            string rewritten = RewriteReference(baseUrl, original, rewrite);
            if (string.Equals(original, rewritten, StringComparison.Ordinal))
            {
                return match.Value;
            }

            string prefix = match.Groups["prefix"].Value;
            if (match.Groups["double"].Success)
            {
                return prefix + "\"" + rewritten + "\"";
            }

            if (match.Groups["single"].Success)
            {
                return prefix + "'" + rewritten + "'";
            }

            return prefix + rewritten;
        }

        private static string RewriteReference(
            string baseUrl,
            string reference,
            Func<string, string> rewrite)
        {
            string resolved = ResolveReference(baseUrl, reference);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            string rewritten = rewrite(resolved);
            return string.IsNullOrEmpty(rewritten) ? resolved : rewritten;
        }

        private static string GetUriValue(Match match)
        {
            if (match.Groups["double"].Success)
            {
                return match.Groups["double"].Value;
            }

            return match.Groups["single"].Success
                ? match.Groups["single"].Value
                : match.Groups["plain"].Value;
        }

        private static string ResolveReference(string baseUrl, string reference)
        {
            if (string.IsNullOrWhiteSpace(reference) ||
                reference.IndexOf("{$", StringComparison.Ordinal) >= 0)
            {
                return reference == null ? null : reference.Trim();
            }

            string trimmed = reference.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                Uri baseUri;
                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out baseUri) &&
                    (string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                {
                    return baseUri.Scheme + ":" + trimmed;
                }
            }

            return M3u8SizeProbe.ResolveUrl(baseUrl, trimmed);
        }

        private static string DescribeAttributeResource(string line)
        {
            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                return "密钥";
            }

            if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                return "初始化片段";
            }

            if (line.StartsWith("#EXT-X-PART:", StringComparison.OrdinalIgnoreCase))
            {
                return "低延迟分片";
            }

            if (line.StartsWith("#EXT-X-PRELOAD-HINT:", StringComparison.OrdinalIgnoreCase))
            {
                return "预加载资源";
            }

            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
            {
                return "媒体轨道";
            }

            if (line.StartsWith("#EXT-X-I-FRAME-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                return "I 帧播放列表";
            }

            return "关联资源";
        }

        private static string DescribeAttributeDetail(string line)
        {
            if (line.StartsWith("#EXT-X-PART:", StringComparison.OrdinalIgnoreCase))
            {
                string duration = FindAttributeValue(line, "DURATION");
                return string.IsNullOrEmpty(duration) ? null : duration + " 秒";
            }

            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
            {
                string type = FindAttributeValue(line, "TYPE");
                string name = FindAttributeValue(line, "NAME");
                return JoinDetails(type, name);
            }

            return null;
        }

        private static string FindAttributeValue(string line, string name)
        {
            Match match = Regex.Match(
                line,
                @"(?:^|[:,])\s*" + Regex.Escape(name) + @"\s*=\s*(?:""(?<quoted>[^""]*)""|(?<plain>[^,]*))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return null;
            }

            return (match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["plain"].Value).Trim();
        }

        private static string ParseExtInfDetail(string line)
        {
            int colon = line.IndexOf(':');
            if (colon < 0 || colon + 1 >= line.Length)
            {
                return null;
            }

            string value = line.Substring(colon + 1);
            int comma = value.IndexOf(',');
            string duration = (comma >= 0 ? value.Substring(0, comma) : value).Trim();
            string title = comma >= 0 && comma + 1 < value.Length
                ? value.Substring(comma + 1).Trim()
                : null;

            string durationDetail = duration.Length == 0 ? null : duration + " 秒";
            return JoinDetails(durationDetail, title);
        }

        private static double ParseExtInfDurationSeconds(string line)
        {
            int colon = line.IndexOf(':');
            if (colon < 0 || colon + 1 >= line.Length)
            {
                return 0;
            }

            string value = line.Substring(colon + 1);
            int comma = value.IndexOf(',');
            string duration = (comma >= 0 ? value.Substring(0, comma) : value).Trim();
            double seconds;
            return double.TryParse(
                duration,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out seconds) && seconds > 0
                ? seconds
                : 0;
        }

        private static string JoinDetails(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first))
            {
                return string.IsNullOrWhiteSpace(second) ? null : second;
            }

            return string.IsNullOrWhiteSpace(second) ? first : first + "；" + second;
        }

        private static bool IsHttpUrl(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                   (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
        }

        private static string[] SplitLines(string text)
        {
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
    }

    internal sealed class BlobPlaylistMessage
    {
        public string BlobUrl;
        public string PageUrl;
        public string BaseUrl;
        public string UserAgent;
        public string PlaylistContent;
    }

    internal static class BlobPlaylistMessageParser
    {
        public const string Marker = "N_M3U8DL_RE_BLOB_PLAYLIST_V1";
        public const string MarkerV2 = "N_M3U8DL_RE_BLOB_PLAYLIST_V2";
        private const int MaximumMessageCharacters = 24 * 1024 * 1024;

        public static bool TryParse(string message, out BlobPlaylistMessage result)
        {
            result = null;
            if (string.IsNullOrEmpty(message) || message.Length > MaximumMessageCharacters)
            {
                return false;
            }

            bool isV2 = message.StartsWith(MarkerV2 + "\n", StringComparison.Ordinal);
            string[] fields = message.Split(
                new[] { '\n' },
                isV2 ? 6 : 5,
                StringSplitOptions.None);
            if ((!isV2 && (fields.Length != 5 ||
                           !string.Equals(fields[0], Marker, StringComparison.Ordinal))) ||
                (isV2 && (fields.Length != 6 ||
                          !string.Equals(fields[0], MarkerV2, StringComparison.Ordinal))) ||
                !PlaylistInput.IsBlobUrl(fields[1]))
            {
                return false;
            }

            try
            {
                int contentIndex = isV2 ? 5 : 4;
                byte[] bytes = Convert.FromBase64String(fields[contentIndex]);
                string content = new UTF8Encoding(false, true).GetString(bytes);
                if (!PlaylistInput.LooksLikePlaylistContent(content) ||
                    !content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n')
                        .StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                BlobPlaylistMessage parsed = new BlobPlaylistMessage();
                parsed.BlobUrl = fields[1].Trim();
                parsed.PageUrl = fields[2].Trim();
                parsed.BaseUrl = fields[isV2 ? 3 : 2].Trim();
                parsed.UserAgent = fields[isV2 ? 4 : 3].Trim();
                parsed.PlaylistContent = content;
                result = parsed;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }
    }
}
