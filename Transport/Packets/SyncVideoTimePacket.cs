using System.IO;

namespace SyncVideo.Transport.Packets
{
    // Heartbeat sent periodically by the host to keep viewers in sync
    public sealed class SyncVideoTimePacket : SyncVideoPacketBase
    {
        public string LobbyId = string.Empty;
        public double MediaTimeSeconds;
        public bool IsPlaying;
        public long HostSentMilliseconds;

        public override string PacketId => SyncVideoPacketIds.Time;

        public static SyncVideoTimePacket Deserialize(ushort senderPlayerId, byte[] data)
        {
            var packet = new SyncVideoTimePacket { SenderPlayerId = senderPlayerId };
            packet.PopulateFrom(data);
            return packet;
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            LobbyId              = SyncVideoPacketSerialization.ReadString(reader);
            MediaTimeSeconds     = reader.ReadDouble();
            IsPlaying            = reader.ReadBoolean();
            HostSentMilliseconds = reader.ReadInt64();
        }

        protected override void WritePayload(BinaryWriter writer)
        {
            SyncVideoPacketSerialization.WriteString(writer, LobbyId);
            writer.Write(MediaTimeSeconds);
            writer.Write(IsPlaying);
            writer.Write(HostSentMilliseconds);
        }
    }
}
