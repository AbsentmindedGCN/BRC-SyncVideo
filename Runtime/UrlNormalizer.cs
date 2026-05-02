using System;
using System.Net;
using System.Text.RegularExpressions;

namespace SyncVideo.Runtime
{
    public static class UrlNormalizer
    {
        private static readonly Regex YoutubeWatch = new Regex(
            @"(?:youtube\.com\/watch\?v=|youtu\.be\/)([\w\-]{6,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex YoutubeShorts = new Regex(
            @"youtube\.com\/shorts\/([\w\-]{6,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] AllowedDirectMediaExtensions =
        {
            ".mp4", ".webm", ".m4v", ".mov", ".avi", ".mkv",
        };

        public const int MaxDirectSuggestionUrlLength = 180;
        public static bool IsMkvUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            try
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                    return (uri.AbsolutePath ?? string.Empty)
                        .EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
            }
            catch { }

            return value.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsMkvSubmissionAllowed(string normalizedUrl, string videoId, string directPlayableUrl, out string error)
        {
            error = string.Empty;
            if (!string.IsNullOrWhiteSpace(videoId))
                return true;

            var candidate = !string.IsNullOrWhiteSpace(directPlayableUrl) ? directPlayableUrl : normalizedUrl;
            if (!IsMkvUrl(candidate))
                return true;

            if (SyncVideoPlugin.Settings?.EnableMkvSupport?.Value ?? false)
                return true;

            error = "MKV Not Supported\n\n<size=70%>Please enable experimental features in config.</size>";
            return false;
        }

        public static bool IsDirectSuggestionUrlTooLong(string normalizedUrl, string videoId, string directPlayableUrl, out string error)
        {
            error = string.Empty;

            if (!string.IsNullOrWhiteSpace(videoId))
                return false;

            var candidate = !string.IsNullOrWhiteSpace(directPlayableUrl) ? directPlayableUrl : normalizedUrl;
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            if (candidate.Length > MaxDirectSuggestionUrlLength)
            {
                error = "MP4 URL length too long!";
                return true;
            }

            return false;
        }

        public static bool ValidateSubmissionUrl(string normalizedUrl, string videoId, string directPlayableUrl, out string error)
        {
            error = string.Empty;
            var candidate = !string.IsNullOrWhiteSpace(directPlayableUrl) ? directPlayableUrl : normalizedUrl;

            if (!IsMkvSubmissionAllowed(normalizedUrl, videoId, directPlayableUrl, out error))
                return false;

            if (!Uri.TryCreate(candidate ?? string.Empty, UriKind.Absolute, out var uri))
            {
                error = "URL Error!";
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            {
                error = "Only http/https URLs are supported.";
                return false;
            }

            if (IsBlockedHost(uri.Host))
            {
                error = "Local or private network URLs are not allowed.";
                return false;
            }

            // YouTube
            if (!string.IsNullOrWhiteSpace(videoId))
                return true;

            var path = (uri.AbsolutePath ?? string.Empty).ToLowerInvariant();
            for (int i = 0; i < AllowedDirectMediaExtensions.Length; i++)
            {
                if (path.EndsWith(AllowedDirectMediaExtensions[i], StringComparison.Ordinal))
                    return true;
            }

            error = "Direct links must point to a supported video file.";
            return false;
        }

        private static bool IsBlockedHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return true;

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!IPAddress.TryParse(host, out var ip))
                return false;

            if (IPAddress.IsLoopback(ip))
                return true;

            var bytes = ip.GetAddressBytes();

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
            {
                if (bytes[0] == 10 || bytes[0] == 127)
                    return true;
                if (bytes[0] == 169 && bytes[1] == 254)
                    return true;
                if (bytes[0] == 192 && bytes[1] == 168)
                    return true;
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                    return true;
            }

            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
                    return true;
                if (bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC)
                    return true;
            }

            return false;
        }

        public static string Normalize(string raw, out string videoId, out string directPlayableUrl)
        {
            raw = (raw ?? string.Empty).Trim();
            videoId = string.Empty;
            directPlayableUrl = raw;

            if (string.IsNullOrEmpty(raw))
                return raw;

            var shortsMatch = YoutubeShorts.Match(raw);
            if (shortsMatch.Success)
            {
                videoId = shortsMatch.Groups[1].Value;
                directPlayableUrl = string.Empty;

                return $"https://www.youtube.com/watch?v={videoId}";
            }

            var match = YoutubeWatch.Match(raw);
            if (match.Success)
            {
                videoId = match.Groups[1].Value;
                directPlayableUrl = string.Empty;

                return $"https://www.youtube.com/watch?v={videoId}";
            }

            return raw;
        }
    }
}