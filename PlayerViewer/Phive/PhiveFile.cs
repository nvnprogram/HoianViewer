using System;
using System.Buffers.Binary;
using System.Text;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// The Phive resource container (.bphcl and friends): a 0x30 byte header, a Havok tagfile
    /// and an AAMP parameter block. Both payloads are kept as bytes so a file can be written
    /// back with only the part that changed replaced.
    /// </summary>
    public class PhiveFile
    {
        public const int HeaderSize = 0x30;

        /// <summary>The header as read. Only the offsets and sizes are rewritten on save.</summary>
        public byte[] Header = new byte[HeaderSize];
        public byte[] Tagfile = Array.Empty<byte>();
        public byte[] Aamp = Array.Empty<byte>();

        public static bool IsPhive(byte[] data) =>
            data.Length >= HeaderSize && Encoding.ASCII.GetString(data, 0, 5) == "Phive";

        public static PhiveFile Read(byte[] data)
        {
            if (!IsPhive(data))
                throw new InvalidOperationException("Not a Phive file");
            var span = data.AsSpan();
            int tagOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[0x0C..]);
            int aampOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[0x10..]);
            int tagSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[0x18..]);
            int aampSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[0x1C..]);
            if (tagOffset < HeaderSize || tagOffset > data.Length)
                throw new InvalidOperationException("Phive tagfile offset out of range");
            var file = new PhiveFile
            {
                Header = data.AsSpan(0, tagOffset).ToArray(),
                Tagfile = data.AsSpan(tagOffset, Math.Min(tagSize, data.Length - tagOffset))
                    .ToArray(),
            };
            if (aampOffset > 0 && aampOffset + aampSize <= data.Length)
                file.Aamp = data.AsSpan(aampOffset, aampSize).ToArray();
            return file;
        }

        /// <summary>Header, tagfile padded to 16 bytes, AAMP; the header's offsets and sizes follow.</summary>
        public byte[] Write()
        {
            int tagOffset = Header.Length;
            int tagSize = (Tagfile.Length + 15) & ~15;
            var output = new byte[tagOffset + tagSize + Aamp.Length];
            Header.CopyTo(output, 0);
            Tagfile.CopyTo(output, tagOffset);
            Aamp.CopyTo(output, tagOffset + tagSize);
            var span = output.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x0C..], (uint)tagOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x10..], (uint)(tagOffset + tagSize));
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x14..], (uint)output.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], (uint)tagSize);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x1C..], (uint)Aamp.Length);
            return output;
        }
    }
}
