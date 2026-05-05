using BepInEx.Logging;
using CommonAPI;
using Reptile;
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace SyncVideo.Runtime
{
    public sealed class VideoScreenManager : IDisposable
    {
        private const string RootObjectName = "SyncVideoPlayerInstance";
        private const float TvStatusFontSize = 70f; // Was 120
        private const float SubtitleFontSize  = 34f;

        private const float DefaultPosX = 0f;
        private const float DefaultPosY = 0.330f;
        private const float DefaultPosZ = 0.220f;
        private const float DefaultScaleX = 0.520f;
        private const float DefaultScaleY = 0.293f;

        // Subtitle background padding
        private const float SubtitleBgPadH = 12f;
        private const float SubtitleBgPadV = 5f;

        private readonly ManualLogSource _logger;
        private readonly SyncVideoController _controller;
        private readonly List<ScreenInstance> _screens = new List<ScreenInstance>();

        private float _posX = DefaultPosX;
        private float _posY = DefaultPosY;
        private float _posZ = DefaultPosZ;
        private float _scaleX = DefaultScaleX;
        private float _scaleY = DefaultScaleY;

        private float _loadingAnimTimer;
        private int   _loadingAnimStep;

        private static TMP_FontAsset _cachedGameFont;

        // Status overlay optimization, rerun only when inputs change
        private string _lastStatusInput;
        private bool   _lastStatusPrepared;
        private bool   _lastStatusPlaying;
        private int    _lastStatusAnimStep = -1;
        private int    _lastStatusRevision = int.MinValue;
        private bool   _lastStatusInLobby;
        private bool   _lastStatusIsHost;
        private bool   _lastStatusIsViewerSyncing;
        private bool   _lastStatusShouldShowFfmpeg;
        private bool   _lastLobbyHasMediaTime;
        private string _cachedFinalStatus  = string.Empty;
        private bool   _cachedShowOverlay;

        // Tick-rate cap
        private float _tickAccumulator;
        private bool  _ffmpegMode;
        private float _ffmpegModeCheckTimer; // starts at 0 then checks on first tick
        private const float FfmpegModeCheckInterval = 10f;

        public struct ScreenTransformData
        {
            public float PosX, PosY, PosZ, ScaleX, ScaleY;
        }

        public VideoScreenManager(ManualLogSource logger, SyncVideoController controller)
        {
            _logger = logger;
            _controller = controller;
        }

        public void Dispose() { Clear(); }

        public void Tick(float deltaTime)
        {
            UpdateLoadingAnimation(deltaTime);

            _ffmpegModeCheckTimer -= deltaTime;
            if (_ffmpegModeCheckTimer <= 0f)
            {
                _ffmpegModeCheckTimer = FfmpegModeCheckInterval;
                _ffmpegMode = YouTube.IsFfmpegAvailable();
            }

            // Cap screen to video FPS
            _tickAccumulator += deltaTime;
            float minInterval = _ffmpegMode ? (1f / 60f) : (1f / 30f);
            if (_tickAccumulator < minInterval)
                return;
            _tickAccumulator -= minInterval;

            PruneDestroyedScreens();

            if (_screens.Count == 0)
                _controller.StopForMissingScreen();

            ApplyBackendTexture();
            ApplyStatusOverlay();
            ApplySubtitles();
        }

        // Remove screen instances that get destroyed to prevent null ref
        private void PruneDestroyedScreens()
        {
            for (int i = _screens.Count - 1; i >= 0; i--)
            {
                var s = _screens[i];
                if (s == null || s.Root == null)
                    _screens.RemoveAt(i);
            }
        }

        public bool HasAnyScreensInMap()
        {
            Junk[] junkObjects = UnityEngine.Object.FindObjectsOfType<Junk>(true);
            for (int i = 0; i < junkObjects.Length; i++)
            {
                var junk = junkObjects[i];
                if (junk != null && string.Equals(junk.name,
                        SyncVideoPlugin.Settings.TvObjectName.Value,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public string GetCurrentTransformSummary()
        {
            return $"<size=80%>X: {_posX:0.000} | Y: {_posY:0.000} | Z: {_posZ:0.000}\nW: {_scaleX:0.000} | H: {_scaleY:0.000}</size>";
        }

        public ScreenTransformData GetCurrentTransformData()
        {
            return new ScreenTransformData
            {
                PosX = _posX, PosY = _posY, PosZ = _posZ,
                ScaleX = _scaleX, ScaleY = _scaleY
            };
        }

        public void ApplyTransformData(ScreenTransformData data)
        {
            _posX   = data.PosX;
            _posY   = data.PosY;
            _posZ   = data.PosZ;
            _scaleX = Mathf.Max(0.1f, data.ScaleX);
            _scaleY = Mathf.Max(0.1f, data.ScaleY);
            Respawn();
        }

        public void Rebind() { SpawnPlayersForActiveScene(); }

        public void AdjustX(float delta) { _posX += delta; Respawn(); }
        public void AdjustY(float delta) { _posY += delta; Respawn(); }
        public void AdjustZ(float delta) { _posZ += delta; Respawn(); }

        public void AdjustSize(float delta)
        {
            _scaleX = Mathf.Max(0.1f, _scaleX + delta);
            _scaleY = _scaleX / (16f / 9f);
            Respawn();
        }

        public void AdjustAspect(float deltaX, float deltaY)
        {
            _scaleX = Mathf.Max(0.1f, _scaleX + deltaX);
            _scaleY = Mathf.Max(0.1f, _scaleY + deltaY);
            Respawn();
        }

        // Font helpers
        private static TMP_FontAsset TryGetGameFont()
        {
            if (_cachedGameFont != null) return _cachedGameFont;

            try
            {
                TextMeshProUGUI[] texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();

                for (int i = 0; i < texts.Length; i++)
                {
                    TextMeshProUGUI t = texts[i];
                    if (t == null || t.font == null) continue;
                    if (t.name == "HeaderLabel") { _cachedGameFont = t.font; return _cachedGameFont; }
                }

                for (int i = 0; i < texts.Length; i++)
                {
                    TextMeshProUGUI t = texts[i];
                    if (t == null || t.font == null) continue;
                    string path = GetHierarchyPath(t.transform);
                    if (path.IndexOf("UIRoot", StringComparison.OrdinalIgnoreCase) >= 0
                     || path.IndexOf("Phone", StringComparison.OrdinalIgnoreCase) >= 0
                     || path.IndexOf("Overlay", StringComparison.OrdinalIgnoreCase) >= 0)
                    { _cachedGameFont = t.font; return _cachedGameFont; }
                }

                for (int i = 0; i < texts.Length; i++)
                {
                    TextMeshProUGUI t = texts[i];
                    if (t != null && t.font != null) { _cachedGameFont = t.font; return _cachedGameFont; }
                }
            }
            catch { }

            return null;
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null) return string.Empty;
            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }
            return path;
        }

        private static void ApplyGameFontToStatusText(TMP_Text text)
        {
            if (text == null) return;
            TMP_FontAsset font = TryGetGameFont();
            if (font != null && text.font != font) text.font = font;
            text.enableAutoSizing = false;
            text.fontSize = TvStatusFontSize;
        }

        public void ResetScreenTransform()
        {
            _posX = DefaultPosX; _posY = DefaultPosY; _posZ = DefaultPosZ;
            _scaleX = DefaultScaleX; _scaleY = DefaultScaleY;
            Respawn();
        }

        private void Respawn() { SpawnPlayersForActiveScene(); }

        public void SpawnPlayersForActiveScene()
        {
            Clear();

            Junk[] junkObjects = UnityEngine.Object.FindObjectsOfType<Junk>(true);
            int count = 0;

            for (int i = 0; i < junkObjects.Length; i++)
            {
                var junk = junkObjects[i];
                if (junk == null) continue;
                if (!string.Equals(junk.name, SyncVideoPlugin.Settings.TvObjectName.Value,
                        StringComparison.OrdinalIgnoreCase)) continue;

                DestroyExistingSyncVideoChildren(junk.transform);
                MakeTvStatic(junk);
                var screen = CreateScreenForTv(junk.transform);
                if (screen != null) { _screens.Add(screen); count++; }

                if (count == 0)
                {
                    _logger.LogWarning("SyncVideo found no TV/screen objects in the current map.");
                    _controller.StopForMissingScreen();
                }

                _logger.LogInfo($"SyncVideo spawned {count} visual TV screen(s).");
                ApplyBackendTexture();
                ApplyStatusOverlay();
            }
        }

        public void OnVideoChanged()
        {
            ApplyBackendTexture(); ApplyStatusOverlay();
        }

        public void OnPlaybackStateChanged(bool playing, double timeSeconds)
        { 
            ApplyBackendTexture(); ApplyStatusOverlay();
        }

        private void MakeTvStatic(Junk junk)
        {
            // PropDisguiseController.FreezeProps() won't work, since it doesn't touch physics at all and only snaps props back to their start position
            if (junk == null || !SyncVideoPlugin.Settings.StaticTVs.Value)
                return;

            try
            {
                var anchor = junk.GetComponent<StaticTvAnchor>();
                if (anchor == null)
                    anchor = junk.gameObject.AddComponent<StaticTvAnchor>();

                anchor.Capture(junk.transform);
                anchor.FreezeRigidbodies();

                if (junk.enabled)
                    junk.enabled = false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"SyncVideo could not make TV static: {ex.Message}");
            }
        }

        private void DestroyExistingSyncVideoChildren(Transform tvTransform)
        {
            for (int i = tvTransform.childCount - 1; i >= 0; i--)
            {
                var child = tvTransform.GetChild(i);
                if (child != null && child.name == RootObjectName)
                    UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        private ScreenInstance CreateScreenForTv(Transform tvTransform)
        {
            var root = new GameObject(RootObjectName);
            root.transform.SetParent(tvTransform, false);
            root.transform.localPosition = new Vector3(_posX, _posY, _posZ);
            root.transform.localRotation = Quaternion.identity;
            root.transform.localScale    = Vector3.one;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "SyncVideoScreenQuad";
            quad.transform.SetParent(root.transform, false);
            quad.transform.localPosition = Vector3.zero;
            quad.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            quad.transform.localScale    = new Vector3(_scaleX, _scaleY, 1f);

            var collider = quad.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);

            var renderer = quad.GetComponent<MeshRenderer>();
            if (renderer == null) { UnityEngine.Object.Destroy(root); return null; }

            var textureShader = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default");
            renderer.material = new Material(textureShader);
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows    = false;
            TryMakeMaterialOneSided(renderer.material);

            // Status backdrop for legibility
            var overlayQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            overlayQuad.name = "SyncVideoStatusBackdrop";
            overlayQuad.transform.SetParent(quad.transform, false);
            overlayQuad.transform.localPosition = new Vector3(0f, 0f, -0.0012f);
            overlayQuad.transform.localRotation = Quaternion.identity;
            overlayQuad.transform.localScale    = new Vector3(1f, 1f, 1f);

            var overlayCollider = overlayQuad.GetComponent<Collider>();
            if (overlayCollider != null) UnityEngine.Object.Destroy(overlayCollider);

            var overlayRenderer = overlayQuad.GetComponent<MeshRenderer>();
            if (overlayRenderer == null) { UnityEngine.Object.Destroy(root); return null; }

            var colorShader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            overlayRenderer.material       = new Material(colorShader);
            overlayRenderer.material.color = new UnityEngine.Color(0f, 0f, 0f, 0.7f);
            overlayRenderer.shadowCastingMode = ShadowCastingMode.Off;
            overlayRenderer.receiveShadows    = false;
            TryMakeMaterialOneSided(overlayRenderer.material);
            overlayQuad.SetActive(false);

            // Status text canvas
            const float canvasWidth  = 1600f;
            const float canvasHeight = 900f;

            var canvasObject = new GameObject("SyncVideoStatusCanvas");
            canvasObject.transform.SetParent(quad.transform, false);
            canvasObject.transform.localPosition = new Vector3(0f, 0f, -0.0016f);
            canvasObject.transform.localRotation = Quaternion.identity;

            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace; canvas.overrideSorting = true;
            canvas.sortingOrder = 500; canvas.pixelPerfect = false;

            var canvasRect = canvas.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(canvasWidth, canvasHeight);

            float canvasScale  = Mathf.Min(_scaleX / canvasWidth, _scaleY / canvasHeight);
            float canvasScaleX = _scaleX > 0.0001f ? canvasScale / _scaleX : 1f;
            float canvasScaleY = _scaleY > 0.0001f ? canvasScale / _scaleY : 1f;
            canvasObject.transform.localScale = new Vector3(canvasScaleX, canvasScaleY, 1f);

            var textObject = new GameObject("SyncVideoStatusText");
            textObject.transform.SetParent(canvasObject.transform, false);

            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(-80f, 50f);
            textRect.offsetMax = new Vector2( 80f,-50f);

            var text = textObject.AddComponent<TextMeshProUGUI>();
            text.text = string.Empty; text.color = UnityEngine.Color.white;
            text.alignment = TextAlignmentOptions.Center; text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow; text.margin = Vector4.zero;
            text.raycastTarget = false;
            textObject.transform.localScale = Vector3.one;
            ApplyGameFontToStatusText(text);
            canvasObject.SetActive(false);

            // Subtitle canvas, sits at 20% of the screen at 80% width
            // Semi-trans background panels are child of the text object and resized on the fly with textInfo.lineInfo
            const float subCanvasWidth  = 1600f;
            const float subCanvasHeight = 900f;

            var subtitleCanvasObj = new GameObject("SyncVideoSubtitleCanvas");
            subtitleCanvasObj.transform.SetParent(quad.transform, false);
            subtitleCanvasObj.transform.localPosition = new Vector3(0f, 0f, -0.0020f);
            subtitleCanvasObj.transform.localRotation = Quaternion.identity;

            var subtitleCanvas = subtitleCanvasObj.AddComponent<Canvas>();
            subtitleCanvas.renderMode = RenderMode.WorldSpace; subtitleCanvas.overrideSorting = true;
            subtitleCanvas.sortingOrder = 510; subtitleCanvas.pixelPerfect = false;

            var subCanvasRect = subtitleCanvas.GetComponent<RectTransform>();
            subCanvasRect.sizeDelta = new Vector2(subCanvasWidth, subCanvasHeight);

            float subCanvasScale  = Mathf.Min(_scaleX / subCanvasWidth, _scaleY / subCanvasHeight);
            float subCanvasScaleX = _scaleX > 0.0001f ? subCanvasScale / _scaleX : 1f;
            float subCanvasScaleY = _scaleY > 0.0001f ? subCanvasScale / _scaleY : 1f;
            subtitleCanvasObj.transform.localScale = new Vector3(subCanvasScaleX, subCanvasScaleY, 1f);

            // Anchor values for the subtitle text rectangle
            var subAnchorMin = new Vector2(0.10f, 0.00f);
            var subAnchorMax = new Vector2(0.90f, 0.20f);
            var subOffsetMin = new Vector2(0f, 30f);
            var subOffsetMax = new Vector2(0f,  0f);

            // Precompute the subtitle text rect centre relative to the screen canvas
            float subTextCX = ((subAnchorMin.x + subAnchorMax.x) * 0.5f * subCanvasWidth + (subOffsetMin.x + subOffsetMax.x) * 0.5f) - subCanvasWidth  * 0.5f;
            float subTextCY = ((subAnchorMin.y + subAnchorMax.y) * 0.5f * subCanvasHeight + (subOffsetMin.y + subOffsetMax.y) * 0.5f) - subCanvasHeight * 0.5f;
            var subtitleTextCenter = new Vector2(subTextCX, subTextCY);

            // Per-line semi-trans background
            const int MaxBgLines = 3;
            var subBgPanels = new RectTransform[MaxBgLines];
            for (int li = 0; li < MaxBgLines; li++)
            {
                var bgGo = new GameObject("SyncVideoSubtitleBg" + li);
                bgGo.transform.SetParent(subtitleCanvasObj.transform, false);

                var bgRect = bgGo.AddComponent<RectTransform>();
                bgRect.anchorMin = new Vector2(0.5f, 0.5f);
                bgRect.anchorMax = new Vector2(0.5f, 0.5f);
                bgRect.anchoredPosition = Vector2.zero;
                bgRect.sizeDelta = Vector2.zero;

                var bgImg = bgGo.AddComponent<Image>();
                bgImg.color = new UnityEngine.Color(0f, 0f, 0f, 0.55f);
                bgImg.raycastTarget = false;

                bgGo.SetActive(false);
                subBgPanels[li] = bgRect;
            }

            // Subtitle text
            var subtitleTextObj = new GameObject("SyncVideoSubtitleText");
            subtitleTextObj.transform.SetParent(subtitleCanvasObj.transform, false);

            var subTextRect = subtitleTextObj.AddComponent<RectTransform>();
            subTextRect.anchorMin = subAnchorMin;
            subTextRect.anchorMax = subAnchorMax;
            subTextRect.offsetMin = subOffsetMin;
            subTextRect.offsetMax = subOffsetMax;

            var subtitleTmp = subtitleTextObj.AddComponent<TextMeshProUGUI>();
            subtitleTmp.text = string.Empty;
            subtitleTmp.color = UnityEngine.Color.white;
            subtitleTmp.alignment  = TextAlignmentOptions.Center;
            subtitleTmp.enableWordWrapping = true;
            subtitleTmp.overflowMode = TextOverflowModes.Overflow;
            subtitleTmp.margin = Vector4.zero;
            subtitleTmp.raycastTarget = false;
            subtitleTextObj.transform.localScale = Vector3.one;

            // Configurable subtitle font size
            float subtitleFontSizeValue = SyncVideoPlugin.Settings?.SubtitleFontSize?.Value ?? SubtitleFontSize;

            // Use default font instead of game font, looks better
            subtitleTmp.enableAutoSizing = false;
            subtitleTmp.fontSize = subtitleFontSizeValue;
            subtitleTmp.lineSpacing = 26f; // fix overlap

            // Add outline
            try
            {
                Material subMat = subtitleTmp.fontMaterial;
                subMat.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.3f);
                subMat.SetColor(ShaderUtilities.ID_OutlineColor, UnityEngine.Color.black);
            }
            catch { }

            subtitleCanvasObj.SetActive(false);

            // Audio loading label
            var audioLoadingObj = new GameObject("SyncVideoAudioLoadingText");
            audioLoadingObj.transform.SetParent(subtitleCanvasObj.transform, false);

            var audioLoadingRect = audioLoadingObj.AddComponent<RectTransform>();
            audioLoadingRect.anchorMin = new Vector2(0.10f, 0.21f);
            audioLoadingRect.anchorMax = new Vector2(0.90f, 0.29f);
            audioLoadingRect.offsetMin = Vector2.zero;
            audioLoadingRect.offsetMax = Vector2.zero;

            var audioLoadingTmp = audioLoadingObj.AddComponent<TextMeshProUGUI>();
            audioLoadingTmp.text = string.Empty;
            audioLoadingTmp.color = UnityEngine.Color.white;
            audioLoadingTmp.alignment  = TextAlignmentOptions.Center;
            audioLoadingTmp.enableWordWrapping = false;
            audioLoadingTmp.overflowMode = TextOverflowModes.Overflow;
            audioLoadingTmp.margin = Vector4.zero;
            audioLoadingTmp.raycastTarget = false;
            audioLoadingObj.transform.localScale = Vector3.one;
            audioLoadingTmp.enableAutoSizing = false;
            audioLoadingTmp.fontSize = subtitleFontSizeValue;
            audioLoadingTmp.lineSpacing = 26f;
            try
            {
                Material audioMat = audioLoadingTmp.fontMaterial;
                audioMat.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.3f);
                audioMat.SetColor(ShaderUtilities.ID_OutlineColor, UnityEngine.Color.black);
            }
            catch { }

            return new ScreenInstance(
                root, renderer, overlayQuad, canvasObject, text,
                subtitleTmp, subBgPanels, subtitleTextCenter, audioLoadingTmp);
        }

        // Material helpers
        private static void TryMakeMaterialOneSided(Material material)
        {
            if (material == null) return;
            TrySetInt(material, "_Cull",     (int)CullMode.Back);
            TrySetInt(material, "_CullMode", (int)CullMode.Back);
            TrySetInt(material, "_Culling",  (int)CullMode.Back);
        }

        private static void TrySetInt(Material material, string propertyName, int value)
        {
            if (material != null && material.HasProperty(propertyName))
                material.SetInt(propertyName, value);
        }

        // Screen status messages
        private void UpdateLoadingAnimation(float deltaTime)
        {
            var lobbyManager = SyncVideoPlugin.LobbyManager;
            var message = _controller.Backend.StatusOverlayText ?? string.Empty;

            bool isLoading =
                (string.IsNullOrWhiteSpace(message)
                    && !_controller.Backend.IsPrepared
                    && !_controller.Backend.IsPlaying)
                || (lobbyManager.InLobby && !lobbyManager.IsHost
                    && (string.Equals(message, "No Video Loaded!",  StringComparison.OrdinalIgnoreCase)
                     || string.Equals(message, "Video URL Error!", StringComparison.OrdinalIgnoreCase)));

            // Animate Syncing text during buffer
            bool isSyncing = lobbyManager.InLobby && !lobbyManager.IsHost
                && (lobbyManager.CurrentLobby != null
                    && !string.IsNullOrWhiteSpace(lobbyManager.CurrentLobby?.CurrentUrl)
                    && (!_controller.Backend.IsPrepared || !_controller.Backend.IsPlaying))
                || _controller.IsViewerCommandSyncing;

            if (!isLoading && !isSyncing) { _loadingAnimTimer = 0f; _loadingAnimStep = 0; return; }

            _loadingAnimTimer += deltaTime;
            if (_loadingAnimTimer >= 0.35f)
            { _loadingAnimTimer = 0f; _loadingAnimStep = (_loadingAnimStep + 1) % 4; }
        }

        private string GetAnimatedLoadingText()
        {
            switch (_loadingAnimStep)
            {
                case 1: return "Loading."; case 2: return "Loading..";
                case 3: return "Loading..."; default: return "Loading";
            }
        }

        private string GetAnimatedSyncingText()
        {
            if (_controller.ShouldShowFfmpegSyncingStatus())
            {
                switch (_loadingAnimStep)
                {
                    case 1: return "FFmpeg enabled!\nDownloading and Syncing.";
                    case 2: return "FFmpeg enabled!\nDownloading and Syncing..";
                    case 3: return "FFmpeg enabled!\nDownloading and Syncing...";
                    default: return "FFmpeg enabled!\nDownloading and Syncing";
                }
            }

            string seekHint = string.Empty;
            int seekDir = _controller.ViewerSeekDirection;
            if (seekDir > 0)
                seekHint = "\n\n<size=70%>Seeking forward!</size>";
            else if (seekDir < 0)
                seekHint = "\n\n<size=70%>Seeking backward!</size>";

            switch (_loadingAnimStep)
            {
                case 1: return "Syncing." + seekHint;
                case 2: return "Syncing.." + seekHint;
                case 3: return "Syncing..." + seekHint;
                default: return "Syncing" + seekHint;
            }
        }

        private void ApplyBackendTexture()
        {
            var texture = _controller.Backend.OutputTexture as Texture;
            if (texture == null) return;
            for (int i = 0; i < _screens.Count; i++)
            {
                var screen = _screens[i];
                if (screen == null || screen.Renderer == null) continue;
                if (screen.LastAppliedTexture == texture) continue;
                screen.Renderer.material.mainTexture = texture;
                screen.LastAppliedTexture = texture;
            }
        }

        private string GetDisplayStatusText(string backendMessage, bool isLoading)
        {
            var message          = backendMessage ?? string.Empty;
            var lobbyManager     = SyncVideoPlugin.LobbyManager;
            var currentLobby     = lobbyManager.CurrentLobby;

            var normalizedMessage = message.IndexOf('\r') >= 0
                ? message.Replace("\r\n", "\n").Replace("\r", "\n").Trim()
                : message.Trim();

            if (!lobbyManager.InLobby &&
                string.Equals(message, "No Video Loaded!", StringComparison.OrdinalIgnoreCase))
                return "No Video Loaded!\nOpen the Sync Video app!";

            if (lobbyManager.InLobby && !lobbyManager.IsHost && currentLobby != null)
            {
                bool hasUrl   = !string.IsNullOrWhiteSpace(currentLobby.CurrentUrl);
                bool isPaused = hasUrl && !currentLobby.IsPlaying && !currentLobby.HasEnded;
                bool isPreparing = hasUrl && !_controller.Backend.IsPrepared && !_controller.Backend.IsPlaying;

                if (currentLobby.HasEnded) return "Video Ended!";

                if (!hasUrl && (isLoading
                    || string.Equals(message, "No Video Loaded!",  StringComparison.OrdinalIgnoreCase)
                    || string.Equals(message, "Video URL Error!", StringComparison.OrdinalIgnoreCase)))
                {
                    switch (_loadingAnimStep)
                    {
                        case 1: return "Host is Setting Up Video.";
                        case 2: return "Host is Setting Up Video..";
                        case 3: return "Host is Setting Up Video...";
                        default: return "Host is Setting Up Video";
                    }
                }

                bool lobbyUrlIsMkv = !string.IsNullOrWhiteSpace(currentLobby.CurrentUrl)
                    && currentLobby.CurrentUrl.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
                if (lobbyUrlIsMkv && !SyncVideoPlugin.Settings.EnableMkvSupport.Value)
                    return "Host is playing an MKV File!\n<size=70%>Enable MKV Support in your Config</size>";

                if (hasUrl && string.Equals(message, "Video URL Error!", StringComparison.OrdinalIgnoreCase))
                    return "Video could not be loaded!\nTry rejoining the lobby!";

                if (_controller.IsViewerCommandSyncing || isPreparing)
                    return GetAnimatedSyncingText();

                if (isPaused)
                {
                    if (currentLobby.MediaTimeSeconds > 0.05d)
                        return "Video Paused!\nWaiting on Host!";

                    if (string.Equals(normalizedMessage, "Video Loaded!\nPress Play!", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(normalizedMessage, "Paused", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(normalizedMessage, "Video Paused!", StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(normalizedMessage))
                        return "Video Loaded!\nWaiting on Host!";
                }

                if (string.Equals(normalizedMessage, "Video Ended!", StringComparison.OrdinalIgnoreCase))
                    return "Video Ended!";
                if (string.Equals(normalizedMessage, "Video Loaded!\nPress Play!", StringComparison.OrdinalIgnoreCase))
                    return "Video Loaded!\nWaiting on Host!";
                if (string.Equals(normalizedMessage, "Paused",        StringComparison.OrdinalIgnoreCase)
                 || string.Equals(normalizedMessage, "Video Paused!", StringComparison.OrdinalIgnoreCase))
                    return "Video Paused!\nWaiting on Host!";
            }

            if (isLoading) return GetAnimatedLoadingText();
            return message;
        }

        private void ApplyStatusOverlay()
        {
            var message     = _controller.Backend.StatusOverlayText ?? string.Empty;
            bool isPrepared = _controller.Backend.IsPrepared;
            bool isPlaying  = _controller.Backend.IsPlaying;
            var lobbyMgr    = SyncVideoPlugin.LobbyManager;
            var lobby       = lobbyMgr.CurrentLobby;
            bool inLobby    = lobbyMgr.InLobby;
            bool isHost     = lobbyMgr.IsHost;
            bool isViewerSyncing  = _controller.IsViewerCommandSyncing;
            bool shouldShowFfmpeg = _controller.ShouldShowFfmpegSyncingStatus();
            int  lobbyRevision    = lobby != null ? lobby.Revision : int.MinValue;
            bool lobbyHasMediaTime = lobby != null && lobby.MediaTimeSeconds > 0.05d;

            // Only recompute when something that affects the display text has actually changed
            bool dirty =
                !string.Equals(_lastStatusInput, message, StringComparison.Ordinal)
                || _lastStatusPrepared         != isPrepared
                || _lastStatusPlaying          != isPlaying
                || _lastStatusAnimStep         != _loadingAnimStep
                || _lastStatusInLobby          != inLobby
                || _lastStatusIsHost           != isHost
                || _lastStatusIsViewerSyncing  != isViewerSyncing
                || _lastStatusShouldShowFfmpeg != shouldShowFfmpeg
                || _lastStatusRevision         != lobbyRevision
                || _lastLobbyHasMediaTime      != lobbyHasMediaTime;

            if (dirty)
            {
                _lastStatusInput            = message;
                _lastStatusPrepared         = isPrepared;
                _lastStatusPlaying          = isPlaying;
                _lastStatusAnimStep         = _loadingAnimStep;
                _lastStatusInLobby          = inLobby;
                _lastStatusIsHost           = isHost;
                _lastStatusIsViewerSyncing  = isViewerSyncing;
                _lastStatusShouldShowFfmpeg = shouldShowFfmpeg;
                _lastStatusRevision         = lobbyRevision;
                _lastLobbyHasMediaTime      = lobbyHasMediaTime;

                bool showText    = !string.IsNullOrWhiteSpace(message);
                bool showLoading = !showText && !isPrepared && !isPlaying;
                _cachedFinalStatus = GetDisplayStatusText(message, showLoading);
                _cachedShowOverlay = !string.IsNullOrWhiteSpace(_cachedFinalStatus);
            }

            string finalText   = _cachedFinalStatus;
            bool   showOverlay = _cachedShowOverlay;

            for (int i = 0; i < _screens.Count; i++)
            {
                var screen = _screens[i];
                if (screen == null) continue;

                if (screen.StatusText != null)
                {
                    // Apply font settings once per screen instance
                    if (!screen.FontSettingsApplied)
                    {
                        ApplyGameFontToStatusText(screen.StatusText);
                        if (_cachedGameFont != null)
                            screen.FontSettingsApplied = true;
                    }

                    if (!string.Equals(screen.StatusText.text, finalText, StringComparison.Ordinal))
                    {
                        screen.StatusText.text = finalText;
                        screen.StatusText.ForceMeshUpdate(true, true);
                    }
                    if (screen.StatusText.gameObject.activeSelf != showOverlay)
                        screen.StatusText.gameObject.SetActive(showOverlay);
                }

                if (screen.StatusBackdrop != null && screen.StatusBackdrop.activeSelf != showOverlay)
                    screen.StatusBackdrop.SetActive(showOverlay);
                if (screen.StatusCanvas != null && screen.StatusCanvas.activeSelf != showOverlay)
                    screen.StatusCanvas.SetActive(showOverlay);
            }
        }

        private void ApplySubtitles()
        {
            var lobbyManager = SyncVideoPlugin.LobbyManager;
            bool isViewer    = lobbyManager.InLobby && !lobbyManager.IsHost;
            bool videoPlaying  = _controller.Backend.IsPlaying;
            bool statusActive  = !string.IsNullOrWhiteSpace(_controller.Backend.StatusOverlayText);

            // Audio channel switch indicator (Viewer only)
            bool audioSwitching     = isViewer && _controller.IsMkvAudioSwitching();
            // Subtitle channel indicator (Host and Viewer)
            bool subtitleExtracting = _controller.IsMkvSubtitleExtracting();

            string subtitle = null;
            if (subtitleExtracting)
                subtitle = "Subtitles Loading...";
            else if (videoPlaying && !statusActive)
                subtitle = _controller.GetCurrentSubtitleText();

            bool showSubtitle    = !string.IsNullOrEmpty(subtitle);
            bool showAudioLoading = audioSwitching;

            for (int i = 0; i < _screens.Count; i++)
            {
                var screen = _screens[i];
                if (screen == null || screen.SubtitleText == null) continue;

                var subtitleCanvas = screen.SubtitleText.transform.parent?.gameObject;

                // The subtitle canvas must be active whenever subtitle text OR audio loading
                bool needCanvas = showSubtitle || showAudioLoading;

                if (!needCanvas)
                {
                    if (subtitleCanvas != null && subtitleCanvas.activeSelf)
                        subtitleCanvas.SetActive(false);
                    if (!string.IsNullOrEmpty(screen.SubtitleText.text))
                        screen.SubtitleText.text = string.Empty;
                    UpdateSubtitleBgPanels(screen, false);

                    // Hide audio loading text
                    if (screen.AudioLoadingText != null)
                    {
                        if (!string.IsNullOrEmpty(screen.AudioLoadingText.text))
                            screen.AudioLoadingText.text = string.Empty;
                        if (screen.AudioLoadingText.gameObject.activeSelf)
                            screen.AudioLoadingText.gameObject.SetActive(false);
                    }
                    continue;
                }

                // Activate canvas BEFORE ForceMeshUpdate
                if (subtitleCanvas != null && !subtitleCanvas.activeSelf)
                    subtitleCanvas.SetActive(true);

                bool subtitleTextChanged = !string.Equals(screen.SubtitleText.text, subtitle ?? string.Empty, StringComparison.Ordinal);
                if (subtitleTextChanged)
                {
                    screen.SubtitleText.text = subtitle ?? string.Empty;
                    screen.SubtitleText.ForceMeshUpdate(true, true);
                }

                bool wasSubtitleActive = screen.SubtitleText.gameObject.activeSelf;
                if (wasSubtitleActive != showSubtitle)
                    screen.SubtitleText.gameObject.SetActive(showSubtitle);
                if (subtitleTextChanged || wasSubtitleActive != showSubtitle)
                    UpdateSubtitleBgPanels(screen, showSubtitle);

                if (screen.AudioLoadingText != null)
                {
                    const string audioLoadingMsg = "Audio channel loading...";
                    if (!string.Equals(screen.AudioLoadingText.text, showAudioLoading ? audioLoadingMsg : string.Empty, StringComparison.Ordinal))
                        screen.AudioLoadingText.text = showAudioLoading ? audioLoadingMsg : string.Empty;
                    if (screen.AudioLoadingText.gameObject.activeSelf != showAudioLoading)
                        screen.AudioLoadingText.gameObject.SetActive(showAudioLoading);
                }
            }
        }

        // Fix subtitle background box to match the font exactly w/ some padding
        private void UpdateSubtitleBgPanels(ScreenInstance screen, bool show)
        {
            var panels = screen.SubtitleBgPanels;
            if (panels == null) return;

            if (!show || screen.SubtitleText == null)
            {
                for (int li = 0; li < panels.Length; li++)
                    if (panels[li] != null && panels[li].gameObject.activeSelf)
                        panels[li].gameObject.SetActive(false);
                return;
            }

            var textInfo = screen.SubtitleText.textInfo;
            int lineCount = textInfo != null ? textInfo.lineCount : 0;
            var textCenter = screen.SubtitleTextCenter;

            for (int li = 0; li < panels.Length; li++)
            {
                var bgRect = panels[li];
                if (bgRect == null) continue;

                if (li >= lineCount || textInfo == null)
                {
                    if (bgRect.gameObject.activeSelf)
                        bgRect.gameObject.SetActive(false);
                    continue;
                }

                var lineInfo = textInfo.lineInfo[li];

                // Skip lines that contain no characters
                if (lineInfo.characterCount == 0)
                {
                    if (bgRect.gameObject.activeSelf)
                        bgRect.gameObject.SetActive(false);
                    continue;
                }

                float lineW = lineInfo.lineExtents.max.x - lineInfo.lineExtents.min.x;
                float lineH = lineInfo.ascender - lineInfo.descender;

                if (lineW < 1f || lineH < 1f)
                {
                    if (bgRect.gameObject.activeSelf)
                        bgRect.gameObject.SetActive(false);
                    continue;
                }

                float cx = (lineInfo.lineExtents.min.x + lineInfo.lineExtents.max.x) * 0.5f;
                float cy = (lineInfo.ascender + lineInfo.descender) * 0.5f;

                bgRect.anchoredPosition = textCenter + new Vector2(cx, cy);
                bgRect.sizeDelta  = new Vector2(lineW + SubtitleBgPadH * 2f, lineH + SubtitleBgPadV * 2f);
                if (!bgRect.gameObject.activeSelf)
                    bgRect.gameObject.SetActive(true);
            }
        }

        private void Clear()
        {
            for (int i = 0; i < _screens.Count; i++)
            {
                var screen = _screens[i];
                if (screen != null) screen.Dispose();
            }
            _screens.Clear();
        }


        private sealed class StaticTvAnchor : MonoBehaviour
        {
            private Transform _target;
            private Vector3 _localPosition;
            private Quaternion _localRotation;
            private Vector3 _localScale;
            private Rigidbody[] _rigidbodies;

            public void Capture(Transform target)
            {
                _target = target;
                if (_target == null) return;

                _localPosition = _target.localPosition;
                _localRotation = _target.localRotation;
                _localScale = _target.localScale;
                _rigidbodies = _target.GetComponentsInChildren<Rigidbody>(true);
            }

            public void FreezeRigidbodies()
            {
                if (_rigidbodies == null) return;

                for (int i = 0; i < _rigidbodies.Length; i++)
                {
                    var body = _rigidbodies[i];
                    if (body == null) continue;

                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                    body.useGravity = false;
                    body.isKinematic = true;
                    body.constraints = RigidbodyConstraints.FreezeAll;
                }
            }

            private void LateUpdate()
            {
                if (_target == null) return;

                if (_target.localPosition != _localPosition)
                    _target.localPosition = _localPosition;
                if (_target.localRotation != _localRotation)
                    _target.localRotation = _localRotation;
                if (_target.localScale != _localScale)
                    _target.localScale = _localScale;
            }
        }

        // Screen Instance
        private sealed class ScreenInstance : IDisposable
        {
            public readonly GameObject Root;
            public readonly MeshRenderer Renderer;
            public readonly GameObject StatusBackdrop;
            public readonly GameObject StatusCanvas;
            public readonly TextMeshProUGUI StatusText;
            public readonly TextMeshProUGUI SubtitleText;
            public readonly RectTransform[] SubtitleBgPanels;
            public readonly Vector2 SubtitleTextCenter;
            public readonly TextMeshProUGUI AudioLoadingText;

            // Tracks last texture assigned to renderer so ApplyBackendTexture can skip the material property when it hasn't changed
            public Texture LastAppliedTexture;
            public bool FontSettingsApplied;

            public ScreenInstance(
                GameObject root,
                MeshRenderer renderer,
                GameObject statusBackdrop,
                GameObject statusCanvas,
                TextMeshProUGUI statusText,
                TextMeshProUGUI subtitleText,
                RectTransform[] subtitleBgPanels,
                Vector2 subtitleTextCenter,
                TextMeshProUGUI audioLoadingText)
            {
                Root = root;
                Renderer = renderer;
                StatusBackdrop = statusBackdrop;
                StatusCanvas = statusCanvas;
                StatusText = statusText;
                SubtitleText = subtitleText;
                SubtitleBgPanels = subtitleBgPanels;
                SubtitleTextCenter = subtitleTextCenter;
                AudioLoadingText = audioLoadingText;
            }

            public void Dispose()
            {
                if (Root != null) UnityEngine.Object.Destroy(Root);
            }
        }
    }
}
