using HarmonyLib;
using SyncVideo.Phone;
using System.Collections.Generic;
using System.Reflection;

namespace SyncVideo.Patches
{
    [HarmonyPatch]
    internal static class PhoneNavigationPatch
    {
        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> TargetMethods()
        {
            var phoneType = typeof(Reptile.Phone.Phone);

            // Disable phone controls while the URL overlay is visible
            var candidates = new[]
            {
                "PhoneMoveLeft",
                "OnPressLeft",
                "HandlePressedBackButton",
                "CloseCurrentApp",
            };

            foreach (var name in candidates)
            {
                var method = AccessTools.Method(phoneType, name);
                if (method != null)
                    yield return method;
            }
        }

        static bool Prefix()
        {
            if (UrlPromptOverlay.IsVisible)
            {
                UrlPromptOverlay.Hide();
                return false;
            }

            return !UrlPromptOverlay.ShouldSuppressPhoneNavigation;
        }
    }
}
