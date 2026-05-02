using System.IO;

namespace SyncVideo.Transport.Packets
{
    public sealed class SyncVideoLobbyAdvertisePacket : SyncVideoPacketBase
    {
        public string LobbyId = string.Empty;
        public ushort HostId;
        public string LobbyName = string.Empty;
        public int MemberCount;
        public string CurrentUrl = string.Empty;
        public string CurrentVideoId = string.Empty;
        public bool IsPlaying;
        public double MediaTimeSeconds;
        public long HostUnixMilliseconds;
        public int Revision;

        public override string PacketId => SyncVideoPacketIds.LobbyAdvertise;

        public static SyncVideoLobbyAdvertisePacket Deserialize(ushort senderPlayerId, byte[] data)
        {
            var packet = new SyncVideoLobbyAdvertisePacket { SenderPlayerId = senderPlayerId };
            packet.PopulateFrom(data);
            return packet;
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            LobbyId              = SyncVideoPacketSerialization.ReadString(reader);
            HostId               = reader.ReadUInt16();
            LobbyName            = SyncVideoPacketSerialization.ReadString(reader);
            MemberCount          = reader.ReadInt32();
            CurrentUrl           = SyncVideoPacketSerialization.ReadString(reader);
            CurrentVideoId       = SyncVideoPacketSerialization.ReadString(reader);
            IsPlaying            = reader.ReadBoolean();
            MediaTimeSeconds     = reader.ReadDouble();
            HostUnixMilliseconds = reader.ReadInt64();
            Revision             = reader.ReadInt32();
        }

        protected override void WritePayload(BinaryWriter writer)
        {
            SyncVideoPacketSerialization.WriteString(writer, LobbyId);
            writer.Write(HostId);
            SyncVideoPacketSerialization.WriteString(writer, LobbyName);
            writer.Write(MemberCount);
            SyncVideoPacketSerialization.WriteString(writer, CurrentUrl);
            SyncVideoPacketSerialization.WriteString(writer, CurrentVideoId);
            writer.Write(IsPlaying);
            writer.Write(MediaTimeSeconds);
            writer.Write(HostUnixMilliseconds);
            writer.Write(Revision);
        }
    }
}
