using System.IO;

namespace SyncVideo.Transport.Packets
{
    public sealed class SyncVideoSuggestionPacket : SyncVideoPacketBase
    {
        public string LobbyId = string.Empty;
        public string Url = string.Empty;
        public string Title = string.Empty;
        public string PlayerName = string.Empty;

        public override string PacketId => SyncVideoPacketIds.Suggestion;

        public static SyncVideoSuggestionPacket Deserialize(ushort senderPlayerId, byte[] data)
        {
            var packet = new SyncVideoSuggestionPacket { SenderPlayerId = senderPlayerId };
            packet.PopulateFrom(data);
            return packet;
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            LobbyId    = SyncVideoPacketSerialization.ReadString(reader);
            Url        = SyncVideoPacketSerialization.ReadString(reader);
            Title      = SyncVideoPacketSerialization.ReadString(reader);
            PlayerName = reader.BaseStream.Position < reader.BaseStream.Length
                ? SyncVideoPacketSerialization.ReadString(reader)
                : string.Empty;
        }

        protected override void WritePayload(BinaryWriter writer)
        {
            SyncVideoPacketSerialization.WriteString(writer, LobbyId);
            SyncVideoPacketSerialization.WriteString(writer, Url);
            SyncVideoPacketSerialization.WriteString(writer, Title);
            SyncVideoPacketSerialization.WriteString(writer, PlayerName);
        }
    }
}
