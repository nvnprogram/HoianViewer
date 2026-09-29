using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayerViewer.Core.Formats
{
    /// <summary>
    /// Writes a little endian BYML from the tree <see cref="Byml"/> reads: hashes as
    /// Dictionary&lt;string, object&gt;, arrays as List&lt;object&gt;, strings, bools, int, float,
    /// uint, long, ulong, double, binary (byte[] and <see cref="BymlBinary"/>) and null.
    /// else.
    /// </summary>
    public static class BymlWriter
    {
        public static byte[] Write(object root, ushort version = 7)
        {
            var keys = new SortedSet<string>(Utf8Order.Instance);
            var strings = new SortedSet<string>(Utf8Order.Instance);
            Collect(root, keys, strings);
            var keyIndex = keys.Select((k, i) => (k, i))
                .ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);
            var stringIndex = strings
                .Select((s, i) => (s, i))
                .ToDictionary(x => x.s, x => x.i, StringComparer.Ordinal);

            var output = new List<byte>(new byte[16]);
            int keyTable = 0,
                stringTable = 0;
            if (keys.Count > 0)
            {
                keyTable = output.Count;
                output.AddRange(StringTable(keys.ToList()));
            }
            if (strings.Count > 0)
            {
                stringTable = output.Count;
                output.AddRange(StringTable(strings.ToList()));
            }

            //Binary data sits between the string table and the root. An aligned node is placed
            //so its bytes, after the u32 size and u32 alignment, start on the alignment.
            var blobs = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
            foreach (var blob in Blobs(root, keyIndex))
            {
                if (blobs.ContainsKey(blob))
                    continue;
                if (blob is BymlBinary aligned)
                {
                    int align = (int)aligned.Alignment;
                    int dataStart = (output.Count + 8 + align - 1) / align * align;
                    output.AddRange(new byte[dataStart - 8 - output.Count]);
                    blobs[blob] = output.Count;
                    AddU32(output, (uint)aligned.Data.Count);
                    AddU32(output, aligned.Alignment);
                    output.AddRange(aligned.Data);
                }
                else
                {
                    var bytes = (byte[])blob;
                    while (output.Count % 4 != 0)
                        output.Add(0);
                    blobs[blob] = output.Count;
                    AddU32(output, (uint)bytes.Length);
                    output.AddRange(bytes);
                }
            }
            while (output.Count % 4 != 0)
                output.Add(0);

            var wide = new List<(int Slot, object Value)>();

            void Put32(int at, uint value)
            {
                output[at] = (byte)value;
                output[at + 1] = (byte)(value >> 8);
                output[at + 2] = (byte)(value >> 16);
                output[at + 3] = (byte)(value >> 24);
            }

            int WriteContainer(object value)
            {
                int start = output.Count;
                if (value is List<object> list)
                {
                    output.Add(0xC0);
                    AddU24(output, list.Count);
                    foreach (var x in list)
                        output.Add(TypeOf(x));
                    while (output.Count % 4 != 0)
                        output.Add(0);
                    int slots = output.Count;
                    output.AddRange(new byte[4 * list.Count]);
                    for (int i = 0; i < list.Count; i++)
                        Fill(slots + i * 4, list[i]);
                }
                else if (value is Dictionary<string, object> hash)
                {
                    var items = hash.OrderBy(kv => keyIndex[kv.Key]).ToList();
                    output.Add(0xC1);
                    AddU24(output, items.Count);
                    int slots = output.Count;
                    output.AddRange(new byte[8 * items.Count]);
                    for (int i = 0; i < items.Count; i++)
                    {
                        int e = slots + i * 8;
                        int k = keyIndex[items[i].Key];
                        output[e] = (byte)k;
                        output[e + 1] = (byte)(k >> 8);
                        output[e + 2] = (byte)(k >> 16);
                        output[e + 3] = TypeOf(items[i].Value);
                        Fill(e + 4, items[i].Value);
                    }
                }
                else
                    throw new InvalidOperationException(
                        "A BYML container must be a hash or an array"
                    );
                return start;
            }

            void Fill(int slot, object x)
            {
                switch (x)
                {
                    case List<object>:
                    case Dictionary<string, object>:
                        Put32(slot, (uint)WriteContainer(x));
                        break;
                    case long:
                    case ulong:
                    case double:
                        wide.Add((slot, x));
                        break;
                    case string s:
                        Put32(slot, (uint)stringIndex[s]);
                        break;
                    case byte[]:
                    case BymlBinary:
                        Put32(slot, (uint)blobs[x]);
                        break;
                    case bool b:
                        Put32(slot, b ? 1u : 0u);
                        break;
                    case int i:
                        Put32(slot, (uint)i);
                        break;
                    case float f:
                        Put32(slot, BitConverter.SingleToUInt32Bits(f));
                        break;
                    case uint u:
                        Put32(slot, u);
                        break;
                    case null:
                        Put32(slot, 0);
                        break;
                    default:
                        throw new NotSupportedException($"BYML value of type {x.GetType().Name}");
                }
            }

            int rootOffset = WriteContainer(root);
            foreach (var (slot, value) in wide)
            {
                while (output.Count % 8 != 0)
                    output.Add(0);
                Put32(slot, (uint)output.Count);
                var bytes = new byte[8];
                switch (value)
                {
                    case long l:
                        BinaryPrimitives.WriteInt64LittleEndian(bytes, l);
                        break;
                    case ulong ul:
                        BinaryPrimitives.WriteUInt64LittleEndian(bytes, ul);
                        break;
                    case double d:
                        BinaryPrimitives.WriteDoubleLittleEndian(bytes, d);
                        break;
                }
                output.AddRange(bytes);
            }

            var result = output.ToArray();
            result[0] = (byte)'Y';
            result[1] = (byte)'B';
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), version);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)keyTable);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)stringTable);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), (uint)rootOffset);
            return result;
        }

        static byte TypeOf(object v) =>
            v switch
            {
                null => 0xFF,
                bool => 0xD0,
                int => 0xD1,
                float => 0xD2,
                uint => 0xD3,
                long => 0xD4,
                ulong => 0xD5,
                double => 0xD6,
                string => 0xA0,
                byte[] => 0xA1,
                BymlBinary => 0xA2,
                List<object> => 0xC0,
                Dictionary<string, object> => 0xC1,
                _ => throw new NotSupportedException($"BYML value of type {v.GetType().Name}"),
            };

        static void Collect(object v, SortedSet<string> keys, SortedSet<string> strings)
        {
            switch (v)
            {
                case Dictionary<string, object> hash:
                    foreach (var (k, x) in hash)
                    {
                        keys.Add(k);
                        Collect(x, keys, strings);
                    }
                    break;
                case List<object> list:
                    foreach (var x in list)
                        Collect(x, keys, strings);
                    break;
                case string s:
                    strings.Add(s);
                    break;
            }
        }

        /// <summary>The binary values in the order the container writer reaches them.</summary>
        static IEnumerable<object> Blobs(object v, Dictionary<string, int> keyIndex)
        {
            IEnumerable<object> children = v switch
            {
                Dictionary<string, object> hash => hash.OrderBy(kv => keyIndex[kv.Key])
                    .Select(kv => kv.Value),
                List<object> list => list,
                _ => null,
            };
            if (children == null)
                yield break;
            foreach (var x in children)
            {
                if (x is byte[] or BymlBinary)
                    yield return x;
                else
                    foreach (var blob in Blobs(x, keyIndex))
                        yield return blob;
            }
        }

        static void AddU32(List<byte> output, uint value)
        {
            output.Add((byte)value);
            output.Add((byte)(value >> 8));
            output.Add((byte)(value >> 16));
            output.Add((byte)(value >> 24));
        }

        static void AddU24(List<byte> output, int value)
        {
            output.Add((byte)value);
            output.Add((byte)(value >> 8));
            output.Add((byte)(value >> 16));
        }

        static byte[] StringTable(List<string> strings)
        {
            var output = new List<byte> { 0xC2 };
            AddU24(output, strings.Count);
            int offsets = output.Count;
            output.AddRange(new byte[4 * (strings.Count + 1)]);
            void Offset(int index, int value) =>
                BinaryPrimitives.WriteUInt32LittleEndian(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(output)[
                        (offsets + index * 4)..
                    ],
                    (uint)value
                );
            for (int i = 0; i < strings.Count; i++)
            {
                Offset(i, output.Count);
                output.AddRange(Encoding.UTF8.GetBytes(strings[i]));
                output.Add(0);
            }
            Offset(strings.Count, output.Count);
            while (output.Count % 4 != 0)
                output.Add(0);
            return output.ToArray();
        }

        sealed class Utf8Order : IComparer<string>
        {
            public static readonly Utf8Order Instance = new();

            public int Compare(string a, string b) =>
                Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));
        }
    }
}
