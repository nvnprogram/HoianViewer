using System;
using System.Collections.Generic;
using System.Linq;

namespace EffectLibrary
{
    /// <summary>ResEmitter byte +0xA92: where the particles are integrated.</summary>
    public enum EmitterCalcType : byte
    {
        Cpu = 0,
        Gpu = 1,

        /// <summary>A compute program integrates and writes the particles back (stream out).</summary>
        GpuCompute = 2,
    }

    /// <summary>
    /// An EMTR: one emitter's ResEmitter payload, its sub-section chain (fields, keyframe
    /// animations, custom params) and its child emitters. Every accessor reads and writes the
    /// payload in place.
    /// </summary>
    public sealed class Emitter
    {
        public EmitterSet Set { get; }

        /// <summary>The parent emitter, or null for a top level emitter of the set.</summary>
        public Emitter Parent { get; }

        public VfxSection Section { get; }

        /// <summary>Position among the parent's children (or the set's emitters).</summary>
        public int Index { get; }

        public IReadOnlyList<Emitter> Children { get; }

        internal Emitter(EmitterSet set, Emitter parent, VfxSection section, int index)
        {
            if (section.Tag != "EMTR")
                throw new InvalidOperationException($"Expected EMTR, found {section.Tag}");
            if (section.Payload.Length != EmitterLayout.PayloadSize)
                throw new InvalidOperationException(
                    $"EMTR payload is 0x{section.Payload.Length:X} bytes, v46 is 0x{EmitterLayout.PayloadSize:X}"
                );
            Set = set;
            Parent = parent;
            Section = section;
            Index = index;
            Children = section.Children.Select((x, i) => new Emitter(set, this, x, i)).ToList();
        }

        public Span<byte> Data => Section.Data;

        public string Name
        {
            get => Section.GetString(EmitterLayout.NameOffset, EmitterLayout.NameLength);
            set => Section.SetString(EmitterLayout.NameOffset, EmitterLayout.NameLength, value);
        }

        public IEnumerable<Emitter> SelfAndDescendants =>
            new[] { this }.Concat(Children.SelectMany(x => x.SelfAndDescendants));

        /// <summary>The bytes the game uploads as sysEmitterStaticUniformBlock.</summary>
        public Span<byte> StaticBlock => Data[..EmitterLayout.StaticBlockSize];

        public bool Visible
        {
            get => Section.GetU8(EmitterLayout.Visible) != 0;
            set => Section.SetU8(EmitterLayout.Visible, value ? (byte)1 : (byte)0);
        }

        public EmitterCalcType CalcType
        {
            get => (EmitterCalcType)Section.GetU8(EmitterLayout.CalcType);
            set => Section.SetU8(EmitterLayout.CalcType, (byte)value);
        }

        public byte FollowType => Section.GetU8(EmitterLayout.FollowType);

        public uint DrawPath => Section.GetU32(EmitterLayout.DrawPath);

        public int Life => Section.GetS32(EmitterLayout.Life);

        public bool InfiniteLife => Section.GetU8(EmitterLayout.InfiniteLife) != 0;

        public byte BillboardType => Section.GetU8(EmitterLayout.BillboardType);

        public byte VolumeType => Section.GetU8(EmitterLayout.VolumeType);

        public ulong ShapePrimitiveId => Section.GetU64(EmitterLayout.ShapePrimitiveId);

        public ulong ParticlePrimitiveId => Section.GetU64(EmitterLayout.ParticlePrimitiveId);

        public ulong ParticlePrimitiveExId => Section.GetU64(EmitterLayout.ParticlePrimitiveExId);

        /// <summary>Program index into the general shader archive for the ordinary pass.</summary>
        public int ShaderIndex
        {
            get => Section.GetS32(EmitterLayout.ShaderIndex);
            set => Section.SetS32(EmitterLayout.ShaderIndex, value);
        }

        /// <summary>The same program with the converter option <see cref="DepthMode"/> names set
        /// (ZONLY_CONVERTER, LUMA_CONVERTER or NO_DEPTH_TEST_CONVERTER).</summary>
        public int DepthModeShaderIndex
        {
            get => Section.GetS32(EmitterLayout.DepthModeShaderIndex);
            set => Section.SetS32(EmitterLayout.DepthModeShaderIndex, value);
        }

        /// <summary>The same program with the converter option <see cref="PassInfo"/> names set,
        /// XLU_Z_PREPASS_CONVERTER in every stock file.</summary>
        public int PassShaderIndex
        {
            get => Section.GetS32(EmitterLayout.PassShaderIndex);
            set => Section.SetS32(EmitterLayout.PassShaderIndex, value);
        }

        /// <summary>Program index into the compute archive (GRSC) for a stream out emitter, -1
        /// for the others.</summary>
        public int ComputeShaderIndex
        {
            get => Section.GetS32(EmitterLayout.ComputeShaderIndex);
            set => Section.SetS32(EmitterLayout.ComputeShaderIndex, value);
        }

        /// <summary>ZONLY, LUMA or NO_DEPTH_TEST: names the option that tells the depth mode
        /// program from the base one.</summary>
        public string DepthMode => Section.GetString(EmitterLayout.DepthMode, 16);

        /// <summary>XLU_Z_PREPASS in every stock file: names the option of the pass program.</summary>
        public string PassInfo => Section.GetString(EmitterLayout.PassInfo, 48);

        /// <summary>The game callback set the emitter's particles go through, 0 for none.</summary>
        public int CustomAction => Section.GetS32(EmitterLayout.CustomAction);

        public RenderState RenderState => new(Section);

        public TextureSampler GetSampler(int slot)
        {
            if ((uint)slot >= EmitterLayout.SamplerCount)
                throw new ArgumentOutOfRangeException(nameof(slot));
            return new TextureSampler(
                Section,
                EmitterLayout.Samplers + slot * EmitterLayout.SamplerStride
            );
        }

        public IEnumerable<TextureSampler> Samplers =>
            Enumerable.Range(0, EmitterLayout.SamplerCount).Select(GetSampler);

        /// <summary>The emitter keyframe animations (the EA** sub-sections).</summary>
        public IEnumerable<EmitterAnimation> Animations =>
            Section
                .SubSections.Where(x => EmitterAnimation.IsAnimationTag(x.Tag))
                .Select(x => new EmitterAnimation(x));

        public VfxSection FindSub(string tag) => Section.FindSub(tag);

        public float GetF32(int offset) => Section.GetF32(offset);

        public void SetF32(int offset, float value) => Section.SetF32(offset, value);

        /// <summary>The GTNT entry a sampler slot names, or null when the slot is empty or the
        /// texture lives in another binary.</summary>
        public TextureEntry ResolveTexture(int slot)
        {
            ulong id = GetSampler(slot).TextureId;
            return id == 0 || id == ulong.MaxValue ? null : Set.File.Textures?.Find(id);
        }

        /// <summary>The mesh a primitive id names: a model of the G3PR bfres or a native PRIM.</summary>
        public PrimitiveRef ResolvePrimitive(ulong id) => PrimitiveRef.Resolve(Set.File, id);

        public override string ToString() => Name;
    }

    /// <summary>ResEmitter +0xBD8, 16 bytes: blend, depth and cull state.</summary>
    public readonly struct RenderState
    {
        readonly VfxSection _section;

        internal RenderState(VfxSection section) => _section = section;

        public bool BlendEnable => _section.GetU8(EmitterLayout.BlendEnable) != 0;
        public bool DepthTest => _section.GetU8(EmitterLayout.DepthTest) != 0;
        public byte DepthFunc => _section.GetU8(EmitterLayout.DepthFunc);
        public bool DepthWrite => _section.GetU8(EmitterLayout.DepthWrite) != 0;
        public byte AlphaTest => _section.GetU8(EmitterLayout.AlphaTest);
        public byte AlphaFunc => _section.GetU8(EmitterLayout.AlphaFunc);

        /// <summary>0 normal, 1 add, 2 sub, 3 screen, 4 multiply.</summary>
        public byte BlendType => _section.GetU8(EmitterLayout.BlendType);

        /// <summary>0 both sides, 1 front (cull back), 2 back (cull front).</summary>
        public byte DisplaySide => _section.GetU8(EmitterLayout.DisplaySide);

        public override string ToString() =>
            $"blend {(BlendEnable ? BlendType.ToString() : "off")} depth test {DepthTest} func {DepthFunc} write {DepthWrite} side {DisplaySide}";
    }

    /// <summary>One of the six 0x20 byte sampler slots at ResEmitter +0xD90.</summary>
    public readonly struct TextureSampler
    {
        readonly VfxSection _section;
        readonly int _at;

        internal TextureSampler(VfxSection section, int at)
        {
            _section = section;
            _at = at;
        }

        /// <summary>The GTNT guid of the texture; 0 or all ones when the slot is unused.</summary>
        public ulong TextureId
        {
            get => _section.GetU64(_at);
            set => _section.SetU64(_at, value);
        }

        public byte WrapU => _section.GetU8(_at + 0x08);
        public byte WrapV => _section.GetU8(_at + 0x09);
        public byte Filter => _section.GetU8(_at + 0x0A);
        public byte IsSphereMap => _section.GetU8(_at + 0x0B);
        public float MaxLod => _section.GetF32(_at + 0x0C);
        public float LodBias => _section.GetF32(_at + 0x10);
        public byte MipLevelLimit => _section.GetU8(_at + 0x14);
        public byte IsDensityFixedU => _section.GetU8(_at + 0x15);
        public byte IsDensityFixedV => _section.GetU8(_at + 0x16);
        public byte IsSquareRgb => _section.GetU8(_at + 0x17);
        public byte IsOnAnotherBinary => _section.GetU8(_at + 0x18);

        public bool IsUsed => TextureId != 0 && TextureId != ulong.MaxValue;
    }
}
