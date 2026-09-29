using System;
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
    /// Minimal little-endian BYML reader. Binary nodes come back as byte[] (0xA1) and
    /// <see cref="BymlBinary"/> (0xA2).
    /// </summary>
    public class Byml
    {
        public object Root { get; private set; }

        public ushort Version { get; private set; }

        readonly byte[] _data;
        string[] _hashKeys = Array.Empty<string>();
        string[] _strings = Array.Empty<string>();

        public Byml(byte[] data)
        {
            _data = data;

            if (data.Length < 16 || data[0] != 'Y' || data[1] != 'B')
                throw new InvalidOperationException("Not a little-endian BYML file.");

            Version = BitConverter.ToUInt16(data, 2);
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

        uint ReadU32(int offset) => BitConverter.ToUInt32(_data, offset);

        uint ReadU24(int offset) =>
            (uint)(_data[offset] | (_data[offset + 1] << 8) | (_data[offset + 2] << 16));

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
                case 0xD0:
                    return ReadU32((int)valueOrOffset) != 0;
                case 0xD1:
                    return BitConverter.ToInt32(_data, (int)valueOrOffset);
                case 0xD2:
                    return BitConverter.ToSingle(_data, (int)valueOrOffset);
                case 0xD3:
                    return ReadU32((int)valueOrOffset);
                case 0xD4:
                    return BitConverter.ToInt64(_data, (int)ReadU32((int)valueOrOffset));
                case 0xD5:
                    return BitConverter.ToUInt64(_data, (int)ReadU32((int)valueOrOffset));
                case 0xD6:
                    return BitConverter.ToDouble(_data, (int)ReadU32((int)valueOrOffset));
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
                case 0xA1:
                case 0xA2:
                case 0xC0:
                case 0xC1:
                case 0xD4:
                case 0xD5:
                case 0xD6:
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
