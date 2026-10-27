using System.Collections.Generic;
using System.Linq;

namespace EffectLibrary
{
    public enum FieldType
    {
        U8,
        U16,
        S32,
        U32,
        U64,
        F32,
        Chars,
        Bytes,
    }

    /// <summary>How far a mapped field is trusted.</summary>
    public enum Confidence
    {
        /// <summary>Checked against the game: its runtime, a capture, or every stock file.</summary>
        Verified,

        /// <summary>Consistent with the data but only named from the older library's layout.</summary>
        Likely,

        /// <summary>Position known, meaning a guess.</summary>
        Guess,
    }

    /// <summary>One field as one version lays it out.</summary>
    public readonly record struct EmitterField(
        string Name,
        int Offset,
        FieldType Type,
        int Count,
        Confidence Confidence
    )
    {
        public int Size =>
            Count
            * Type switch
            {
                FieldType.U8 or FieldType.Chars or FieldType.Bytes => 1,
                FieldType.U16 => 2,
                FieldType.U64 => 8,
                _ => 4,
            };
    }

    /// <summary>
    /// The ResEmitter (EMTR payload) field table: one row per field with its offset in v46
    /// (0xEF0 bytes) and in v54 (0x1168 bytes). The first <see cref="StaticBlockSize"/> bytes of
    /// v46 (0xCC0 of v54) are the shader's sysEmitterStaticUniformBlock; the rest is CPU side.
    /// The constants are the v46 offsets the v46 shaped readers use.
    /// </summary>
    public static class EmitterLayout
    {
        public const int PayloadSize = 0xEF0;
        public const int NameOffset = 0x10;
        public const int NameLength = 96;
        public const int StaticBlockSize = 0xA90;

        public const int KeyCounts = 0x080;
        public const int TexPatternAnims = 0x130;
        public const int TexPatternAnimStride = 0x90;
        public const int TexShiftAnims = 0x490;
        public const int TexShiftAnimStride = 0x50;
        public const int Color0Keys = 0x680;
        public const int Alpha0Keys = 0x700;
        public const int Color1Keys = 0x780;
        public const int Alpha1Keys = 0x800;
        public const int ScaleKeys = 0x8C0;
        public const int ParamKeys = 0x940;

        public const int CustomAction = 0xCF0;

        public const int SamplerStride = 0x20;
        public const int SamplerCount = 6;
        public const int TextureAnims = 0xE50;
        public const int TextureAnimStride = 0x10;

        /// <summary>A field and where each version keeps it, -1 where a version has none. A
        /// field whose shape changed between the versions is two rows of one name.</summary>
        public readonly record struct Row(
            string Name,
            int V46,
            int V54,
            FieldType Type,
            int Count,
            Confidence V46Confidence,
            Confidence V54Confidence
        );

        const Confidence V = Confidence.Verified;
        const Confidence L = Confidence.Likely;
        const Confidence G = Confidence.Guess;
        const FieldType U8 = FieldType.U8;
        const FieldType U16 = FieldType.U16;
        const FieldType S32 = FieldType.S32;
        const FieldType U32 = FieldType.U32;
        const FieldType U64 = FieldType.U64;
        const FieldType F32 = FieldType.F32;
        const FieldType Chars = FieldType.Chars;
        const FieldType Bytes = FieldType.Bytes;

        static Row B(
            string name,
            int v46,
            int v54,
            FieldType type,
            int count,
            Confidence c46,
            Confidence c54
        ) => new(name, v46, v54, type, count, c46, c54);

        static Row Only46(string name, int at, FieldType type, int count, Confidence c) =>
            new(name, at, -1, type, count, c, c);

        static Row Only54(string name, int at, FieldType type, int count, Confidence c) =>
            new(name, -1, at, type, count, c, c);

        /// <summary>Every mapped field. Gaps are unmapped bytes.</summary>
        public static readonly IReadOnlyList<Row> Rows = new[]
        {
            B("Flag", 0x000, 0x000, U32, 1, L, G),
            B("RandomSeed", 0x004, 0x004, U32, 1, L, G),
            B("Name", NameOffset, NameOffset, Chars, NameLength, V, L),
            //Static block, uploaded as sysEmitterStaticUniformBlock after StaticUniformBlock.Build.
            B("Static.ShaderFlags", 0x070, 0x070, U32, 4, V, G),
            B("Static.KeyCount.Color0", 0x080, 0x080, U32, 1, V, L),
            B("Static.KeyCount.Alpha0", 0x084, 0x084, U32, 1, V, L),
            B("Static.KeyCount.Color1", 0x088, 0x088, U32, 1, V, L),
            B("Static.KeyCount.Alpha1", 0x08C, 0x08C, U32, 1, V, L),
            B("Static.KeyCount.Scale", 0x090, 0x090, U32, 1, V, L),
            Only54("Static.KeyCount.Keys5", 0x094, U32, 1, G),
            B("Static.KeyCount.Param", 0x094, 0x098, U32, 1, V, V),
            Only54("Static.KeyCount.Keys7", 0x09C, U32, 3, V),
            Only54("Static.Unknown0A8", 0x0A8, U32, 2, G),
            B("Static.LoopFrames", 0x0A0, 0x0B0, F32, 5, V, V),
            B("Static.LoopRandom", 0x0B4, 0x0C4, F32, 5, V, V),
            Only54("Static.Unknown0D8", 0x0D8, U32, 2, G),
            Only54("Static.LoopFrames5", 0x0E0, F32, 4, V),
            Only54("Static.LoopRandom5", 0x0F0, F32, 4, V),
            B("Static.Gravity", 0x0D0, 0x100, F32, 4, L, L),
            B("Static.AirRegist", 0x0E0, 0x110, F32, 1, L, L),
            B("Static.Center", 0x0F0, 0x120, F32, 2, L, L),
            B("Static.Offset", 0x0F8, 0x128, F32, 1, L, L),
            B("Static.FluctuationAmplitude", 0x100, 0x130, F32, 2, L, L),
            B("Static.FluctuationCycle", 0x108, 0x138, F32, 2, L, L),
            B("Static.FluctuationPhaseRandom", 0x110, 0x140, F32, 2, L, L),
            B("Static.FluctuationPhaseInit", 0x118, 0x148, F32, 2, L, L),
            B("Static.ShaderCoefficient", 0x120, 0x150, F32, 2, L, L),
            B(
                "Static.TexPatternAnim",
                TexPatternAnims,
                0x160,
                Bytes,
                6 * TexPatternAnimStride,
                V,
                L
            ),
            B("Static.TexShiftAnim", TexShiftAnims, 0x4C0, Bytes, 6 * TexShiftAnimStride, V, L),
            B("Static.ColorScale", 0x670, 0x6A0, F32, 1, L, L),
            B("Static.Color0Keys", Color0Keys, 0x6B0, F32, 32, V, L),
            B("Static.Alpha0Keys", Alpha0Keys, 0x730, F32, 32, V, L),
            B("Static.Color1Keys", Color1Keys, 0x7B0, F32, 32, V, L),
            B("Static.Alpha1Keys", Alpha1Keys, 0x830, F32, 32, V, L),
            B("Static.SoftEdge", 0x880, 0x8B0, F32, 2, L, L),
            B("Static.FresnelAlpha", 0x888, 0x8B8, F32, 2, L, L),
            B("Static.NearDistAlpha", 0x890, 0x8C0, F32, 2, L, L),
            B("Static.FarDistAlpha", 0x898, 0x8C8, F32, 2, L, L),
            B("Static.Decal", 0x8A0, 0x8D0, F32, 2, L, L),
            B("Static.AlphaThreshold", 0x8A8, 0x8D8, F32, 1, L, L),
            B("Static.AddVelToScale", 0x8B0, 0x8E0, F32, 1, L, L),
            B("Static.SoftParticleDist", 0x8B4, 0x8E4, F32, 1, L, L),
            B("Static.SoftParticleVolume", 0x8B8, 0x8E8, F32, 1, L, L),
            B("Static.ScaleKeys", ScaleKeys, 0x8F0, F32, 32, V, L),
            Only54("Static.Keys5", 0x970, F32, 32, G),
            B("Static.ParamKeys", ParamKeys, 0x9F0, F32, 32, V, L),
            Only54("Static.Param1Keys", 0xA70, F32, 32, G),
            Only54("Static.Param2Keys", 0xAF0, F32, 32, G),
            Only54("Static.Param3Keys", 0xB70, F32, 32, G),
            B("Static.Unknown9C0", 0x9C0, 0xBF0, F32, 16, G, G),
            B("Static.Rotation", 0xA00, 0xC30, F32, 3, V, L),
            B("Static.RotationRandom", 0xA10, 0xC40, F32, 3, V, L),
            B("Static.RotationSpeed", 0xA20, 0xC50, F32, 3, V, L),
            B("Static.RotateRegist", 0xA2C, 0xC5C, F32, 1, L, L),
            B("Static.RotationSpeedRandom", 0xA30, 0xC60, F32, 3, V, L),
            B("Static.ScaleLimitDist", 0xA40, 0xC70, F32, 2, L, L),
            B("Static.RotateAxisParam0", 0xA50, 0xC80, F32, 3, V, L),
            B("Static.RotateAxisParam1", 0xA60, 0xC90, F32, 3, V, G),
            Only54("Static.RotateAxisParam2", 0xCA0, F32, 3, G),
            Only54("Static.RotateAxisParam3", 0xCB0, F32, 3, G),
            Only46("Static.UnknownA70", 0xA70, F32, 8, G),
            //Emitter info. The runtime reads these bytes directly.
            B("Info.Visible", 0xA90, 0xCC0, U8, 1, V, L),
            B("Info.StripeOrSortType", 0xA91, 0xCC1, U8, 1, G, G),
            B("Info.CalcType", 0xA92, 0xCC2, U8, 1, V, L),
            B("Info.FollowType", 0xA93, 0xCC3, U8, 1, V, L),
            B("Info.Flags", 0xA94, 0xCC4, U8, 12, G, G),
            B("Info.RandomSeed", 0xAA0, 0xCD0, U32, 1, L, L),
            B("Info.DrawPath", 0xAA4, 0xCD4, U32, 1, V, L),
            B("Info.AlphaFadeTime", 0xAA8, 0xCD8, S32, 1, L, L),
            B("Info.FadeInTime", 0xAAC, 0xCDC, S32, 1, L, L),
            B("Info.Translate", 0xAB0, 0xCE0, F32, 3, L, L),
            B("Info.TranslateRandom", 0xABC, 0xCEC, F32, 3, L, L),
            B("Info.Rotate", 0xAC8, 0xCF8, F32, 3, L, L),
            B("Info.RotateRandom", 0xAD4, 0xD04, F32, 3, L, L),
            B("Info.Scale", 0xAE0, 0xD10, F32, 3, L, L),
            B("Info.Color0", 0xAEC, 0xD1C, F32, 4, L, L),
            B("Info.Color1", 0xAFC, 0xD2C, F32, 4, L, L),
            B("Info.LodDistance", 0xB0C, 0xD3C, F32, 3, V, L),
            B("Info.FadeInScaleMin", 0xB18, 0xD48, F32, 1, V, L),
            B("Info.FadeOutScaleMin", 0xB1C, 0xD4C, F32, 1, V, L),
            B("Inherit.Flags", 0xB20, 0xD50, U8, 13, V, G),
            B("Inherit.VelocityRate", 0xB30, 0xD60, F32, 1, V, G),
            B("Inherit.ScaleRate", 0xB34, 0xD64, F32, 1, V, G),
            B("Emission.IsOneTime", 0xB38, 0xD68, U8, 1, L, L),
            B("Emission.IsWorldGravity", 0xB39, 0xD69, U8, 1, V, L),
            B("Emission.Flags", 0xB3A, 0xD6A, U8, 2, L, L),
            B("Emission.Start", 0xB3C, 0xD6C, U32, 1, L, L),
            B("Emission.Timing", 0xB40, 0xD70, U32, 1, L, L),
            B("Emission.Duration", 0xB44, 0xD74, U32, 1, L, L),
            B("Emission.Rate", 0xB48, 0xD78, F32, 1, L, L),
            B("Emission.RateRandom", 0xB4C, 0xD7C, F32, 1, L, L),
            B("Emission.Interval", 0xB50, 0xD80, S32, 1, L, L),
            B("Emission.IntervalRandom", 0xB54, 0xD84, F32, 1, L, L),
            B("Emission.PositionRandom", 0xB58, 0xD88, F32, 1, L, L),
            B("Emission.GravityScale", 0xB5C, 0xD8C, F32, 1, L, G),
            B("Emission.GravityAxis", 0xB60, 0xD90, F32, 3, L, G),
            B("Emission.Distance", 0xB6C, 0xD9C, F32, 4, L, L),
            B("Emission.DistanceMaxParticles", 0xB7C, 0xDAC, S32, 1, L, L),
            B("Shape.VolumeType", 0xB80, 0xDB0, U8, 1, L, L),
            B("Shape.Flags", 0xB81, 0xDB1, U8, 7, L, L),
            B("Shape.Sweep", 0xB88, 0xDB8, F32, 3, L, L),
            B("Shape.SurfacePosRandom", 0xB94, 0xDC4, F32, 1, L, L),
            B("Shape.CaliberRatio", 0xB98, 0xDC8, F32, 1, L, L),
            B("Shape.Line", 0xB9C, 0xDCC, F32, 2, L, L),
            B("Shape.Radius", 0xBA4, 0xDD4, F32, 3, L, L),
            B("Shape.FormScale", 0xBB0, 0xDE0, F32, 3, L, L),
            B("Shape.PrimEmitType", 0xBBC, 0xDEC, S32, 1, L, L),
            B("Shape.PrimitiveId", 0xBC0, 0xDF0, U64, 1, V, G),
            B("Shape.Divide", 0xBC8, 0xDF8, S32, 4, L, L),
            //Render state, 16 bytes (RenderState).
            B("Render.BlendEnable", 0xBD8, 0xE08, U8, 1, V, L),
            B("Render.DepthTest", 0xBD9, 0xE09, U8, 1, L, L),
            B("Render.DepthFunc", 0xBDA, 0xE0A, U8, 1, V, L),
            B("Render.DepthWrite", 0xBDB, 0xE0B, U8, 1, V, L),
            B("Render.AlphaTest", 0xBDC, 0xE0C, U8, 1, G, G),
            B("Render.AlphaFunc", 0xBDD, 0xE0D, U8, 1, G, G),
            B("Render.BlendType", 0xBDE, 0xE0E, U8, 1, L, L),
            B("Render.DisplaySide", 0xBDF, 0xE0F, U8, 1, V, L),
            B("Render.AlphaThreshold", 0xBE0, 0xE10, F32, 1, G, G),
            B("Particle.InfiniteLife", 0xBE8, 0xE18, U8, 1, L, L),
            B("Particle.IsTrimming", 0xBE9, 0xE19, U8, 1, L, L),
            B("Particle.BillboardType", 0xBEA, 0xE1A, U8, 1, L, L),
            B("Particle.RotType", 0xBEB, 0xE1B, U8, 1, L, L),
            B("Particle.OffsetType", 0xBEC, 0xE1C, U8, 1, V, L),
            B("Particle.RotateDirRandom", 0xBED, 0xE1D, U8, 3, V, L),
            B("Particle.IsRotate", 0xBF0, 0xE20, U8, 3, V, L),
            B("Particle.PrimitiveScaleType", 0xBF3, 0xE23, U8, 1, V, L),
            B("Particle.IsTextureCommonRandom", 0xBF4, 0xE24, U8, 1, V, L),
            B("Particle.Flags", 0xBF5, 0xE25, U8, 3, L, L),
            B("Particle.Life", 0xBF8, 0xE28, S32, 1, L, L),
            B("Particle.LifeRandom", 0xBFC, 0xE2C, S32, 1, L, L),
            B("Particle.MomentumRandom", 0xC00, 0xE30, F32, 1, L, L),
            B("Particle.PrimitiveVertexFlags", 0xC04, 0xE34, U32, 1, L, L),
            B("Particle.PrimitiveId", 0xC08, 0xE38, U64, 1, V, L),
            B("Particle.PrimitiveExId", 0xC10, 0xE40, U64, 1, L, G),
            //v54 has nine animation loop slots to v46's five, and the first four rates as u16.
            Only46("Particle.AnimLoop", 0xC18, U8, 5, V),
            Only54("Particle.AnimLoop", 0xE48, U8, 9, V),
            Only46("Particle.AnimLoopRandom", 0xC1D, U8, 5, V),
            Only54("Particle.AnimLoopRandom", 0xE51, U8, 9, V),
            B("Particle.PrimFlags", 0xC22, 0xE5A, U8, 2, L, G),
            Only46("Particle.AnimLoopRate", 0xC24, S32, 5, V),
            Only54("Particle.AnimLoopRate", 0xE5C, U16, 4, V),
            Only54("Particle.AnimLoopRate4", 0xE64, S32, 5, V),
            Only46("Combiner", 0xC38, U8, 16, L),
            Only54("Particle.AnimInterpolation", 0xE78, U8, 6, V),
            Only54("Particle.RotateWaveEnable", 0xE7E, U8, 3, V),
            Only54("Particle.RotateWaveType", 0xE81, U8, 3, V),
            Only54("Particle.UnknownE84", 0xE84, U8, 12, G),
            Only46("Shader.Type", 0xC48, U8, 1, G),
            Only54("Shader.Type", 0xE90, U8, 4, G),
            B("Shader.Index", 0xC4C, 0xE94, S32, 1, V, L),
            B("Shader.DepthModeIndex", 0xC50, 0xE98, S32, 1, V, L),
            B("Shader.PassIndex", 0xC54, 0xE9C, S32, 1, V, L),
            B("Shader.ComputeIndex", 0xC58, 0xEB4, S32, 1, V, L),
            Only46("Shader.ExtraIndex", 0xC5C, S32, 2, G),
            Only54("Shader.ExtraIndex", 0xEA0, S32, 5, G),
            Only54("Shader.ExtraIndex2", 0xEB8, S32, 2, G),
            B("Shader.Unknown", 0xC64, 0xEC0, S32, 1, G, L),
            B("Shader.CustomFlags", 0xC68, 0xEC8, U32, 2, V, L),
            Only46("Action", 0xC98, U32, 6, G),
            B("DepthMode", 0xCB0, 0xF08, Chars, 16, V, L),
            B("PassInfo", 0xCC0, 0xF18, Chars, 48, V, G),
            B("CustomAction", CustomAction, 0xF98, U32, 1, G, L),
            B("Velocity.AllDirection", 0xCF4, 0xF9C, F32, 1, L, L),
            B("Velocity.DirectionalScale", 0xCF8, 0xFA0, F32, 1, L, L),
            B("Velocity.Direction", 0xCFC, 0xFA4, F32, 3, L, L),
            B("Velocity.DiffusionAngle", 0xD08, 0xFB0, F32, 1, L, L),
            B("Velocity.XZDiffusion", 0xD0C, 0xFB4, F32, 1, L, L),
            B("Velocity.Diffusion", 0xD10, 0xFB8, F32, 3, L, L),
            B("Velocity.Random", 0xD1C, 0xFC4, F32, 1, L, L),
            B("Velocity.EmitterInherit", 0xD20, 0xFC8, F32, 1, L, L),
            B("UnknownD24", 0xD24, 0xFCC, F32, 4, G, G),
            B("Color.Flags", 0xD34, 0xFDC, U8, 8, L, L),
            B("Color.Types", 0xD3C, 0xFE4, U8, 4, V, L),
            B("Color.Color0", 0xD40, 0xFE8, F32, 3, V, L),
            B("Color.Alpha0", 0xD4C, 0xFF4, F32, 1, V, L),
            B("Color.Color1", 0xD50, 0xFF8, F32, 3, V, L),
            B("Color.Alpha1", 0xD5C, 0x1004, F32, 1, V, L),
            B("Scale.Base", 0xD60, 0x1008, F32, 3, L, L),
            B("Scale.Random", 0xD6C, 0x1014, F32, 3, L, L),
            B("Scale.Flags", 0xD78, 0x1020, U8, 4, L, L),
            B("Scale.MinMax", 0xD7C, 0x1024, F32, 2, L, G),
            B("Fluctuation.Flags", 0xD84, 0x102C, U8, 3, L, L),
            B("Fluctuation.WaveType", 0xD87, 0x102F, U8, 1, V, L),
            B("Fluctuation.PhaseRandom", 0xD88, 0x1030, U8, 2, L, G),
            //v54 drops the two LOD floats from each sampler (TextureSampler).
            Only46("Samplers", 0xD90, Bytes, SamplerCount * SamplerStride, V),
            Only54("Samplers", 0x1038, Bytes, SamplerCount * 0x18, L),
            B("TextureAnims", TextureAnims, 0x10C8, Bytes, 6 * TextureAnimStride, V, L),
            B("Reserved", 0xEB0, 0x1128, Bytes, 0x40, G, G),
        };

        /// <summary>One version's fields, in offset order.</summary>
        public static IReadOnlyList<EmitterField> FieldsOf(ushort version)
        {
            bool v46 = version == VfxFile.SwitchV46;
            return Rows.Where(r => (v46 ? r.V46 : r.V54) >= 0)
                .Select(r =>
                    v46
                        ? new EmitterField(r.Name, r.V46, r.Type, r.Count, r.V46Confidence)
                        : new EmitterField(r.Name, r.V54, r.Type, r.Count, r.V54Confidence)
                )
                .OrderBy(f => f.Offset)
                .ToArray();
        }
    }
}
