using BombRushMP.Plugin;
using HarmonyLib;
using TMPro;

namespace SyncVideo.Patches
{
    [HarmonyPatch(typeof(NotificationUI), "SetNotification")]
    internal static class NotificationUiPatch
    {
        static void Postfix(NotificationUI __instance)
        {
            var gamemodeLabel = Traverse.Create(__instance).Field("_gamemodeLabel").GetValue<TextMeshProUGUI>();
            if (gamemodeLabel == null)
                return;

            var current = gamemodeLabel.text;
            var updated = current
                .Replace("Pro Skater Score Battle", "Sync Video Watch Party")
                .Replace("Pro Skater Battle", "Sync Video Watch Party");

            if (!string.Equals(current, updated, System.StringComparison.Ordinal))
                gamemodeLabel.text = updated;
        }
    }
}
