using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using BepInEx.Logging;

namespace SyncVideo.Runtime
{
    public static class YouTube
    {
        private const int DefaultTargetHeight = 480;
        private static readonly TimeSpan StreamUrlCacheDuration = TimeSpan.FromHours(4);

        private static readonly ManualLogSource Logger =
            BepInEx.Logging.Logger.CreateLogSource("SyncVideo.YouTube");

        private static readonly Dictionary<string, StreamCacheEntry> StreamUrlCache =
            new Dictionary<string, StreamCacheEntry>(StringComparer.Ordinal);
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, PendingRequest> PendingRequests =
            new Dictionary<string, PendingRequest>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Process> ActiveProcesses =
            new Dictionary<string, Process>(StringComparer.Ordinal);
        private static readonly HashSet<string> CancelledRequests =
            new HashSet<string>(StringComparer.Ordinal);

        // Use HTTP server to play the ffmpeg videos at higher quality
        private static readonly LocalFileServer FileServer = new LocalFileServer();

        // Track file paths that are currently being downloaded to so they don't get deleted when the cache clears
        private static readonly System.Collections.Generic.HashSet<string> _activeDownloadPaths =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string _cachedFfmpegPath;
        private static bool   _ffmpegPathSearched;
        private static string _cachedYtDlpPath;
        private static bool   _ytDlpPathSearched;

        // Public API
        public static void ResolveAsync(
            string videoId,
            string originalUrl,
            Action<string> onResolved,
            Action<string> onError)
        {
            if (string.IsNullOrEmpty(videoId))
            {
                onError?.Invoke("No video ID provided!");
                return;
            }

            int targetHeight = GetConfigTargetResolutionHeight();
            // Only use ffmpeg mode if both config toggle is on AND executable exists
            bool ffmpegEnabled = SyncVideoPlugin.Settings?.UseFFmpeg?.Value ?? false;
            string ffmpegPath = ffmpegEnabled ? FindFfmpeg() : null;

            if (ffmpegPath != null)
                DownloadAndCacheAsync(videoId, originalUrl, targetHeight, ffmpegPath, onResolved, onError);
            else
                StreamUrlResolveAsync(videoId, originalUrl, targetHeight, onResolved, onError);
        }

        public static void ValidateSuggestionAsync(
            string originalUrl,
            string videoId,
            string directPlayableUrl,
            Action<string> onSuccess,
            Action<string> onError)
        {
            if (string.IsNullOrWhiteSpace(originalUrl))
            {
                onError?.Invoke("URL Error!");
                return;
            }

            if (!UrlNormalizer.ValidateSubmissionUrl(originalUrl, videoId, directPlayableUrl, out var validationError))
            {
                onError?.Invoke(validationError);
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (string.IsNullOrEmpty(videoId))
                    {
                        if (!Uri.TryCreate(directPlayableUrl ?? originalUrl, UriKind.Absolute, out var uri)
                            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        {
                            onError?.Invoke("URL Error!");
                            return;
                        }

                        onSuccess?.Invoke(originalUrl);
                        return;
                    }

                    string ytDlpPath = FindYtDlp();
                    if (ytDlpPath == null)
                    {
                        onError?.Invoke("Video not supported!");
                        return;
                    }

                    if (IsLivestream(ytDlpPath, originalUrl))
                    {
                        onError?.Invoke("Video not supported!");
                        return;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = ytDlpPath,
                        Arguments = $"--no-playlist --no-warnings --skip-download --print title -- \"{originalUrl}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    using (var process = Process.Start(psi))
                    {
                        string stdout = process.StandardOutput.ReadToEnd();
                        string stderr = process.StandardError.ReadToEnd();
                        process.WaitForExit(20_000);

                        if (process.ExitCode != 0)
                        {
                            if (IsUrlError(stderr))
                                onError?.Invoke("URL Error!");
                            else if (IsAgeRestrictedError(stderr))
                                onError?.Invoke("<color=red>Video not supported!\nVideo is age-restricted.</color>");
                            else
                                onError?.Invoke("Video not supported!");
                            return;
                        }

                        string title = FirstNonEmpty(stdout);
                        if (string.IsNullOrWhiteSpace(title))
                        {
                            onError?.Invoke("Video not supported!");
                            return;
                        }

                        onSuccess?.Invoke(title);
                    }
                }
                catch
                {
                    onError?.Invoke("Video not supported!");
                }
            });
        }

        public static bool IsFfmpegAvailable() =>
            (SyncVideoPlugin.Settings?.UseFFmpeg?.Value ?? false) && FindFfmpeg() != null;

        // Delete MP4s in the cache folder
        private static float _lastCacheClearTime = -999f;
        private const float CacheClearCooldown = 2f;

        public static void ClearAllCache()
        {
            string cacheDir = GetCacheDirectory();
            if (!Directory.Exists(cacheDir)) return;

            // Check if any MP4s actually exist before any deletes
            string[] files = Directory.GetFiles(cacheDir, "*.mp4");
            if (files.Length == 0) return;

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _lastCacheClearTime < CacheClearCooldown) return;
            _lastCacheClearTime = now;

            lock (CacheLock)
            {
                foreach (string file in files)
                {
                    // Never delete a file that yt-dlp is currently writing
                    if (_activeDownloadPaths.Contains(file))
                    {
                        Logger.LogInfo($"[YouTube] Skipping active download during cache clear: {Path.GetFileName(file)}");
                        continue;
                    }
                    try { File.Delete(file); }
                    catch { /* file may still be in use; skip silently */ }
                }
            }
            Logger.LogInfo("[YouTube] Cache cleared.");
        }

        public static void InvalidateCache(string videoId)
        {
            if (string.IsNullOrEmpty(videoId)) return;

            lock (CacheLock)
            {
                var toRemove = new List<string>();
                foreach (var key in StreamUrlCache.Keys)
                    if (key.StartsWith(videoId + "@", StringComparison.Ordinal))
                        toRemove.Add(key);
                foreach (var key in toRemove)
                    StreamUrlCache.Remove(key);
            }

            string cacheDir = GetCacheDirectory();
            if (!Directory.Exists(cacheDir)) return;
            foreach (string file in Directory.GetFiles(cacheDir, SanitiseId(videoId) + "_*.mp4"))
            {
                try { File.Delete(file); }
                catch { }
            }
        }

        private static void DownloadAndCacheAsync(
            string videoId,
            string originalUrl,
            int targetHeight,
            string ffmpegPath,
            Action<string> onResolved,
            Action<string> onError)
        {
            string cachedFile = GetCachedFilePath(videoId, targetHeight);

            if (File.Exists(cachedFile))
            {
                Logger.LogInfo($"[YouTube] Video already in cache folder: {videoId} @ {targetHeight}p");
                onResolved?.Invoke(FileServer.Serve(cachedFile));
                return;
            }

            string requestKey = $"download:{videoId}@{targetHeight}";
            if (!TryBeginPendingRequest(requestKey, onResolved, onError))
                return;

            // Lock output file so ClearAllCache does not delete it
            lock (CacheLock) { _activeDownloadPaths.Add(cachedFile); }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string ytDlpPath = FindYtDlp();
                    if (ytDlpPath == null) { CompletePendingError(requestKey, "yt-dlp.exe not found!"); return; }

                    // reject livestreams
                    if (IsLivestream(ytDlpPath, originalUrl))
                    {
                        Logger.LogInfo($"[YouTube] Blocked download of livestream: {videoId}");
                        CompletePendingError(requestKey, "Livestreams are not supported!");
                        return;
                    }

                    Directory.CreateDirectory(GetCacheDirectory());

                    string ffmpegDir = Path.GetDirectoryName(ffmpegPath) ?? string.Empty;
                    Logger.LogInfo($"[YouTube] Downloading {videoId} @ {targetHeight}p (ffmpeg merge)...");

                    var psi = new ProcessStartInfo
                    {
                        FileName = ytDlpPath,
                        Arguments = $"--no-playlist --no-warnings" +
                                    $" --ffmpeg-location \"{ffmpegDir}\"" +
                                    $" -f \"{BuildFormatWithFfmpeg(targetHeight)}\"" +
                                    $" --merge-output-format mp4" +
                                    $" -o \"{cachedFile}\"" +
                                    $" -- \"{originalUrl}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    bool success = false;
                    string lastError = null;

                    for (int attempt = 1; attempt <= 2 && !success; attempt++)
                    {
                        CleanupPartialDownloadFiles(videoId, targetHeight);

                        using (var process = Process.Start(psi))
                        {
                            RegisterActiveProcess(requestKey, process);
                            if (IsRequestCancelled(requestKey))
                            {
                                try { if (!process.HasExited) process.Kill(); } catch { }
                                return;
                            }

                            process.StandardOutput.ReadToEnd();
                            string stderr = process.StandardError.ReadToEnd();
                            process.WaitForExit(300_000);

                            if (process.ExitCode == 0 && WaitForFileReady(cachedFile, 2000))
                            {
                                success = true;
                                break;
                            }

                            TryDelete(cachedFile);
                            lastError = FirstNonEmpty(stderr) ?? "Unknown yt-dlp error.";
                            Logger.LogError($"[YouTube] Download failed ({process.ExitCode}) attempt {attempt}: {lastError}");

                            if (attempt < 2 && IsRetryableDownloadError(lastError))
                            {
                                Thread.Sleep(300);
                                continue;
                            }

                            CompletePendingError(requestKey, IsAgeRestrictedError(lastError)
                                ? "<color=red>Video not supported!\nVideo is age-restricted.</color>"
                                : $"Download failed: {lastError}");
                            return;
                        }
                    }

                    if (!success)
                    {
                        CompletePendingError(requestKey, IsAgeRestrictedError(lastError)
                            ? "Video not supported!\nVideo is age-restricted."
                            : $"Download failed: {lastError ?? "Unknown yt-dlp error."}");
                        return;
                    }

                    Logger.LogInfo($"[YouTube] Download complete: {videoId} @ {targetHeight}p");
                    CompletePendingSuccess(requestKey, FileServer.Serve(cachedFile));
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[YouTube] Exception: {ex}");
                    CompletePendingError(requestKey, $"YouTube exception: {ex.Message}");
                }
                finally
                {
                    // Unregister so ClearAllCache can remove it
                    lock (CacheLock) { _activeDownloadPaths.Remove(cachedFile); }
                }
            });
        }

        // fallback if ffmpeg is not enabled or its not in the folder
        private static void StreamUrlResolveAsync(
            string videoId,
            string originalUrl,
            int targetHeight,
            Action<string> onResolved,
            Action<string> onError)
        {
            string cacheKey = $"{videoId}@{targetHeight}";

            lock (CacheLock)
            {
                if (StreamUrlCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expiry)
                {
                    Logger.LogInfo($"[YouTube] URL cache hit: {videoId} @ {targetHeight}p");
                    onResolved?.Invoke(cached.Url);
                    return;
                }
            }

            string requestKey = $"stream:{cacheKey}";
            if (!TryBeginPendingRequest(requestKey, onResolved, onError))
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string ytDlpPath = FindYtDlp();
                    if (ytDlpPath == null) { CompletePendingError(requestKey, "yt-dlp.exe not found!"); return; }

                    Logger.LogInfo($"[YouTube] Loading Video URL: {videoId} @ {targetHeight}p");

                    var psi = new ProcessStartInfo
                    {
                        FileName = ytDlpPath,
                        Arguments = $"--no-playlist --no-warnings" +
                                    $" -f \"{BuildFormatWithoutFfmpeg(targetHeight)}\"" +
                                    $" --get-url -- \"{originalUrl}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    using (var process = Process.Start(psi))
                    {
                        RegisterActiveProcess(requestKey, process);
                        if (IsRequestCancelled(requestKey))
                        {
                            try { if (!process.HasExited) process.Kill(); } catch { }
                            return;
                        }

                        string stdout = process.StandardOutput.ReadToEnd();
                        string stderr = process.StandardError.ReadToEnd();
                        process.WaitForExit(30_000);

                        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                        {
                            string errLine = FirstNonEmpty(stderr) ?? "No output from yt-dlp!";
                            Logger.LogError($"[YouTube] yt-dlp error ({process.ExitCode}): {errLine}");
                            CompletePendingError(requestKey, IsAgeRestrictedError(errLine)
                                ? "Video not supported!\nVideo is age-restricted."
                                : $"yt-dlp error: {errLine}");
                            return;
                        }

                        string directUrl = FirstNonEmpty(stdout);
                        if (string.IsNullOrEmpty(directUrl)) { CompletePendingError(requestKey, "yt-dlp returned an empty URL."); return; }

                        Logger.LogInfo($"[YouTube] Loaded {videoId}: {directUrl.Substring(0, Math.Min(80, directUrl.Length))}...");

                        lock (CacheLock)
                            StreamUrlCache[cacheKey] = new StreamCacheEntry(directUrl, DateTime.UtcNow + StreamUrlCacheDuration);

                        CompletePendingSuccess(requestKey, directUrl);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[YouTube] Exception: {ex}");
                    CompletePendingError(requestKey, $"YouTube Exception: {ex.Message}");
                }
            });
        }

        public static void CancelAllPendingRequests()
        {
            List<Process> processesToKill = null;

            lock (CacheLock)
            {
                // ToList() copy avoids modifying the collection while iterating
                foreach (var key in new List<string>(PendingRequests.Keys))
                    CancelledRequests.Add(key);

                PendingRequests.Clear();

                // Deduplicate processes with a HashSet, no LINQ needed
                var seen = new HashSet<Process>();
                processesToKill = new List<Process>(ActiveProcesses.Count);
                foreach (var p in ActiveProcesses.Values)
                    if (p != null && seen.Add(p))
                        processesToKill.Add(p);

                ActiveProcesses.Clear();
            }

            foreach (var process in processesToKill)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch { }
            }
        }

        private static void RegisterActiveProcess(string requestKey, Process process)
        {
            if (process == null)
                return;

            lock (CacheLock)
                ActiveProcesses[requestKey] = process;
        }

        private static void UnregisterActiveProcess(string requestKey, Process process = null)
        {
            lock (CacheLock)
            {
                if (!ActiveProcesses.TryGetValue(requestKey, out var existing))
                    return;

                if (process != null && !ReferenceEquals(existing, process))
                    return;

                ActiveProcesses.Remove(requestKey);
            }
        }

        private static bool IsRequestCancelled(string requestKey)
        {
            lock (CacheLock)
                return CancelledRequests.Contains(requestKey);
        }

        private static bool TryBeginPendingRequest(string requestKey, Action<string> onResolved, Action<string> onError)
        {
            lock (CacheLock)
            {
                if (PendingRequests.TryGetValue(requestKey, out var pending))
                {
                    pending.OnResolved.Add(onResolved);
                    pending.OnError.Add(onError);
                    Logger.LogInfo($"[YouTube] Joining pending request: {requestKey}");
                    return false;
                }

                CancelledRequests.Remove(requestKey);
                pending = new PendingRequest();
                pending.OnResolved.Add(onResolved);
                pending.OnError.Add(onError);
                PendingRequests[requestKey] = pending;
                return true;
            }
        }

        private static void CompletePendingSuccess(string requestKey, string resolvedUrl)
        {
            PendingRequest pending = null;
            bool cancelled;
            lock (CacheLock)
            {
                ActiveProcesses.Remove(requestKey);
                cancelled = CancelledRequests.Remove(requestKey);
                if (PendingRequests.TryGetValue(requestKey, out pending))
                    PendingRequests.Remove(requestKey);
            }

            if (pending == null || cancelled)
                return;

            foreach (var callback in pending.OnResolved)
                callback?.Invoke(resolvedUrl);
        }

        private static void CompletePendingError(string requestKey, string errorMessage)
        {
            PendingRequest pending = null;
            bool cancelled;
            lock (CacheLock)
            {
                ActiveProcesses.Remove(requestKey);
                cancelled = CancelledRequests.Remove(requestKey);
                if (PendingRequests.TryGetValue(requestKey, out pending))
                    PendingRequests.Remove(requestKey);
            }

            if (pending == null || cancelled)
                return;

            foreach (var callback in pending.OnError)
                callback?.Invoke(errorMessage);
        }

        private static bool IsUrlError(string stderr)
        {
            string err = (stderr ?? string.Empty).ToLowerInvariant();
            return err.Contains("unsupported url")
                || err.Contains("invalid url")
                || err.Contains("bad url")
                || err.Contains("no such file")
                || err.Contains("unable to download webpage")
                || err.Contains("unable to extract")
                || err.Contains("not a valid url");
        }

        private static bool IsAgeRestrictedError(string stderr)
        {
            string err = (stderr ?? string.Empty).ToLowerInvariant();
            return err.Contains("age-restricted")
                || err.Contains("age restricted")
                || err.Contains("sign in to confirm your age")
                || err.Contains("this video may be inappropriate for some users");
        }


        private static bool WaitForFileReady(string path, int timeoutMs)
        {
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            if (stream.Length > 0)
                                return true;
                        }
                    }
                }
                catch
                {
                }

                Thread.Sleep(100);
            }

            return File.Exists(path);
        }

        private static bool IsRetryableDownloadError(string error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return false;

            return error.IndexOf("WinError 2", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("cannot find the file specified", StringComparison.OrdinalIgnoreCase) >= 0
                || error.IndexOf("Unable to rename file", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void CleanupPartialDownloadFiles(string videoId, int targetHeight)
        {
            string cacheDir = GetCacheDirectory();
            if (!Directory.Exists(cacheDir))
                return;

            string prefix = $"{SanitiseId(videoId)}_{targetHeight}";
            foreach (string file in Directory.GetFiles(cacheDir, prefix + "*"))
            {
                if (string.Equals(file, GetCachedFilePath(videoId, targetHeight), StringComparison.OrdinalIgnoreCase))
                    continue;

                try { File.Delete(file); } catch { }
            }
        }

        private static string BuildFormatWithFfmpeg(int height)
        {
            // force H.264 video
            return $"bestvideo[height<={height}][ext=mp4][vcodec^=avc]+bestaudio[ext=m4a]" +
                   $"/bestvideo[height<={height}][ext=mp4][vcodec^=avc]+bestaudio[acodec^=mp4a]" +
                   $"/bestvideo[height<={height}][ext=mp4]+bestaudio[ext=m4a]" +
                   $"/best[height<={height}][ext=mp4]" +
                   $"/best[ext=mp4]";
        }

        private static string BuildFormatWithoutFfmpeg(int height)
        {
            // Combined single-file streams
            // Format 18  = 360p, MP4/H.264
            // Format 22 = 720p, MP4/H.264
            // Format 43 = 360p, WebM/VP8
            // Format 44 = 480p, WebM/VP8
            // Format 45 = 720p, WebM/VP8
            // Format 59 = 480p, MP4/H.264
            // Format 78 = 480p, MP4/H.264
            if (height >= 720)
            {
                return "22/45" +
                       "/best[height<=720][fps>30][ext=mp4][protocol=https]" +
                       "/best[height<=720][fps>30][protocol=https]" +
                       "/best[height<=720][ext=mp4][protocol=https]" +
                       "/best[height<=720][protocol=https]" +
                       "/18/43/best[protocol=https]";
            }
            else if (height >= 480)
            {
                return $"59/78/44" +
                       $"/best[height<={height}][fps>30][ext=mp4][protocol=https]" +
                       $"/best[height<={height}][fps>30][protocol=https]" +
                       $"/best[height<={height}][ext=mp4][protocol=https]" +
                       $"/best[height<={height}][protocol=https]" +
                       $"/18/43/best[protocol=https]";
            }
            else if (height >= 360)
            {
                return "18/43" +
                       "/best[height<=360][ext=mp4][protocol=https]" +
                       "/best[height<=360][protocol=https]" +
                       "/best[protocol=https]";
            }
            else
            {
                return $"best[height<={height}][ext=mp4][protocol=https]" +
                       $"/best[height<={height}][protocol=https]" +
                       $"/best[protocol=https]";
            }
        }

        // Helpers
        internal static string GetCacheDirectory()
        {
            string pluginDir = SyncVideoPlugin.Settings?.PluginDirectory ?? string.Empty;
            return Path.Combine(pluginDir, "cache");
        }

        private static string GetCachedFilePath(string videoId, int height)
            => Path.Combine(GetCacheDirectory(), $"{SanitiseId(videoId)}_{height}.mp4");

        private static string SanitiseId(string videoId)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in videoId)
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            return sb.Length > 0 ? sb.ToString() : "unknown";
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static int GetConfigTargetResolutionHeight()
        {
            string raw = SyncVideoPlugin.Settings?.YouTubeStreamResolution?.Value;
            if (!string.IsNullOrEmpty(raw))
            {
                string normalised = raw.Trim().ToLowerInvariant().Replace('×', 'x');
                int sep = normalised.IndexOf('x');
                if (sep >= 0 && int.TryParse(normalised.Substring(sep + 1), out int h) && h > 0)
                    return h;
            }
            return DefaultTargetHeight;
        }

        // Pull the title for YT vids
        public static void ResolveTitleAsync(string originalUrl, string videoId, Action<string> onTitle)
        {
            // For MP4s grab URL
            if (string.IsNullOrEmpty(videoId))
            {
                onTitle?.Invoke(originalUrl ?? string.Empty);
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string ytDlpPath = FindYtDlp();
                    if (ytDlpPath == null) { onTitle?.Invoke(originalUrl); return; }

                    var psi = new ProcessStartInfo
                    {
                        FileName = ytDlpPath,
                        Arguments = $"--no-playlist --no-warnings --print title -- \"{originalUrl}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    using (var process = Process.Start(psi))
                    {
                        string stdout = process.StandardOutput.ReadToEnd().Trim();
                        process.StandardError.ReadToEnd();
                        process.WaitForExit(15_000);
                        onTitle?.Invoke(string.IsNullOrEmpty(stdout) ? originalUrl : stdout);
                    }
                }
                catch { onTitle?.Invoke(originalUrl); }
            });
        }


        public static void ResolveTitleAndUploaderAsync(string originalUrl, string videoId, Action<string, string> onResolved)
        {
            if (string.IsNullOrEmpty(videoId))
            {
                onResolved?.Invoke(originalUrl ?? string.Empty, string.Empty);
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string ytDlpPath = FindYtDlp();
                    if (ytDlpPath == null)
                    {
                        onResolved?.Invoke(originalUrl ?? string.Empty, string.Empty);
                        return;
                    }

                    var psi = new ProcessStartInfo
                    {
                        FileName = ytDlpPath,
                        Arguments = $"--no-playlist --no-warnings --skip-download --print title --print uploader -- \"{originalUrl}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    using (var process = Process.Start(psi))
                    {

                        string stdout = process.StandardOutput.ReadToEnd();
                        process.StandardError.ReadToEnd();
                        process.WaitForExit(15_000);

                        string title = originalUrl ?? string.Empty;
                        string uploader = string.Empty;
                        // Plain loop avoids LINQ allocation on the background thread.
                        int found = 0;
                        foreach (string rawLine in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            string trimmed = rawLine.Trim();
                            if (string.IsNullOrEmpty(trimmed)) continue;
                            if (found == 0) title    = trimmed;
                            else            uploader  = trimmed;
                            if (++found == 2) break;
                        }

                        onResolved?.Invoke(title, uploader);
                    }
                }
                catch
                {
                    onResolved?.Invoke(originalUrl ?? string.Empty, string.Empty);
                }
            });
        }

        // Force error if video is a livestream to prevent downloading forever and not showing anything
        private static bool IsLivestream(string ytDlpPath, string url)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ytDlpPath,
                    Arguments = $"--no-playlist --no-warnings --print is_live -- \"{url}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using (var process = Process.Start(psi))
                {
                    string stdout = process.StandardOutput.ReadToEnd().Trim();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit(15_000);
                    return string.Equals(stdout, "True", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static string FindYtDlp()
        {
            lock (CacheLock)
            {
                if (_ytDlpPathSearched)
                    return _cachedYtDlpPath;
                _ytDlpPathSearched = true;
                string pluginDir = SyncVideoPlugin.Settings?.PluginDirectory ?? string.Empty;
                if (!string.IsNullOrEmpty(pluginDir))
                    foreach (string name in new[] { "yt-dlp.exe", "yt-dlp" })
                    {
                        string c = Path.Combine(pluginDir, name);
                        if (File.Exists(c)) { _cachedYtDlpPath = c; return c; }
                    }
                _cachedYtDlpPath = null;
                return null;
            }
        }

        private static string FindFfmpeg()
        {
            lock (CacheLock)
            {
                if (_ffmpegPathSearched)
                    return _cachedFfmpegPath;
                _ffmpegPathSearched = true;
                string pluginDir = SyncVideoPlugin.Settings?.PluginDirectory ?? string.Empty;
                if (!string.IsNullOrEmpty(pluginDir))
                    foreach (string name in new[] { "ffmpeg.exe", "ffmpeg" })
                    {
                        string c = Path.Combine(pluginDir, name);
                        if (File.Exists(c)) { _cachedFfmpegPath = c; return c; }
                    }
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (string segment in pathEnv.Split(Path.PathSeparator))
                    foreach (string name in new[] { "ffmpeg.exe", "ffmpeg" })
                    {
                        try { string c = Path.Combine(segment.Trim(), name); if (File.Exists(c)) { _cachedFfmpegPath = c; return c; } }
                        catch { }
                    }
                _cachedFfmpegPath = null;
                return null;
            }
        }

        private static string FirstNonEmpty(string multiline)
        {
            if (string.IsNullOrEmpty(multiline)) return null;
            foreach (string line in multiline.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (!string.IsNullOrEmpty(t)) return t;
            }
            return null;
        }
        private sealed class PendingRequest
        {
            public readonly List<Action<string>> OnResolved = new List<Action<string>>();
            public readonly List<Action<string>> OnError = new List<Action<string>>();
        }

        private readonly struct StreamCacheEntry
        {
            public readonly string Url;
            public readonly DateTime Expiry;
            public StreamCacheEntry(string url, DateTime expiry) { Url = url; Expiry = expiry; }
        }

        // Local HTTP file server for ffmpeg playback after DL
        private sealed class LocalFileServer
        {
            private HttpListener _listener;
            private int _port;
            private bool _started;
            private readonly object _lock = new object();

            public string Serve(string filePath)
            {
                EnsureStarted();
                // Include the filename in the URL so the server can find the right file
                string fileName = Uri.EscapeDataString(Path.GetFileName(filePath));
                return $"http://127.0.0.1:{_port}/{fileName}";
            }

            private void EnsureStarted()
            {
                lock (_lock)
                {
                    if (_started) return;
                    _listener = new HttpListener();
                    _port = GetFreePort();
                    _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                    _listener.Start();
                    _started = true;

                    Logger.LogInfo($"[YouTube] Local file server started on port {_port}");
                    ThreadPool.QueueUserWorkItem(_ => AcceptLoop());
                }
            }

            private static int GetFreePort()
            {
                var tmp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                tmp.Start();
                int port = ((System.Net.IPEndPoint)tmp.LocalEndpoint).Port;
                tmp.Stop();
                return port;
            }

            private void AcceptLoop()
            {
                while (_listener?.IsListening == true)
                {
                    HttpListenerContext ctx;
                    try { ctx = _listener.GetContext(); }
                    catch { break; }
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
            }

            private void HandleRequest(HttpListenerContext ctx)
            {
                try
                {
                    string rawName = ctx.Request.Url.AbsolutePath.TrimStart('/');
                    string fileName = Uri.UnescapeDataString(rawName);
                    string cacheDir = GetCacheDirectory();
                    string filePath = Path.Combine(cacheDir, fileName);

                    if (!File.Exists(filePath))
                    {
                        Logger.LogWarning($"[YouTube] Server: file not found: {filePath}");
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        return;
                    }

                    long fileLength = new FileInfo(filePath).Length;
                    ctx.Response.ContentType = "video/mp4";
                    ctx.Response.AddHeader("Accept-Ranges", "bytes");

                    string rangeHeader = ctx.Request.Headers["Range"];
                    long start = 0;
                    long end = fileLength - 1;

                    if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                    {
                        string[] parts = rangeHeader.Substring(6).Split('-');
                        if (parts.Length >= 1 && long.TryParse(parts[0], out long s)) start = s;
                        if (parts.Length >= 2 && long.TryParse(parts[1], out long e)) end = e;
                        ctx.Response.StatusCode = 206;
                        ctx.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{fileLength}");
                    }
                    else
                    {
                        ctx.Response.StatusCode = 200;
                    }

                    long length = end - start + 1;
                    ctx.Response.ContentLength64 = length;

                    using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        fs.Seek(start, SeekOrigin.Begin);
                        byte[] buf = new byte[65536];
                        long remaining = length;
                        while (remaining > 0)
                        {
                            int toRead = (int)Math.Min(buf.Length, remaining);
                            int read = fs.Read(buf, 0, toRead);
                            if (read == 0) break;
                            ctx.Response.OutputStream.Write(buf, 0, read);
                            remaining -= read;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex.Message.IndexOf("forcibly closed by the remote host", StringComparison.OrdinalIgnoreCase) >= 0)
                        Logger.LogInfo("[YouTube] Server request closed by client.");
                    else
                        Logger.LogError($"[YouTube] Server request error: {ex.Message}");
                    try { ctx.Response.StatusCode = 500; } catch { }
                }
                finally
                {
                    try { ctx.Response.Close(); } catch { }
                }
            }
        }
    }
}
