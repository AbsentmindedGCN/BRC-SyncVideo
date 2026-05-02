using CommonAPI.Phone;
using TMPro;
using UnityEngine;
using System.Reflection;

namespace SyncVideo.Phone
{
    public sealed class AppSyncVideoScreenOptions : CustomApp
    {
        //public override bool Available => SyncVideoPlugin.Settings != null && SyncVideoPlugin.Settings.ShowScreenPositionMenu.Value;
        public override bool Available => false;

        private PhoneButton _summaryButton;

        public static void Initialize()
        {
            PhoneAPI.RegisterApp<AppSyncVideoScreenOptions>("sync video screen");
        }

        public override void OnAppInit()
        {
            base.OnAppInit();
            CreateTitleBar("Screen Position", AppSyncVideo._iconSprite);
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

            if (_summaryButton != null)
                TrySetButtonLabel(_summaryButton, SyncVideoPlugin.ScreenManager.GetCurrentTransformSummary());
        }

        private void BuildButtons()
        {
            ScrollView.RemoveAllButtons();

            AddAdjustButton("X -", () => SyncVideoPlugin.ScreenManager.AdjustX(-0.01f));
            AddAdjustButton("X +", () => SyncVideoPlugin.ScreenManager.AdjustX(0.01f));
            AddAdjustButton("Y -", () => SyncVideoPlugin.ScreenManager.AdjustY(-0.01f));
            AddAdjustButton("Y +", () => SyncVideoPlugin.ScreenManager.AdjustY(0.01f));
            AddAdjustButton("Z -", () => SyncVideoPlugin.ScreenManager.AdjustZ(-0.005f));
            AddAdjustButton("Z +", () => SyncVideoPlugin.ScreenManager.AdjustZ(0.005f));
            AddAdjustButton("Size -", () => SyncVideoPlugin.ScreenManager.AdjustSize(-0.02f));
            AddAdjustButton("Size +", () => SyncVideoPlugin.ScreenManager.AdjustSize(0.02f));
            AddAdjustButton("Wider", () => SyncVideoPlugin.ScreenManager.AdjustAspect(0.02f, 0f));
            AddAdjustButton("Taller", () => SyncVideoPlugin.ScreenManager.AdjustAspect(0f, 0.02f));

            AddAdjustButton("Reset to Default", () => SyncVideoPlugin.ScreenManager.ResetScreenTransform());

            _summaryButton = PhoneUIUtility.CreateSimpleButton(SyncVideoPlugin.ScreenManager.GetCurrentTransformSummary());
            ScrollView.AddButton(_summaryButton);
            MakeButtonTextOneSizeSmaller(_summaryButton);
        }

        private void AddAdjustButton(string label, System.Action onConfirm)
        {
            var button = PhoneUIUtility.CreateSimpleButton(label);
            button.OnConfirm += () => onConfirm();
            ScrollView.AddButton(button);
        }

        private static void TrySetButtonLabel(PhoneButton button, string label)
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

                var tmp = (button as Component)?.GetComponentInChildren<TMP_Text>(true);
                if (tmp != null)
                    tmp.text = label;
            }
            catch
            {
            }
        }

        private static void MakeButtonTextOneSizeSmaller(PhoneButton button)
        {
            try
            {
                var tmp = (button as Component)?.GetComponentInChildren<TMP_Text>(true);
                if (tmp != null)
                    tmp.fontSize = Mathf.Max(1f, tmp.fontSize - 1f);
            }
            catch
            {
            }
        }
    }
}
