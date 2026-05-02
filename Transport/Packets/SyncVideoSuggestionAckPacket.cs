using System.IO;

namespace SyncVideo.Transport.Packets
{
    public sealed class SyncVideoSuggestionAckPacket : SyncVideoPacketBase
    {
        public ushort RecipientPlayerId;
        public string LobbyId = string.Empty;
        public string SuggestionKey = string.Empty;

        public override string PacketId => SyncVideoPacketIds.SuggestionAck;

        public static SyncVideoSuggestionAckPacket Deserialize(ushort senderPlayerId, byte[] data)
        {
            var packet = new SyncVideoSuggestionAckPacket { SenderPlayerId = senderPlayerId };
            packet.PopulateFrom(data);
            return packet;
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            RecipientPlayerId = reader.ReadUInt16();
            LobbyId           = SyncVideoPacketSerialization.ReadString(reader);
            SuggestionKey     = SyncVideoPacketSerialization.ReadString(reader);
        }

        protected override void WritePayload(BinaryWriter writer)
        {
            writer.Write(RecipientPlayerId);
            SyncVideoPacketSerialization.WriteString(writer, LobbyId);
            SyncVideoPacketSerialization.WriteString(writer, SuggestionKey);
        }
    }
}
