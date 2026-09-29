using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Text;

namespace PlayerViewer.Phive
{
    public enum AampType : byte
    {
        Bool = 0,
        F32 = 1,
        Int = 2,
        Vec2 = 3,
        Vec3 = 4,
        Vec4 = 5,
        Color = 6,
        String32 = 7,
        String64 = 8,
        Curve1 = 9,
        Curve2 = 10,
        Curve3 = 11,
        Curve4 = 12,
        BufferInt = 13,
        BufferF32 = 14,
        String256 = 15,
        Quat = 16,
        U32 = 17,
        BufferU32 = 18,
        BufferBinary = 19,
        StringRef = 20,
    }

    /// <summary>One AAMP parameter: its name hash, type and value (raw bytes, or a string for the string types).</summary>
    public class AampParam
    {
        public uint Hash;
        public AampType Type;

        /// <summary>The value's bytes for the non string types; a buffer's element bytes without its count.</summary>
        public byte[] Data = Array.Empty<byte>();
        public string Text;

        public bool IsString =>
            Type
                is AampType.String32
                    or AampType.String64
                    or AampType.String256
                    or AampType.StringRef;

        public bool IsBuffer =>
            Type
                is AampType.BufferInt
                    or AampType.BufferF32
                    or AampType.BufferU32
                    or AampType.BufferBinary;

        public bool Bool
        {
            get => Data.Length > 0 && Data[0] != 0;
            set => Data = BitConverter.GetBytes(value ? 1u : 0u);
        }

        public float Float
        {
            get => Data.Length >= 4 ? BitConverter.ToSingle(Data, 0) : 0;
            set => Data = BitConverter.GetBytes(value);
        }

        public int Int
        {
            get => Data.Length >= 4 ? BitConverter.ToInt32(Data, 0) : 0;
            set => Data = BitConverter.GetBytes(value);
        }

        public AampParam Clone() =>
            new()
            {
                Hash = Hash,
                Type = Type,
                Data = (byte[])Data.Clone(),
                Text = Text,
            };

        public override string ToString() =>
            IsString ? $"{Hash:X8} {Type} '{Text}'"
            : Type == AampType.Bool ? $"{Hash:X8} {Bool}"
            : Type == AampType.F32 ? $"{Hash:X8} {Float}"
            : $"{Hash:X8} {Type} {Convert.ToHexString(Data)}";
    }

    /// <summary>An AAMP parameter object: named parameters in file order.</summary>
    public class AampObject
    {
        public uint Hash;
        public List<AampParam> Params = new();

        public AampParam Find(string name) => Find(Aamp.Hash(name));

        public AampParam Find(uint hash) => Params.FirstOrDefault(p => p.Hash == hash);

        public AampObject Clone() =>
            new() { Hash = Hash, Params = Params.Select(p => p.Clone()).ToList() };
    }

    /// <summary>An AAMP parameter list: child lists and objects in file order.</summary>
    public class AampList
    {
        public uint Hash;
        public List<AampList> Lists = new();
        public List<AampObject> Objects = new();

        public AampList FindList(string name) =>
            Lists.FirstOrDefault(l => l.Hash == Aamp.Hash(name));

        public AampObject FindObject(string name) =>
            Objects.FirstOrDefault(o => o.Hash == Aamp.Hash(name));
    }

    /// <summary>
    /// An AAMP (version 2, little endian) parameter archive. Names are only their CRC32. The
    /// writer lays the file out as the game's files are laid out: lists, then objects, then
    /// parameters, then the value data with equal values stored once, then the strings.
    /// </summary>
    public class Aamp
    {
        public uint Version;
        public uint Flags = 3;
        public string DataType = "xml";
        public AampList Root = new() { Hash = Hash("param_root") };

        public static uint Hash(string name) => Crc32.HashToUInt32(Encoding.UTF8.GetBytes(name));

        const int HeaderSize = 0x30;

        public static Aamp Read(byte[] data)
        {
            if (data.Length < HeaderSize || Encoding.ASCII.GetString(data, 0, 4) != "AAMP")
                throw new InvalidOperationException("Not an AAMP file");
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) != 2)
                throw new InvalidOperationException("Only AAMP version 2 is supported");
            var aamp = new Aamp { Flags = U32(data, 8), Version = U32(data, 0x10) };
            int typeLen = 0;
            while (data[HeaderSize + typeLen] != 0)
                typeLen++;
            aamp.DataType = Encoding.ASCII.GetString(data, HeaderSize, typeLen);
            int rootOffset = HeaderSize + (int)U32(data, 0x14);
            aamp.Root = ReadList(data, rootOffset);
            return aamp;
        }

        static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));

        static ushort U16(byte[] d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(o));

        static AampList ReadList(byte[] d, int offset)
        {
            var list = new AampList { Hash = U32(d, offset) };
            int lists = offset + U16(d, offset + 4) * 4;
            int numLists = U16(d, offset + 6);
            int objects = offset + U16(d, offset + 8) * 4;
            int numObjects = U16(d, offset + 10);
            for (int i = 0; i < numLists; i++)
                list.Lists.Add(ReadList(d, lists + i * 12));
            for (int i = 0; i < numObjects; i++)
                list.Objects.Add(ReadObject(d, objects + i * 8));
            return list;
        }

        static AampObject ReadObject(byte[] d, int offset)
        {
            var obj = new AampObject { Hash = U32(d, offset) };
            int parameters = offset + U16(d, offset + 4) * 4;
            int count = U16(d, offset + 6);
            for (int i = 0; i < count; i++)
            {
                int p = parameters + i * 8;
                uint packed = U32(d, p + 4);
                var param = new AampParam { Hash = U32(d, p), Type = (AampType)(packed >> 24) };
                int dataOffset = p + (int)(packed & 0xFFFFFF) * 4;
                if (param.IsString)
                {
                    int end = dataOffset;
                    while (d[end] != 0)
                        end++;
                    param.Text = Encoding.UTF8.GetString(d, dataOffset, end - dataOffset);
                }
                else if (param.IsBuffer)
                {
                    int count2 = (int)U32(d, dataOffset - 4);
                    int element = param.Type == AampType.BufferBinary ? 1 : 4;
                    param.Data = d.AsSpan(dataOffset, count2 * element).ToArray();
                }
                else
                    param.Data = d.AsSpan(dataOffset, FixedSize(param.Type)).ToArray();
                obj.Params.Add(param);
            }
            return obj;
        }

        static int FixedSize(AampType type) =>
            type switch
            {
                AampType.Vec2 => 8,
                AampType.Vec3 => 12,
                AampType.Vec4 or AampType.Color or AampType.Quat => 16,
                AampType.Curve1 => 0x80,
                AampType.Curve2 => 0x100,
                AampType.Curve3 => 0x180,
                AampType.Curve4 => 0x200,
                _ => 4,
            };

        public byte[] Write()
        {
            var lists = new List<AampList>();
            var objects = new List<AampObject>();
            var parameters = new List<AampParam>();
            var listCursor = new Dictionary<AampList, int>(ReferenceEqualityComparer.Instance);
            var objectCursor = new Dictionary<AampList, int>(ReferenceEqualityComparer.Instance);
            lists.Add(Root);
            CollectLists(Root, lists, listCursor);
            CollectObjects(Root, objects, objectCursor);
            foreach (var o in objects)
                parameters.AddRange(o.Params);

            var typeBytes = Encoding.ASCII.GetBytes(DataType + "\0");
            int typeSize = (typeBytes.Length + 3) & ~3;
            int listStart = HeaderSize + typeSize;
            int objectStart = listStart + lists.Count * 12;
            int paramStart = objectStart + objects.Count * 8;
            int dataStart = paramStart + parameters.Count * 8;

            var listOffset = new Dictionary<AampList, int>(ReferenceEqualityComparer.Instance);
            var objectOffset = new Dictionary<AampObject, int>(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < lists.Count; i++)
                listOffset[lists[i]] = listStart + i * 12;
            for (int i = 0; i < objects.Count; i++)
                objectOffset[objects[i]] = objectStart + i * 8;

            //Values: equal bytes are stored once, each aligned to 4; a buffer carries its count first.
            var data = new List<byte>();
            var dataAt = new Dictionary<string, int>();
            var paramData = new int[parameters.Count];
            for (int i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                if (p.IsString)
                    continue;
                byte[] stored = p.IsBuffer
                    ? BitConverter
                        .GetBytes(p.Data.Length / (p.Type == AampType.BufferBinary ? 1 : 4))
                        .Concat(p.Data)
                        .ToArray()
                    : p.Data;
                string key = Convert.ToHexString(stored);
                if (!dataAt.TryGetValue(key, out int at))
                {
                    at = data.Count;
                    data.AddRange(stored);
                    while (data.Count % 4 != 0)
                        data.Add(0);
                    dataAt[key] = at;
                }
                paramData[i] = dataStart + at + (p.IsBuffer ? 4 : 0);
            }
            int stringStart = dataStart + data.Count;
            var strings = new List<byte>();
            var stringAt = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                if (!p.IsString)
                    continue;
                string s = p.Text ?? "";
                if (!stringAt.TryGetValue(s, out int at))
                {
                    at = strings.Count;
                    strings.AddRange(Encoding.UTF8.GetBytes(s));
                    strings.Add(0);
                    while (strings.Count % 4 != 0)
                        strings.Add(0);
                    stringAt[s] = at;
                }
                paramData[i] = stringStart + at;
            }

            int size = stringStart + strings.Count;
            var output = new byte[size];
            var span = output.AsSpan();
            Encoding.ASCII.GetBytes("AAMP").CopyTo(output, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 2);
            BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Flags);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x0C..], (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x10..], Version);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x14..], (uint)typeSize);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x18..], (uint)lists.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x1C..], (uint)objects.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x20..], (uint)parameters.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x24..], (uint)data.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(span[0x28..], (uint)strings.Count);
            typeBytes.CopyTo(output, HeaderSize);

            foreach (var list in lists)
            {
                int at = listOffset[list];
                int childLists = listStart + listCursor[list] * 12;
                int childObjects = objectStart + objectCursor[list] * 8;
                BinaryPrimitives.WriteUInt32LittleEndian(span[at..], list.Hash);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    span[(at + 4)..],
                    (ushort)((childLists - at) / 4)
                );
                BinaryPrimitives.WriteUInt16LittleEndian(
                    span[(at + 6)..],
                    (ushort)list.Lists.Count
                );
                BinaryPrimitives.WriteUInt16LittleEndian(
                    span[(at + 8)..],
                    (ushort)((childObjects - at) / 4)
                );
                BinaryPrimitives.WriteUInt16LittleEndian(
                    span[(at + 10)..],
                    (ushort)list.Objects.Count
                );
            }
            int paramIndex = 0;
            foreach (var obj in objects)
            {
                int at = objectOffset[obj];
                int first = paramStart + paramIndex * 8;
                BinaryPrimitives.WriteUInt32LittleEndian(span[at..], obj.Hash);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    span[(at + 4)..],
                    (ushort)((first - at) / 4)
                );
                BinaryPrimitives.WriteUInt16LittleEndian(
                    span[(at + 6)..],
                    (ushort)obj.Params.Count
                );
                paramIndex += obj.Params.Count;
            }
            for (int i = 0; i < parameters.Count; i++)
            {
                int at = paramStart + i * 8;
                BinaryPrimitives.WriteUInt32LittleEndian(span[at..], parameters[i].Hash);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    span[(at + 4)..],
                    (uint)((paramData[i] - at) / 4) | ((uint)parameters[i].Type << 24)
                );
            }
            data.CopyTo(output, dataStart);
            strings.CopyTo(output, stringStart);
            return output;
        }

        /// <summary>Lists in file order: a list's children together, each child's own after its siblings.</summary>
        static void CollectLists(
            AampList list,
            List<AampList> into,
            Dictionary<AampList, int> cursor
        )
        {
            cursor[list] = into.Count;
            into.AddRange(list.Lists);
            foreach (var child in list.Lists)
                CollectLists(child, into, cursor);
        }

        static void CollectObjects(
            AampList list,
            List<AampObject> into,
            Dictionary<AampList, int> cursor
        )
        {
            cursor[list] = into.Count;
            into.AddRange(list.Objects);
            foreach (var child in list.Lists)
                CollectObjects(child, into, cursor);
        }
    }
}
