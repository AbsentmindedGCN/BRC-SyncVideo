using BepInEx.Logging;
using BombRushMP.Common;
using BombRushMP.Common.Networking;
using BombRushMP.Common.Packets;
using BombRushMP.Plugin;
using BombRushMP.Plugin.Gamemodes;
using SyncVideo.Model;
using SyncVideo.Transport;
using SyncVideo.Transport.Packets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SyncVideo.Runtime
{
    public sealed class VideoLobbyManager : IDisposable
    {
        private const int SyncMagic = 0x31565953; // "SYV1"
        private const byte SyncVersion = 5;
        private const string OfflineLobbyId = "offline";
        private static readonly Regex _sanitizeSpriteRx = new Regex(@"<sprite[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _sanitizeTagRx    = new Regex(@"<[^>]+>", RegexOptions.Compiled);

        private sealed class SyncShadow
        {
            public bool IsSyncVideoLobby;
            public string Url = string.Empty;
            public string VideoId = string.Empty;
            public bool IsPlaying;
            public double MediaTimeSeconds;
            public long HostUnixMilliseconds;
            public int Revision;
            public int SeekRevision;
            public bool HasEnded;
            public bool IsOpen = true;
            public bool SuggestionsOpen;
            public int SelectedAudioTrack = 0;
            public int SelectedSubtitleTrack = -1;

            public SyncShadow Clone()
            {
                return (SyncShadow)MemberwiseClone();
            }
        }

        private readonly ManualLogSource _logger;
        private readonly SyncVideoTransport _transport;
        private readonly List<VideoLobby> _visibleLobbies = new List<VideoLobby>();
        private readonly Dictionary<string, SyncVideoStatePacket> _pendingStatePackets = new Dictionary<string, SyncVideoStatePacket>(StringComparer.Ordinal);
        private SyncShadow _hostShadow = new SyncShadow { IsSyncVideoLobby = true };
        private float _stateTimer;
        private float _pushCooldownTimer; // Debounce for PushHostShadowToNativeLobby in MaybeBroadcastHostState
        private string _lastCurrentLobbyId;
        private bool _offlineLobbyActive;
        private bool _leaveInProgress;
        private string _requestedStateLobbyId;
        private bool _receivedFreshStateForCurrentLobby;
        private float _stateRequestRetryTimer;
        private bool _syncVideoHostActive; // fix for other BombRushMP lobbies

        // Reusable buffer
        private readonly Dictionary<string, VideoLobby> _refreshPreviousById = new Dictionary<string, VideoLobby>(StringComparer.Ordinal);
        private readonly List<uint> _refreshLobbyKeys = new List<uint>();

        // Suggestions
        private readonly Dictionary<string, VideoSuggestion> _suggestions =
            new Dictionary<string, VideoSuggestion>(StringComparer.Ordinal);
        private readonly HashSet<string> _hostReceivedSuggestionKeys = new HashSet<string>(StringComparer.Ordinal);
        private bool _suggestionsOpen;
        private readonly HashSet<string> _pendingSuggestionMetadataLookups = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> _suggestionOrder = new List<string>();
        private bool _playlistModeEnabled;

        internal bool   HasPendingSuggestion       => false;
        internal string PendingSuggestionUrl        => string.Empty;
        internal string PendingSuggestionTitle      => string.Empty;
        internal string PendingSuggestionPlayerName => string.Empty;

        public event Action LobbiesChanged;
        public event Action<VideoLobby> ActiveLobbyChanged;
        public event Action<VideoLobby> ActiveStateChanged;
        public event Action SuggestionsChanged;

        public VideoLobby HostedLobby => IsHost ? CurrentLobby : null;
        public VideoLobby JoinedLobby => !IsHost ? CurrentLobby : null;
        public IReadOnlyCollection<VideoLobby> Lobbies => _visibleLobbies;
        public VideoLobby CurrentLobby { get; private set; }

        private ClientLobbyManager NativeLobbyManager =>
            ClientController.Instance != null ? ClientController.Instance.ClientLobbyManager : null;

        public bool InLobby => CurrentLobby != null;
        public bool IsHost => CurrentLobby != null && (_offlineLobbyActive || CurrentLobby.HostId == _transport.LocalPlayerId);
        public bool OfflineModeEnabled => SyncVideoPlugin.Settings.EnableOfflineMode.Value;
        public bool IsOfflineLobby => _offlineLobbyActive;
        public bool LeaveInProgress => _leaveInProgress;
        public bool CanAcceptNewMembers => CurrentLobby != null && CurrentLobby.IsOpen;
        public bool IsLobbyOpen => CurrentLobby != null ? CurrentLobby.IsOpen : _hostShadow.IsOpen;

        public void SetLobbyOpen(bool isOpen)
        {
            if (!IsHost || CurrentLobby == null)
                return;

            _hostShadow.IsSyncVideoLobby = true;
            _hostShadow.IsOpen = isOpen;
            _hostShadow.Revision++;
            _hostShadow.HostUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            CurrentLobby.IsOpen = isOpen;
            CurrentLobby.Revision = _hostShadow.Revision;
            CurrentLobby.HostUnixMilliseconds = _hostShadow.HostUnixMilliseconds;

            if (_offlineLobbyActive)
            {
                UpdateOfflineLobbySnapshot(true, true);
            }
            else
            {
                PushHostShadowToNativeLobby();
                BroadcastStatePacket();
                ActiveStateChanged?.Invoke(CurrentLobby);
                LobbiesChanged?.Invoke();
            }
        }

        public void ToggleLobbyOpen()
        {
            SetLobbyOpen(!IsLobbyOpen);
        }

        public struct VideoSuggestion
        {
            public string Url;
            public string Title;
            public string ChannelName;

            public string GetButtonLabel()
            {
                var resolvedTitle = string.IsNullOrWhiteSpace(Title) ? Url : Title;

                // Truncate if longer than 42 characters
                if (resolvedTitle.Length > 42)
                {
                    resolvedTitle = resolvedTitle.Substring(0, 40) + "...";
                }

                return $"<size=75%>{resolvedTitle}</size>";
            }
        }

        public bool SuggestionsOpen => _suggestionsOpen;
        public bool PlaylistModeEnabled => _playlistModeEnabled;

        public Dictionary<string, VideoSuggestion> GetSuggestions()
        {
            return new Dictionary<string, VideoSuggestion>(_suggestions, StringComparer.Ordinal);
        }

        public List<KeyValuePair<string, VideoSuggestion>> GetOrderedSuggestions()
        {
            var ordered = new List<KeyValuePair<string, VideoSuggestion>>(_suggestions.Count);
            for (int i = 0; i < _suggestionOrder.Count; i++)
            {
                var key = _suggestionOrder[i];
                if (_suggestions.TryGetValue(key, out var suggestion))
                    ordered.Add(new KeyValuePair<string, VideoSuggestion>(key, suggestion));
            }

            if (ordered.Count < _suggestions.Count)
            {
                var orderedKeys = new HashSet<string>(_suggestionOrder, StringComparer.Ordinal);
                foreach (var kvp in _suggestions)
                {
                    if (!orderedKeys.Contains(kvp.Key))
                        ordered.Add(kvp);
                }
            }

            return ordered;
        }

        public void TogglePlaylistMode()
        {
            _playlistModeEnabled = !_playlistModeEnabled;
            SuggestionsChanged?.Invoke();
        }

        public bool QueueHostUrl(string rawInput, string title = null)
        {
            if (!IsHost)
                return false;

            var normalized = UrlNormalizer.Normalize(rawInput, out var videoId, out var directPlayableUrl);
            if (UrlNormalizer.IsDirectSuggestionUrlTooLong(normalized, videoId, directPlayableUrl, out _)
                || !UrlNormalizer.ValidateSubmissionUrl(normalized, videoId, directPlayableUrl, out _))
                return false;

            AddOrUpdateSuggestion(_transport.LocalPlayerId, string.Empty, normalized, string.IsNullOrWhiteSpace(title) ? normalized : title, "Host queue");
            return true;
        }

        public bool TryDequeueNextSuggestion(out string suggestionEntryKey, out VideoSuggestion suggestion)
        {
            for (int i = 0; i < _suggestionOrder.Count; i++)
            {
                var key = _suggestionOrder[i];
                if (_suggestions.TryGetValue(key, out suggestion))
                {
                    suggestionEntryKey = key;
                    _suggestions.Remove(key);
                    _suggestionOrder.RemoveAt(i);
                    SuggestionsChanged?.Invoke();
                    return true;
                }
            }

            suggestionEntryKey = string.Empty;
            suggestion = default(VideoSuggestion);
            return false;
        }

        private static string BuildStoredSuggestionEntryKey(ushort senderPlayerId, string url)
        {
            return senderPlayerId + "|" + BuildSuggestionKey(url);
        }

        private static string BuildSuggestionKey(string url)
        {
            var normalized = UrlNormalizer.Normalize(url ?? string.Empty, out var videoId, out _);
            if (!string.IsNullOrWhiteSpace(videoId))
                return "Y:" + videoId;
            return "U:" + (normalized ?? string.Empty);
        }

        private static bool HasResolvedSuggestionMetadata(VideoSuggestion suggestion)
        {
            if (!string.IsNullOrWhiteSpace(suggestion.ChannelName))
                return true;

            if (string.IsNullOrWhiteSpace(suggestion.Title) || string.IsNullOrWhiteSpace(suggestion.Url))
                return false;

            if (string.Equals(suggestion.Title, "Loading suggestion...", StringComparison.Ordinal))
                return false;

            return !string.Equals(suggestion.Title, suggestion.Url, StringComparison.Ordinal);
        }

        private static bool IsFallbackSuggestionTitle(string title, string normalizedUrl, string videoId)
        {
            return string.IsNullOrWhiteSpace(title)
                || string.Equals(title, normalizedUrl, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(videoId) && string.Equals(title, videoId, StringComparison.Ordinal));
        }

        private static string GetPendingSuggestionTitle(string normalizedUrl, string videoId, string incomingTitle)
        {
            if (!string.IsNullOrWhiteSpace(videoId) && IsFallbackSuggestionTitle(incomingTitle, normalizedUrl, videoId))
                return "Loading suggestion...";

            return string.IsNullOrWhiteSpace(incomingTitle) ? normalizedUrl : incomingTitle.Trim();
        }

        private void SendSuggestionConfirmation(ushort recipientPlayerId, string url)
        {
            if (!IsHost || CurrentLobby == null || !_transport.Connected || string.IsNullOrWhiteSpace(url))
                return;

            try
            {
                _transport.SendToPlayer(new SyncVideoSuggestionAckPacket
                {
                    RecipientPlayerId = recipientPlayerId,
                    LobbyId           = CurrentLobby.LobbyId,
                    SuggestionKey     = BuildSuggestionKey(url)
                }, recipientPlayerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[SyncVideo] Failed to send suggestion confirmation: " + ex.Message);
            }
        }

        private void AddOrUpdateSuggestion(ushort senderPlayerId, string playerName, string url, string title, string source)
        {
            if (!IsHost || CurrentLobby == null)
                return;

            if (string.IsNullOrWhiteSpace(url))
                return;

            var normalizedUrl = UrlNormalizer.Normalize(url, out var videoId, out _);
            var incomingTitle = GetPendingSuggestionTitle(normalizedUrl, videoId, title);
            var suggestionEntryKey = BuildStoredSuggestionEntryKey(senderPlayerId, normalizedUrl);
            var queueWasEmptyBeforeAdd = _suggestionOrder.Count == 0;

            var hadExisting = _suggestions.TryGetValue(suggestionEntryKey, out var existing);
            var existingHasResolvedMetadata = hadExisting && HasResolvedSuggestionMetadata(existing);
            var incomingIsFallbackTitle = IsFallbackSuggestionTitle(incomingTitle, normalizedUrl, videoId)
                || string.Equals(incomingTitle, "Loading suggestion...", StringComparison.Ordinal);

            var titleToStore = existingHasResolvedMetadata && incomingIsFallbackTitle
                ? existing.Title
                : incomingTitle;
            var channelToStore = existingHasResolvedMetadata
                ? existing.ChannelName
                : string.Empty;

            if (hadExisting
                && string.Equals(existing.Url, normalizedUrl, StringComparison.Ordinal)
                && string.Equals(existing.Title, titleToStore, StringComparison.Ordinal)
                && string.Equals(existing.ChannelName, channelToStore, StringComparison.Ordinal))
            {
                return;
            }

            var suggestionSenderName = GetPlayerDisplayName(senderPlayerId);
            _logger.LogInfo($"Received suggestion via {source} from {suggestionSenderName} for lobby {CurrentLobby.LobbyId}: {titleToStore}");

            _suggestions[suggestionEntryKey] = new VideoSuggestion
            {
                Url         = normalizedUrl,
                Title       = titleToStore,
                ChannelName = channelToStore
            };
            if (!hadExisting)
                _suggestionOrder.Add(suggestionEntryKey);
            SuggestionsChanged?.Invoke();

            var shouldAutoloadQueuedSuggestion = !hadExisting
                && queueWasEmptyBeforeAdd
                && _playlistModeEnabled
                && SyncVideoPlugin.Settings.HostAutoplay.Value
                && CurrentLobby != null
                && CurrentLobby.HasEnded;

            if (shouldAutoloadQueuedSuggestion)
            {
                RemoveSuggestion(suggestionEntryKey);
                SyncVideoPlugin.SyncController?.HostSetUrl(normalizedUrl);
                return;
            }

            TryResolveSuggestionMetadata(senderPlayerId, suggestionEntryKey, normalizedUrl);
        }

        private void TryResolveSuggestionMetadata(ushort senderPlayerId, string suggestionEntryKey, string url)
        {
            var normalized = UrlNormalizer.Normalize(url, out var videoId, out _);
            if (string.IsNullOrWhiteSpace(videoId))
                return;

            var lookupKey = suggestionEntryKey + "|" + videoId;
            if (!_pendingSuggestionMetadataLookups.Add(lookupKey))
                return;

            YouTube.ResolveTitleAndUploaderAsync(normalized, videoId, (resolvedTitle, resolvedUploader) =>
            {
                SyncVideoPlugin.SyncController.EnqueueMainThreadAction(() =>
                {
                    _pendingSuggestionMetadataLookups.Remove(lookupKey);

                    if (!_suggestions.TryGetValue(suggestionEntryKey, out var suggestion))
                        return;

                    if (!string.Equals(suggestion.Url, url, StringComparison.Ordinal))
                        return;

                    var titleToUse = string.IsNullOrWhiteSpace(resolvedTitle) ? suggestion.Title : resolvedTitle;
                    var uploaderToUse = string.IsNullOrWhiteSpace(resolvedUploader) ? suggestion.ChannelName : resolvedUploader;

                    if (string.Equals(suggestion.Title, titleToUse, StringComparison.Ordinal)
                        && string.Equals(suggestion.ChannelName, uploaderToUse, StringComparison.Ordinal))
                    {
                        return;
                    }

                    suggestion.Title = titleToUse;
                    suggestion.ChannelName = uploaderToUse;
                    _suggestions[suggestionEntryKey] = suggestion;
                    SuggestionsChanged?.Invoke();
                });
            });
        }


        public bool RemoveSuggestion(string suggestionEntryKey)
        {
            if (string.IsNullOrWhiteSpace(suggestionEntryKey))
                return false;

            var removed = _suggestions.Remove(suggestionEntryKey);
            if (removed)
            {
                _suggestionOrder.Remove(suggestionEntryKey);
                SuggestionsChanged?.Invoke();
            }

            return removed;
        }

        public void ClearSuggestions()
        {
            _suggestions.Clear();
            _suggestionOrder.Clear();
            _hostReceivedSuggestionKeys.Clear();
            _pendingSuggestionMetadataLookups.Clear();
            SuggestionsChanged?.Invoke();
        }

        public void SetSuggestionsOpen(bool open)
        {
            if (!IsHost || CurrentLobby == null)
                return;

            _suggestionsOpen = open;
            _hostShadow.SuggestionsOpen = open;
            if (CurrentLobby != null)
                CurrentLobby.SuggestionsOpen = open;
            SuggestionsChanged?.Invoke();

            // Do NOT push to native lobby, PushHostShadowToNativeLobby goes thru the ACN server and causes RefreshFromNative to overwrite CurrentLobby.MediaTimeSeconds
            if (CurrentLobby.Members.Count > 1)
            {
                _transport.BroadcastToLobby(new SyncVideoSuggestionsOpenPacket
                {
                    LobbyId = CurrentLobby.LobbyId,
                    IsOpen  = open
                });
            }
        }

        public void SendSuggestion(string url, string title)
        {
            if (IsHost || CurrentLobby == null || CurrentLobby.HostId == 0)
                return;

            var resolvedUrl        = url ?? string.Empty;
            var resolvedTitle      = string.IsNullOrEmpty(title) ? resolvedUrl : title;
            var normalizedForValidation = UrlNormalizer.Normalize(resolvedUrl, out var validationVideoId, out var validationDirectUrl);
            if (UrlNormalizer.IsDirectSuggestionUrlTooLong(normalizedForValidation, validationVideoId, validationDirectUrl, out var validationError)
                || !UrlNormalizer.ValidateSubmissionUrl(normalizedForValidation, validationVideoId, validationDirectUrl, out validationError))
            {
                _logger.LogWarning("[SyncVideo] Rejected unsafe suggestion URL before send: " + validationError);
                return;
            }

            _logger.LogInfo($"[SyncVideo] Sending suggestion for lobby {CurrentLobby.LobbyId}: {resolvedTitle}");

            // Send suggestion directly to the host
            _transport.SendToPlayer(new SyncVideoSuggestionPacket
            {
                LobbyId    = CurrentLobby.LobbyId,
                Url        = resolvedUrl,
                Title      = resolvedTitle,
                PlayerName = string.Empty
            }, CurrentLobby.HostId);
        }

        public void RequestSuggestionScan() { }

        private void ClearSuggestionState()
        {
            _suggestions.Clear();
            _suggestionOrder.Clear();
            _suggestionsOpen    = false;
            _hostReceivedSuggestionKeys.Clear();
            SuggestionsChanged?.Invoke();
        }

        public VideoLobbyManager(ManualLogSource logger, SyncVideoTransport transport)
        {
            _logger    = logger;
            _transport = transport;

            ClientLobbyManager.LobbiesUpdated += OnNativeLobbiesUpdated;
            ClientLobbyManager.LobbyChanged   += OnNativeLobbyChanged;
            _transport.SyncPacketReceived     += OnSyncPacketReceived;

            RefreshFromNative();
        }

        public void Dispose()
        {
            ClientLobbyManager.LobbiesUpdated -= OnNativeLobbiesUpdated;
            ClientLobbyManager.LobbyChanged   -= OnNativeLobbyChanged;
            _transport.SyncPacketReceived     -= OnSyncPacketReceived;
        }

        // Viewers send a StateRequest packet directly to the host so it broadcasts its current state
        public void RequestStateFromHost()
        {
            RequestFreshStateForCurrentLobby(true);
        }

        private void RequestFreshStateForCurrentLobby(bool force = false)
        {
            if (_offlineLobbyActive || !_transport.Connected || CurrentLobby == null || IsHost
                || string.IsNullOrWhiteSpace(CurrentLobby.LobbyId) || CurrentLobby.HostId == 0)
                return;

            if (!force && string.Equals(_requestedStateLobbyId, CurrentLobby.LobbyId, StringComparison.Ordinal) && _receivedFreshStateForCurrentLobby)
                return;

            if (!force && string.Equals(_requestedStateLobbyId, CurrentLobby.LobbyId, StringComparison.Ordinal) && _stateRequestRetryTimer > 0f && _stateRequestRetryTimer < 0.95f)
                return;

            _requestedStateLobbyId = CurrentLobby.LobbyId;
            _stateRequestRetryTimer = 0f;

            try
            {
                _transport.SendToPlayer(new SyncVideoStateRequestPacket
                {
                    LobbyId = CurrentLobby.LobbyId
                }, CurrentLobby.HostId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[SyncVideo] Failed to request fresh SyncVideo state: " + ex.Message);
            }
        }

        private void ResetFreshStateTracking(string lobbyId)
        {
            _requestedStateLobbyId = lobbyId;
            _receivedFreshStateForCurrentLobby = false;
            _stateRequestRetryTimer = 0f;
        }

        public void Tick(float deltaTime)
        {
            if (!IsHost)
            {
                if (!_offlineLobbyActive && CurrentLobby != null && _transport.Connected && !_receivedFreshStateForCurrentLobby)
                {
                    _stateRequestRetryTimer += deltaTime;
                    if (_stateRequestRetryTimer >= 0.5f)
                    {
                        _stateRequestRetryTimer = 0f;
                        RequestFreshStateForCurrentLobby(true);
                    }
                }

                return;
            }

            if (CurrentLobby == null)
                return;

            if (_offlineLobbyActive)
            {
                if (CurrentLobby.IsPlaying)
                {
                    _hostShadow.HostUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    UpdateOfflineLobbySnapshot(false, true);
                }

                return;
            }

            if (!_transport.Connected)
                return;

            // Drain timers
            if (_pushCooldownTimer > 0f)
                _pushCooldownTimer = Math.Max(0f, _pushCooldownTimer - deltaTime);

            // Drain burst retransmit timers
            _stateTimer += deltaTime;
            if (_stateTimer >= SyncVideoPlugin.Settings.HostStateResendInterval.Value)
            {
                _stateTimer = 0f;
                BroadcastTimePacket();
            }
        }

        public void HostLobby(string lobbyName = null)
        {
            _leaveInProgress = false;
            _syncVideoHostActive = true;
            if (!SyncVideoPlugin.ScreenManager.HasAnyScreensInMap())
                return;

            if (OfflineModeEnabled)
            {
                CreateOfflineLobby(lobbyName);
                return;
            }

            var native = NativeLobbyManager;
            if (native == null)
                return;

            if (!native.CanJoinLobby())
                return;

            if (native.CurrentLobby != null)
                native.LeaveLobby();

            _offlineLobbyActive = false;
            _hostShadow = new SyncShadow { IsSyncVideoLobby = true };

            HudManager.OnLobbyEnter();

            var defaultSettings = GamemodeFactory.GetGamemodeSettings(GamemodeIDs.ProSkaterScoreBattle);
            native.CreateLobby(GamemodeIDs.ProSkaterScoreBattle, defaultSettings);
        }

        public void JoinLobby(string lobbyId)
        {
            _leaveInProgress = false;
            if (OfflineModeEnabled || _offlineLobbyActive)
                return;

            var native = NativeLobbyManager;
            if (native == null || !SyncVideoPlugin.ScreenManager.HasAnyScreensInMap())
                return;

            // Avoid LINQ FirstOrDefault lambda allocation on every join attempt
            VideoLobby targetLobby = null;
            for (int _i = 0; _i < _visibleLobbies.Count; _i++)
            {
                var _l = _visibleLobbies[_i];
                if (_l != null && string.Equals(_l.LobbyId, lobbyId, StringComparison.Ordinal))
                { targetLobby = _l; break; }
            }
            if (targetLobby != null && !targetLobby.IsOpen)
                return;

            if (!uint.TryParse(lobbyId, out var parsedLobbyId))
                return;

            HudManager.OnLobbyEnter();

            native.JoinLobby(parsedLobbyId);
        }

        public void LeaveLobby()
        {
            if (_offlineLobbyActive)
            {
                var oldLobbyId = _lastCurrentLobbyId;

                _syncVideoHostActive = false;
                _offlineLobbyActive  = false;
                CurrentLobby         = null;
                _lastCurrentLobbyId  = null;
                _hostShadow          = new SyncShadow { IsSyncVideoLobby = true };
                _stateTimer          = 0f;
                _leaveInProgress     = false;
                ResetFreshStateTracking(null);

                if (!string.Equals(oldLobbyId, _lastCurrentLobbyId, StringComparison.Ordinal))
                    ActiveLobbyChanged?.Invoke(CurrentLobby);

                ClearSuggestionState();
                ActiveStateChanged?.Invoke(CurrentLobby);
                LobbiesChanged?.Invoke();
                return;
            }

            var native = NativeLobbyManager;
            if (native == null)
                return;

            var oldCurrentLobbyId = _lastCurrentLobbyId;
            _syncVideoHostActive = false;
            _leaveInProgress     = true;
            CurrentLobby         = null;
            _lastCurrentLobbyId  = null;
            _hostShadow          = new SyncShadow { IsSyncVideoLobby = true };
            _stateTimer          = 0f;
            ResetFreshStateTracking(null);

            if (!string.Equals(oldCurrentLobbyId, _lastCurrentLobbyId, StringComparison.Ordinal))
                ActiveLobbyChanged?.Invoke(CurrentLobby);

            ClearSuggestionState();
            ActiveStateChanged?.Invoke(CurrentLobby);
            LobbiesChanged?.Invoke();

            native.LeaveLobby();
        }

        public void SetVideo(string url, string videoId)
        {
            if (!IsHost || CurrentLobby == null)
                return;

            _hostShadow.IsSyncVideoLobby = true;
            _hostShadow.Url = url ?? string.Empty;
            _hostShadow.VideoId = videoId ?? string.Empty;
            _hostShadow.MediaTimeSeconds = 0d;
            _hostShadow.IsPlaying = false;
            _hostShadow.Revision++;
            _hostShadow.SeekRevision = 0;
            _hostShadow.HasEnded = false;
            _hostShadow.HostUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // Reset track selection — a new video starts with default audio and no subtitles
            _hostShadow.SelectedAudioTrack    = 0;
            _hostShadow.SelectedSubtitleTrack = -1;

            CurrentLobby.MediaTimeSeconds = 0d; // Fix for pause color
            CurrentLobby.IsPlaying = false;
            CurrentLobby.HasEnded  = false;
            CurrentLobby.SeekRevision = _hostShadow.SeekRevision;

            if (_offlineLobbyActive)
            {
                UpdateOfflineLobbySnapshot(true, true);
            }
            else
            {
                PushHostShadowToNativeLobby();
                BroadcastStatePacket();
                RefreshFromNative();
                ActiveStateChanged?.Invoke(CurrentLobby);
                LobbiesChanged?.Invoke();
            }
        }

        public void SetPlayback(bool playing)
        {
            if (!IsHost || CurrentLobby == null)
                return;

            _hostShadow.IsSyncVideoLobby = true;
            _hostShadow.IsPlaying = playing;
            if (playing)
                _hostShadow.HasEnded = false;
            _hostShadow.Revision++;
            _hostShadow.HostUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            CurrentLobby.IsPlaying = _hostShadow.IsPlaying;
            CurrentLobby.HasEnded  = _hostShadow.HasEnded;
            CurrentLobby.Revision  = _hostShadow.Revision;
            CurrentLobby.HostUnixMilliseconds = _hostShadow.HostUnixMilliseconds;

            if (_offlineLobbyActive)
            {
                UpdateOfflineLobbySnapshot(false, true);
            }
            else
            {
                BroadcastStatePacket();
                PushHostShadowToNativeLobby();
                ActiveStateChanged?.Invoke(CurrentLobby);
            }
        }

        public void SeekRelative(double seconds)
        {
            if (!IsHost || CurrentLobby == null)
                return;

            if (CurrentLobby.HasEnded || _hostShadow.HasEnded)
                return;

            _hostShadow.IsSyncVideoLobby    = true;
            _hostShadow.MediaTimeSeconds    = Math.Max(0d, _hostShadow.MediaTimeSeconds + seconds);
            _hostShadow.HasEnded            = false;
            _hostShadow.Revision++;
            _hostShadow.SeekRevision++;
            _hostShadow.HostUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            CurrentLobby.MediaTimeSeconds    = _hostShadow.MediaTimeSeconds;
            CurrentLobby.HasEnded            = _hostShadow.HasEnded;
            CurrentLobby.Revision            = _hostShadow.Revision;
            CurrentLobby.SeekRevision        = _hostShadow.SeekRevision;
            CurrentLobby.HostUnixMilliseconds = _hostShadow.HostUnixMilliseconds;

            if (_offlineLobbyActive)
            {
                UpdateOfflineLobbySnapshot(false, true);
            }
            else
            {
                BroadcastStatePacket();
                PushHostShadowToNativeLobby();
                ActiveStateChanged?.Invoke(CurrentLobby);
            }
        }

        public void NotifyPlaybackEnded(double seconds)
        {
            if (!IsHost || CurrentLobby == null)
                return;

            _hostShadow.IsSyncVideoLobby    = true;
            _hostShadow.IsPlaying           = false;
            _hostShadow.HasEnded            = true;
            _hostShadow.MediaTimeSeconds    = Math.Max(0d, seconds);
            _hostShadow.Revision++;
            _hostShadow.HostUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            CurrentLobby.IsPlaying          = false;
            CurrentLobby.HasEnded           = true;
            CurrentLobby.MediaTimeSeconds   = _hostShadow.MediaTimeSeconds;
            CurrentLobby.Revision           = _hostShadow.Revision;
            CurrentLobby.HostUnixMilliseconds = _hostShadow.HostUnixMilliseconds;

            if (_offlineLobbyActive)
            {
                UpdateOfflineLobbySnapshot(false, true);
            }
            else
            {
                BroadcastStatePacket();
                PushHostShadowToNativeLobby();
                ActiveStateChanged?.Invoke(CurrentLobby);
            }
        }

        public void RestartFromBeginning(bool playing)
        {
            if (!IsHost || CurrentLobby == null)
                return;

            _hostShadow.IsSyncVideoLobby    = true;
            _hostShadow.MediaTimeSeconds    = 0d;
            _hostShadow.IsPlaying           = playing;
            _hostShadow.HasEnded            = false;
            _hostShadow.Revision++;
            _hostShadow.HostUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            CurrentLobby.MediaTimeSeconds   = 0d;
            CurrentLobby.IsPlaying          = playing;
            CurrentLobby.HasEnded           = false;
            CurrentLobby.Revision           = _hostShadow.Revision;
            CurrentLobby.HostUnixMilliseconds = _hostShadow.HostUnixMilliseconds;

            if (_offlineLobbyActive)
            {
                UpdateOfflineLobbySnapshot(false, true);
            }
            else
            {
                BroadcastStatePacket();
                PushHostShadowToNativeLobby();
                ActiveStateChanged?.Invoke(CurrentLobby);
            }
        }

        // Broadcast the host audio and subtitle track
        public void SetMkvTrackSelection(int audioTrack, int subtitleTrack)
        {
            if (!IsHost || CurrentLobby == null)
                return;

            _hostShadow.SelectedAudioTrack    = audioTrack;
            _hostShadow.SelectedSubtitleTrack  = subtitleTrack;
            _hostShadow.Revision++;
            CurrentLobby.SelectedAudioTrack   = audioTrack;
            CurrentLobby.SelectedSubtitleTrack = subtitleTrack;
            CurrentLobby.Revision              = _hostShadow.Revision;

            if (_offlineLobbyActive)
                UpdateOfflineLobbySnapshot(true, true);
            else
            {
                PushHostShadowToNativeLobby();
                BroadcastStatePacket();
            }
        }

        public void SetObservedPlaybackTime(double seconds)
        {
            if (!IsHost || CurrentLobby == null)
                return;

            _hostShadow.MediaTimeSeconds = Math.Max(0d, seconds);
        }

        private void CreateOfflineLobby(string lobbyName)
        {
            var oldLobbyId = _lastCurrentLobbyId;

            HudManager.OnLobbyEnter();

            _offlineLobbyActive = true;
            _hostShadow = new SyncShadow
            {
                IsSyncVideoLobby     = true,
                HostUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            CurrentLobby = BuildOfflineSnapshot(lobbyName);
            _lastCurrentLobbyId = CurrentLobby?.LobbyId;
            _stateTimer = 0f;
            ResetFreshStateTracking(_lastCurrentLobbyId);

            if (!string.Equals(oldLobbyId, _lastCurrentLobbyId, StringComparison.Ordinal))
                ActiveLobbyChanged?.Invoke(CurrentLobby);

            ActiveStateChanged?.Invoke(CurrentLobby);
            LobbiesChanged?.Invoke();
        }

        private void UpdateOfflineLobbySnapshot(bool notifyLobbiesChanged, bool notifyActiveStateChanged)
        {
            if (!_offlineLobbyActive)
                return;

            CurrentLobby = BuildOfflineSnapshot(CurrentLobby?.LobbyName);
            _lastCurrentLobbyId = CurrentLobby?.LobbyId;

            if (notifyActiveStateChanged)
                ActiveStateChanged?.Invoke(CurrentLobby);

            if (notifyLobbiesChanged)
                LobbiesChanged?.Invoke();
        }

        private VideoLobby BuildOfflineSnapshot(string lobbyName)
        {
            var hostId = _transport.LocalPlayerId != 0 ? _transport.LocalPlayerId : ushort.MaxValue;
            var resolvedName = string.IsNullOrWhiteSpace(lobbyName)
                ? $"{SyncVideoConfig.DefaultLobbyName} (Offline)"
                : lobbyName;

            var lobby = new VideoLobby
            {
                LobbyId              = OfflineLobbyId,
                HostId               = hostId,
                LobbyName            = resolvedName,
                CurrentUrl           = _hostShadow.Url,
                CurrentVideoId       = _hostShadow.VideoId,
                IsPlaying            = _hostShadow.IsPlaying,
                MediaTimeSeconds     = _hostShadow.MediaTimeSeconds,
                HostUnixMilliseconds = _hostShadow.HostUnixMilliseconds,
                Revision             = _hostShadow.Revision,
                SeekRevision         = _hostShadow.SeekRevision,
                HasEnded             = _hostShadow.HasEnded,
                IsOpen               = _hostShadow.IsOpen,
                SelectedAudioTrack   = _hostShadow.SelectedAudioTrack,
                SelectedSubtitleTrack = _hostShadow.SelectedSubtitleTrack
            };

            lobby.Members.Add(hostId);
            return lobby;
        }

        private void OnNativeLobbiesUpdated()
        {
            if (_offlineLobbyActive)
                return;

            var oldLobbyId  = _lastCurrentLobbyId;
            var prevUrl      = CurrentLobby?.CurrentUrl;
            var prevRevision = CurrentLobby?.Revision ?? int.MinValue;
            var prevIsPlaying = CurrentLobby?.IsPlaying ?? false;
            var prevHasEnded = CurrentLobby?.HasEnded ?? false;

            RefreshFromNative();

            bool lobbyIdChanged = !string.Equals(oldLobbyId, _lastCurrentLobbyId, StringComparison.Ordinal);
            if (lobbyIdChanged)
            {
                ResetFreshStateTracking(_lastCurrentLobbyId);
                ActiveLobbyChanged?.Invoke(CurrentLobby);
            }

            TryApplyPendingStateForCurrentLobby();
            RequestFreshStateForCurrentLobby(false);

            // Only fire ActiveStateChanged if something meaningful changed in the lobby, like member join/leave
            bool contentChanged = lobbyIdChanged
                || CurrentLobby == null
                || (CurrentLobby.Revision != prevRevision)
                || !string.Equals(CurrentLobby.CurrentUrl, prevUrl, StringComparison.Ordinal)
                || CurrentLobby.IsPlaying != prevIsPlaying
                || CurrentLobby.HasEnded != prevHasEnded;

            if (contentChanged)
                ActiveStateChanged?.Invoke(CurrentLobby);

            LobbiesChanged?.Invoke();
            MaybeBroadcastHostState();
        }

        private void OnNativeLobbyChanged()
        {
            if (_offlineLobbyActive)
                return;

            EnsureHostLobbyIsTagged();

            var oldLobbyId   = _lastCurrentLobbyId;
            var prevUrl      = CurrentLobby?.CurrentUrl;
            var prevRevision = CurrentLobby?.Revision ?? int.MinValue;
            var prevIsPlaying = CurrentLobby?.IsPlaying ?? false;
            var prevHasEnded = CurrentLobby?.HasEnded ?? false;

            RefreshFromNative();

            bool lobbyIdChanged = !string.Equals(oldLobbyId, _lastCurrentLobbyId, StringComparison.Ordinal);
            if (lobbyIdChanged)
            {
                ResetFreshStateTracking(_lastCurrentLobbyId);
                ActiveLobbyChanged?.Invoke(CurrentLobby);

                if (CurrentLobby != null)
                    HudManager.OnLobbyEnter();
            }

            TryApplyPendingStateForCurrentLobby();
            RequestFreshStateForCurrentLobby(false);

            // Only fire ActiveStateChanged if something meaningful changed in the lobby, like member join/leave
            bool contentChanged = lobbyIdChanged
                || CurrentLobby == null
                || (CurrentLobby.Revision != prevRevision)
                || !string.Equals(CurrentLobby.CurrentUrl, prevUrl, StringComparison.Ordinal)
                || CurrentLobby.IsPlaying != prevIsPlaying
                || CurrentLobby.HasEnded != prevHasEnded;

            if (contentChanged)
                ActiveStateChanged?.Invoke(CurrentLobby);

            LobbiesChanged?.Invoke();
            MaybeBroadcastHostState();
        }

        private void OnSyncPacketReceived(SyncVideoPacketBase packet)
        {
            if (_offlineLobbyActive || packet == null)
                return;

            // Request the current full state on join
            var stateRequestPacket = packet as SyncVideoStateRequestPacket;
            if (stateRequestPacket != null)
            {
                if (!IsHost || CurrentLobby == null)
                    return;

                if (!string.Equals(CurrentLobby.LobbyId, stateRequestPacket.LobbyId, StringComparison.Ordinal))
                    return;

                _hostShadow.HostUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SendStatePacketToPlayer(packet.SenderPlayerId);
                return;
            }

            var suggestionPacket = packet as SyncVideoSuggestionPacket;
            if (suggestionPacket != null)
            {
                if (!IsHost || CurrentLobby == null)
                    return;
                if (!string.Equals(CurrentLobby.LobbyId, suggestionPacket.LobbyId, StringComparison.Ordinal))
                    return;

                AddOrUpdateSuggestion(
                    suggestionPacket.SenderPlayerId,
                    suggestionPacket.PlayerName,
                    suggestionPacket.Url,
                    suggestionPacket.Title,
                    "suggestion packet");
                SendSuggestionConfirmation(suggestionPacket.SenderPlayerId, suggestionPacket.Url);
                return;
            }

            var suggestionsOpenPacket = packet as SyncVideoSuggestionsOpenPacket;
            if (suggestionsOpenPacket != null)
            {
                if (IsHost)
                    return;
                if (CurrentLobby == null || !string.Equals(CurrentLobby.LobbyId, suggestionsOpenPacket.LobbyId, StringComparison.Ordinal))
                    return;
                _suggestionsOpen = suggestionsOpenPacket.IsOpen;
                SuggestionsChanged?.Invoke();
                return;
            }

            var suggestionAckPacket = packet as SyncVideoSuggestionAckPacket;
            if (suggestionAckPacket != null)
            {
                // Acknowledge a new viewer, but no pending suggestion state to clear
                return;
            }

            // Heartbeat to ONLY update MediaTimeSeconds
            var timePacket = packet as SyncVideoTimePacket;
            if (timePacket != null)
            {
                if (!IsHost
                    && !string.IsNullOrWhiteSpace(timePacket.LobbyId)
                    && CurrentLobby != null
                    && string.Equals(CurrentLobby.LobbyId, timePacket.LobbyId, StringComparison.Ordinal)
                    && (CurrentLobby.HostId == 0 || timePacket.SenderPlayerId == 0 || timePacket.SenderPlayerId == CurrentLobby.HostId))
                {
                    CurrentLobby.MediaTimeSeconds = Math.Max(0d, timePacket.MediaTimeSeconds);
                    CurrentLobby.IsPlaying        = timePacket.IsPlaying;
                    CurrentLobby.LastSeenSeconds  = UnityEngine.Time.unscaledTime;
                    if (timePacket.HostSentMilliseconds > 0)
                        CurrentLobby.HostUnixMilliseconds = timePacket.HostSentMilliseconds;
                    // No ActiveStateChanged since SyncVideoController.Tick() will read the updated values
                }
                return;
            }

            var statePacket = packet as SyncVideoStatePacket;
            if (statePacket == null)
                return;

            if (IsHost)
                return;

            if (string.IsNullOrWhiteSpace(statePacket.LobbyId))
                return;

            _pendingStatePackets[statePacket.LobbyId] = CloneStatePacket(statePacket);

            if (!statePacket.IsOpen && (CurrentLobby == null || !string.Equals(CurrentLobby.LobbyId, statePacket.LobbyId, StringComparison.Ordinal)))
                return;

            if (CurrentLobby == null || !string.Equals(CurrentLobby.LobbyId, statePacket.LobbyId, StringComparison.Ordinal))
            {
                var seededLobby = TryCreateCurrentLobbyFromPacket(statePacket);
                if (seededLobby == null)
                    return;

                var oldLobbyId = _lastCurrentLobbyId;
                CurrentLobby = seededLobby;
                _lastCurrentLobbyId = CurrentLobby.LobbyId;

                if (!string.Equals(oldLobbyId, _lastCurrentLobbyId, StringComparison.Ordinal))
                    ActiveLobbyChanged?.Invoke(CurrentLobby);
            }

            if (CurrentLobby.HostId != 0 && statePacket.SenderPlayerId != 0 && statePacket.SenderPlayerId != CurrentLobby.HostId)
                return;

            _requestedStateLobbyId = CurrentLobby.LobbyId;
            _receivedFreshStateForCurrentLobby = true;
            _stateRequestRetryTimer = 0f;

            // Separate the video/playback state from video position changes, that way existing viewers aren't forced to resync
            // Position updates from state packets must reset LastSeenSeconds or use ActiveStateChanged
            bool structuralChanged = statePacket.Revision > CurrentLobby.Revision
                || !string.Equals(CurrentLobby.CurrentUrl,       statePacket.Url,     StringComparison.Ordinal)
                || !string.Equals(CurrentLobby.CurrentVideoId,   statePacket.VideoId, StringComparison.Ordinal)
                || CurrentLobby.IsPlaying  != statePacket.IsPlaying
                || CurrentLobby.HasEnded   != statePacket.HasEnded
                || CurrentLobby.IsOpen     != statePacket.IsOpen
                || CurrentLobby.SelectedAudioTrack    != statePacket.SelectedAudioTrack
                || CurrentLobby.SelectedSubtitleTrack != statePacket.SelectedSubtitleTrack;

            if (!structuralChanged)
                return;

            CurrentLobby.CurrentUrl           = statePacket.Url ?? string.Empty;
            CurrentLobby.CurrentVideoId       = statePacket.VideoId ?? string.Empty;
            CurrentLobby.IsPlaying            = statePacket.IsPlaying;
            CurrentLobby.HasEnded             = statePacket.HasEnded;
            CurrentLobby.IsOpen               = statePacket.IsOpen;
            CurrentLobby.SuggestionsOpen      = statePacket.SuggestionsOpen;
            CurrentLobby.Revision             = statePacket.Revision;
            CurrentLobby.SeekRevision         = statePacket.SeekRevision;
            CurrentLobby.SelectedAudioTrack   = statePacket.SelectedAudioTrack;
            CurrentLobby.SelectedSubtitleTrack = statePacket.SelectedSubtitleTrack;
            CurrentLobby.MediaTimeSeconds     = Math.Max(0d, statePacket.MediaTimeSeconds);
            CurrentLobby.HostUnixMilliseconds = statePacket.HostUnixMilliseconds;
            CurrentLobby.LastSeenSeconds      = Time.unscaledTime;

            if (!IsHost && _suggestionsOpen != statePacket.SuggestionsOpen)
            {
                _suggestionsOpen = statePacket.SuggestionsOpen;
                SuggestionsChanged?.Invoke();
            }

            ActiveStateChanged?.Invoke(CurrentLobby);
            LobbiesChanged?.Invoke();
        }


        private void TryApplyPendingStateForCurrentLobby()
        {
            if (CurrentLobby == null || string.IsNullOrWhiteSpace(CurrentLobby.LobbyId))
                return;

            if (!_pendingStatePackets.TryGetValue(CurrentLobby.LobbyId, out var statePacket) || statePacket == null)
                return;

            if (CurrentLobby.HostId != 0 && statePacket.SenderPlayerId != 0 && statePacket.SenderPlayerId != CurrentLobby.HostId)
                return;

            _requestedStateLobbyId = CurrentLobby.LobbyId;
            _receivedFreshStateForCurrentLobby = true;
            _stateRequestRetryTimer = 0f;

            // Only apply for major change
            bool structuralChanged = statePacket.Revision > CurrentLobby.Revision
                || !string.Equals(CurrentLobby.CurrentUrl,       statePacket.Url,     StringComparison.Ordinal)
                || !string.Equals(CurrentLobby.CurrentVideoId,   statePacket.VideoId, StringComparison.Ordinal)
                || CurrentLobby.IsPlaying  != statePacket.IsPlaying
                || CurrentLobby.HasEnded   != statePacket.HasEnded
                || CurrentLobby.IsOpen     != statePacket.IsOpen
                || CurrentLobby.SelectedAudioTrack    != statePacket.SelectedAudioTrack
                || CurrentLobby.SelectedSubtitleTrack != statePacket.SelectedSubtitleTrack;

            if (!structuralChanged)
                return;

            CurrentLobby.CurrentUrl           = statePacket.Url ?? string.Empty;
            CurrentLobby.CurrentVideoId       = statePacket.VideoId ?? string.Empty;
            CurrentLobby.IsPlaying            = statePacket.IsPlaying;
            CurrentLobby.HasEnded             = statePacket.HasEnded;
            CurrentLobby.IsOpen               = statePacket.IsOpen;
            CurrentLobby.SuggestionsOpen      = statePacket.SuggestionsOpen;
            CurrentLobby.Revision             = statePacket.Revision;
            CurrentLobby.SeekRevision         = statePacket.SeekRevision;
            CurrentLobby.SelectedAudioTrack   = statePacket.SelectedAudioTrack;
            CurrentLobby.SelectedSubtitleTrack = statePacket.SelectedSubtitleTrack;
            CurrentLobby.MediaTimeSeconds     = Math.Max(0d, statePacket.MediaTimeSeconds);
            CurrentLobby.HostUnixMilliseconds = statePacket.HostUnixMilliseconds;
            CurrentLobby.LastSeenSeconds      = Time.unscaledTime;

            if (!IsHost && _suggestionsOpen != statePacket.SuggestionsOpen)
            {
                _suggestionsOpen = statePacket.SuggestionsOpen;
                SuggestionsChanged?.Invoke();
            }
        }

        private static SyncVideoStatePacket CloneStatePacket(SyncVideoStatePacket source)
        {
            return new SyncVideoStatePacket
            {
                LobbyId              = source.LobbyId ?? string.Empty,
                Url                  = source.Url ?? string.Empty,
                VideoId              = source.VideoId ?? string.Empty,
                IsPlaying            = source.IsPlaying,
                MediaTimeSeconds     = source.MediaTimeSeconds,
                HostUnixMilliseconds = source.HostUnixMilliseconds,
                Revision             = source.Revision,
                SeekRevision         = source.SeekRevision,
                HasEnded             = source.HasEnded,
                IsOpen               = source.IsOpen,
                SuggestionsOpen      = source.SuggestionsOpen,
                SenderPlayerId       = source.SenderPlayerId,
                SelectedAudioTrack   = source.SelectedAudioTrack,
                SelectedSubtitleTrack = source.SelectedSubtitleTrack
            };
        }

        private void MaybeBroadcastHostState()
        {
            if (_offlineLobbyActive || !IsHost || CurrentLobby == null || !_transport.Connected)
                return;

            _hostShadow.HostUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // Push current shadow to native lobby so joining viewers get a fresh MediaTimeSeconds w/ cooldown
            if (_pushCooldownTimer <= 0f)
                PushHostShadowToNativeLobby();

            BroadcastStatePacket();
        }

        private VideoLobby TryCreateCurrentLobbyFromPacket(SyncVideoStatePacket statePacket)
        {
            var native = NativeLobbyManager;
            if (native == null || native.CurrentLobby == null || native.CurrentLobby.LobbyState == null)
                return null;

            var state = native.CurrentLobby.LobbyState;
            if (!string.Equals(state.Id.ToString(), statePacket.LobbyId, StringComparison.Ordinal))
                return null;

            var hostName = GetHostName(state.HostId);
            var lobby = new VideoLobby
            {
                LobbyId              = statePacket.LobbyId,
                HostId               = state.HostId,
                LobbyName            = string.IsNullOrWhiteSpace(hostName) ? "Host Lobby" : $"{hostName} Lobby",
                CurrentUrl           = statePacket.Url ?? string.Empty,
                CurrentVideoId       = statePacket.VideoId ?? string.Empty,
                IsPlaying            = statePacket.IsPlaying,
                HasEnded             = statePacket.HasEnded,
                MediaTimeSeconds     = Math.Max(0d, statePacket.MediaTimeSeconds),
                HostUnixMilliseconds = statePacket.HostUnixMilliseconds,
                Revision             = statePacket.Revision,
                SeekRevision         = statePacket.SeekRevision,
                IsOpen               = statePacket.IsOpen,
                LastSeenSeconds      = Time.unscaledTime,
                SelectedAudioTrack   = statePacket.SelectedAudioTrack,
                SelectedSubtitleTrack = statePacket.SelectedSubtitleTrack
            };

            if (state.Players != null)
            {
                foreach (var player in state.Players)
                    lobby.Members.Add(player.Key);
            }

            return lobby;
        }

        private void EnsureHostLobbyIsTagged()
        {
            // Only tag the lobby as SyncVideo if the host explicitly it as one to prevent issues with ACN gamemodes
            if (!_syncVideoHostActive)
                return;

            var native = NativeLobbyManager;
            if (native == null || native.CurrentLobby == null || native.CurrentLobby.LobbyState == null)
                return;

            if (native.CurrentLobby.LobbyState.HostId != _transport.LocalPlayerId)
                return;

            ParseSyncShadow(native.CurrentLobby.LobbyState.GamemodeSettings, out var isSyncVideo);
            if (isSyncVideo)
                return; // Keep the authoritative local shadow, since server copy has stale MediaTimeSeconds

            _hostShadow.IsSyncVideoLobby     = true;
            _hostShadow.HostUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PushHostShadowToNativeLobby();
        }

        private void RefreshFromNative()
        {
            _refreshPreviousById.Clear();

            if (CurrentLobby != null && !string.IsNullOrWhiteSpace(CurrentLobby.LobbyId))
                _refreshPreviousById[CurrentLobby.LobbyId] = CurrentLobby;

            for (int i = 0; i < _visibleLobbies.Count; i++)
            {
                var existing = _visibleLobbies[i];
                if (existing != null && !string.IsNullOrWhiteSpace(existing.LobbyId) && !_refreshPreviousById.ContainsKey(existing.LobbyId))
                    _refreshPreviousById[existing.LobbyId] = existing;
            }

            _visibleLobbies.Clear();

            if (_offlineLobbyActive)
            {
                CurrentLobby = BuildOfflineSnapshot(CurrentLobby?.LobbyName);
                if (CurrentLobby != null && _refreshPreviousById.TryGetValue(CurrentLobby.LobbyId, out var previousOffline))
                    CurrentLobby.LastSeenSeconds = previousOffline.LastSeenSeconds;

                _lastCurrentLobbyId = CurrentLobby != null ? CurrentLobby.LobbyId : null;
                return;
            }

            var native = NativeLobbyManager;
            if (native != null)
            {
                // Avoid LINQ OrderBy
                _refreshLobbyKeys.Clear();
                foreach (var _k in native.Lobbies.Keys)
                    _refreshLobbyKeys.Add(_k);
                _refreshLobbyKeys.Sort();
                foreach (var _lobbyKey in _refreshLobbyKeys)
                {
                    if (!native.Lobbies.TryGetValue(_lobbyKey, out var _nativeLobby))
                        continue;
                    var snapshot = BuildSnapshot(_nativeLobby, false);
                    if (snapshot != null)
                    {
                        if (_refreshPreviousById.TryGetValue(snapshot.LobbyId, out var previousSnapshot))
                            snapshot.LastSeenSeconds = previousSnapshot.LastSeenSeconds;

                        if (snapshot.IsOpen)
                            _visibleLobbies.Add(snapshot);
                    }
                }

                if (_leaveInProgress)
                {
                    CurrentLobby = null;
                    if (native.CurrentLobby == null)
                        _leaveInProgress = false;
                }
                else
                {
                    CurrentLobby = native.CurrentLobby != null ? BuildSnapshot(native.CurrentLobby, true) : null;
                    if (CurrentLobby != null && _refreshPreviousById.TryGetValue(CurrentLobby.LobbyId, out var previousCurrent))
                    {
                        // Preserve live position data set by time/state packets
                        CurrentLobby.LastSeenSeconds = previousCurrent.LastSeenSeconds;
                        if (previousCurrent.MediaTimeSeconds > 0d)
                        {
                            CurrentLobby.MediaTimeSeconds     = previousCurrent.MediaTimeSeconds;
                            CurrentLobby.HostUnixMilliseconds = previousCurrent.HostUnixMilliseconds;
                        }
                        if (previousCurrent.Revision > CurrentLobby.Revision)
                            CurrentLobby.Revision = previousCurrent.Revision;
                    }
                }
            }
            else
            {
                CurrentLobby = null;
                _leaveInProgress = false;
            }

            _lastCurrentLobbyId = CurrentLobby != null ? CurrentLobby.LobbyId : null;

            // Keep Suggestions access in sync with host setting
            if (!IsHost && CurrentLobby != null)
                _suggestionsOpen = CurrentLobby.SuggestionsOpen;
        }

        private VideoLobby BuildSnapshot(Lobby nativeLobby, bool allowCurrentUnmarked)
        {
            if (nativeLobby == null || nativeLobby.LobbyState == null)
                return null;

            var state = nativeLobby.LobbyState;
            var sync = ParseSyncShadow(state.GamemodeSettings, out var isSyncVideo);

            if (!isSyncVideo)
            {
                if (!allowCurrentUnmarked)
                    return null;

                // Only treat lobby as a SyncVideo lobby if the host activated via SyncVideo
                if (state.HostId == _transport.LocalPlayerId && _syncVideoHostActive)
                    sync = _hostShadow.Clone();
                else
                    return null;
            }

            var hostName = GetHostName(state.HostId);
            var lobby = new VideoLobby
            {
                LobbyId               = state.Id.ToString(),
                HostId                = state.HostId,
                LobbyName             = string.IsNullOrWhiteSpace(hostName) ? "Host Lobby" : $"{hostName} Lobby",
                CurrentUrl            = sync.Url,
                CurrentVideoId        = sync.VideoId,
                IsPlaying             = sync.IsPlaying,
                MediaTimeSeconds      = sync.MediaTimeSeconds,
                HostUnixMilliseconds  = sync.HostUnixMilliseconds,
                Revision              = sync.Revision,
                SeekRevision          = sync.SeekRevision,
                HasEnded              = sync.HasEnded,
                IsOpen                = sync.IsOpen,
                SuggestionsOpen       = sync.SuggestionsOpen,
                SelectedAudioTrack    = sync.SelectedAudioTrack,
                SelectedSubtitleTrack = sync.SelectedSubtitleTrack
            };

            if (state.Players != null)
            {
                foreach (var player in state.Players)
                    lobby.Members.Add(player.Key);
            }

            return lobby;
        }

        private static string SanitizeLobbyDisplayName(string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return string.Empty;

            var cleaned = _sanitizeSpriteRx.Replace(rawName, string.Empty);
            cleaned = _sanitizeTagRx.Replace(cleaned, string.Empty);
            cleaned = cleaned.Replace("\n", " ").Replace("\r", " ").Trim();
            return cleaned;
        }

        public string SanitizeDisplayNameForUi(string rawName)
        {
            return SanitizeLobbyDisplayName(rawName);
        }

        private string GetHostName(ushort hostId)
        {
            try
            {
                if (ClientController.Instance != null &&
                    ClientController.Instance.Players != null &&
                    ClientController.Instance.Players.TryGetValue(hostId, out var player))
                {
                    return SanitizeLobbyDisplayName(MPUtility.GetPlayerDisplayName(player.ClientState));
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        public string GetPlayerDisplayName(ushort playerId)
        {
            try
            {
                if (ClientController.Instance != null &&
                    ClientController.Instance.Players != null &&
                    ClientController.Instance.Players.TryGetValue(playerId, out var player))
                {
                    var name = SanitizeLobbyDisplayName(MPUtility.GetPlayerDisplayName(player.ClientState));
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;
                }
            }
            catch
            {
            }

            return $"Player {playerId}";
        }

        public bool KickPlayer(ushort playerId)
        {
            if (!IsHost || CurrentLobby == null || playerId == 0 || playerId == _transport.LocalPlayerId || playerId == CurrentLobby.HostId)
                return false;

            try
            {
                var packetType = Type.GetType("BombRushMP.Common.Packets.ClientLobbyKick, BombRushMP.Common");
                if (packetType == null)
                    return false;

                var packet = Activator.CreateInstance(packetType) as Packet;
                if (packet == null)
                    return false;

                bool assigned = false;
                var candidates = new[] { "PlayerId", "TargetPlayerId", "KickedPlayerId", "ClientId", "Id" };
                foreach (var name in candidates)
                {
                    var field = packetType.GetField(name);
                    if (field != null && (field.FieldType == typeof(ushort) || field.FieldType == typeof(int)))
                    {
                        if (field.FieldType == typeof(ushort)) field.SetValue(packet, playerId); else field.SetValue(packet, (int)playerId);
                        assigned = true;
                        break;
                    }

                    var prop = packetType.GetProperty(name);
                    if (prop != null && prop.CanWrite && (prop.PropertyType == typeof(ushort) || prop.PropertyType == typeof(int)))
                    {
                        if (prop.PropertyType == typeof(ushort)) prop.SetValue(packet, playerId, null); else prop.SetValue(packet, (int)playerId, null);
                        assigned = true;
                        break;
                    }
                }

                if (!assigned)
                {
                    foreach (var field in packetType.GetFields())
                    {
                        if ((field.FieldType == typeof(ushort) || field.FieldType == typeof(int)) && field.Name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (field.FieldType == typeof(ushort)) field.SetValue(packet, playerId); else field.SetValue(packet, (int)playerId);
                            assigned = true;
                            break;
                        }
                    }
                }

                if (!assigned)
                    return false;

                // Send the native BombRushMP kick packet directly through ClientController
                ClientController.Instance.SendPacket(
                    packet,
                    BombRushMP.Common.Networking.IMessage.SendModes.ReliableUnordered,
                    NetChannels.ClientAndLobbyUpdates);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to kick player from lobby: " + ex.Message);
                return false;
            }
        }

        private void PushHostShadowToNativeLobby()
        {
            // Reset debounce on every call to prevent MaybeBroadcastHostState from re-pushing when the ServerLobbiesUpdate echo fires OnNativeLobbiesUpdated
            _pushCooldownTimer = 3.0f; // NO LOOP FOR YOU

            var native = NativeLobbyManager;
            if (native == null || native.CurrentLobby == null)
                return;

            try
            {
                var baseSettings = GamemodeFactory.GetGamemodeSettings(GamemodeIDs.ProSkaterScoreBattle);
                byte[] settingsBytes;

                using (var ms = new MemoryStream())
                using (var writer = new BinaryWriter(ms, Encoding.UTF8))
                {
                    baseSettings.Write(writer);
                    WriteSyncShadow(writer, _hostShadow);
                    writer.Flush();
                    settingsBytes = ms.ToArray();
                }

                ClientController.Instance.SendPacket(
                    new ClientLobbySetGamemode(GamemodeIDs.ProSkaterScoreBattle, settingsBytes),
                    IMessage.SendModes.ReliableUnordered,
                    NetChannels.ClientAndLobbyUpdates);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to push SyncVideo state into native lobby settings: " + ex);
            }
        }

        private void BroadcastTimePacket()
        {
            if (_offlineLobbyActive || !_transport.Connected || CurrentLobby == null)
                return;
            if (CurrentLobby.Members.Count <= 1)
                return;

            try
            {
                // Get the exact MediaTimeSeconds so viewers can compensate for the time spent loading
                var sentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _hostShadow.HostUnixMilliseconds = sentMs;

                _transport.BroadcastToLobby(new SyncVideoTimePacket
                {
                    LobbyId              = CurrentLobby.LobbyId ?? string.Empty,
                    MediaTimeSeconds     = _hostShadow.MediaTimeSeconds,
                    IsPlaying            = _hostShadow.IsPlaying,
                    HostSentMilliseconds = sentMs,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to broadcast SyncVideo time packet: " + ex);
            }
        }

        private void BroadcastStatePacket(bool skipMemberCheck = false)
        {
            if (_offlineLobbyActive || !_transport.Connected || CurrentLobby == null)
                return;

            // Skip the broadcast when no other members are present to receive it
            if (!skipMemberCheck && CurrentLobby.Members.Count <= 1)
                return;

            try
            {
                _transport.BroadcastToLobby(new SyncVideoStatePacket
                {
                    LobbyId              = CurrentLobby.LobbyId ?? string.Empty,
                    Url                  = _hostShadow.Url ?? string.Empty,
                    VideoId              = _hostShadow.VideoId ?? string.Empty,
                    IsPlaying            = _hostShadow.IsPlaying,
                    MediaTimeSeconds     = _hostShadow.MediaTimeSeconds,
                    HostUnixMilliseconds = _hostShadow.HostUnixMilliseconds,
                    Revision             = _hostShadow.Revision,
                    SeekRevision         = _hostShadow.SeekRevision,
                    HasEnded             = _hostShadow.HasEnded,
                    IsOpen               = _hostShadow.IsOpen,
                    SuggestionsOpen      = _hostShadow.SuggestionsOpen,
                    SelectedAudioTrack   = _hostShadow.SelectedAudioTrack,
                    SelectedSubtitleTrack = _hostShadow.SelectedSubtitleTrack
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to broadcast SyncVideo host state: " + ex.Message);
            }
        }

        private void SendStatePacketToPlayer(ushort targetPlayerId)
        {
            if (_offlineLobbyActive || !_transport.Connected || CurrentLobby == null)
                return;

            try
            {
                _transport.SendToPlayer(new SyncVideoStatePacket
                {
                    LobbyId              = CurrentLobby.LobbyId ?? string.Empty,
                    Url                  = _hostShadow.Url ?? string.Empty,
                    VideoId              = _hostShadow.VideoId ?? string.Empty,
                    IsPlaying            = _hostShadow.IsPlaying,
                    MediaTimeSeconds     = _hostShadow.MediaTimeSeconds,
                    HostUnixMilliseconds = _hostShadow.HostUnixMilliseconds,
                    Revision             = _hostShadow.Revision,
                    SeekRevision         = _hostShadow.SeekRevision,
                    HasEnded             = _hostShadow.HasEnded,
                    IsOpen               = _hostShadow.IsOpen,
                    SuggestionsOpen      = _hostShadow.SuggestionsOpen,
                    SelectedAudioTrack   = _hostShadow.SelectedAudioTrack,
                    SelectedSubtitleTrack = _hostShadow.SelectedSubtitleTrack
                }, targetPlayerId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to send SyncVideo host state to player " + targetPlayerId + ": " + ex.Message);
            }
        }

        private SyncShadow ParseSyncShadow(byte[] settingsBytes, out bool isSyncVideo)
        {
            var result = new SyncShadow();
            isSyncVideo = false;
            if (settingsBytes == null || settingsBytes.Length < 9)
                return result;

            try
            {
                for (int i = settingsBytes.Length - 4; i >= 0; i--)
                {
                    if (BitConverter.ToInt32(settingsBytes, i) != SyncMagic)
                        continue;

                    using (var ms = new MemoryStream(settingsBytes))
                    using (var reader = new BinaryReader(ms, Encoding.UTF8))
                    {
                        ms.Position = i;
                        var magic   = reader.ReadInt32();
                        var version = reader.ReadByte();
                        if (magic != SyncMagic || version < 1 || version > SyncVersion)
                            continue;

                        result.IsSyncVideoLobby  = reader.ReadBoolean();
                        result.Url               = reader.ReadString();
                        result.VideoId           = reader.ReadString();
                        result.IsPlaying         = reader.ReadBoolean();
                        result.MediaTimeSeconds  = reader.ReadDouble();
                        result.HostUnixMilliseconds = reader.ReadInt64();
                        result.Revision          = reader.ReadInt32();
                        result.HasEnded          = version >= 2 && ms.Position < ms.Length ? reader.ReadBoolean() : false;
                        result.IsOpen            = version >= 3 && ms.Position < ms.Length ? reader.ReadBoolean() : true;
                        result.SuggestionsOpen   = version >= 4 && ms.Position < ms.Length ? reader.ReadBoolean() : false;
                        result.SelectedAudioTrack    = version >= 5 && ms.Position < ms.Length ? reader.ReadInt32() : 0;
                        result.SelectedSubtitleTrack = version >= 5 && ms.Position < ms.Length ? reader.ReadInt32() : -1;
                        result.SeekRevision = ms.Position < ms.Length ? reader.ReadInt32() : 0;
                        isSyncVideo = result.IsSyncVideoLobby;
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse SyncVideo lobby metadata: " + ex.Message);
            }

            return result;
        }

        private void WriteSyncShadow(BinaryWriter writer, SyncShadow shadow)
        {
            writer.Write(SyncMagic);
            writer.Write(SyncVersion);
            writer.Write(shadow.IsSyncVideoLobby);
            writer.Write(shadow.Url ?? string.Empty);
            writer.Write(shadow.VideoId ?? string.Empty);
            writer.Write(shadow.IsPlaying);
            writer.Write(shadow.MediaTimeSeconds);
            writer.Write(shadow.HostUnixMilliseconds);
            writer.Write(shadow.Revision);
            writer.Write(shadow.HasEnded);
            writer.Write(shadow.IsOpen);
            writer.Write(shadow.SuggestionsOpen);
            writer.Write(shadow.SelectedAudioTrack);
            writer.Write(shadow.SelectedSubtitleTrack);
            writer.Write(shadow.SeekRevision);
        }
    }
}