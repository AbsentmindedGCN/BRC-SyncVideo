using System.IO;

namespace SyncVideo.Transport.Packets
{
    public sealed class SyncVideoLobbyMembersPacket : SyncVideoPacketBase
    {
        public string LobbyId = string.Empty;
        public ushort[] MemberIds = new ushort[0];

        public override string PacketId => SyncVideoPacketIds.LobbyMembers;

        public static SyncVideoLobbyMembersPacket Deserialize(ushort senderPlayerId, byte[] data)
        {
            var packet = new SyncVideoLobbyMembersPacket { SenderPlayerId = senderPlayerId };
            packet.PopulateFrom(data);
            return packet;
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            LobbyId   = SyncVideoPacketSerialization.ReadString(reader);
            MemberIds = SyncVideoPacketSerialization.ReadUShortArray(reader);
        }

        protected override void WritePayload(BinaryWriter writer)
        {
            SyncVideoPacketSerialization.WriteString(writer, LobbyId);
            SyncVideoPacketSerialization.WriteUShortArray(writer, MemberIds);
        }
    }
}
