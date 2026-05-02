using BombRushMP.Plugin;
using CommonAPI.Phone;
using TMPro;
using UnityEngine;

namespace SyncVideo.Phone
{
    public sealed class AppSyncVideoLobbyKick : CustomApp
    {
        public override bool Available => false;
        public static void Initialize()
        {
            PhoneAPI.RegisterApp<AppSyncVideoLobbyKick>("sync video kick viewers");
        }

        public override void OnAppInit()
        {
            base.OnAppInit();
            CreateTitleBar("Kick Viewers", AppSyncVideo._iconSprite);
            ScrollView = PhoneScrollView.Create(this);
        }

        public override void OnAppEnable()
        {
            base.OnAppEnable();
            BuildButtons();
        }

        public override void OnAppUpdate()
        {
            base.OnAppUpdate();
            if (!SyncVideoPlugin.LobbyManager.InLobby || !SyncVideoPlugin.LobbyManager.IsHost)
            {
                MyPhone.OpenApp(typeof(AppSyncVideoLobby));
            }
        }

        private void BuildButtons()
        {
            if (ScrollView == null)
                return;

            ScrollView.RemoveAllButtons();

            var lobby = SyncVideoPlugin.LobbyManager.CurrentLobby;
            if (lobby == null || !SyncVideoPlugin.LobbyManager.IsHost)
            {
                var none = PhoneUIUtility.CreateSimpleButton("No Viewers");
                ScrollView.AddButton(none);
                return;
            }

            ushort localId = SyncVideoPlugin.Transport != null ? SyncVideoPlugin.Transport.LocalPlayerId : (ushort)0;
            bool addedAny = false;

            foreach (var memberId in lobby.Members)
            {
                if (memberId == 0 || memberId == localId || memberId == lobby.HostId)
                    continue;

                string label = GetPlayerName(memberId);
                var kickButton = PhoneUIUtility.CreateSimpleButton(label);
                kickButton.OnConfirm += () =>
                {
                    if (ClientController.Instance != null && ClientController.Instance.ClientLobbyManager != null)
                        ClientController.Instance.ClientLobbyManager.KickPlayer(memberId);

                    BuildButtons();
                };
                ScrollView.AddButton(kickButton);
                addedAny = true;
            }

            if (!addedAny)
            {
                var none = PhoneUIUtility.CreateSimpleButton("No Viewers to Kick");
                ScrollView.AddButton(none);
            }

            /*
            var back = PhoneUIUtility.CreateSimpleButton("Back");
            back.OnConfirm += () => MyPhone.OpenApp(typeof(AppSyncVideoLobby));
            ScrollView.AddButton(back);
            */
        }

        private static string GetPlayerName(ushort playerId)
        {
            try
            {
                var sanitized = SyncVideoPlugin.LobbyManager.GetPlayerDisplayName(playerId);
                if (!string.IsNullOrWhiteSpace(sanitized))
                    return sanitized;
            }
            catch
            {
            }

            return "Player " + playerId;
        }
    }
}
