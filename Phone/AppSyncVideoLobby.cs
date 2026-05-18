using CommonAPI.Phone;
using System;
using System.Reflection;
using System.Text;
using SyncVideo.Runtime;
using TMPro;
using UnityEngine;

namespace SyncVideo.Phone
{
    public sealed class AppSyncVideoLobby : CustomApp
    {
        public override bool Available => false;
        public static bool ReopenRequested;

        private bool _showingWaitingState;
        private bool _reopenAfterPromptCancel;
        private object _playPauseButton;
        private string _lastPlayPauseLabel;
        private object _suggestionsToggleButton;
        private string _lastSuggestionsToggleLabel;
        private object _viewSuggestionsButton;
        private bool _lastSuggestionsOpen;
        private string _lastViewSuggestionsLabel;
        private object _viewerSuggestButton;
        private bool _lastViewerSuggestionsOpen;
        private object _playlistModeButton;
        private string _lastPlaylistModeLabel;
        private object _setUrlButton;
        private string _lastSetUrlLabel;
        private object _mkvSettingsButton;
        private bool _lastShowMkvSettings;
        private int _suggestionValidationRequestId;
        private bool _suggestionValidationInProgress;

        public static void Initialize()
        {
            PhoneAPI.RegisterApp<AppSyncVideoLobby>("sync video lobby");
        }

        public override void OnAppInit()
        {
            base.OnAppInit();
            CreateTitleBar("Video Lobby", AppSyncVideo._iconSprite);
            ScrollView = PhoneScrollView.Create(this);
        }

        public override void OnAppEnable()
        {
            base.OnAppEnable();
            BuildButtons();
        }

        private static bool ShouldBlockInput => UrlPromptOverlay.IsVisible && !UrlPromptOverlay.IsConfirmation;

        public override void OnPressLeft()
        {
            if (UrlPromptOverlay.IsVisible)
            {
                if (UrlPromptOverlay.IsConfirmation)
                {
                    UrlPromptOverlay.Hide();
                    return;
                }
                _reopenAfterPromptCancel = true;
                UrlPromptOverlay.Hide();
                return;
            }

            _reopenAfterPromptCancel = false;
            base.OnPressLeft();
        }

        public override void OnPressRight()
        {
            if (UrlPromptOverlay.IsVisible && UrlPromptOverlay.IsConfirmation) { UrlPromptOverlay.Hide(); return; }
            if (ShouldBlockInput) return;
            base.OnPressRight();
        }

        public override void OnPressUp()
        {
            if (UrlPromptOverlay.IsVisible && UrlPromptOverlay.IsConfirmation) { UrlPromptOverlay.Hide(); return; }
            if (ShouldBlockInput) return;
            base.OnPressUp();
        }

        public override void OnPressDown()
        {
            if (UrlPromptOverlay.IsVisible && UrlPromptOverlay.IsConfirmation) { UrlPromptOverlay.Hide(); return; }
            if (ShouldBlockInput) return;
            base.OnPressDown();
        }

        public override void OnHoldUp()
        {
            if (ShouldBlockInput) return;
            base.OnHoldUp();
        }

        public override void OnHoldDown()
        {
            if (ShouldBlockInput) return;
            base.OnHoldDown();
        }

        public override void OnReleaseUp()
        {
            if (ShouldBlockInput) return;
            base.OnReleaseUp();
        }

        public override void OnReleaseDown()
        {
            if (ShouldBlockInput) return;
            base.OnReleaseDown();
        }

        public override void OnReleaseRight()
        {
            if (ShouldBlockInput) return;
            base.OnReleaseRight();
        }

        public override void OnAppDisable()
        {
            // Only reopen the lobby app if the viewer explicitly cancelled a URL prompt
            ReopenRequested = _reopenAfterPromptCancel
                && SyncVideoPlugin.LobbyManager.InLobby
                && !SyncVideoPlugin.LobbyManager.LeaveInProgress;

            _reopenAfterPromptCancel = false;
            base.OnAppDisable();
            UrlPromptOverlay.Hide();
            _playPauseButton = null;
            _lastPlayPauseLabel = null;
            _suggestionsToggleButton = null;
            _lastSuggestionsToggleLabel = null;
            _viewSuggestionsButton = null;
            _lastViewSuggestionsLabel = null;
            _viewerSuggestButton = null;
            _playlistModeButton = null;
            _lastPlaylistModeLabel = null;
            _setUrlButton = null;
            _lastSetUrlLabel = null;
            _mkvSettingsButton = null;
            _lastShowMkvSettings = false;
            _suggestionValidationInProgress = false;
            _suggestionValidationRequestId++;
        }

        public override void OnAppUpdate()
        {
            base.OnAppUpdate();

            if (!SyncVideoPlugin.LobbyManager.InLobby)
            {
                if (_showingWaitingState)
                {
                    var current = SyncVideoPlugin.LobbyManager.CurrentLobby;
                    if (current != null)
                        BuildButtons();

                    return;
                }

                // If lobby closes unexpectantly fix for locking players in lobby menu
                HudManager.Reset();
                ReopenRequested = false;
                MyPhone.CloseCurrentApp();
                return;
            }

            if (_showingWaitingState && SyncVideoPlugin.LobbyManager.CurrentLobby != null)
                BuildButtons();

            // Fix for play / pause button getting stuck on yellow
            if (_playPauseButton != null)
            {
                string newLabel = GetPlayPauseLabel();
                if (newLabel != _lastPlayPauseLabel)
                {
                    _lastPlayPauseLabel = newLabel;
                    TrySetButtonLabel(_playPauseButton, newLabel);
                }
            }

            // Update suggestions toggle
            if (_suggestionsToggleButton != null)
            {
                string newLabel = GetSuggestionsToggleLabel();
                if (newLabel != _lastSuggestionsToggleLabel)
                {
                    _lastSuggestionsToggleLabel = newLabel;
                    TrySetButtonLabel(_suggestionsToggleButton, newLabel);
                }
            }

            if (_viewSuggestionsButton != null)
            {
                bool open = SyncVideoPlugin.LobbyManager.SuggestionsOpen;
                string newLabel = GetViewSuggestionsLabel(open);
                if (open != _lastSuggestionsOpen || newLabel != _lastViewSuggestionsLabel)
                {
                    _lastSuggestionsOpen = open;
                    _lastViewSuggestionsLabel = newLabel;
                    TrySetButtonLabel(_viewSuggestionsButton, newLabel);
                }
            }

            if (_viewerSuggestButton != null)
            {
                bool open = SyncVideoPlugin.LobbyManager.SuggestionsOpen;
                if (open != _lastViewerSuggestionsOpen)
                {
                    _lastViewerSuggestionsOpen = open;
                    TrySetButtonLabel(_viewerSuggestButton,
                        open ? "Suggest Video" : "<color=grey>Suggest Video</color>");
                }
            }

            if (_playlistModeButton != null)
            {
                string newLabel = GetPlaylistModeLabel();
                if (newLabel != _lastPlaylistModeLabel)
                {
                    _lastPlaylistModeLabel = newLabel;
                    TrySetButtonLabel(_playlistModeButton, newLabel);
                }
            }

            if (_setUrlButton != null)
            {
                string newLabel = GetHostSubmitLabel();
                if (newLabel != _lastSetUrlLabel)
                {
                    _lastSetUrlLabel = newLabel;
                    TrySetButtonLabel(_setUrlButton, newLabel);
                }
            }

            bool shouldShowMkvSettings = SyncVideoPlugin.SyncController.ShouldShowMkvSettings();
            if (shouldShowMkvSettings != _lastShowMkvSettings)
            {
                _lastShowMkvSettings = shouldShowMkvSettings;
                BuildButtons();
                return;
            }

            if (_suggestionValidationInProgress && !UrlPromptOverlay.IsVisible)
            {
                _suggestionValidationInProgress = false;
                _suggestionValidationRequestId++;
            }

        }

        private static string GetPlayPauseLabel()
        {
            var lobby = SyncVideoPlugin.LobbyManager.CurrentLobby;
            bool hasUrl = lobby != null && !string.IsNullOrEmpty(lobby.CurrentUrl);
            bool isPlaying = lobby != null && lobby.IsPlaying;
            bool hasEnded = lobby != null && lobby.HasEnded;
            bool isPaused = hasUrl && !isPlaying && !hasEnded && lobby != null && lobby.MediaTimeSeconds > 0.05;
            return isPaused ? "<color=yellow>Play / Pause</color>" : "Play / Pause";
        }

        private static string GetVolumeBar()
        {
            if (SyncVideoPlugin.SyncController.IsMuted)
                return "\n<size=70%><color=red>[MUTED]</color></size>";

            float volume = SyncVideoPlugin.SyncController.LocalVolume;
            int filled = Mathf.Clamp(Mathf.RoundToInt(volume * 10f), 0, 10);
            int pct = filled * 10;

            var sb = new StringBuilder();
            sb.Append("[");
            for (int i = 0; i < 10; i++)
            {
                if (i < filled)
                {
                    string hex = VolumeGradientHex(i / 9f);
                    sb.Append("<color=#").Append(hex).Append(">#</color>");
                }
                else
                {
                    sb.Append("-");
                }
            }
            sb.Append("] ").Append(pct).Append("%");

            return "\n<size=70%>" + sb + "</size>";
        }

        // Gardient Volume Bar. Maps t in [0,1] to a green, yellow, then red color string (RRGGBB)
        private static string VolumeGradientHex(float t)
        {
            int r, g;
            if (t <= 0.5f)
            {
                r = Mathf.RoundToInt(t * 2f * 255f);
                g = 255;
            }
            else
            {
                r = 255;
                g = Mathf.RoundToInt((1f - t) * 2f * 255f);
            }
            return r.ToString("X2") + g.ToString("X2") + "00";
        }

        private static string GetVolumeDownLabel()
        {
            string bar = GetVolumeBar();
            return SyncVideoPlugin.SyncController.LocalVolume <= 0f
                ? "No Volume" + bar
                : "Volume Down (-)" + bar;
        }

        private static string GetVolumeUpLabel()
        {
            string bar = GetVolumeBar();
            return SyncVideoPlugin.SyncController.LocalVolume >= 1f
                ? "Max Volume" + bar
                : "Volume Up (+)" + bar;
        }

        private static string GetLobbyUiToggleLabel()
        {
            return SyncVideoPlugin.Settings.HideNativeLobbyUi.Value
                ? "Lobby UI: <color=green>Hidden</color>"
                : "Lobby UI: <color=red>Visible</color>";
        }

        private static string GetLobbyOpenToggleLabel()
        {
            var lobby = SyncVideoPlugin.LobbyManager.CurrentLobby;
            return lobby != null && lobby.IsOpen
                ? "Lobby: <color=green>Open</color>"
                : "Lobby: <color=red>Closed</color>";
        }

        private static string GetAutoplayToggleLabel()
        {
            return SyncVideoPlugin.Settings.HostAutoplay.Value
                ? "Autoplay: <color=green>On</color>"
                : "Autoplay: <color=red>Off</color>";
        }

        private static string GetSuggestionsToggleLabel()
        {
            return SyncVideoPlugin.LobbyManager.SuggestionsOpen
                ? "Suggestions: <color=green>Open</color>"
                : "Suggestions: <color=red>Closed</color>";
        }

        private static string GetPlaylistModeLabel()
        {
            return SyncVideoPlugin.LobbyManager.PlaylistModeEnabled
                ? "Playlist Mode: <color=green>On</color>"
                : "Playlist Mode: <color=red>Off</color>";
        }

        private static string GetHostSubmitLabel()
        {
            return SyncVideoPlugin.LobbyManager.PlaylistModeEnabled ? "Add URL to Queue" : "Set URL";
        }

        private static string GetViewSuggestionsLabel(bool suggestionsOpen)
        {
            if (SyncVideoPlugin.LobbyManager.PlaylistModeEnabled)
                return "Playlist Queue";

            return suggestionsOpen
                ? "View Suggestions"
                : "<color=grey>View Suggestions</color>";
        }
        private static string GetMuteLabel()
        {
            bool isMuted = SyncVideoPlugin.SyncController.IsMuted;

            return isMuted
                ? "<color=red>Mute / Unmute</color>"
                : "Mute / Unmute";
        }

        // Format a duration in seconds as H:MM:SS (>=1 hour) or M:SS
        private static string FormatVideoTime(double totalSeconds)
        {
            int t = (int)Math.Max(0d, totalSeconds);
            int h = t / 3600;
            int m = (t % 3600) / 60;
            int s = t % 60;
            return h > 0
                ? $"{h}:{m:D2}:{s:D2}"
                : $"{m}:{s:D2}";
        }

        // Parse H:MM:SS, MM:SS, or plain seconds. Returns false if the string is not a valid non-negative time.
        private static bool TryParseTimeInput(string input, out double seconds)
        {
            seconds = 0d;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            string[] parts = input.Trim().Split(':');
            try
            {
                if (parts.Length == 1)
                {
                    if (!double.TryParse(parts[0],
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double sec) || sec < 0d)
                        return false;
                    seconds = sec;
                    return true;
                }
                else if (parts.Length == 2)
                {
                    // MM:SS
                    if (!int.TryParse(parts[0], out int min) || min < 0)
                        return false;
                    if (!double.TryParse(parts[1],
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double sec) || sec < 0d)
                        return false;
                    seconds = min * 60d + sec;
                    return true;
                }
                else if (parts.Length == 3)
                {
                    // H:MM:SS
                    if (!int.TryParse(parts[0], out int hrs) || hrs < 0)
                        return false;
                    if (!int.TryParse(parts[1], out int min) || min < 0)
                        return false;
                    if (!double.TryParse(parts[2],
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double sec) || sec < 0d)
                        return false;
                    seconds = hrs * 3600d + min * 60d + sec;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static void TrySetButtonLabel(object button, string label)
        {
            if (button == null || string.IsNullOrEmpty(label))
                return;

            var type = button.GetType();

            try
            {
                var setText = type.GetMethod("SetText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (setText != null)
                {
                    setText.Invoke(button, new object[] { label });
                }

                var updateTextLabel = type.GetMethod("UpdateTextLabel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (updateTextLabel != null)
                {
                    updateTextLabel.Invoke(button, new object[] { label });
                }

                var textField = type.GetField("textLabel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                               ?? type.GetField("label", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (textField != null)
                {
                    if (textField.GetValue(button) is TMP_Text tmp)
                    {
                        tmp.richText = true;
                        tmp.text = label;
                        return;
                    }

                    if (textField.GetValue(button) is GameObject go)
                    {
                        var tmpInChildren = go.GetComponentInChildren<TMP_Text>(true);
                        if (tmpInChildren != null)
                        {
                            tmpInChildren.richText = true;
                            tmpInChildren.text = label;
                            return;
                        }
                    }
                }

                if (button is Component component)
                {
                    var tmp = component.GetComponentInChildren<TMP_Text>(true);
                    if (tmp != null)
                    {
                        tmp.richText = true;
                        tmp.text = label;
                    }
                }
            }
            catch
            {
            }
        }

        private void BuildButtons()
        {
            if (ScrollView == null)
                return;

            var lobby = SyncVideoPlugin.LobbyManager.CurrentLobby;
            ScrollView.RemoveAllButtons();

            if (lobby == null)
            {
                _showingWaitingState = true;

                var waiting = PhoneUIUtility.CreateSimpleButton("Waiting for Lobby...");
                ScrollView.AddButton(waiting);

                /*
                var back = PhoneUIUtility.CreateSimpleButton("Back");
                back.OnConfirm += () => MyPhone.OpenApp(typeof(AppSyncVideo));
                ScrollView.AddButton(back);
                return;
                */
            }

            _showingWaitingState = false;

            if (SyncVideoPlugin.LobbyManager.IsHost)
            {
                _setUrlButton = null;
                _lastSetUrlLabel = GetHostSubmitLabel();
                var setUrl = PhoneUIUtility.CreateSimpleButton(_lastSetUrlLabel);
                _setUrlButton = setUrl;
                setUrl.OnConfirm += () =>
                {
                    if (_suggestionValidationInProgress)
                        return;

                    UrlPromptOverlay.Show(value =>
                    {
                        var normalized = UrlNormalizer.Normalize(value, out var videoId, out var directPlayableUrl);
                        if (!UrlNormalizer.ValidateSubmissionUrl(normalized, videoId, directPlayableUrl, out var validationError))
                        {
                            UrlPromptOverlay.ShowConfirmation($"{validationError}\n\n<size=75%>Press left arrow key to go back.</size>");
                            return;
                        }

                        if (SyncVideoPlugin.LobbyManager.PlaylistModeEnabled)
                        {
                            if (UrlNormalizer.IsDirectSuggestionUrlTooLong(normalized, videoId, directPlayableUrl, out validationError))
                            {
                                UrlPromptOverlay.ShowConfirmation($"{validationError}\n\n<size=75%>Press left arrow key to go back.</size>");
                                return;
                            }

                            _suggestionValidationInProgress = true;
                            int validationToken = ++_suggestionValidationRequestId;

                            UrlPromptOverlay.ShowConfirmation(
                                "Checking video...\n\n<size=75%>Press left arrow key to go back.</size>");

                            YouTube.ValidateSuggestionAsync(
                                normalized,
                                videoId,
                                directPlayableUrl,
                                title =>
                                {
                                    SyncVideoPlugin.SyncController.EnqueueMainThreadAction(() =>
                                    {
                                        if (validationToken != _suggestionValidationRequestId)
                                            return;

                                        _suggestionValidationInProgress = false;
                                        if (SyncVideoPlugin.LobbyManager.QueueHostUrl(normalized, string.IsNullOrWhiteSpace(title) ? normalized : title))
                                            UrlPromptOverlay.ShowConfirmation("<color=green>Added to queue!</color>\n\n<size=75%>Press left arrow key to go back.</size>");
                                        else
                                            UrlPromptOverlay.ShowConfirmation("<color=red>URL Error!</color>\n\n<size=75%>Press left arrow key to go back.</size>");
                                    });
                                },
                                errorMessage =>
                                {
                                    SyncVideoPlugin.SyncController.EnqueueMainThreadAction(() =>
                                    {
                                        if (validationToken != _suggestionValidationRequestId)
                                            return;

                                        _suggestionValidationInProgress = false;
                                        UrlPromptOverlay.ShowConfirmation(
                                            $"{errorMessage}\n\n<size=75%>Press left arrow key to go back.</size>");
                                    });
                                });
                            return;
                        }

                        SyncVideoPlugin.SyncController.HostSetUrl(normalized);
                    }, true);
                };
                ScrollView.AddButton(setUrl);

                _playPauseButton = null;
                var playPause = PhoneUIUtility.CreateSimpleButton(GetPlayPauseLabel());
                _playPauseButton = playPause;
                _lastPlayPauseLabel = GetPlayPauseLabel();
                playPause.OnConfirm += () =>
                {
                    var currentLobby = SyncVideoPlugin.LobbyManager.CurrentLobby;
                    bool isPlaying = currentLobby != null && currentLobby.IsPlaying;

                    if (isPlaying)
                        SyncVideoPlugin.SyncController.HostPause();
                    else
                        SyncVideoPlugin.SyncController.HostPlay();

                    TrySetButtonLabel(playPause, GetPlayPauseLabel());
                };
                ScrollView.AddButton(playPause);

                var restart = PhoneUIUtility.CreateSimpleButton("Restart Video");
                restart.OnConfirm += () =>
                {
                    var currentLobby = SyncVideoPlugin.LobbyManager.CurrentLobby;
                    if (currentLobby == null || string.IsNullOrEmpty(currentLobby.CurrentUrl))
                    {
                        UrlPromptOverlay.ShowConfirmation("No Video Loaded!\n\n<size=75%>Press left arrow key to go back.</size>");
                        return;
                    }
                    SyncVideoPlugin.SyncController.HostRestart();
                };
                ScrollView.AddButton(restart);

                _lastShowMkvSettings = SyncVideoPlugin.SyncController.ShouldShowMkvSettings();
                if (_lastShowMkvSettings)
                {
                    var mkvSettings = PhoneUIUtility.CreateSimpleButton("MKV Settings");
                    _mkvSettingsButton = mkvSettings;
                    mkvSettings.OnConfirm += () => MyPhone.OpenApp(typeof(AppSyncVideoMkvSettings));
                    ScrollView.AddButton(mkvSettings);
                }
            }

            // Changing tracks here overrides host settings sync
            if (!SyncVideoPlugin.LobbyManager.IsHost)
            {
                bool viewerShowMkv = SyncVideoPlugin.SyncController.ShouldShowMkvSettings();
                _lastShowMkvSettings = viewerShowMkv;
                if (viewerShowMkv)
                {
                    var mkvSettings = PhoneUIUtility.CreateSimpleButton("MKV Settings");
                    _mkvSettingsButton = mkvSettings;
                    mkvSettings.OnConfirm += () => MyPhone.OpenApp(typeof(AppSyncVideoMkvSettings));
                    ScrollView.AddButton(mkvSettings);
                }
            }

            var volumeDown = PhoneUIUtility.CreateSimpleButton(GetVolumeDownLabel());
            var volumeUp = PhoneUIUtility.CreateSimpleButton(GetVolumeUpLabel());
            var mute = PhoneUIUtility.CreateSimpleButton(GetMuteLabel());

            volumeDown.OnConfirm += () =>
            {
                if (SyncVideoPlugin.SyncController.IsMuted)
                    SyncVideoPlugin.SyncController.ToggleMute();
                SyncVideoPlugin.SyncController.AdjustLocalVolume(-0.1f);
                TrySetButtonLabel(volumeDown, GetVolumeDownLabel());
                TrySetButtonLabel(volumeUp, GetVolumeUpLabel());
                TrySetButtonLabel(mute, GetMuteLabel());
            };
            ScrollView.AddButton(volumeDown);

            volumeUp.OnConfirm += () =>
            {
                if (SyncVideoPlugin.SyncController.IsMuted)
                    SyncVideoPlugin.SyncController.ToggleMute();
                SyncVideoPlugin.SyncController.AdjustLocalVolume(0.1f);
                TrySetButtonLabel(volumeUp, GetVolumeUpLabel());
                TrySetButtonLabel(volumeDown, GetVolumeDownLabel());
                TrySetButtonLabel(mute, GetMuteLabel());
            };
            ScrollView.AddButton(volumeUp);

            mute.OnConfirm += () =>
            {
                SyncVideoPlugin.SyncController.ToggleMute();
                TrySetButtonLabel(mute, GetMuteLabel());
                TrySetButtonLabel(volumeDown, GetVolumeDownLabel());
                TrySetButtonLabel(volumeUp, GetVolumeUpLabel());
            };

            ScrollView.AddButton(mute);

            if (SyncVideoPlugin.LobbyManager.IsHost)
            {
                var back = PhoneUIUtility.CreateSimpleButton("Seek << 5 sec");
                back.OnConfirm += () => SyncVideoPlugin.SyncController.HostSeekRelative(-5d);
                ScrollView.AddButton(back);

                var forward = PhoneUIUtility.CreateSimpleButton("Seek >> 5 sec");
                forward.OnConfirm += () => SyncVideoPlugin.SyncController.HostSeekRelative(5d);
                ScrollView.AddButton(forward);

                var seekToTime = PhoneUIUtility.CreateSimpleButton("Seek to Time...");
                seekToTime.OnConfirm += () =>
                {
                    string currentTimePlaceholder = FormatVideoTime(SyncVideoPlugin.SyncController.Backend.CurrentTimeSeconds);
                    UrlPromptOverlay.Show(input =>
                    {
                        if (!TryParseTimeInput(input, out double requestedSeconds))
                        {
                            UrlPromptOverlay.ShowConfirmation("<color=red>Invalid time!</color>\n\n<size=75%>Press left arrow key to go back.</size>");
                            return;
                        }

                        double dur = SyncVideoPlugin.SyncController.VideoDurationSeconds;
                        if (dur <= 0d || requestedSeconds < 0d || requestedSeconds > dur - 3d)
                        {
                            string message;

                            if (dur > 0d)
                            {
                                message = "<color=red>Invalid time!</color>\n\n" + $"<size=75%>Valid range: 0:00 - {FormatVideoTime(dur - 3d)}</size>\n\n" + "<size=75%>Press left arrow key to go back.</size>";
                            }
                            else
                            {
                                message = "<color=red>No video loaded!</color>\n\n" + "<size=75%>Press left arrow key to go back.</size>";
                            }

                            UrlPromptOverlay.ShowConfirmation(message);
                            return;
                        }

                        SyncVideoPlugin.SyncController.HostSeekToTime(requestedSeconds);
                    }, false,
                        "Seek to Time",
                        "Enter the specific time you want to seek to.\nType \"4:20\" to seek four minutes and twenty seconds in.\n\nPress left arrow key to cancel.",
                        "Current Video Time: " + currentTimePlaceholder);
                };
                ScrollView.AddButton(seekToTime);
            }

            if (SyncVideoPlugin.Settings.ShowScreenPositionMenu.Value)
            {
                var screenButton = PhoneUIUtility.CreateSimpleButton("Adjust Screen");
                screenButton.OnConfirm += () => MyPhone.OpenApp(typeof(AppSyncVideoScreenOptions));
                ScrollView.AddButton(screenButton);
            }

            var lobbyUiToggle = PhoneUIUtility.CreateSimpleButton(GetLobbyUiToggleLabel());
            lobbyUiToggle.OnConfirm += () =>
            {
                SyncVideoPlugin.Settings.HideNativeLobbyUi.Value = !SyncVideoPlugin.Settings.HideNativeLobbyUi.Value;
                TrySetButtonLabel(lobbyUiToggle, GetLobbyUiToggleLabel());
            };
            ScrollView.AddButton(lobbyUiToggle);

            if (SyncVideoPlugin.LobbyManager.IsHost)
            {
                var hideHud = PhoneUIUtility.CreateSimpleButton(HudManager.GetLabel());
                hideHud.OnConfirm += () =>
                {
                    HudManager.Cycle();
                    TrySetButtonLabel(hideHud, HudManager.GetLabel());
                };
                ScrollView.AddButton(hideHud);

                var autoplayToggle = PhoneUIUtility.CreateSimpleButton(GetAutoplayToggleLabel());
                autoplayToggle.OnConfirm += () =>
                {
                    SyncVideoPlugin.Settings.HostAutoplay.Value = !SyncVideoPlugin.Settings.HostAutoplay.Value;
                    TrySetButtonLabel(autoplayToggle, GetAutoplayToggleLabel());
                };
                ScrollView.AddButton(autoplayToggle);

                var lobbyOpenToggle = PhoneUIUtility.CreateSimpleButton(GetLobbyOpenToggleLabel());
                TrySetButtonLabel(lobbyOpenToggle, GetLobbyOpenToggleLabel());
                lobbyOpenToggle.OnConfirm += () =>
                {
                    SyncVideoPlugin.LobbyManager.ToggleLobbyOpen();
                    TrySetButtonLabel(lobbyOpenToggle, GetLobbyOpenToggleLabel());
                };
                ScrollView.AddButton(lobbyOpenToggle);

                _playlistModeButton = null;
                var playlistMode = PhoneUIUtility.CreateSimpleButton(GetPlaylistModeLabel());
                _playlistModeButton = playlistMode;
                _lastPlaylistModeLabel = GetPlaylistModeLabel();
                playlistMode.OnConfirm += () =>
                {
                    SyncVideoPlugin.LobbyManager.TogglePlaylistMode();
                    TrySetButtonLabel(playlistMode, GetPlaylistModeLabel());
                    TrySetButtonLabel(_setUrlButton, GetHostSubmitLabel());
                    TrySetButtonLabel(_viewSuggestionsButton, GetViewSuggestionsLabel(SyncVideoPlugin.LobbyManager.SuggestionsOpen));
                };
                ScrollView.AddButton(playlistMode);

                _suggestionsToggleButton = null;
                _viewSuggestionsButton = null;
                var suggestionsToggle = PhoneUIUtility.CreateSimpleButton(GetSuggestionsToggleLabel());
                _suggestionsToggleButton = suggestionsToggle;
                _lastSuggestionsToggleLabel = GetSuggestionsToggleLabel();
                suggestionsToggle.OnConfirm += () =>
                {
                    SyncVideoPlugin.LobbyManager.SetSuggestionsOpen(!SyncVideoPlugin.LobbyManager.SuggestionsOpen);
                };
                ScrollView.AddButton(suggestionsToggle);

                bool suggestionsOpen = SyncVideoPlugin.LobbyManager.SuggestionsOpen;
                _lastSuggestionsOpen = suggestionsOpen;
                _lastViewSuggestionsLabel = GetViewSuggestionsLabel(suggestionsOpen);
                var viewSuggestions = PhoneUIUtility.CreateSimpleButton(_lastViewSuggestionsLabel);
                _viewSuggestionsButton = viewSuggestions;
                viewSuggestions.OnConfirm += () =>
                {
                    if (SyncVideoPlugin.LobbyManager.PlaylistModeEnabled || SyncVideoPlugin.LobbyManager.SuggestionsOpen)
                        MyPhone.OpenApp(typeof(AppSyncVideoSuggestions));
                };
                ScrollView.AddButton(viewSuggestions);

                var kickPlayers = PhoneUIUtility.CreateSimpleButton("Kick Viewers");
                kickPlayers.OnConfirm += () => MyPhone.OpenApp(typeof(AppSyncVideoLobbyKick));
                ScrollView.AddButton(kickPlayers);
            }
            else
            {
                var hideHud = PhoneUIUtility.CreateSimpleButton(HudManager.GetLabel());
                hideHud.OnConfirm += () =>
                {
                    HudManager.Cycle();
                    TrySetButtonLabel(hideHud, HudManager.GetLabel());
                };
                ScrollView.AddButton(hideHud);

                // Gray out when suggestions are closed
                _viewerSuggestButton = null;
                bool viewerSuggestOpen = SyncVideoPlugin.LobbyManager.SuggestionsOpen;
                _lastViewerSuggestionsOpen = viewerSuggestOpen;
                string suggestLabel = viewerSuggestOpen
                    ? "Suggest Video"
                    : "<color=grey>Suggest Video</color>";
                var suggest = PhoneUIUtility.CreateSimpleButton(suggestLabel);
                _viewerSuggestButton = suggest;
                suggest.OnConfirm += () =>
                {
                    if (!SyncVideoPlugin.LobbyManager.SuggestionsOpen)
                        return;
                    if (_suggestionValidationInProgress)
                        return;

                    _suggestionValidationInProgress = true;
                    int validationToken = ++_suggestionValidationRequestId;

                    UrlPromptOverlay.Show(url =>
                    {
                        var normalized = UrlNormalizer.Normalize(url, out var videoId, out var directPlayableUrl);

                        string validationError = string.Empty;

                        if (string.IsNullOrWhiteSpace(videoId))
                        {
                            _suggestionValidationInProgress = false;
                            UrlPromptOverlay.ShowConfirmation(
                                "<color=red>Video not suggested!</color>\n\n<size=75%><color=red>Only YouTube links allowed!</color></size>\n\n<size=75%>Press left arrow key to go back.</size>");
                            return;
                        }

                        if (UrlNormalizer.IsDirectSuggestionUrlTooLong(normalized, videoId, directPlayableUrl, out validationError))
                        {
                            _suggestionValidationInProgress = false;
                            UrlPromptOverlay.ShowConfirmation(
                                $"{validationError}\n\n<size=75%>Press left arrow key to go back.</size>");
                            return;
                        }

                        if (string.IsNullOrWhiteSpace(normalized) ||
                            !UrlNormalizer.ValidateSubmissionUrl(normalized, videoId, directPlayableUrl, out validationError))
                        {
                            _suggestionValidationInProgress = false;
                            UrlPromptOverlay.ShowConfirmation(
                                $"{validationError}\n\n<size=75%>Press left arrow key to go back.</size>");
                            return;
                        }

                        UrlPromptOverlay.ShowConfirmation(
                            "Checking video...\n\n<size=75%>Press left arrow key to cancel.</size>");

                        YouTube.ValidateSuggestionAsync(
                            normalized,
                            videoId,
                            directPlayableUrl,
                            title =>
                            {
                                SyncVideoPlugin.SyncController.EnqueueMainThreadAction(() =>
                                {
                                    if (validationToken != _suggestionValidationRequestId)
                                        return;

                                    _suggestionValidationInProgress = false;
                                    SyncVideoPlugin.LobbyManager.SendSuggestion(normalized, string.IsNullOrWhiteSpace(title) ? normalized : title);
                                    UrlPromptOverlay.ShowConfirmation(
                                        "<color=green>Video submitted!</color>\n\n<size=75%>Press left arrow to go back.</size>");
                                });
                            },
                            errorMessage =>
                            {
                                SyncVideoPlugin.SyncController.EnqueueMainThreadAction(() =>
                                {
                                    if (validationToken != _suggestionValidationRequestId)
                                        return;

                                    _suggestionValidationInProgress = false;
                                    UrlPromptOverlay.ShowConfirmation(
                                        $"{errorMessage}\n\n<size=75%>Press left arrow key to go back.</size>");
                                });
                            });
                    }, true);
                };
                ScrollView.AddButton(suggest);
            }

            string leaveLabel = SyncVideoPlugin.LobbyManager.IsHost
            ? "<color=red>Close Lobby</color>"
            : "Leave Lobby";

            var leave = PhoneUIUtility.CreateSimpleButton(leaveLabel);

            leave.OnConfirm += () =>
            {
                HudManager.Reset();
                SyncVideoPlugin.LobbyManager.LeaveLobby();
                MyPhone.CloseCurrentApp();
            };

            ScrollView.AddButton(leave);
        }
    }
}
