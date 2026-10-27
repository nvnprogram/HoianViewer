using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace EffectLibrary
{
    /// <summary>
    /// ResEmitter field map
    /// </summary>
    public sealed class EmitterMap
    {
        public ushort Version { get; init; }
        public int PayloadSize { get; init; }
        public int StaticBlockSize { get; init; }
        public int Visible { get; init; }
        public int CalcType { get; init; }
        public int FollowType { get; init; }
        public int DrawPath { get; init; }
        public int VolumeType { get; init; }
        public int ShapePrimitiveId { get; init; }
        public int RenderState { get; init; }
        public int InfiniteLife { get; init; }
        public int BillboardType { get; init; }
        public int Life { get; init; }
        public int ParticlePrimitiveId { get; init; }
        public int ParticlePrimitiveExId { get; init; }
        public int ShaderIndex { get; init; }
        public int DepthModeShaderIndex { get; init; }
        public int PassShaderIndex { get; init; }
        public int ComputeShaderIndex { get; init; }
        public int CustomShaderFlags { get; init; }
        public int DepthMode { get; init; }
        public int PassInfo { get; init; }
        public int CustomAction { get; init; }
        public int Samplers { get; init; }
        public int SamplerStride { get; init; }
        public int SamplerCount { get; init; }
        public int TextureAnims { get; init; }
        public int TextureAnimStride { get; init; }

        /// <summary>Every mapped field, in offset order.</summary>
        public IReadOnlyList<EmitterField> Fields { get; init; }

        /// <summary>A static block key table: its key count, the keys, and the fewest keys the load time padding acts on.</summary>
        public readonly record struct KeyTable(int CountAt, int Table, int MinKeys);

        public IReadOnlyList<KeyTable> KeyTables { get; init; }

        /// <summary>Particle animation loop slots: the loop and random start flags per slot.</summary>
        public int LoopSlots { get; init; }
        public int LoopFlags { get; init; }
        public int LoopRandomFlags { get; init; }

        /// <summary>Per loop slot, where the static block keeps its frame count and its random start.</summary>
        public IReadOnlyList<int> LoopFrames { get; init; }
        public IReadOnlyList<int> LoopRandomStart { get; init; }

        static readonly (string Count, int Index, string Table, int MinKeys)[] KeyTableRows =
        {
            ("Static.KeyCount.Color0", 0, "Static.Color0Keys", 1),
            ("Static.KeyCount.Alpha0", 0, "Static.Alpha0Keys", 1),
            ("Static.KeyCount.Color1", 0, "Static.Color1Keys", 1),
            ("Static.KeyCount.Alpha1", 0, "Static.Alpha1Keys", 1),
            ("Static.KeyCount.Scale", 0, "Static.ScaleKeys", 1),
            ("Static.KeyCount.Keys5", 0, "Static.Keys5", 1),
            ("Static.KeyCount.Param", 0, "Static.ParamKeys", 1),
            ("Static.KeyCount.Keys7", 0, "Static.Param1Keys", 2),
            ("Static.KeyCount.Keys7", 1, "Static.Param2Keys", 2),
            ("Static.KeyCount.Keys7", 2, "Static.Param3Keys", 2),
        };

        //Spans of v46 bytes and where v54 keeps them, gaps included. ToV46 copies these.
        static readonly (int From, int To, int At)[] V54Ranges =
        {
            (0x000, 0x094, 0x000),
            (0x094, 0x098, 0x098),
            (0x0A0, 0x0D0, 0x0B0),
            (0x0D0, 0x940, 0x100),
            (0x940, 0x9C0, 0x9F0),
            (0x9C0, 0xC1D, 0xBF0),
            (0xC1D, 0xC22, 0xE51),
            (0xC38, 0xC3E, 0xE78),
            (0xC40, 0xC46, 0xE7E),
            (0xC4C, 0xC58, 0xE94),
            (0xC58, 0xC5C, 0xEB4),
            (0xC68, 0xC70, 0xEC8),
            (0xCB0, 0xCF0, 0xF08),
            (0xCF0, 0xD90, 0xF98),
            (0xE50, 0xEB0, 0x10C8),
        };

        public static readonly EmitterMap V46 = Build(
            VfxFile.SwitchV46,
            EmitterLayout.PayloadSize,
            EmitterLayout.StaticBlockSize
        );

        public static readonly EmitterMap V54 = Build(VfxFile.OunceV54, 0x1168, 0xCC0);

        static EmitterMap Build(ushort version, int payloadSize, int staticBlockSize)
        {
            var fields = EmitterLayout.FieldsOf(version);
            EmitterField Field(string name) => fields.First(f => f.Name == name);
            int At(string name) => Field(name).Offset;
            bool Has(string name) => fields.Any(f => f.Name == name);
            //Each element's offset of the named fields, in the order given.
            int[] Elements(params string[] names) =>
                names
                    .Where(Has)
                    .Select(Field)
                    .SelectMany(f =>
                        Enumerable.Range(0, f.Count).Select(i => f.Offset + f.Size / f.Count * i)
                    )
                    .ToArray();
            return new EmitterMap
            {
                Version = version,
                PayloadSize = payloadSize,
                StaticBlockSize = staticBlockSize,
                Visible = At("Info.Visible"),
                CalcType = At("Info.CalcType"),
                FollowType = At("Info.FollowType"),
                DrawPath = At("Info.DrawPath"),
                VolumeType = At("Shape.VolumeType"),
                ShapePrimitiveId = At("Shape.PrimitiveId"),
                RenderState = At("Render.BlendEnable"),
                InfiniteLife = At("Particle.InfiniteLife"),
                BillboardType = At("Particle.BillboardType"),
                Life = At("Particle.Life"),
                ParticlePrimitiveId = At("Particle.PrimitiveId"),
                ParticlePrimitiveExId = At("Particle.PrimitiveExId"),
                ShaderIndex = At("Shader.Index"),
                DepthModeShaderIndex = At("Shader.DepthModeIndex"),
                PassShaderIndex = At("Shader.PassIndex"),
                ComputeShaderIndex = At("Shader.ComputeIndex"),
                CustomShaderFlags = At("Shader.CustomFlags"),
                DepthMode = At("DepthMode"),
                PassInfo = At("PassInfo"),
                CustomAction = At("CustomAction"),
                Samplers = At("Samplers"),
                SamplerStride = Field("Samplers").Size / EmitterLayout.SamplerCount,
                SamplerCount = EmitterLayout.SamplerCount,
                TextureAnims = At("TextureAnims"),
                TextureAnimStride = EmitterLayout.TextureAnimStride,
                Fields = fields,
                KeyTables = KeyTableRows
                    .Where(r => Has(r.Count) && Has(r.Table))
                    .Select(r => new KeyTable(At(r.Count) + 4 * r.Index, At(r.Table), r.MinKeys))
                    .ToArray(),
                LoopSlots = Field("Particle.AnimLoop").Count,
                LoopFlags = At("Particle.AnimLoop"),
                LoopRandomFlags = At("Particle.AnimLoopRandom"),
                LoopFrames = Elements("Static.LoopFrames", "Static.LoopFrames5"),
                LoopRandomStart = Elements("Static.LoopRandom", "Static.LoopRandom5"),
                _loopRates = At("Particle.AnimLoopRate"),
                _loopRates4 = fields.FirstOrDefault(f => f.Name == "Particle.AnimLoopRate4").Offset,
            };
        }

        /// <summary>Where this version keeps a field of the table, or -1 when it has none.</summary>
        public int OffsetOf(string name)
        {
            foreach (var f in Fields)
                if (f.Name == name)
                    return f.Offset;
            return -1;
        }

        public static EmitterMap For(ushort version) => version == VfxFile.OunceV54 ? V54 : V46;

        public bool IsV46 => Version == VfxFile.SwitchV46;

        public const int V46LoopSlots = 5;

        public const int V46LoopRates = 0xC24;

        //v54 keeps the first four loop rates as u16 and the rest as s32 further on.
        int _loopRates;
        int _loopRates4;

        /// <summary>Where this version keeps the field at <paramref name="v46Offset"/> of a v46 emitter.</summary>
        public int At(int v46Offset)
        {
            if (IsV46)
                return v46Offset;
            foreach (var (from, to, at) in V54Ranges)
                if (v46Offset >= from && v46Offset < to)
                    return at + v46Offset - from;
            throw new ArgumentOutOfRangeException(
                nameof(v46Offset),
                $"0x{v46Offset:X} has no known place in a v{Version} emitter"
            );
        }

        /// <summary>The loop rate of particle animation slot <paramref name="k"/>, in frames.</summary>
        public int LoopRate(ReadOnlySpan<byte> data, int k) =>
            IsV46 ? BinaryPrimitives.ReadInt32LittleEndian(data[(_loopRates + 4 * k)..])
            : k < 4 ? BinaryPrimitives.ReadUInt16LittleEndian(data[(_loopRates + 2 * k)..])
            : BinaryPrimitives.ReadInt32LittleEndian(data[(_loopRates4 + 4 * (k - 4))..]);

        /// <summary>
        /// The first <paramref name="length"/> bytes of an emitter laid out as v46: each mapped
        /// field copied to its v46 place, the loop rates as s32, the rest zero.
        /// </summary>
        public byte[] ToV46(ReadOnlySpan<byte> data, int length)
        {
            if (IsV46)
                return data[..length].ToArray();
            var b = new byte[length];
            foreach (var (from, to, at) in V54Ranges)
                if (from < length)
                    data.Slice(at, Math.Min(to, length) - from).CopyTo(b.AsSpan(from));
            for (int k = 0; k < V46LoopSlots; k++)
                if (V46LoopRates + 4 * k + 4 <= length)
                    BinaryPrimitives.WriteInt32LittleEndian(
                        b.AsSpan(V46LoopRates + 4 * k),
                        LoopRate(data, k)
                    );
            return b;
        }
    }
}
