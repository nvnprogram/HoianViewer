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
            Map = set.File.EmitterMap;
            if (section.Payload.Length != Map.PayloadSize)
                throw new InvalidOperationException(
                    $"EMTR payload is 0x{section.Payload.Length:X} bytes, v{Map.Version} is 0x{Map.PayloadSize:X}"
                );
            Set = set;
            Parent = parent;
            Section = section;
            Index = index;
            Children = section.Children.Select((x, i) => new Emitter(set, this, x, i)).ToList();
        }

        public Span<byte> Data => Section.Data;

        /// <summary>Where this file's version keeps the fields below.</summary>
        public EmitterMap Map { get; }

        public string Name
        {
            get => Section.GetString(EmitterLayout.NameOffset, EmitterLayout.NameLength);
            set => Section.SetString(EmitterLayout.NameOffset, EmitterLayout.NameLength, value);
        }

        public IEnumerable<Emitter> SelfAndDescendants =>
            new[] { this }.Concat(Children.SelectMany(x => x.SelfAndDescendants));

        /// <summary>The bytes the game uploads as sysEmitterStaticUniformBlock.</summary>
        public Span<byte> StaticBlock => Data[..Map.StaticBlockSize];

        public bool Visible
        {
            get => Section.GetU8(Map.Visible) != 0;
            set => Section.SetU8(Map.Visible, value ? (byte)1 : (byte)0);
        }

        public EmitterCalcType CalcType
        {
            get => (EmitterCalcType)Section.GetU8(Map.CalcType);
            set => Section.SetU8(Map.CalcType, (byte)value);
        }

        public byte FollowType => Section.GetU8(Map.FollowType);

        public uint DrawPath => Section.GetU32(Map.DrawPath);

        public int Life => Section.GetS32(Map.Life);

        public bool InfiniteLife => Section.GetU8(Map.InfiniteLife) != 0;

        public byte BillboardType => Section.GetU8(Map.BillboardType);

        public byte VolumeType => Section.GetU8(Map.VolumeType);

        public ulong ShapePrimitiveId => Section.GetU64(Map.ShapePrimitiveId);

        public ulong ParticlePrimitiveId => Section.GetU64(Map.ParticlePrimitiveId);

        public ulong ParticlePrimitiveExId => Section.GetU64(Map.ParticlePrimitiveExId);

        /// <summary>Program index into the general shader archive for the ordinary pass.</summary>
        public int ShaderIndex
        {
            get => Section.GetS32(Map.ShaderIndex);
            set => Section.SetS32(Map.ShaderIndex, value);
        }

        /// <summary>The same program with the converter option <see cref="DepthMode"/> names set
        /// (ZONLY_CONVERTER, LUMA_CONVERTER or NO_DEPTH_TEST_CONVERTER).</summary>
        public int DepthModeShaderIndex
        {
            get => Section.GetS32(Map.DepthModeShaderIndex);
            set => Section.SetS32(Map.DepthModeShaderIndex, value);
        }

        /// <summary>The same program with the converter option <see cref="PassInfo"/> names set,
        /// XLU_Z_PREPASS_CONVERTER in every stock file.</summary>
        public int PassShaderIndex
        {
            get => Section.GetS32(Map.PassShaderIndex);
            set => Section.SetS32(Map.PassShaderIndex, value);
        }

        /// <summary>Program index into the compute archive (GRSC) for a stream out emitter, -1
        /// for the others.</summary>
        public int ComputeShaderIndex
        {
            get => Section.GetS32(Map.ComputeShaderIndex);
            set => Section.SetS32(Map.ComputeShaderIndex, value);
        }

        /// <summary>ZONLY, LUMA or NO_DEPTH_TEST: names the option that tells the depth mode
        /// program from the base one.</summary>
        public string DepthMode => Section.GetString(Map.DepthMode, 16);

        /// <summary>XLU_Z_PREPASS in every stock file: names the option of the pass program.</summary>
        public string PassInfo => Section.GetString(Map.PassInfo, 48);

        /// <summary>The game callback set the emitter's particles go through, 0 for none.</summary>
        public int CustomAction => Section.GetS32(Map.CustomAction);

        public RenderState RenderState => new(Section, Map.RenderState);

        public TextureSampler GetSampler(int slot)
        {
            if ((uint)slot >= Map.SamplerCount)
                throw new ArgumentOutOfRangeException(nameof(slot));
            return new TextureSampler(
                Section,
                Map.Samplers + slot * Map.SamplerStride,
                Map.SamplerStride
            );
        }

        public IEnumerable<TextureSampler> Samplers =>
            Enumerable.Range(0, Map.SamplerCount).Select(GetSampler);

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

    /// <summary>ResEmitter +0xBD8 (v54 +0xE08), 16 bytes: blend, depth and cull state.</summary>
    public readonly struct RenderState
    {
        readonly VfxSection _section;
        readonly int _at;

        internal RenderState(VfxSection section, int at)
        {
            _section = section;
            _at = at;
        }

        public bool BlendEnable => _section.GetU8(_at) != 0;
        public bool DepthTest => _section.GetU8(_at + 1) != 0;
        public byte DepthFunc => _section.GetU8(_at + 2);
        public bool DepthWrite => _section.GetU8(_at + 3) != 0;
        public byte AlphaTest => _section.GetU8(_at + 4);
        public byte AlphaFunc => _section.GetU8(_at + 5);

        /// <summary>0 normal, 1 add, 2 sub, 3 screen, 4 multiply.</summary>
        public byte BlendType => _section.GetU8(_at + 6);

        /// <summary>0 both sides, 1 front (cull back), 2 back (cull front).</summary>
        public byte DisplaySide => _section.GetU8(_at + 7);

        public override string ToString() =>
            $"blend {(BlendEnable ? BlendType.ToString() : "off")} depth test {DepthTest} func {DepthFunc} write {DepthWrite} side {DisplaySide}";
    }

    /// <summary>
    /// One of the six sampler slots: 0x20 bytes at ResEmitter +0xD90 in v46, 0x18 bytes at
    /// +0x1038 in v54, which drops the two LOD floats and moves the flag bytes up.
    /// </summary>
    public readonly struct TextureSampler
    {
        readonly VfxSection _section;
        readonly int _at;
        readonly bool _short;

        internal TextureSampler(VfxSection section, int at, int stride)
        {
            _section = section;
            _at = at;
            _short = stride != EmitterLayout.SamplerStride;
        }

        int Flag(int v46) => _short ? v46 - 7 : v46;

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
        public float MaxLod => _short ? 16 : _section.GetF32(_at + 0x0C);
        public float LodBias => _short ? 0 : _section.GetF32(_at + 0x10);
        public byte MipLevelLimit => _section.GetU8(_at + Flag(0x14));
        public byte IsDensityFixedU => _section.GetU8(_at + Flag(0x15));
        public byte IsDensityFixedV => _section.GetU8(_at + Flag(0x16));
        public byte IsSquareRgb => _section.GetU8(_at + Flag(0x17));
        public byte IsOnAnotherBinary => _short ? (byte)0 : _section.GetU8(_at + 0x18);

        public bool IsUsed => TextureId != 0 && TextureId != ulong.MaxValue;
    }
}
