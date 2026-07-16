using System;
using System.Collections.Generic;
using System.Text;

namespace PlayerViewer.Core.Formats
{
    /// <summary>
    /// Minimal little-endian SARC archive reader.
    /// </summary>
    public class Sarc
    {
        public readonly Dictionary<string, ArraySegment<byte>> Files = new(
            StringComparer.OrdinalIgnoreCase
        );

        public Sarc(byte[] data)
        {
            if (
                data.Length < 0x20
                || data[0] != 'S'
                || data[1] != 'A'
                || data[2] != 'R'
                || data[3] != 'C'
            )
                throw new InvalidOperationException("Not a SARC archive.");

            uint dataOffset = BitConverter.ToUInt32(data, 0x0C);

            //SFAT header directly after the 0x14-byte SARC header.
            int sfat = 0x14;
            ushort nodeCount = BitConverter.ToUInt16(data, sfat + 6);
            int nodes = sfat + 0x0C;

            //SFNT after the node table.
            int sfnt = nodes + nodeCount * 0x10;
            int names = sfnt + 8;

            for (int i = 0; i < nodeCount; i++)
            {
                int node = nodes + i * 0x10;
                uint attrs = BitConverter.ToUInt32(data, node + 4);
                uint start = BitConverter.ToUInt32(data, node + 8);
                uint end = BitConverter.ToUInt32(data, node + 12);

                string name;
                if ((attrs & 0x01000000) != 0)
                {
                    int nameOff = names + (int)((attrs & 0xFFFF) * 4);
                    int strEnd = nameOff;
                    while (data[strEnd] != 0)
                        strEnd++;
                    name = Encoding.UTF8.GetString(data, nameOff, strEnd - nameOff);
                }
                else
                {
                    name = $"hash_{BitConverter.ToUInt32(data, node):X8}";
                }

                Files[name] = new ArraySegment<byte>(
                    data,
                    (int)(dataOffset + start),
                    (int)(end - start)
                );
            }
        }

        public byte[] GetFile(string name)
        {
            if (!Files.TryGetValue(name, out var segment))
                return null;
            return segment.ToArray();
        }

        public string FindFile(Func<string, bool> predicate)
        {
            foreach (var name in Files.Keys)
                if (predicate(name))
                    return name;
            return null;
        }
    }
}
