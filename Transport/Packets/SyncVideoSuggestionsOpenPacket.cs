using System.IO;

namespace SyncVideo.Transport.Packets
{
    public sealed class SyncVideoSuggestionsOpenPacket : SyncVideoPacketBase
    {
        public string LobbyId = string.Empty;
        public bool IsOpen;

        public override string PacketId => SyncVideoPacketIds.SuggestionsOpen;

        public static SyncVideoSuggestionsOpenPacket Deserialize(ushort senderPlayerId, byte[] data)
        {
            var packet = new SyncVideoSuggestionsOpenPacket { SenderPlayerId = senderPlayerId };
            packet.PopulateFrom(data);
            return packet;
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            LobbyId = SyncVideoPacketSerialization.ReadString(reader);
            IsOpen  = reader.ReadBoolean();
        }

        protected override void WritePayload(BinaryWriter writer)
        {
            SyncVideoPacketSerialization.WriteString(writer, LobbyId);
            writer.Write(IsOpen);
        }
    }
}
