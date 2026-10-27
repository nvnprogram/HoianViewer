using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PlayerViewer.Core.Formats
{
    /// <summary>
    /// Writes a BYML from the tree <see cref="Byml"/> reads: hashes as
    /// Dictionary&lt;string, object&gt; (<see cref="BymlKeyHash"/> for the hash keyed ones),
    /// arrays as List&lt;object&gt;, strings, bools, int, float, uint, long, ulong, double, binary
    /// (byte[] and <see cref="BymlBinary"/>), <see cref="BymlValue128"/> and null.
    /// </summary>
    public static class BymlWriter
    {
        public static byte[] Write(object root, ushort version = 7, bool bigEndian = false)
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
                output.AddRange(StringTable(keys.ToList(), bigEndian));
            }
            if (strings.Count > 0)
            {
                stringTable = output.Count;
                output.AddRange(StringTable(strings.ToList(), bigEndian));
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
                    AddU32(output, (uint)aligned.Data.Count, bigEndian);
                    AddU32(output, aligned.Alignment, bigEndian);
                    output.AddRange(aligned.Data);
                }
                else
                {
                    var bytes = (byte[])blob;
                    while (output.Count % 4 != 0)
                        output.Add(0);
                    blobs[blob] = output.Count;
                    AddU32(output, (uint)bytes.Length, bigEndian);
                    output.AddRange(bytes);
                }
            }
            while (output.Count % 4 != 0)
                output.Add(0);

            var wide = new List<(int Slot, object Value)>();

            void Put(int at, ulong value, int size)
            {
                for (int i = 0; i < size; i++)
                    output[at + i] = (byte)(value >> (8 * (bigEndian ? size - 1 - i : i)));
            }

            void Put32(int at, uint value) => Put(at, value, 4);

            int WriteContainer(object value)
            {
                int start = output.Count;
                if (value is List<object> list)
                {
                    output.Add(0xC0);
                    AddU24(output, list.Count, bigEndian);
                    foreach (var x in list)
                        output.Add(TypeOf(x));
                    while (output.Count % 4 != 0)
                        output.Add(0);
                    int slots = output.Count;
                    output.AddRange(new byte[4 * list.Count]);
                    for (int i = 0; i < list.Count; i++)
                        Fill(slots + i * 4, list[i]);
                }
                else if (value is BymlKeyHash keyed)
                {
                    //Entries in hash order, then one type byte each.
                    int hashSize = keyed.Wide ? 8 : 4;
                    var items = keyed
                        .Select(kv => (Hash: ParseHashKey(kv.Key, keyed.Wide), kv.Value))
                        .OrderBy(x => x.Hash)
                        .ToList();
                    output.Add(keyed.Wide ? (byte)0x21 : (byte)0x20);
                    AddU24(output, items.Count, bigEndian);
                    int slots = output.Count;
                    int stride = hashSize + 4;
                    output.AddRange(new byte[stride * items.Count]);
                    foreach (var (_, v) in items)
                        output.Add(TypeOf(v));
                    while (output.Count % 4 != 0)
                        output.Add(0);
                    for (int i = 0; i < items.Count; i++)
                    {
                        int e = slots + i * stride;
                        Put(e, items[i].Hash, hashSize);
                        Fill(e + hashSize, items[i].Value);
                    }
                }
                else if (value is Dictionary<string, object> hash)
                {
                    var items = hash.OrderBy(kv => keyIndex[kv.Key]).ToList();
                    output.Add(0xC1);
                    AddU24(output, items.Count, bigEndian);
                    int slots = output.Count;
                    output.AddRange(new byte[8 * items.Count]);
                    for (int i = 0; i < items.Count; i++)
                    {
                        int e = slots + i * 8;
                        Put(e, (uint)keyIndex[items[i].Key], 3);
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
                    case BymlValue128:
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

            int rootOffset = root == null ? 0 : WriteContainer(root);
            foreach (var (slot, value) in wide)
            {
                while (output.Count % 8 != 0)
                    output.Add(0);
                Put32(slot, (uint)output.Count);
                if (value is BymlValue128 v128)
                {
                    output.AddRange(v128.Bytes);
                    continue;
                }
                ulong bits = value switch
                {
                    long l => (ulong)l,
                    ulong ul => ul,
                    double d => BitConverter.DoubleToUInt64Bits(d),
                    _ => 0,
                };
                output.AddRange(new byte[8]);
                Put(output.Count - 8, bits, 8);
            }

            var result = output.ToArray();
            result[0] = (byte)(bigEndian ? 'B' : 'Y');
            result[1] = (byte)(bigEndian ? 'Y' : 'B');
            WriteHeader(result, 2, version, 2, bigEndian);
            WriteHeader(result, 4, (uint)keyTable, 4, bigEndian);
            WriteHeader(result, 8, (uint)stringTable, 4, bigEndian);
            WriteHeader(result, 12, (uint)rootOffset, 4, bigEndian);
            return result;
        }

        static void WriteHeader(byte[] b, int at, uint value, int size, bool bigEndian)
        {
            for (int i = 0; i < size; i++)
                b[at + i] = (byte)(value >> (8 * (bigEndian ? size - 1 - i : i)));
        }

        static ulong ParseHashKey(string key, bool wide)
        {
            int digits = wide ? 16 : 8;
            if (
                key.Length != digits + 1
                || key[0] != '#'
                || !ulong.TryParse(
                    key.AsSpan(1),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out ulong hash
                )
            )
                throw new InvalidOperationException(
                    $"A hash keyed BYML node has the key \"{key}\", not '#' and {digits} hex digits"
                );
            return hash;
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
                BymlValue128 => 0x40,
                List<object> => 0xC0,
                BymlKeyHash k => k.Wide ? (byte)0x21 : (byte)0x20,
                Dictionary<string, object> => 0xC1,
                _ => throw new NotSupportedException($"BYML value of type {v.GetType().Name}"),
            };

        static void Collect(object v, SortedSet<string> keys, SortedSet<string> strings)
        {
            switch (v)
            {
                case Dictionary<string, object> hash:
                    bool named = hash is not BymlKeyHash;
                    foreach (var (k, x) in hash)
                    {
                        if (named)
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
                BymlKeyHash keyed => keyed
                    .OrderBy(kv => ParseHashKey(kv.Key, keyed.Wide))
                    .Select(kv => kv.Value),
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

        static void AddU32(List<byte> output, uint value, bool bigEndian)
        {
            for (int i = 0; i < 4; i++)
                output.Add((byte)(value >> (8 * (bigEndian ? 3 - i : i))));
        }

        static void AddU24(List<byte> output, int value, bool bigEndian)
        {
            for (int i = 0; i < 3; i++)
                output.Add((byte)(value >> (8 * (bigEndian ? 2 - i : i))));
        }

        static byte[] StringTable(List<string> strings, bool bigEndian)
        {
            var output = new List<byte> { 0xC2 };
            AddU24(output, strings.Count, bigEndian);
            int offsets = output.Count;
            output.AddRange(new byte[4 * (strings.Count + 1)]);
            void Offset(int index, int value)
            {
                for (int i = 0; i < 4; i++)
                    output[offsets + index * 4 + i] = (byte)(
                        value >> (8 * (bigEndian ? 3 - i : i))
                    );
            }
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
