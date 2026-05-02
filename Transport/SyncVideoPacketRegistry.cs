using BepInEx.Logging;

namespace SyncVideo.Transport
{
    // Moved to SyncVideoTransport, now uses ClientController.RegisterCustomPacketHandler API
    public static class SyncVideoPacketRegistry
    {
        public static void RegisterPackets(ManualLogSource logger) { }
    }
}
