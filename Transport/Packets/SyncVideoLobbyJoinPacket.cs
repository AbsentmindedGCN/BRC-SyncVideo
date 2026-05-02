using System.IO;

namespace SyncVideo.Transport.Packets
{
    public sealed class SyncVideoLobbyJoinPacket : SyncVideoPacketBase
    {
        public string LobbyId = string.Empty;
        public ushort PlayerId;

        public override string PacketId => SyncVideoPacketIds.LobbyJoin;

        public static SyncVideoLobbyJoinPacket Deserialize(ushort senderPlayerId, byte[] data)
        {
            var packet = new SyncVideoLobbyJoinPacket { SenderPlayerId = senderPlayerId };
            packet.PopulateFrom(data);
            return packet;
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            LobbyId  = SyncVideoPacketSerialization.ReadString(reader);
            PlayerId = reader.ReadUInt16();
        }

        protected override void WritePayload(BinaryWriter writer)
        {
            SyncVideoPacketSerialization.WriteString(writer, LobbyId);
            writer.Write(PlayerId);
        }
    }
}
