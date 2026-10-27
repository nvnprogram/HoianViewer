using System;
using System.Buffers.Binary;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// The Phive shape container (.bphsh): the 0x30 byte header, a Havok tagfile holding the
    /// shape, the material table and the collision filter table.
    /// </summary>
    public class PhiveShapeFile
    {
        public const int HeaderSize = 0x30;
        public const int MaterialSize = 16;

        public byte[] Header = new byte[HeaderSize];
        public byte[] Tagfile = Array.Empty<byte>();

        /// <summary>One 16 byte entry per material: s32 material, s32, u64 flags.</summary>
        public byte[] Materials = Array.Empty<byte>();

        /// <summary>One entry per material, layers then mask; padded to 16 bytes.</summary>
        public byte[] Filters = Array.Empty<byte>();

        public int MaterialCount => Materials.Length / MaterialSize;

        /// <summary>8 for a Havok 2021 tagfile, 16 for 2025 and later.</summary>
        public int FilterSize => HavokVersion(Tagfile) >= 20250000 ? 16 : 8;

        public static PhiveShapeFile Read(byte[] data)
        {
            if (!PhiveFile.IsPhive(data))
                throw new InvalidOperationException("Not a Phive file");
            int U32(int at) => (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            int tagOffset = U32(0x0C), materialOffset = U32(0x10), filterOffset = U32(0x14);
            int tagSize = U32(0x1C), materialSize = U32(0x20), filterSize = U32(0x24);
            if (
                tagOffset < HeaderSize
                || tagOffset + tagSize > data.Length
                || materialOffset + materialSize > data.Length
                || filterOffset + filterSize > data.Length
            )
                throw new InvalidOperationException("Phive shape sections out of range");
            return new PhiveShapeFile
            {
                Header = data.AsSpan(0, tagOffset).ToArray(),
                Tagfile = data.AsSpan(tagOffset, tagSize).ToArray(),
                Materials = data.AsSpan(materialOffset, materialSize).ToArray(),
                Filters = data.AsSpan(filterOffset, filterSize).ToArray(),
            };
        }

        /// <summary>Header, then each section padded to 16 bytes; the header's offsets and sizes follow.</summary>
        public byte[] Write()
        {
            int tagSize = Align(Tagfile.Length), materialSize = Align(Materials.Length);
            int filterSize = Align(Filters.Length);
            int tagOffset = Header.Length;
            int materialOffset = tagOffset + tagSize;
            int filterOffset = materialOffset + materialSize;
            var output = new byte[filterOffset + filterSize];
            Header.CopyTo(output, 0);
            Tagfile.CopyTo(output, tagOffset);
            Materials.CopyTo(output, materialOffset);
            Filters.CopyTo(output, filterOffset);
            void Put(int at, int value) =>
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), (uint)value);
            Put(0x0C, tagOffset);
            Put(0x10, materialOffset);
            Put(0x14, filterOffset);
            Put(0x18, output.Length);
            Put(0x1C, tagSize);
            Put(0x20, materialSize);
            Put(0x24, filterSize);
            return output;
        }

        public (int Material, int Unknown, ulong Flags) Material(int index)
        {
            var s = Materials.AsSpan(index * MaterialSize);
            return (
                BinaryPrimitives.ReadInt32LittleEndian(s),
                BinaryPrimitives.ReadInt32LittleEndian(s[4..]),
                BinaryPrimitives.ReadUInt64LittleEndian(s[8..])
            );
        }

        public (ulong Layers, ulong Mask) Filter(int index)
        {
            var s = Filters.AsSpan(index * FilterSize);
            return FilterSize == 16
                ? (
                    BinaryPrimitives.ReadUInt64LittleEndian(s),
                    BinaryPrimitives.ReadUInt64LittleEndian(s[8..])
                )
                : (
                    BinaryPrimitives.ReadUInt32LittleEndian(s),
                    BinaryPrimitives.ReadUInt32LittleEndian(s[4..])
                );
        }

        /// <summary>The tagfile's SDKV as a number (20210100), or 0 when it has none.</summary>
        public static int HavokVersion(ReadOnlySpan<byte> tagfile)
        {
            int at = tagfile[..Math.Min(tagfile.Length, 0x40)].IndexOf("SDKV"u8);
            if (at < 0 || at + 12 > tagfile.Length)
                return 0;
            int value = 0;
            foreach (byte c in tagfile.Slice(at + 4, 8))
            {
                if (c < '0' || c > '9')
                    return 0;
                value = value * 10 + (c - '0');
            }
            return value;
        }

        static int Align(int size) => (size + 15) & ~15;
    }
}
