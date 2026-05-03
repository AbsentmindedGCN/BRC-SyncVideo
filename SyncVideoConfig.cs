using BepInEx.Configuration;
using Reptile;
using System;
using System.IO;
using System.Text;

namespace SyncVideo
{
    public sealed class SyncVideoConfig
    {
        public const string DefaultLobbyName = "Sync Video Lobby";
        public const bool LogBusMessages = false;

        public ConfigEntry<string> TvObjectName { get; }
        public ConfigEntry<string> ScreenMaterialTextureName { get; }
        public ConfigEntry<float> HostBeaconInterval { get; }
        public ConfigEntry<bool> EnableOfflineMode { get; }
        public ConfigEntry<float> HostStateResendInterval { get; }
        public ConfigEntry<float> DriftToleranceSeconds { get; }
        public ConfigEntry<float> HardSeekThresholdSeconds { get; }
        public ConfigEntry<bool> AutoAttachToTVsOnStageLoad { get; }
        public ConfigEntry<bool> ShowScreenPositionMenu { get; }
        public ConfigEntry<bool> ShowRefreshScreensButton { get; }
        public ConfigEntry<bool> HideNativeLobbyUi { get; }
        public ConfigEntry<bool> HostAutoplay { get; }
        public ConfigEntry<int> DefaultVolume { get; }
        public ConfigEntry<string> VideoRenderResolution { get; }
        public ConfigEntry<string> YouTubeStreamResolution { get; }
        public ConfigEntry<bool> UseFFmpeg { get; }
        public ConfigEntry<bool> EnableMkvSupport { get; }
        public ConfigEntry<bool> SuppressAFK { get; }
        public ConfigEntry<bool> MuteMusicAndAmbient { get; }
        public ConfigEntry<bool> EnableMkvFfmpegConversion { get; }
        public ConfigEntry<bool> MkvTranscodeToH264 { get; }
        public ConfigEntry<float> YouTubeVolumeScale { get; }
        public ConfigEntry<float> SubtitleFontSize { get; }

        public string PluginDirectory { get; }
        public string AdvancedConfigPath { get; }

        public SyncVideoConfig(ConfigFile config, ConfigFile advancedConfig, string pluginLocation)
        {
            PluginDirectory = Path.GetDirectoryName(pluginLocation) ?? string.Empty;
            AdvancedConfigPath = advancedConfig.ConfigFilePath;

            EnableOfflineMode = config.Bind("OfflineMode", "EnableOfflineMode", false, "Disable all online functionality when enabled. Create local lobbies for personal use.");
            
            HideNativeLobbyUi = config.Bind("ACN", "Hide Lobby UI", true, "Hide ACN's lobby UI by default.");
            SuppressAFK = config.Bind("ACN", "Suppress AFK", true, "Hide AFK animations for yourself and other players while in a Sync Video lobby.");

            HostAutoplay = config.Bind("Video", "Host Autoplay", true, "If hosting a video lobby, automatically start playback loading your video URL.");
            VideoRenderResolution = config.Bind("Video", "Video Render Resolution", "854x480", "Render resolution used for MP4 video playback. Higher resolutions will cause lag. Options: 1920x1080, 1280x720, 960x540, 854x480, 640x360, 426x240.");
            YouTubeStreamResolution = config.Bind("Video", "YouTube Resolution", "1280x720", "Resolution used when streaming YouTube videos. Options: 1920x1080, 1280x720, 960x540, 854x480, 640x360, 426x240.");
            UseFFmpeg = config.Bind("Video", "Use FFmpeg", false, "Enable FFmpeg for higher quality YouTube video playback. Requires ffmpeg.exe to be placed in the plugin folder, next to SyncVideo.dll. When disabled, stream videos without downloading.");
            EnableMkvSupport = config.Bind("Video", "MKV Support", true, "Enable experimental MKV playback support. An MKV Settings menu for the host will appear in the app when an MKV file is loaded.");
            SubtitleFontSize = config.Bind("Video", "Subtitle Font Size", 34f, "Font size for MKV subtitles displayed on the TV screen. Default is 34.");
            ShowScreenPositionMenu = config.Bind("Video", "Show Screen Position Menu", false, "Show the Screen Position menu in the phone app. Lets you move the screen around. Does not sync with viewers if host.");

            DefaultVolume = config.Bind("Volume", "Default Volume", 90, "Starting volume level (0–100). Automatically rounds to increments of 10 in-game.");
            MuteMusicAndAmbient = config.Bind("Volume", "Mute Music and Ambient SFX", true, "Mute the game's music and ambient sounds while in a Sync Video lobby.");

            TvObjectName = advancedConfig.Bind("Debug", "TV Object Name", "TV", "Scene object name to bind video screens to.");
            ScreenMaterialTextureName = advancedConfig.Bind("Debug", "Screen Material Texture Name", "_MainTex", "Texture slot used when drawing video to a renderer.");
            AutoAttachToTVsOnStageLoad = advancedConfig.Bind("Debug", "Auto Attach To TVs On Load", true, "Automatically bind screens to matching TV objects when a map loads.");
            ShowRefreshScreensButton = advancedConfig.Bind("Debug", "Refresh Screens Button", false, "Show the Refresh Screens button in the phone app. Rebinds screens to objects.");
            YouTubeVolumeScale = advancedConfig.Bind("Debug", "YouTube Volume Scale", 1.0f, "Volume multiplier applied to YouTube videos (0.0-1.0). Makes YouTube videos not kill your ears.");

            EnableMkvFfmpegConversion = advancedConfig.Bind("Experimental", "MKV To MP4 Conversion", false, "When enabled, MKV files are converted into MP4 with FFmpeg. This re-muxes the MKV to fix container issues for MKVs using the H.264 codec.");
            MkvTranscodeToH264 = advancedConfig.Bind("Experimental", "Transcode MKV to H.264", false, "Transcodes the video to H.264 instead of using the MKV's codec. Required for H.265/HEVC, VP9, AV1, and 10-bit H.264 sources. Will be very slow.");

            HostBeaconInterval = advancedConfig.Bind("Networking", "Host Beacon Interval", 2.0f, "Seconds between lobby beacons.");
            HostStateResendInterval = advancedConfig.Bind("Networking", "Host State Resend Interval", 0.5f, "Seconds between host sync state broadcasts.");

            DriftToleranceSeconds = advancedConfig.Bind("Sync", "Drift Tolerance", 0.16f, "Amount of time difference between host and viewer, in seconds. If drift exceeds this, the viewer nudges toward host time.");
            HardSeekThresholdSeconds = advancedConfig.Bind("Sync", "Hard Seek Threshold", 0.60f, "Maximum amount of time difference between host and viewer, in seconds. If drift exceeds this, the viewer performs a hard seek.");

            // LogBusMessages = advancedConfig.Bind("Debug", "LogBusMessages", false, "Writes every hidden chat bus message to the log.");

            advancedConfig.Save();
        }

    }
}
