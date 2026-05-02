using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;

namespace SyncVideo.Runtime
{
    public sealed class SubtitleTrackInfo
    {
        public int StreamIndex;
        public string Language;
        public string Title;
    }

    public sealed class AudioTrackInfo
    {
        public int StreamIndex;
        public int AudioIndex;
        public string Language;
        public string Title;
        public string CodecName;

        public string GetMenuLabel()
        {
            string name = !string.IsNullOrWhiteSpace(Title) ? Title.Trim() : string.Empty;
            string lang = !string.IsNullOrWhiteSpace(Language) &&
                          !Language.Equals("und", StringComparison.OrdinalIgnoreCase)
                ? Language.Trim()
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(lang))
                return name + " - [" + lang + "]";
            if (!string.IsNullOrWhiteSpace(name))
                return name;
            if (!string.IsNullOrWhiteSpace(lang))
                return "[" + lang + "]";
            if (!string.IsNullOrWhiteSpace(CodecName))
                return CodecName;
            return string.Empty;
        }
    }

    public sealed class SubtitleEntry
    {
        public double StartTime;
        public double EndTime;
        public string Text;
    }

    // Probes MKV/video for subtitle tracks using FFmpeg
    public sealed class SubtitleManager
    {
        private readonly ManualLogSource _logger;

        // Volatile refs written on BG thread
        private volatile List<SubtitleTrackInfo> _tracks = new List<SubtitleTrackInfo>();
        private volatile List<SubtitleEntry> _entries = new List<SubtitleEntry>();

        private int _selectedTrack = -1; // disabled
        private int _searchHint = 0;
        private volatile bool _isProbing = false;
        private volatile bool _isExtracting = false;
        private volatile bool _hasLoggedSubtitleReady = false;
        private CancellationTokenSource _sessionCts;
        private CancellationTokenSource _selectionCts;

        public int TrackCount => _tracks.Count;
        public int SelectedTrack => _selectedTrack;
        public bool IsProbing => _isProbing;
        public bool IsExtracting => _isExtracting;

        public SubtitleManager(ManualLogSource logger)
        {
            _logger = logger;
        }

        public string GetTrackLabel(int index)
        {
            var tracks = _tracks;
            if (index < 0 || index >= tracks.Count)
                return "Subtitle Track " + (index + 1);

            var t = tracks[index];
            string label = "Subtitle Track " + (index + 1);

            string title = StripOuterBrackets(t.Title);
            if (!string.IsNullOrWhiteSpace(title))
                label += " (" + title + ")";
            else
            {
                string lang = FormatLanguage(StripOuterBrackets(t.Language));
                if (!string.IsNullOrWhiteSpace(lang))
                    label += " (" + lang + ")";
            }
            return label;
        }

        // Strip brackets
        private static string StripOuterBrackets(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            string s = value.Trim();
            if (s.Length >= 2 && s[0] == '[' && s[s.Length - 1] == ']')
                return s.Substring(1, s.Length - 2).Trim();
            return s;
        }

        // Map ISO 639 language codes
        private static string FormatLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language) ||
                language.Equals("und", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            switch (language.Trim().ToLowerInvariant())
            {
                case "en":  case "eng":             return "English";
                case "ja":  case "jpn": case "jp":  return "Japanese";
                case "es":  case "spa":             return "Spanish";
                case "fr":  case "fre": case "fra": return "French";
                case "de":  case "ger": case "deu": return "German";
                case "it":  case "ita":             return "Italian";
                case "pt":  case "por":             return "Portuguese";
                case "zh":  case "zho": case "chi": return "Chinese";
                case "ko":  case "kor":             return "Korean";
                case "ru":  case "rus":             return "Russian";
                case "ar":  case "ara":             return "Arabic";
                default:                            return language.Trim();
            }
        }

        // Subtitle probing
        public static bool IsSubtitleProbeSupported(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            string clean = url;
            int queryIndex = clean.IndexOf('?');
            if (queryIndex >= 0)
                clean = clean.Substring(0, queryIndex);

            string extension = Path.GetExtension(clean);
            if (string.IsNullOrWhiteSpace(extension))
                return false;

            switch (extension.ToLowerInvariant())
            {
                case ".mkv":
                case ".mp4":
                case ".m4v":
                case ".mov":
                case ".webm":
                case ".avi":
                    return true;
                default:
                    return false;
            }
        }

        // Check if there is subtitles in the video file submitted
        public void ProbeAsync(string url, string ffmpegPath, Action onComplete)
        {
            CancelAllWork();
            _tracks = new List<SubtitleTrackInfo>();
            _entries = new List<SubtitleEntry>();
            _selectedTrack = -1;
            _searchHint = 0;
            _hasLoggedSubtitleReady = false;

            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(ffmpegPath))
            {
                onComplete?.Invoke();
                return;
            }

            if (_trackCache.TryGetValue(url, out var cachedTracks))
            {
                _tracks = new List<SubtitleTrackInfo>(cachedTracks);
                onComplete?.Invoke();
                return;
            }

            _isProbing = true;
            _sessionCts = new CancellationTokenSource();
            var token = _sessionCts.Token;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!token.IsCancellationRequested)
                    {
                        var detected = ProbeSubtitleTracks(url, ffmpegPath, token);
                        if (!token.IsCancellationRequested)
                        {
                            _tracks = detected;
                            if (detected != null && detected.Count > 0)
                                _trackCache[url] = new List<SubtitleTrackInfo>(detected);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_logger != null)
                        _logger.LogWarning("[SubtitleManager] Probe error: " + ex.Message);
                }
                finally
                {
                    _isProbing = false;
                    if (!token.IsCancellationRequested)
                        onComplete?.Invoke();
                }
            });
        }

        // Subtitle track select
        public void SelectTrack(int trackIndex, string url, string ffmpegPath, Action onComplete)
        {
            CancelSelectionOnly();
            _entries = new List<SubtitleEntry>();
            _searchHint = 0;
            _hasLoggedSubtitleReady = false;

            var tracks = _tracks;
            if (trackIndex < 0 || trackIndex >= tracks.Count)
            {
                _selectedTrack = -1;
                onComplete?.Invoke();
                return;
            }

            _selectedTrack = trackIndex;
            _isExtracting = true;
            _selectionCts = new CancellationTokenSource();
            var selectionToken = _selectionCts.Token;
            var sessionToken = _sessionCts != null ? _sessionCts.Token : CancellationToken.None;
            int streamIndex = tracks[trackIndex].StreamIndex;

            string cacheKey = BuildSubtitleCacheKey(url, streamIndex);
            if (TryGetParsedSubtitleEntries(cacheKey, out var cachedEntries))
            {
                _entries = cachedEntries;
                LogSubtitlesReady(cachedEntries != null ? cachedEntries.Count : 0, false);
                _isExtracting = false;
                onComplete?.Invoke();
                return;
            }

            if (TryLoadSubtitleEntriesFromDiskCache(cacheKey, out cachedEntries))
            {
                _entries = cachedEntries;
                LogSubtitlesReady(cachedEntries != null ? cachedEntries.Count : 0, false);
                _isExtracting = false;
                onComplete?.Invoke();
                return;
            }

            int firstProgressNotified = 0;

            Action<List<SubtitleEntry>> progressListener = partialEntries =>
            {
                if (selectionToken.IsCancellationRequested || partialEntries == null || partialEntries.Count == 0)
                    return;

                _entries = partialEntries;
                LogSubtitlesReady(partialEntries.Count, true);

                // Refresh after the selected track has enough subtitles to display
                if (Interlocked.Exchange(ref firstProgressNotified, 1) == 0)
                {
                    _isExtracting = false;
                    onComplete?.Invoke();
                }
            };

            AddSubtitleProgressListener(cacheKey, progressListener);

            if (TryGetPartialSubtitleEntries(cacheKey, out var partialCachedEntries) && partialCachedEntries.Count > 0)
            {
                _entries = partialCachedEntries;
                LogSubtitlesReady(partialCachedEntries.Count, true);
            }

            var extractionTask = GetOrStartSubtitleExtraction(cacheKey, url, streamIndex, ffmpegPath, sessionToken);

            extractionTask.ContinueWith(t =>
            {
                RemoveSubtitleProgressListener(cacheKey, progressListener);

                if (selectionToken.IsCancellationRequested)
                    return;

                try
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result != null)
                    {
                        if (TryGetParsedSubtitleEntries(cacheKey, out var parsedEntries))
                        {
                            _entries = parsedEntries;
                            LogSubtitlesReady(parsedEntries != null ? parsedEntries.Count : 0, false);
                        }
                        else
                        {
                            _entries = ParseAndCacheSubtitleEntries(cacheKey, t.Result);
                            LogSubtitlesReady(_entries != null ? _entries.Count : 0, false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_logger != null)
                        _logger.LogWarning("[SubtitleManager] Extract error: " + ex.Message);
                }
                finally
                {
                    bool shouldNotify = Interlocked.Exchange(ref firstProgressNotified, 1) == 0;
                    _isExtracting = false;
                    if (shouldNotify && !selectionToken.IsCancellationRequested)
                        onComplete?.Invoke();
                }
            }, TaskScheduler.Default);
        }

        public void DisableSubtitles()
        {
            Cancel();
            _selectedTrack = -1;
            _entries = new List<SubtitleEntry>();
            _searchHint = 0;
            _hasLoggedSubtitleReady = false;
        }

        // Get the subtitle track so host can select which track to use like VLC
        internal void EagerExtractAllTracksAsync(string url, string ffmpegPath)
        {
            var tracks = _tracks;
            if (tracks == null || tracks.Count == 0 || string.IsNullOrWhiteSpace(url))
                return;

            var sessionToken = _sessionCts != null ? _sessionCts.Token : CancellationToken.None;
            int selectedTrack = _selectedTrack;

            if (selectedTrack < 0 || selectedTrack >= tracks.Count)
                return;

            int selectedStreamIndex = tracks[selectedTrack].StreamIndex;
            string selectedCacheKey = BuildSubtitleCacheKey(url, selectedStreamIndex);

            var pendingKeys    = new List<string>(tracks.Count);
            var pendingIndexes = new List<int>(tracks.Count);

            for (int i = 0; i < tracks.Count; i++)
            {
                int streamIndex = tracks[i].StreamIndex;
                string cacheKey = BuildSubtitleCacheKey(url, streamIndex);

                if (TryGetParsedSubtitleEntries(cacheKey, out _))
                    continue;
                if (TryLoadSubtitleEntriesFromDiskCache(cacheKey, out _))
                    continue;

                // prioritize selected subtitle track!!
                if (streamIndex == selectedStreamIndex)
                    continue;

                pendingKeys.Add(cacheKey);
                pendingIndexes.Add(streamIndex);
            }

            ThreadPool.QueueUserWorkItem(state =>
            {
                try
                {
                    if (!sessionToken.IsCancellationRequested &&
                        !TryGetParsedSubtitleEntries(selectedCacheKey, out _))
                    {
                        GetOrStartSubtitleExtraction(selectedCacheKey, url, selectedStreamIndex, ffmpegPath, sessionToken).Wait();
                    }

                    // Only preload the remaining tracks after the active track
                    for (int i = 0; i < pendingKeys.Count; i++)
                    {
                        if (sessionToken.IsCancellationRequested)
                            break;

                        string cacheKey    = pendingKeys[i];
                        int    streamIndex = pendingIndexes[i];

                        if (TryGetParsedSubtitleEntries(cacheKey, out _))
                            continue;

                        GetOrStartSubtitleExtraction(cacheKey, url, streamIndex, ffmpegPath, sessionToken).Wait();
                    }
                }
                catch
                {
                }
            });
        }

        public void Cancel()
        {
            CancelSelectionOnly();
        }

        private void CancelSelectionOnly()
        {
            if (_selectionCts != null)
            {
                _selectionCts.Cancel();
                _selectionCts = null;
            }
            _isExtracting = false;
        }

        private void CancelAllWork()
        {
            CancelSelectionOnly();

            if (_sessionCts != null)
            {
                _sessionCts.Cancel();
                _sessionCts = null;
            }

            KillActiveSubtitleProcesses();
            _isProbing = false;
        }

        public void Clear()
        {
            if ((_isExtracting || (_entries != null && _entries.Count > 0)) && _selectedTrack >= 0)
            {
                var extractionStatus = _isExtracting
                ? "Was extracting."
                : "Extraction finished.";

                _logger?.LogWarning(
                    $"[SubtitleManager] Lobby shutting down, subtitles abandoned! Track #{_selectedTrack} has {_entries.Count} Entries. {extractionStatus}"
                );
            }

            CancelAllWork();
            _tracks = new List<SubtitleTrackInfo>();
            _entries = new List<SubtitleEntry>();
            _selectedTrack = -1;
            _searchHint = 0;
            _hasLoggedSubtitleReady = false;
        }

        // Subtitle playback
        public string GetActiveSubtitle(double time)
        {
            var entries = _entries;
            if (entries == null || entries.Count == 0)
                return null;

            int count = entries.Count;

            if (_searchHint >= count)
                _searchHint = 0;

            var hint = entries[_searchHint];
            if (time >= hint.StartTime && time < hint.EndTime)
            {
                return hint.Text;
            }

            // Seek forwards
            if (time >= hint.EndTime)
            {
                for (int i = _searchHint + 1; i < count; i++)
                {
                    var e = entries[i];
                    if (time < e.StartTime)
                    {
                        _searchHint = i;
                        return null;
                    }
                    if (time < e.EndTime)
                    {
                        _searchHint = i;
                        return e.Text;
                    }
                }
                _searchHint = count - 1;
                return null;
            }
            
            // Seek backwards
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                var e = entries[mid];
                if (time < e.StartTime)      hi = mid - 1;
                else if (time >= e.EndTime)  lo = mid + 1;
                else
                {
                    _searchHint = mid;
                    return e.Text;
                }
            }
            return null;
        }

        private void LogSubtitlesReady(int loadedEntryCount, bool stillLoading)
        {
            if (_hasLoggedSubtitleReady || loadedEntryCount <= 0)
                return;

            _hasLoggedSubtitleReady = true;

            if (_logger != null)
            {
                var statusMessage = stillLoading
                    ? "Subtitles loading in background..."
                    : "Subtitles complete!";

                _logger.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[SubtitleManager] Subtitles ready: Sub Track #{0} pre-loaded, {1} Entries Loaded. {2}",
                    _selectedTrack, loadedEntryCount, statusMessage));
            }
        }

        public void ResetSearchHint()
        {
            _searchHint = 0;
        }

        // SubRip Text Parsing
        private static readonly Regex _htmlTagRx = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex _ssaOverrideTagRx = new Regex(@"\{\\[^}]*\}", RegexOptions.Compiled);

        // Probe Subtitle Tracks
        private static readonly Regex _titleLineRx = new Regex(@"^\s+title\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Probe Video Codec
        private static readonly Regex _videoCodecRx = new Regex(@"Stream #0:\d+[^:]*: Video: (\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly ConcurrentDictionary<string, List<SubtitleTrackInfo>> _trackCache = new ConcurrentDictionary<string, List<SubtitleTrackInfo>>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, string> _subtitleSrtCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, List<SubtitleEntry>> _subtitleEntryCache = new ConcurrentDictionary<string, List<SubtitleEntry>>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, byte> _subtitleCompleteCache = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, Task<string>> _subtitleExtractTasks = new ConcurrentDictionary<string, Task<string>>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, Process> _subtitleProcesses = new ConcurrentDictionary<string, Process>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, List<Action<List<SubtitleEntry>>>> _subtitleProgressListeners = new ConcurrentDictionary<string, List<Action<List<SubtitleEntry>>>>(StringComparer.Ordinal);

        internal static List<SubtitleEntry> ParseSrt(string content)
        {
            var result = new List<SubtitleEntry>();
            if (string.IsNullOrWhiteSpace(content))
                return result;

            var reader = new StringReader(content);
            var textBuilder = new System.Text.StringBuilder(256);
            string line;

            while (true)
            {
                // Scan for the next timing line, skipping sequence numbers and blank lines
                string timingLine = null;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.IndexOf("-->", StringComparison.Ordinal) >= 0)
                    {
                        timingLine = line;
                        break;
                    }
                }

                if (timingLine == null)
                    break; // EOF

                string[] parts = timingLine.Split(new[] { "-->" }, StringSplitOptions.None);
                if (parts.Length < 2)
                    continue;

                if (!TryParseSrtTime(parts[0].Trim(), out double start))
                    continue;
                if (!TryParseSrtTime(parts[1].Trim(), out double end))
                    continue;
                if (end <= start)
                    continue;

                // Collect text lines until a blank line or EOF
                textBuilder.Clear();
                while ((line = reader.ReadLine()) != null && line.Length > 0)
                {
                    if (textBuilder.Length > 0)
                        textBuilder.Append('\n');
                    textBuilder.Append(line);
                }

                if (textBuilder.Length == 0)
                    continue;

                string text = CleanSubtitleText(textBuilder.ToString());
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(new SubtitleEntry { StartTime = start, EndTime = end, Text = text });
            }

            reader.Dispose();
            result.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            return result;
        }


        private static string CleanSubtitleText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // Strip the stupid ASS subtitle formatting tags like {\an8}
            text = _ssaOverrideTagRx.Replace(text, string.Empty);
            text = _htmlTagRx.Replace(text, string.Empty);
            return text.Trim();
        }

        private static bool TryParseSrtTime(string s, out double seconds)
        {
            seconds = 0d;
            s = s.Replace(',', '.').Trim();
            int colon1 = s.IndexOf(':');
            int colon2 = s.IndexOf(':', colon1 + 1);
            if (colon1 < 0 || colon2 < 0)
                return false;

            if (!int.TryParse(s.Substring(0, colon1), out int h))
                return false;
            if (!int.TryParse(s.Substring(colon1 + 1, colon2 - colon1 - 1), out int m))
                return false;
            if (!double.TryParse(s.Substring(colon2 + 1),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double sec))
                return false;

            seconds = h * 3600d + m * 60d + sec;
            return true;
        }

        // FFmpeg helpers
        private static readonly Regex _streamSubRx = new Regex(
            @"Stream #0:(\d+)(?:\(([^)]+)\))?[^:]*: Subtitle",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static List<SubtitleTrackInfo> ProbeSubtitleTracks(
            string url, string ffmpegPath, CancellationToken token)
        {
            var result = new List<SubtitleTrackInfo>();
            string ffprobePath = FindFfprobePath(ffmpegPath);

            if (!string.IsNullOrWhiteSpace(ffprobePath))
            {
                try
                {
                    var ffprobePsi = new ProcessStartInfo
                    {
                        FileName = ffprobePath,
                        // -probesize and -analyzeduration keep the probe fast for larger files otherwise its slow as fuck
                        Arguments = $"-v error -probesize 10000000 -analyzeduration 10000000" +
                                    $" -show_entries stream=index,codec_type:stream_tags=language,title" +
                                    $" -select_streams s -of default=noprint_wrappers=0 \"{EscapeArg(url)}\"",
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var ffprobeProc = new Process { StartInfo = ffprobePsi })
                    {
                        CancellationTokenRegistration probeCancelRegistration = default(CancellationTokenRegistration);
                        try
                        {
                            ffprobeProc.Start();
                            probeCancelRegistration = token.Register(() => KillProcess(ffprobeProc));
                            string ffprobeOutput = ffprobeProc.StandardOutput.ReadToEnd();
                            ffprobeProc.WaitForExit(15000);

                            if (token.IsCancellationRequested)
                                return result;

                            ParseFfprobeSubtitleStreams(ffprobeOutput, result, token);
                            if (result.Count > 0)
                                return result;
                        }
                        finally
                        {
                            probeCancelRegistration.Dispose();
                        }
                    }
                }
                catch
                {
                    // Just fall back to FFmpeg stderr parsing
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath, // -t 0 causes FFmpeg to exit after reading headers; -probesize limits initial read
                Arguments = $"-probesize 10000000 -analyzeduration 10000000 -i \"{EscapeArg(url)}\" -hide_banner",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = new Process { StartInfo = psi })
            {
                CancellationTokenRegistration probeCancelRegistration = default(CancellationTokenRegistration);
                string stderr;
                try
                {
                    proc.Start();
                    probeCancelRegistration = token.Register(() => KillProcess(proc));

                    // Read all stderr, grab stream info from ffmpeg
                    stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(15000);
                }
                finally
                {
                    probeCancelRegistration.Dispose();
                }

                if (token.IsCancellationRequested)
                    return result;

                // finalise each subtitle entry only when the next line arrives
                SubtitleTrackInfo currentSub = null;
                int subIndex = 0;

                foreach (var raw in stderr.Split('\n'))
                {
                    if (token.IsCancellationRequested) break;

                    string line = raw.TrimEnd('\r');

                    if (line.IndexOf("Stream #", StringComparison.Ordinal) >= 0)
                    {
                        // Finalise the previous subtitle track before moving on
                        if (currentSub != null)
                        {
                            result.Add(currentSub);
                            currentSub = null;
                        }

                        var streamMatch = _streamSubRx.Match(line);
                        if (streamMatch.Success)
                        {
                            currentSub = new SubtitleTrackInfo
                            {
                                StreamIndex = int.Parse(streamMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                                Language    = StripOuterBrackets(streamMatch.Groups[2].Value.Trim())
                            };
                            subIndex++;
                        }
                        continue;
                    }

                    // Title metadata
                    if (currentSub != null)
                    {
                        var titleMatch = _titleLineRx.Match(line);
                        if (titleMatch.Success)
                            currentSub.Title = StripOuterBrackets(titleMatch.Groups[1].Value.Trim());
                    }
                }

                if (currentSub != null && !token.IsCancellationRequested)
                    result.Add(currentSub);
            }

            return result;
        }

        private List<SubtitleEntry> ParseAndCacheSubtitleEntries(string cacheKey, string srt)
        {
            var parsed = ParseSrt(srt);
            if (parsed == null)
                parsed = new List<SubtitleEntry>();

            _subtitleEntryCache[cacheKey] = parsed;
            _subtitleCompleteCache[cacheKey] = 1;
            return parsed;
        }

        private static bool TryGetParsedSubtitleEntries(string cacheKey, out List<SubtitleEntry> entries)
        {
            if (_subtitleCompleteCache.ContainsKey(cacheKey) &&
                _subtitleEntryCache.TryGetValue(cacheKey, out entries) && entries != null)
                return true;

            entries = null;
            return false;
        }

        private static bool TryGetPartialSubtitleEntries(string cacheKey, out List<SubtitleEntry> entries)
        {
            if (_subtitleEntryCache.TryGetValue(cacheKey, out entries) && entries != null)
                return true;

            entries = null;
            return false;
        }

        private static void AddSubtitleProgressListener(string cacheKey, Action<List<SubtitleEntry>> listener)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || listener == null)
                return;

            var listeners = _subtitleProgressListeners.GetOrAdd(cacheKey, _ => new List<Action<List<SubtitleEntry>>>());
            lock (listeners)
                listeners.Add(listener);
        }

        private static void RemoveSubtitleProgressListener(string cacheKey, Action<List<SubtitleEntry>> listener)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || listener == null)
                return;

            if (!_subtitleProgressListeners.TryGetValue(cacheKey, out var listeners) || listeners == null)
                return;

            lock (listeners)
                listeners.Remove(listener);
        }

        private static void PublishSubtitleProgress(string cacheKey, List<SubtitleEntry> entries)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || entries == null || entries.Count == 0)
                return;

            var snapshot = new List<SubtitleEntry>(entries);
            snapshot.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));
            _subtitleEntryCache[cacheKey] = snapshot;

            if (!_subtitleProgressListeners.TryGetValue(cacheKey, out var listeners) || listeners == null)
                return;

            Action<List<SubtitleEntry>>[] listenerSnapshot;
            lock (listeners)
                listenerSnapshot = listeners.ToArray();

            for (int i = 0; i < listenerSnapshot.Length; i++)
            {
                try
                {
                    listenerSnapshot[i]?.Invoke(snapshot);
                }
                catch
                {
                }
            }
        }

        private static bool TryParseSrtBlock(List<string> lines, out SubtitleEntry entry)
        {
            entry = null;
            if (lines == null || lines.Count == 0)
                return false;

            int timingIndex = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].IndexOf("-->", StringComparison.Ordinal) >= 0)
                {
                    timingIndex = i;
                    break;
                }
            }

            if (timingIndex < 0)
                return false;

            string[] parts = lines[timingIndex].Split(new[] { "-->" }, StringSplitOptions.None);
            if (parts.Length < 2)
                return false;

            if (!TryParseSrtTime(parts[0].Trim(), out double start))
                return false;
            if (!TryParseSrtTime(parts[1].Trim(), out double end))
                return false;
            if (end <= start)
                return false;

            var textBuilder = new StringBuilder(256);
            for (int i = timingIndex + 1; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;
                if (textBuilder.Length > 0)
                    textBuilder.Append('\n');
                textBuilder.Append(lines[i]);
            }

            if (textBuilder.Length == 0)
                return false;

            string text = CleanSubtitleText(textBuilder.ToString());
            if (string.IsNullOrWhiteSpace(text))
                return false;

            entry = new SubtitleEntry { StartTime = start, EndTime = end, Text = text };
            return true;
        }

        private List<SubtitleEntry> LoadAndParseSubtitleEntriesFromDisk(string cacheKey)
        {
            if (!TryGetCachedSubtitleSrt(cacheKey, out var srt) || string.IsNullOrWhiteSpace(srt))
                return null;

            return ParseAndCacheSubtitleEntries(cacheKey, srt);
        }

        private bool TryLoadSubtitleEntriesFromDiskCache(string cacheKey, out List<SubtitleEntry> entries)
        {
            if (TryGetParsedSubtitleEntries(cacheKey, out entries))
                return true;

            entries = LoadAndParseSubtitleEntriesFromDisk(cacheKey);
            return entries != null;
        }

        private Task<string> GetOrStartSubtitleExtraction(string cacheKey, string url, int subtitleStreamIndex, string ffmpegPath, CancellationToken sessionToken)
        {
            return _subtitleExtractTasks.GetOrAdd(cacheKey, _ =>
                Task.Run(() =>
                {
                    try
                    {
                        if (TryGetCachedSubtitleSrt(cacheKey, out var cachedSrt) && !string.IsNullOrWhiteSpace(cachedSrt))
                        {
                            ParseAndCacheSubtitleEntries(cacheKey, cachedSrt);
                            return cachedSrt;
                        }

                        string srt = ExtractSubtitleSrt(cacheKey, url, subtitleStreamIndex, ffmpegPath, sessionToken);
                        if (string.IsNullOrWhiteSpace(srt) || sessionToken.IsCancellationRequested)
                            return null;

                        _subtitleSrtCache[cacheKey] = srt;
                        var parsedEntries = ParseAndCacheSubtitleEntries(cacheKey, srt);
                        QueueDiskCacheWrite(cacheKey, srt, subtitleStreamIndex, parsedEntries != null ? parsedEntries.Count : 0, _logger);
                        return srt;
                    }
                    finally
                    {
                        Task<string> removedTask;
                        _subtitleExtractTasks.TryRemove(cacheKey, out removedTask);
                    }
                }, sessionToken));
        }

        private static void QueueDiskCacheWrite(string cacheKey, string srt, int subtitleStreamIndex, int entryCount, ManualLogSource logger)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(srt))
                return;

            ThreadPool.QueueUserWorkItem(_ => StoreCachedSubtitleSrt(cacheKey, srt, subtitleStreamIndex, entryCount, logger));
        }

        private static string ExtractSubtitleSrt(
            string cacheKey, string url, int subtitleStreamIndex, string ffmpegPath, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                // subtitleStreamIndex is the absolute FFmpeg stream index from ffprobe
                Arguments = $"-nostdin -probesize 10000000 -analyzeduration 1000000 -i \"{EscapeArg(url)}\" -vn -an -dn -map 0:{subtitleStreamIndex} -c:s srt -f srt pipe:1 -hide_banner -loglevel error",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = new Process { StartInfo = psi })
            {
                CancellationTokenRegistration cancelRegistration = default(CancellationTokenRegistration);
                try
                {
                    proc.Start();
                    _subtitleProcesses[cacheKey] = proc;
                    cancelRegistration = token.Register(() => KillProcess(proc));
                    proc.BeginErrorReadLine();

                    var fullSrt = new StringBuilder(64 * 1024);
                    var blockLines = new List<string>(8);
                    var parsedEntries = new List<SubtitleEntry>();
                    var publishTimer = Stopwatch.StartNew();
                    bool publishedAny = false;
                    string line;

                    while (!token.IsCancellationRequested && (line = proc.StandardOutput.ReadLine()) != null)
                    {
                        fullSrt.AppendLine(line);

                        if (line.Length == 0)
                        {
                            if (TryParseSrtBlock(blockLines, out var entry))
                            {
                                parsedEntries.Add(entry);

                                if (!publishedAny || publishTimer.ElapsedMilliseconds >= 50)
                                {
                                    PublishSubtitleProgress(cacheKey, parsedEntries);
                                    publishTimer.Restart();
                                    publishedAny = true;
                                }
                            }

                            blockLines.Clear();
                            continue;
                        }

                        blockLines.Add(line);
                    }

                    if (token.IsCancellationRequested)
                        return null;

                    if (TryParseSrtBlock(blockLines, out var finalEntry))
                        parsedEntries.Add(finalEntry);

                    if (parsedEntries.Count > 0)
                        PublishSubtitleProgress(cacheKey, parsedEntries);

                    if (!proc.WaitForExit(60000))
                    {
                        KillProcess(proc);
                        return null;
                    }

                    if (token.IsCancellationRequested)
                        return null;

                    string output = fullSrt.ToString();
                    return string.IsNullOrWhiteSpace(output) ? null : output;
                }
                finally
                {
                    cancelRegistration.Dispose();
                    Process removedProcess;
                    _subtitleProcesses.TryRemove(cacheKey, out removedProcess);
                }
            }
        }

        private static void KillActiveSubtitleProcesses()
        {
            foreach (var pair in _subtitleProcesses.ToArray())
            {
                KillProcess(pair.Value);
            }

            _subtitleProcesses.Clear();
        }

        private static void KillProcess(Process proc)
        {
            if (proc == null)
                return;

            try
            {
                if (!proc.HasExited)
                    proc.Kill();
            }
            catch
            {
            }
        }

        private static string BuildSubtitleCacheKey(string url, int subtitleStreamIndex)
        {
            return (url ?? string.Empty) + "|" + subtitleStreamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool TryGetCachedSubtitleSrt(string cacheKey, out string srt)
        {
            if (_subtitleSrtCache.TryGetValue(cacheKey, out srt) && !string.IsNullOrWhiteSpace(srt))
                return true;

            string diskPath = GetSubtitleCachePath(cacheKey);
            try
            {
                if (File.Exists(diskPath))
                {
                    srt = File.ReadAllText(diskPath);
                    if (!string.IsNullOrWhiteSpace(srt))
                    {
                        _subtitleSrtCache[cacheKey] = srt;
                        return true;
                    }
                }
            }
            catch
            {
            }

            srt = null;
            return false;
        }

        private static void StoreCachedSubtitleSrt(string cacheKey, string srt, int subtitleStreamIndex, int entryCount, ManualLogSource logger)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(srt))
                return;

            _subtitleSrtCache[cacheKey] = srt;

            string cacheDir = GetSubtitleCacheDirectory();
            if (string.IsNullOrWhiteSpace(cacheDir))
                return;

            try
            {
                Directory.CreateDirectory(cacheDir);
                string finalPath = GetSubtitleCachePath(cacheKey);
                string tempPath = finalPath + ".tmp";
                File.WriteAllText(tempPath, srt);
                if (File.Exists(finalPath))
                    File.Delete(finalPath);
                File.Move(tempPath, finalPath);

                logger?.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[SubtitleManager] Subtitle track cached! Sub Track #{0} complete, {1} Entries loaded. Cache Path: {2}",
                    subtitleStreamIndex, entryCount, finalPath));
            }
            catch (Exception ex)
            {
                logger?.LogError("[SubtitleManager] Failed to cache subtitle track " + subtitleStreamIndex + ": " + ex.Message);
            }
        }

        private static string GetSubtitleCacheDirectory()
        {
            string pluginDir = SyncVideoPlugin.Settings?.PluginDirectory ?? string.Empty;
            if (string.IsNullOrWhiteSpace(pluginDir))
                return string.Empty;

            return Path.Combine(pluginDir, "SubtitleCache");
        }

        private static string GetSubtitleCachePath(string cacheKey)
        {
            string cacheDir = GetSubtitleCacheDirectory();
            uint hash = 2166136261u;
            foreach (char c in cacheKey ?? string.Empty)
                hash = (hash ^ (uint)c) * 16777619u;
            return Path.Combine(cacheDir, string.Format(System.Globalization.CultureInfo.InvariantCulture, "sub_{0:x8}.srt", hash));
        }

        // Fix for double quotes
        private static string EscapeArg(string s)
        {
            return s?.Replace("\"", "\\\"") ?? string.Empty;
        }

        // FFmpeg path
        private static void ParseFfprobeSubtitleStreams(string output, List<SubtitleTrackInfo> result, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(output))
                return;

            string[] blocks = output.Replace("\r\n", "\n").Split(new[] { "[STREAM]", "[/STREAM]" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawBlock in blocks)
            {
                if (token.IsCancellationRequested)
                    break;

                string block = rawBlock.Trim();
                if (block.Length == 0)
                    continue;

                string codecType = string.Empty;
                string language = string.Empty;
                string title = string.Empty;

                foreach (var rawLine in block.Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("codec_type=", StringComparison.OrdinalIgnoreCase))
                        codecType = line.Substring("codec_type=".Length).Trim();
                    else if (line.StartsWith("TAG:language=", StringComparison.OrdinalIgnoreCase))
                    {
                        string lang = StripOuterBrackets(line.Substring("TAG:language=".Length).Trim());
                        if (!string.IsNullOrWhiteSpace(lang) && !lang.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                            language = lang;
                    }
                    else if (line.StartsWith("TAG:title=", StringComparison.OrdinalIgnoreCase))
                    {
                        string t2 = StripOuterBrackets(line.Substring("TAG:title=".Length).Trim());
                        if (!string.IsNullOrWhiteSpace(t2) && !t2.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                            title = t2;
                    }
                }

                if (!codecType.Equals("subtitle", StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new SubtitleTrackInfo
                {
                    StreamIndex = result.Count,
                    Language = language,
                    Title = title
                });
            }
        }

        public static List<AudioTrackInfo> ProbeAudioTracks(string url, string ffmpegPath, ManualLogSource logger = null)
        {
            var result = new List<AudioTrackInfo>();
            string ffprobePath = FindFfprobePath(ffmpegPath);
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(ffprobePath))
                return result;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -probesize 100M -analyzeduration 100M" +
                                $" -select_streams a -show_entries stream=index,codec_type,codec_name:stream_tags=language,title" +
                                $" -of default=noprint_wrappers=0 \"{EscapeArg(url)}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(30000);
                    ParseFfprobeAudioStreams(output, result);
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning("[SubtitleManager] Audio probe error: " + ex.Message);
            }

            for (int i = 0; i < result.Count; i++)
            {
                result[i].AudioIndex = i;
                logger?.LogInfo("[SubtitleManager] Audio stream " + (i + 1) +
                                ": streamIndex=" + result[i].StreamIndex +
                                ", codec=" + (result[i].CodecName ?? string.Empty) +
                                ", language=" + (result[i].Language ?? string.Empty) +
                                ", title=" + (result[i].Title ?? string.Empty));
            }

            return result;
        }

        private static void ParseFfprobeAudioStreams(string output, List<AudioTrackInfo> result)
        {
            if (string.IsNullOrWhiteSpace(output))
                return;

            AudioTrackInfo current = null;
            foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("index=", StringComparison.OrdinalIgnoreCase))
                {
                    if (current != null && current.StreamIndex >= 0)
                        result.Add(current);
                    current = new AudioTrackInfo { StreamIndex = -1, AudioIndex = result.Count };
                    int streamIndex;
                    if (int.TryParse(line.Substring("index=".Length).Trim(), out streamIndex))
                        current.StreamIndex = streamIndex;
                    continue;
                }

                if (current == null)
                    current = new AudioTrackInfo { StreamIndex = -1, AudioIndex = result.Count };

                if (line.StartsWith("codec_name=", StringComparison.OrdinalIgnoreCase))
                    current.CodecName = line.Substring("codec_name=".Length).Trim();
                else if (line.StartsWith("TAG:language=", StringComparison.OrdinalIgnoreCase))
                    current.Language = line.Substring("TAG:language=".Length).Trim();
                else if (line.StartsWith("TAG:title=", StringComparison.OrdinalIgnoreCase))
                    current.Title = line.Substring("TAG:title=".Length).Trim();
            }

            if (current != null && current.StreamIndex >= 0)
                result.Add(current);
        }

        public static string GetMkvAudioTrackCachedOutputPath(string sourceUrl, string playableUrl, int streamIndex)
        {
            string pluginDir = SyncVideoPlugin.Settings?.PluginDirectory ?? string.Empty;
            string cacheDir  = Path.Combine(pluginDir, "cache");
            try { Directory.CreateDirectory(cacheDir); } catch { }

            string key = (sourceUrl ?? string.Empty) + "|" + (playableUrl ?? string.Empty) + "|audio-vlc-v1|" + streamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            uint hash = 2166136261u;
            foreach (char c in key)
                hash = (hash ^ (uint)c) * 16777619u;
            return Path.Combine(cacheDir, $"mkv_audio_{hash:x8}.mp4");
        }

        public static void PrepareMkvAudioTrackFileAsync(
            string sourceUrl,
            string playableUrl,
            int streamIndex,
            string ffmpegPath,
            CancellationToken token,
            Action<bool, string> onComplete)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool success = false;
                string outputPath = null;
                try
                {
                    outputPath = GetMkvAudioTrackCachedOutputPath(sourceUrl, playableUrl, streamIndex);
                    if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                    {
                        success = true;
                        return;
                    }

                    if (File.Exists(outputPath))
                        try { File.Delete(outputPath); } catch { }

                    // Use the already-playable video as input 0 and the original MKV audio stream as input 1
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = $"-i \"{EscapeArg(playableUrl)}\" -i \"{EscapeArg(sourceUrl)}\" " +
                                    $"-map 0:v:0 -map 1:{streamIndex} -map_metadata 1 -map_chapters -1 -sn -dn " +
                                    $"-c:v copy -c:a aac -ac 2 -ar 48000 -b:a 192k -af aresample=async=1:first_pts=0 " +
                                    $"-shortest -movflags +faststart -f mp4 -y \"{EscapeArg(outputPath)}\" -hide_banner -loglevel error",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var proc = new Process { StartInfo = psi })
                    {
                        proc.Start();
                        proc.BeginErrorReadLine();
                        proc.ErrorDataReceived += (_, __) => { };
                        while (!proc.WaitForExit(500))
                        {
                            if (token.IsCancellationRequested)
                            {
                                try { proc.Kill(); } catch { }
                                return;
                            }
                        }
                        success = proc.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
                    }

                    if (!success)
                    {
                        try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                    }
                }
                catch
                {
                    success = false;
                    try { if (outputPath != null && File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                }
                finally
                {
                    if (!token.IsCancellationRequested)
                        onComplete?.Invoke(success, success ? outputPath : null);
                }
            });
        }

        private static string FindFfprobePath(string ffmpegPath)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    string directory = Path.GetDirectoryName(ffmpegPath) ?? string.Empty;
                    foreach (string name in new[] { "ffprobe.exe", "ffprobe" })
                    {
                        string candidate = Path.Combine(directory, name);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
            catch { }

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string segment in pathEnv.Split(Path.PathSeparator))
                foreach (string name in new[] { "ffprobe.exe", "ffprobe" })
                {
                    try
                    {
                        string candidate = Path.Combine(segment.Trim(), name);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch { }
                }

            return null;
        }

        public static string FindFfmpegPath()
        {
            string pluginDir = SyncVideoPlugin.Settings?.PluginDirectory ?? string.Empty;
            if (!string.IsNullOrEmpty(pluginDir))
                foreach (string name in new[] { "ffmpeg.exe", "ffmpeg" })
                {
                    string c = Path.Combine(pluginDir, name);
                    if (File.Exists(c)) return c;
                }

            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string segment in pathEnv.Split(Path.PathSeparator))
                foreach (string name in new[] { "ffmpeg.exe", "ffmpeg" })
                {
                    try
                    {
                        string c = Path.Combine(segment.Trim(), name);
                        if (File.Exists(c)) return c;
                    }
                    catch { }
                }

            return null;
        }

        // MKV conversion
        public static string GetMkvCachedOutputPath(string inputUrl, bool transcode)
        {
            string pluginDir = SyncVideoPlugin.Settings?.PluginDirectory ?? string.Empty;
            string cacheDir  = Path.Combine(pluginDir, "cache");
            try { Directory.CreateDirectory(cacheDir); } catch { }

            uint hash = 2166136261u; // Hash URL for re-mux and transcode
            foreach (char c in inputUrl ?? string.Empty)
                hash = (hash ^ (uint)c) * 16777619u;
            if (transcode) hash ^= 0xdeadbeef;

            string suffix = transcode ? "tc_aacall_v2" : "rm_aacall_v2";
            return Path.Combine(cacheDir, $"mkv_{hash:x8}_{suffix}.mp4");
        }

        // Convert the MKV to MP4 so Unity's stupid video player can actually suport it
        // Needed for MKVs if they use H.265/HEVC, VP9, or the AV1 codec, this forces transcoding to H.264 + AAC
        public static void ConvertMkvAutoAsync(
            string inputUrl,
            string ffmpegPath,
            bool   userTranscode,
            CancellationToken token,
            Action<bool, string> onComplete)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool   success = false;
                string result  = null;
                try
                {
                    if (token.IsCancellationRequested) return;

                    // Check what video codec with MKV uses
                    bool transcodeToH264 = userTranscode;
                    if (!transcodeToH264)
                    {
                        string codec = ProbeVideoCodec(inputUrl, ffmpegPath, token);
                        if (!token.IsCancellationRequested &&
                            (string.Equals(codec, "hevc", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(codec, "vp9",  StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(codec, "av1", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(codec, "mpeg4",  StringComparison.OrdinalIgnoreCase)))
                        {
                            transcodeToH264 = true;
                        }
                    }

                    if (token.IsCancellationRequested) return;

                    string outputPath = GetMkvCachedOutputPath(inputUrl, transcodeToH264);

                    // Use cached file
                    if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                    {
                        success = true;
                        result  = outputPath;
                        return;
                    }

                    // Clean up all your failures
                    if (File.Exists(outputPath))
                        try { File.Delete(outputPath); } catch { }

                    string encodingArgs = transcodeToH264
                        ? "-map 0:v:0 -map 0:a? -map_metadata 0 -map_chapters 0 -sn -dn -c:v libx264 -preset fast -crf 23 -pix_fmt yuv420p -c:a aac -ac 2 -b:a 192k -movflags +faststart -f mp4"
                        : "-map 0:v:0 -map 0:a? -map_metadata 0 -map_chapters 0 -sn -dn -c:v copy -c:a aac -ac 2 -ar 48000 -b:a 192k -movflags +faststart -f mp4";

                    success = RunFfmpegConvert(ffmpegPath, inputUrl, outputPath, encodingArgs, token);

                    if (success && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                        result = outputPath;
                    else
                    {
                        success = false;
                        try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                    }
                }
                catch
                {
                    success = false;
                    try { if (result != null && File.Exists(result)) File.Delete(result); } catch { }
                }
                finally
                {
                    if (!token.IsCancellationRequested)
                        onComplete?.Invoke(success, result);
                }
            });
        }

        // Convert MKV to MP4 for better compatibility
        public static void ConvertMkvAsync(
            string inputUrl,
            string ffmpegPath,
            string outputPath,
            bool   transcodeToH264,
            CancellationToken token,
            Action<bool, string> onComplete)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool   success = false;
                string result  = null;
                try
                {
                    // Clean up all your failures
                    if (File.Exists(outputPath))
                        try { File.Delete(outputPath); } catch { }

                    string encodingArgs = transcodeToH264
                        ? "-map 0:v:0 -map 0:a? -map_metadata 0 -map_chapters 0 -sn -dn -c:v libx264 -preset fast -crf 23 -pix_fmt yuv420p -c:a aac -ac 2 -b:a 192k -movflags +faststart -f mp4"
                        : "-map 0:v:0 -map 0:a? -map_metadata 0 -map_chapters 0 -sn -dn -c:v copy -c:a aac -ac 2 -ar 48000 -b:a 192k -movflags +faststart -f mp4";

                    success = RunFfmpegConvert(ffmpegPath, inputUrl, outputPath, encodingArgs, token);

                    if (success && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                        result = outputPath;
                    else
                    {
                        success = false;
                        try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                    }
                }
                catch
                {
                    success = false;
                    try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                }

                if (!token.IsCancellationRequested)
                    onComplete?.Invoke(success, result);
            });
        }

        // Use ffprobe to grab the codec the video uses
        private static string ProbeVideoCodec(string url, string ffmpegPath, CancellationToken token)
        {
            string ffprobePath = FindFfprobePath(ffmpegPath);
            if (!string.IsNullOrWhiteSpace(ffprobePath))
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName  = ffprobePath,
                        Arguments = $"-v error -probesize 10000000 -analyzeduration 10000000" +
                                    $" -select_streams v:0 -show_entries stream=codec_name -of csv=p=0" +
                                    $" \"{EscapeArg(url)}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        UseShellExecute        = false,
                        CreateNoWindow         = true
                    };

                    using (var proc = new Process { StartInfo = psi })
                    {
                        proc.Start();
                        string output = proc.StandardOutput.ReadToEnd().Trim();
                        proc.WaitForExit(10000);
                        if (token.IsCancellationRequested) return string.Empty;
                        if (!string.IsNullOrEmpty(output)) return output;
                    }
                }
                catch { }
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName  = ffmpegPath,
                    Arguments = $"-probesize 10000000 -analyzeduration 10000000 -i \"{EscapeArg(url)}\" -hide_banner",
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };

                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    string stderr = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(10000);
                    if (token.IsCancellationRequested) return string.Empty;

                    // Match "Stream #0:N...: Video: hevc ..." or "Video: h264 ..."
                    var match = _videoCodecRx.Match(stderr);

                    return match.Success
                        ? match.Groups[1].Value.ToLowerInvariant()
                        : string.Empty;
                }
            }
            catch { return string.Empty; }
        }

        private static bool RunFfmpegConvert(
            string ffmpegPath,
            string inputUrl,
            string outputPath,
            string encodingArgs,
            CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName  = ffmpegPath,
                Arguments = $"-i \"{EscapeArg(inputUrl)}\" {encodingArgs} -y" +
                            $" \"{EscapeArg(outputPath)}\" -hide_banner -loglevel error",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };

            try
            {
                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    proc.BeginErrorReadLine();
                    proc.ErrorDataReceived += (_, __) => { };

                    while (!proc.WaitForExit(500))
                    {
                        if (token.IsCancellationRequested)
                        {
                            try { proc.Kill(); } catch { }
                            return false;
                        }
                    }

                    return proc.ExitCode == 0;
                }
            }
            catch { return false; }
        }
    }
}
