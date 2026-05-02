using System.IO;

namespace SyncVideo.Transport.Packets
{
    // This is sent by a viewer to the host when joining a lobby to request the current state
    public sealed class SyncVideoStateRequestPacket : SyncVideoPacketBase
    {
        public string LobbyId = string.Empty;

        public override string PacketId => SyncVideoPacketIds.StateRequest;

        public static SyncVideoStateRequestPacket Deserialize(ushort senderPlayerId, byte[] data)
        {
            var packet = new SyncVideoStateRequestPacket { SenderPlayerId = senderPlayerId };
            packet.PopulateFrom(data);
            return packet;
        }

        // Broadcast a fresh SyncVideoStatePacket to the lobby
        protected override void ReadPayload(BinaryReader reader)
        {
            LobbyId = SyncVideoPacketSerialization.ReadString(reader);
        }

        protected override void WritePayload(BinaryWriter writer)
        {
            SyncVideoPacketSerialization.WriteString(writer, LobbyId);
        }
    }
}
