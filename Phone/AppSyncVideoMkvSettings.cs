using CommonAPI.Phone;
using SyncVideo.Runtime;

namespace SyncVideo.Phone
{
    public sealed class AppSyncVideoMkvSettings : CustomApp
    {
        public override bool Available => false;

        private int _lastTrackCount     = -1;
        private int _lastSelectedTrack  = -1;
        private int _lastSubCount       = -2;
        private int _lastSelectedSub    = -2;
        private bool _lastAudioProbing  = false;
        private bool _lastSubProbing    = false;
        private bool _lastSubExtracting = false;

        public static void Initialize()
        {
            PhoneAPI.RegisterApp<AppSyncVideoMkvSettings>("sync video mkv settings");
        }

        public override void OnAppInit()
        {
            base.OnAppInit();
            CreateTitleBar("MKV Settings", AppSyncVideo._iconSprite);
            ScrollView = PhoneScrollView.Create(this);
        }

        public override void OnAppEnable()
        {
            base.OnAppEnable();
            _lastTrackCount     = -1;
            _lastSelectedTrack  = -1;
            _lastSubCount       = -2;
            _lastSelectedSub    = -2;
            _lastAudioProbing   = false;
            _lastSubProbing     = false;
            _lastSubExtracting  = false;
            BuildButtons();
        }

        public override void OnAppUpdate()
        {
            base.OnAppUpdate();

            if (!SyncVideoPlugin.LobbyManager.InLobby
                || !(SyncVideoPlugin.Settings?.EnableMkvSupport?.Value ?? false)
                || !SyncVideoPlugin.SyncController.IsCurrentMkv())
            {
                AppSyncVideoLobby.ReopenRequested = false;
                MyPhone.CloseCurrentApp(); // Force close app for viewers if host closes lobby
                return;
            }

            int trackCount     = SyncVideoPlugin.SyncController.GetMkvAudioTrackCount();
            int selectedTrack  = SyncVideoPlugin.SyncController.GetMkvSelectedAudioTrack();
            int subCount       = SyncVideoPlugin.SyncController.GetMkvSubtitleTrackCount();
            int selectedSub    = SyncVideoPlugin.SyncController.GetMkvSelectedSubtitleTrack();
            bool audioProbing  = SyncVideoPlugin.SyncController.IsMkvAudioProbing();
            bool subProbing    = SyncVideoPlugin.SyncController.IsMkvSubtitleProbing();
            bool subExtracting = SyncVideoPlugin.SyncController.IsMkvSubtitleExtracting();

            bool dirty =
                trackCount     != _lastTrackCount    ||
                selectedTrack  != _lastSelectedTrack ||
                audioProbing   != _lastAudioProbing ||
                subCount       != _lastSubCount      ||
                selectedSub    != _lastSelectedSub   ||
                subProbing     != _lastSubProbing ||
                subExtracting  != _lastSubExtracting;

            if (dirty)
                BuildButtons();
        }

        private void BuildButtons()
        {
            if (ScrollView == null)
                return;

            ScrollView.RemoveAllButtons();

            // Audio tracks
            int trackCount    = SyncVideoPlugin.SyncController.GetMkvAudioTrackCount();
            int selectedTrack = SyncVideoPlugin.SyncController.GetMkvSelectedAudioTrack();
            bool audioProbing = SyncVideoPlugin.SyncController.IsMkvAudioProbing();
            _lastTrackCount    = trackCount;
            _lastSelectedTrack = selectedTrack;
            _lastAudioProbing  = audioProbing;

            var audioHeader = PhoneUIUtility.CreateSimpleButton("<size=80%><color=grey>--- Audio Tracks ---</color></size>");
            ScrollView.AddButton(audioHeader);

            if (trackCount <= 0)
            {
                var none = PhoneUIUtility.CreateSimpleButton("No audio tracks found.");
                ScrollView.AddButton(none);
            }
            else
            {
                for (int i = 0; i < trackCount; i++)
                {
                    int trackIndex = i;
                    string label   = SyncVideoPlugin.SyncController.GetMkvAudioTrackLabel(trackIndex);
                    if (trackIndex == selectedTrack)
                        label = "<color=green>" + label + "</color>";

                    var btn = PhoneUIUtility.CreateSimpleButton(label);
                    btn.OnConfirm += () =>
                    {
                        SyncVideoPlugin.SyncController.SelectMkvAudioTrack(trackIndex);
                        BuildButtons();
                    };
                    ScrollView.AddButton(btn);
                }
            }

            // Subtitle tracks
            int subCount    = SyncVideoPlugin.SyncController.GetMkvSubtitleTrackCount();
            int selectedSub = SyncVideoPlugin.SyncController.GetMkvSelectedSubtitleTrack();
            bool probing    = SyncVideoPlugin.SyncController.IsMkvSubtitleProbing();
            bool extracting = SyncVideoPlugin.SyncController.IsMkvSubtitleExtracting();
            _lastSubCount       = subCount;
            _lastSelectedSub    = selectedSub;
            _lastSubProbing     = probing;
            _lastSubExtracting  = extracting;

            var subHeader = PhoneUIUtility.CreateSimpleButton("<size=80%><color=grey>--- Subtitle Tracks ---</color></size>");
            ScrollView.AddButton(subHeader);

            bool hasFfmpeg = Runtime.SubtitleManager.FindFfmpegPath() != null;

            if (!hasFfmpeg)
            {
                var noFfmpeg = PhoneUIUtility.CreateSimpleButton(
                    "Subtitles: <color=grey>Requires FFmpeg</color>");
                ScrollView.AddButton(noFfmpeg);
            }
            else if (probing)
            {
                var scanning = PhoneUIUtility.CreateSimpleButton(
                    "Subtitles: <color=yellow>Scanning...</color>");
                ScrollView.AddButton(scanning);
            }
            else if (extracting)
            {
                var loading = PhoneUIUtility.CreateSimpleButton(
                    "Subtitles: <color=yellow>Loading track...</color>");
                ScrollView.AddButton(loading);
            }
            else if (subCount <= 0)
            {
                var noSubs = PhoneUIUtility.CreateSimpleButton(
                    "Subtitles: <color=grey>None found</color>");
                ScrollView.AddButton(noSubs);
            }
            else
            {
                // Disable subtitle option
                {
                    string noneLabel = "None";
                    if (selectedSub < 0)
                        noneLabel = "<color=green>None</color>";

                    var noneBtn = PhoneUIUtility.CreateSimpleButton(noneLabel);
                    noneBtn.OnConfirm += () =>
                    {
                        SyncVideoPlugin.SyncController.SelectMkvSubtitleTrack(-1, BuildButtons);
                    };
                    ScrollView.AddButton(noneBtn);
                }

                for (int i = 0; i < subCount; i++)
                {
                    int subIndex = i;
                    string label = SyncVideoPlugin.SyncController.GetMkvSubtitleTrackLabel(subIndex);
                    if (subIndex == selectedSub)
                        label = "<color=green>" + label + "</color>";

                    var btn = PhoneUIUtility.CreateSimpleButton(label);
                    btn.OnConfirm += () =>
                    {
                        SyncVideoPlugin.SyncController.SelectMkvSubtitleTrack(subIndex, BuildButtons);
                    };
                    ScrollView.AddButton(btn);
                }
            }
        }
    }
}
