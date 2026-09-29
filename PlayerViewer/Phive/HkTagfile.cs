using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// A Havok 2021 TAG0 tagfile, read into its reflected types and an object graph of
    /// <see cref="HkObject"/> and <see cref="HkArray"/>.
    /// </summary>
    public class HkTagfile
    {
        public const uint ItemSingle = 0x10000000,
            ItemArray = 0x20000000;

        public struct Item
        {
            public HkType Type;
            public uint Kind; //ItemSingle, ItemArray, or 0 for the null item
            public int Offset;
            public int Count;
        }

        byte[] _data = Array.Empty<byte>();
        readonly List<Item> _items = new();
        readonly List<HkType> _types = new() { null };

        readonly Dictionary<int, HkObject[]> _records = new();
        readonly Dictionary<int, HkArray> _arrays = new();
        readonly Dictionary<int, string> _strings = new();

        /// <summary>The SDKV payload: "20210100", or "20200100" for an older export.</summary>
        public string SdkVersion { get; private set; } = "";

        /// <summary>The SDKV section's payload bytes as read.</summary>
        public byte[] SdkVersionSection { get; private set; } = Array.Empty<byte>();

        /// <summary>The whole TYPE section, header included, as read.</summary>
        public byte[] TypeSection { get; private set; } = Array.Empty<byte>();

        /// <summary>Reflected types by id; id 0 and ids the file never declares are null.</summary>
        public IReadOnlyList<HkType> Types => _types;

        public IReadOnlyList<Item> Items => _items;

        /// <summary>Item count, the null item 0 included.</summary>
        public int ItemCount => _items.Count;

        /// <summary>The root object (item 1), an hkRootLevelContainer in every Phive file.</summary>
        public HkObject Root => Single(1);

        public static HkTagfile Read(byte[] tag)
        {
            var file = new HkTagfile();
            file.Load(tag);
            return file;
        }

        public HkType FindType(string name) =>
            _types.FirstOrDefault(t => t != null && t.Name == name);

        public string ItemTypeName(int index) =>
            index > 0 && index < _items.Count ? _items[index].Type?.Name ?? "" : "";

        #region reading

        static uint ReadBE32(byte[] d, int pos) =>
            BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos));

        void Load(byte[] data)
        {
            if (data.Length < 8 || Encoding.ASCII.GetString(data, 4, 4) != "TAG0")
                throw new InvalidOperationException("Not a TAG0 file");
            int tag0Size = (int)(ReadBE32(data, 0) & 0x3FFFFFFF);

            int pos = 8;
            int indxStart = -1,
                indxSize = 0;
            bool haveData = false,
                haveTypes = false;
            while (pos < tag0Size && pos + 8 <= data.Length)
            {
                int secSize = (int)(ReadBE32(data, pos) & 0x3FFFFFFF);
                if (secSize < 8)
                {
                    pos += 4;
                    continue;
                }
                string magic = Encoding.ASCII.GetString(data, pos + 4, 4);
                switch (magic)
                {
                    case "SDKV":
                        SdkVersionSection = data.AsSpan(pos + 8, secSize - 8).ToArray();
                        SdkVersion = Encoding.ASCII.GetString(SdkVersionSection).TrimEnd('\0');
                        break;
                    case "DATA":
                        _data = data.AsSpan(pos + 8, secSize - 8).ToArray();
                        haveData = true;
                        break;
                    case "TYPE":
                        TypeSection = data.AsSpan(pos, secSize).ToArray();
                        BuildTypes(HkTypeSection.Read(TypeSection));
                        haveTypes = true;
                        break;
                    case "INDX":
                        indxStart = pos + 8;
                        indxSize = secSize - 8;
                        break;
                }
                pos += secSize;
            }
            if (!haveData || !haveTypes || indxStart < 0)
                throw new InvalidOperationException("Missing TAG0 sections");
            ParseIndex(data, indxStart, indxSize);
        }

        //The reflected types of the parsed TYPE section, linked by id. The first body of an id wins.
        void BuildTypes(HkTypeSection section)
        {
            string TypeString(int index) =>
                index >= 0 && index < section.TypeStrings.Count ? section.TypeStrings[index] : "";
            var names = new List<(string Name, List<(string, ulong)> Templates)> { ("", null) };
            for (int id = 1; id < section.Names.Count; id++)
            {
                var n = section.Names[id];
                names.Add(
                    (
                        TypeString(n.NameIndex),
                        n.Templates.Select(t => (TypeString(t.NameIndex), t.Value)).ToList()
                    )
                );
            }

            var parentIds = new Dictionary<HkType, int>();
            var subtypeIds = new Dictionary<HkType, int>();
            var fieldTypeIds = new Dictionary<HkType, List<int>>();
            foreach (var body in section.Bodies)
            {
                while (_types.Count <= body.Id)
                    _types.Add(null);
                if (_types[body.Id] != null)
                    continue;
                var type = new HkType
                {
                    Id = body.Id,
                    OptBits = (uint)body.OptBits,
                    Format = (uint)body.Format,
                    Version = (uint)body.Version,
                    Size = (uint)body.Size,
                    Align = (uint)body.Align,
                    Flags = (uint)body.Flags,
                };
                var fieldIds = new List<int>();
                for (int i = 0; i < body.Fields.Count; i++)
                {
                    var f = body.Fields[i];
                    string name =
                        f.NameIndex >= 0 && f.NameIndex < section.FieldStrings.Count
                            ? section.FieldStrings[f.NameIndex]
                            : $"field{i}";
                    type.Fields.Add(new HkField(name, (uint)f.Flags, (int)f.Offset, null));
                    fieldIds.Add(f.TypeId);
                }
                if (type.Id < names.Count)
                {
                    type.Name = names[type.Id].Name;
                    type.Templates = names[type.Id].Templates ?? new();
                }
                _types[type.Id] = type;
                parentIds[type] = body.ParentId;
                subtypeIds[type] = body.SubtypeId;
                fieldTypeIds[type] = fieldIds;
            }

            //An id named in TNA1 with no body is an alias of its "f" suffixed type (hkVector4f).
            var byName = new Dictionary<string, HkType>(StringComparer.Ordinal);
            foreach (var t in _types)
                if (t != null && t.Name.Length > 0 && !byName.ContainsKey(t.Name))
                    byName[t.Name] = t;
            for (int id = 1; id < names.Count; id++)
            {
                while (_types.Count <= id)
                    _types.Add(null);
                if (_types[id] == null && names[id].Name.Length > 0)
                {
                    byName.TryGetValue(names[id].Name + "f", out var concrete);
                    _types[id] = new HkType
                    {
                        Id = id,
                        Name = names[id].Name,
                        Templates = names[id].Templates ?? new(),
                        Parent = concrete,
                        IsStub = true,
                    };
                }
            }

            var signatures = HkTypeCatalog.Signatures(section);
            for (int id = 1; id < names.Count && id < _types.Count; id++)
                if (_types[id] != null)
                    _types[id].Signature = signatures[id];

            foreach (var (type, id) in parentIds)
                type.Parent = TypeById(id);
            foreach (var (type, id) in subtypeIds)
                type.Subtype = TypeById(id);
            foreach (var (type, ids) in fieldTypeIds)
                for (int i = 0; i < type.Fields.Count; i++)
                    type.Fields[i] = type.Fields[i] with { Type = TypeById(ids[i]) };
        }

        HkType TypeById(int id) => id > 0 && id < _types.Count ? _types[id] : null;

        void ParseIndex(byte[] data, int pos, int size)
        {
            int end = pos + size;
            while (pos < end)
            {
                int subSize = (int)(ReadBE32(data, pos) & 0x3FFFFFFF);
                if (subSize == 0)
                    break;
                if (Encoding.ASCII.GetString(data, pos + 4, 4) == "ITEM")
                {
                    int count = (subSize - 8) / 12;
                    for (int i = 0; i < count; i++)
                    {
                        var e = data.AsSpan(pos + 8 + i * 12);
                        uint packed = BinaryPrimitives.ReadUInt32LittleEndian(e);
                        _items.Add(
                            new Item
                            {
                                Type = TypeById((int)(packed & 0xFFFFFF)),
                                Kind = packed & 0xF0000000,
                                Offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[4..]),
                                Count = (int)BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                            }
                        );
                    }
                }
                pos += subSize;
            }
        }

        #endregion

        #region decoding

        /// <summary>The records of a record item, decoded once and shared by every reference.</summary>
        public HkObject[] Records(int index)
        {
            if (_records.TryGetValue(index, out var cached))
                return cached;
            var item = _items[index];
            var type = item.Type;
            int size = type?.ByteSize ?? 0;
            if (type == null || type.EffectiveKind != HkKind.Record || size == 0)
            {
                _records[index] = Array.Empty<HkObject>();
                return _records[index];
            }
            //Registered first, so a pointer back into this item resolves to the same instances.
            var records = new HkObject[item.Count];
            for (int i = 0; i < records.Length; i++)
                records[i] = new HkObject(type) { SourceItem = index };
            _records[index] = records;
            for (int i = 0; i < records.Length; i++)
                FillRecord(records[i], item.Offset + i * size);
            return records;
        }

        /// <summary>A pointer's target: a single record, a string, or the item as an array.</summary>
        public object Pointer(int index)
        {
            if (index <= 0 || index >= _items.Count)
                return null;
            var item = _items[index];
            if (item.Type == null)
                return null;
            if (IsCharItem(item))
                return String(index);
            if (item.Type.EffectiveKind == HkKind.Record && item.Count == 1)
                return Records(index)[0];
            return ArrayItem(index, item.Type);
        }

        public HkObject Single(int index) => Pointer(index) as HkObject;

        /// <summary>The item holding each string, the first when a string is stored twice.</summary>
        public Dictionary<string, int> StringItems()
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 1; i < _items.Count; i++)
                if (_items[i].Type != null && IsCharItem(_items[i]))
                    map.TryAdd(String(i), i);
            return map;
        }

        static bool IsCharItem(Item item) =>
            item.Type.Resolved.Name == "char" && item.Type.EffectiveKind == HkKind.Int;

        string String(int index)
        {
            if (_strings.TryGetValue(index, out var s))
                return s;
            var item = _items[index];
            int len = 0;
            while (len < item.Count && _data[item.Offset + len] != 0)
                len++;
            s = Encoding.UTF8.GetString(_data, item.Offset, len);
            _strings[index] = s;
            return s;
        }

        /// <summary>An item as an hkArray's contents. <paramref name="elementType"/> names the element type of an unset array.</summary>
        HkArray ArrayItem(int index, HkType elementType)
        {
            if (index <= 0 || index >= _items.Count)
                return new HkArray(elementType, 0);
            if (_arrays.TryGetValue(index, out var cached))
                return cached;
            var item = _items[index];
            var type = item.Type ?? elementType;
            var array = new HkArray(type, item.Count) { SourceItem = index };
            _arrays[index] = array;
            if (type == null)
                return array;

            switch (type.EffectiveKind)
            {
                case HkKind.Record:
                    array.AddRange(Records(index));
                    break;
                case HkKind.Pointer:
                case HkKind.String:
                    for (int i = 0; i < item.Count; i++)
                        array.Add(
                            Pointer(
                                (int)
                                    BinaryPrimitives.ReadUInt64LittleEndian(
                                        _data.AsSpan(item.Offset + i * 8)
                                    )
                            )
                        );
                    break;
                default:
                    int size = type.ByteSize;
                    for (int i = 0; i < item.Count; i++)
                        array.Add(DecodeValue(item.Offset + i * size, type));
                    break;
            }
            return array;
        }

        void FillRecord(HkObject obj, int offset)
        {
            var fields = obj.Type.AllFields;
            for (int i = 0; i < fields.Count; i++)
                obj.Values[i] = DecodeValue(offset + fields[i].Offset, fields[i].Type);
        }

        object DecodeValue(int offset, HkType declared)
        {
            if (declared == null)
                return null;
            var type = declared.Resolved;
            switch (type.Kind)
            {
                case HkKind.Int:
                {
                    bool signed = type.IsSigned;
                    var span = _data.AsSpan(offset);
                    switch (type.IntBytes)
                    {
                        case 1:
                            return signed ? (sbyte)span[0] : span[0];
                        case 2:
                            return signed
                                ? BinaryPrimitives.ReadInt16LittleEndian(span)
                                : BinaryPrimitives.ReadUInt16LittleEndian(span);
                        case 4:
                            return signed
                                ? BinaryPrimitives.ReadInt32LittleEndian(span)
                                : BinaryPrimitives.ReadUInt32LittleEndian(span);
                        default:
                            return signed
                                ? BinaryPrimitives.ReadInt64LittleEndian(span)
                                : BinaryPrimitives.ReadUInt64LittleEndian(span);
                    }
                }
                case HkKind.Float:
                    return declared.ByteSize switch
                    {
                        8 => BitConverter.ToDouble(_data, offset),
                        2 => BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(_data, offset)),
                        _ => BitConverter.ToSingle(_data, offset),
                    };
                case HkKind.Bool:
                    return _data[offset] != 0;
                case HkKind.Pointer:
                case HkKind.String:
                    return Pointer(
                        (int)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(offset))
                    );
                case HkKind.Record:
                {
                    var obj = new HkObject(declared);
                    FillRecord(obj, offset);
                    return obj;
                }
                case HkKind.Array:
                {
                    int tupleCount = declared.TupleCount;
                    if (tupleCount > 0)
                        return DecodeTuple(offset, type, tupleCount);
                    //hkArray {u64 item, s32 size, s32 capacityAndFlags}: the item's count is the length.
                    int itemRef = (int)
                        BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(offset));
                    return ArrayItem(itemRef, type.Subtype);
                }
                default:
                {
                    int size = declared.ByteSize;
                    return size > 0 ? _data.AsSpan(offset, size).ToArray() : null;
                }
            }
        }

        /// <summary>A fixed inline tuple: its elements by the subtype, as float[] when they are reals.</summary>
        object DecodeTuple(int offset, HkType type, int count)
        {
            var element = type.Subtype;
            int elementSize = element?.ByteSize ?? 0;
            if (element == null || elementSize == 0)
            {
                //No element type: raw reals by the tuple's own size.
                int floats = type.ByteSize > 0 ? type.ByteSize / 4 : count;
                var raw = new float[floats];
                for (int i = 0; i < floats; i++)
                    raw[i] = BitConverter.ToSingle(_data, offset + i * 4);
                return raw;
            }
            if (element.EffectiveKind == HkKind.Float && elementSize == 4)
            {
                var f = new float[count];
                for (int i = 0; i < count; i++)
                    f[i] = BitConverter.ToSingle(_data, offset + i * 4);
                return f;
            }
            var values = new object[count];
            for (int i = 0; i < count; i++)
                values[i] = DecodeValue(offset + i * elementSize, element);
            return values;
        }

        #endregion

        /// <summary>Debug: the reflected type table, one line per type.</summary>
        public IEnumerable<string> DumpTypes()
        {
            foreach (var t in _types.Where(t => t != null))
                yield return $"type {t.Id}: '{t.Name}' kind={t.Kind} fmt=0x{t.Format:X} size={t.Size} parent='{t.Parent?.Name}' sub='{t.Subtype?.Name}' "
                    + $"fields=[{string.Join(",", t.Fields.Select(f => $"{f.Name}@{f.Offset}:{f.Type?.Name}"))}]";
        }
    }
}
