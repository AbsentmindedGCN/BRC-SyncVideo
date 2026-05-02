using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.IO;
using SyncVideo.Phone;
using SyncVideo.Runtime;
using SyncVideo.Transport;
using UnityEngine;
using System;

namespace SyncVideo
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency("CommonAPI", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("BombRushMP.Plugin", BepInDependency.DependencyFlags.HardDependency)]
    public sealed class SyncVideoPlugin : BaseUnityPlugin
    {
        public static SyncVideoPlugin Instance { get; private set; }
        public static SyncVideoConfig Settings { get; private set; }
        public static VideoLobbyManager LobbyManager { get; private set; }
        public static SyncVideoController SyncController { get; private set; }
        public static VideoScreenManager ScreenManager { get; private set; }
        public static SyncVideoTransport Transport { get; private set; }
        public static LobbyUiOverrideManager LobbyUiOverride { get; private set; }

        public static event Action<bool> LobbyStateChanged;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            var advancedConfigPath = Path.Combine(Paths.ConfigPath, "transrights.SyncVideoAdvanced.cfg");
            var advancedConfig = new ConfigFile(advancedConfigPath, true);
            Settings = new SyncVideoConfig(Config, advancedConfig, Info.Location);

            SyncVideoPacketRegistry.RegisterPackets(Logger);
            Transport = new SyncVideoTransport(Logger);
            LobbyManager = new VideoLobbyManager(Logger, Transport);
            LobbyManager.ActiveLobbyChanged += lobby => { LobbyStateChanged?.Invoke(lobby != null); };
            SyncController = new SyncVideoController(Logger, LobbyManager);
            ScreenManager = new VideoScreenManager(Logger, SyncController);
            LobbyUiOverride = new LobbyUiOverrideManager(Logger, LobbyManager);

            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            _harmony.PatchAll();

            AppSyncVideo.Initialize();
            AppSyncVideoLobby.Initialize();
            AppSyncVideoPublicLobbies.Initialize();
            AppSyncVideoScreenOptions.Initialize();
            AppSyncVideoLobbyKick.Initialize();
            AppSyncVideoSuggestions.Initialize();
            AppSyncVideoMkvSettings.Initialize();

            Logger.LogInfo($"{PluginInfo.PLUGIN_NAME} loaded.");
        }

        private void Update()
        {
            LobbyManager.Tick(Time.unscaledDeltaTime);
            SyncController.Tick(Time.unscaledDeltaTime);
            ScreenManager.Tick(Time.unscaledDeltaTime);
            HudManager.Tick();
        }

        private void LateUpdate()
        {
            LobbyUiOverride?.Tick(Time.unscaledDeltaTime);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            LobbyUiOverride?.Dispose();
            ScreenManager?.Dispose();
            SyncController?.Dispose();
            LobbyManager?.Dispose();
            Transport?.Dispose();
        }
    }
}
