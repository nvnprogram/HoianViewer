using System.Collections.Generic;

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
    /// The v46 ResEmitter (EMTR payload, 0xEF0 bytes). Its first 0xA90 bytes are uploaded
    /// verbatim as the shader's sysEmitterStaticUniformBlock; everything past that is CPU side.
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

        public const int Visible = 0xA90;
        public const int CalcType = 0xA92;
        public const int FollowType = 0xA93;
        public const int DrawPath = 0xAA4;

        public const int Emission = 0xB38;
        public const int VolumeType = 0xB80;
        public const int ShapePrimitiveId = 0xBC0;

        public const int RenderState = 0xBD8;
        public const int BlendEnable = 0xBD8;
        public const int DepthTest = 0xBD9;
        public const int DepthFunc = 0xBDA;
        public const int DepthWrite = 0xBDB;
        public const int AlphaTest = 0xBDC;
        public const int AlphaFunc = 0xBDD;
        public const int BlendType = 0xBDE;
        public const int DisplaySide = 0xBDF;

        public const int BillboardType = 0xBEA;
        public const int Life = 0xBF8;
        public const int InfiniteLife = 0xBE8;
        public const int ParticlePrimitiveId = 0xC08;
        public const int ParticlePrimitiveExId = 0xC10;

        public const int ShaderType = 0xC48;
        public const int ShaderIndex = 0xC4C;
        public const int DepthModeShaderIndex = 0xC50;
        public const int PassShaderIndex = 0xC54;
        public const int ComputeShaderIndex = 0xC58;
        public const int CustomShaderFlags = 0xC68;

        public const int DepthMode = 0xCB0;
        public const int PassInfo = 0xCC0;
        public const int CustomAction = 0xCF0;

        public const int Samplers = 0xD90;
        public const int SamplerStride = 0x20;
        public const int SamplerCount = 6;
        public const int TextureAnims = 0xE50;
        public const int TextureAnimStride = 0x10;

        static EmitterField F(string name, int offset, FieldType type, int count, Confidence c) =>
            new(name, offset, type, count, c);

        const Confidence V = Confidence.Verified;
        const Confidence L = Confidence.Likely;
        const Confidence G = Confidence.Guess;

        /// <summary>Every mapped field, in offset order. Gaps are unmapped bytes.</summary>
        public static readonly IReadOnlyList<EmitterField> Fields = new[]
        {
            F("Flag", 0x000, FieldType.U32, 1, L),
            F("RandomSeed", 0x004, FieldType.U32, 1, L),
            F("Name", NameOffset, FieldType.Chars, NameLength, V),
            //Static block, uploaded as sysEmitterStaticUniformBlock after StaticUniformBlock.Build.
            F("Static.ShaderFlags", 0x070, FieldType.U32, 4, V),
            F("Static.KeyCount.Color0", 0x080, FieldType.U32, 1, V),
            F("Static.KeyCount.Alpha0", 0x084, FieldType.U32, 1, V),
            F("Static.KeyCount.Color1", 0x088, FieldType.U32, 1, V),
            F("Static.KeyCount.Alpha1", 0x08C, FieldType.U32, 1, V),
            F("Static.KeyCount.Scale", 0x090, FieldType.U32, 1, V),
            F("Static.KeyCount.Param", 0x094, FieldType.U32, 1, V),
            F("Static.LoopFrames", 0x0A0, FieldType.F32, 5, V),
            F("Static.LoopRandom", 0x0B4, FieldType.F32, 5, V),
            F("Static.Gravity", 0x0D0, FieldType.F32, 4, L),
            F("Static.AirRegist", 0x0E0, FieldType.F32, 1, L),
            F("Static.Center", 0x0F0, FieldType.F32, 2, L),
            F("Static.Offset", 0x0F8, FieldType.F32, 1, L),
            F("Static.FluctuationAmplitude", 0x100, FieldType.F32, 2, L),
            F("Static.FluctuationCycle", 0x108, FieldType.F32, 2, L),
            F("Static.FluctuationPhaseRandom", 0x110, FieldType.F32, 2, L),
            F("Static.FluctuationPhaseInit", 0x118, FieldType.F32, 2, L),
            F("Static.ShaderCoefficient", 0x120, FieldType.F32, 2, L),
            F(
                "Static.TexPatternAnim",
                TexPatternAnims,
                FieldType.Bytes,
                6 * TexPatternAnimStride,
                V
            ),
            F("Static.TexShiftAnim", TexShiftAnims, FieldType.Bytes, 6 * TexShiftAnimStride, V),
            F("Static.ColorScale", 0x670, FieldType.F32, 1, L),
            F("Static.Color0Keys", Color0Keys, FieldType.F32, 32, V),
            F("Static.Alpha0Keys", Alpha0Keys, FieldType.F32, 32, V),
            F("Static.Color1Keys", Color1Keys, FieldType.F32, 32, V),
            F("Static.Alpha1Keys", Alpha1Keys, FieldType.F32, 32, V),
            F("Static.SoftEdge", 0x880, FieldType.F32, 2, L),
            F("Static.FresnelAlpha", 0x888, FieldType.F32, 2, L),
            F("Static.NearDistAlpha", 0x890, FieldType.F32, 2, L),
            F("Static.FarDistAlpha", 0x898, FieldType.F32, 2, L),
            F("Static.Decal", 0x8A0, FieldType.F32, 2, L),
            F("Static.AlphaThreshold", 0x8A8, FieldType.F32, 1, L),
            F("Static.AddVelToScale", 0x8B0, FieldType.F32, 1, L),
            F("Static.SoftParticleDist", 0x8B4, FieldType.F32, 1, L),
            F("Static.SoftParticleVolume", 0x8B8, FieldType.F32, 1, L),
            F("Static.ScaleKeys", ScaleKeys, FieldType.F32, 32, V),
            F("Static.ParamKeys", ParamKeys, FieldType.F32, 32, V),
            F("Static.Unknown9C0", 0x9C0, FieldType.F32, 16, G),
            F("Static.Rotation", 0xA00, FieldType.F32, 3, V),
            F("Static.RotationRandom", 0xA10, FieldType.F32, 3, V),
            F("Static.RotationSpeed", 0xA20, FieldType.F32, 3, V),
            F("Static.RotateRegist", 0xA2C, FieldType.F32, 1, L),
            F("Static.RotationSpeedRandom", 0xA30, FieldType.F32, 3, V),
            F("Static.ScaleLimitDist", 0xA40, FieldType.F32, 2, L),
            F("Static.RotateAxisParam0", 0xA50, FieldType.F32, 3, V),
            F("Static.RotateAxisParam1", 0xA60, FieldType.F32, 3, V),
            F("Static.UnknownA70", 0xA70, FieldType.F32, 8, G),
            //Emitter info. The runtime reads these bytes directly.
            F("Info.Visible", Visible, FieldType.U8, 1, V),
            F("Info.StripeOrSortType", 0xA91, FieldType.U8, 1, G),
            F("Info.CalcType", CalcType, FieldType.U8, 1, V),
            F("Info.FollowType", FollowType, FieldType.U8, 1, V),
            F("Info.Flags", 0xA94, FieldType.U8, 12, G),
            F("Info.RandomSeed", 0xAA0, FieldType.U32, 1, L),
            F("Info.DrawPath", DrawPath, FieldType.U32, 1, V),
            F("Info.AlphaFadeTime", 0xAA8, FieldType.S32, 1, L),
            F("Info.FadeInTime", 0xAAC, FieldType.S32, 1, L),
            F("Info.Translate", 0xAB0, FieldType.F32, 3, L),
            F("Info.TranslateRandom", 0xABC, FieldType.F32, 3, L),
            F("Info.Rotate", 0xAC8, FieldType.F32, 3, L),
            F("Info.RotateRandom", 0xAD4, FieldType.F32, 3, L),
            F("Info.Scale", 0xAE0, FieldType.F32, 3, L),
            F("Info.Color0", 0xAEC, FieldType.F32, 4, L),
            F("Info.Color1", 0xAFC, FieldType.F32, 4, L),
            F("Info.LodDistance", 0xB0C, FieldType.F32, 3, V),
            F("Info.FadeInScaleMin", 0xB18, FieldType.F32, 1, V),
            F("Info.FadeOutScaleMin", 0xB1C, FieldType.F32, 1, V),
            F("Inherit.Flags", 0xB20, FieldType.U8, 13, V),
            F("Inherit.VelocityRate", 0xB30, FieldType.F32, 1, V),
            F("Inherit.ScaleRate", 0xB34, FieldType.F32, 1, V),
            F("Emission.IsOneTime", Emission, FieldType.U8, 1, L),
            F("Emission.IsWorldGravity", 0xB39, FieldType.U8, 1, V),
            F("Emission.Flags", 0xB3A, FieldType.U8, 2, L),
            F("Emission.Start", 0xB3C, FieldType.U32, 1, L),
            F("Emission.Timing", 0xB40, FieldType.U32, 1, L),
            F("Emission.Duration", 0xB44, FieldType.U32, 1, L),
            F("Emission.Rate", 0xB48, FieldType.F32, 1, L),
            F("Emission.RateRandom", 0xB4C, FieldType.F32, 1, L),
            F("Emission.Interval", 0xB50, FieldType.S32, 1, L),
            F("Emission.IntervalRandom", 0xB54, FieldType.F32, 1, L),
            F("Emission.PositionRandom", 0xB58, FieldType.F32, 1, L),
            F("Emission.GravityScale", 0xB5C, FieldType.F32, 1, L),
            F("Emission.GravityAxis", 0xB60, FieldType.F32, 3, L),
            F("Emission.Distance", 0xB6C, FieldType.F32, 4, L),
            F("Emission.DistanceMaxParticles", 0xB7C, FieldType.S32, 1, L),
            F("Shape.VolumeType", VolumeType, FieldType.U8, 1, L),
            F("Shape.Flags", 0xB81, FieldType.U8, 7, L),
            F("Shape.Sweep", 0xB88, FieldType.F32, 3, L),
            F("Shape.SurfacePosRandom", 0xB94, FieldType.F32, 1, L),
            F("Shape.CaliberRatio", 0xB98, FieldType.F32, 1, L),
            F("Shape.Line", 0xB9C, FieldType.F32, 2, L),
            F("Shape.Radius", 0xBA4, FieldType.F32, 3, L),
            F("Shape.FormScale", 0xBB0, FieldType.F32, 3, L),
            F("Shape.PrimEmitType", 0xBBC, FieldType.S32, 1, L),
            F("Shape.PrimitiveId", ShapePrimitiveId, FieldType.U64, 1, V),
            F("Shape.Divide", 0xBC8, FieldType.S32, 4, L),
            F("Render.BlendEnable", BlendEnable, FieldType.U8, 1, V),
            F("Render.DepthTest", DepthTest, FieldType.U8, 1, L),
            F("Render.DepthFunc", DepthFunc, FieldType.U8, 1, V),
            F("Render.DepthWrite", DepthWrite, FieldType.U8, 1, V),
            F("Render.AlphaTest", AlphaTest, FieldType.U8, 1, G),
            F("Render.AlphaFunc", AlphaFunc, FieldType.U8, 1, G),
            F("Render.BlendType", BlendType, FieldType.U8, 1, L),
            F("Render.DisplaySide", DisplaySide, FieldType.U8, 1, V),
            F("Render.AlphaThreshold", 0xBE0, FieldType.F32, 1, G),
            F("Particle.InfiniteLife", InfiniteLife, FieldType.U8, 1, L),
            F("Particle.IsTrimming", 0xBE9, FieldType.U8, 1, L),
            F("Particle.BillboardType", BillboardType, FieldType.U8, 1, L),
            F("Particle.RotType", 0xBEB, FieldType.U8, 1, L),
            F("Particle.OffsetType", 0xBEC, FieldType.U8, 1, V),
            F("Particle.RotateDirRandom", 0xBED, FieldType.U8, 3, V),
            F("Particle.IsRotate", 0xBF0, FieldType.U8, 3, V),
            F("Particle.PrimitiveScaleType", 0xBF3, FieldType.U8, 1, V),
            F("Particle.IsTextureCommonRandom", 0xBF4, FieldType.U8, 1, V),
            F("Particle.Flags", 0xBF5, FieldType.U8, 3, L),
            F("Particle.Life", Life, FieldType.S32, 1, L),
            F("Particle.LifeRandom", 0xBFC, FieldType.S32, 1, L),
            F("Particle.MomentumRandom", 0xC00, FieldType.F32, 1, L),
            F("Particle.PrimitiveVertexFlags", 0xC04, FieldType.U32, 1, L),
            F("Particle.PrimitiveId", ParticlePrimitiveId, FieldType.U64, 1, V),
            F("Particle.PrimitiveExId", ParticlePrimitiveExId, FieldType.U64, 1, L),
            F("Particle.AnimLoop", 0xC18, FieldType.U8, 5, V),
            F("Particle.AnimLoopRandom", 0xC1D, FieldType.U8, 5, V),
            F("Particle.PrimFlags", 0xC22, FieldType.U8, 2, L),
            F("Particle.AnimLoopRate", 0xC24, FieldType.S32, 5, V),
            F("Combiner", 0xC38, FieldType.U8, 16, L),
            F("Shader.Type", ShaderType, FieldType.U8, 1, G),
            F("Shader.Index", ShaderIndex, FieldType.S32, 1, V),
            F("Shader.DepthModeIndex", DepthModeShaderIndex, FieldType.S32, 1, V),
            F("Shader.PassIndex", PassShaderIndex, FieldType.S32, 1, V),
            F("Shader.ComputeIndex", ComputeShaderIndex, FieldType.S32, 1, V),
            F("Shader.ExtraIndex", 0xC5C, FieldType.S32, 2, G),
            F("Shader.Unknown", 0xC64, FieldType.S32, 1, G),
            F("Shader.CustomFlags", CustomShaderFlags, FieldType.U32, 2, V),
            F("Action", 0xC98, FieldType.U32, 6, G),
            F("DepthMode", DepthMode, FieldType.Chars, 16, V),
            F("PassInfo", PassInfo, FieldType.Chars, 48, V),
            F("CustomAction", CustomAction, FieldType.U32, 1, G),
            F("Velocity.AllDirection", 0xCF4, FieldType.F32, 1, L),
            F("Velocity.DirectionalScale", 0xCF8, FieldType.F32, 1, L),
            F("Velocity.Direction", 0xCFC, FieldType.F32, 3, L),
            F("Velocity.DiffusionAngle", 0xD08, FieldType.F32, 1, L),
            F("Velocity.XZDiffusion", 0xD0C, FieldType.F32, 1, L),
            F("Velocity.Diffusion", 0xD10, FieldType.F32, 3, L),
            F("Velocity.Random", 0xD1C, FieldType.F32, 1, L),
            F("Velocity.EmitterInherit", 0xD20, FieldType.F32, 1, L),
            F("UnknownD24", 0xD24, FieldType.F32, 4, G),
            F("Color.Flags", 0xD34, FieldType.U8, 8, L),
            F("Color.Types", 0xD3C, FieldType.U8, 4, V),
            F("Color.Color0", 0xD40, FieldType.F32, 3, V),
            F("Color.Alpha0", 0xD4C, FieldType.F32, 1, V),
            F("Color.Color1", 0xD50, FieldType.F32, 3, V),
            F("Color.Alpha1", 0xD5C, FieldType.F32, 1, V),
            F("Scale.Base", 0xD60, FieldType.F32, 3, L),
            F("Scale.Random", 0xD6C, FieldType.F32, 3, L),
            F("Scale.Flags", 0xD78, FieldType.U8, 4, L),
            F("Scale.MinMax", 0xD7C, FieldType.F32, 2, L),
            F("Fluctuation.Flags", 0xD84, FieldType.U8, 3, L),
            F("Fluctuation.WaveType", 0xD87, FieldType.U8, 1, V),
            F("Fluctuation.PhaseRandom", 0xD88, FieldType.U8, 2, L),
            F("Samplers", Samplers, FieldType.Bytes, SamplerCount * SamplerStride, V),
            F("TextureAnims", TextureAnims, FieldType.Bytes, 6 * TextureAnimStride, V),
            F("Reserved", 0xEB0, FieldType.Bytes, 0x40, G),
        };
    }
}
