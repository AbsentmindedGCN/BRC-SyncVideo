using HarmonyLib;
using Reptile;

namespace SyncVideo.Patches
{
    [HarmonyPatch(typeof(ASceneSetupInstruction))]
    [HarmonyPatch("SetSceneActive")]
    internal static class StageVideoPatch
    {
        private static string _lastScene;

        private static void Postfix(string sceneToSetActive)
        {
            // Prevent SetSceneActive from firing multiple times
            if (string.Equals(_lastScene, sceneToSetActive, System.StringComparison.Ordinal))
                return;

            _lastScene = sceneToSetActive;
            SyncVideoPlugin.ScreenManager?.SpawnPlayersForActiveScene();
        }
    }
}