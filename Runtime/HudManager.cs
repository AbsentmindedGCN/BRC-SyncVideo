using BombRushMP.Plugin;
using HarmonyLib;
using Reptile;
using UnityEngine;

namespace SyncVideo.Runtime
{
    public enum HudMode
    {
        Off = 0,
        UI = 1,
        UIChat = 2,
        UIAll = 3,
    }

    public static class HudManager
    {
        private static HudMode _currentMode = HudMode.Off;

        private static bool _savedShowChat;
        private static bool _savedShowNamePlates;
        private static bool _savedShowAFKEffects;
        private static bool _savedAfkMessages;
        private static bool _hasSavedSettings;
        private static bool _suppressAfkForLobby;

        private static float _afkTickTimer;
        private const float AfkTickInterval = 0.5f;

        private static CanvasGroup _gameplayCanvasGroup;

        public static HudMode CurrentMode => _currentMode;

        public static string GetLabel()
        {
            switch (_currentMode)
            {
                case HudMode.Off:     return "Hide HUD: <color=green>Off</color>";
                case HudMode.UI:      return "Hide HUD: <color=yellow>UI</color>";
                case HudMode.UIChat:  return "Hide HUD: <color=yellow>UI+Chat</color>";
                case HudMode.UIAll:   return "Hide HUD: <color=yellow>Everything</color>";
                default:              return "Hide HUD";
            }
        }

        public static void Cycle()
        {
            _currentMode = (HudMode)(((int)_currentMode + 1) % 4);
            Apply();
        }

        public static void Tick()
        {
            bool suppressAFK = _currentMode >= HudMode.UI || (_suppressAfkForLobby && SyncVideoPlugin.LobbyManager?.InLobby == true);
            if (!suppressAFK)
                return;

            // throttle suppress AFK it doesnt need to run so much
            _afkTickTimer += Time.unscaledDeltaTime;
            if (_afkTickTimer < AfkTickInterval)
                return;
            _afkTickTimer = 0f;

            try
            {
                PlayerComponent.GetLocal()?.StopAFK();
            }
            catch { }

            try
            {
                var mpSettings = MPSettings.Instance;
                if (mpSettings != null)
                {
                    if (mpSettings.ShowAFKEffects)
                        mpSettings.ShowAFKEffects = false;
                    if (mpSettings.AFKMessages)
                        mpSettings.AFKMessages = false;
                }
            }
            catch { }
        }

        public static void OnLobbyEnter()
        {
            var mpSettings = MPSettings.Instance;
            if (mpSettings != null && !_hasSavedSettings)
            {
                _savedShowChat       = mpSettings.ShowChat;
                _savedShowNamePlates = mpSettings.ShowNamePlates;
                _savedShowAFKEffects = mpSettings.ShowAFKEffects;
                _savedAfkMessages    = mpSettings.AFKMessages;
                _hasSavedSettings    = true;
            }

            _suppressAfkForLobby = SyncVideoPlugin.Settings?.SuppressAFK?.Value == true;

            Apply();
        }

        public static void Reset()
        {
            _currentMode = HudMode.Off;
            _suppressAfkForLobby = false;
            _afkTickTimer = 0f;
            ApplyGameplayUiHide(false);

            var mpSettings = MPSettings.Instance;
            if (mpSettings != null)
            {
                // Always restore
                mpSettings.ShowChat       = _hasSavedSettings ? _savedShowChat       : true;
                mpSettings.ShowNamePlates = _hasSavedSettings ? _savedShowNamePlates : true;
                mpSettings.ShowAFKEffects = _hasSavedSettings ? _savedShowAFKEffects : true;
                mpSettings.AFKMessages    = _hasSavedSettings ? _savedAfkMessages    : true;
            }
            _hasSavedSettings = false;
        }

        private static void Apply()
        {
            bool hideUi = _currentMode >= HudMode.UI;
            bool suppressAFK = _currentMode >= HudMode.UI || _suppressAfkForLobby;
            bool hideChat  = _currentMode >= HudMode.UIChat;
            bool hideNames = _currentMode >= HudMode.UIAll;

            var mpSettings = MPSettings.Instance;
            if (mpSettings != null && !_hasSavedSettings)
            {
                _savedShowChat       = mpSettings.ShowChat;
                _savedShowNamePlates = mpSettings.ShowNamePlates;
                _savedShowAFKEffects = mpSettings.ShowAFKEffects;
                _savedAfkMessages    = mpSettings.AFKMessages;
                _hasSavedSettings    = true;
            }

            ApplyGameplayUiHide(hideUi);

            if (mpSettings != null)
            {
                mpSettings.ShowChat       = hideChat    ? false : (_hasSavedSettings ? _savedShowChat       : true);
                mpSettings.ShowNamePlates = hideNames   ? false : (_hasSavedSettings ? _savedShowNamePlates : true);
                mpSettings.ShowAFKEffects = suppressAFK ? false : (_hasSavedSettings ? _savedShowAFKEffects : true);
                mpSettings.AFKMessages    = suppressAFK ? false : (_hasSavedSettings ? _savedAfkMessages    : true);
            }
        }

        private static void ApplyGameplayUiHide(bool hide)
        {
            try
            {
                var uiManager = Core.Instance?.UIManager;
                if (uiManager == null)
                    return;

                if (_gameplayCanvasGroup == null)
                {
                    var traverse     = Traverse.Create(uiManager);
                    var gameplayComp = traverse.Field("gameplay").GetValue() as Component;
                    if (gameplayComp == null)
                        return;

                    _gameplayCanvasGroup = gameplayComp.GetComponent<CanvasGroup>();
                    if (_gameplayCanvasGroup == null)
                        _gameplayCanvasGroup = gameplayComp.gameObject.AddComponent<CanvasGroup>();
                }

                _gameplayCanvasGroup.alpha          = hide ? 0f : 1f;
                _gameplayCanvasGroup.interactable   = !hide;
                _gameplayCanvasGroup.blocksRaycasts = !hide;
            }
            catch
            {
                _gameplayCanvasGroup = null;
            }
        }
    }
}
