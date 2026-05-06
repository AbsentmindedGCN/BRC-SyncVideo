using BepInEx.Logging;
using SyncVideo.Model;
using System;
using System.Collections.Concurrent;

namespace SyncVideo.Runtime
{
    public sealed class SyncVideoController : IDisposable
    {
        private readonly ManualLogSource _logger;
        private readonly VideoLobbyManager _lobbyManager;
        private readonly DirectUrlVideoBackend _backend;
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();

        private int _lastAppliedRevision = -1;
        private int _lastAppliedSeekRevision;
        private bool _restartAfterPrepare;
        private bool _restartAutoPlay;
        private bool _autoplayAfterPrepare;
        private bool _lastRemotePlaying;
        private float _viewerCommandSyncingTimer;
        private int _viewerSeekDirection;

        private float _seekCooldownTimer; // Post-seek cooldown to prevent the Tick from re-seeking while Unity is still buffering
        private double _lastSeekTarget = double.MinValue;
        private string _lastLoadedLobbyUrl = string.Empty;
        private string _lastLoadedVideoId = string.Empty;
        private string _pendingLoadLobbyUrl = string.Empty;
        private string _pendingLoadVideoId = string.Empty;
        private bool _loadInProgress;
        private int _activeLoadRequestId;
        private System.Threading.CancellationTokenSource _mkvConversionCts;
        private int _lastAppliedAudioTrack = int.MinValue;
        private int _lastAppliedSubtitleTrack = int.MinValue;
        private bool _viewerLocalTrackOverride = false; // Let viewer override host settings for MKVs with tracks, so viewer can watch dubbed while host watches subbed

        public event Action<VideoSyncState> StateChanged;

        public SyncVideoController(ManualLogSource logger, VideoLobbyManager lobbyManager)
        {
            _logger = logger;
            _lobbyManager = lobbyManager;
            _backend = new DirectUrlVideoBackend();
            _lobbyManager.ActiveStateChanged += OnActiveStateChanged;
            _backend.Prepared += OnPrepared;
            _backend.Ended += OnEnded;
            _backend.AudioTrackSwitchCompleted += OnAudioTrackSwitchCompleted;
            _backend.AudioTracksChanged += OnAudioTracksChanged;
        }

        public void Dispose()
        {
            _lobbyManager.ActiveStateChanged -= OnActiveStateChanged;
            _backend.Prepared -= OnPrepared;
            _backend.Ended -= OnEnded;
            _backend.AudioTrackSwitchCompleted -= OnAudioTrackSwitchCompleted;
            _backend.AudioTracksChanged -= OnAudioTracksChanged;
            _backend.Dispose();
        }

        public IVideoBackend Backend => _backend;
        public float LocalVolume => _backend.LocalVolume;
        public bool IsMuted => _backend.IsMuted;
        public bool IsViewerCommandSyncing => _viewerCommandSyncingTimer > 0f;
        public int ViewerSeekDirection => _viewerSeekDirection;
        public bool IsCurrentMkv() => (_backend as DirectUrlVideoBackend)?.IsCurrentMkv ?? false;
        public bool ShouldShowMkvSettings()
        {
            if (!(SyncVideoPlugin.Settings?.EnableMkvSupport?.Value ?? false))
                return false;

            string targetUrl = string.Empty;

            if (_loadInProgress && !string.IsNullOrWhiteSpace(_pendingLoadLobbyUrl))
            {
                targetUrl = _pendingLoadLobbyUrl;
            }
            else
            {
                targetUrl = _lobbyManager.CurrentLobby?.CurrentUrl ?? string.Empty;
                if (string.IsNullOrWhiteSpace(targetUrl))
                    targetUrl = (_backend as DirectUrlVideoBackend)?.CurrentDirectUrl ?? string.Empty;
            }

            return UrlNormalizer.IsMkvUrl(targetUrl);
        }
        public bool ShouldShowFfmpegSyncingStatus() => (_backend as DirectUrlVideoBackend)?.ShouldShowFfmpegSyncingStatus ?? false;
        public int GetMkvAudioTrackCount() => (_backend as DirectUrlVideoBackend)?.AudioTrackCount ?? 0;
        public int GetMkvSelectedAudioTrack() => (_backend as DirectUrlVideoBackend)?.SelectedAudioTrack ?? 0;
        public string GetMkvAudioTrackLabel(int trackIndex) => (_backend as DirectUrlVideoBackend)?.GetAudioTrackLabel(trackIndex) ?? ("Audio Track " + (trackIndex + 1));

        public bool SelectMkvAudioTrack(int trackIndex)
        {
            var backend = _backend as DirectUrlVideoBackend;
            if (backend == null) return false;

            bool result;
            if (_lobbyManager.IsHost)
            {
                result = backend.SelectAudioTrack(trackIndex);
            }
            else
            {
                var lobby = _lobbyManager.CurrentLobby;
                double resumeTime = lobby != null ? GetExpectedTime(lobby) : backend.CurrentTimeSeconds;
                bool resumePlaying = lobby != null && lobby.IsPlaying;
                result = backend.SelectAudioTrack(trackIndex, resumeTime, resumePlaying);
            }

            if (result && _lobbyManager.IsHost)
            {
                int subtitle = _lobbyManager.CurrentLobby?.SelectedSubtitleTrack ?? -1;
                _lobbyManager.SetMkvTrackSelection(trackIndex, subtitle);
            }
            else if (result && !_lobbyManager.IsHost)
            {
                // Viewer made a manual selection so stop overwriting their choice
                _viewerLocalTrackOverride = true;
            }
            return result;
        }

        public int GetMkvSubtitleTrackCount() => (_backend as DirectUrlVideoBackend)?.SubtitleTrackCount ?? 0;
        public int GetMkvSelectedSubtitleTrack() => (_backend as DirectUrlVideoBackend)?.SelectedSubtitleTrack ?? -1;
        public string GetMkvSubtitleTrackLabel(int trackIndex) => (_backend as DirectUrlVideoBackend)?.GetSubtitleTrackLabel(trackIndex) ?? ("Subtitle Track " + (trackIndex + 1));
        public bool IsMkvSubtitleProbing() => (_backend as DirectUrlVideoBackend)?.IsSubtitleProbing ?? false;
        public bool IsMkvSubtitleExtracting() => (_backend as DirectUrlVideoBackend)?.IsSubtitleExtracting ?? false;
        public bool IsMkvAudioSwitching() => (_backend as DirectUrlVideoBackend)?.IsAudioSwitching ?? false;
        public bool IsMkvAudioProbing() => (_backend as DirectUrlVideoBackend)?.IsAudioProbing ?? false;

        // Select and load subtitle track asynchronously
        public void SelectMkvSubtitleTrack(int trackIndex, System.Action onComplete)
        {
            var backend = _backend as DirectUrlVideoBackend;
            if (backend == null) { onComplete?.Invoke(); return; }

            if (trackIndex < 0)
            {
                backend.DisableSubtitles();
                if (_lobbyManager.IsHost)
                {
                    int audio = _lobbyManager.CurrentLobby?.SelectedAudioTrack ?? 0;
                    _lobbyManager.SetMkvTrackSelection(audio, -1);
                }
                else
                {
                    // Viewer manually disabled subtitles
                    _viewerLocalTrackOverride = true;
                }
                onComplete?.Invoke();
                return;
            }

            // Broadcast immediately so viewers don't wait forever
            if (_lobbyManager.IsHost)
            {
                int audio = _lobbyManager.CurrentLobby?.SelectedAudioTrack ?? 0;
                _lobbyManager.SetMkvTrackSelection(audio, trackIndex);
            }
            else
            {
                // Viewer manually selected a subtitle track, let them override
                _viewerLocalTrackOverride = true;
            }

            backend.SelectSubtitleTrack(trackIndex, onComplete);
        }

        // Get subtitle for current time in video
        public string GetCurrentSubtitleText()
        {
            var backend = _backend as DirectUrlVideoBackend;
            if (backend == null) return null;
            return backend.GetCurrentSubtitleText(backend.CurrentTimeSeconds);
        }

        public void Tick(float deltaTime)
        {
            while (_mainThreadActions.TryDequeue(out var action))
                action?.Invoke();

            _backend.Tick(deltaTime);

            if (_viewerCommandSyncingTimer > 0f)
                _viewerCommandSyncingTimer = Math.Max(0f, _viewerCommandSyncingTimer - deltaTime);

            if (_seekCooldownTimer > 0f)
                _seekCooldownTimer = Math.Max(0f, _seekCooldownTimer - deltaTime);

            var lobby = _lobbyManager.CurrentLobby;
            if (lobby == null)
            {
                _viewerCommandSyncingTimer = 0f;
                return;
            }

            if (_lobbyManager.IsHost)
            {
                _viewerCommandSyncingTimer = 0f;

                if (lobby.IsPlaying)
                    _lobbyManager.SetObservedPlaybackTime(_backend.CurrentTimeSeconds);

                _lastRemotePlaying = lobby.IsPlaying;
                return;
            }

            var justStartedPlaying = lobby.IsPlaying && !_lastRemotePlaying;
            var justStoppedPlaying = !lobby.IsPlaying && _lastRemotePlaying;
            _lastRemotePlaying = lobby.IsPlaying;

            if (_loadInProgress || !_backend.IsPrepared)
                return;

            if (justStartedPlaying)
            {
                var startTime = GetExpectedTime(lobby);
                if (startTime <= 0.5d)
                    startTime = 0d;

                // Guard against a fuck ton of sudden state packets
                // Let the game finish processing waitForFirstFrame so it doesn't break and crash spectacularly
                bool targetShifted = Math.Abs(startTime - _lastSeekTarget) > 5.0d;
                if (_seekCooldownTimer <= 0f || targetShifted)
                {
                    _seekCooldownTimer = 5.0f;
                    _lastSeekTarget = startTime;
                    _backend.Seek(startTime);
                }

                if (!_backend.IsPlaying)
                    _backend.Play();

                return;
            }

            if (justStoppedPlaying)
            {
                _viewerCommandSyncingTimer = 0f;

                if (_backend.IsPlaying)
                    _backend.Pause();
            }
            else if (lobby.IsPlaying && !_backend.IsPlaying)
            {
                _backend.Play();
            }

            var target = GetExpectedTime(lobby);
            var current = _backend.CurrentTimeSeconds;
            var delta = target - current;
            var drift = Math.Abs(delta);

            if (!lobby.IsPlaying)
            {
                _viewerCommandSyncingTimer = 0f;

                if (drift >= 0.03d)
                    _backend.Seek(target);

                _backend.NudgeToward(current, 0d);
                return;
            }

            // stale packet guard
            float _stateAge = UnityEngine.Time.unscaledTime - lobby.LastSeenSeconds;
            bool _stateIsStale = _stateAge > 4f;
            if (_stateIsStale)
                return;

            // Existing viewers no longer hard-seek for small drifts
            if (drift >= Math.Max(1.5d, (double)SyncVideoPlugin.Settings.HardSeekThresholdSeconds.Value))
            {
                // Only re-seek if the cooldown expired OR if the host video position changed
                bool targetShifted = Math.Abs(target - _lastSeekTarget) > 5.0d;
                if (_seekCooldownTimer > 0f && !targetShifted)
                {
                    // Still buffering the previous seek so don't seek again or it would mess up the buffer scan
                    _viewerCommandSyncingTimer = drift > 0.75d ? 0.75f : 0f;
                    return;
                }

                _viewerCommandSyncingTimer = drift > 0.75d ? 0.75f : 0f;
                _seekCooldownTimer = 5.0f;
                _lastSeekTarget = target;
                _backend.Seek(target);
                return;
            }

            if (drift > SyncVideoPlugin.Settings.DriftToleranceSeconds.Value)
            {
                _viewerCommandSyncingTimer = drift > 0.5d ? 0.35f : 0f;
                _backend.NudgeToward(target, drift);
            }
            else if (drift <= 0.03d)
            {
                _viewerCommandSyncingTimer = 0f;
                _viewerSeekDirection = 0;
                _backend.NudgeToward(current, 0d);
            }
            else
            {
                // Dead zone in case drift is small but above display threshold so hold normal speed
                _backend.NudgeToward(current, 0d);
            }
        }

        public void HostSetUrl(string rawInput)
        {
            if (!_lobbyManager.IsHost)
                return;

            var normalized = UrlNormalizer.Normalize(rawInput, out var videoId, out var directPlayableUrl);
            if (!UrlNormalizer.ValidateSubmissionUrl(normalized, videoId, directPlayableUrl, out _))
                return;

            CancelPendingLoadAndPlayback();

            _autoplayAfterPrepare = SyncVideoPlugin.Settings.HostAutoplay.Value;
            _lobbyManager.SetVideo(normalized, videoId);
            LoadUrlWithResolution(normalized, videoId, directPlayableUrl);
            SyncVideoPlugin.ScreenManager?.OnVideoChanged();
        }

        public void HostPlay()
        {
            if (!_lobbyManager.IsHost)
                return;

            var currentLobby = _lobbyManager.CurrentLobby;
            var startTime = _backend.CurrentTimeSeconds;

            if (currentLobby != null && !currentLobby.IsPlaying)
            {
                if (currentLobby.HasEnded || startTime <= 0.05d)
                    startTime = 0d;
            }

            _backend.Seek(startTime);
            _backend.Play();
            _lobbyManager.SetObservedPlaybackTime(startTime);
            _lobbyManager.SetPlayback(true);
            SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(true, startTime);
        }

        public void HostPause()
        {
            if (!_lobbyManager.IsHost)
                return;

            _backend.Pause();
            _lobbyManager.SetPlayback(false);
            SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(false, _backend.CurrentTimeSeconds);
        }

        public void HostSeekRelative(double seconds)
        {
            if (!_lobbyManager.IsHost)
                return;

            var currentLobby = _lobbyManager.CurrentLobby;
            if (currentLobby != null && currentLobby.HasEnded)
                return;

            var newTime = Math.Max(0d, _backend.CurrentTimeSeconds + seconds);
            _backend.Seek(newTime);
            _lobbyManager.SeekRelative(seconds);

            var isPlaying = _lobbyManager.CurrentLobby != null && _lobbyManager.CurrentLobby.IsPlaying;
            SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(isPlaying, _backend.CurrentTimeSeconds);
        }

        public void HostRestart()
        {
            if (!_lobbyManager.IsHost)
                return;

            var currentLobby = _lobbyManager.CurrentLobby;
            var hasEnded = currentLobby != null && currentLobby.HasEnded;
            var shouldPlayAfterRestart = (currentLobby != null && currentLobby.IsPlaying) || hasEnded;

            if (!_backend.IsPrepared)
            {
                if (string.IsNullOrWhiteSpace(_backend.CurrentDirectUrl))
                    return;

                _restartAfterPrepare = true;
                _restartAutoPlay = shouldPlayAfterRestart;
                _backend.ReloadCurrent();
                return;
            }

            _backend.Seek(0d);
            _lobbyManager.RestartFromBeginning(shouldPlayAfterRestart);

            if (shouldPlayAfterRestart)
                _backend.Play();
            else
                _backend.Pause();

            SyncVideoPlugin.ScreenManager?.OnVideoChanged();
            SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(shouldPlayAfterRestart, 0d);
            StateChanged?.Invoke(MakeState(_lobbyManager.CurrentLobby));
        }

        public void EnqueueMainThreadAction(Action action)
        {
            if (action == null)
                return;

            _mainThreadActions.Enqueue(action);
        }

        public void AdjustLocalVolume(float delta)
        {
            _backend.AdjustVolume(delta);
        }

        public void ToggleMute()
        {
            _backend.ToggleMute();
        }

        public void StopForMissingScreen()
        {
            if (!_backend.IsPrepared && !_backend.IsPlaying)
                return;

            var hostTime = _backend.CurrentTimeSeconds;
            _backend.Stop();

            if (_lobbyManager.IsHost && _lobbyManager.CurrentLobby != null)
            {
                _lobbyManager.SetObservedPlaybackTime(hostTime);
                _lobbyManager.SetPlayback(false);
            }

            SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(false, hostTime);
        }

        private void SeekAndSetDirection(double target)
        {
            _viewerSeekDirection = target > _backend.CurrentTimeSeconds ? 1 : -1;
            _backend.Seek(target);
        }

        private int CancelPendingLoadAndPlayback()
        {
            _activeLoadRequestId++;
            _mkvConversionCts?.Cancel();
            _mkvConversionCts = null;
            _restartAfterPrepare = false;
            _restartAutoPlay = false;
            _autoplayAfterPrepare = false;
            _loadInProgress = false;
            _pendingLoadLobbyUrl = string.Empty;
            _pendingLoadVideoId = string.Empty;
            _seekCooldownTimer = 0f;
            _lastSeekTarget = double.MinValue;
            _lastAppliedAudioTrack = int.MinValue;
            _lastAppliedSubtitleTrack = int.MinValue;
            _viewerLocalTrackOverride = false;
            YouTube.CancelAllPendingRequests();
            _backend.Stop();
            return _activeLoadRequestId;
        }

        private void LoadUrlWithResolution(string originalUrl, string videoId, string directPlayableUrl = null)
        {
            int requestId = _activeLoadRequestId;
            _loadInProgress = true;
            _pendingLoadLobbyUrl = originalUrl ?? string.Empty;
            _pendingLoadVideoId = videoId ?? string.Empty;

            bool needsYouTubeResolution =
                !string.IsNullOrEmpty(videoId) &&
                string.IsNullOrWhiteSpace(directPlayableUrl);

            if (needsYouTubeResolution)
            {
                if (YouTube.IsFfmpegAvailable())
                    _backend.SetDownloadingStatus();
                else
                    _backend.SetResolvingStatus();

                YouTube.ClearAllCache();
                YouTube.ResolveAsync(
                    videoId,
                    originalUrl,
                    resolvedUrl =>
                    {
                        // Callback arrives on a background thread
                        _mainThreadActions.Enqueue(() =>
                        {
                            if (requestId != _activeLoadRequestId)
                                return;

                            _backend.Load(resolvedUrl, originalUrl, videoId);
                        });
                    },
                    errorMessage =>
                    {
                        _mainThreadActions.Enqueue(() =>
                        {
                            if (requestId != _activeLoadRequestId)
                                return;

                            _loadInProgress = false;
                            _pendingLoadLobbyUrl = string.Empty;
                            _pendingLoadVideoId = string.Empty;
                            _backend.SetErrorStatus(errorMessage);
                        });
                    });
            }
            else if (IsMkvConversionEnabled(directPlayableUrl ?? originalUrl))
            {
                // FFmpeg MKV conversion
                // Check if using a code and force transcode into H.264 + AAC, better compatibility
                bool userTranscode = SyncVideoPlugin.Settings?.MkvTranscodeToH264?.Value ?? false;
                string sourceUrl = directPlayableUrl ?? originalUrl;
                string ffmpegPath = SubtitleManager.FindFfmpegPath();

                _backend.SetConvertingStatus();

                // Check cache
                string cachedRemux = SubtitleManager.GetMkvCachedOutputPath(sourceUrl, false);
                string cachedTranscode = SubtitleManager.GetMkvCachedOutputPath(sourceUrl, true);
                string cachedPath =
                    System.IO.File.Exists(cachedTranscode) ? cachedTranscode :
                    System.IO.File.Exists(cachedRemux) ? cachedRemux :
                    null;

                if (cachedPath != null)
                {
                    if (requestId != _activeLoadRequestId) return;
                    _backend.Load(MakeFileUrl(cachedPath), originalUrl, videoId ?? string.Empty);
                }
                else
                {
                    _mkvConversionCts = new System.Threading.CancellationTokenSource();
                    var convToken = _mkvConversionCts.Token;

                    SubtitleManager.ConvertMkvAutoAsync(sourceUrl, ffmpegPath, userTranscode, convToken,
                        (success, resultPath) =>
                        {
                            _mainThreadActions.Enqueue(() =>
                            {
                                if (requestId != _activeLoadRequestId) return;

                                _loadInProgress = false;
                                _pendingLoadLobbyUrl = string.Empty;
                                _pendingLoadVideoId = string.Empty;

                                if (success && !string.IsNullOrEmpty(resultPath))
                                    _backend.Load(MakeFileUrl(resultPath), originalUrl, videoId ?? string.Empty);
                                else
                                    _backend.SetErrorStatus("MKV conversion failed.");
                            });
                        });
                }
            }
            else
            {
                // Direct load for non MKV
                if (requestId != _activeLoadRequestId)
                    return;

                _backend.Load(directPlayableUrl ?? originalUrl, originalUrl, videoId ?? string.Empty);
            }
        }

        private void OnActiveStateChanged(VideoLobby lobby)
        {
            if (lobby == null)
            {
                CancelPendingLoadAndPlayback(); // safe to delete file now and cancel stale viewer loads
                _lastAppliedRevision = -1;
                _lastAppliedSeekRevision = 0;
                _lastRemotePlaying = false;
                _viewerCommandSyncingTimer = 0f;
                _seekCooldownTimer = 0f;
                _lastSeekTarget = double.MinValue;
                _viewerSeekDirection = 0;
                _lastLoadedLobbyUrl = string.Empty;
                _lastLoadedVideoId = string.Empty;
                YouTube.ClearAllCache();
                StateChanged?.Invoke(null);
                return;
            }

            if (_lobbyManager.IsHost)
            {
                StateChanged?.Invoke(MakeState(lobby));
                return;
            }

            if (lobby.Revision == _lastAppliedRevision)
                return;

            _lastAppliedRevision = lobby.Revision;

            var expected = GetExpectedTime(lobby);
            var lobbyUrl = lobby.CurrentUrl ?? string.Empty;
            var lobbyVideoId = lobby.CurrentVideoId ?? string.Empty;
            bool hasVideo = !string.IsNullOrWhiteSpace(lobbyUrl);
            bool sameVideo = string.Equals(_lastLoadedLobbyUrl, lobbyUrl, StringComparison.Ordinal)
                             && string.Equals(_lastLoadedVideoId, lobbyVideoId, StringComparison.Ordinal);
            bool samePendingLoad = _loadInProgress
                                   && string.Equals(_pendingLoadLobbyUrl, lobbyUrl, StringComparison.Ordinal)
                                   && string.Equals(_pendingLoadVideoId, lobbyVideoId, StringComparison.Ordinal);
            bool backendNeedsReload = hasVideo && !_backend.IsPrepared && !_backend.IsPlaying && !lobby.HasEnded && !samePendingLoad && !_backend.IsAudioSwitching;
            bool needsReload = hasVideo && (!sameVideo || backendNeedsReload) && !samePendingLoad;

            if (needsReload)
            {
                _lastLoadedLobbyUrl = lobbyUrl;
                _lastLoadedVideoId = lobbyVideoId;
                _lastAppliedSeekRevision = lobby.SeekRevision;
                _viewerSeekDirection = 0;
                _viewerCommandSyncingTimer = 0f;
                CancelPendingLoadAndPlayback();
                LoadUrlWithResolution(lobbyUrl, lobbyVideoId);

                // Let OnPrepared finish the actual seek/play/pause
                _lastRemotePlaying = lobby.IsPlaying;
                SyncVideoPlugin.ScreenManager?.OnVideoChanged();
                SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(lobby.IsPlaying, lobby.IsPlaying ? expected : expected);
                StateChanged?.Invoke(MakeState(lobby));
                return;
            }

            bool hostSeekChanged = lobby.SeekRevision > _lastAppliedSeekRevision;

            if (lobby.HasEnded && !lobby.IsPlaying)
            {
                _viewerCommandSyncingTimer = 0f;
                _backend.ShowEndedState(expected);
            }
            else if (!lobby.IsPlaying)
            {
                _viewerCommandSyncingTimer = 0f;

                if (_backend.IsPlaying)
                    _backend.Pause();

                if (Math.Abs(_backend.CurrentTimeSeconds - expected) >= 0.03d)
                {
                    if (hostSeekChanged)
                        SeekAndSetDirection(expected);
                    else
                        _backend.Seek(expected);
                }
            }
            else
            {
                var drift = Math.Abs(_backend.CurrentTimeSeconds - expected);

                if (!_backend.IsPlaying)
                    _backend.Play();

                if (drift >= 1.5d)
                {
                    // Hard seek for large drift but still respect the cooldown to prevent seeks that can crash players
                    bool targetShifted = Math.Abs(expected - _lastSeekTarget) > 5.0d;
                    if (_seekCooldownTimer <= 0f || targetShifted)
                    {
                        _seekCooldownTimer = 5.0f;
                        _lastSeekTarget = expected;
                        if (hostSeekChanged)
                            SeekAndSetDirection(expected);
                        else
                            _backend.Seek(expected);
                    }
                    _viewerCommandSyncingTimer = drift >= 0.75d ? 0.6f : 0f;
                }
                else if (!_backend.IsPlaying && expected <= 0.5d)
                {
                    // Snap to start
                    _backend.Seek(0d);
                    _viewerCommandSyncingTimer = 0f;
                }
                else if (drift > 0.20d)
                {
                    _backend.NudgeToward(expected, drift);
                    _viewerCommandSyncingTimer = drift >= 0.5d ? 0.35f : 0f;
                }
                else
                {
                    _viewerCommandSyncingTimer = 0f;
                }
            }

            if (hostSeekChanged)
                _lastAppliedSeekRevision = lobby.SeekRevision;

            _lastRemotePlaying = lobby.IsPlaying;
            SyncVideoPlugin.ScreenManager?.OnVideoChanged();
            SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(lobby.IsPlaying, lobby.IsPlaying ? expected : expected);
            ApplyLobbyTrackSelection(lobby);
            StateChanged?.Invoke(MakeState(lobby));
        }

        private void OnPrepared()
        {
            _loadInProgress = false;
            _pendingLoadLobbyUrl = string.Empty;
            _pendingLoadVideoId = string.Empty;

            var currentLobby = _lobbyManager.CurrentLobby;
            if (currentLobby != null)
            {
                _lastLoadedLobbyUrl = currentLobby.CurrentUrl ?? string.Empty;
                _lastLoadedVideoId = currentLobby.CurrentVideoId ?? string.Empty;
            }

            if (_restartAfterPrepare && _lobbyManager.IsHost)
            {
                _restartAfterPrepare = false;
                _backend.Seek(0d);
                _lobbyManager.RestartFromBeginning(_restartAutoPlay);

                if (_restartAutoPlay)
                    _backend.Play();
                else
                    _backend.Pause();

                SyncVideoPlugin.ScreenManager?.OnVideoChanged();
                SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(_restartAutoPlay, 0d);
                StateChanged?.Invoke(MakeState(_lobbyManager.CurrentLobby));
                return;
            }

            if (_autoplayAfterPrepare && _lobbyManager.IsHost)
            {
                _autoplayAfterPrepare = false;
                _backend.Seek(0d);
                _backend.Play();
                _lobbyManager.RestartFromBeginning(true);
                SyncVideoPlugin.ScreenManager?.OnVideoChanged();
                SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(true, 0d);
                StateChanged?.Invoke(MakeState(_lobbyManager.CurrentLobby));
                return;
            }

            var lobby = _lobbyManager.CurrentLobby;
            if (lobby == null)
                return;

            var expected = GetExpectedTime(lobby);
            _lastAppliedSeekRevision = lobby.SeekRevision;
            _viewerSeekDirection = 0;

            if (lobby.HasEnded && !lobby.IsPlaying)
            {
                _backend.ShowEndedState(expected);
            }
            else
            {
                // Use GetExpectedTime so load time is compensated, that way the viewer seeks to where the host's video actually currently is
                var initialTime = lobby.IsPlaying ? GetExpectedTime(lobby) : expected;

                if (initialTime < 0.5d)
                    initialTime = 0d;

                if (initialTime > 0d)
                {
                    _seekCooldownTimer = 5.0f;
                    _lastSeekTarget = initialTime;
                }
                // Use seek so the join sync doesn't trigger the seek status for Viewer
                _backend.Seek(initialTime);

                if (lobby.IsPlaying)
                {
                    lobby.MediaTimeSeconds = initialTime;
                    lobby.LastSeenSeconds = UnityEngine.Time.unscaledTime;
                    _backend.Play();
                }
                else
                {
                    _backend.Pause();
                }
            }

            _lastRemotePlaying = lobby.IsPlaying;
            SyncVideoPlugin.ScreenManager?.OnVideoChanged();
            SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(lobby.IsPlaying, expected);
            // Apply host's audio/subtitle track selection now that the backend is ready
            ApplyLobbyTrackSelection(lobby);
        }


        private void OnAudioTracksChanged()
        {
            StateChanged?.Invoke(MakeState(_lobbyManager.CurrentLobby));
        }

        private void OnAudioTrackSwitchCompleted()
        {
            if (_lobbyManager.IsHost)
                return;

            ForceImmediateViewerResync();
        }

        private void ForceImmediateViewerResync()
        {
            var lobby = _lobbyManager.CurrentLobby;
            if (lobby == null)
                return;

            double target = GetExpectedTime(lobby);
            double current = _backend.CurrentTimeSeconds;
            double delta = target - current;
            double drift = Math.Abs(delta);

            if (lobby.IsPlaying)
            {
                if (!_backend.IsPlaying)
                    _backend.Play();

                if (drift >= SyncVideoPlugin.Settings.HardSeekThresholdSeconds.Value)
                    _backend.Seek(target);

                _viewerCommandSyncingTimer = drift >= 0.25d ? 0.35f : 0f;
                _backend.NudgeToward(target, drift);
            }
            else
            {
                if (_backend.IsPlaying)
                    _backend.Pause();

                _backend.Seek(target);
                _backend.NudgeToward(current, 0d);
                _viewerCommandSyncingTimer = 0f;
            }

            SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(lobby.IsPlaying, target);
        }

        private void OnEnded()
        {
            var lobby = _lobbyManager.CurrentLobby;
            if (lobby == null)
                return;

            if (_lobbyManager.IsHost)
            {
                _lobbyManager.NotifyPlaybackEnded(_backend.CurrentTimeSeconds);
                SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(false, _backend.CurrentTimeSeconds);
                StateChanged?.Invoke(MakeState(_lobbyManager.CurrentLobby));

                if (_lobbyManager.PlaylistModeEnabled && _lobbyManager.TryDequeueNextSuggestion(out _, out var nextSuggestion))
                    HostSetUrl(nextSuggestion.Url);
            }
            else
            {
                SyncVideoPlugin.ScreenManager?.OnPlaybackStateChanged(false, _backend.CurrentTimeSeconds);
            }
        }

        private double GetExpectedTime(VideoLobby lobby)
        {
            if (lobby == null)
                return 0d;

            var expected = Math.Max(0d, lobby.MediaTimeSeconds);
            if (!lobby.IsPlaying || lobby.LastSeenSeconds <= 0f)
                return expected;

            var elapsed = UnityEngine.Time.unscaledTime - lobby.LastSeenSeconds;
            if (elapsed <= 0f || elapsed > 30f)
                return expected;

            return expected + elapsed;
        }


        private static bool IsMkvConversionEnabled(string url)
        {
            if (!UrlNormalizer.IsMkvUrl(url)) return false;
            return SyncVideoPlugin.Settings?.EnableMkvFfmpegConversion?.Value ?? false;
        }

        private static string MakeFileUrl(string localPath)
        {
            return "file:///" + (localPath ?? string.Empty).Replace('\\', '/');
        }

        // Called by DirectUrlVideoBackend after subtitle probing finishes to prevent OnPrepared from beating async probe
        internal void ReapplyLobbyTrackSelection()
        {
            if (_lobbyManager.IsHost) return;
            if (_viewerLocalTrackOverride) return;
            _lastAppliedAudioTrack = int.MinValue;
            _lastAppliedSubtitleTrack = int.MinValue;
            var lobby = _lobbyManager.CurrentLobby;
            if (lobby == null) return;
            ApplyLobbyTrackSelection(lobby);
        }

        // Apply host audio and subtitle track
        private void ApplyLobbyTrackSelection(VideoLobby lobby)
        {
            if (_lobbyManager.IsHost || lobby == null) return;
            if (!_backend.IsPrepared) return;
            // Viewer overwrote their track selection so DO NOT RESYNC host setting
            if (_viewerLocalTrackOverride) return;

            var backend = _backend as DirectUrlVideoBackend;
            if (backend == null) return;

            int audioTrack = lobby.SelectedAudioTrack;
            int subtitleTrack = lobby.SelectedSubtitleTrack;

            if (audioTrack != _lastAppliedAudioTrack)
            {
                _lastAppliedAudioTrack = audioTrack;
                backend.SelectAudioTrack(audioTrack);
            }

            if (subtitleTrack != _lastAppliedSubtitleTrack)
            {
                if (subtitleTrack < 0)
                {
                    _lastAppliedSubtitleTrack = subtitleTrack;
                    backend.DisableSubtitles();
                }
                else if (SubtitleManager.FindFfmpegPath() != null)
                {
                    // Don't commit _lastAppliedSubtitleTrack yet if probing is still running otherwise SelectTrack will mess up and stop working
                    // Use ReapplyLobbyTrackSelection AFTER probing
                    if (backend.IsSubtitleProbing)
                        return;

                    _lastAppliedSubtitleTrack = subtitleTrack;
                    backend.SelectSubtitleTrack(subtitleTrack, null);
                }
            }
        }

        private VideoSyncState MakeState(VideoLobby lobby)
        {
            if (lobby == null)
                return null;

            return new VideoSyncState
            {
                Url = lobby.CurrentUrl,
                VideoId = lobby.CurrentVideoId,
                IsPlaying = lobby.IsPlaying,
                MediaTimeSeconds = lobby.MediaTimeSeconds,
                HostUnixMilliseconds = lobby.HostUnixMilliseconds,
                Revision = lobby.Revision,
                HasEnded = lobby.HasEnded
            };
        }
    }
}