using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Video;

namespace SyncVideo.Runtime
{
    public sealed class DirectUrlVideoBackend : IDisposable, IVideoBackend
    {
        private const string NoVideoLoadedMessage = "No Video Loaded!";
        private const string LoadedReadyMessage = "Video Loaded!\nPress Play!";
        private const string UrlErrorMessage = "Video URL Error!";
        private const string PausedMessage = "Paused!";
        private const string VideoEndedMessage = "Video Ended!";
        private const string ResolvingBaseMessage = "Loading Youtube URL!\nPlease wait";
        private const string DownloadingBaseMessage = "FFmpeg enabled!\nDownloading HD Video!\nPlease wait";

        private readonly ManualLogSource _logger;
        private readonly GameObject _root;
        private readonly VideoPlayer _player;
        private readonly RenderTexture _texture;
        private readonly int _renderWidth;
        private readonly int _renderHeight;
        private readonly SubtitleManager _subtitleManager;
        private readonly bool _useAudioSourceOutput;
        private readonly List<AudioSource> _audioSources = new List<AudioSource>();

        private string _lastOriginalUrl = string.Empty;
        private string _subtitleSourceUrl = string.Empty;
        private string _lastVideoId = string.Empty;
        private sealed class AudioTrackInfo
        {
            public int StreamIndex;
            public int UnityTrackIndex;
            public string Language = string.Empty;
            public string Title = string.Empty;
            public string Codec = string.Empty;
            public int Channels;
        }

        private readonly object _audioTrackLock = new object();
        private readonly List<AudioTrackInfo> _audioTracks = new List<AudioTrackInfo>();
        private CancellationTokenSource _audioProbeCts;
        private int _knownAudioTrackCount = 1;
        private int _selectedAudioTrack = 0;
        private bool _isAudioProbing;
        private bool _isMuted;
        private float _volume;
        private double _lastKnownTimeSeconds;
        private bool _isResolving;
        private float _resolvingAnimTimer;
        private int _resolvingAnimStep;
        private string _resolvingCurrentBase = string.Empty;

        private bool _isAudioSwitching;
        private double _audioSwitchPendingSeek;
        private bool _audioSwitchPendingWasPlaying;

        public string CurrentDirectUrl { get; private set; } = string.Empty;
        public bool HasPreparedUrl => !string.IsNullOrWhiteSpace(CurrentDirectUrl);
        public string StatusOverlayText { get; private set; } = NoVideoLoadedMessage;

        public event Action Prepared;
        public event Action Ended;
        public event Action AudioTrackSwitchCompleted;
        public event Action AudioTracksChanged;

        public DirectUrlVideoBackend()
        {
            _logger = BepInEx.Logging.Logger.CreateLogSource("SyncVideo.DirectUrlVideoBackend");
            _subtitleManager = new SubtitleManager(_logger);
            _useAudioSourceOutput = SyncVideoPlugin.Settings == null || SyncVideoPlugin.Settings.UseUnityAudioSource.Value;

            int defaultVolumePct = SyncVideoPlugin.Settings != null
                ? SyncVideoPlugin.Settings.DefaultVolume.Value
                : 90;
            _volume = Mathf.Clamp01(defaultVolumePct / 100f);

            _root = new GameObject("SyncVideoBackend");
            UnityEngine.Object.DontDestroyOnLoad(_root);

            ParseRenderResolution(
                SyncVideoPlugin.Settings != null ? SyncVideoPlugin.Settings.VideoRenderResolution.Value : null,
                out _renderWidth, out _renderHeight);

            _texture = new RenderTexture(_renderWidth, _renderHeight, 0, RenderTextureFormat.ARGB32);
            _texture.useMipMap = false;
            _texture.autoGenerateMips = false;
            _texture.antiAliasing = 1;
            _texture.Create();
            ClearRenderTexture();

            _player = _root.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.isLooping = false;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = _texture;
            _player.aspectRatio = VideoAspectRatio.FitInside;
            _player.audioOutputMode = _useAudioSourceOutput ? VideoAudioOutputMode.AudioSource : VideoAudioOutputMode.Direct;
            if (_useAudioSourceOutput)
                EnsureAudioSourceCount(1);
            _player.EnableAudioTrack(0, true);
            _player.skipOnDrop = true;
            _player.waitForFirstFrame = true;
            _player.prepareCompleted += OnPrepareCompleted;
            _player.errorReceived += OnErrorReceived;
            _player.loopPointReached += OnLoopPointReached;

            _logger.LogInfo($"Using video render texture {_renderWidth}x{_renderHeight}.");
            _logger.LogInfo($"Using video audio output mode {(_useAudioSourceOutput ? "AudioSource" : "Direct")}.");
        }

        private static void ParseRenderResolution(string configuredValue, out int width, out int height)
        {
            width = 854;
            height = 480;
            var raw = string.IsNullOrWhiteSpace(configuredValue)
                ? string.Empty
                : configuredValue.Trim().ToLowerInvariant().Replace('x', 'x');
            switch (raw)
            {
                case "1920x1080": width = 1920; height = 1080; return;
                case "1280x720": width = 1280; height = 720; return;
                case "960x540": width = 960; height = 540; return;
                case "854x480": width = 854; height = 480; return;
                case "640x360": width = 640; height = 360; return;
                case "426x240": width = 426; height = 240; return;
            }
        }

        public bool IsPrepared => _player.isPrepared;
        public bool IsPlaying => _player.isPlaying;
        public double CurrentTimeSeconds => (_player.isPrepared || _player.isPlaying) ? _player.time : _lastKnownTimeSeconds;
        public double DurationSeconds => _player.isPrepared ? _player.length : 0d;
        public object OutputTexture => _texture;
        public float LocalVolume => _volume;
        public bool IsMuted => _isMuted;
        public bool IsCurrentMkv => UrlNormalizer.IsMkvUrl(CurrentDirectUrl) || UrlNormalizer.IsMkvUrl(_lastOriginalUrl);
        public bool ShouldShowFfmpegSyncingStatus => !string.IsNullOrEmpty(_lastVideoId) && YouTube.IsFfmpegAvailable();
        public int AudioTrackCount => Math.Max(1, _knownAudioTrackCount);
        public int SelectedAudioTrack => Mathf.Clamp(_selectedAudioTrack, 0, Math.Max(0, AudioTrackCount - 1));

        // Subtitles
        public int SubtitleTrackCount => _subtitleManager.TrackCount;
        public int SelectedSubtitleTrack => _subtitleManager.SelectedTrack;
        public bool IsSubtitleProbing => _subtitleManager.IsProbing;
        public bool IsSubtitleExtracting => _subtitleManager.IsExtracting;
        public bool IsAudioSwitching => _isAudioSwitching;
        public bool IsAudioProbing => _isAudioProbing;

        public string GetSubtitleTrackLabel(int index) => _subtitleManager.GetTrackLabel(index);

        // Activate subtitles
        public void SelectSubtitleTrack(int trackIndex, Action onComplete)
        {
            if (string.IsNullOrWhiteSpace(_subtitleSourceUrl))
                return;
            string ffmpegPath = SubtitleManager.FindFfmpegPath();
            if (ffmpegPath == null)
                return;

            _subtitleManager.SelectTrack(trackIndex, _subtitleSourceUrl, ffmpegPath, () =>
                SyncVideoPlugin.SyncController?.EnqueueMainThreadAction(onComplete));
        }

        public void DisableSubtitles()
        {
            _subtitleManager.DisableSubtitles();
        }

        // Get the subtitles text currently active
        public string GetCurrentSubtitleText(double time) =>
            _subtitleManager.GetActiveSubtitle(time);

        // Video status helpers
        public void SetResolvingStatus()
        {
            if (_player.isPlaying || _player.isPrepared) _player.Stop();
            _player.playbackSpeed = 1f;
            _lastKnownTimeSeconds = 0d;
            CurrentDirectUrl = string.Empty;
            ClearRenderTexture();
            _isResolving = true;
            _resolvingAnimTimer = 0f;
            _resolvingAnimStep = 0;
            _resolvingCurrentBase = ResolvingBaseMessage;
            SetStatusOverlay(ResolvingBaseMessage);
        }

        public void SetDownloadingStatus()
        {
            if (_player.isPlaying || _player.isPrepared) _player.Stop();
            _player.playbackSpeed = 1f;
            _lastKnownTimeSeconds = 0d;
            CurrentDirectUrl = string.Empty;
            ClearRenderTexture();
            _isResolving = true;
            _resolvingAnimTimer = 0f;
            _resolvingAnimStep = 0;
            _resolvingCurrentBase = DownloadingBaseMessage;
            SetStatusOverlay(DownloadingBaseMessage);
        }

        public void SetConvertingStatus()
        {
            if (_player.isPlaying || _player.isPrepared) _player.Stop();
            _player.playbackSpeed = 1f;
            _lastKnownTimeSeconds = 0d;
            CurrentDirectUrl = string.Empty;
            ClearRenderTexture();
            _isResolving = true;
            _resolvingAnimTimer = 0f;
            _resolvingAnimStep = 0;
            _resolvingCurrentBase = "FFmpeg: Converting MKV!\nPlease wait";
            SetStatusOverlay("FFmpeg: Converting MKV!\nPlease wait");
        }

        public void SetErrorStatus(string message)
        {
            _isResolving = false;
            ClearRenderTexture();
            // add extra error handling if this stupid thing can catch the reason
            int nl = (message ?? string.Empty).IndexOf('\n');
            string overlay = nl >= 0
                ? "Error: Video not supported!" + message.Substring(nl)
                : "Error: Video not supported!";
            SetStatusOverlay(overlay);
        }

        public void Dispose()
        {
            _player.prepareCompleted -= OnPrepareCompleted;
            _player.errorReceived -= OnErrorReceived;
            _player.loopPointReached -= OnLoopPointReached;
            CancelAudioProbe();
            _subtitleManager.Clear();
            if (_texture != null) _texture.Release();
            if (_root != null) UnityEngine.Object.Destroy(_root);
            if (_logger != null) BepInEx.Logging.Logger.Sources.Remove(_logger);
        }

        public void Load(string directPlayableUrl, string originalUrl, string videoId)
        {
            _lastOriginalUrl = originalUrl ?? string.Empty;
            _subtitleSourceUrl = ResolveSubtitleSourceUrl(directPlayableUrl, _lastOriginalUrl);
            _lastVideoId = videoId ?? string.Empty;
            CurrentDirectUrl = directPlayableUrl ?? string.Empty;
            _lastKnownTimeSeconds = 0d;
            ResetAudioTrackCache();
            _selectedAudioTrack = 0;

            _isAudioSwitching = false;
            _audioSwitchPendingSeek = 0d;
            _audioSwitchPendingWasPlaying = false;

            Stop();

            if (string.IsNullOrWhiteSpace(directPlayableUrl))
            {
                SetStatusOverlay(UrlErrorMessage);
                return;
            }

            ProbeAudioTracksIfSupported(_subtitleSourceUrl);
            ProbeSubtitlesIfSupported(_subtitleSourceUrl);

            SetStatusOverlay(string.Empty);
            _player.source = VideoSource.Url;
            _player.url = directPlayableUrl;
            ResetAudioRouting();
            ConfigureAudioTracks();
            _player.Prepare();
        }

        public void ReloadCurrent()
        {
            if (string.IsNullOrWhiteSpace(CurrentDirectUrl)) return;
            Load(CurrentDirectUrl, _lastOriginalUrl, _lastVideoId);
        }

        public void Play()
        {
            if (_player.isPrepared)
            {
                SetStatusOverlay(string.Empty);
                _player.Play();
            }
        }

        public void Pause()
        {
            if (_player.isPrepared)
            {
                _player.Pause();
                UpdatePausedOverlay();
            }
        }

        public void Stop()
        {
            if (_player.isPlaying || _player.isPrepared) _player.Stop();
            StopAudioSources();
            _player.playbackSpeed = 1f;
            _lastKnownTimeSeconds = 0d;
            _isResolving = false;
            SetStatusOverlay(NoVideoLoadedMessage);
            ClearRenderTexture();
            CancelAudioProbe();
            ResetAudioTrackCache();
            _subtitleManager.Clear();
        }

        public void Seek(double seconds)
        {
            if (_player.isPrepared)
            {
                // Pause, set time, then play
                bool wasPlaying = _player.isPlaying;
                if (wasPlaying) _player.Pause();
                _player.time = Math.Max(0d, seconds);
                _lastKnownTimeSeconds = Math.Max(0d, seconds);
                _subtitleManager.ResetSearchHint();
                if (wasPlaying) _player.Play();
                else UpdatePausedOverlay();
            }
        }

        public void NudgeToward(double seconds, double driftSeconds)
        {
            if (!_player.isPrepared) return;
            var diff = seconds - _player.time;
            var absDiff = Math.Abs(diff);
            if (absDiff < 0.015d || driftSeconds <= 0d) { _player.playbackSpeed = 1f; return; }

            float correction = Mathf.Clamp((float)(diff * 0.14d), -0.08f, 0.08f);
            if (Math.Abs(correction) < 0.012f)
                correction = diff > 0d ? 0.012f : -0.012f;

            _player.playbackSpeed = Mathf.Clamp(1f + correction, 0.92f, 1.08f);
        }

        public void Tick(float deltaTime)
        {
            if (_isResolving)
            {
                _resolvingAnimTimer += deltaTime;
                if (_resolvingAnimTimer >= 0.35f)
                {
                    _resolvingAnimTimer = 0f;
                    _resolvingAnimStep = (_resolvingAnimStep + 1) % 4;
                    // Only rebuild the string when the step actually changes, not every frame
                    switch (_resolvingAnimStep)
                    {
                        case 1: SetStatusOverlay(_resolvingCurrentBase + "."); break;
                        case 2: SetStatusOverlay(_resolvingCurrentBase + ".."); break;
                        case 3: SetStatusOverlay(_resolvingCurrentBase + "..."); break;
                        default: SetStatusOverlay(_resolvingCurrentBase); break;
                    }
                }
            }

            if (_player.isPrepared || _player.isPlaying)
                _lastKnownTimeSeconds = Math.Max(0d, _player.time);

            if (!_player.isPlaying && Math.Abs(_player.playbackSpeed - 1f) > 0.001f)
                _player.playbackSpeed = 1f;
        }

        public void AdjustVolume(float delta)
        {
            _volume = Mathf.Clamp01(_volume + delta);
            ApplyAudioState();
        }

        public void ToggleMute()
        {
            _isMuted = !_isMuted;
            ApplyAudioState();
        }

        // Audio track select
        public string GetAudioTrackLabel(int trackIndex)
        {
            AudioTrackInfo info = GetCachedAudioTrack(trackIndex);
            int display = trackIndex + 1;
            if (info == null)
                return _isAudioProbing ? "Audio Track " + display + "\n<color=yellow>Scanning...</color>" : "Audio Track " + display;

            string detail = BuildAudioTrackDetail(info);
            if (string.IsNullOrWhiteSpace(detail))
                return "Audio Track " + display;

            return "Audio Track " + display + "\n(" + detail + ")";
        }

        public bool SelectAudioTrack(int trackIndex)
        {
            return SelectAudioTrack(trackIndex, null, null);
        }

        public bool SelectAudioTrack(int trackIndex, double? resumeTimeOverride, bool? resumePlayingOverride)
        {
            if (string.IsNullOrWhiteSpace(CurrentDirectUrl))
                return false;

            int trackCount = AudioTrackCount;
            if (trackCount <= 0)
                return false;

            int clamped = Mathf.Clamp(trackIndex, 0, Math.Max(0, trackCount - 1));
            if (clamped == _selectedAudioTrack)
                return true;

            _selectedAudioTrack = clamped;

            _audioSwitchPendingSeek = resumeTimeOverride ?? CurrentTimeSeconds;
            _audioSwitchPendingWasPlaying = resumePlayingOverride ?? _player.isPlaying;
            _isAudioSwitching = true;

            // Force a fresh prepare to fix silent audio channels
            if (_player.isPlaying || _player.isPrepared)
                _player.Stop();

            _player.playbackSpeed = 1f;
            _player.source = VideoSource.Url;
            _player.url = CurrentDirectUrl;

            ResetAudioRouting();
            ConfigureAudioTracks();
            _player.Prepare();

            return true;
        }

        public void ShowEndedState(double seconds)
        {
            if (_player.isPlaying || _player.isPrepared) _player.Stop();
            StopAudioSources();
            _player.playbackSpeed = 1f;
            _lastKnownTimeSeconds = Math.Max(_lastKnownTimeSeconds, seconds);
            ClearRenderTexture();
            SetStatusOverlay(VideoEndedMessage);
        }

        private void ApplyAudioState()
        {
            float vol = _isMuted ? 0f : _volume;
            bool isYouTube = !string.IsNullOrEmpty(_lastVideoId);
            if (isYouTube && SyncVideoPlugin.Settings != null)
                vol *= Mathf.Clamp01(SyncVideoPlugin.Settings.YouTubeVolumeScale.Value);

            // set volume per track
            int trackCount = Math.Max(1, _knownAudioTrackCount);
            int selectedTrack = Mathf.Clamp(_selectedAudioTrack, 0, Math.Max(0, trackCount - 1));

            if (_useAudioSourceOutput)
            {
                if (HasCachedAudioTracks())
                {
                    List<AudioTrackInfo> tracks = GetCachedAudioTracksSnapshot();
                    EnsureAudioSourceCount(tracks.Count);
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        int unityIndex = tracks[i].UnityTrackIndex;
                        if (unityIndex >= 0 && unityIndex < _audioSources.Count)
                            _audioSources[unityIndex].volume = i == selectedTrack ? vol : 0f;
                    }
                    return;
                }

                EnsureAudioSourceCount(trackCount);
                for (int i = 0; i < trackCount && i < _audioSources.Count; i++)
                    _audioSources[i].volume = i == selectedTrack ? vol : 0f;
                return;
            }

            if (HasCachedAudioTracks())
            {
                List<AudioTrackInfo> tracks = GetCachedAudioTracksSnapshot();
                for (int i = 0; i < tracks.Count; i++)
                {
                    ushort unityIndex = (ushort)tracks[i].UnityTrackIndex;
                    try { _player.SetDirectAudioVolume(unityIndex, i == selectedTrack ? vol : 0f); }
                    catch { }
                }
                return;
            }

            for (ushort i = 0; i < (ushort)trackCount; i++)
            {
                try { _player.SetDirectAudioVolume(i, i == selectedTrack ? vol : 0f); }
                catch { }
            }
        }

        private void OnPrepareCompleted(VideoPlayer source)
        {
            source.playbackSpeed = 1f;
            _lastKnownTimeSeconds = 0d;

            if (_isAudioSwitching)
            {
                // Reapply the selected track after prepare to fix silence
                int reportedTrackCount = Math.Max(1, (int)source.audioTrackCount);
                if (!HasCachedAudioTracks())
                    _knownAudioTrackCount = Math.Max(_knownAudioTrackCount, reportedTrackCount);
                _selectedAudioTrack = Mathf.Clamp(_selectedAudioTrack, 0, Math.Max(0, _knownAudioTrackCount - 1));
                ResetAudioRouting();
                ConfigureAudioTracks();
                ApplyAudioState();

                _isAudioSwitching = false;
                double seekTime = _audioSwitchPendingSeek;
                bool wasPlaying = _audioSwitchPendingWasPlaying;
                _audioSwitchPendingSeek = 0d;
                _audioSwitchPendingWasPlaying = false;

                if (seekTime > 0.05d)
                {
                    source.time = Math.Max(0d, seekTime);
                    _lastKnownTimeSeconds = Math.Max(0d, seekTime);
                }

                _subtitleManager.ResetSearchHint();

                if (wasPlaying)
                    source.Play();
                else
                    UpdatePausedOverlay();

                AudioTrackSwitchCompleted?.Invoke();

                // Do NOT use Prepared or trigger SyncVideoController.OnPrepared, that fucks up lobby sync state!!
                return;
            }

            if (!HasCachedAudioTracks())
                _knownAudioTrackCount = Math.Max(1, (int)source.audioTrackCount);
            _selectedAudioTrack = Mathf.Clamp(_selectedAudioTrack, 0, Math.Max(0, _knownAudioTrackCount - 1));
            ResetAudioRouting();
            ConfigureAudioTracks();
            ApplyAudioState();

            ClearRenderTexture();
            SetStatusOverlay(LoadedReadyMessage);
            Prepared?.Invoke();
        }

        private void OnErrorReceived(VideoPlayer source, string message)
        {
            _logger.LogError("SyncVideo URL Video backend error: " + message);
            source.Stop();
            StopAudioSources();
            source.playbackSpeed = 1f;
            SetStatusOverlay(UrlErrorMessage);
            ClearRenderTexture();
        }

        private void OnLoopPointReached(VideoPlayer source)
        {
            var endedTime = source.time;
            if (endedTime <= 0d && source.frameCount > 0 && source.frameRate > 0f)
                endedTime = source.frameCount / source.frameRate;

            _lastKnownTimeSeconds = Math.Max(_lastKnownTimeSeconds, endedTime);
            source.Stop();
            StopAudioSources();
            source.playbackSpeed = 1f;
            ClearRenderTexture();
            SetStatusOverlay(VideoEndedMessage);
            Ended?.Invoke();
        }

        private void UpdatePausedOverlay()
        {
            if (!_player.isPrepared) return;
            SetStatusOverlay(_player.time <= 0.05d ? LoadedReadyMessage : PausedMessage);
        }

        private void SetStatusOverlay(string message)
        {
            StatusOverlayText = message ?? string.Empty;
        }

        private void ResetAudioRouting()
        {
            if (_useAudioSourceOutput)
                return;

            try
            {
                _player.audioOutputMode = VideoAudioOutputMode.None;
                _player.audioOutputMode = VideoAudioOutputMode.Direct;
            }
            catch { }
        }

        private void EnsureAudioSourceCount(int count)
        {
            if (!_useAudioSourceOutput)
                return;

            count = Math.Max(1, count);
            while (_audioSources.Count < count)
            {
                var source = _root.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 0f;
                source.volume = 0f;
                _audioSources.Add(source);
            }
        }

        private void StopAudioSources()
        {
            if (!_useAudioSourceOutput)
                return;

            for (int i = 0; i < _audioSources.Count; i++)
            {
                try { _audioSources[i].Stop(); }
                catch { }
            }
        }

        private void ConfigureAudioTracks()
        {
            int trackCount = Math.Max(1, _knownAudioTrackCount);
            int selectedTrack = Mathf.Clamp(_selectedAudioTrack, 0, Math.Max(0, trackCount - 1));

            if (HasCachedAudioTracks())
            {
                List<AudioTrackInfo> tracks = GetCachedAudioTracksSnapshot();

                try { _player.controlledAudioTrackCount = (ushort)tracks.Count; }
                catch { }
                EnsureAudioSourceCount(tracks.Count);

                for (int i = 0; i < tracks.Count; i++)
                {
                    ushort unityIndex = (ushort)tracks[i].UnityTrackIndex;
                    try { _player.EnableAudioTrack(unityIndex, i == selectedTrack); }
                    catch { }
                    if (_useAudioSourceOutput && unityIndex < _audioSources.Count)
                    {
                        try { _player.SetTargetAudioSource(unityIndex, _audioSources[unityIndex]); }
                        catch { }
                    }
                }

                AudioTrackInfo info = selectedTrack >= 0 && selectedTrack < tracks.Count ? tracks[selectedTrack] : null;
                if (info != null)
                    // _logger.LogInfo($"[MKV Audio] Selected Audio Track {selectedTrack + 1}: ffprobe stream #{info.StreamIndex}, Unity track #{info.UnityTrackIndex}.");
                    return;
            }

            try { _player.controlledAudioTrackCount = (ushort)trackCount; }
            catch { }
            EnsureAudioSourceCount(trackCount);

            for (int i = 0; i < trackCount; i++)
            {
                try { _player.EnableAudioTrack((ushort)i, i == selectedTrack); }
                catch { }
                if (_useAudioSourceOutput && i < _audioSources.Count)
                {
                    try { _player.SetTargetAudioSource((ushort)i, _audioSources[i]); }
                    catch { }
                }
            }
        }


        private void ResetAudioTrackCache()
        {
            CancelAudioProbe();
            lock (_audioTrackLock)
                _audioTracks.Clear();
            _knownAudioTrackCount = 1;
            _isAudioProbing = false;
        }

        private void CancelAudioProbe()
        {
            try { _audioProbeCts?.Cancel(); }
            catch { }
            try { _audioProbeCts?.Dispose(); }
            catch { }
            _audioProbeCts = null;
        }

        private bool HasCachedAudioTracks()
        {
            lock (_audioTrackLock)
                return _audioTracks.Count > 0;
        }

        private AudioTrackInfo GetCachedAudioTrack(int trackIndex)
        {
            lock (_audioTrackLock)
            {
                if (trackIndex < 0 || trackIndex >= _audioTracks.Count)
                    return null;
                return _audioTracks[trackIndex];
            }
        }

        private List<AudioTrackInfo> GetCachedAudioTracksSnapshot()
        {
            lock (_audioTrackLock)
                return new List<AudioTrackInfo>(_audioTracks);
        }

        private void ProbeAudioTracksIfSupported(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!UrlNormalizer.IsMkvUrl(url)) return;

            string ffmpegPath = SubtitleManager.FindFfmpegPath();
            if (ffmpegPath == null) return;

            string ffprobePath = FindFfprobePath(ffmpegPath);

            CancelAudioProbe();
            var cts = new CancellationTokenSource();
            _audioProbeCts = cts;
            _isAudioProbing = true;
            AudioTracksChanged?.Invoke();

            Task.Run(() =>
            {
                List<AudioTrackInfo> detected = null;
                try
                {
                    if (ffprobePath != null)
                        detected = ProbeAudioTracks(url, ffprobePath, cts.Token);

                    if ((detected == null || detected.Count == 0) && !cts.IsCancellationRequested)
                        detected = ProbeAudioTracksFromFfmpegStderr(url, ffmpegPath, cts.Token);
                }
                catch (Exception ex) { _logger.LogWarning("[MKV Audio] Probe error: " + ex.Message); }

                if (cts.IsCancellationRequested)
                    return;

                SyncVideoPlugin.SyncController?.EnqueueMainThreadAction(() =>
                {
                    if (cts.IsCancellationRequested)
                        return;

                    _isAudioProbing = false;
                    if (detected != null && detected.Count > 0)
                    {
                        lock (_audioTrackLock)
                        {
                            _audioTracks.Clear();
                            _audioTracks.AddRange(detected);
                        }
                        _knownAudioTrackCount = detected.Count;
                        _selectedAudioTrack = Mathf.Clamp(_selectedAudioTrack, 0, Math.Max(0, _knownAudioTrackCount - 1));
                        ConfigureAudioTracks();
                        ApplyAudioState();
                        _logger.LogInfo($"[MKV Audio] Cached {detected.Count} audio track(s)!");
                    }

                    AudioTracksChanged?.Invoke();
                });
            }, cts.Token);
        }

        private static List<AudioTrackInfo> ProbeAudioTracks(string url, string ffprobePath, CancellationToken token)
        {
            var result = new List<AudioTrackInfo>();
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                // -probesize and -analyzeduration keep the probe fast for larger files
                // -select_streams limits output to audio tracks
                // -show_entries limits fields to what BuildAudioTrackDetail needs
                // -of default=noprint_wrappers=0 produces flat key=value output (no [STREAM] delimiters)
                Arguments = "-v error -probesize 100M -analyzeduration 100M" +
                            " -select_streams a -show_entries stream=index,codec_name,channels:stream_tags=language,title" +
                            " -of default=noprint_wrappers=0 " + QuoteArg(url),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var proc = new Process { StartInfo = psi })
            {
                proc.Start();
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(15000);
                if (!proc.HasExited)
                {
                    try { proc.Kill(); } catch { }
                    return result;
                }

                ParseFfprobeAudioStreams(output, result, token);
            }

            return result;
        }

        private static void ParseFfprobeAudioStreams(string output, List<AudioTrackInfo> result, CancellationToken token)
        {
            AudioTrackInfo current = null;
            foreach (var rawLine in (output ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                if (token.IsCancellationRequested)
                    break;

                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line.StartsWith("index=", StringComparison.OrdinalIgnoreCase))
                {
                    if (current != null && current.StreamIndex >= 0)
                        result.Add(current);
                    current = new AudioTrackInfo { StreamIndex = -1, UnityTrackIndex = result.Count };
                    int streamIndex;
                    if (int.TryParse(line.Substring("index=".Length).Trim(), out streamIndex))
                        current.StreamIndex = streamIndex;
                    continue;
                }

                if (current == null)
                    current = new AudioTrackInfo { StreamIndex = -1, UnityTrackIndex = result.Count };

                if (line.StartsWith("codec_name=", StringComparison.OrdinalIgnoreCase))
                    current.Codec = line.Substring("codec_name=".Length).Trim();
                else if (line.StartsWith("channels=", StringComparison.OrdinalIgnoreCase))
                {
                    int ch;
                    if (int.TryParse(line.Substring("channels=".Length).Trim(), out ch))
                        current.Channels = ch;
                }
                else if (line.StartsWith("TAG:language=", StringComparison.OrdinalIgnoreCase))
                {
                    // Strip brackets so they look nicer
                    string lang = line.Substring("TAG:language=".Length).Trim().Trim('[', ']');
                    if (!string.IsNullOrWhiteSpace(lang) && !lang.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                        current.Language = lang;
                }
                else if (line.StartsWith("TAG:title=", StringComparison.OrdinalIgnoreCase))
                {
                    string t = line.Substring("TAG:title=".Length).Trim();
                    if (!string.IsNullOrWhiteSpace(t) && !t.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                        current.Title = t;
                }
            }

            if (current != null && current.StreamIndex >= 0)
                result.Add(current);
        }

        // Fallback ffmpeg stderr audio probe
        private static List<AudioTrackInfo> ProbeAudioTracksFromFfmpegStderr(string url, string ffmpegPath, CancellationToken token)
        {
            var result = new List<AudioTrackInfo>();
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-probesize 10000000 -analyzeduration 10000000 -i " + QuoteArg(url) + " -hide_banner",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = new Process { StartInfo = psi })
            {
                proc.Start();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(15000);
                if (!proc.HasExited)
                {
                    try { proc.Kill(); } catch { }
                    return result;
                }

                if (token.IsCancellationRequested)
                    return result;

                AudioTrackInfo currentAudio = null;
                foreach (var rawLine in stderr.Split('\n'))
                {
                    if (token.IsCancellationRequested) break;
                    string line = rawLine.TrimEnd('\r');

                    if (line.IndexOf("Stream #", StringComparison.Ordinal) >= 0)
                    {
                        // Finalize the previous audio track before moving to the next stream
                        if (currentAudio != null)
                        {
                            result.Add(currentAudio);
                            currentAudio = null;
                        }

                        var m = _audioStreamRx.Match(line);
                        if (m.Success)
                        {
                            int streamIdx;
                            int.TryParse(m.Groups[1].Value, out streamIdx);
                            string lang = m.Groups[2].Value.Trim().Trim('[', ']');
                            if (lang.Equals("und", StringComparison.OrdinalIgnoreCase))
                                lang = string.Empty;

                            currentAudio = new AudioTrackInfo
                            {
                                StreamIndex = streamIdx,
                                UnityTrackIndex = result.Count,
                                Language = lang
                            };
                        }
                        continue;
                    }

                    // Get an audio stream metadata block and pick up the title
                    if (currentAudio != null)
                    {
                        var tm = _audioTitleLineRx.Match(line);
                        if (tm.Success)
                            currentAudio.Title = tm.Groups[1].Value.Trim();
                    }
                }

                if (currentAudio != null && !token.IsCancellationRequested)
                    result.Add(currentAudio);
            }

            return result;
        }

        // Matches an audio stream line in ffmpeg -i stderr
        // Group 1 = stream index, Group 2 = optional language code
        private static readonly Regex _audioStreamRx = new Regex(
            @"Stream #0:(\d+)(?:\(([^)]+)\))?[^:]*: Audio",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Metadata title line
        private static readonly Regex _audioTitleLineRx = new Regex(
            @"^\s+title\s*:\s*(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string BuildAudioTrackDetail(AudioTrackInfo info)
        {
            string title = CleanTrackText(info.Title);
            string language = FormatLanguage(info.Language);
            string codec = string.IsNullOrWhiteSpace(info.Codec) ? string.Empty : info.Codec.Trim().ToUpperInvariant();
            string channels = FormatChannels(info.Channels);

            string main = title;
            if (string.IsNullOrWhiteSpace(main))
            {
                if (!string.IsNullOrWhiteSpace(language))
                    main = language;
                if (!string.IsNullOrWhiteSpace(channels))
                    main = string.IsNullOrWhiteSpace(main) ? channels : main + " " + channels;
                if (!string.IsNullOrWhiteSpace(codec))
                    main = string.IsNullOrWhiteSpace(main) ? codec : main + " " + codec;
            }

            if (!string.IsNullOrWhiteSpace(language) &&
                main.IndexOf(language, StringComparison.OrdinalIgnoreCase) < 0)
                main += " - [" + language + "]";

            return main.Trim();
        }

        private static string CleanTrackText(string value)
        {
            return (value ?? string.Empty).Replace("_", " ").Trim();
        }

        private static string FormatChannels(int channels)
        {
            switch (channels)
            {
                case 1: return "1.0";
                case 2: return "2.0";
                case 6: return "5.1";
                case 8: return "7.1";
                default: return channels > 0 ? channels.ToString() + "ch" : string.Empty;
            }
        }

        private static string FormatLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language) || language.Equals("und", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            switch (language.Trim().ToLowerInvariant())
            {
                case "en":
                case "eng": return "English";
                case "ja":
                case "jpn":
                case "jp": return "Japanese";
                case "es":
                case "spa": return "Spanish";
                case "fr":
                case "fre":
                case "fra": return "French";
                case "de":
                case "ger":
                case "deu": return "German";
                case "it":
                case "ita": return "Italian";
                case "pt":
                case "por": return "Portuguese";
                default: return language.Trim();
            }
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

        private static string QuoteArg(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string ResolveSubtitleSourceUrl(string directPlayableUrl, string originalUrl)
        {
            if (!string.IsNullOrWhiteSpace(originalUrl) && UrlNormalizer.IsMkvUrl(originalUrl))
                return originalUrl;
            return directPlayableUrl ?? string.Empty;
        }

        private void ProbeSubtitlesIfSupported(string url)
        {
            _subtitleManager.Clear();
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!SubtitleManager.IsSubtitleProbeSupported(url)) return;
            string ffmpegPath = SubtitleManager.FindFfmpegPath();
            if (ffmpegPath == null) return;

            _subtitleManager.ProbeAsync(url, ffmpegPath, () =>
            {
                _subtitleManager.EagerExtractAllTracksAsync(url, ffmpegPath);
                // Reapply tracks as soon as they're read, prevent it from going ahead of probe
                SyncVideoPlugin.SyncController?.EnqueueMainThreadAction(() => SyncVideoPlugin.SyncController?.ReapplyLobbyTrackSelection());
            });
        }

        private void ClearRenderTexture()
        {
            if (_texture == null) return;
            var previous = RenderTexture.active;
            RenderTexture.active = _texture;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = previous;
        }
    }
}