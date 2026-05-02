using BepInEx.Logging;
using BombRushMP.Plugin;
using TMPro;
using UnityEngine;

namespace SyncVideo.Runtime
{
    public sealed class LobbyUiOverrideManager : System.IDisposable
    {
        private readonly ManualLogSource _logger;
        private readonly VideoLobbyManager _lobbyManager;

        private LobbyUI _cachedLobbyUi;
        private Canvas _cachedCanvas;
        private CanvasGroup _cachedCanvasGroup;
        private TextMeshProUGUI _cachedLobbyName;
        private GameObject _cachedLobbySettings;
        private GameObject _cachedGameplayUi;

        // Original states cache
        private string _originalLobbyName;
        private bool _originalLobbySettingsActive;
        private bool _originalGameplayUiActive;
        private float _inviteRewriteTimer;
        private float _publicLobbyFilterTimer;

        private TextMeshProUGUI[] _cachedTexts;
        private float _textsRefreshTimer;
        private const float TextsRefreshInterval = 3.0f;
        private bool _isPublicLobbyAppOpen;
        private float _appOpenCheckTimer;
        private const float AppOpenCheckInterval = 0.5f;
        private bool? _lastHideAll;

        // Cap Tick to 30fps
        private float _tickAccumulator;
        private const float TickInterval = 1f / 30f;

        // Reusable buffer
        private readonly System.Collections.Generic.HashSet<string> _lobbyNamesBuffer = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

        public LobbyUiOverrideManager(ManualLogSource logger, VideoLobbyManager lobbyManager)
        {
            _logger = logger;
            _lobbyManager = lobbyManager;
        }

        public void Dispose()
        {
        }

        public void Tick(float deltaTime)
        {
            _tickAccumulator += deltaTime;
            if (_tickAccumulator < TickInterval)
                return;

            float dt = _tickAccumulator;
            _tickAccumulator = 0f;

            try
            {
                ApplyOverride(dt);
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning("Failed to override All City Network lobby UI: " + ex.Message);
                ClearCache();
            }
        }

        private void ApplyOverride(float deltaTime)
        {
            bool inSyncLobby = _lobbyManager != null && _lobbyManager.InLobby;

            if (_isPublicLobbyAppOpen || !inSyncLobby)
            {
                _appOpenCheckTimer -= deltaTime;
                if (_appOpenCheckTimer <= 0f)
                {
                    _appOpenCheckTimer = AppOpenCheckInterval;
                    _isPublicLobbyAppOpen = IsBombRushMpPublicLobbyAppOpen();
                }
            }

            bool needsInviteRewrite = !inSyncLobby && (_lobbyManager?.Lobbies.Count ?? 0) > 0; // Don't keep searching if already in lobby
            bool needsTextWork = needsInviteRewrite || _isPublicLobbyAppOpen;

            if (needsTextWork)
            {
                _textsRefreshTimer -= deltaTime;
                if (_textsRefreshTimer <= 0f)
                {
                    _textsRefreshTimer = TextsRefreshInterval;
                    _cachedTexts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                }

                if (needsInviteRewrite)
                {
                    _inviteRewriteTimer -= deltaTime;
                    if (_inviteRewriteTimer <= 0f)
                    {
                        _inviteRewriteTimer = 0.75f;
                        RewriteInviteTexts(_cachedTexts);
                    }
                }

                if (_isPublicLobbyAppOpen)
                {
                    _publicLobbyFilterTimer -= deltaTime;
                    if (_publicLobbyFilterTimer <= 0f)
                    {
                        _publicLobbyFilterTimer = 1.5f;
                        HideSyncVideoLobbiesFromMultiplayerMenu(_cachedTexts);
                    }
                }
            }

            if (!inSyncLobby)
            {
                if (_cachedCanvas != null)
                {
                    RestoreOverriddenElements();
                    if (_lastHideAll != false)
                        SetCanvasHidden(false);
                    ClearCache();
                    _lastHideAll = null; // reset so next lobby entry re-applies
                }
                return;
            }

            if (!TryResolveReferences())
                return;

            bool leavePending = _lobbyManager.LeaveInProgress;
            bool hideAll = leavePending || SyncVideoPlugin.Settings.HideNativeLobbyUi.Value;

            // Keep these hidden so Sync Video does not show score battle rules
            if (_cachedLobbySettings != null && _cachedLobbySettings.activeSelf)
                _cachedLobbySettings.SetActive(false);

            if (_cachedGameplayUi != null && _cachedGameplayUi.activeSelf)
                _cachedGameplayUi.SetActive(false);

            if (_cachedLobbyName != null &&
                _cachedLobbyName.text != "Sync Video Lobby" &&
                _cachedLobbyName.text != "Sync Video Watch Party")
                _cachedLobbyName.text = "Sync Video Lobby";

            if (_lastHideAll != hideAll)
            {
                _lastHideAll = hideAll;
                SetCanvasHidden(hideAll);
            }
        }

        private bool TryResolveReferences()
        {
            var lobbyUi = LobbyUI.Instance;
            if (lobbyUi == null)
            {
                ClearCache();
                return false;
            }

            if (_cachedLobbyUi == lobbyUi && _cachedCanvas != null)
                return true;

            _cachedLobbyUi = lobbyUi;
            var root = lobbyUi.transform;

            var canvasTransform = FindDeepChild(root, "Canvas");
            if (canvasTransform == null)
            {
                ClearCache();
                return false;
            }

            _cachedCanvas = canvasTransform.GetComponent<Canvas>();
            if (_cachedCanvas == null)
            {
                ClearCache();
                return false;
            }

            _cachedCanvasGroup = canvasTransform.GetComponent<CanvasGroup>();
            if (_cachedCanvasGroup == null)
                _cachedCanvasGroup = canvasTransform.gameObject.AddComponent<CanvasGroup>();

            _cachedLobbyName = FindDeepTMP(root, "Lobby Name");
            var lobbySettingsTransform = FindDeepChild(root, "Lobby Settings");
            _cachedLobbySettings = lobbySettingsTransform != null ? lobbySettingsTransform.gameObject : null;
            var gameplayUiTransform = FindDeepChild(root, "Gameplay UI");
            _cachedGameplayUi = gameplayUiTransform != null ? gameplayUiTransform.gameObject : null;
            // Save the original states so we can restore them when leaving
            _originalLobbyName = _cachedLobbyName != null ? _cachedLobbyName.text : null;
            _originalLobbySettingsActive = _cachedLobbySettings != null && _cachedLobbySettings.activeSelf;
            _originalGameplayUiActive = _cachedGameplayUi != null && _cachedGameplayUi.activeSelf;
            return true;
        }

        private void HideSyncVideoLobbiesFromMultiplayerMenu(TextMeshProUGUI[] texts)
        {
            // Use the cached app-open result
            if (_lobbyManager == null || !_isPublicLobbyAppOpen)
                return;

            _lobbyNamesBuffer.Clear();
            foreach (var lobby in _lobbyManager.Lobbies)
            {
                if (lobby != null && !string.IsNullOrWhiteSpace(lobby.LobbyName))
                    _lobbyNamesBuffer.Add(lobby.LobbyName);
            }

            if (_lobbyNamesBuffer.Count == 0)
                return;

            if (texts == null)
                return;

            foreach (var tmp in texts)
            {
                if (tmp == null)
                    continue;

                var current = tmp.text;
                if (string.IsNullOrWhiteSpace(current) || !_lobbyNamesBuffer.Contains(current.Trim()))
                    continue;

                HideContainingLobbyButton(tmp);
            }
        }

        private static bool IsBombRushMpPublicLobbyAppOpen()
        {
            try
            {
                var coreType = System.Type.GetType("Reptile.Core, Assembly-CSharp");
                if (coreType == null)
                    return false;

                var instanceProp = coreType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var core = instanceProp != null ? instanceProp.GetValue(null, null) : null;
                if (core == null)
                    return false;

                var uiManagerProp = coreType.GetProperty("UIManager", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var uiManager = uiManagerProp != null ? uiManagerProp.GetValue(core, null) : null;
                if (uiManager == null)
                    return false;

                var uiManagerType = uiManager.GetType();
                var myPhoneProp = uiManagerType.GetProperty("MyPhone", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var phone = myPhoneProp != null ? myPhoneProp.GetValue(uiManager, null) : null;
                if (phone == null)
                    return false;

                var phoneType = phone.GetType();
                var currentAppField = phoneType.GetField("m_CurrentApp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var currentApp = currentAppField != null ? currentAppField.GetValue(phone) : null;
                if (currentApp == null)
                {
                    var currentAppProp = phoneType.GetProperty("CurrentApp", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    currentApp = currentAppProp != null ? currentAppProp.GetValue(phone, null) : null;
                }

                return currentApp != null
                    && string.Equals(currentApp.GetType().Name, "AppMultiplayerPublicLobbies", System.StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        // Cache to prevent repeated processing
        private static readonly System.Collections.Generic.HashSet<GameObject> _hiddenObjects
            = new System.Collections.Generic.HashSet<GameObject>();

        private static void HideContainingLobbyButton(TextMeshProUGUI tmp)
        {
            if (tmp == null)
                return;

            Transform candidate = tmp.transform;
            const System.StringComparison cmp = System.StringComparison.OrdinalIgnoreCase;
            for (int i = 0; candidate != null && i < 8; i++, candidate = candidate.parent)
            {
                if (candidate == null)
                    continue;

                var go = candidate.gameObject;
                if (_hiddenObjects.Contains(go))
                    return;

                var components = candidate.GetComponents<Component>();
                for (int c = 0; c < components.Length; c++)
                {
                    var component = components[c];
                    if (component == null)
                        continue;

                    var typeName = component.GetType().Name;
                    if (typeName.IndexOf("PhoneScrollButton", cmp) >= 0 ||
                        typeName.Equals("Button", cmp))
                    {
                        if (go.activeSelf)
                        {
                            go.SetActive(false);
                            _hiddenObjects.Add(go);
                        }
                        return;
                    }
                }
            }
        }

        private static void RewriteInviteTexts(TextMeshProUGUI[] texts)
        {
            if (texts == null)
                return;

            foreach (var tmp in texts)
            {
                if (tmp == null)
                    continue;

                var current = tmp.text;
                if (string.IsNullOrEmpty(current) || current.IndexOf("Has invited you to their", System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var updated = current
                    .Replace("Pro Skater Score Battle", "Sync Video")
                    .Replace("Pro Skater Battle", "Sync Video");

                if (!string.Equals(current, updated, System.StringComparison.Ordinal))
                    tmp.text = updated;
            }
        }

        private void RestoreOverriddenElements()
        {
            if (_cachedLobbySettings != null)
                _cachedLobbySettings.SetActive(_originalLobbySettingsActive);

            if (_cachedGameplayUi != null)
                _cachedGameplayUi.SetActive(_originalGameplayUiActive);

            if (_cachedLobbyName != null && _originalLobbyName != null)
                _cachedLobbyName.text = _originalLobbyName;
        }

        private void SetCanvasHidden(bool hidden)
        {
            if (_cachedCanvas == null || _cachedCanvasGroup == null)
                return;

            _cachedCanvas.enabled = !hidden;
            _cachedCanvasGroup.alpha = hidden ? 0f : 1f;
            _cachedCanvasGroup.interactable = !hidden;
            _cachedCanvasGroup.blocksRaycasts = !hidden;
        }

        private void ClearCache()
        {
            _lastHideAll = null;
            _cachedLobbyUi = null;
            _cachedCanvas = null;
            _cachedCanvasGroup = null;
            _cachedLobbyName = null;
            _cachedLobbySettings = null;
            _cachedGameplayUi = null;
            _originalLobbyName = null;
            _originalLobbySettingsActive = false;
            _originalGameplayUiActive = false;
        }

        private static Transform FindDeepChild(Transform root, string name)
        {
            if (root == null)
                return null;

            if (root.name == name)
                return root;

            for (int i = 0; i < root.childCount; i++)
            {
                var found = FindDeepChild(root.GetChild(i), name);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static TextMeshProUGUI FindDeepTMP(Transform root, string name)
        {
            var target = FindDeepChild(root, name);
            return target != null ? target.GetComponent<TextMeshProUGUI>() : null;
        }
    }
}
