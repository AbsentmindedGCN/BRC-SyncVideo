using BepInEx.Configuration;
using Reptile;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace SyncVideo
{
    public sealed class SyncVideoConfig
    {
        public const string DefaultLobbyName = "Sync Video Lobby";
        public const bool LogBusMessages = false;

        private const int ConfigVersion = 5;
        private const string ConfigVersionMarker = "SyncVideoConfigVersion";

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
        public ConfigEntry<bool> UseUnityAudioSource { get; }
        public ConfigEntry<bool> EnableMkvFfmpegConversion { get; }
        public ConfigEntry<bool> MkvTranscodeToH264 { get; }
        public ConfigEntry<float> YouTubeVolumeScale { get; }
        public ConfigEntry<float> SubtitleFontSize { get; }
        public ConfigEntry<bool> StaticTVs { get; }

        public string PluginDirectory { get; }
        public string AdvancedConfigPath { get; }

        public SyncVideoConfig(ConfigFile config, ConfigFile advancedConfig, string pluginLocation)
        {
            bool configMigrated = Migrate(config);
            bool advancedConfigMigrated = Migrate(advancedConfig);

            PluginDirectory = Path.GetDirectoryName(pluginLocation) ?? string.Empty;
            AdvancedConfigPath = advancedConfig.ConfigFilePath;

            EnableOfflineMode = config.Bind("Offline Mode", "EnableOfflineMode", false, "Disable all online functionality when enabled. Create local lobbies for personal use.");

            HideNativeLobbyUi = config.Bind("ACN", "Hide Lobby UI", true, "Hide ACN's lobby UI by default.");
            SuppressAFK = config.Bind("ACN", "Suppress AFK", true, "Hide AFK animations for yourself and other players while in a Sync Video lobby.");

            HostAutoplay = config.Bind("Video", "Host Autoplay", true, "If hosting a video lobby, automatically start playback after loading your video URL.");
            VideoRenderResolution = config.Bind("Video", "Video Render Resolution", "854x480", "Render resolution used for MP4 video playback. Higher resolutions will cause lag. Options: 1920x1080, 1280x720, 960x540, 854x480, 640x360, 426x240.");
            YouTubeStreamResolution = config.Bind("Video", "YouTube Resolution", "1280x720", "Resolution used when streaming YouTube videos. Options: 1920x1080, 1280x720, 960x540, 854x480, 640x360, 426x240.");
            UseFFmpeg = config.Bind("Video", "Use FFmpeg", false, "Enable FFmpeg for higher quality YouTube video playback. Requires ffmpeg.exe to be placed in the plugin folder, next to SyncVideo.dll. When disabled, stream videos without downloading.");
            EnableMkvSupport = config.Bind("Video", "MKV Support", true, "Enable experimental MKV playback support. An MKV Settings menu for the host will appear in the app when an MKV file is loaded.");
            SubtitleFontSize = config.Bind("Video", "Subtitle Font Size", 34f, "Font size for MKV subtitles displayed on the TV screen. Default is 34.");

            DefaultVolume = config.Bind("Volume", "Default Volume", 90, "Starting volume level (0–100). Automatically rounds to increments of 10 in-game.");
            MuteMusicAndAmbient = config.Bind("Volume", "Mute Music and Ambient SFX", true, "Mute the game's music and ambient sounds while in a Sync Video lobby.");

            StaticTVs = config.Bind("World Props", "Static TVs", true, "Make TVs stay in place, so people can't kick them and disrupt your watch party.");
            ShowScreenPositionMenu = config.Bind("World Props", "Show Screen Position Menu", false, "Show the Screen Position menu in the phone app. Lets you move the screen around. Does not sync with viewers if host.");

            TvObjectName = advancedConfig.Bind("Debug", "TV Object Name", "TV", "Scene object name to bind video screens to.");
            ScreenMaterialTextureName = advancedConfig.Bind("Debug", "Screen Material Texture Name", "_MainTex", "Texture slot used when drawing video to a renderer.");
            AutoAttachToTVsOnStageLoad = advancedConfig.Bind("Debug", "Auto Attach To TVs On Load", true, "Automatically bind screens to matching TV objects when a map loads.");
            ShowRefreshScreensButton = advancedConfig.Bind("Debug", "Refresh Screens Button", false, "Show the Refresh Screens button in the phone app. Rebinds screens to objects.");
            YouTubeVolumeScale = advancedConfig.Bind("Debug", "YouTube Volume Scale", 1.0f, "Volume multiplier applied to YouTube videos (0.0-1.0). Makes YouTube videos not kill your ears.");
            UseUnityAudioSource = advancedConfig.Bind("Debug", "Unity AudioSource", false, "Use Unity AudioSource output for video audio instead of Direct output. Slightly higher latency, but can prevent video/audio offset.");

            EnableMkvFfmpegConversion = advancedConfig.Bind("Experimental", "MKV To MP4 Conversion", false, "When enabled, MKV files are converted into MP4 with FFmpeg. This re-muxes the MKV to fix container issues for MKVs using the H.264 codec.");
            MkvTranscodeToH264 = advancedConfig.Bind("Experimental", "Transcode MKV to H.264", false, "Transcodes the video to H.264 instead of using the MKV's codec. Required for H.265/HEVC, VP9, AV1, and 10-bit H.264 sources. Will be very slow.");

            HostBeaconInterval = advancedConfig.Bind("Networking", "Host Beacon Interval", 2.0f, "Seconds between lobby beacons.");
            HostStateResendInterval = advancedConfig.Bind("Networking", "Host State Resend Interval", 0.5f, "Seconds between host sync state broadcasts.");

            DriftToleranceSeconds = advancedConfig.Bind("Sync", "Drift Tolerance", 0.16f, "Amount of time difference between host and viewer, in seconds. If drift exceeds this, the viewer nudges toward host time.");
            HardSeekThresholdSeconds = advancedConfig.Bind("Sync", "Hard Seek Threshold", 0.60f, "Maximum amount of time difference between host and viewer, in seconds. If drift exceeds this, the viewer performs a hard seek.");

            // LogBusMessages = advancedConfig.Bind("Debug", "LogBusMessages", false, "Writes every hidden chat bus message to the log.");

            if (configMigrated)
                SaveCleanConfig(config);
            else
                config.Save();

            if (advancedConfigMigrated)
                SaveCleanConfig(advancedConfig);
            else
                advancedConfig.Save();
        }

        private bool Migrate(ConfigFile config)
        {
            int version = ReadConfigVersion(config.ConfigFilePath);
            if (version >= ConfigVersion)
                return false;

            bool saveOnConfigSet = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;

            try
            {
                config.Clear();
                ClearOrphanedEntries(config);

                try
                {
                    File.Delete(config.ConfigFilePath);
                }
                catch { }
            }
            finally
            {
                config.SaveOnConfigSet = saveOnConfigSet;
            }

            return true;
        }

        private int ReadConfigVersion(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return 0;

                string currentSection = string.Empty;
                foreach (var rawLine in File.ReadAllLines(path))
                {
                    string line = rawLine.Trim();

                    if (line.StartsWith("## " + ConfigVersionMarker, StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("# " + ConfigVersionMarker, StringComparison.OrdinalIgnoreCase))
                    {
                        int separator = line.IndexOf('=');
                        if (separator >= 0 && int.TryParse(line.Substring(separator + 1).Trim(), out int markerVersion))
                            return markerVersion;
                    }

                    if (line.Length > 2 && line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        currentSection = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }

                    if (string.Equals(currentSection, "Config", StringComparison.OrdinalIgnoreCase)
                     && line.StartsWith("Version", StringComparison.OrdinalIgnoreCase))
                    {
                        int separator = line.IndexOf('=');
                        if (separator >= 0 && int.TryParse(line.Substring(separator + 1).Trim(), out int oldVersion))
                            return Math.Min(oldVersion, ConfigVersion - 1);
                    }
                }
            }
            catch { }

            return 0;
        }

        private void SaveCleanConfig(ConfigFile config)
        {
            try
            {
                string directory = Path.GetDirectoryName(config.ConfigFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var sections = new List<string>();
                var entriesBySection = new Dictionary<string, List<ConfigEntryBase>>(StringComparer.OrdinalIgnoreCase);

                foreach (var definition in config.Keys)
                {
                    if (definition == null)
                        continue;

                    ConfigEntryBase entry = null;
                    try { entry = config[definition]; } catch { }
                    if (entry == null)
                        continue;

                    string section = definition.Section ?? string.Empty;
                    if (!entriesBySection.TryGetValue(section, out var entries))
                    {
                        entries = new List<ConfigEntryBase>();
                        entriesBySection[section] = entries;
                        sections.Add(section);
                    }

                    entries.Add(entry);
                }

                using (var writer = new StreamWriter(config.ConfigFilePath, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine("## " + ConfigVersionMarker + " = " + ConfigVersion.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine();

                    foreach (var section in sections)
                    {
                        writer.WriteLine("[" + section + "]");

                        foreach (var entry in entriesBySection[section])
                        {
                            string description = entry.Description?.Description;
                            if (!string.IsNullOrEmpty(description))
                            {
                                foreach (var descriptionLine in description.Replace("\r\n", "\n").Split('\n'))
                                    writer.WriteLine("## " + descriptionLine);
                            }

                            writer.WriteLine(entry.Definition.Key + " = " + GetConfigValueString(entry));
                            writer.WriteLine();
                        }
                    }
                }
            }
            catch
            {
                config.Save();
            }
        }

        private string GetConfigValueString(ConfigEntryBase entry)
        {
            try
            {
                if (entry.BoxedValue == null)
                    return string.Empty;

                if (entry.BoxedValue is IFormattable formattable)
                    return formattable.ToString(null, CultureInfo.InvariantCulture);

                return entry.BoxedValue.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private void ClearOrphanedEntries(ConfigFile config)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                foreach (var property in config.GetType().GetProperties(flags))
                {
                    if (property.Name.IndexOf("orphan", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    try
                    {
                        var value = property.GetValue(config, null);
                        value?.GetType().GetMethod("Clear", flags)?.Invoke(value, null);
                    }
                    catch { }
                }

                foreach (var field in config.GetType().GetFields(flags))
                {
                    if (field.Name.IndexOf("orphan", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    try
                    {
                        var value = field.GetValue(config);
                        value?.GetType().GetMethod("Clear", flags)?.Invoke(value, null);
                    }
                    catch { }
                }
            }
            catch { }
        }


    }
}