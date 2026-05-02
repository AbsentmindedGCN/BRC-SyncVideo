using System.IO;

namespace SyncVideo.Transport.Packets
{
    public sealed class SyncVideoLobbyClosedPacket : SyncVideoPacketBase
    {
        public string LobbyId = string.Empty;

        public override string PacketId => SyncVideoPacketIds.LobbyClosed;

        public static SyncVideoLobbyClosedPacket Deserialize(ushort senderPlayerId, byte[] data)
        {
            var packet = new SyncVideoLobbyClosedPacket { SenderPlayerId = senderPlayerId };
            packet.PopulateFrom(data);
            return packet;
        }

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
