using CommonAPI;
using CommonAPI.Phone;
using System.IO;
using System.Reflection;
using SyncVideo.Runtime;
using TMPro;
using UnityEngine;

namespace SyncVideo.Phone
{
    public sealed class AppSyncVideo : CustomApp
    {
        internal static Sprite _iconSprite;
        private bool _built;
        private bool _lastInLobbyState;
        private bool _pendingLobbyOpen;

        public static void Initialize()
        {
            var iconPath = Path.Combine(SyncVideoPlugin.Settings.PluginDirectory, "syncvideo_icon.png");
            _iconSprite = File.Exists(iconPath)
                ? TextureUtility.LoadSprite(iconPath)
                : null;

            PhoneAPI.RegisterApp<AppSyncVideo>("sync video", _iconSprite);
        }

        public override void OnAppInit()
        {
            base.OnAppInit();
            CreateTitleBar("Sync Video", _iconSprite);
            ScrollView = PhoneScrollView.Create(this);
        }

        public override void OnAppEnable()
        {
            base.OnAppEnable();
            if (AppSyncVideoLobby.ReopenRequested && SyncVideoPlugin.LobbyManager.InLobby)
            {
                AppSyncVideoLobby.ReopenRequested = false;
                MyPhone.OpenApp(typeof(AppSyncVideoLobby));
                return;
            }

            AppSyncVideoLobby.ReopenRequested = false;
            _lastInLobbyState = SyncVideoPlugin.LobbyManager.InLobby;
            BuildButtons();
            _built = true;
        }

        public override void OnAppDisable()
        {
            base.OnAppDisable();
            _built = false;
            _pendingLobbyOpen = false;
        }

        public override void OnAppUpdate()
        {
            base.OnAppUpdate();

            bool inLobby = SyncVideoPlugin.LobbyManager.InLobby;
            if (_pendingLobbyOpen && inLobby && SyncVideoPlugin.LobbyManager.CurrentLobby != null)
            {
                _pendingLobbyOpen = false;
                MyPhone.OpenApp(typeof(AppSyncVideoLobby));
                return;
            }

            if (!_built || inLobby != _lastInLobbyState)
            {
                _lastInLobbyState = inLobby;
                BuildButtons();
                _built = true;
            }
        }

        public override void OnPressLeft()
        {
            base.OnPressLeft();
        }

        private static string GetLobbyUiToggleLabel()
        {
            return SyncVideoPlugin.Settings.HideNativeLobbyUi.Value
                ? "Lobby UI: <color=green>Hidden</color>"
                : "Lobby UI: <color=red>Visible</color>";
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
                    return;
                }

                var updateTextLabel = type.GetMethod("UpdateTextLabel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                if (updateTextLabel != null)
                {
                    updateTextLabel.Invoke(button, new object[] { label });
                    return;
                }

                var textField = type.GetField("textLabel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                               ?? type.GetField("label", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (textField != null)
                {
                    if (textField.GetValue(button) is TMP_Text tmp)
                    {
                        tmp.text = label;
                        return;
                    }

                    if (textField.GetValue(button) is GameObject go)
                    {
                        var tmpInChildren = go.GetComponentInChildren<TMP_Text>(true);
                        if (tmpInChildren != null)
                        {
                            tmpInChildren.text = label;
                            return;
                        }
                    }
                }

                if (button is Component component)
                {
                    var tmp = component.GetComponentInChildren<TMP_Text>(true);
                    if (tmp != null)
                        tmp.text = label;
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

            ScrollView.RemoveAllButtons();

            if (!SyncVideoPlugin.ScreenManager.HasAnyScreensInMap())
            {
                var none = PhoneUIUtility.CreateSimpleButton("No Screens!");
                ScrollView.AddButton(none);

                var rebind = PhoneUIUtility.CreateSimpleButton("Re-scan Map");
                rebind.OnConfirm += () =>
                {
                    SyncVideoPlugin.ScreenManager.Rebind();
                    BuildButtons();
                };
                ScrollView.AddButton(rebind);
                return;
            }

            var lobbyManager = SyncVideoPlugin.LobbyManager;

            if (!lobbyManager.InLobby)
            {
                var hostButton = PhoneUIUtility.CreateSimpleButton(lobbyManager.OfflineModeEnabled ? "Host Offline" : "Host Lobby");
                hostButton.OnConfirm += () =>
                {
                    _pendingLobbyOpen = true;
                    lobbyManager.HostLobby();

                    if (SyncVideoPlugin.LobbyManager.InLobby && SyncVideoPlugin.LobbyManager.CurrentLobby != null)
                    {
                        _pendingLobbyOpen = false;
                        MyPhone.OpenApp(typeof(AppSyncVideoLobby));
                    }
                };
                ScrollView.AddButton(hostButton);

                if (!lobbyManager.OfflineModeEnabled)
                {
                    var joinButton = PhoneUIUtility.CreateSimpleButton("Join Lobby");
                    joinButton.OnConfirm += () =>
                    {
                        MyPhone.OpenApp(typeof(AppSyncVideoPublicLobbies));
                    };
                    ScrollView.AddButton(joinButton);
                }
                else
                {
                    var offlineInfo = PhoneUIUtility.CreateSimpleButton("Offline Mode Enabled");
                    ScrollView.AddButton(offlineInfo);
                }
            }
            else
            {
                var currentLobbyButton = PhoneUIUtility.CreateSimpleButton("Current Lobby");
                currentLobbyButton.OnConfirm += () => MyPhone.OpenApp(typeof(AppSyncVideoLobby));
                ScrollView.AddButton(currentLobbyButton);

                string leaveLabel = lobbyManager.IsHost ? "<color=red>Close Lobby</color>" : "Leave Lobby";
                var leaveLobbyButton = PhoneUIUtility.CreateSimpleButton(leaveLabel);
                leaveLobbyButton.OnConfirm += () =>
                {
                    HudManager.Reset();
                    SyncVideoPlugin.LobbyManager.LeaveLobby();
                    BuildButtons();
                };
                ScrollView.AddButton(leaveLobbyButton);
            }

            /*
            var lobbyUiToggle = PhoneUIUtility.CreateSimpleButton(GetLobbyUiToggleLabel());
            lobbyUiToggle.OnConfirm += () =>
            {
                SyncVideoPlugin.Settings.HideNativeLobbyUi.Value = !SyncVideoPlugin.Settings.HideNativeLobbyUi.Value;
                TrySetButtonLabel(lobbyUiToggle, GetLobbyUiToggleLabel());
            };
            ScrollView.AddButton(lobbyUiToggle);
            */

            if (SyncVideoPlugin.Settings.ShowRefreshScreensButton.Value)
            {
                var rebindButton = PhoneUIUtility.CreateSimpleButton("Refresh Screens");
                rebindButton.OnConfirm += () =>
                {
                    SyncVideoPlugin.ScreenManager.Rebind();
                    BuildButtons();
                };
                ScrollView.AddButton(rebindButton);
            }
        }
    }
}