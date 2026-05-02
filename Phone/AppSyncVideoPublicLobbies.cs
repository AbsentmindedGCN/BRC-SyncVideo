using CommonAPI.Phone;
using System.Linq;

namespace SyncVideo.Phone
{
    public sealed class AppSyncVideoPublicLobbies : CustomApp
    {
        private bool _pendingJoin;
        private string _pendingLobbyId = string.Empty;
        public override bool Available => false;

        public static void Initialize()
        {
            PhoneAPI.RegisterApp<AppSyncVideoPublicLobbies>("sync video lobbies");
        }

        public override void OnAppInit()
        {
            base.OnAppInit();
            CreateTitleBar("Video Lobbies", AppSyncVideo._iconSprite);
            ScrollView = PhoneScrollView.Create(this);
        }

        public override void OnAppEnable()
        {
            base.OnAppEnable();

            if (SyncVideoPlugin.LobbyManager.InLobby)
            {
                MyPhone.CloseCurrentApp();
                return;
            }

            BuildButtons();
        }

        public override void OnAppDisable()
        {
            base.OnAppDisable();
            _pendingJoin = false;
            _pendingLobbyId = string.Empty;
        }

        /*
        public override void OnPressLeft()
        {
            _pendingJoin = false;
            _pendingLobbyId = string.Empty;
            MyPhone.CloseCurrentApp();
        }
        */

        public override void OnPressLeft()
        {
            _pendingJoin = false;
            _pendingLobbyId = string.Empty;
            base.OnPressLeft();
        }

        public override void OnAppUpdate()
        {
            base.OnAppUpdate();

            if (_pendingJoin)
            {
                if (SyncVideoPlugin.LobbyManager.InLobby
                    && SyncVideoPlugin.LobbyManager.CurrentLobby != null
                    && string.Equals(SyncVideoPlugin.LobbyManager.CurrentLobby.LobbyId, _pendingLobbyId, System.StringComparison.Ordinal))
                {
                    _pendingJoin = false;
                    _pendingLobbyId = string.Empty;
                    MyPhone.OpenApp(typeof(AppSyncVideoLobby));
                    return;
                }
            }
        }

        private void RefreshLobbyButtons()
        {
            BuildButtons();
        }


        private void BuildButtons()
        {
            if (ScrollView == null)
                return;

            ScrollView.RemoveAllButtons();

            var refreshTop = PhoneUIUtility.CreateSimpleButton("Refresh");
            refreshTop.OnConfirm += RefreshLobbyButtons;
            ScrollView.AddButton(refreshTop);

            if (SyncVideoPlugin.LobbyManager.OfflineModeEnabled)
            {
                var offline = PhoneUIUtility.CreateSimpleButton("Offline Mode Enabled");
                ScrollView.AddButton(offline);
            }

            if (!SyncVideoPlugin.ScreenManager.HasAnyScreensInMap())
            {
                var none = PhoneUIUtility.CreateSimpleButton("No Screens!");
                ScrollView.AddButton(none);
                return;
            }

            var lobbies = SyncVideoPlugin.LobbyManager.Lobbies
                .OrderBy(x => x.LobbyName)
                .ToArray();

            if (lobbies.Length == 0)
            {
                var none = PhoneUIUtility.CreateSimpleButton("No Sync Video Lobbies Found");
                ScrollView.AddButton(none);
                return;
            }

            for (int i = 0; i < lobbies.Length; i++)
            {
                var lobby = lobbies[i];
                var button = PhoneUIUtility.CreateSimpleButton(lobby.LobbyName);
                button.OnConfirm += () =>
                {
                    _pendingJoin = true;
                    _pendingLobbyId = lobby.LobbyId ?? string.Empty;
                    SyncVideoPlugin.LobbyManager.JoinLobby(_pendingLobbyId);
                };
                ScrollView.AddButton(button);
            }
        }
    }
}
