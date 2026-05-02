using BombRushMP.Plugin;
using HarmonyLib;

namespace SyncVideo.Patches
{
    [HarmonyPatch(typeof(ClientLobbyManager), "GetLobbyName")]
    internal static class ClientLobbyNamePatch
    {
        static void Postfix(uint lobbyId, ref string __result)
        {
            var lobbyManager = SyncVideoPlugin.LobbyManager;
            if (lobbyManager == null)
                return;

            foreach (var lobby in lobbyManager.Lobbies)
            {
                if (lobby == null)
                    continue;

                if (uint.TryParse(lobby.LobbyId,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var nativeId)
                    && nativeId == lobbyId)
                {
                    __result = "Sync Video Watch Party";
                    return;
                }
            }
        }
    }
}
