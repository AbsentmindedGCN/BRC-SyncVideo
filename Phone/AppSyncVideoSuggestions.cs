using System;
using System.Linq;
using CommonAPI.Phone;
using SyncVideo.Runtime;
using UnityEngine;

namespace SyncVideo.Phone
{
    public sealed class AppSyncVideoSuggestions : CustomApp
    {
        public override bool Available => false;

        private int  _lastSuggestionCount = -1;
        private bool _lastSuggestionsOpen;
        private string _lastSuggestionSignature = string.Empty;

        public static void Initialize()
        {
            PhoneAPI.RegisterApp<AppSyncVideoSuggestions>("sync video suggestions");
        }

        public override void OnAppInit()
        {
            base.OnAppInit();
            CreateTitleBar("<size=85%>Suggestions</size>", AppSyncVideo._iconSprite);
            ScrollView = PhoneScrollView.Create(this);
        }

        public override void OnAppEnable()
        {
            base.OnAppEnable();
            CreateTitleBar(SyncVideoPlugin.LobbyManager.PlaylistModeEnabled ? "Playlist Queue" : "<size=85%>Suggestions</size>", AppSyncVideo._iconSprite);
            _lastSuggestionCount = -1;
            _lastSuggestionsOpen = SyncVideoPlugin.LobbyManager.SuggestionsOpen;
            _lastSuggestionSignature = string.Empty;

            SyncVideoPlugin.LobbyManager.RequestSuggestionScan();

            BuildButtons();
        }

        public override void OnAppUpdate()
        {
            base.OnAppUpdate();

            if (!SyncVideoPlugin.LobbyManager.InLobby || !SyncVideoPlugin.LobbyManager.IsHost)
            {
                MyPhone.OpenApp(typeof(AppSyncVideoLobby));
                return;
            }

            var suggestions = SyncVideoPlugin.LobbyManager.GetOrderedSuggestions();
            var count = suggestions.Count;
            var suggestionsOpen = SyncVideoPlugin.LobbyManager.SuggestionsOpen;
            var signature = BuildSuggestionSignature(suggestions);
            if (count != _lastSuggestionCount || suggestionsOpen != _lastSuggestionsOpen || signature != _lastSuggestionSignature)
                BuildButtons();
        }

        private static string BuildSuggestionSignature(System.Collections.Generic.IReadOnlyList<System.Collections.Generic.KeyValuePair<string, VideoLobbyManager.VideoSuggestion>> suggestions)
        {
            if (suggestions == null || suggestions.Count == 0)
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < suggestions.Count; i++)
            {
                var kvp = suggestions[i];
                sb.Append(kvp.Key).Append('|')
                  .Append(kvp.Value.Url ?? string.Empty).Append('|')
                  .Append(kvp.Value.Title ?? string.Empty).Append('|')
                  .Append(kvp.Value.ChannelName ?? string.Empty).Append(';');
            }
            return sb.ToString();
        }

        private void BuildButtons()
        {
            if (ScrollView == null)
                return;

            ScrollView.RemoveAllButtons();

            // Scan for suggestions
            /*
            var refresh = PhoneUIUtility.CreateSimpleButton("Refresh");
            refresh.OnConfirm += () =>
            {
                SyncVideoPlugin.LobbyManager.RequestSuggestionScan();
                BuildButtons();
            };
            ScrollView.AddButton(refresh);
            */

            if (!SyncVideoPlugin.LobbyManager.IsHost || SyncVideoPlugin.LobbyManager.CurrentLobby == null)
                return;

            var suggestions = SyncVideoPlugin.LobbyManager.GetOrderedSuggestions();
            _lastSuggestionCount = suggestions.Count;
            _lastSuggestionsOpen = SyncVideoPlugin.LobbyManager.SuggestionsOpen;
            _lastSuggestionSignature = BuildSuggestionSignature(suggestions);

            var clear = PhoneUIUtility.CreateSimpleButton("<color=red>Clear All</color>");
            clear.OnConfirm += () =>
            {
                SyncVideoPlugin.LobbyManager.ClearSuggestions();
                BuildButtons();
            };
            ScrollView.AddButton(clear);

            if (suggestions.Count == 0)
            {
                var none = PhoneUIUtility.CreateSimpleButton(
                    SyncVideoPlugin.LobbyManager.PlaylistModeEnabled
                        ? "No videos added yet."
                        : "No suggestions yet.");
                ScrollView.AddButton(none);
                return;
            }

            foreach (var kvp in suggestions)
            {
                var suggestionEntryKey = kvp.Key;
                var suggestion = kvp.Value;
                string label = suggestion.GetButtonLabel();
                var btn = PhoneUIUtility.CreateSimpleButton(label);
                btn.OnConfirm += () =>
                {
                    SyncVideoPlugin.SyncController.HostSetUrl(suggestion.Url);
                    SyncVideoPlugin.LobbyManager.RemoveSuggestion(suggestionEntryKey);
                    //MyPhone.OpenApp(typeof(AppSyncVideoLobby));
                    BuildButtons();
                };
                ScrollView.AddButton(btn);
            }
        }
    }
}
