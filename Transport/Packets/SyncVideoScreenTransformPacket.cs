using System.IO;

namespace SyncVideo.Transport.Packets
{
    public sealed class SyncVideoScreenTransformPacket : SyncVideoPacketBase
    {
        public string LobbyId = string.Empty;
        public float PosX;
        public float PosY;
        public float PosZ;
        public float ScaleX;
        public float ScaleY;
        public int Revision;

        public override string PacketId => SyncVideoPacketIds.ScreenTransform;

        public static SyncVideoScreenTransformPacket Deserialize(ushort senderPlayerId, byte[] data)
        {
            var packet = new SyncVideoScreenTransformPacket { SenderPlayerId = senderPlayerId };
            packet.PopulateFrom(data);
            return packet;
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            LobbyId  = SyncVideoPacketSerialization.ReadString(reader);
            PosX     = reader.ReadSingle();
            PosY     = reader.ReadSingle();
            PosZ     = reader.ReadSingle();
            ScaleX   = reader.ReadSingle();
            ScaleY   = reader.ReadSingle();
            Revision = reader.ReadInt32();
        }

        protected override void WritePayload(BinaryWriter writer)
        {
            SyncVideoPacketSerialization.WriteString(writer, LobbyId);
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
            writer.Write(ScaleX);
            writer.Write(ScaleY);
            writer.Write(Revision);
        }
    }
}
