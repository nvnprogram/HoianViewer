using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace PlayerViewer.Core.Formats
{
    /// <summary>
    /// A binary node with an alignment (type 0xA2): u32 size, u32 alignment, then the bytes,
    /// placed so the bytes start on the alignment. Read as a slice of the loaded file, since the
    /// effect archives carry a hundred MB in one.
    /// </summary>
    public sealed class BymlBinary
    {
        public ArraySegment<byte> Data { get; }
        public uint Alignment { get; }

        public BymlBinary(ArraySegment<byte> data, uint alignment)
        {
            if (alignment == 0 || (alignment & (alignment - 1)) != 0)
                throw new ArgumentException(
                    "The alignment must be a power of two",
                    nameof(alignment)
                );
            Data = data;
            Alignment = alignment;
        }
    }

    /// <summary>
    /// A hash keyed by the hash of each name rather than the name (0x20, or 0x21 with
    /// <see cref="Wide"/>). The keys are "#" and the hash in hex.
    /// </summary>
    public sealed class BymlKeyHash : Dictionary<string, object>
    {
        public bool Wide { get; }

        public BymlKeyHash(bool wide, int capacity = 0)
            : base(capacity)
        {
            Wide = wide;
        }
    }

    /// <summary>A 16 byte value stored by offset (0x40).</summary>
    public sealed class BymlValue128
    {
        public byte[] Bytes { get; }

        public BymlValue128(byte[] bytes)
        {
            if (bytes.Length != 16)
                throw new ArgumentException("A 0x40 value is 16 bytes", nameof(bytes));
            Bytes = bytes;
        }
    }

    /// <summary>
    /// Minimal BYML reader for either byte order. Binary nodes come back as byte[] (0xA1) and
    /// <see cref="BymlBinary"/> (0xA2), the hash keyed hashes as <see cref="BymlKeyHash"/>, the
    /// 16 byte value as <see cref="BymlValue128"/>.
    /// </summary>
    public class Byml
    {
        public object Root { get; private set; }

        public ushort Version { get; private set; }

        readonly byte[] _data;
        readonly bool _bigEndian;
        string[] _hashKeys = Array.Empty<string>();
        string[] _strings = Array.Empty<string>();

        public Byml(byte[] data)
        {
            _data = data;

            if (
                data.Length < 16
                || !(data[0] == 'Y' && data[1] == 'B' || data[0] == 'B' && data[1] == 'Y')
            )
                throw new InvalidOperationException("Not a BYML file.");
            _bigEndian = data[0] == 'B';

            Version = _bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
            uint hashKeyTableOff = ReadU32(4);
            uint stringTableOff = ReadU32(8);
            uint rootOff = ReadU32(12);

            if (hashKeyTableOff != 0)
                _hashKeys = ReadStringTable(hashKeyTableOff);
            if (stringTableOff != 0)
                _strings = ReadStringTable(stringTableOff);

            if (rootOff != 0)
                Root = ReadNode(_data[rootOff], rootOff);
        }

        uint ReadU32(int offset) =>
            _bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset))
                : BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset));

        ulong ReadU64(int offset) =>
            _bigEndian
                ? BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(offset))
                : BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(offset));

        uint ReadU24(int offset) =>
            _bigEndian
                ? (uint)(_data[offset] << 16 | _data[offset + 1] << 8 | _data[offset + 2])
                : (uint)(_data[offset] | (_data[offset + 1] << 8) | (_data[offset + 2] << 16));

        string[] ReadStringTable(uint offset)
        {
            //u8 type (0xC2), u24 count, then count+1 u32 offsets relative to table start.
            uint count = ReadU24((int)offset + 1);
            var result = new string[count];
            for (uint i = 0; i < count; i++)
            {
                uint strOff = offset + ReadU32((int)(offset + 4 + i * 4));
                int end = (int)strOff;
                while (_data[end] != 0)
                    end++;
                result[i] = Encoding.UTF8.GetString(_data, (int)strOff, end - (int)strOff);
            }
            return result;
        }

        object ReadNode(byte type, uint valueOrOffset)
        {
            switch (type)
            {
                case 0xA0:
                    return _strings[ReadU32((int)valueOrOffset)];
                case 0xA1:
                {
                    int size = (int)ReadU32((int)valueOrOffset);
                    var bytes = new byte[size];
                    Array.Copy(_data, (int)valueOrOffset + 4, bytes, 0, size);
                    return bytes;
                }
                case 0xA2:
                {
                    int size = (int)ReadU32((int)valueOrOffset);
                    uint alignment = ReadU32((int)valueOrOffset + 4);
                    return new BymlBinary(
                        new ArraySegment<byte>(_data, (int)valueOrOffset + 8, size),
                        alignment
                    );
                }
                case 0xC0:
                    return ReadArray(valueOrOffset);
                case 0xC1:
                    return ReadHash(valueOrOffset);
                case 0x20:
                    return ReadKeyHashHash(valueOrOffset);
                case 0x21:
                    return ReadKeyHash64Hash(valueOrOffset);
                case 0xD0:
                    return ReadU32((int)valueOrOffset) != 0;
                case 0xD1:
                    return (int)ReadU32((int)valueOrOffset);
                case 0xD2:
                    return BitConverter.Int32BitsToSingle((int)ReadU32((int)valueOrOffset));
                case 0xD3:
                    return ReadU32((int)valueOrOffset);
                //The 64 bit values and the 16 byte one arrive here already at their data.
                case 0xD4:
                    return (long)ReadU64((int)valueOrOffset);
                case 0xD5:
                    return ReadU64((int)valueOrOffset);
                case 0xD6:
                    return BitConverter.Int64BitsToDouble((long)ReadU64((int)valueOrOffset));
                case 0x40:
                {
                    var bytes = new byte[16];
                    Array.Copy(_data, (int)valueOrOffset, bytes, 0, 16);
                    return new BymlValue128(bytes);
                }
                case 0xFF:
                    return null;
                default:
                    throw new NotSupportedException($"BYML node type 0x{type:X2} not supported.");
            }
        }

        //Container nodes store the child value inline for value types and as an offset for containers.
        object ReadNodeIndirect(byte type, int slotOffset)
        {
            switch (type)
            {
                case 0x20:
                case 0x21:
                case 0xA1:
                case 0xA2:
                case 0xC0:
                case 0xC1:
                case 0xD4:
                case 0xD5:
                case 0xD6:
                case 0x40:
                    return ReadNode(type, ReadU32(slotOffset));
                default:
                    return ReadNode(type, (uint)slotOffset);
            }
        }

        List<object> ReadArray(uint offset)
        {
            uint count = ReadU24((int)offset + 1);
            var list = new List<object>((int)count);
            uint typesStart = offset + 4;
            //Type bytes padded to a 4 byte boundary, then the u32 value slots.
            uint valuesStart = typesStart + ((count + 3) & ~3u);
            for (uint i = 0; i < count; i++)
                list.Add(ReadNodeIndirect(_data[typesStart + i], (int)(valuesStart + i * 4)));
            return list;
        }

        Dictionary<string, object> ReadHash(uint offset)
        {
            uint count = ReadU24((int)offset + 1);
            var dict = new Dictionary<string, object>((int)count);
            for (uint i = 0; i < count; i++)
            {
                int entry = (int)(offset + 4 + i * 8);
                string key = _hashKeys[ReadU24(entry)];
                dict[key] = ReadNodeIndirect(_data[entry + 3], entry + 4);
            }
            return dict;
        }

        /// <summary>
        /// A hash keyed by the hash of each name (0x20): u32 hash and value per entry, then the
        /// type bytes. Read with <see cref="HashKey"/> as the key.
        /// </summary>
        Dictionary<string, object> ReadKeyHashHash(uint offset)
        {
            uint count = ReadU24((int)offset + 1);
            var dict = new BymlKeyHash(false, (int)count);
            uint types = offset + 4 + count * 8;
            for (uint i = 0; i < count; i++)
            {
                int entry = (int)(offset + 4 + i * 8);
                dict[$"#{ReadU32(entry):x8}"] = ReadNodeIndirect(_data[types + i], entry + 4);
            }
            return dict;
        }

        /// <summary>
        /// The 64 bit form of the hash keyed node (0x21): u64 hash and value per entry, then the
        /// type bytes. Keys are the hash in hex.
        /// </summary>
        Dictionary<string, object> ReadKeyHash64Hash(uint offset)
        {
            uint count = ReadU24((int)offset + 1);
            var dict = new BymlKeyHash(true, (int)count);
            uint types = offset + 4 + count * 12;
            for (uint i = 0; i < count; i++)
            {
                int entry = (int)(offset + 4 + i * 12);
                dict[$"#{ReadU64(entry):x16}"] = ReadNodeIndirect(_data[types + i], entry + 8);
            }
            return dict;
        }

        /// <summary>The key a hash-keyed node files a name under.</summary>
        public static string HashKey(string name) => $"#{Murmur3.Hash(name):x8}";

        #region typed access helpers

        public static Dictionary<string, object> AsHash(object node) =>
            node as Dictionary<string, object>;

        public static List<object> AsArray(object node) => node as List<object>;

        public static string GetString(
            Dictionary<string, object> hash,
            string key,
            string fallback = ""
        ) => hash != null && hash.TryGetValue(key, out var v) && v is string s ? s : fallback;

        public static int GetInt(Dictionary<string, object> hash, string key, int fallback = 0)
        {
            if (hash == null || !hash.TryGetValue(key, out var v))
                return fallback;
            return v switch
            {
                int i => i,
                uint u => (int)u,
                float f => (int)f,
                _ => fallback,
            };
        }

        public static float GetFloat(
            Dictionary<string, object> hash,
            string key,
            float fallback = 0f
        )
        {
            if (hash == null || !hash.TryGetValue(key, out var v))
                return fallback;
            return v switch
            {
                float f => f,
                int i => i,
                uint u => u,
                double d => (float)d,
                _ => fallback,
            };
        }

        public static bool GetBool(
            Dictionary<string, object> hash,
            string key,
            bool fallback = false
        ) => hash != null && hash.TryGetValue(key, out var v) && v is bool b ? b : fallback;

        #endregion
    }
}
