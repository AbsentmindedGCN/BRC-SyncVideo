using System.IO;
using System.Text;

namespace SyncVideo.Transport.Packets
{
    public abstract class SyncVideoPacketBase
    {
        public ushort SenderPlayerId;

        public abstract string PacketId { get; }

        // serialise the packet payload to a byte array
        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms, Encoding.UTF8))
            {
                WritePayload(writer);
                writer.Flush();
                return ms.ToArray();
            }
        }

        // deserialise the payload from a received byte array
        protected void PopulateFrom(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms, Encoding.UTF8))
            {
                ReadPayload(reader);
            }
        }

        protected abstract void WritePayload(BinaryWriter writer);
        protected abstract void ReadPayload(BinaryReader reader);
    }
}
