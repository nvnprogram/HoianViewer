using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using BfresLibrary.GX2;
using EffectLibrary;

namespace PlayerViewer.Effects
{
    /// <summary>How one vertex component is stored.</summary>
    public enum VertexComponentType
    {
        Float,
        Half,
        UNorm8,
        SNorm8,
        UNorm16,
        SNorm16,

        /// <summary>Three signed normalised 10 bit components and a 2 bit one, packed in 32 bits.</summary>
        SNorm10_10_10_2,
        UNorm10_10_10_2,
    }

    /// <summary>One vertex input of a mesh: which buffer, where in the vertex, and how it is stored.</summary>
    public readonly record struct EffectVertexAttribute(
        string Name,
        int Buffer,
        int Offset,
        int Components,
        VertexComponentType Type
    );

    /// <summary>
    /// A particle primitive as the GPU reads it: vertex buffers exactly as stored, the attribute
    /// layout under the program input names, and triangle list indices.
    /// </summary>
    public sealed class EffectMesh
    {
        public List<byte[]> Buffers { get; } = new();
        public List<int> Strides { get; } = new();
        public List<EffectVertexAttribute> Attributes { get; } = new();
        public uint[] Indices { get; private set; }

        /// <summary>The program inputs in G3NT order.</summary>
        static readonly string[] Semantics =
        {
            EffectBindings.PositionAttr,
            EffectBindings.NormalAttr,
            EffectBindings.TangentAttr,
            EffectBindings.VertexColorAttr,
            EffectBindings.TexCoordAttr,
            null,
        };

        static readonly string[] ModelNames = { "_p0", "_n0", "_t0", "_c0", "_u0", "_u1" };

        public EffectVertexAttribute? Find(string name) =>
            Attributes.FindIndex(x => x.Name == name) is int i && i >= 0 ? Attributes[i] : null;

        /// <summary>The mesh a primitive reference names, or null when it has none.</summary>
        public static EffectMesh From(VfxFile file, PrimitiveRef primitive)
        {
            if (primitive?.Model != null)
                return FromModel(file, primitive.Model);
            if (primitive?.Native != null)
                return FromNative(primitive.Native);
            return null;
        }

        static EffectMesh FromModel(VfxFile file, PrimitiveEntry entry)
        {
            var model = file.Primitives.GetModel(entry);
            if (model == null || model.Shapes.Count == 0)
                return null;
            var shape = model.Shapes[0];
            var vb = model.VertexBuffers[shape.VertexBufferIndex];
            var mesh = new EffectMesh();
            foreach (var b in vb.Buffers)
            {
                mesh.Buffers.Add(b.Data[0]);
                mesh.Strides.Add(b.Stride);
            }
            for (int s = 0; s < Semantics.Length; s++)
            {
                if (Semantics[s] == null || entry.Attributes[s] < 0)
                    continue;
                //The G3NT index counts attributes the packager later dropped, so the name decides.
                int index = vb.Attributes.IndexOf(ModelNames[s]);
                if (index < 0)
                    continue;
                var a = vb.Attributes[index];
                var (components, type) = Describe(a.Format);
                mesh.Attributes.Add(new(Semantics[s], a.BufferIndex, a.Offset, components, type));
            }
            int uv1 = entry.Attributes[5] < 0 ? -1 : vb.Attributes.IndexOf(ModelNames[5]);
            if (uv1 >= 0 && mesh.Find(EffectBindings.TexCoordAttr) is EffectVertexAttribute uv0)
            {
                var a = vb.Attributes[uv1];
                var (components, type) = Describe(a.Format);
                mesh.PackTexCoords(
                    uv0,
                    new("", a.BufferIndex, a.Offset, components, type),
                    (int)vb.VertexCount
                );
            }
            var m = shape.Meshes[0];
            mesh.Indices = m.GetIndices().Select(i => i + m.FirstVertex).ToArray();
            return mesh;
        }

        /// <summary>
        /// The texcoord input is one float4 with uv0 in xy and uv1 in zw; the game fetches both in
        /// one read, and a primitive's position can be built from them. Both are decoded into a
        /// float4 buffer of their own.
        /// </summary>
        void PackTexCoords(EffectVertexAttribute uv0, EffectVertexAttribute uv1, int count)
        {
            var packed = new float[count * 4];
            for (int v = 0; v < count; v++)
            {
                Read(uv0, v, packed.AsSpan(v * 4, 2));
                Read(uv1, v, packed.AsSpan(v * 4 + 2, 2));
            }
            Attributes.Remove(uv0);
            Attributes.Add(
                new(EffectBindings.TexCoordAttr, Buffers.Count, 0, 4, VertexComponentType.Float)
            );
            Buffers.Add(MemoryMarshal.AsBytes(packed.AsSpan()).ToArray());
            Strides.Add(16);
        }

        /// <summary>Vertices in the attribute's buffer.</summary>
        public int VertexCount(EffectVertexAttribute a) =>
            Buffers[a.Buffer].Length / Math.Max(1, Strides[a.Buffer]);

        /// <summary>Decodes one vertex of an attribute into floats.</summary>
        public void Read(EffectVertexAttribute a, int vertex, Span<float> into)
        {
            if (
                a.Type is VertexComponentType.SNorm10_10_10_2 or VertexComponentType.UNorm10_10_10_2
            )
            {
                uint p = BitConverter.ToUInt32(
                    Buffers[a.Buffer],
                    vertex * Strides[a.Buffer] + a.Offset
                );
                for (int c = 0; c < into.Length && c < 4; c++)
                {
                    int bits = c < 3 ? 10 : 2;
                    uint v = (p >> (c * 10)) & ((1u << bits) - 1);
                    if (a.Type == VertexComponentType.UNorm10_10_10_2)
                        into[c] = v / (float)((1 << bits) - 1);
                    else
                    {
                        int sv = (int)(v << (32 - bits)) >> (32 - bits);
                        into[c] = Math.Max(sv / (float)((1 << (bits - 1)) - 1), -1f);
                    }
                }
                return;
            }
            var data = Buffers[a.Buffer].AsSpan(vertex * Strides[a.Buffer] + a.Offset);
            for (int c = 0; c < into.Length && c < a.Components; c++)
                into[c] = a.Type switch
                {
                    VertexComponentType.Float => BitConverter.ToSingle(data[(4 * c)..]),
                    VertexComponentType.Half => (float)BitConverter.ToHalf(data[(2 * c)..]),
                    VertexComponentType.UNorm8 => data[c] / 255f,
                    VertexComponentType.SNorm8 => Math.Max((sbyte)data[c] / 127f, -1f),
                    VertexComponentType.UNorm16 => BitConverter.ToUInt16(data[(2 * c)..]) / 65535f,
                    VertexComponentType.SNorm16 => Math.Max(
                        BitConverter.ToInt16(data[(2 * c)..]) / 32767f,
                        -1f
                    ),
                    _ => throw new NotSupportedException($"Texcoord format {a.Type}"),
                };
        }

        static EffectMesh FromNative(NativePrimitive prim)
        {
            var mesh = new EffectMesh();
            void Add(string name, System.Numerics.Vector4[] values)
            {
                if (values == null || values.Length == 0)
                    return;
                mesh.Attributes.Add(new(name, mesh.Buffers.Count, 0, 4, VertexComponentType.Float));
                mesh.Buffers.Add(MemoryMarshal.AsBytes(values.AsSpan()).ToArray());
                mesh.Strides.Add(16);
            }
            Add(EffectBindings.PositionAttr, prim.Positions);
            Add(EffectBindings.NormalAttr, prim.Normals);
            Add(EffectBindings.TangentAttr, prim.Tangents);
            Add(EffectBindings.VertexColorAttr, prim.Colors);
            var uv = prim.TexCoords0;
            if (uv != null && prim.TexCoord1Count > 0)
            {
                var uv1 = prim.TexCoords1;
                uv = uv.Select((t, i) => new System.Numerics.Vector4(t.X, t.Y, uv1[i].X, uv1[i].Y))
                    .ToArray();
            }
            Add(EffectBindings.TexCoordAttr, uv);
            mesh.Indices = prim.Indices;
            return mesh;
        }

        static (int, VertexComponentType) Describe(GX2AttribFormat format) =>
            format switch
            {
                GX2AttribFormat.Format_32_Single => (1, VertexComponentType.Float),
                GX2AttribFormat.Format_32_32_Single => (2, VertexComponentType.Float),
                GX2AttribFormat.Format_32_32_32_Single => (3, VertexComponentType.Float),
                GX2AttribFormat.Format_32_32_32_32_Single => (4, VertexComponentType.Float),
                GX2AttribFormat.Format_16_Single => (1, VertexComponentType.Half),
                GX2AttribFormat.Format_16_16_Single => (2, VertexComponentType.Half),
                GX2AttribFormat.Format_16_16_16_16_Single => (4, VertexComponentType.Half),
                GX2AttribFormat.Format_8_UNorm => (1, VertexComponentType.UNorm8),
                GX2AttribFormat.Format_8_8_UNorm => (2, VertexComponentType.UNorm8),
                GX2AttribFormat.Format_8_8_8_8_UNorm => (4, VertexComponentType.UNorm8),
                GX2AttribFormat.Format_8_SNorm => (1, VertexComponentType.SNorm8),
                GX2AttribFormat.Format_8_8_SNorm => (2, VertexComponentType.SNorm8),
                GX2AttribFormat.Format_8_8_8_8_SNorm => (4, VertexComponentType.SNorm8),
                GX2AttribFormat.Format_16_UNorm => (1, VertexComponentType.UNorm16),
                GX2AttribFormat.Format_16_16_UNorm => (2, VertexComponentType.UNorm16),
                GX2AttribFormat.Format_16_16_16_16_UNorm => (4, VertexComponentType.UNorm16),
                GX2AttribFormat.Format_16_SNorm => (1, VertexComponentType.SNorm16),
                GX2AttribFormat.Format_16_16_SNorm => (2, VertexComponentType.SNorm16),
                GX2AttribFormat.Format_16_16_16_16_SNorm => (4, VertexComponentType.SNorm16),
                GX2AttribFormat.Format_10_10_10_2_SNorm => (4, VertexComponentType.SNorm10_10_10_2),
                GX2AttribFormat.Format_10_10_10_2_UNorm => (4, VertexComponentType.UNorm10_10_10_2),
                _ => throw new NotSupportedException($"Primitive vertex format {format}"),
            };
    }
}
