using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayerViewer.Core.Formats
{
    /// <summary>
    /// Writes a little endian SARC
    /// </summary>
    public static class SarcWriter
    {
        const uint HashKey = 0x65;
        const int Alignment = 8;

        public static uint NameHash(string name)
        {
            uint hash = 0;
            foreach (byte b in Encoding.UTF8.GetBytes(name))
                hash = hash * HashKey + (uint)(sbyte)b;
            return hash;
        }

        public static byte[] Write(IEnumerable<KeyValuePair<string, byte[]>> files)
        {
            var nodes = files
                .Select(f => (Name: f.Key, Data: f.Value, Hash: NameHash(f.Key)))
                .OrderBy(f => f.Hash)
                .ToList();

            var names = new List<byte>();
            var nameOffsets = new int[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                nameOffsets[i] = names.Count;
                names.AddRange(Encoding.UTF8.GetBytes(nodes[i].Name));
                names.Add(0);
                while (names.Count % 4 != 0)
                    names.Add(0);
            }

            int sfat = 0x14;
            int sfnt = sfat + 0x0C + nodes.Count * 0x10;
            int dataOffset = Align(sfnt + 8 + names.Count);

            var starts = new int[nodes.Count];
            int cursor = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                cursor = Align(dataOffset + cursor) - dataOffset;
                starts[i] = cursor;
                cursor += nodes[i].Data.Length;
            }

            var output = new byte[dataOffset + cursor];
            var span = output.AsSpan();
            Encoding.ASCII.GetBytes("SARC").CopyTo(output, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 0x14);
            BinaryPrimitives.WriteUInt16LittleEndian(span[6..], 0xFEFF);
            BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)output.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x0C..], (uint)dataOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(span[0x10..], 0x0100);

            Encoding.ASCII.GetBytes("SFAT").CopyTo(output, sfat);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(sfat + 4)..], 0x0C);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(sfat + 6)..], (ushort)nodes.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(sfat + 8)..], HashKey);
            for (int i = 0; i < nodes.Count; i++)
            {
                int node = sfat + 0x0C + i * 0x10;
                BinaryPrimitives.WriteUInt32LittleEndian(span[node..], nodes[i].Hash);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    span[(node + 4)..],
                    0x01000000u | (uint)(nameOffsets[i] / 4)
                );
                BinaryPrimitives.WriteUInt32LittleEndian(span[(node + 8)..], (uint)starts[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    span[(node + 12)..],
                    (uint)(starts[i] + nodes[i].Data.Length)
                );
                nodes[i].Data.CopyTo(output, dataOffset + starts[i]);
            }

            Encoding.ASCII.GetBytes("SFNT").CopyTo(output, sfnt);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(sfnt + 4)..], 8);
            names.CopyTo(output, sfnt + 8);
            return output;
        }

        static int Align(int value) => (value + Alignment - 1) & ~(Alignment - 1);
    }
}
