namespace SyncVideo.Transport.Packets
{
    // Packet ID strings for BombRushMP's custom packet API
    // Thx Duchess <3
    public static class SyncVideoPacketIds
    {
        public const string LobbyAdvertise  = "syncvideo.lobby_advertise";
        public const string LobbyJoin       = "syncvideo.lobby_join";
        public const string LobbyLeave      = "syncvideo.lobby_leave";
        public const string LobbyMembers    = "syncvideo.lobby_members";
        public const string State           = "syncvideo.state";
        public const string LobbyClosed     = "syncvideo.lobby_closed";
        public const string StateRequest    = "syncvideo.state_request";
        public const string ScreenTransform = "syncvideo.screen_transform";
        public const string Suggestion      = "syncvideo.suggestion";
        public const string SuggestionsOpen = "syncvideo.suggestions_open";
        public const string SuggestionAck   = "syncvideo.suggestion_ack";
        public const string Time            = "syncvideo.time";
    }
}
