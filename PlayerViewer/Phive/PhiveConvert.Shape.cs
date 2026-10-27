using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayerViewer.Phive
{
    public static partial class PhiveConvert
    {
        static byte[] Shape(byte[] data, bool to2025)
        {
            if (!PhiveFile.IsPhive(data))
                throw new ConvertException("Not a Phive file.");
            PhiveShapeFile file;
            try
            {
                file = PhiveShapeFile.Read(data);
            }
            catch (InvalidOperationException)
            {
                throw new ConvertException("Not a shape file.");
            }
            var tag = ReadSource(file.Tagfile, to2025);
            if (tag.ItemTypeName(1) != "hknpMeshShape")
                throw new ConvertException("Not a mesh shape file.");
            var mesh = Mesh.Read(TagSection(file.Tagfile, "DATA"), !to2025);
            if (to2025)
                mesh.To2025();
            else
                mesh.ToSplatoon3();
            file.Filters = ConvertFilters(file, to2025);
            file.Tagfile = mesh.Write(to2025);
            return file.Write();
        }

        static byte[] TagSection(byte[] tag, string magic)
        {
            int end = (int)(BinaryPrimitives.ReadUInt32BigEndian(tag) & 0x3FFFFFFF);
            for (int pos = 8; pos + 8 <= Math.Min(end, tag.Length); )
            {
                int size = (int)(BinaryPrimitives.ReadUInt32BigEndian(tag.AsSpan(pos)) & 0x3FFFFFFF);
                if (size < 8)
                    break;
                if (Encoding.ASCII.GetString(tag, pos + 4, 4) == magic)
                    return tag.AsSpan(pos + 8, size - 8).ToArray();
                pos += size;
            }
            throw new ConvertException($"The shape has no {magic} section.");
        }

        //2025.1 widens each entry to two u64 and sets mask bits 30 and 31 where 2021.1 clears them.
        static byte[] ConvertFilters(PhiveShapeFile file, bool to2025)
        {
            int count = file.MaterialCount;
            if (file.Filters.Length < count * file.FilterSize)
                throw new ConvertException("The collision filter table is short.");
            int width = to2025 ? 16 : 8;
            var output = new byte[count * width];
            for (int i = 0; i < count; i++)
            {
                var (layers, mask) = file.Filter(i);
                var e = output.AsSpan(i * width);
                if (to2025)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(e, layers == uint.MaxValue ? ulong.MaxValue : layers);
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        e[8..],
                        mask == uint.MaxValue ? ulong.MaxValue : mask | 0xC0000000
                    );
                }
                else
                {
                    if ((layers != ulong.MaxValue && layers > uint.MaxValue) || (mask != ulong.MaxValue && mask > uint.MaxValue))
                        throw new ConvertException("A collision filter uses layers Splatoon 3 lacks.");
                    BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)layers);
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        e[4..],
                        mask == ulong.MaxValue ? uint.MaxValue : (uint)mask & 0x3FFFFFFF
                    );
                }
            }
            return output;
        }

        /// <summary>
        /// An hknpMeshShape as plain data: the tagfile's relative arrays have no pointers for the
        /// generic writer to follow, so the DATA section is read and laid out here.
        /// </summary>
        sealed class Mesh
        {
            //Field offsets of hknpMeshShape (2021.1 v1, 2025.1 v4).
            sealed record Layout(
                int Props,
                int UserData,
                int Codec,
                int Conversion,
                int TagTable,
                int Tree,
                int Compact,
                int Sections,
                byte Type,
                byte Dispatch,
                ushort Flags
            );

            static readonly Layout Layout2021 = new(48, 40, 64, 80, 112, 120, 136, 144, 8, 3, 4);
            static readonly Layout Layout2025 = new(32, 48, 56, 64, 96, 104, 132, 136, 7, 2, 0);

            const int ShapeSize = 160,
                NodeSize = 128,
                SectionSize = 64,
                FloatOffset = int.MaxValue;
            const uint ItemSingle = 0x10000000,
                ItemArray = 0x20000000;

            sealed class Node
            {
                public byte[] Aabb;
                public uint[] Data = new uint[4];
                public bool Leaf;

                float At(int vector, int slot) =>
                    BinaryPrimitives.ReadSingleLittleEndian(Aabb.AsSpan(vector * 16 + slot * 4));

                public bool Used(int slot) =>
                    At(0, slot) <= At(1, slot) && At(2, slot) <= At(3, slot) && At(4, slot) <= At(5, slot);

                public int UsedCount => Enumerable.Range(0, 4).Count(Used);

                /// <summary>The lower bound over the used slots for an even vector (lx, ly, lz), the upper for an odd one.</summary>
                public float Bound(int vector)
                {
                    var values = Enumerable.Range(0, 4).Where(Used).Select(j => At(vector, j));
                    return vector % 2 == 0 ? values.Min() : values.Max();
                }
            }

            sealed class Section
            {
                public byte[] Bvh,
                    Primitives,
                    Vertices,
                    Interior,
                    Placement;
                public float[] Floats;

                public int PrimitiveCount => Primitives.Length / 4;

                public bool Quad(int k) => Primitives[k * 4 + 2] != Primitives[k * 4 + 3];
            }

            byte[] _base;
            byte _keyBits;
            ulong _userData;
            uint _codec;
            byte[] _conversion;
            byte[] _tagTable;
            readonly List<Node> _nodes = new();
            bool _compact;
            readonly List<Section> _sections = new();

            public static Mesh Read(byte[] data, bool is2025)
            {
                var l = is2025 ? Layout2025 : Layout2021;
                if (data.Length < ShapeSize)
                    throw new ConvertException("The shape data is short.");
                int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
                if (data[24] != l.Type || data[25] != l.Dispatch || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26)) != l.Flags)
                    throw new ConvertException("The mesh shape has settings no stock file uses.");
                if (I32(l.Props + 8) != 0)
                    throw new ConvertException("The shape carries shape properties, which the converter does not handle.");
                if (!is2025 && BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(32)) != 0)
                    throw new ConvertException("The shape has a convex radius, which Raiders lacks.");
                if (is2025 && (I32(120) != 1 || I32(124) != 0 || I32(128) != 0))
                    throw new ConvertException("The shape's tree is not compact.");
                if (is2025 && BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(144)) != 0)
                    throw new ConvertException("The shape maps its primitives, which Splatoon 3 cannot.");

                var mesh = new Mesh
                {
                    _base = data.AsSpan(0, 24).ToArray(),
                    _keyBits = data[28],
                    _userData = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(l.UserData)),
                    _codec = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(l.Codec)),
                    _conversion = data.AsSpan(l.Conversion, 32).ToArray(),
                    _tagTable = View(data, l.TagTable, 8),
                    _compact = data[l.Compact] != 0,
                };

                long nodeOffset = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(l.Tree));
                int nodeCount = I32(l.Tree + 8);
                for (int i = 0; i < nodeCount; i++)
                {
                    int a = checked((int)(l.Tree + nodeOffset) + i * NodeSize);
                    var node = new Node { Aabb = data.AsSpan(a, 96).ToArray() };
                    for (int j = 0; j < 4; j++)
                        node.Data[j] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(a + 96 + j * 4));
                    if (is2025)
                    {
                        if (I32(a + 112) != 0 || (data[a + 116] & ~1) != 0)
                            throw new ConvertException("The shape's tree uses filter masks, which Splatoon 3 lacks.");
                        node.Leaf = (data[a + 116] & 1) != 0;
                    }
                    else
                    {
                        if (data[a + 113] != 0)
                            throw new ConvertException("The shape's tree has inactive nodes.");
                        node.Leaf = data[a + 112] != 0;
                    }
                    mesh._nodes.Add(node);
                }
                if (nodeCount < 2)
                    throw new ConvertException("The shape has no tree.");

                int sectionAt = l.Sections + I32(l.Sections);
                int sectionCount = I32(l.Sections + 4);
                for (int i = 0; i < sectionCount; i++)
                {
                    int a = sectionAt + i * SectionSize;
                    var s = new Section
                    {
                        Bvh = View(data, a, 28),
                        Primitives = View(data, a + 8, 4),
                        Interior = View(data, a + 24, 1),
                        Placement = data.AsSpan(a + 32, 32).ToArray(),
                    };
                    var vertices = View(data, a + 16, is2025 ? 1 : 6);
                    if (is2025 && I32(a + 32) == FloatOffset)
                    {
                        if (vertices.Length % 12 != 0)
                            throw new ConvertException("A section's vertices are malformed.");
                        s.Floats = new float[vertices.Length / 4];
                        for (int k = 0; k < s.Floats.Length; k++)
                            s.Floats[k] = BinaryPrimitives.ReadSingleLittleEndian(vertices.AsSpan(k * 4));
                    }
                    else
                    {
                        //2025.1 pads the last section's vertices by two bytes.
                        if (vertices.Length % 6 != 0 && !(is2025 && vertices.Length % 6 == 2))
                            throw new ConvertException("A section's vertices are malformed.");
                        s.Vertices = vertices.AsSpan(0, vertices.Length / 6 * 6).ToArray();
                    }
                    mesh._sections.Add(s);
                }
                return mesh;
            }

            static byte[] View(byte[] data, int at, int unit)
            {
                int offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
                int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at + 4));
                return count == 0 ? Array.Empty<byte>() : data.AsSpan(at + offset, count * unit).ToArray();
            }

            /// <summary>2021.1 flags whole primitives as interior, 2025.1 each triangle of one.</summary>
            public void To2025()
            {
                foreach (var s in _sections)
                {
                    int n = s.PrimitiveCount;
                    var bits = new byte[(2 * n + 7) / 8];
                    for (int k = 0; k < n; k++)
                        if (Bit(s.Interior, k))
                        {
                            Set(bits, 2 * k);
                            if (s.Quad(k))
                                Set(bits, 2 * k + 1);
                        }
                    s.Interior = bits;
                }
                //A lone section sits under an inner root in 2025.1 and in a leaf root in 2021.1.
                if (_nodes[1].Leaf && _nodes[1].UsedCount == 1)
                {
                    foreach (var n in _nodes.Skip(1).Where(n => !n.Leaf))
                        for (int j = 0; j < 4; j++)
                            if (n.Used(j))
                                n.Data[j]++;
                    var root = new Node { Aabb = (byte[])_nodes[0].Aabb.Clone() };
                    for (int v = 0; v < 6; v++)
                        BinaryPrimitives.WriteSingleLittleEndian(root.Aabb.AsSpan(v * 16), _nodes[1].Bound(v));
                    root.Data[0] = 2;
                    _nodes.Insert(1, root);
                }
            }

            public void ToSplatoon3()
            {
                foreach (var s in _sections)
                {
                    int n = s.PrimitiveCount;
                    var bits = new byte[(n + 7) / 8];
                    for (int k = 0; k < n; k++)
                        if (Bit(s.Interior, 2 * k) && (!s.Quad(k) || Bit(s.Interior, 2 * k + 1)))
                            Set(bits, k);
                    s.Interior = bits;
                    if (s.Floats != null)
                        Quantize(s);
                }
                if (!_nodes[1].Leaf && _nodes[1].UsedCount == 1)
                {
                    if (_nodes[1].Data[0] != 2)
                        throw new ConvertException("The shape's tree is out of order.");
                    _nodes.RemoveAt(1);
                    foreach (var n in _nodes.Skip(1).Where(n => !n.Leaf))
                        for (int j = 0; j < 4; j++)
                            if (n.Used(j))
                                n.Data[j]--;
                }
            }

            //2021.1 stores only 16 bit vertices: the section offset in units of the shape's scale, then the remainder.
            void Quantize(Section s)
            {
                int count = s.Floats.Length / 3;
                var offset = new int[3];
                for (int axis = 0; axis < 3; axis++)
                {
                    float scale = BinaryPrimitives.ReadSingleLittleEndian(_conversion.AsSpan(axis * 4));
                    double min = double.MaxValue;
                    for (int k = 0; k < count; k++)
                        min = Math.Min(min, Math.Round((double)s.Floats[k * 3 + axis] * scale));
                    offset[axis] = count > 0 ? (int)min : 0;
                }
                var q = new byte[count * 6];
                for (int k = 0; k < count; k++)
                    for (int axis = 0; axis < 3; axis++)
                    {
                        float scale = BinaryPrimitives.ReadSingleLittleEndian(_conversion.AsSpan(axis * 4));
                        double v = Math.Round((double)s.Floats[k * 3 + axis] * scale) - offset[axis];
                        if (v < 0 || v > ushort.MaxValue)
                            throw new ConvertException("A section is too large for Splatoon 3's vertex precision.");
                        BinaryPrimitives.WriteUInt16LittleEndian(q.AsSpan(k * 6 + axis * 2), (ushort)v);
                    }
                for (int axis = 0; axis < 3; axis++)
                    BinaryPrimitives.WriteInt32LittleEndian(s.Placement.AsSpan(axis * 4), offset[axis]);
                s.Vertices = q;
                s.Floats = null;
            }

            static bool Bit(byte[] bits, int i) => i / 8 < bits.Length && (bits[i / 8] >> (i % 8) & 1) != 0;

            static void Set(byte[] bits, int i) => bits[i / 8] |= (byte)(1 << (i % 8));

            /// <summary>The tagfile: items in the order Havok's writer lays them out, a TYPE section built for them.</summary>
            public byte[] Write(bool is2025)
            {
                var l = is2025 ? Layout2025 : Layout2021;
                string nodeType = is2025 ? "hkcdSimdTreeNamespace::Node" : "hkcdSimdTree::Node";
                string vertexType = is2025 ? "hkUint8" : "hknpMeshShape::GeometrySection::Vertex16_3";
                var data = new List<byte>(new byte[ShapeSize]);
                var items = new List<(string Type, uint Kind, int Offset, int Count)>
                {
                    ("hknpMeshShape", ItemSingle, 0, 1),
                };

                void Put32(int at, int value)
                {
                    for (int b = 0; b < 4; b++)
                        data[at + b] = (byte)(value >> (8 * b));
                }
                void Put64(int at, long value)
                {
                    Put32(at, (int)value);
                    Put32(at + 4, (int)(value >> 32));
                }
                void PutBytes(int at, byte[] bytes)
                {
                    for (int b = 0; b < bytes.Length; b++)
                        data[at + b] = bytes[b];
                }
                int Append(byte[] bytes)
                {
                    while (data.Count % 16 != 0)
                        data.Add(0);
                    int at = data.Count;
                    data.AddRange(bytes);
                    return at;
                }
                void PutArray(int field, byte[] bytes, int unit, string type)
                {
                    if (bytes.Length == 0)
                        return;
                    int at = Append(bytes);
                    items.Add((type, ItemArray, at, bytes.Length / unit));
                    Put32(field, at - field);
                    Put32(field + 4, bytes.Length / unit);
                }

                PutBytes(0, _base);
                data[24] = l.Type;
                data[25] = l.Dispatch;
                data[26] = (byte)l.Flags;
                data[27] = (byte)(l.Flags >> 8);
                data[28] = _keyBits;
                Put64(l.UserData, (long)_userData);
                Put32(l.Codec, (int)_codec);
                PutBytes(l.Conversion, _conversion);
                PutArray(l.TagTable, _tagTable, 8, "hknpMeshShape::ShapeTagTableEntry");

                var (parent, leaves, depth) = Derive();
                var nodes = new byte[_nodes.Count * NodeSize];
                for (int i = 0; i < _nodes.Count; i++)
                {
                    var n = _nodes[i];
                    var e = nodes.AsSpan(i * NodeSize);
                    n.Aabb.CopyTo(e);
                    for (int j = 0; j < 4; j++)
                        BinaryPrimitives.WriteUInt32LittleEndian(e[(96 + j * 4)..], n.Data[j]);
                    if (i == 0)
                        continue;
                    if (is2025)
                    {
                        e[116] = (byte)(n.Leaf ? 1 : 0);
                        e[117] = (byte)depth[i];
                        BinaryPrimitives.WriteUInt32LittleEndian(e[120..], (uint)parent[i]);
                        BinaryPrimitives.WriteUInt32LittleEndian(e[124..], (uint)leaves[i]);
                    }
                    else
                        e[112] = (byte)(n.Leaf ? 1 : 0);
                }
                int nodeAt = Append(nodes);
                items.Add((nodeType, ItemArray, nodeAt, _nodes.Count));
                Put64(l.Tree, nodeAt - l.Tree);
                Put32(l.Tree + 8, _nodes.Count);
                if (is2025)
                    Put32(120, 1);
                data[l.Compact] = (byte)(_compact ? 1 : 0);

                int sectionAt = 0;
                if (_sections.Count > 0)
                {
                    sectionAt = Append(new byte[_sections.Count * SectionSize]);
                    items.Add(("hknpMeshShape::GeometrySection", ItemArray, sectionAt, _sections.Count));
                    Put32(l.Sections, sectionAt - l.Sections);
                    Put32(l.Sections + 4, _sections.Count);
                }
                for (int i = 0; i < _sections.Count; i++)
                    PutBytes(sectionAt + i * SectionSize + 32, _sections[i].Placement);
                for (int i = 0; i < _sections.Count; i++)
                    PutArray(sectionAt + i * SectionSize, _sections[i].Bvh, 28, "hknpAabb8TreeNode");
                for (int i = 0; i < _sections.Count; i++)
                    PutArray(sectionAt + i * SectionSize + 8, _sections[i].Primitives, 4, "hknpMeshShape::GeometrySection::Primitive");
                for (int i = 0; i < _sections.Count; i++)
                {
                    var s = _sections[i];
                    byte[] bytes;
                    if (s.Floats != null)
                    {
                        bytes = new byte[s.Floats.Length * 4];
                        for (int k = 0; k < s.Floats.Length; k++)
                            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(k * 4), s.Floats[k]);
                    }
                    else if (is2025 && i == _sections.Count - 1)
                    {
                        bytes = new byte[s.Vertices.Length + 2];
                        s.Vertices.CopyTo(bytes, 0);
                    }
                    else
                        bytes = s.Vertices;
                    PutArray(sectionAt + i * SectionSize + 16, bytes, is2025 ? 1 : 6, vertexType);
                }
                for (int i = 0; i < _sections.Count; i++)
                    PutArray(sectionAt + i * SectionSize + 24, _sections[i].Interior, 1, "hkUint8");
                while (data.Count % 16 != 0)
                    data.Add(0);

                string sdk = is2025 ? Havok2025 : Havok2021;
                var builder = new HkTypeBuilder(HkTypeCatalog.Stock(sdk));
                foreach (var item in items)
                    builder.Write(item.Type);
                if (builder.Missing.Count > 0)
                    throw new ConvertException("No type information for " + builder.Missing[0] + ".");
                var index = new byte[(items.Count + 1) * 12];
                for (int i = 0; i < items.Count; i++)
                {
                    var e = index.AsSpan((i + 1) * 12);
                    BinaryPrimitives.WriteUInt32LittleEndian(e, items[i].Kind | (uint)builder.IdOf(items[i].Type));
                    BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)items[i].Offset);
                    BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)items[i].Count);
                }
                var body = new List<byte>();
                body.AddRange(Section("SDKV", Encoding.ASCII.GetBytes(sdk), 1));
                body.AddRange(Section("DATA", data.ToArray(), 1));
                body.AddRange(builder.Section.Write());
                body.AddRange(Section("INDX", Section("ITEM", index, 1), 0));
                return Section("TAG0", body.ToArray(), 0);
            }

            //2025.1's per node bookkeeping: parent, sections below and depth, counted from the root.
            (int[] Parent, int[] Leaves, int[] Depth) Derive()
            {
                int count = _nodes.Count;
                var parent = new int[count];
                var leaves = new int[count];
                var depth = new int[count];
                var visiting = new bool[count];
                void Visit(int i, int from)
                {
                    if (i <= 0 || i >= count || visiting[i])
                        throw new ConvertException("The shape's tree is malformed.");
                    visiting[i] = true;
                    parent[i] = from;
                    var n = _nodes[i];
                    if (n.Leaf)
                    {
                        leaves[i] = n.UsedCount;
                        depth[i] = 1;
                        return;
                    }
                    for (int j = 0; j < 4; j++)
                        if (n.Used(j))
                        {
                            int c = (int)n.Data[j];
                            Visit(c, i);
                            leaves[i] += leaves[c];
                            depth[i] = Math.Max(depth[i], depth[c] + 1);
                        }
                }
                Visit(1, 0);
                return (parent, leaves, depth);
            }
        }
    }
}
