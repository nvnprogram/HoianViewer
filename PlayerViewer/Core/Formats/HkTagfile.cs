using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayerViewer.Core.Formats
{
    /// <summary>
    /// Read-only parser for the Havok TAG0 tagfile payload inside Splatoon 3 phive
    /// files (bphcl hair cloth etc).
    /// </summary>
    public class HkTagfile
    {
        //--- reflection info
        class TypeDef
        {
            public uint TypeId,
                ParentTypeId,
                Format,
                SubtypeId,
                Version,
                Size,
                Align,
                Flags,
                OptBits;
            public FormatKind Kind;
            public string Name = "";
            public List<(string Name, uint Flags, uint Offset, uint TypeId)> Fields = new();
        }

        enum FormatKind : uint
        {
            Void = 0,
            Opaque = 1,
            Bool = 2,
            String = 3,
            Int = 4,
            Float = 5,
            Pointer = 6,
            Record = 7,
            Array = 8,
        }

        class Item
        {
            public ushort TypeIndex;
            public byte Flags;
            public uint Offset;
            public uint Count;
        }

        const uint OPT_FORMAT = 1 << 0,
            OPT_SUBTYPE = 1 << 1,
            OPT_VERSION = 1 << 2,
            OPT_SIZE_ALIGN = 1 << 3,
            OPT_FLAGS = 1 << 4,
            OPT_DECLS = 1 << 5,
            OPT_INTERFACES = 1 << 6,
            OPT_ATTRIBUTE_STRING = 1 << 7,
            OPT_MUTABLE = 1 << 8;

        const int INT_SIGNED_BIT = 1 << 9;
        const int INT_NUM_BITS_SHIFT = 10;

        //Tuple types (hkVector4f etc.) store the element count in the high bits
        //of the format word (0x428 = tuple of 4, 0x1028 = tuple of 16).
        const int TUPLE_COUNT_SHIFT = 8;

        byte[] _data; //DATA section payload
        readonly List<Item> _items = new();
        readonly List<TypeDef> _types = new(); //indexed by typeId
        readonly List<string> _typeStrings = new();
        readonly List<string> _fieldStrings = new();

        //Decoded item cache (index -> object graph)
        readonly Dictionary<int, object> _decoded = new();

        public string SdkVersion { get; private set; } = "";

        /// <summary>Parses a TAG0 tagfile blob (already sliced out of the phive container).</summary>
        public static HkTagfile Parse(byte[] tag)
        {
            var file = new HkTagfile();
            file.Load(tag);
            return file;
        }

        /// <summary>
        /// Parses a whole .bphcl (Phive header wrapping a TAG0 section + AAMP).
        /// </summary>
        public static HkTagfile ParseBphcl(byte[] bphcl)
        {
            if (bphcl.Length < 0x30 || Encoding.ASCII.GetString(bphcl, 0, 5) != "Phive")
                throw new InvalidOperationException("Not a Phive file");
            uint tagOffset = BitConverter.ToUInt32(bphcl, 0x0C); //HktOffset
            uint tagSize = BitConverter.ToUInt32(bphcl, 0x18); //Section0 size
            var tag = new byte[tagSize];
            Array.Copy(bphcl, tagOffset, tag, 0, Math.Min(tagSize, bphcl.Length - tagOffset));
            return Parse(tag);
        }

        static uint ReadBE32(byte[] d, int pos) =>
            (uint)((d[pos] << 24) | (d[pos + 1] << 16) | (d[pos + 2] << 8) | d[pos + 3]);

        void Load(byte[] data)
        {
            if (ReadBE32(data, 4) != 0 && Encoding.ASCII.GetString(data, 4, 4) != "TAG0")
                throw new InvalidOperationException("Not a TAG0 file");
            uint tag0Size = ReadBE32(data, 0) & 0x3FFFFFFF;

            int pos = 8;
            int typeStart = -1,
                typeSize = 0,
                indxStart = -1,
                indxSize = 0;
            while (pos < tag0Size)
            {
                uint secSizeRaw = ReadBE32(data, pos);
                int secSize = (int)(secSizeRaw & 0x3FFFFFFF);
                if (secSize < 8)
                {
                    pos += 4;
                    continue;
                } //zero padding
                string magic = Encoding.ASCII.GetString(data, pos + 4, 4);
                switch (magic)
                {
                    case "SDKV":
                        SdkVersion = Encoding
                            .ASCII.GetString(data, pos + 8, secSize - 8)
                            .TrimEnd('\0');
                        break;
                    case "DATA":
                        _data = new byte[secSize - 8];
                        Array.Copy(data, pos + 8, _data, 0, secSize - 8);
                        break;
                    case "TYPE":
                        typeStart = pos;
                        typeSize = secSize;
                        break;
                    case "INDX":
                        indxStart = pos + 8;
                        indxSize = secSize - 8;
                        break;
                }
                pos += secSize;
            }
            if (_data == null || typeStart < 0 || indxStart < 0)
                throw new InvalidOperationException("Missing TAG0 sections");

            ParseTypeSection(data, typeStart, typeSize);
            ParseIndx(data, indxStart, indxSize);
        }

        void ParseTypeSection(byte[] data, int start, int size)
        {
            int pos = start + 8;
            int end = start + size;
            int tbdyPos = -1,
                tbdySize = 0;
            var tna1 = (pos: -1, size: 0);

            while (pos < end)
            {
                int subSize = (int)(ReadBE32(data, pos) & 0x3FFFFFFF);
                if (subSize == 0)
                    break;
                string magic = Encoding.ASCII.GetString(data, pos + 4, 4);
                int payload = pos + 8,
                    payloadSize = subSize - 8;
                switch (magic)
                {
                    case "TST1":
                        ReadStringTable(data, payload, payloadSize, _typeStrings);
                        break;
                    case "FST1":
                        ReadStringTable(data, payload, payloadSize, _fieldStrings);
                        break;
                    case "TNA1":
                        tna1 = (payload, payloadSize);
                        break;
                    case "TBDY":
                        tbdyPos = payload;
                        tbdySize = payloadSize;
                        break;
                }
                pos += subSize;
            }

            //TNA1: type name associations (typeIds are sequential from 1).
            //Everything is VLE encoded: count, then per type (nameId, templateCount,
            //then templateCount x (templateNameId, templateValue)).
            var typeNames = new List<string> { "" };
            if (tna1.pos >= 0)
            {
                var reader = new VleReader(data, tna1.pos, tna1.size);
                ulong count = reader.ReadVle();
                for (ulong i = 0; i + 1 < count && reader.HasMore; i++)
                {
                    ulong strIdx = reader.ReadVle();
                    ulong templateCount = reader.ReadVle();
                    typeNames.Add(
                        strIdx < (ulong)_typeStrings.Count ? _typeStrings[(int)strIdx] : ""
                    );
                    for (ulong t = 0; t < templateCount; t++)
                    {
                        reader.ReadVle();
                        reader.ReadVle();
                    }
                }
            }

            if (tbdyPos >= 0)
                ParseTbdy(data, tbdyPos, tbdySize, typeNames);

            //Some typeIds only exist as TNA1 name entries with no TBDY body
            //(aliases like hkVector4 -> hkVector4f). Create named stubs and
            //link each to the concrete float variant ("<name>f") when present
            //so kind/size resolution can walk through them.
            var byName = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var t in _types)
                if (t != null && t.Name.Length > 0 && !byName.ContainsKey(t.Name))
                    byName[t.Name] = t.TypeId;
            for (int id = 1; id < typeNames.Count; id++)
            {
                while (_types.Count <= id)
                    _types.Add(null);
                if (_types[id] == null && typeNames[id].Length > 0)
                {
                    byName.TryGetValue(typeNames[id] + "f", out uint concrete);
                    _types[id] = new TypeDef
                    {
                        TypeId = (uint)id,
                        Name = typeNames[id],
                        ParentTypeId = concrete,
                    };
                }
            }
        }

        static void ReadStringTable(byte[] data, int pos, int size, List<string> target)
        {
            int end = pos + size;
            while (pos < end)
            {
                int len = 0;
                while (pos + len < end && data[pos + len] != 0)
                    len++;
                target.Add(Encoding.UTF8.GetString(data, pos, len));
                pos += len + 1;
            }
        }

        /// <summary>Debug: TBDY entry order trace (typeId, start offset relative to TBDY).</summary>
        public List<(uint TypeId, int Offset)> TbdyTrace { get; } = new();

        /// <summary>Debug: raw TBDY payload bytes.</summary>
        public byte[] TbdyRaw { get; private set; }

        void ParseTbdy(byte[] data, int pos, int size, List<string> typeNames)
        {
            TbdyRaw = new byte[size];
            Array.Copy(data, pos, TbdyRaw, 0, size);
            var reader = new VleReader(data, pos, size);
            while (reader.HasMore)
            {
                int entryStart = reader.Position - pos;
                var type = new TypeDef();
                type.TypeId = (uint)reader.ReadVle();
                TbdyTrace.Add((type.TypeId, entryStart));
                type.ParentTypeId = (uint)reader.ReadVle();
                type.OptBits = (uint)reader.ReadVle();

                if ((type.OptBits & OPT_FORMAT) != 0)
                {
                    type.Format = (uint)reader.ReadVle();
                    type.Kind = (FormatKind)(type.Format & 0x1F);
                }
                if ((type.OptBits & OPT_SUBTYPE) != 0)
                    type.SubtypeId = (uint)reader.ReadVle();
                if ((type.OptBits & OPT_VERSION) != 0)
                    type.Version = (uint)reader.ReadVle();
                if ((type.OptBits & OPT_SIZE_ALIGN) != 0)
                {
                    type.Size = (uint)reader.ReadVle();
                    type.Align = (uint)reader.ReadVle();
                }
                if ((type.OptBits & OPT_FLAGS) != 0)
                    type.Flags = (uint)reader.ReadVle();
                if ((type.OptBits & OPT_DECLS) != 0)
                {
                    ulong encoded = reader.ReadVle();
                    uint numFields = (uint)(encoded & 0xFFFF);
                    for (uint i = 0; i < numFields; i++)
                    {
                        uint nameId = (uint)reader.ReadVle();
                        uint flags = (uint)reader.ReadVle();
                        //Flag bit 0x80 marks an extra VLE payload after the flags
                        //(seen on e.g. hkPackedVector3::values, hclCollidable::userData).
                        //Not consuming it desyncs the whole remaining type table.
                        if ((flags & 0x80) != 0)
                            reader.ReadVle();
                        uint offset = (uint)reader.ReadVle();
                        uint typeId = (uint)reader.ReadVle();
                        string name =
                            nameId < _fieldStrings.Count ? _fieldStrings[(int)nameId] : $"field{i}";
                        type.Fields.Add((name, flags, offset, typeId));
                    }
                }
                if ((type.OptBits & OPT_INTERFACES) != 0)
                {
                    uint numIfaces = (uint)reader.ReadVle();
                    for (uint i = 0; i < numIfaces; i++)
                    {
                        reader.ReadVle();
                        reader.ReadVle();
                    }
                }
                if ((type.OptBits & OPT_ATTRIBUTE_STRING) != 0)
                    reader.ReadVle();
                if ((type.OptBits & OPT_MUTABLE) != 0)
                    reader.ReadVle();

                type.Name = type.TypeId < typeNames.Count ? typeNames[(int)type.TypeId] : "";
                while (_types.Count <= type.TypeId)
                    _types.Add(null);
                //Keep the first declaration: trailing bytes after the last valid
                //entry can desync the reader and produce garbage duplicates.
                if (_types[(int)type.TypeId] == null)
                    _types[(int)type.TypeId] = type;
            }
        }

        void ParseIndx(byte[] data, int pos, int size)
        {
            int end = pos + size;
            while (pos < end)
            {
                int subSize = (int)(ReadBE32(data, pos) & 0x3FFFFFFF);
                if (subSize == 0)
                    break;
                string magic = Encoding.ASCII.GetString(data, pos + 4, 4);
                if (magic == "ITEM")
                {
                    int count = (subSize - 8) / 12;
                    for (int i = 0; i < count; i++)
                    {
                        int e = pos + 8 + i * 12;
                        _items.Add(
                            new Item
                            {
                                TypeIndex = BitConverter.ToUInt16(data, e),
                                Flags = data[e + 3],
                                Offset = BitConverter.ToUInt32(data, e + 4),
                                Count = BitConverter.ToUInt32(data, e + 8),
                            }
                        );
                    }
                }
                pos += subSize;
            }
        }

        #region type helpers

        TypeDef GetType(uint typeId) => typeId < _types.Count ? _types[(int)typeId] : null;

        uint TypeByteSize(uint typeId)
        {
            var t = GetType(typeId);
            if (t == null)
                return 0;
            if (t.Size > 0)
                return t.Size;
            return t.ParentTypeId != 0 ? TypeByteSize(t.ParentTypeId) : 0;
        }

        FormatKind EffectiveKind(uint typeId)
        {
            var t = GetType(typeId);
            if (t == null)
                return FormatKind.Void;
            if (t.Kind != FormatKind.Void)
                return t.Kind;
            return t.ParentTypeId != 0 ? EffectiveKind(t.ParentTypeId) : FormatKind.Void;
        }

        TypeDef EffectiveType(uint typeId)
        {
            var t = GetType(typeId);
            if (t == null)
                return null;
            if (t.Kind != FormatKind.Void || t.ParentTypeId == 0)
                return t;
            return EffectiveType(t.ParentTypeId);
        }

        TypeDef RecordType(uint typeId)
        {
            var t = EffectiveType(typeId);
            while (t != null && t.Fields.Count == 0 && t.ParentTypeId != 0)
                t = GetType(t.ParentTypeId);
            return t;
        }

        #endregion

        /// <summary>Debug: dumps the reflected type table.</summary>
        public IEnumerable<string> DumpTypes()
        {
            foreach (var t in _types.Where(t => t != null))
            {
                string parent = GetType(t.ParentTypeId)?.Name ?? "";
                yield return $"type {t.TypeId}: '{t.Name}' kind={t.Kind} fmt=0x{t.Format:X} size={t.Size} parent='{parent}' "
                    + $"fields=[{string.Join(",", t.Fields.Select(f => $"{f.Name}@{f.Offset}:{GetType(f.TypeId)?.Name}({f.TypeId})"))}]";
            }
        }

        #region decoding

        /// <summary>Item count (item 0 is the null item).</summary>
        public int ItemCount => _items.Count;

        public string ItemTypeName(int index) =>
            index > 0 && index < _items.Count ? GetType(_items[index].TypeIndex)?.Name ?? "" : "";

        /// <summary>
        /// Finds the first item of the given type name and returns its decoded records.
        /// </summary>
        public List<Dictionary<string, object>> FindItems(string typeName)
        {
            for (int i = 1; i < _items.Count; i++)
            {
                if (ItemTypeName(i) == typeName)
                    return DecodeItem(i) as List<Dictionary<string, object>>;
            }
            return null;
        }

        /// <summary>All decoded record groups of the given type (one list per item).</summary>
        public IEnumerable<(int index, List<Dictionary<string, object>> records)> AllItems(
            string typeName
        )
        {
            for (int i = 1; i < _items.Count; i++)
            {
                if (
                    ItemTypeName(i) == typeName
                    && DecodeItem(i) is List<Dictionary<string, object>> records
                )
                    yield return (i, records);
            }
        }

        /// <summary>
        /// Decodes an item into: List&lt;Dictionary&gt; (records), List&lt;numbers&gt;
        /// (int/float/bool arrays), string (char arrays), or null.
        /// </summary>
        public object DecodeItem(int index)
        {
            if (index <= 0 || index >= _items.Count)
                return null;
            if (_decoded.TryGetValue(index, out var cached))
                return cached;

            var item = _items[index];
            var kind = EffectiveKind(item.TypeIndex);
            var effType = EffectiveType(item.TypeIndex);
            uint elemSize = TypeByteSize(item.TypeIndex);

            object result = null;
            if (kind == FormatKind.Record && item.Count > 0 && elemSize > 0)
            {
                var records = new List<Dictionary<string, object>>((int)item.Count);
                _decoded[index] = records; //pre-register (cycles via pointers)
                var recType = RecordType(item.TypeIndex);
                for (uint i = 0; i < item.Count; i++)
                    records.Add(DecodeRecord((int)(item.Offset + i * elemSize), recType));
                return records;
            }
            if (kind == FormatKind.Array && item.Count > 0 && elemSize > 0)
            {
                //Item whose element type is a fixed tuple (e.g. hkVector4 array).
                int tupleCount = (int)(effType.Format >> TUPLE_COUNT_SHIFT);
                var list = new List<object>((int)item.Count);
                _decoded[index] = list;
                for (uint i = 0; i < item.Count; i++)
                    list.Add(DecodeTuple((int)(item.Offset + i * elemSize), effType, tupleCount));
                return list;
            }
            if (kind == FormatKind.Int && item.Count > 0)
            {
                uint numBits = effType.Format >> INT_NUM_BITS_SHIFT;
                if (numBits <= 8)
                {
                    //Heuristic string detection (matches the reference implementation).
                    bool isString = true;
                    for (uint i = 0; i < item.Count; i++)
                    {
                        byte c = _data[item.Offset + i];
                        if (c != 0 && (c < 0x20 || c > 0x7E))
                        {
                            isString = false;
                            break;
                        }
                    }
                    if (isString)
                    {
                        int len = 0;
                        while (len < item.Count && _data[item.Offset + len] != 0)
                            len++;
                        result = Encoding.ASCII.GetString(_data, (int)item.Offset, len);
                        _decoded[index] = result;
                        return result;
                    }
                }
                var list = new List<object>((int)item.Count);
                for (uint i = 0; i < item.Count; i++)
                    list.Add(DecodeValue((int)(item.Offset + i * elemSize), effType));
                _decoded[index] = list;
                return list;
            }
            if (
                (kind == FormatKind.Float || kind == FormatKind.Bool || kind == FormatKind.Opaque)
                && item.Count > 0
                && elemSize > 0
            )
            {
                var list = new List<object>((int)item.Count);
                for (uint i = 0; i < item.Count; i++)
                    list.Add(DecodeValue((int)(item.Offset + i * elemSize), effType));
                _decoded[index] = list;
                return list;
            }
            if ((kind == FormatKind.Pointer || kind == FormatKind.String) && item.Count > 0)
            {
                //Array of pointers: each element is a u64 item ref.
                var list = new List<object>((int)item.Count);
                _decoded[index] = list;
                for (uint i = 0; i < item.Count; i++)
                {
                    ulong itemRef = BitConverter.ToUInt64(_data, (int)(item.Offset + i * 8));
                    list.Add(itemRef != 0 ? DecodeItem((int)itemRef) : null);
                }
                return list;
            }

            _decoded[index] = result;
            return result;
        }

        List<object> FloatVector(int offset, int count)
        {
            var list = new List<object>(count);
            for (int i = 0; i < count; i++)
                list.Add(BitConverter.ToSingle(_data, offset + i * 4));
            return list;
        }

        /// <summary>
        /// Decodes a fixed inline tuple type (hkVector4f = 4 floats,
        /// hkTransformf = 16 floats, ...). Element type is the subtype.
        /// </summary>
        object DecodeTuple(int offset, TypeDef type, int tupleCount)
        {
            var elemType = EffectiveType(type.SubtypeId);
            uint elemSize = TypeByteSize(type.SubtypeId);
            if (elemType == null || elemSize == 0)
            {
                //Fall back to raw floats sized by the tuple's own byte size.
                uint size = type.Size > 0 ? type.Size : (uint)tupleCount * 4;
                return FloatVector(offset, (int)size / 4);
            }
            var list = new List<object>(tupleCount);
            for (int i = 0; i < tupleCount; i++)
            {
                int elemOffset = offset + i * (int)elemSize;
                if (EffectiveKind(type.SubtypeId) == FormatKind.Record)
                    list.Add(DecodeRecord(elemOffset, RecordType(type.SubtypeId)));
                else if (EffectiveKind(type.SubtypeId) == FormatKind.Array)
                {
                    var sub = EffectiveType(type.SubtypeId);
                    list.Add(DecodeTuple(elemOffset, sub, (int)(sub.Format >> TUPLE_COUNT_SHIFT)));
                }
                else
                    list.Add(DecodeValue(elemOffset, elemType));
            }
            return list;
        }

        Dictionary<string, object> DecodeRecord(int offset, TypeDef type)
        {
            var obj = new Dictionary<string, object>(StringComparer.Ordinal);
            if (type == null)
                return obj;

            //Include inherited fields (walk to root first).
            if (type.ParentTypeId != 0)
            {
                var parentRec = RecordType(type.ParentTypeId);
                if (parentRec != null && parentRec != type)
                    foreach (var kv in DecodeRecord(offset, parentRec))
                        obj[kv.Key] = kv.Value;
            }

            foreach (var field in type.Fields)
            {
                int fieldOffset = offset + (int)field.Offset;
                var kind = EffectiveKind(field.TypeId);
                if (kind == FormatKind.Record)
                {
                    obj[field.Name] = DecodeRecord(fieldOffset, RecordType(field.TypeId));
                }
                else if (kind == FormatKind.Array)
                {
                    var arrType = EffectiveType(field.TypeId);
                    int tupleCount = (int)(arrType.Format >> TUPLE_COUNT_SHIFT);
                    if (tupleCount > 0)
                    {
                        //Fixed inline tuple (hkVector4f, hkTransformf, hkMatrix...).
                        obj[field.Name] = DecodeTuple(fieldOffset, arrType, tupleCount);
                    }
                    else
                    {
                        //hkArray layout: {u64 itemRef, s32 size, s32 capacityAndFlags}.
                        //The item's own count is authoritative (each array gets its own
                        //item); m_size only trims when it's a valid smaller count.
                        ulong itemRef = BitConverter.ToUInt64(_data, fieldOffset);
                        int size = BitConverter.ToInt32(_data, fieldOffset + 8);
                        object decoded = itemRef != 0 ? DecodeItem((int)itemRef) : null;
                        if (size > 0)
                        {
                            if (
                                decoded is List<Dictionary<string, object>> recs
                                && recs.Count > size
                            )
                                decoded = recs.Take(size).ToList();
                            else if (decoded is List<object> vals && vals.Count > size)
                                decoded = vals.Take(size).ToList();
                        }
                        obj[field.Name] = decoded;
                    }
                }
                else
                {
                    obj[field.Name] = DecodeValue(fieldOffset, EffectiveType(field.TypeId));
                }
            }
            return obj;
        }

        object DecodeValue(int offset, TypeDef type)
        {
            if (type == null)
                return null;
            switch (type.Kind)
            {
                case FormatKind.Int:
                {
                    uint numBits = type.Format >> INT_NUM_BITS_SHIFT;
                    bool signed = (type.Format & INT_SIGNED_BIT) != 0;
                    if (numBits <= 8)
                        return signed ? (sbyte)_data[offset] : (object)_data[offset];
                    if (numBits <= 16)
                        return signed
                            ? BitConverter.ToInt16(_data, offset)
                            : (object)BitConverter.ToUInt16(_data, offset);
                    if (numBits <= 32)
                        return signed
                            ? BitConverter.ToInt32(_data, offset)
                            : (object)BitConverter.ToUInt32(_data, offset);
                    return signed
                        ? BitConverter.ToInt64(_data, offset)
                        : (object)BitConverter.ToUInt64(_data, offset);
                }
                case FormatKind.Float:
                    if (type.Size == 4)
                        return BitConverter.ToSingle(_data, offset);
                    if (type.Size == 8)
                        return BitConverter.ToDouble(_data, offset);
                    if (type.Size >= 12 && type.Size % 4 == 0) //hkVector4/matrix vector types
                        return FloatVector(offset, (int)type.Size / 4);
                    return (object)BitConverter.ToUInt16(_data, offset); //half, raw bits
                case FormatKind.Bool:
                    return _data[offset] != 0;
                case FormatKind.Pointer:
                case FormatKind.String:
                {
                    ulong itemRef = BitConverter.ToUInt64(_data, offset);
                    return itemRef != 0 ? DecodeItem((int)itemRef) : null;
                }
                default:
                {
                    //hkVector4f / hkTransformf / hkMatrix4f etc. resolve to opaque or
                    //void kinds but serialize as raw float blocks; decode by size.
                    uint size = type.Size > 0 ? type.Size : TypeByteSize(type.TypeId);
                    if (size >= 12 && size % 4 == 0 && size <= 64)
                        return FloatVector(offset, (int)size / 4);
                    return null;
                }
            }
        }

        #endregion

        class VleReader
        {
            readonly byte[] _d;
            int _pos;
            readonly int _end;

            public VleReader(byte[] d, int pos, int size)
            {
                _d = d;
                _pos = pos;
                _end = pos + size;
            }

            public bool HasMore => _pos < _end;
            public int Position => _pos;

            byte Next() => _pos < _end ? _d[_pos++] : (byte)0;

            public ulong ReadVle()
            {
                byte b0 = Next();
                if ((b0 & 0x80) == 0)
                    return b0;
                if ((b0 & 0xC0) == 0x80)
                    return ((ulong)(b0 & 0x3F) << 8) | Next();
                if ((b0 & 0xE0) == 0xC0)
                {
                    byte b1 = Next(),
                        b2 = Next();
                    return ((ulong)(b0 & 0x1F) << 16) | ((ulong)b1 << 8) | b2;
                }
                if ((b0 & 0xF8) == 0xE0)
                {
                    byte b1 = Next(),
                        b2 = Next(),
                        b3 = Next();
                    return ((ulong)(b0 & 0x0F) << 24) | ((ulong)b1 << 16) | ((ulong)b2 << 8) | b3;
                }
                if ((b0 & 0xF8) == 0xE8)
                {
                    byte b1 = Next(),
                        b2 = Next(),
                        b3 = Next(),
                        b4 = Next();
                    return ((ulong)(b0 & 0x07) << 32)
                        | ((ulong)b1 << 24)
                        | ((ulong)b2 << 16)
                        | ((ulong)b3 << 8)
                        | b4;
                }
                if ((b0 & 0xFE) == 0xF0)
                {
                    ulong v = 0;
                    for (int i = 0; i < 7; i++)
                        v = (v << 8) | Next();
                    return ((ulong)(b0 & 0x01) << 56) | v;
                }
                if (b0 == 0xF8)
                {
                    ulong v = 0;
                    for (int i = 0; i < 5; i++)
                        v = (v << 8) | Next();
                    return v;
                }
                if (b0 == 0xF9)
                {
                    ulong v = 0;
                    for (int i = 0; i < 8; i++)
                        v = (v << 8) | Next();
                    return v;
                }
                return 0;
            }
        }
    }
}
