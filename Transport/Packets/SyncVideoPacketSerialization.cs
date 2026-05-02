using System.IO;

namespace SyncVideo.Transport.Packets
{
    public static class SyncVideoPacketSerialization
    {
        public static void WriteString(BinaryWriter writer, string value)
        {
            writer.Write(value ?? string.Empty);
        }

        public static string ReadString(BinaryReader reader)
        {
            return reader.ReadString();
        }

        public static void WriteUShortArray(BinaryWriter writer, ushort[] values)
        {
            if (values == null)
            {
                writer.Write(0);
                return;
            }

            writer.Write(values.Length);
            for (int i = 0; i < values.Length; i++)
                writer.Write(values[i]);
        }

        public static ushort[] ReadUShortArray(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count <= 0)
                return new ushort[0];

            var values = new ushort[count];
            for (int i = 0; i < count; i++)
                values[i] = reader.ReadUInt16();

            return values;
        }
    }
}
