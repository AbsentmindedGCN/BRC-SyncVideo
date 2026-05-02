// Removed — suggestion and time-sync data is now transmitted via dedicated SyncVideo custom
// packets (SyncVideoSuggestionPacket, SyncVideoStateRequestPacket) using BombRushMP's
// ClientController.RegisterCustomPacketHandler API. The ClientState embedding approach
// is no longer used.
namespace SyncVideo.Patches { }
