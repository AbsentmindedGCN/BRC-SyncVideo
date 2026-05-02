using Reptile;
using System;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SyncVideo.Phone
{
    public sealed class UrlPromptOverlay : MonoBehaviour
    {
        private static UrlPromptOverlay _instance;
        private static TMP_FontAsset _cachedGameFont;

        private TMP_InputField _input;
        private TextMeshProUGUI _confirmationText;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _helpText;
        private GameObject _panelGo;
        private GameObject _inputGo;
        private GameObject _submitButtonGo;
        private GameObject _cancelButtonGo;
        private Action<string> _onSubmit;
        private bool _fontApplied;
        private static float _suppressPhoneNavigationUntil = -1f;
        private static bool _showAsViewerSuggestion;
        private bool _cancelInputWasDown;

        public static bool IsVisible => _instance != null && _instance.gameObject.activeSelf;
        public static bool IsConfirmation { get; private set; }
        public static bool ShouldSuppressPhoneNavigation => UnityEngine.Time.unscaledTime <= _suppressPhoneNavigationUntil;

        private static void EnableMouseForOverlay()
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            try
            {
                var gameInput = Core.Instance.GameInput;
                if (gameInput != null)
                {
                    gameInput.EnableControllerMaps(BaseModule.IN_GAME_INPUT_MAPS);
                    gameInput.EnableControllerMaps(BaseModule.MENU_INPUT_MAPS);
                }
            }
            catch
            {
            }
        }

        private static void RestoreMouseAfterOverlay()
        {
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;

            try
            {
                var gameInput = Core.Instance.GameInput;
                if (gameInput != null)
                {
                    gameInput.DisableAllControllerMaps();
                    gameInput.EnableControllerMaps(BaseModule.IN_GAME_INPUT_MAPS);
                    gameInput.EnableControllerMaps(BaseModule.MENU_INPUT_MAPS);
                }
            }
            catch
            {
            }
        }

        private static bool IsCurrentUserHost()
        {
            try
            {
                return SyncVideoPlugin.LobbyManager != null && SyncVideoPlugin.LobbyManager.IsHost;
            }
            catch
            {
                return false;
            }
        }

        public static void Show(Action<string> onSubmit, bool showAsViewerSuggestion = false)
        {
            EnsureInstance();

            IsConfirmation = false;
            _showAsViewerSuggestion = showAsViewerSuggestion && !IsCurrentUserHost();
            _instance._onSubmit = onSubmit;
            _instance._cancelInputWasDown = true;
            _instance.gameObject.SetActive(true);
            EnableMouseForOverlay();
            _instance.ApplyPromptLayout();
            _instance.UpdateHelpText();
            _instance.ApplyGameFontToOverlay();
            _instance._input.text = GetClipboardText();
            _instance._input.caretPosition = _instance._input.text.Length;
            _instance._input.Select();
            _instance._input.ActivateInputField();
        }

        public static void ShowConfirmation(string message)
        {
            EnsureInstance();

            IsConfirmation = true;
            _showAsViewerSuggestion = false;
            _instance._onSubmit = null;
            _instance._cancelInputWasDown = true;
            _instance.gameObject.SetActive(true);
            RestoreMouseAfterOverlay();
            _instance.ApplyConfirmationLayout(message);
            _instance.ApplyGameFontToOverlay();

            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
        }

        public static void Hide()
        {
            if (_instance == null)
                return;

            IsConfirmation = false;
            _showAsViewerSuggestion = false;
            _suppressPhoneNavigationUntil = UnityEngine.Time.unscaledTime + 0.2f;
            _instance._cancelInputWasDown = false;
            RestoreMouseAfterOverlay();
            _instance.gameObject.SetActive(false);
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
        }

        private static void EnsureInstance()
        {
            if (_instance != null)
                return;

            var go = new GameObject("SyncVideoUrlPrompt");
            go.transform.SetParent(Core.Instance.UIManager.transform, false);
            _instance = go.AddComponent<UrlPromptOverlay>();
            _instance.Build();
        }

        private static string GetClipboardText()
        {
            try
            {
                var guiUtilityType = Type.GetType("UnityEngine.GUIUtility, UnityEngine.IMGUIModule")
                                     ?? Type.GetType("UnityEngine.GUIUtility, UnityEngine");

                if (guiUtilityType != null)
                {
                    var prop = guiUtilityType.GetProperty("systemCopyBuffer", BindingFlags.Public | BindingFlags.Static);
                    if (prop != null)
                        return prop.GetValue(null) as string ?? string.Empty;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static TMP_FontAsset TryGetGameFont()
        {
            if (_cachedGameFont != null)
                return _cachedGameFont;

            try
            {
                TextMeshProUGUI[] texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();

                for (int i = 0; i < texts.Length; i++)
                {
                    TextMeshProUGUI text = texts[i];
                    if (text == null || text.font == null)
                        continue;

                    if (text.name == "HeaderLabel")
                    {
                        _cachedGameFont = text.font;
                        return _cachedGameFont;
                    }
                }

                for (int i = 0; i < texts.Length; i++)
                {
                    TextMeshProUGUI text = texts[i];
                    if (text == null || text.font == null)
                        continue;

                    string path = GetHierarchyPath(text.transform);
                    if (path.IndexOf("UIRoot", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("Phone", StringComparison.OrdinalIgnoreCase) >= 0
                        || path.IndexOf("Overlay", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _cachedGameFont = text.font;
                        return _cachedGameFont;
                    }
                }

                for (int i = 0; i < texts.Length; i++)
                {
                    TextMeshProUGUI text = texts[i];
                    if (text != null && text.font != null)
                    {
                        _cachedGameFont = text.font;
                        return _cachedGameFont;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;

            string path = transform.name;
            while (transform.parent != null)
            {
                transform = transform.parent;
                path = transform.name + "/" + path;
            }

            return path;
        }

        private void ApplyGameFontToOverlay()
        {
            TMP_FontAsset font = TryGetGameFont();
            if (font == null)
                return;

            TMP_Text[] texts = GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text == null)
                    continue;

                if (text.font != font)
                    text.font = font;
            }

            _fontApplied = true;
        }

        private static bool TryGetAxisRaw(string axisName, out float value)
        {
            value = 0f;

            try
            {
                value = Input.GetAxisRaw(axisName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsCancelInputPressedThisFrame()
        {
            bool cancelDown = Input.GetKey(KeyCode.LeftArrow)
                              || Input.GetKey(KeyCode.Escape) // probs unnecessary since this pauses but meh
                              || Input.GetKey(KeyCode.JoystickButton1);

            bool pressedThisFrame = cancelDown && !_cancelInputWasDown;
            _cancelInputWasDown = cancelDown;
            return pressedThisFrame;
        }

        private void Update()
        {
            if (!gameObject.activeSelf)
                return;

            if (!_fontApplied)
                ApplyGameFontToOverlay();

            if (IsConfirmation)
            {
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;

                if (IsCancelInputPressedThisFrame())
                {
                    Hide();
                    return;
                }

                return;
            }

            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            if (IsCancelInputPressedThisFrame())
            {
                Cancel();
                return;
            }

            if (_input != null && !_input.isFocused)
                _input.ActivateInputField();

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                SubmitCurrentValue();
                return;
            }
        }

        private void SubmitCurrentValue()
        {
            string value = _input != null ? _input.text : string.Empty;
            RestoreMouseAfterOverlay();
            gameObject.SetActive(false);
            _onSubmit?.Invoke(value);

            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
        }

        private void Cancel()
        {
            if (_input != null)
                _input.DeactivateInputField();

            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);

            RestoreMouseAfterOverlay();
            Hide();
        }


        private void UpdateHelpText()
        {
            if (_helpText == null)
                return;

            if (_titleText != null)
            {
                _titleText.text = "Submit URL";
                _titleText.gameObject.SetActive(true);
            }

            _helpText.text = _showAsViewerSuggestion
                ? "Paste your YouTube link, then press Enter to suggest.\nPress left arrow key to cancel.\n\nOnly YouTube links are supported for suggestions!"
                : "Paste your video link, then press Enter to load.\nPress left arrow key to cancel.\n\nSupports YouTube, MP4, WebM, AVI, MOV, M4V, and most MKVs.";
        }

        private void ApplyPromptLayout()
        {
            if (_panelGo == null)
                return;

            RectTransform panelRt = _panelGo.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.2f, 0.32f);
            panelRt.anchorMax = new Vector2(0.8f, 0.68f);
            panelRt.offsetMin = Vector2.zero;
            panelRt.offsetMax = Vector2.zero;

            if (_titleText != null)
            {
                _titleText.text = "Submit URL";
                _titleText.gameObject.SetActive(true);
            }

            if (_helpText != null)
                _helpText.gameObject.SetActive(true);

            if (_inputGo != null)
                _inputGo.SetActive(true);

            if (_submitButtonGo != null)
                _submitButtonGo.SetActive(true);

            if (_cancelButtonGo != null)
                _cancelButtonGo.SetActive(true);

            if (_confirmationText != null)
                _confirmationText.gameObject.SetActive(false);
        }

        private void ApplyConfirmationLayout(string message)
        {
            if (_panelGo == null)
                return;

            RectTransform panelRt = _panelGo.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.34f, 0.42f);
            panelRt.anchorMax = new Vector2(0.66f, 0.58f);
            panelRt.offsetMin = Vector2.zero;
            panelRt.offsetMax = Vector2.zero;

            if (_titleText != null)
                _titleText.gameObject.SetActive(false);

            if (_helpText != null)
                _helpText.gameObject.SetActive(false);

            if (_inputGo != null)
                _inputGo.SetActive(false);

            if (_submitButtonGo != null)
                _submitButtonGo.SetActive(false);

            if (_cancelButtonGo != null)
                _cancelButtonGo.SetActive(false);

            if (_confirmationText != null)
            {
                _confirmationText.text = message;
                RectTransform confirmRt = _confirmationText.rectTransform;
                confirmRt.anchorMin = new Vector2(0.08f, 0.18f);
                confirmRt.anchorMax = new Vector2(0.92f, 0.82f);
                confirmRt.offsetMin = Vector2.zero;
                confirmRt.offsetMax = Vector2.zero;
                _confirmationText.gameObject.SetActive(true);
            }
        }

        private void Build()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            gameObject.AddComponent<GraphicRaycaster>();

            _panelGo = new GameObject("Panel");
            _panelGo.transform.SetParent(transform, false);
            var panel = _panelGo.AddComponent<Image>();
            panel.color = new Color(0f, 0f, 0f, 0.85f);

            var rt = panel.rectTransform;
            rt.anchorMin = new Vector2(0.2f, 0.32f);
            rt.anchorMax = new Vector2(0.8f, 0.68f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var titleGo = new GameObject("TitleText");
            titleGo.transform.SetParent(_panelGo.transform, false);
            _titleText = titleGo.AddComponent<TextMeshProUGUI>();
            _titleText.text = "Submit URL";
            _titleText.fontSize = 28;
            _titleText.color = Color.white;
            _titleText.alignment = TextAlignmentOptions.Center;
            _titleText.enableWordWrapping = false;
            var titleRt = _titleText.rectTransform;
            titleRt.anchorMin = new Vector2(0.07f, 0.84f);
            titleRt.anchorMax = new Vector2(0.93f, 0.94f);
            titleRt.offsetMin = Vector2.zero;
            titleRt.offsetMax = Vector2.zero;
            titleGo.SetActive(false);

            // Help text
            var helpGo = new GameObject("HelpText");
            helpGo.transform.SetParent(_panelGo.transform, false);
            _helpText = helpGo.AddComponent<TextMeshProUGUI>();
            _helpText.text = "Paste your video link, then press Enter to load.\nPress left arrow key to cancel.\n\nSupports YouTube, MP4, WebM, AVI, MOV, M4V, and most MKVs.";
            _helpText.fontSize = 22;
            _helpText.color = Color.white;
            _helpText.alignment = TextAlignmentOptions.Center;
            _helpText.enableWordWrapping = true;
            var helpRt = _helpText.rectTransform;
            helpRt.anchorMin = new Vector2(0.07f, 0.58f);
            helpRt.anchorMax = new Vector2(0.93f, 0.82f);
            helpRt.offsetMin = Vector2.zero;
            helpRt.offsetMax = Vector2.zero;

            _inputGo = new GameObject("Input");
            _inputGo.transform.SetParent(_panelGo.transform, false);
            var inputImage = _inputGo.AddComponent<Image>();
            inputImage.color = Color.white;
            _inputGo.AddComponent<RectMask2D>();
            var inputRt = inputImage.rectTransform;
            //inputRt.anchorMin = new Vector2(0.07f, 0.46f);
            //inputRt.anchorMax = new Vector2(0.93f, 0.56f);
            inputRt.anchorMin = new Vector2(0.07f, 0.40f);
            inputRt.anchorMax = new Vector2(0.93f, 0.50f);
            inputRt.offsetMin = Vector2.zero;
            inputRt.offsetMax = Vector2.zero;

            // Input field
            _input = _inputGo.AddComponent<TMP_InputField>();
            var textGo = new GameObject("Text");
            textGo.transform.SetParent(_inputGo.transform, false);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.fontSize = 20;
            text.color = Color.black;
            text.enableWordWrapping = false;
            var textRt = text.rectTransform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(12, 6);
            textRt.offsetMax = new Vector2(-12, -6);
            _input.textComponent = text;
            _input.lineType = TMP_InputField.LineType.SingleLine;
            _input.onSubmit.AddListener(delegate { SubmitCurrentValue(); });

            // Placeholder text
            var placeholderGo = new GameObject("Placeholder");
            placeholderGo.transform.SetParent(_inputGo.transform, false);
            var placeholder = placeholderGo.AddComponent<TextMeshProUGUI>();
            //placeholder.text = "https://awesomevideowebsite.com/my-video.mp4";
            placeholder.text = "https://www.youtube.com/watch?v=k9jNqmC211c";
            placeholder.fontSize = 20;
            placeholder.color = new Color(0f, 0f, 0f, 0.4f);
            placeholder.alignment = TextAlignmentOptions.MidlineLeft;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            var phRt = placeholder.rectTransform;
            phRt.anchorMin = Vector2.zero;
            phRt.anchorMax = Vector2.one;
            phRt.offsetMin = new Vector2(12, 2);
            phRt.offsetMax = new Vector2(-12, -2);
            _input.placeholder = placeholder;

            _submitButtonGo = MakeButton(_panelGo.transform, "Submit", new Vector2(0.24f, 0.14f), SubmitCurrentValue);
            _cancelButtonGo = MakeButton(_panelGo.transform, "Cancel", new Vector2(0.58f, 0.14f), Cancel);

            // Confirmation text
            var confirmGo = new GameObject("ConfirmationText");
            confirmGo.transform.SetParent(_panelGo.transform, false);
            _confirmationText = confirmGo.AddComponent<TextMeshProUGUI>();
            _confirmationText.fontSize = 28;
            _confirmationText.color = Color.white;
            _confirmationText.alignment = TextAlignmentOptions.Center;
            _confirmationText.enableWordWrapping = true;
            var confirmRt = _confirmationText.rectTransform;
            confirmRt.anchorMin = new Vector2(0.08f, 0.18f);
            confirmRt.anchorMax = new Vector2(0.92f, 0.82f);
            confirmRt.offsetMin = Vector2.zero;
            confirmRt.offsetMax = Vector2.zero;
            confirmGo.SetActive(false);

            UpdateHelpText();
            ApplyGameFontToOverlay();
            gameObject.SetActive(false);
        }

        private GameObject MakeButton(Transform parent, string label, Vector2 anchorMin, Action onClick)
        {
            var buttonGo = new GameObject(label + "Button");
            buttonGo.transform.SetParent(parent, false);

            var image = buttonGo.AddComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.95f);

            var button = buttonGo.AddComponent<Button>();
            button.onClick.AddListener(delegate { onClick(); });

            var rt = image.rectTransform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMin + new Vector2(0.18f, 0.16f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var textGo = new GameObject("Label");
            textGo.transform.SetParent(buttonGo.transform, false);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            text.text = label;
            text.color = Color.black;
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = 22;

            var textRt = text.rectTransform;
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.offsetMin = Vector2.zero;
            textRt.offsetMax = Vector2.zero;

            return buttonGo;
        }
    }
}
