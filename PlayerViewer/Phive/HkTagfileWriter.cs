using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// Serialises an object graph back into a TAG0 tagfile. Source items keep their index and DATA
    /// place, new ones follow in walk order, and the TYPE section is built for what the graph uses.
    /// </summary>
    public static class HkTagfileWriter
    {
        sealed class Node
        {
            public object Value; //HkObject (single item), HkArray or string
            public HkType Type;
            public uint Kind;
            public int Count;
            public int SourceIndex;
            public int Discovery;
            public int Index;
            public int Offset;
            public byte[] Data;
            public List<(int Offset, HkType FieldType, Node Target)> Slots = new();
        }

        /// <param name="typeSection">Types to know besides the stock catalogue; the source's own when null.</param>
        /// <param name="keepTypes">Write <paramref name="typeSection"/> as it is, with the source's type ids, instead of building one.</param>
        public static byte[] Write(
            HkTagfile source,
            HkObject root,
            byte[] typeSection = null,
            bool keepTypes = false
        )
        {
            var writer = new Walker(source);
            var rootNode = writer.NodeFor(root, null);
            writer.Drain();
            var nodes = writer.Nodes;

            //Source items first in their old order, new ones after in walk order.
            var ordered = nodes
                .OrderBy(n => n.SourceIndex > 0 ? 0 : 1)
                .ThenBy(n => n.SourceIndex)
                .ThenBy(n => n.Discovery)
                .ToList();
            for (int i = 0; i < ordered.Count; i++)
                ordered[i].Index = i + 1;
            if (rootNode.Index != 1)
                throw new InvalidOperationException("The root must stay item 1");

            foreach (var n in nodes)
                writer.Encode(n);

            //DATA: source items in their old places, new ones after.
            var layout = nodes
                .OrderBy(n => n.SourceIndex > 0 ? 0 : 1)
                .ThenBy(n => n.SourceIndex > 0 ? source.Items[n.SourceIndex].Offset : 0)
                .ThenBy(n => n.Index)
                .ToList();
            var data = new List<byte>();
            foreach (var n in layout)
            {
                //Records to their type's alignment, arrays to at least 16 and byte arrays to 2.
                int align = n.Type.Alignment;
                if (n.Kind == HkTagfile.ItemArray)
                    align =
                        n.Type.EffectiveKind == HkKind.Int && n.Type.ByteSize == 1
                            ? 2
                            : Math.Max(16, align);
                while (data.Count % align != 0)
                    data.Add(0);
                n.Offset = data.Count;
                data.AddRange(n.Data);
            }
            while (data.Count % 16 != 0)
                data.Add(0);

            Func<HkType, int> idOf = t => t.Id;
            var types = typeSection ?? source.TypeSection;
            if (!keepTypes)
            {
                var builder = new HkTypeBuilder(
                    HkTypeCatalog.Stock(source.SdkVersion).Overlay(HkTypeSection.Read(types))
                );
                foreach (var n in layout)
                {
                    builder.Write(n.Type.Signature);
                    //The copy program runs one instruction over each chunk of elements, strings last.
                    int size = Math.Max(1, n.Type.ByteSize);
                    foreach (
                        var slot in n
                            .Slots.Where(s => s.Target != null)
                            .OrderBy(s => s.FieldType.EffectiveKind == HkKind.String)
                            .ThenBy(s => s.Offset / size / 1024)
                            .ThenBy(s => s.Offset % size)
                            .ThenBy(s => s.Offset / size)
                    )
                        builder.Write(slot.FieldType.Signature);
                }
                if (builder.Missing.Count > 0)
                    throw new InvalidOperationException(
                        "No type information for " + string.Join(", ", builder.Missing.Distinct())
                    );
                idOf = t => builder.IdOf(t.Signature);
                types = builder.Section.Write();
            }

            var items = new byte[(ordered.Count + 1) * 12];
            foreach (var n in ordered)
            {
                var e = items.AsSpan(n.Index * 12);
                BinaryPrimitives.WriteUInt32LittleEndian(e, n.Kind | (uint)idOf(n.Type));
                BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)n.Offset);
                BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)n.Count);
            }

            //PTCH: every set pointer and array slot, grouped by the slot's declared type.
            var fixups = new SortedDictionary<int, List<int>>();
            foreach (var n in nodes)
            foreach (var (offset, fieldType, target) in n.Slots)
                if (target != null)
                {
                    if (!fixups.TryGetValue(idOf(fieldType), out var list))
                        fixups[idOf(fieldType)] = list = new List<int>();
                    list.Add(n.Offset + offset);
                }
            var patch = new List<byte>();
            foreach (var (typeId, offsets) in fixups)
            {
                offsets.Sort();
                AppendLE(patch, (uint)typeId);
                AppendLE(patch, (uint)offsets.Count);
                foreach (int o in offsets)
                    AppendLE(patch, (uint)o);
            }

            var indx = Section("ITEM", items, 1)
                .Concat(Section("PTCH", patch.ToArray(), 1))
                .ToArray();
            var body = Section("SDKV", source.SdkVersionSection, 1)
                .Concat(Section("DATA", data.ToArray(), 1))
                .Concat(types)
                .Concat(Section("INDX", indx, 0))
                .ToArray();
            return Section("TAG0", body, 0);
        }

        static void AppendLE(List<byte> list, uint value)
        {
            list.Add((byte)value);
            list.Add((byte)(value >> 8));
            list.Add((byte)(value >> 16));
            list.Add((byte)(value >> 24));
        }

        static byte[] Section(string magic, byte[] body, uint flags)
        {
            var output = new byte[body.Length + 8];
            BinaryPrimitives.WriteUInt32BigEndian(
                output,
                ((uint)(body.Length + 8) & 0x3FFFFFFF) | (flags << 30)
            );
            Encoding.ASCII.GetBytes(magic).CopyTo(output, 4);
            body.CopyTo(output, 8);
            return output;
        }

        /// <summary>A number already in the CLR type of its field, written at that type's width.</summary>
        static void WriteNumber(Span<byte> span, object value)
        {
            switch (value)
            {
                case null:
                    return;
                case sbyte v:
                    span[0] = (byte)v;
                    return;
                case byte v:
                    span[0] = v;
                    return;
                case short v:
                    BinaryPrimitives.WriteInt16LittleEndian(span, v);
                    return;
                case ushort v:
                    BinaryPrimitives.WriteUInt16LittleEndian(span, v);
                    return;
                case int v:
                    BinaryPrimitives.WriteInt32LittleEndian(span, v);
                    return;
                case uint v:
                    BinaryPrimitives.WriteUInt32LittleEndian(span, v);
                    return;
                case long v:
                    BinaryPrimitives.WriteInt64LittleEndian(span, v);
                    return;
                case ulong v:
                    BinaryPrimitives.WriteUInt64LittleEndian(span, v);
                    return;
                case double v:
                    BinaryPrimitives.WriteDoubleLittleEndian(span, v);
                    return;
                case System.Half v:
                    BinaryPrimitives.WriteHalfLittleEndian(span, v);
                    return;
                case float v:
                    BinaryPrimitives.WriteSingleLittleEndian(span, v);
                    return;
                default:
                    throw new InvalidCastException($"Not a number: {value.GetType().Name}");
            }
        }

        sealed class Walker
        {
            readonly HkTagfile _source;
            readonly Dictionary<string, int> _stringItems;
            readonly HkType _charType;
            readonly Dictionary<object, Node> _byObject = new(ReferenceEqualityComparer.Instance);
            readonly Dictionary<string, Node> _byString = new(StringComparer.Ordinal);
            readonly Queue<Node> _pending = new();
            public readonly List<Node> Nodes = new();

            public Walker(HkTagfile source)
            {
                _source = source;
                _stringItems = source.StringItems();
                _charType = source.FindType("char");
            }

            public void Drain()
            {
                while (_pending.Count > 0)
                    Visit(_pending.Dequeue());
            }

            /// <summary>The node a pointer or array slot refers to, registered on first sight; null for nothing.</summary>
            public Node NodeFor(object value, HkType declared)
            {
                switch (value)
                {
                    case null:
                        return null;
                    case string s:
                        if (_byString.TryGetValue(s, out var sn))
                            return sn;
                        var bytes = Encoding.UTF8.GetBytes(s + "\0");
                        sn = Add(
                            s,
                            _charType ?? throw new InvalidOperationException("No char type"),
                            HkTagfile.ItemArray,
                            bytes.Length,
                            _stringItems.GetValueOrDefault(s)
                        );
                        sn.Data = bytes;
                        _byString[s] = sn;
                        return sn;
                    case HkArray a:
                        if (a.Count == 0)
                            return null;
                        if (_byObject.TryGetValue(a, out var an))
                            return an;
                        var elementType = a.ElementType ?? declared?.Resolved.Subtype;
                        if (elementType == null)
                            throw new InvalidOperationException("Array without an element type");
                        an = Add(
                            a,
                            elementType,
                            SourceKind(a.SourceItem, HkTagfile.ItemArray),
                            a.Count,
                            a.SourceItem
                        );
                        _byObject[a] = an;
                        return an;
                    case HkObject o:
                        if (_byObject.TryGetValue(o, out var on))
                            return on;
                        on = Add(
                            o,
                            o.Type,
                            SourceKind(o.SourceItem, HkTagfile.ItemSingle),
                            1,
                            o.SourceItem
                        );
                        _byObject[o] = on;
                        return on;
                    default:
                        throw new InvalidOperationException(
                            $"Cannot point at a {value.GetType().Name}"
                        );
                }
            }

            uint SourceKind(int sourceItem, uint fallback) =>
                sourceItem > 0 && sourceItem < _source.ItemCount
                    ? _source.Items[sourceItem].Kind
                    : fallback;

            Node Add(object value, HkType type, uint kind, int count, int sourceIndex)
            {
                //A source index only holds when the item still has the shape it was read with.
                if (sourceIndex > 0)
                {
                    var item = _source.Items[sourceIndex];
                    if (
                        item.Count != count
                        || item.Type != type
                        || Nodes.Any(n => n.SourceIndex == sourceIndex)
                    )
                        sourceIndex = 0;
                }
                var node = new Node
                {
                    Value = value,
                    Type = type,
                    Kind = kind,
                    Count = count,
                    SourceIndex = sourceIndex,
                    Discovery = Nodes.Count,
                };
                Nodes.Add(node);
                _pending.Enqueue(node);
                return node;
            }

            //Collect what a node points at, so every target has a node before encoding.
            void Visit(Node node)
            {
                switch (node.Value)
                {
                    case HkObject o:
                        VisitRecord(o);
                        break;
                    case HkArray a:
                        foreach (var element in a)
                            VisitValue(element, node.Type);
                        break;
                }
            }

            void VisitRecord(HkObject o)
            {
                var fields = o.Type.AllFields;
                for (int i = 0; i < fields.Count; i++)
                    VisitValue(o.Values[i], fields[i].Type);
            }

            void VisitValue(object value, HkType type)
            {
                if (type == null || value == null)
                    return;
                var r = type.Resolved;
                switch (r.Kind)
                {
                    case HkKind.Pointer:
                    case HkKind.String:
                        NodeFor(value, type);
                        break;
                    case HkKind.Record:
                        if (value is HkObject inline)
                            VisitRecord(inline);
                        break;
                    case HkKind.Array:
                        if (type.TupleCount > 0)
                        {
                            if (value is object[] parts)
                                foreach (var p in parts)
                                    VisitValue(p, r.Subtype);
                        }
                        else
                            NodeFor(value, type);
                        break;
                }
            }

            public void Encode(Node node)
            {
                if (node.Data != null)
                    return;
                int size = node.Type.ByteSize;
                var buffer = new byte[size * node.Count];
                switch (node.Value)
                {
                    case HkObject o:
                        EncodeRecord(node, buffer, 0, o);
                        break;
                    case HkArray a:
                        for (int i = 0; i < a.Count; i++)
                            EncodeValue(node, buffer, i * size, node.Type, a[i]);
                        break;
                }
                node.Data = buffer;
            }

            void EncodeRecord(Node node, byte[] buffer, int offset, HkObject o)
            {
                var fields = o.Type.AllFields;
                for (int i = 0; i < fields.Count; i++)
                    EncodeValue(
                        node,
                        buffer,
                        offset + fields[i].Offset,
                        fields[i].Type,
                        o.Values[i]
                    );
            }

            void EncodeValue(Node node, byte[] buffer, int offset, HkType declared, object value)
            {
                if (declared == null)
                    return;
                var type = declared.Resolved;
                var span = buffer.AsSpan(offset);
                switch (type.Kind)
                {
                    case HkKind.Int:
                    case HkKind.Float:
                        WriteNumber(span, HkValue.Coerce(value, declared));
                        return;
                    case HkKind.Bool:
                        span[0] = value is bool b && b ? (byte)1 : (byte)0;
                        return;
                    case HkKind.Pointer:
                    case HkKind.String:
                    {
                        var target = NodeFor(value, declared);
                        BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)(target?.Index ?? 0));
                        node.Slots.Add((offset, declared, target));
                        return;
                    }
                    case HkKind.Record:
                        if (value is HkObject inline)
                            EncodeRecord(node, buffer, offset, inline);
                        return;
                    case HkKind.Array:
                    {
                        int tupleCount = declared.TupleCount;
                        if (tupleCount > 0)
                        {
                            var element = type.Subtype;
                            int elementSize = element?.ByteSize ?? 4;
                            if (value is float[] f)
                                for (
                                    int i = 0;
                                    i < f.Length && i * 4 + 4 <= buffer.Length - offset;
                                    i++
                                )
                                    BinaryPrimitives.WriteSingleLittleEndian(span[(i * 4)..], f[i]);
                            else if (value is object[] parts)
                                for (int i = 0; i < parts.Length; i++)
                                    EncodeValue(
                                        node,
                                        buffer,
                                        offset + i * elementSize,
                                        element,
                                        parts[i]
                                    );
                            return;
                        }
                        var target = NodeFor(value, declared);
                        BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)(target?.Index ?? 0));
                        node.Slots.Add((offset, declared, target));
                        return;
                    }
                    default:
                        if (value is byte[] raw)
                            raw.CopyTo(span);
                        return;
                }
            }
        }
    }
}
