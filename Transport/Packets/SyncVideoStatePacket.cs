using System.IO;

namespace SyncVideo.Transport.Packets
{
    public sealed class SyncVideoStatePacket : SyncVideoPacketBase
    {
        public string LobbyId = string.Empty;
        public string Url = string.Empty;
        public string VideoId = string.Empty;
        public bool IsPlaying;
        public double MediaTimeSeconds;
        public long HostUnixMilliseconds;
        public int Revision;
        public int SeekRevision;
        public bool HasEnded;
        public bool IsOpen = true;
        public bool SuggestionsOpen;
        public int SelectedAudioTrack = 0;
        public int SelectedSubtitleTrack = -1;

        public override string PacketId => SyncVideoPacketIds.State;

        public static SyncVideoStatePacket Deserialize(ushort senderPlayerId, byte[] data)
        {
            var packet = new SyncVideoStatePacket { SenderPlayerId = senderPlayerId };
            packet.PopulateFrom(data);
            return packet;
        }

        protected override void ReadPayload(BinaryReader reader)
        {
            LobbyId              = SyncVideoPacketSerialization.ReadString(reader);
            Url                  = SyncVideoPacketSerialization.ReadString(reader);
            VideoId              = SyncVideoPacketSerialization.ReadString(reader);
            IsPlaying            = reader.ReadBoolean();
            MediaTimeSeconds     = reader.ReadDouble();
            HostUnixMilliseconds = reader.ReadInt64();
            Revision             = reader.ReadInt32();
            HasEnded             = reader.ReadBoolean();
            IsOpen               = reader.ReadBoolean();
            SuggestionsOpen      = reader.ReadBoolean();
            if (reader.BaseStream.Position < reader.BaseStream.Length)
                SelectedAudioTrack    = reader.ReadInt32();
            if (reader.BaseStream.Position < reader.BaseStream.Length)
                SelectedSubtitleTrack = reader.ReadInt32();
            if (reader.BaseStream.Position < reader.BaseStream.Length)
                SeekRevision = reader.ReadInt32();
        }

        protected override void WritePayload(BinaryWriter writer)
        {
            SyncVideoPacketSerialization.WriteString(writer, LobbyId);
            SyncVideoPacketSerialization.WriteString(writer, Url);
            SyncVideoPacketSerialization.WriteString(writer, VideoId);
            writer.Write(IsPlaying);
            writer.Write(MediaTimeSeconds);
            writer.Write(HostUnixMilliseconds);
            writer.Write(Revision);
            writer.Write(HasEnded);
            writer.Write(IsOpen);
            writer.Write(SuggestionsOpen);
            writer.Write(SelectedAudioTrack);
            writer.Write(SelectedSubtitleTrack);
            writer.Write(SeekRevision);
        }
    }
}
