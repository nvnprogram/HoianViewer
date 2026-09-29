using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using BfresLibrary;

namespace EffectLibrary
{
    /// <summary>One G3NT record: a primitive guid, the model it is, and which vertex attribute
    /// of that model feeds each particle input (-1 for none).</summary>
    public sealed class PrimitiveEntry
    {
        public int Index { get; init; }
        public ulong Id { get; init; }

        /// <summary>Attribute indices for position, normal, tangent, colour, uv0, uv1.</summary>
        public sbyte[] Attributes { get; init; }

        public override string ToString() => $"model {Index} ({Id:X})";
    }

    /// <summary>
    /// G3PR: the particle meshes as one BFRES, with the G3NT table. Record i names model i of the
    /// BFRES. The BFRES is parsed on first use and saved as its original bytes.
    /// </summary>
    public sealed class PrimitiveArchive
    {
        public VfxSection Section { get; }
        public VfxSection Table { get; }
        public IReadOnlyList<PrimitiveEntry> Entries { get; }

        readonly Dictionary<ulong, PrimitiveEntry> _byId = new();
        ResFile _resFile;
        Exception _loadError;

        PrimitiveArchive(VfxSection section)
        {
            Section = section;
            Table = section.FindChild("G3NT");
            Entries = Table == null ? Array.Empty<PrimitiveEntry>() : ReadTable(Table.Data);
            foreach (var entry in Entries)
                _byId.TryAdd(entry.Id, entry);
        }

        internal static PrimitiveArchive From(VfxSection section) =>
            section == null ? null : new PrimitiveArchive(section);

        /// <summary>Records of 24 bytes: {u64 guid, u32 offset to the next record or 0, u32
        /// 8, s8 attribute index x6, u16 0}.</summary>
        static List<PrimitiveEntry> ReadTable(ReadOnlySpan<byte> data)
        {
            var list = new List<PrimitiveEntry>();
            int at = 0;
            while (at + 24 <= data.Length)
            {
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data[at..]);
                uint next = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 8)..]);
                var attributes = MemoryMarshal.Cast<byte, sbyte>(data.Slice(at + 16, 6)).ToArray();
                list.Add(
                    new PrimitiveEntry
                    {
                        Index = list.Count,
                        Id = id,
                        Attributes = attributes,
                    }
                );
                if (next == 0)
                    break;
                at += (int)next;
            }
            return list;
        }

        public PrimitiveEntry Find(ulong id) => _byId.GetValueOrDefault(id);

        public Memory<byte> BfresBytes => Section.Payload;

        public Exception LoadError => _loadError;

        /// <summary>The parsed BFRES, or null when it does not parse.</summary>
        public ResFile ResFile
        {
            get
            {
                if (_resFile != null || _loadError != null)
                    return _resFile;
                try
                {
                    _resFile = new ResFile(Streams.Open(BfresBytes));
                }
                catch (Exception e)
                {
                    _loadError = e;
                }
                return _resFile;
            }
        }

        public Model GetModel(PrimitiveEntry entry) =>
            entry == null || ResFile == null || entry.Index >= ResFile.Models.Count
                ? null
                : ResFile.Models[entry.Index];
    }

    /// <summary>
    /// A PRIM: a mesh stored natively in the effect file rather than in the BFRES. Its first
    /// eight bytes are its guid; attributes are float4 arrays and the indices u32.
    /// </summary>
    public sealed class NativePrimitive
    {
        public VfxSection Section { get; }

        public NativePrimitive(VfxSection section) => Section = section;

        public ulong Id => Section.GetU64(0);

        public int PositionCount => Section.GetS32(0x08);
        public int NormalCount => Section.GetS32(0x10);
        public int TangentCount => Section.GetS32(0x18);
        public int ColorCount => Section.GetS32(0x20);
        public int TexCoord0Count => Section.GetS32(0x28);
        public int TexCoord1Count => Section.GetS32(0x30);
        public int IndexCount => Section.GetS32(0x38);

        /// <summary>Components per element as declared, for the attribute at slot i (position,
        /// normal, tangent, colour, uv0, uv1). Storage is always four floats.</summary>
        public int GetComponentCount(int slot) => Section.GetS32(0x0C + 8 * slot);

        public Vector4[] Positions => ReadVectors(PositionCount, Section.GetU32(0x3C));
        public Vector4[] Normals => ReadVectors(NormalCount, Section.GetU32(0x40));
        public Vector4[] Tangents => ReadVectors(TangentCount, Section.GetU32(0x44));
        public Vector4[] Colors => ReadVectors(ColorCount, Section.GetU32(0x48));
        public Vector4[] TexCoords0 => ReadVectors(TexCoord0Count, Section.GetU32(0x4C));

        /// <summary>The second uv set follows the first in the same block.</summary>
        public Vector4[] TexCoords1 =>
            ReadVectors(TexCoord1Count, Section.GetU32(0x4C) + (uint)(16 * TexCoord0Count));

        public uint[] Indices
        {
            get
            {
                uint at = Section.GetU32(0x50);
                return MemoryMarshal
                    .Cast<byte, uint>(Section.Data.Slice((int)at, 4 * IndexCount))
                    .ToArray();
            }
        }

        Vector4[] ReadVectors(int count, uint at) =>
            count == 0
                ? Array.Empty<Vector4>()
                : MemoryMarshal
                    .Cast<byte, Vector4>(Section.Data.Slice((int)at, 16 * count))
                    .ToArray();
    }

    /// <summary>PRMA's native meshes by guid.</summary>
    public sealed class NativePrimitiveList
    {
        public VfxSection Section { get; }
        public IReadOnlyList<NativePrimitive> Items { get; }

        readonly Dictionary<ulong, NativePrimitive> _byId = new();

        internal NativePrimitiveList(VfxSection section)
        {
            Section = section;
            var items = new List<NativePrimitive>();
            if (section != null)
                foreach (var child in section.Children)
                    if (child.Tag == "PRIM")
                        items.Add(new NativePrimitive(child));
            Items = items;
            foreach (var item in items)
                _byId.TryAdd(item.Id, item);
        }

        public NativePrimitive Find(ulong id) => _byId.GetValueOrDefault(id);
    }

    /// <summary>Where a primitive guid led: a BFRES model, a native mesh, or nowhere.</summary>
    public sealed class PrimitiveRef
    {
        public ulong Id { get; init; }
        public PrimitiveEntry Model { get; init; }
        public NativePrimitive Native { get; init; }

        public bool Found => Model != null || Native != null;

        internal static PrimitiveRef Resolve(VfxFile file, ulong id)
        {
            if (id == 0 || id == ulong.MaxValue)
                return null;
            return new PrimitiveRef
            {
                Id = id,
                Model = file.Primitives?.Find(id),
                Native = file.NativePrimitives.Find(id),
            };
        }
    }

    static class Streams
    {
        /// <summary>A read only stream over the bytes, without a copy when they are an array.</summary>
        public static Stream Open(Memory<byte> bytes) =>
            MemoryMarshal.TryGetArray<byte>(bytes, out var seg)
                ? new MemoryStream(seg.Array, seg.Offset, seg.Count, false)
                : new MemoryStream(bytes.ToArray(), false);
    }
}
