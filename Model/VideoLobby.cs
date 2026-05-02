using System;
using System.Collections.Generic;

namespace SyncVideo.Model
{
    public sealed class VideoLobby
    {
        public string LobbyId;
        public ushort HostId;
        public string LobbyName;
        public string CurrentUrl;
        public string CurrentVideoId;
        public bool IsPlaying;
        public double MediaTimeSeconds;
        public long HostUnixMilliseconds;
        public int Revision;
        public bool HasEnded;
        public bool IsOpen = true;
        public bool SuggestionsOpen;
        public int SelectedAudioTrack = 0;
        public int SelectedSubtitleTrack = -1;
        public readonly HashSet<ushort> Members = new HashSet<ushort>();
        public float LastSeenSeconds;
    }
}
